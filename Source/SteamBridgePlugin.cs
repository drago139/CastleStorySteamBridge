using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using Brix.Game.Network;
using Brix.Network;
using Brix.UI.Builder.Menu;
using Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    [BepInPlugin("local.castlestory.steambridge", "Castle Story Steam Bridge (experimental)", BridgeVersion.Text)]
    public sealed partial class SteamBridgePlugin : BaseUnityPlugin
    {
        const int Channel = 77;
        BridgeCore bridge;
        CSteamID peer;
        Callback<P2PSessionRequest_t> request;
        Callback<P2PSessionConnectFail_t> failure;
        bool visible = false, connecting, host;   // 0.3.45: the F8 window allocates garbage on every OnGUI pass, so it starts hidden
        ConfigEntry<bool> traceOverlayInput;
        string lastOverlayFocus = "";
        int traceInputUntil;
        bool overlayF8Held;
        int overlayF8Frame = -1;
        bool TraceOverlayInput { get { return traceOverlayInput != null && traceOverlayInput.Value; } }
        void TraceOverlay(string where)
        {
            if (TraceOverlayInput) Logger.LogInfo("F8TRACE frame=" + Time.frameCount + " " + where
                + " keyboard=" + GUIUtility.keyboardControl + " focus=" + lastOverlayFocus + " visible=" + visible);
        }
        float nextHello, started, lastData;
        CSteamID closingPeer;
        float closeAt;
        string status = "Steam Bridge " + BridgeVersion.Text + ". Лобі друзів доступні у публічному списку серверів.";
        Rect box = new Rect(40, 60, 530, 510);
        Vector2 scroll;
        readonly List<CSteamID> friends = new List<CSteamID>();
        readonly Dictionary<CSteamID, BridgeCore> guests = new Dictionary<CSteamID, BridgeCore>();
        readonly Dictionary<CSteamID, float> closeGuests = new Dictionary<CSteamID, float>();
        readonly Dictionary<CSteamID, float> guestSince = new Dictionary<CSteamID, float>();
        readonly byte[] steamReceiveBuffer = new byte[65512];
        readonly List<CSteamID> closingSnapshot = new List<CSteamID>(3);
        readonly List<CSteamID> guestSnapshot = new List<CSteamID>(3);
        float lastUpdateAt, nextPauseLog, nextGapLog;
        bool hosting;
        int hostingPort;

        // 0.3.8: a client bridge follows the game's own session. When the game's client goes away after it
        // was connected (host closed the lobby, the player was kicked or left, the connection was lost), the
        // bridge stops by itself; otherwise "Міст уже працює" blocks joining again until F8 → Stop.
        // Debounced (seconds and frames) so a single hitch or scene change never ends a live session.
        bool sessionSeen;
        float sessionGoneSince, connectingSince;
        int sessionGoneFrames;
        const float SessionGoneSeconds = 6f, GameNeverStartedSeconds = 25f;
        const int SessionGoneFrames = 60;

        // Send mode (BepInEx config, F8 button). Reliable is the 0.3.3 behaviour.
        const string SendReliable = "Reliable", SendMixed = "Mixed";
        ConfigEntry<string> sendMode;
        bool mixedSend;

        // Diagnostics: how long the bridge's own work takes, to tell a game stall from a bridge stall.
        static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double frameMs, frameGuests, frameDiscovery, frameRead, framePump;
        double statMaxMs, statTotalMs, statMaxGuests, statMaxDiscovery, statMaxRead, statMaxPump;
        int statFrames, statQueueMax, statSteamOut, statSteamIn, statUnreliable;
        float nextStatLog, nextSlowLog, statWindowStart, nextDiagErrorLog;

        void BindConfig()
        {
            try
            {
                sendMode = Config.Bind("Network", "SendMode", SendReliable, new ConfigDescription(
                    "Reliable: all game packets go through Steam reliably (0.3.3 behaviour). "
                    + "Mixed: game packets up to 1200 bytes go unreliable, larger and control packets stay reliable. "
                    + "Each PC chooses its own mode; peers stay compatible.",
                    new AcceptableValueList<string>(SendReliable, SendMixed)));
            }
            catch (Exception e) { Logger.LogWarning("Send mode config unavailable, using Reliable: " + e.Message); }
            try { allowStrangers = Config.Bind("Network", "AllowStrangers", true, "When you host, players who are not your Steam friends can join if they know your host code (shown in F8). Max 3 guests. Logs and diagnostics are only ever shared with friends."); }
            catch (Exception e) { Logger.LogWarning("AllowStrangers config unavailable, using true: " + e.Message); }
            try { allowMixed = Config.Bind("Network", "AllowMixedSendMode", false, "Allow the Mixed send mode. Unsafe: an outage of about 5 seconds then breaks the UNet connection (NetworkError.BadMessage) and drops the player. Keep false."); }
            catch (Exception e) { Logger.LogWarning("AllowMixedSendMode config unavailable: " + e.Message); }
            // 0.4.5: experiment switch, default true = unchanged behaviour. Steam relays a P2P session through its servers only when a direct connection cannot be made.
            try { allowRelay = Config.Bind("Network", "AllowRelay", true, "Steam may relay P2P traffic through its servers when a direct connection cannot be made (true = as before). false = direct connections only: a peer behind a strict NAT then cannot connect at all. Needs a game restart; it only applies to sessions created afterwards (the legacy Steam API cannot FORCE the relay)."); }
            catch (Exception e) { Logger.LogWarning("AllowRelay config unavailable, using true: " + e.Message); }
            if (sendMode != null && sendMode.Value == SendMixed && !AllowMixed) Logger.LogWarning("SendMode = Mixed is ignored (unsafe); using Reliable. Set [Network] AllowMixedSendMode = true to override.");
            Logger.LogInfo("Send mode: " + SendModeName() + "; strangers may join by host code: " + AllowStrangersOn + "; Steam relay allowed: " + AllowRelayOn);
            BindDiagnosticsConfig();
            traceOverlayInput = Config.Bind("Diagnostics", "TraceOverlayInput", false, "Trace F8 and overlay focus events for local diagnosis. Keep false normally.");
            BindTestConfig();
            try { simpleF8 = Config.Bind("Interface", "SimpleF8", true, "F8 window: simple layout with tabs (Гра / Налаштування / Діагностика) and plain-language hints. false = the old single long list. Takes effect at once, no restart."); }
            catch (Exception e) { Logger.LogWarning("SimpleF8 config unavailable, using the simple layout: " + e.Message); }
            try { f8HintLeft = Config.Bind("Interface", "F8HintSessions", 3, "How many game starts show the one-line hint \"press F8\" at the bottom of the screen (it ends for good once F8 was opened). 0 = never."); }
            catch (Exception e) { Logger.LogWarning("F8HintSessions config unavailable: " + e.Message); }
        }
        // 0.3.50: Mixed (small packets without Steam retransmission) is off unless [Network] AllowMixedSendMode = true. Soak tests showed a
        // network outage of about 5 s drops those packets for good and UNet then fails with NetworkError.BadMessage and removes the player,
        // while Reliable survived outages of 35 s. Old configs that still say Mixed are therefore ignored.
        ConfigEntry<bool> allowMixed, allowRelay;
        bool AllowMixed { get { return allowMixed != null && allowMixed.Value; } }
        bool AllowRelayOn { get { return allowRelay == null || allowRelay.Value; } }
        string SendModeName() { return sendMode != null && sendMode.Value == SendMixed && AllowMixed ? SendMixed : SendReliable; }
        static double Lap(ref long mark)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = (now - mark) * TickMs;
            mark = now;
            return ms;
        }
        // active: a bridge session was running when this frame started (it may have ended during it).
        void RecordFrame(long frameStart, bool active)
        {
            if (!DiagnosticsEnabled) return;
            frameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - frameStart) * TickMs;
            // Idle: the next bridge session starts a fresh 15 s window.
            if (!active) { nextStatLog = 0; return; }
            statFrames++; statTotalMs += frameMs;
            if (frameMs > statMaxMs) statMaxMs = frameMs;
            if (frameGuests > statMaxGuests) statMaxGuests = frameGuests;
            if (frameDiscovery > statMaxDiscovery) statMaxDiscovery = frameDiscovery;
            if (frameRead > statMaxRead) statMaxRead = frameRead;
            if (framePump > statMaxPump) statMaxPump = framePump;
            float now = Time.realtimeSinceStartup;
            if (frameMs >= 50 && now >= nextSlowLog)
            {
                nextSlowLog = now + 5;
                DiagWarn("Slow bridge work: " + frameMs.ToString("F1") + " ms (guests " + frameGuests.ToString("F1")
                    + ", discovery " + frameDiscovery.ToString("F1") + ", read " + frameRead.ToString("F1") + ", pump " + framePump.ToString("F1") + ")");
            }
            // The session ended during this frame; StopBridge has already flushed its stats.
            if (bridge == null && !hosting) { nextStatLog = 0; return; }
            if (now < nextStatLog) return;
            if (nextStatLog > 0) LogStats("");
            nextStatLog = now + 15;
            ResetStats();
        }
        // Logs the partial window before a session ends (the most useful one before a disconnect).
        // Never lets diagnostics break teardown (e.g. at quit, after Steam has shut down).
        void FlushStats()
        {
            try { if (DiagnosticsEnabled && nextStatLog > 0 && statFrames > 0) LogStats(" (session end)"); }
            catch (Exception e) { Logger.LogWarning("Final bridge stats unavailable: " + e.Message); }
            nextStatLog = 0;
            ResetStats();
        }
        void LogStats(string note)
        {
            string link = "";
            P2PSessionState_t s;
            bool steam = SteamManager.Initialized;
            if (steam && bridge != null && SteamNetworking.GetP2PSessionState(peer, out s))
            {
                if (s.m_nBytesQueuedForSend > statQueueMax) statQueueMax = s.m_nBytesQueuedForSend;
                link = "; link=" + (s.m_bUsingRelay != 0 ? "relay" : "P2P");
            }
            if (steam) foreach (CSteamID id in guests.Keys)
                if (SteamNetworking.GetP2PSessionState(id, out s))
                {
                    if (s.m_nBytesQueuedForSend > statQueueMax) statQueueMax = s.m_nBytesQueuedForSend;
                    link += "; " + id + "=" + (s.m_bUsingRelay != 0 ? "relay" : "P2P") + " q" + s.m_nBytesQueuedForSend;
                }
            diagLink = link;
            DiagWindow("Bridge stats " + (Time.realtimeSinceStartup - statWindowStart).ToString("F0") + " s" + note + ": mode=" + SendModeName() + (hosting ? "; host guests=" + guests.Count : "; client") + link
                + "; frames=" + statFrames + "; work avg " + (statFrames == 0 ? 0 : statTotalMs / statFrames).ToString("F2") + " ms, max " + statMaxMs.ToString("F1")
                + " ms (guests " + statMaxGuests.ToString("F1") + ", discovery " + statMaxDiscovery.ToString("F1") + ", read " + statMaxRead.ToString("F1") + ", pump " + statMaxPump.ToString("F1")
                + "); steam queue max " + statQueueMax + " B; steam packets out " + statSteamOut + " (unreliable " + statUnreliable + "), in " + statSteamIn);
            CheckStarvation(statSteamOut, statSteamIn);
            if (note.Length == 0) BlackBoxWindow(statSteamOut);      // regular 15 s windows only, not the partial one at session end
        }
        // 0.3.8: the host's game can stop broadcasting the world (pause, loading, hang) while only UNet heartbeats keep
        // arriving, so "no data for 3 s" never fires. Compare what we get with what we send instead.
        int starvedWindows;
        long clientLastSent, clientLastReceived;
        string starvationNote, starvationBanner;
        void CheckStarvation(int outPk, int inPk)
        {
            if (hosting || bridge == null || !bridge.Ready)
            { starvedWindows = 0; starvationNote = starvationBanner = null; clientLastSent = clientLastReceived = 0; return; }
            // Count only game datagrams, excluding ping, updates and diagnostics.
            long sent = bridge.Sent - clientLastSent, received = bridge.Received - clientLastReceived;
            clientLastSent = bridge.Sent; clientLastReceived = bridge.Received;
            bool inWorld = !loadActive && GameClientConnected()
                && NetworkClient.allClients != null && NetworkClient.allClients.Count > 0
                && NetworkClient.allClients[0].connection != null && NetworkClient.allClients[0].connection.isReady;
            bool starved = inWorld && sent >= 60 && received <= 40 && received * 8 < sent;
            if (starved)
            {
                starvedWindows++;
                if (starvedWindows >= 2)
                {
                    bool alive = PingIsFresh(peer);
                    starvationBanner = alive ? "Відповіді на пінг є, але ігрових даних від хоста мало" : "Ігрових даних від хоста мало; відповідей на пінг немає";
                    starvationNote = starvationBanner + " (" + received + " отримано, " + sent + " відправлено за вікно). Причина ще не визначена.";
                    if (starvedWindows == 2) DiagWarn(starvationNote + SteamLinkNote(peer));
                }
            }
            else if (!inWorld || received >= 100 || received * 3 >= sent)
            {
                if (starvationNote != null) DiagWarn("Game traffic recovered");
                starvedWindows = 0; starvationNote = starvationBanner = null;
            }
        }
        void ResetStats()
        {
            DiagAccumulate();
            statFrames = 0; statTotalMs = statMaxMs = statMaxGuests = statMaxDiscovery = statMaxRead = statMaxPump = 0;
            statQueueMax = statSteamOut = statSteamIn = statUnreliable = 0;
            statWindowStart = Time.realtimeSinceStartup;
        }

        bool IsFriend(CSteamID id) { return SteamFriends.GetFriendRelationship(id) == EFriendRelationship.k_EFriendRelationshipFriend; }
        // 0.3.40: anyone who knows the host code may join a hosted lobby (AllowStrangers). Logs and diagnostics still go to friends only.
        ConfigEntry<bool> allowStrangers;
        bool peerByCode;
        string joinCode = "";
        bool AllowStrangersOn { get { return allowStrangers == null || allowStrangers.Value; } }
        bool IsTrusted(CSteamID id) { return IsFriend(id) || (hosting && AllowStrangersOn) || (!hosting && bridge != null && id == peer); }
        static bool LooksLikeSteamId(ulong id) { return id > 76561190000000000UL && id < 76561300000000000UL; }
        BridgeCore Guest(CSteamID id)
        {
            if (!hosting || !IsTrusted(id) || IsBanned(id)) return null;
            BridgeCore b;
            if (guests.TryGetValue(id, out b)) return b;
            if (guests.Count >= 3 || closeGuests.ContainsKey(id)) return null;
            b = new BridgeCore(true, hostingPort, delegate(byte[] p) { return SendTo(id, p); }, delegate(byte[] buf, int n) { return SendRangeTo(id, buf, n); });
            b.ExtraHandler = delegate(byte kind, byte[] payload) { OnExtraPacket(id, kind, payload); };
            guests.Add(id, b); guestSince[id] = Time.realtimeSinceStartup;
            Logger.LogInfo("Guest bridge created: " + id + "; loopback=" + b.Port);
            return b;
        }
        // Mixed mode: the host's game may reject a join once (full lobby, match in progress) with a
        // single non-retransmitted UNet datagram, so each new guest's first seconds stay reliable.
        const float ReliableJoinSeconds = 30;
        bool JoinSettled(CSteamID id)
        {
            float since;
            return guestSince.TryGetValue(id, out since) && Time.realtimeSinceStartup - since >= ReliableJoinSeconds;
        }
        void RemoveGuest(CSteamID id, bool notify)
        {
            BridgeCore b;
            if (!guests.TryGetValue(id, out b)) return;
            if (notify) { try { b.Goodbye(); } catch (Exception e) { Logger.LogWarning(e.Message); } }
            NoteDeadGuest(b);
            b.Dispose(); guests.Remove(id); guestSince.Remove(id); versionAnnounced.Remove(id); peerVersions.Remove(id); pings.Remove(id); trafficWindows.Remove(id); closeGuests[id] = Time.realtimeSinceStartup + 2;
        }
        // 0.4.27 review: a Steam packet larger than a bridge frame (65512 B; no bridge version sends one on channel 77) used to throw inside Update. The catch there
        // stopped the whole bridge (a host lost every guest) and the packet stayed in Steam's queue. It is read and dropped instead, like SlowLane does on channel 80.
        float nextOversizeLog;
        void DropOversizedPacket(uint size)
        {
            byte[] skip = size <= 2 * 1024 * 1024 ? new byte[size] : steamReceiveBuffer; uint read; CSteamID from;
            SteamNetworking.ReadP2PPacket(skip, (uint)skip.Length, out read, out from, Channel);   // a buffer smaller than the packet truncates it (Steam API); either way it leaves the queue
            if (Time.realtimeSinceStartup < nextOversizeLog) return;
            nextOversizeLog = Time.realtimeSinceStartup + 30f;
            Logger.LogWarning("Dropped an oversized Steam packet on the bridge channel: " + size + " B from " + from);
        }
        void UpdateGuests()
        {
            ApplyKickRequests();
            closingSnapshot.Clear(); closingSnapshot.AddRange(closeGuests.Keys);
            foreach (CSteamID id in closingSnapshot)
                if (Time.realtimeSinceStartup >= closeGuests[id]) { SteamNetworking.CloseP2PChannelWithUser(id, Channel); closeGuests.Remove(id); }
            if (!hosting) return;
            if (!NetworkServer.active) { StopBridge(); status = "Хост завершив гру."; visible = true; return; }
            uint size;
            for (int i = 0; i < 768 && SteamNetworking.IsP2PPacketAvailable(out size, Channel); i++)
            {
                if (size > 65512) { DropOversizedPacket(size); continue; }   // 0.4.27 review: was a throw; Update's catch then stopped the host bridge for every guest
                byte[] data = steamReceiveBuffer; uint read; CSteamID sender;
                if (!SteamNetworking.ReadP2PPacket(data, size, out read, out sender, Channel) || read != size) continue;
                statSteamIn++;
                if (!IsTrusted(sender)) { HostDropLog(sender, "packet"); continue; }
                BridgeCore b;
                if (!guests.TryGetValue(sender, out b))
                {
                    if (size != 5 || data[0] != 67 || data[1] != 83 || data[2] != 83 || data[3] != 1 || data[4] != 1) continue;
                    b = Guest(sender);
                }
                else if (read == 5 && data[4] == 1 && HostRecyclesOnHello(sender, b)) b = Guest(sender);   // 0.4.17: the guest came back; its old bridge still held the old UNET connection
                if (b == null) { HostDropLog(sender, "hello"); continue; }
                // A hello is a new join attempt (e.g. the friend restarted after a crash before this
                // host noticed): restart the reliable join window for it.
                if (read == 5 && data[4] == 1) guestSince[sender] = Time.realtimeSinceStartup;
                try { b.Receive(data, (int)read); if (b.RemoteClosed) { RemoveGuest(sender, false); status = "Друг покинув гру. Інші з'єднання працюють."; } }
                catch (Exception e) { Logger.LogWarning("Guest receive failed: " + e.Message); RemoveGuest(sender, false); }
            }
            guestSnapshot.Clear(); guestSnapshot.AddRange(guests.Keys);
            foreach (CSteamID id in guestSnapshot)
                try { guests[id].Pump(); } catch (Exception e) { Logger.LogWarning("Guest send failed: " + e.Message); RemoveGuest(id, false); }
            GuardHostConnections();
        }
        void Update()
        {
            if (diagOnly) { UpdateDiagnosticsOnly(); return; }
            RetireLegacyTrials();
            float now = Time.realtimeSinceStartup;
            if (DiagnosticsEnabled && (bridge != null || hosting) && lastUpdateAt > 0 && now - lastUpdateAt > 1 && now >= nextPauseLog)
            {
                // Bridge work runs inside Update, so a long gap with small bridge work points at the game itself.
                float pauseSeconds = now - lastUpdateAt;
                diagPauses++; if (pauseSeconds > diagLongestPause) diagLongestPause = pauseSeconds;
                DiagWarn("Main game loop pause: " + pauseSeconds.ToString("F2") + " s; bridge work in previous frame " + frameMs.ToString("F1") + " ms");
                nextPauseLog = now + 10;
            }
            lastUpdateAt = now;
            if (DiagnosticsEnabled) HealthFrame();
            if (DiagnosticsEnabled && now >= nextGapLog)
            {
                nextGapLog = now + 15;
                if (bridge != null) LogReceiveGap(peer, bridge);
                foreach (var entry in guests) LogReceiveGap(entry.Key, entry.Value);
            }
            if (TraceOverlayInput && (Input.GetKeyDown(KeyCode.F8) || Input.GetKey(KeyCode.F8) || Input.GetKeyUp(KeyCode.F8) || Time.frameCount <= traceInputUntil))
                TraceOverlay("Update down=" + Input.GetKeyDown(KeyCode.F8) + " held=" + Input.GetKey(KeyCode.F8) + " up=" + Input.GetKeyUp(KeyCode.F8));
            // Focused IMGUI can hide Input.GetKey even while the physical key is held.
            // Poll only F8 while latched; missed KeyUp/focus transitions recover on physical release.
            if (overlayF8Held && !OverlayPhysicalF8Down()) overlayF8Held = false;
            if (Input.GetKeyDown(KeyCode.F8)) ToggleBridgeWindow();
            ClickLogTick();
            DiagBundleTick();
            TreeCutGuard.Tick(); TestFlush(); TestControlTick(); AllocBegin(); PositionLinkTick(); AllocEnd(0); AllocBegin(); ItemFilterTick(); AllocEnd(1); AllocBegin(); PeriodicStatsTick(); AllocEnd(2); AllocBegin(); FreezeWatchTick(); AllocEnd(3); RunInBackgroundTick(); GroupsMenuTick(); UnitGroupsTick(); BorderlessTick(); FreeCursorTick(); GuestDigTick(); CatapultCensusTick(); SysDiagTick(); VirtualPlayerTick(); BotTick(); QueueDiagTick(); LinkProbeTick(); LinkWatchdogTick(); GuestHoldTick(); RadminWatchTick(); ModMemTick(); HostPauseTick(); PauseResyncTick(); TimeSyncTick(); SlowLaneTick(); SessionShareTick(); RestSyncTick(); ResourceScatterLog.Tick(); LuaPruneTick(); LuaLiveCensusTick(); AllocCutTick(); PerfFrameTick(); PinkScanTick(); StockFillTick(); FlailWatchTick(); BuriedRescueTick(); FloatLogTick(); InfiniteCatapultTick(); MemStatsTick(); LuaCallStatsTick(); TextureLimitTick(); ChaosBotTick(); SentinelTargets.Poll();
            if (Input.GetKeyDown(KeyCode.F7)) ListMapsInChat();
            try { TrackLoad(); } catch (Exception) { }
            JoinLoadTick();
            if (!SteamManager.Initialized) return;
            long mark = System.Diagnostics.Stopwatch.GetTimestamp(), frameStart = mark;
            bool active = bridge != null || hosting;
            frameGuests = frameDiscovery = frameRead = framePump = 0;
            try
            {
                if (request == null)
                {
                    request = Callback<P2PSessionRequest_t>.Create(OnRequest);
                    failure = Callback<P2PSessionConnectFail_t>.Create(OnFailure);
                    InstallSteamJoinTrace();
                    RefreshFriends();
                    Logger.LogInfo("Steam Bridge ready; F8 opens controls. No game assemblies modified.");
                }
                UpdateGuests();
                UpdateTick();
                frameGuests = Lap(ref mark);
                UpdateDiscovery();
                frameDiscovery = Lap(ref mark);
                if (hosting) return;
                if (closeAt > 0 && Time.realtimeSinceStartup >= closeAt)
                {
                    SteamNetworking.CloseP2PChannelWithUser(closingPeer, Channel); closeAt = 0;
                }
                if (bridge == null) return;
                if (host && !NetworkServer.active)
                {
                    StopBridge(); LeaveGameSession(); status = "Хост завершив гру. З'єднання закрито."; visible = true; return;
                }
                if (!host && !bridge.Ready && Time.realtimeSinceStartup >= nextHello)
                {
                    nextHello = Time.realtimeSinceStartup + 2;
                    bridge.Hello();
                }
                uint size;
                for (int i = 0; i < 256 && SteamNetworking.IsP2PPacketAvailable(out size, Channel); i++)
                {
                    if (size > 65512) { DropOversizedPacket(size); continue; }   // 0.4.27 review: was a throw that stopped the bridge and left the game session
                    byte[] data = steamReceiveBuffer;
                    uint read; CSteamID sender;
                    if (!SteamNetworking.ReadP2PPacket(data, size, out read, out sender, Channel)) continue;
                    statSteamIn++;
                    if (sender == peer && read == size)
                    {
                        long before = bridge.Received;
                        bridge.Receive(data, (int)read);
                        if (bridge.RemoteClosed)
                        {
                            bool wasHost = host;
                            StopBridge(false);
                            if (!wasHost && UNetManager.instance != null) UNetManager.instance.Disconnect();
                            status = wasHost ? "Друг покинув гру." : "Хост покинув гру.";
                            Logger.LogInfo(status); visible = true; return;
                        }
                        if (bridge.Received != before) lastData = Time.realtimeSinceStartup;
                    }
                }
                frameRead = Lap(ref mark);
                bridge.Pump();
                framePump = Lap(ref mark);
                if (!host && bridge.Ready && !connecting)
                {
                    if (NetworkClient.active || UNetManager.instance == null) throw new InvalidOperationException("Спочатку повернись у головне меню.");
                    connecting = true;
                    ResetSessionWatch();
                    connectingSince = Time.realtimeSinceStartup;
                    Logger.LogInfo("Connecting game to loopback " + bridge.Port + " through selected Steam peer.");
                    if (modalJoin)
                    {
                        // The game's waiting modal is still open and listening; connect under it.
                        // Keep modalJoin set until the connect starts, so a failure here still
                        // reaches FailModalJoin (via StopBridge) and closes the waiting window.
                        if (UNetManager.instance.ConnectToServer("127.0.0.1", bridge.Port) == null)
                            throw new InvalidOperationException("Гра не змогла почати з'єднання.");
                        modalJoin = false;
                    }
                    else
                    {
                        // Use the same waiting/lobby UI flow as a normal direct join.
                        // Match data is transient, so the player's saved IP/port stays intact.
                        NetworkServersMenu.SelectedMatchData = new MatchList.MatchData
                        {
                            IsLanGame = true,
                            Address = new MatchAddress("127.0.0.1", (ushort)bridge.Port),
                            MatchName = "Steam Bridge"
                        };
                        NetworkWaitingModalMenu.OpenAsClientSignal.Invoke();
                    }
                    status = "Steam відповів. Гра підключається... F8 приховує вікно.";
                }
                if (!host && connecting && WatchGameSession()) return;
                if (!host && connecting && UNetManager.instance != null && UNetManager.instance.LocalLobbyPlayer != null)
                    status = "Гравець підключений. F8 приховує вікно моду.";
                if (!bridge.Ready && !host && Time.realtimeSinceStartup - started > 35)
                {
                    SteamJoinNoAnswerDiag(Time.realtimeSinceStartup - started); StopBridge(); status = "Друг не відповів за 35 с. Перевірте мод і вибір друга на обох ПК." + (OfflineStatusHint().Length > 0 ? " " + OfflineStatusHint() : "");
                }
            }
            catch (Exception e) { Logger.LogError(e); StopBridge(); status = "Міст зупинено: " + e.Message; visible = true; }
            finally
            {
                // Diagnostics are best-effort: they must never break the bridge or the frame.
                try { if (SteamManager.Initialized) UpdateTransferTick(); RecordFrame(frameStart, active); }
                catch (Exception e)
                {
                    nextStatLog = 0;
                    if (Time.realtimeSinceStartup >= nextDiagErrorLog)
                    { nextDiagErrorLog = Time.realtimeSinceStartup + 60; Logger.LogWarning("Bridge diagnostics failed: " + e.Message); }
                }
            }
        }
        static bool GameClientConnected()
        {
            if (NetworkClient.allClients == null) return false;
            foreach (NetworkClient client in NetworkClient.allClients) if (client != null && client.isConnected) return true;
            return false;
        }
        void ResetSessionWatch() { sessionSeen = false; sessionGoneSince = 0; sessionGoneFrames = 0; }
        // Returns true when it has just stopped the bridge (the caller must leave this frame's bridge work).
        bool WatchGameSession()
        {
            float now = Time.realtimeSinceStartup;
            if (GameClientConnected()) { if (!sessionSeen) RememberHost(); sessionSeen = true; sessionGoneSince = 0; sessionGoneFrames = 0; return false; }
            string reason = null;
            if (sessionSeen)
            {
                if (sessionGoneSince == 0) sessionGoneSince = now;
                sessionGoneFrames++;
                if (now - sessionGoneSince >= SessionGoneSeconds && sessionGoneFrames >= SessionGoneFrames)
                    reason = "Гра вийшла з лобі (лобі закрито, вас вилучено або зв'язок втрачено). Міст зупинено — можна підключатися знову.";
            }
            else if (connectingSince > 0 && now - connectingSince >= GameNeverStartedSeconds)
                reason = "Гра так і не почала з'єднання. Міст зупинено — спробуйте ще раз.";
            if (reason == null) return false;
            Logger.LogInfo("Game session ended while the bridge was up (seen=" + sessionSeen + "); stopping the bridge: " + reason);
            StopBridge();
            status = reason; visible = true;
            return true;
        }
        static float ReceiveAge(BridgeCore b)
        { return unchecked((uint)(Environment.TickCount - b.LastReceiveMilliseconds)) / 1000f; }
        void LogReceiveGap(CSteamID id, BridgeCore b)
        {
            if (b.Ready && b.Received > 0 && ReceiveAge(b) >= 3)
            {
                float age = ReceiveAge(b);
                diagGaps++; if (age > diagLongestGap) diagLongestGap = age;
                DiagWarn("Game data gap: peer=" + id + "; age=" + age.ToString("F1") + " s; sent=" + b.Sent + "; received=" + b.Received + SteamLinkNote(id));
            }
        }
        // 0.3.8: what Steam says about the session while game data is missing (relay or direct, connecting, error, queue).
        string SteamLinkNote(CSteamID id)
        {
            try
            {
                P2PSessionState_t s;
                if (SteamManager.Initialized && SteamNetworking.GetP2PSessionState(id, out s))
                    return "; steam link=" + (s.m_bUsingRelay != 0 ? "relay" : "P2P") + " active=" + s.m_bConnectionActive + " connecting=" + s.m_bConnecting
                        + " err=" + s.m_eP2PSessionError + " queued=" + s.m_nBytesQueuedForSend + "B/" + s.m_nPacketsQueuedForSend + "pkt";
            }
            catch (Exception) { }
            return "";
        }
        void OnRequest(P2PSessionRequest_t p)
        {
            if (JoinLoadAcceptRequest(p.m_steamIDRemote)) { SteamNetworking.AcceptP2PSessionWithUser(p.m_steamIDRemote); return; }
            if (hosting)
            {
                if (Guest(p.m_steamIDRemote) != null) SteamNetworking.AcceptP2PSessionWithUser(p.m_steamIDRemote);
                else HostDropLog(p.m_steamIDRemote, "P2P session request");
                return;
            }
            if (bridge != null && p.m_steamIDRemote == peer && (IsFriend(peer) || peerByCode))
                SteamNetworking.AcceptP2PSessionWithUser(peer);
        }
        void OnFailure(P2PSessionConnectFail_t p)
        {
            if (WatchdogFailureIsOurs(p.m_steamIDRemote, p.m_eP2PSessionError.ToString())) return;   // 0.4.15: the session the watchdog just reopened failed to come up; do not end the game session for that
            if (hosting)
            {
                // 0.4.27 review: DiagWarn never logs (DiagnosticsEnabled is false), so a guest removed here left no trace in the host's LogOutput.log
                if (guests.ContainsKey(p.m_steamIDRemote)) Logger.LogWarning("Steam P2P failure " + p.m_eP2PSessionError + " for guest " + p.m_steamIDRemote + ": its bridge is removed, the guest has to join again");
                RemoveGuest(p.m_steamIDRemote, false); DiagWarn("Guest Steam failure: " + p.m_eP2PSessionError); return;
            }
            if (bridge != null && p.m_steamIDRemote == peer)
            { if (GuestHoldsOnFailure(p.m_eP2PSessionError.ToString())) return;   // 0.4.17: a running session waits for the host (cfg Network/GuestHoldOnP2PFailureSeconds)
              Logger.LogWarning("Steam P2P failure " + p.m_eP2PSessionError + " for the host " + peer + ": leaving the session");   // 0.4.27 review: was DiagWarn only, which never logs
              DiagWarn("Steam P2P failure " + p.m_eP2PSessionError); StopBridge(false); LeaveGameSession(); status = "Втрачено зв'язок через Steam (код " + p.m_eP2PSessionError + "). Причина виходу хоста невідома."; visible = true; }
        }
        bool Send(byte[] p)
        { return SendTo(peer, p); }
        bool SendTo(CSteamID recipient, byte[] p)
        { return SendRangeTo(recipient, p, p.Length); }
        bool SendRangeTo(CSteamID recipient, byte[] p, int length)
        { if (TestShape(recipient, p, length)) return true; return SendRangeToCore(recipient, p, length); }
        bool SendRangeToCore(CSteamID recipient, byte[] p, int length)
        {
            P2PSessionState_t state;
            if (SteamNetworking.GetP2PSessionState(recipient, out state))
            {
                if (state.m_nBytesQueuedForSend > statQueueMax) statQueueMax = state.m_nBytesQueuedForSend;
                if (state.m_nBytesQueuedForSend > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Черга Steam перевищила 4 МБ; з'єднання не встигає.");
            }
            // Reliable preserves whole UDP datagrams >1200 bytes using Steam fragmentation.
            // Mixed: small game datagrams go unreliable, since the game (UNet) has its own
            // reliability; k_EP2PSendUnreliable (not NoDelay) batches until the session is open.
            // Once the local game session has ended, UNet's disconnect datagram is sent only once
            // and never retransmitted, so it must go reliably or the peer keeps a ghost player
            // until the 10-minute DisconnectTimeout.
            bool gameLive = hosting ? NetworkServer.active && JoinSettled(recipient) : NetworkClient.active;
            bool unreliable = mixedSend && gameLive && BridgeCore.CanSendUnreliable(p, length);
            if (!SteamNetworking.SendP2PPacket(recipient, p, (uint)length, unreliable ? EP2PSend.k_EP2PSendUnreliable : EP2PSend.k_EP2PSendReliable, Channel))
                throw new InvalidOperationException("Steam не прийняв пакет.");
            statSteamOut++;
            if (unreliable) statUnreliable++; else QdCountOut(length);
            return true;
        }
        void StartBridge(bool asHost)
        {
            try
            {
                if (closeGuests.Count != 0) throw new InvalidOperationException("Зачекай 2 секунди після закриття попередніх з'єднань.");
                // The mode is fixed for the whole session; the F8 switch applies to the next one.
                mixedSend = SendModeName() == SendMixed;
                if (asHost)
                {
                    if (bridge != null) throw new InvalidOperationException("Спочатку зупини клієнтське з'єднання.");
                    if (UNetManager.instance == null || !NetworkServer.active) throw new InvalidOperationException("Спочатку створи багатокористувацьке лобі.");
                    hostingPort = UNetManager.instance.networkPort; hosting = true;
                    DiagBeginSession();
                    SteamNetworking.AllowP2PPacketRelay(AllowRelayOn);
                    status = "Хост активний: дозволено гравцям Steam. До 4 гравців разом із хостом.";
                    Logger.LogInfo("Multi-client host ready; game port=" + hostingPort + "; send mode=" + SendModeName()); return;
                }
                if (closeAt > 0 && Time.realtimeSinceStartup < closeAt)
                    throw new InvalidOperationException("Зачекай 2 секунди після закриття попереднього з'єднання.");
                if (peer.m_SteamID == 0 || (!IsFriend(peer) && !peerByCode))
                    throw new InvalidOperationException("Вибери друга зі списку Steam або введи код хоста.");
                if (UNetManager.instance == null) throw new InvalidOperationException("Дочекайся головного меню гри.");
                if (asHost && !NetworkServer.active) throw new InvalidOperationException("Спочатку створи багатокористувацьке лобі.");
                if (!asHost && NetworkClient.active) throw new InvalidOperationException("Спочатку вийди в головне меню.");
                SteamNetworking.AllowP2PPacketRelay(AllowRelayOn);
                host = asHost; connecting = false; started = Time.realtimeSinceStartup; nextHello = 0; lastData = 0;
                PosNewSession();   // 0.4.27 review: a new session with the SAME host kept the old sequence number, so the first position packet counted every packet the host sent to others since the last session as lost (loss 99 %, +1 tick smoothing delay for about 40 s); verify: also drops the old packets still queued on the position channel
                ResetSessionWatch(); connectingSince = 0;
                bridge = new BridgeCore(host, UNetManager.instance.networkPort, Send, delegate(byte[] buf, int n) { return SendRangeTo(peer, buf, n); });
                CSteamID diagPeer = peer;
                bridge.ExtraHandler = delegate(byte kind, byte[] payload) { OnExtraPacket(diagPeer, kind, payload); };
                DiagBeginSession();
                status = host ? "Очікую вибраного друга через Steam. Залишайся в лобі." : "Очікую відповіді моду друга через Steam...";
                Logger.LogInfo("Steam Bridge mode=" + (host ? "host" : "client") + " UDP loopback=" + bridge.Port + "; send mode=" + SendModeName());
                if (!host) SteamJoinStartDiag();
            }
            catch (Exception e) { StopBridge(); status = e.Message; Logger.LogError(e); }
        }
        void StopBridge(bool notify = true)
        {
            SessionShareOnStop();   // 0.3.89: last report while the links still exist
            ClearLobbyHostData();
            // Capture only bounded metadata before teardown; all archive IO runs afterwards.
            BundleSnapshot bundle = null;
            try { if (fzActive) EndFreeze(Time.realtimeSinceStartup); bundle = CaptureBundle(false); }
            catch (Exception e) { Logger.LogWarning("Diagnostics snapshot failed: " + e.Message); }
            ClearReconnectOpen();
            FlushStats();
            DiagShareOnExit();
            DiagEndSession();
            hosting = false;
            foreach (CSteamID id in new List<CSteamID>(guests.Keys)) RemoveGuest(id, notify);
            if (bridge != null)
            {
                if (notify && SteamManager.Initialized)
                {
                    try { bridge.Goodbye(); } catch (Exception e) { Logger.LogWarning("Exit notification failed: " + e.Message); }
                }
                bridge.Dispose(); bridge = null;
                // Give the reliable exit packet time to leave before closing its channel.
                closingPeer = peer; closeAt = Time.realtimeSinceStartup + 2;
            }
            connecting = false; peerByCode = false;
            ResetSessionWatch(); connectingSince = 0;
            starvedWindows = 0; starvationNote = starvationBanner = null; clientLastSent = clientLastReceived = 0; CancelUpdateTransfers();
            FailModalJoin();
            ResetProposals();
            AutoBundleOnSessionEnd(bundle);
        }
        void RefreshFriends()
        {
            friends.Clear();
            for (int i = 0; i < SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate); i++)
            {
                CSteamID f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                FriendGameInfo_t info;
                if (SteamFriends.GetFriendGamePlayed(f, out info) && info.m_gameID.AppID().m_AppId == 227860) friends.Add(f);
            }
        }
        GUIStyle bannerStyle;
        // Visible even when the F8 window is hidden, so a guest sees at once that the stop is on the host's side.
        void DrawBanner()
        {
            if (starvationBanner == null || bridge == null) return;
            starvationBanner = PingIsFresh(peer) ? "Відповіді на пінг є, але ігрових даних від хоста мало" : "Ігрових даних від хоста мало; відповідей на пінг немає";
            if (bannerStyle == null)
            {
                bannerStyle = new GUIStyle(GUI.skin.label);
                bannerStyle.alignment = TextAnchor.MiddleCenter; bannerStyle.fontSize = 16; bannerStyle.fontStyle = FontStyle.Bold; bannerStyle.wordWrap = true;
            }
            Rect r = new Rect(Screen.width * 0.5f - 300f, 10f, 600f, 44f);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.78f); GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.85f, 0.2f, 1f); GUI.Label(r, starvationBanner, bannerStyle);
            GUI.color = old;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);
        static bool OverlayPhysicalF8Down()
        {
            try { return (GetAsyncKeyState(0x77) & 0x8000) != 0; }
            catch (Exception) { return Input.GetKey(KeyCode.F8); }
        }
        bool releaseBridgeFocus;
        Rect joinCodeRect;
        void ToggleBridgeWindow()
        {
            // IMGUI can own F8 before Update. Both paths share one press latch.
            if (overlayF8Held || overlayF8Frame == Time.frameCount) return;
            overlayF8Held = true;
            overlayF8Frame = Time.frameCount;
            if (visible) HideBridgeWindow(); else visible = true;
            TraceOverlay("Toggled");
        }
        void HandleOverlayKey()
        {
            Event e = Event.current;
            if (e.keyCode != KeyCode.F8) return;
            if (e.type == EventType.KeyUp && !OverlayPhysicalF8Down()) overlayF8Held = false;
            if (e.type != EventType.KeyDown) return;
            ToggleBridgeWindow();
            e.Use();
        }
        void HideBridgeWindow()
        {
            visible = false;
            GUIUtility.keyboardControl = 0;
            releaseBridgeFocus = true;
        }
        void ReleaseBridgeFocus()
        {
            GUIUtility.keyboardControl = 0;
            GUI.FocusControl(null);
            releaseBridgeFocus = false;
        }
        void OnGUI()
        {
            if (diagOnly) { OnGuiDiagnosticsOnly(); BotOnGui(); return; }
            BotOnGui();   // 0.4.10 in-game bot: one status line while a run is active
            if (TraceOverlayInput)
            {
                lastOverlayFocus = GUI.GetNameOfFocusedControl();
                Event inputEvent = Event.current;
                if (inputEvent.isKey || inputEvent.type == EventType.MouseDown || inputEvent.type == EventType.MouseUp)
                {
                    TraceOverlay("OnGUI type=" + inputEvent.type + " raw=" + inputEvent.rawType + " key=" + inputEvent.keyCode);
                    if (inputEvent.keyCode == KeyCode.F8) traceInputUntil = Time.frameCount + 2;
                }
            }
            HandleOverlayKey();
            if (releaseBridgeFocus) ReleaseBridgeFocus();
            DrawJoinLoadConsent();
            DrawSocialOverlay(); DrawUnitGroups(); DrawGroupsMenu(); SentinelTargets.DrawBox();
            try { DrawPauseBanner(); } catch (Exception) { }
            if (!visible) { DrawBanner(); DrawF8Hint(); return; }
            F8HintDone();
            box = GUILayout.Window(227877, box, Draw, "Castle Story — Steam Bridge " + BridgeVersion.Text + " (build " + BridgeVersion.Build + ") / F8");
            ItemFilterGui();
        }
        bool showMore;
        Vector2 overlayContentScroll;
        GUIStyle overlayButtonStyle, overlayToggleStyle, overlayLabelStyle;
        void Draw(int id)
        {
            // Long hints/toggles must wrap, otherwise their minimum width stretches scroll content.
            GUIStyle button = GUI.skin.button, toggle = GUI.skin.toggle, label = GUI.skin.label;
            if (overlayButtonStyle == null)
            {
                overlayButtonStyle = new GUIStyle(button); overlayButtonStyle.wordWrap = true;
                overlayToggleStyle = new GUIStyle(toggle); overlayToggleStyle.wordWrap = true;
                overlayLabelStyle = new GUIStyle(label); overlayLabelStyle.wordWrap = true;
            }
            // Layout is calculated after the callback. Entries must retain wrapped style objects.
            try { GUI.skin.button = overlayButtonStyle; GUI.skin.toggle = overlayToggleStyle; GUI.skin.label = overlayLabelStyle; DrawContent(id); }
            finally { GUI.skin.button = button; GUI.skin.toggle = toggle; GUI.skin.label = label; }
        }
        void DrawContent(int id)
        {
            if (TraceOverlayInput)
            {
                lastOverlayFocus = GUI.GetNameOfFocusedControl();
                if (Event.current.isKey || Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp)
                    TraceOverlay("Draw type=" + Event.current.type + " key=" + Event.current.keyCode);
            }
            HandleOverlayKey();
            // Simple layout: a shorter window (the scroll view still scrolls); the classic one keeps its 800 px.
            float maxContentHeight = SimpleF8 ? (f8Tab == 0 ? 470f : 640f) : 800f;
            overlayContentScroll = GUILayout.BeginScrollView(overlayContentScroll, GUILayout.Width(510), GUILayout.Height(Math.Max(180f, Math.Min(maxContentHeight, Screen.height - box.y - 65f))));
            // A scroll view otherwise measures unconstrained content and creates a horizontal bar.
            GUILayout.BeginVertical(GUILayout.Width(480));
            // Let the target button finish MouseUp before releasing window/text focus.
            bool releaseAfterClick = Event.current.type == EventType.MouseUp && !joinCodeRect.Contains(Event.current.mousePosition)
                && GUI.GetNameOfFocusedControl() == "CSBJoinCode";
            if (SimpleF8) DrawSimpleBody();
            else
            {
            GUILayout.Label(status);
            DrawSteamStatusWarning();
            DrawPing();
            if (offerFrom.m_SteamID != 0 || xferActive) DrawUpdate();
            if (GUILayout.Button((showMore ? "▾ " : "▸ ") + "Додатково: оновлення, діагностика, предмети")) showMore = !showMore;
            if (showMore)
            {
                DrawTestPanel();
                if (!(offerFrom.m_SteamID != 0 || xferActive)) DrawUpdate();
                DrawDiagnostics(false);
                DrawJoinLoad();
                DrawDiagBundle();
                DrawBorderless();
                DrawFixes();
                DrawGroupsSettings();
                if (GUILayout.Button("Предмети будівництва (вимкнути непотрібні, з картинками)...")) ItemFilter.WindowOpen = !ItemFilter.WindowOpen;
                if (NetworkCompat.Status.Length > 0) GUILayout.Label(NetworkCompat.Status);
                if (ModInventory.F8Line.Length > 0) GUILayout.Label(ModInventory.F8Line);   // 0.4.14 MOD-INV (a cached string)
            }
            if (!SteamManager.Initialized) GUILayout.Label("Запусти Steam і оригінальну гру зі своєї бібліотеки.");
            else if (hosting)
            {
                GUILayout.Label("Хост + до 3 гравців. Підключених мостів: " + guests.Count + "; режим " + (mixedSend ? SendMixed : SendReliable));
                ulong myCode = SteamUser.GetSteamID().m_SteamID;
                GUILayout.Label((AllowStrangersOn ? "Код хоста для будь-кого (не лише друзів): " : "Сторонні вимкнено (AllowStrangers=false). Код: ") + myCode);
                DrawRadminHost();
                DrawHostPauseButton();
                if (GUILayout.Button("Скопіювати код хоста")) { GUIUtility.systemCopyBuffer = myCode.ToString(); status = "Код скопійовано. Надішли його гравцеві."; }
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(260));
                foreach (KeyValuePair<CSteamID, BridgeCore> g in guests)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(PingName(g.Key) + ": " + (g.Value.Ready ? "з'єднаний" : "очікує") + "; пакети " + g.Value.Sent + "/" + g.Value.Received);
                    if (GUILayout.Button("Кік", GUILayout.Width(46))) RequestKick(g.Key, 0f);
                    if (GUILayout.Button("Бан 10 хв", GUILayout.Width(78))) RequestKick(g.Key, 10f);
                    if (GUILayout.Button("Бан 60 хв", GUILayout.Width(78))) RequestKick(g.Key, 60f);
                    GUILayout.EndHorizontal();
                    if (g.Value.Received > 0) GUILayout.Label("Останні дані від друга: " + ReceiveAge(g.Value).ToString("F1") + " с тому");
                    P2PSessionState_t s;
                    if (SteamNetworking.GetP2PSessionState(g.Key, out s)) GUILayout.Label("Черга " + s.m_nBytesQueuedForSend + "; помилка " + s.m_eP2PSessionError);
                }
                GUILayout.EndScrollView();
                DrawDiagnostics(true);
                DrawBans();
                if (GUILayout.Button("Зупинити хост-міст (від'єднає всіх гравців)")) { autoHostSuppressed = true; StopBridge(); status = "Хост-міст зупинено."; }
            }
            else if (bridge == null)
            {
                DrawReconnect(); GUILayout.Label("Для списку лобі всім потрібна 0.3.3 або новіша. Або вибери хоста вручну:");
                if (GUILayout.Button("Оновити список гравців Castle Story")) RefreshFriends();
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(190));
                foreach (CSteamID f in friends)
                    if (GUILayout.Button((f == peer ? "✓ " : "") + SteamFriends.GetFriendPersonaName(f))) peer = f;
                GUILayout.EndScrollView();
                if (friends.Count == 0) GUILayout.Label("Немає друзів у Castle Story. Усі запустіть гру через Steam.");
                if (GUILayout.Button("Я хост: дозволити до 3 друзів Steam")) StartBridge(true);
                if (GUILayout.Button("Приєднатися до вибраного хоста через Steam")) { peerByCode = false; StartBridge(false); }
                GUILayout.Label("Або за кодом хоста (працює і з не-друзями):");
                GUI.SetNextControlName("CSBJoinCode");
                joinCode = GUILayout.TextField(joinCode ?? "", 20);
                if (Event.current.type == EventType.Repaint) joinCodeRect = GUILayoutUtility.GetLastRect();
                if (GUILayout.Button("Приєднатися за кодом"))
                {
                    ulong typed;
                    if (!ulong.TryParse((joinCode ?? "").Trim(), out typed) || !LooksLikeSteamId(typed)) status = "Код має бути числом SteamID (17 цифр, починається з 7656119…).";
                    else if (typed == SteamUser.GetSteamID().m_SteamID) status = "Це твій власний код.";
                    else { peer = new CSteamID(typed); peerByCode = !IsFriend(peer); StartBridge(false); }
                }
                DrawRadminJoin();
                GUILayout.Label("Хост створює лобі й дозволяє друзів. Усі входять у лобі, потім хост запускає карту.");
                // Test switch: applies to the next bridge session; saved in BepInEx\config.
                if (sendMode != null && GUILayout.Button("Режим надсилання: " + SendModeName() + " (натисни, щоб змінити)"))
                    sendMode.Value = SendModeName() == SendMixed ? SendReliable : SendMixed;
            }
            else
            {
                GUILayout.Label("Друг: " + SteamFriends.GetFriendPersonaName(peer));
                GUILayout.Label("Пакети гри: відправлено " + bridge.Sent + ", отримано " + bridge.Received + "; режим " + (mixedSend ? SendMixed : SendReliable));
                P2PSessionState_t s;
                if (SteamNetworking.GetP2PSessionState(peer, out s))
                    GUILayout.Label("Steam: " + (s.m_bUsingRelay != 0 ? "relay" : "P2P") + "; черга " + s.m_nBytesQueuedForSend + " байт; помилка " + s.m_eP2PSessionError);
                if (lastData > 0) GUILayout.Label("Останні дані: " + (Time.realtimeSinceStartup - lastData).ToString("F1") + " с тому");
                if (starvationNote != null) GUILayout.Label(starvationNote);
                DrawDiagnostics(true);
                if (GUILayout.Button("Зупинити міст (з'єднання гри перерветься)")) { bool wasClient = !host; StopBridge(); if (wasClient) LeaveGameSession(); status = "Міст зупинено. Повернись у головне меню перед новою спробою."; }
            }
            if (GUILayout.Button("Приховати — відкрити знову клавішею F8")) { HideBridgeWindow(); ReleaseBridgeFocus(); }
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 530, 25));
            if (releaseAfterClick) ReleaseBridgeFocus();
        }
        void OnDestroy()
        {
            ShutdownJoinLoad(); PtFlush(); DxShutdown();
            ShutdownGroupsMenu();
            ShutdownGameplayFixes();
            CancelSocialDownload();
            if (mapDownloadResult != null) mapDownloadResult.Unregister();
            UnhookUnityLog();
            ShutdownDiscovery();
            ShutdownLoadGuard();
            StopBridge();
            if (request != null) request.Unregister();
            if (failure != null) failure.Unregister();
        }
    }
}


