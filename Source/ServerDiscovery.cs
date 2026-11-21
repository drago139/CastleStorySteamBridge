using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.Game.Network;
using Brix.Network;
using Brix.UI.Builder.Menu;
using HarmonyLib;
using Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    public sealed partial class SteamBridgePlugin
    {
        const string PresenceKey = "csb_lobby_v1";
        const string MatchTag = "CSB:";
        static SteamBridgePlugin discoveryInstance;
        Harmony discoveryHarmony;
        bool autoHostSuppressed, discoveryReady, gameStarting;
        // True while the game's own "Joining Game" modal waits for the Steam bridge.
        bool modalJoin;
        float nextDiscovery;
        string ownAdvertisement;
        ulong lastHostedLobby;
        CSteamID publishedBridgeLobby;
        static MethodInfo lobbySceneCallback;
        static int staleLobbyCallbacks;
        static int steamCallbackExceptions;
        static int unsupportedWorkshopMetadata;
        static int tracedLuaErrors;
        readonly Dictionary<CSteamID, MatchList.MatchData> advertised = new Dictionary<CSteamID, MatchList.MatchData>();

        void Awake()
        {
            BindConfig();
            if (DiagOnlyRequested()) { AwakeDiagnosticsOnly(); return; }
            ApplyLeanBuild();   // 0.4.25: log-only diagnostics and GC pacing forced off before any module binds its switch
            CleanupUpdateLeftovers();
            NetworkCompat.Apply(Logger);
            try { ModInventory.Install(Config, Logger); } catch (Exception e) { Logger.LogWarning("Mod inventory skipped: " + e.Message); }   // 0.4.14 MOD-INV (log only; before the discovery try, so a failing install below is still inventoried)
            try
            {
                discoveryInstance = this;
                discoveryHarmony = new Harmony("local.castlestory.steambridge.discovery");
                MethodInfo getMatches = typeof(MatchList).GetMethod("GetMatchList");
                MethodInfo join = typeof(NetworkServersActionsMenu).GetMethod("OnJoin", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo start = typeof(UNetManager).GetMethod("OnLobbyServerPlayersReady");
                MethodInfo connect = typeof(UNetManager).GetMethod("ConnectToServerWithMatchData");
                if (getMatches == null || join == null || start == null || connect == null) throw new MissingMethodException("Server menu hooks not found");
                discoveryHarmony.Patch(getMatches, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AddAdvertisedMatches", BindingFlags.Static | BindingFlags.NonPublic)));
                discoveryHarmony.Patch(join, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("JoinAdvertisedMatch", BindingFlags.Static | BindingFlags.NonPublic)));
                discoveryHarmony.Patch(start, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeMapStart", BindingFlags.Static | BindingFlags.NonPublic)));
                // ServerInspectorMenu also joins through this method, bypassing the ActionsMenu button.
                discoveryHarmony.Patch(connect, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("ConnectAdvertisedMatch", BindingFlags.Static | BindingFlags.NonPublic)));
                // 0.3.8: closing the waiting modal clears NetworkWaitingModalMenu.OnJoinSuccessSignal, so after the first
                // successful join the multiplayer menu no longer hears the next one and the lobby is drawn over the server
                // list (buttons of the list, lobby rows on top). Re-subscribe the menu right before the signal fires.
                MethodInfo joinSuccess = typeof(NetworkWaitingModalMenu).GetMethod("OnJoinSuccess", BindingFlags.Instance | BindingFlags.NonPublic);
                if (joinSuccess != null) discoveryHarmony.Patch(joinSuccess, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeJoinSuccess", BindingFlags.Static | BindingFlags.NonPublic)), new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AfterJoinSuccess", BindingFlags.Static | BindingFlags.NonPublic)));   // 0.4.19: postfix releases the menus connected only for this signal
                else Logger.LogWarning("Join success hook not found; after a second join the lobby may draw over the server list.");
                MethodInfo abort = typeof(NetworkWaitingActionsMenu).GetMethod("OnAbort", BindingFlags.Static | BindingFlags.NonPublic);
                if (abort != null) discoveryHarmony.Patch(abort, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AbortModalJoin", BindingFlags.Static | BindingFlags.NonPublic)));
                else Logger.LogWarning("Join abort hook not found; cancel the Steam join with F8.");
                discoveryReady = true;
                Logger.LogInfo("Server list integration ready. Hosts advertised to Steam friends only.");
                InstallLobbySceneGuard();
                InstallWorkshopMetadataGuard();
                InstallImprintRegistryGuard();
                InstallLuaAttribution();
                try { discoveryHarmony.Patch(typeof(CallbackDispatcher).GetMethod("ExceptionHandler"), new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("TraceSteamCallbackException", BindingFlags.Static | BindingFlags.NonPublic))); }
                catch (Exception e) { Logger.LogWarning("Steam callback stack capture unavailable: " + e.Message); }
                InitLoadGuard();
                InitLobbyPing();
                InstallDiscoveryDiag();
                InstallTimeoutPolicy(); InstallExtrapolation(); InstallPositionLink(); InstallAdaptiveSmoothing(); InstallQueueDiag(); InstallLinkProbe(); InstallModMem(); InstallGuestCatapultCooldown(); InstallHostPause(); InstallPauseResync(); InstallTimeSync(); InstallSessionShare(); InstallRestSync(); InstallLuaPrune(); InstallLuaCallStats(); InstallStateSnapshot(); InstallHitchProfiler(); InstallItemFilter(); InstallPeriodicStats(); InstallReconnect(); InstallFreezeWatch(); InstallRunInBackground(); InstallDiagBundle(); InstallRadmin(); InstallUnitGroups(); InstallBorderless(); InstallFreeCursor(); InstallPerfTrace(); InstallAllocCut(); InstallLinkWatchdog(); InstallLinkResilience(); InstallJoinLoad();
                try { InstallGuestDigMarker(discoveryHarmony); Logger.LogInfo("GUEST-DIG-MARKER hook ready"); } catch (Exception e) { Logger.LogWarning("GUEST-DIG-MARKER unavailable: " + e); }
                try { ClientCatapultMarker.Install(discoveryHarmony); Logger.LogInfo("CATAPULT-MARKER guest-only hooks=3 ready"); } catch (Exception e) { Logger.LogWarning("CATAPULT-MARKER unavailable: " + e); }
                try { ResourceScatterLog.Install(discoveryHarmony, Config, PtLog); }
                catch (Exception e) { Logger.LogWarning("Resource scatter observer unavailable: " + e.GetType().Name); }
                PatchFixes();
                InitGameplayFixes();
            }
            catch (Exception e)
            {
                if (discoveryHarmony != null) discoveryHarmony.UnpatchSelf();
                Logger.LogError("Server discovery unavailable; manual F8 connection remains available: " + e);
            }
        }

        void InstallLobbySceneGuard()
        {
            try
            {
                lobbySceneCallback = typeof(NetworkLobbyPlayer).GetMethod("OnSceneLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
                if (lobbySceneCallback == null) { Logger.LogWarning("Lobby scene guard unavailable: callback not found."); return; }
                discoveryHarmony.Patch(lobbySceneCallback, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeLobbySceneLoaded", BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Logger.LogWarning("Lobby scene guard unavailable: " + e.Message); }
        }

        // The guard lives in its OWN Harmony instance that is never unpatched: the game's late FactorylessImprint.OnDestroy calls arrive AFTER our plugin cleanup
        // (same frame, found in Phase 5), so removing the patch with the other guards re-opened the null reference at "Exit to desktop". It only skips a native
        // unregister when the registry singleton is already gone, so it stays harmless if it outlives the plugin.
        static Harmony imprintHarmony;
        void InstallImprintRegistryGuard()
        {
            try
            {
                if (imprintHarmony != null) return;
                MethodInfo unregister = typeof(Brix.External.Factories.FactorylessImprint).GetMethod("UnregisterId", BindingFlags.Instance | BindingFlags.NonPublic);
                if (unregister == null) { Logger.LogWarning("Imprint registry guard unavailable: UnregisterId not found."); return; }
                Harmony h = new Harmony("local.castlestory.steambridge.imprint");   // B-034: set the field only after Patch succeeded, so a failed attempt can be retried
                h.Patch(unregister, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeImprintUnregister", BindingFlags.Static | BindingFlags.NonPublic)));
                imprintHarmony = h;
                Logger.LogInfo("Imprint registry lifetime guard ready.");
            }
            catch (Exception e) { Logger.LogWarning("Imprint registry guard unavailable: " + e.Message); }
        }

        static int imprintLiveTraces, imprintGoneTraces;
        static bool BeforeImprintUnregister()
        {
            // The native quit flag does not cover registry-first scene destruction.
            // Unity equality also rejects a destroyed native singleton. All live calls stay native.
            bool alive = Brix.Components.BrixSingleton<Brix.External.Factories.MainGameObjectRegistry>.Instance != null;
            if (discoveryInstance != null && discoveryInstance.TraceOverlayInput
                && (alive ? ++imprintLiveTraces : ++imprintGoneTraces) <= 3)
                discoveryInstance.Logger.LogInfo("IMPRINTTRACE registryAlive=" + alive + " runOriginal=" + alive);
            return alive;
        }

        static bool BeforeLobbySceneLoaded(NetworkLobbyPlayer __instance)
        {
            // Unity can retain a managed scene callback after its native lobby component is gone.
            // Keep the original callback for every live component with its required identity.
            if (__instance != null && __instance.GetComponent<NetworkIdentity>() != null) return true;
            bool destroyed = __instance == null;
            if (destroyed && !object.ReferenceEquals(__instance, null))
            {
                try
                {
                    Type action = typeof(UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>);
                    UnityEngine.SceneManagement.SceneManager.sceneLoaded -= (UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>)Delegate.CreateDelegate(action, __instance, lobbySceneCallback);
                }
                catch (Exception e) { if (discoveryInstance != null) discoveryInstance.Logger.LogWarning("Could not detach stale lobby scene callback: " + e.Message); }
            }
            staleLobbyCallbacks++;
            if (discoveryInstance != null && staleLobbyCallbacks <= 3) discoveryInstance.Logger.LogWarning("Skipped invalid lobby scene callback (" + (destroyed ? "destroyed component" : "missing NetworkIdentity") + "); total " + staleLobbyCallbacks);
            return false;
        }

        static void TraceSteamCallbackException(Exception e)
        {
            // The game's dispatcher prints only e.Message, losing the callback and stack.
            if (e != null && discoveryInstance != null && ++steamCallbackExceptions <= 5) discoveryInstance.Logger.LogWarning("Steam callback exception: " + e);
        }

        void InstallLuaAttribution()
        {
            try
            {
                // No descriptor Index hook: it is a hot path even when no released target occurs.
                discoveryHarmony.Patch(typeof(Brix.Lua.LuaLibs).GetMethod("LuaExceptionCatcher"), new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("TraceLuaError", BindingFlags.Static | BindingFlags.NonPublic)));
                MethodInfo kill = typeof(MoonSharp.Interpreter.Script).GetMethod("_Kill", BindingFlags.Static | BindingFlags.NonPublic);
                if (kill != null) discoveryHarmony.Patch(kill, null, null, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("TraceLuaKill", BindingFlags.Static | BindingFlags.NonPublic)), null);
            }
            catch (Exception e) { Logger.LogWarning("Lua attribution unavailable: " + e.Message); }
        }

        static void TraceLuaError(Exception __0)
        {
            try
            {
                MoonSharp.Interpreter.InterpreterException e = __0 as MoonSharp.Interpreter.InterpreterException;
                if (e != null && discoveryInstance != null && tracedLuaErrors++ < 3) discoveryInstance.Logger.LogWarning("Lua error location: " + e.DecoratedMessage);
            }
            catch (Exception) { }
        }

        static void TraceLuaKill(Exception __exception, MoonSharp.Interpreter.Script __0)
        {
            try
            {
                if (__exception != null && discoveryInstance != null && tracedLuaErrors++ < 3) discoveryInstance.Logger.LogWarning("MoonSharp script shutdown failed: script=" + (__0 == null ? "unknown" : __0.FriendlyName) + "; " + __exception);
            }
            catch (Exception) { }
        }

        void InstallWorkshopMetadataGuard()
        {
            try
            {
                MethodInfo metadata = typeof(Brix.Steam.WorkshopItem).GetMethod("DeserialiseMetadata", BindingFlags.Static | BindingFlags.NonPublic);
                if (metadata == null) { Logger.LogWarning("Workshop metadata guard unavailable: parser not found."); return; }
                discoveryHarmony.Patch(metadata, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AfterWorkshopMetadata", BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Logger.LogWarning("Workshop metadata guard unavailable: " + e.Message); }
        }

        static void AfterWorkshopMetadata(ref System.Collections.Hashtable __result)
        {
            // Subscribed mods need not have Castle Story's map/preset metadata.
            // The native callback unboxes both keys without checking for null.
            if (__result != null && __result["ItemType"] is long && __result["ItemTags"] is long) return;
            if (__result == null) __result = new System.Collections.Hashtable();
            if (!(__result["ItemType"] is long)) __result["ItemType"] = (long)Brix.Steam.WorkshopItemType.Unset;
            if (!(__result["ItemTags"] is long)) __result["ItemTags"] = 0L;
            if (discoveryInstance != null && ++unsupportedWorkshopMetadata <= 3) discoveryInstance.Logger.LogInfo("Workshop metadata has no native map/preset fields; using Unset for missing fields.");
        }

        void Publish(string value)
        {
            if (ownAdvertisement == value) return;
            if (!SteamFriends.SetRichPresence(PresenceKey, value))
            { Logger.LogWarning("Steam did not accept lobby advertisement"); return; }
            ownAdvertisement = value;
        }

        void UpdateDiscovery()
        {
            if (!discoveryReady || Time.realtimeSinceStartup < nextDiscovery) return;
            nextDiscovery = Time.realtimeSinceStartup + 3;
            UNetManager manager = UNetManager.instance;
            if (!NetworkServer.active) { autoHostSuppressed = false; gameStarting = false; }
            CSteamID lobby = SteamManager.Instance.LobbyId;
            if (NetworkServer.active && manager != null && manager.LocalPlayerIsHost && lobby.m_SteamID != 0 && lastHostedLobby != lobby.m_SteamID)
            { lastHostedLobby = lobby.m_SteamID; autoHostSuppressed = false; gameStarting = false; }
            bool openLobby = !gameStarting && manager != null && Neo.multiplayer && NetworkServer.active && manager.LocalPlayerIsHost
                && lobby.m_SteamID != 0 && SteamMatchmaking.GetNumLobbyMembers(lobby) > 0
                && SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID();
            if (openLobby && !hosting && bridge == null && !autoHostSuppressed && closeGuests.Count == 0) StartBridge(true);
            int count = manager == null || manager.LobbyPlayerList == null ? 1 : manager.LobbyPlayerList.Count;
            if (openLobby && hosting && count < 4)
                Publish("0.3.0|" + lobby.m_SteamID + "|" + Math.Max(1, count));
            else Publish(null);
            PublishLobbyHostData(lobby, openLobby && hosting);

            advertised.Clear();
            // Discovery belongs to the menus; avoid Steam friend scans during gameplay.
            if (NetworkClient.active || NetworkServer.active) return;
            int addressIndex = 1; System.Text.StringBuilder presenceSummary = new System.Text.StringBuilder(); int inGame = 0;
            for (int i = 0; i < SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate); i++)
            {
                CSteamID friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                FriendGameInfo_t info;
                if (!SteamFriends.GetFriendGamePlayed(friend, out info) || info.m_gameID.AppID().m_AppId != 227860) continue;
                SteamFriends.RequestFriendRichPresence(friend);
                string presence = SteamFriends.GetFriendRichPresence(friend, PresenceKey);
                inGame++; presenceSummary.Append(SteamFriends.GetFriendPersonaName(friend)).Append('=').Append(String.IsNullOrEmpty(presence) ? "(no lobby advert)" : presence).Append(' ');
                if (String.IsNullOrEmpty(presence)) continue;
                string[] fields = presence.Split('|'); ulong lobbyId; int players;
                if (fields.Length != 3 || fields[0] != "0.3.0" || !UInt64.TryParse(fields[1], out lobbyId) || lobbyId == 0
                    || !Int32.TryParse(fields[2], out players) || players < 1 || players >= 4) continue;
                // A loopback-only marker is never contacted: the Join prefix starts the Steam bridge.
                MatchAddress address = new MatchAddress("127.254." + (addressIndex / 256) + "." + (addressIndex % 256), 3000);
                addressIndex++;
                if (addressIndex >= 65535) break;
                string name = SteamFriends.GetFriendPersonaName(friend);
                advertised.Add(friend, new MatchList.MatchData {
                    Address = address, FacilitatorGUID = MatchTag + friend.m_SteamID,
                    LobbySteamID = new CSteamID(lobbyId), MatchName = "[Steam Bridge] " + name,
                    OwnerName = name, PlayerCount = players, IsLanGame = false,
                    LastUpdateTimestamp = Time.realtimeSinceStartup, Version = Neo.SemanticVersion
                });
            }
            LogFriendPresence(inGame + " in game: " + presenceSummary);
        }

        static void AddAdvertisedMatches(Dictionary<MatchAddress, MatchList.MatchData> __result)
        {
            SteamBridgePlugin plugin = discoveryInstance;
            if (plugin == null || !plugin.discoveryReady || __result == null) return;
            try
            {
                foreach (MatchAddress address in new List<MatchAddress>(__result.Keys))
                {
                    MatchList.MatchData data = __result[address];
                    bool replaced = data.FacilitatorGUID != null && data.FacilitatorGUID.StartsWith(MatchTag, StringComparison.Ordinal);
                    foreach (MatchList.MatchData announced in plugin.advertised.Values)
                        if (!data.IsLanGame && data.LobbySteamID == announced.LobbySteamID) replaced = true;
                    if (replaced) __result.Remove(address);
                }
                foreach (MatchList.MatchData data in plugin.advertised.Values) __result[data.Address] = data;
            }
            catch (Exception e) { plugin.Logger.LogWarning("Server list update failed: " + e.Message); }
        }

        static bool JoinAdvertisedMatch()
        {
            // Let the game open its waiting modal; ConnectAdvertisedMatch takes over from there.
            return true;
        }

        static bool ConnectAdvertisedMatch(MatchList.MatchData __0)
        {
            MatchList.MatchData match = __0;
            if (match.FacilitatorGUID == null || !match.FacilitatorGUID.StartsWith(MatchTag, StringComparison.Ordinal))
            {
                // 0.3.40: a public Steam lobby whose host runs the bridge and allows strangers: join through the bridge, friend or not.
                SteamBridgePlugin lobbyPlugin = discoveryInstance; ulong lobbyHost;
                if (lobbyPlugin != null) lobbyPlugin.Logger.LogInfo("STEAMJOIN: game connect request, match name='" + match.MatchName + "' guid='" + match.FacilitatorGUID + "'; " + SteamJoinLobbyDiag(match.LobbySteamID));
                ModInventory.CheckLobby(match.LobbySteamID, "public-or-invite");   // 0.4.14 MOD-INV: log only, never changes the join
                if (lobbyPlugin != null && TryLobbyHost(match, out lobbyHost)) { lobbyPlugin.Logger.LogInfo("STEAMJOIN: lobby carries bridge host data -> joining through the bridge"); return lobbyPlugin.JoinLobbyHost(lobbyHost); }
                // 0.3.67: a Steam "Join Game"/invite passes only the lobby id. A friend host whose lobby has no bridge data (strangers not allowed) but who advertises the bridge in rich presence used to
                // fall through to the game's own direct connect (a loopback address nobody listens on). Join that friend through the bridge instead.
                try
                {
                    if (lobbyPlugin != null && SteamManager.Initialized && match.LobbySteamID.m_SteamID != 0)
                    {
                        CSteamID owner = SteamMatchmaking.GetLobbyOwner(match.LobbySteamID);
                        if (owner.m_SteamID != 0 && owner != SteamUser.GetSteamID() && lobbyPlugin.IsFriend(owner) && LooksLikeSteamId(owner.m_SteamID)
                            && !String.IsNullOrEmpty(SteamFriends.GetFriendRichPresence(owner, PresenceKey)))
                        {
                            lobbyPlugin.Logger.LogInfo("STEAMJOIN: friend host " + owner.m_SteamID + " advertises the bridge in presence -> joining through the bridge");
                            return lobbyPlugin.JoinLobbyHost(owner.m_SteamID);
                        }
                    }
                }
                catch (Exception e) { if (lobbyPlugin != null) lobbyPlugin.Logger.LogWarning("STEAMJOIN: fallback failed: " + e.Message); }
                if (lobbyPlugin != null) lobbyPlugin.Logger.LogInfo("STEAMJOIN: no bridge route for this lobby -> the game's own direct connect continues");
                return true;
            }
            SteamBridgePlugin plugin = discoveryInstance;
            if (plugin == null) return false;
            plugin.visible = true;
            // Keep the waiting modal open: closing it clears the menu's join-success
            // subscription, and the lobby then draws over whatever menu is active.
            plugin.modalJoin = true;
            ulong id;
            if (!UInt64.TryParse(match.FacilitatorGUID.Substring(MatchTag.Length), out id)) { plugin.FailModalJoin(); return false; }
            CSteamID friend = new CSteamID(id);
            if (!plugin.advertised.ContainsKey(friend))
            { plugin.status = "Лобі вже недоступне. Онови список серверів."; plugin.FailModalJoin(); return false; }
            if (plugin.bridge != null || plugin.hosting)
            { plugin.status = "Міст уже працює. Зупини його через F8 і спробуй знову."; plugin.FailModalJoin(); return false; }
            plugin.peer = friend;
            ModInventory.CheckLobby(match.LobbySteamID, "friend");   // 0.4.14 MOD-INV: log only (rich-presence joins usually see no lobby data: n/a)
            plugin.Logger.LogInfo("Joining advertised Steam host through game server list: " + friend);
            plugin.StartBridge(false);
            if (plugin.bridge == null) plugin.FailModalJoin();
            return false;
        }

        // 0.3.40: the host publishes its code in the lobby data so a modded player can join a public lobby without being a friend.
        void PublishLobbyHostData(CSteamID lobby, bool active)
        {
            try
            {
                if (publishedBridgeLobby.m_SteamID != 0 && publishedBridgeLobby != lobby) ClearLobbyHostData();
                if (!active || !AllowStrangersOn || lobby.m_SteamID == 0 || SteamMatchmaking.GetLobbyOwner(lobby) != SteamUser.GetSteamID()) { ClearLobbyHostData(); return; }
                publishedBridgeLobby = lobby;
                string id = SteamUser.GetSteamID().m_SteamID.ToString();
                if (SteamMatchmaking.GetLobbyData(lobby, "csb_host") != id) SteamMatchmaking.SetLobbyData(lobby, "csb_host", id);
                if (SteamMatchmaking.GetLobbyData(lobby, "csb_ver") != BridgeVersion.Text) SteamMatchmaking.SetLobbyData(lobby, "csb_ver", BridgeVersion.Text);
                string mods = ModInventory.Hash;   // 0.4.14 MOD-INV: a guest logs MOD-MISMATCH when its own MODS-HASH differs (log only)
                if (mods.Length > 0 && SteamMatchmaking.GetLobbyData(lobby, "csb_mods") != mods) SteamMatchmaking.SetLobbyData(lobby, "csb_mods", mods);
                SteamMatchmaking.SetLobbyData(lobby, "csb_seen", SteamUtils.GetServerRealTime().ToString(System.Globalization.CultureInfo.InvariantCulture));
                // The game itself leaves name/version/address empty while a lobby is open, and its list hides entries without a name.
                // Fill them in so the lobby is shown; the address is a loopback marker that is never contacted (the join is intercepted).
                if (String.IsNullOrEmpty(SteamMatchmaking.GetLobbyData(lobby, "name"))) SteamMatchmaking.SetLobbyData(lobby, "name", "[Мод] " + SteamFriends.GetPersonaName());
                if (String.IsNullOrEmpty(SteamMatchmaking.GetLobbyData(lobby, "version"))) SteamMatchmaking.SetLobbyData(lobby, "version", Neo.SemanticVersion);
                if (String.IsNullOrEmpty(SteamMatchmaking.GetLobbyData(lobby, "guid"))) SteamMatchmaking.SetLobbyData(lobby, "guid", "csbl-" + id);
                if (String.IsNullOrEmpty(SteamMatchmaking.GetLobbyData(lobby, "hostInternalIP")))
                {
                    ulong n = SteamUser.GetSteamID().m_SteamID;
                    SteamMatchmaking.SetLobbyData(lobby, "hostInternalIP", "127.253." + (n % 251 + 1) + "." + ((n / 251) % 251 + 1));
                }
            }
            catch (Exception e) { Logger.LogWarning("Lobby host data not published: " + e.Message); }
        }

        void ClearLobbyHostData()
        {
            try
            {
                if (!SteamManager.Initialized || publishedBridgeLobby.m_SteamID == 0) return;
                if (SteamMatchmaking.GetLobbyOwner(publishedBridgeLobby) == SteamUser.GetSteamID())
                {
                    SteamMatchmaking.SetLobbyData(publishedBridgeLobby, "csb_host", "");
                    SteamMatchmaking.SetLobbyData(publishedBridgeLobby, "csb_ver", "");
                    SteamMatchmaking.SetLobbyData(publishedBridgeLobby, "csb_seen", "");
                    SteamMatchmaking.SetLobbyData(publishedBridgeLobby, "csb_mods", "");
                }
                publishedBridgeLobby = new CSteamID(0);
            }
            catch (Exception e) { Logger.LogWarning("Lobby host data not cleared: " + e.Message); }
        }

        static bool TryLobbyHost(MatchList.MatchData match, out ulong hostId)
        {
            hostId = 0;
            try
            {
                if (!SteamManager.Initialized || match.LobbySteamID.m_SteamID == 0) return false;
                string ver = SteamMatchmaking.GetLobbyData(match.LobbySteamID, "csb_ver");
                // Any bridge host since the public-lobby feature (0.3.40) is accepted; an exact-version match would break joins to hosts on older versions.
                if (String.IsNullOrEmpty(ver) || !(ver == BridgeVersion.Text || UpdateVerifier.IsNewer(ver, "0.3.39"))) return false;
                // Newer hosts publish a heartbeat (csb_seen) and clear the data on stop; older hosts never write it, so freshness is checked only when it exists.
                string seenText = SteamMatchmaking.GetLobbyData(match.LobbySteamID, "csb_seen");
                bool freshHeartbeat = false;
                if (!String.IsNullOrEmpty(seenText))
                {
                    long seen;
                    if (!Int64.TryParse(seenText, out seen)) return false;
                    long age = (long)SteamUtils.GetServerRealTime() - seen;
                    if (seen <= 0 || age < 0 || age > 20) return false;
                    freshHeartbeat = true;
                }
                string value = SteamMatchmaking.GetLobbyData(match.LobbySteamID, "csb_host");
                if (String.IsNullOrEmpty(value) || !UInt64.TryParse(value, out hostId) || !LooksLikeSteamId(hostId)) return false;
                if (SteamMatchmaking.GetNumLobbyMembers(match.LobbySteamID) <= 0) return false;
                ulong ownerId = SteamMatchmaking.GetLobbyOwner(match.LobbySteamID).m_SteamID;
                // 0.4.5: a lobby read from the server list usually has NO owner yet (owner=0 in the log), so a click on the list entry ended in the game's own
                // direct connect to a fake address ("Неможливо під'єднатися до сервера"). With a fresh heartbeat and a Steam FRIEND as host the owner is not needed:
                // joining a friend is what the player wants, and a stranger still has to be the proven owner (LinkRules.LobbyHostAccepted, unit-tested).
                bool friend = false;
                if (ownerId == 0 && freshHeartbeat)
                {
                    SteamBridgePlugin pl = discoveryInstance;
                    friend = pl != null && pl.IsFriend(new CSteamID(hostId));
                    if (pl != null) pl.Logger.LogInfo("STEAMJOIN: lobby owner unknown (owner=0), fresh heartbeat, host " + hostId + (friend ? " is a friend -> accepted" : " is not a friend -> refused"));
                }
                if (LinkRules.LobbyHostAccepted(ownerId, hostId, freshHeartbeat, friend)) return true;
                hostId = 0;
                return false;
            }
            catch (Exception) { return false; }
        }

        // Returns false so the game's own (direct IP) connect is skipped.
        bool JoinLobbyHost(ulong hostId)
        {
            visible = true;
            modalJoin = true;
            if (hostId == SteamUser.GetSteamID().m_SteamID) { FailModalJoin(); return false; }
            if (bridge != null || hosting) { status = "Міст уже працює. Зупини його через F8 і спробуй знову."; FailModalJoin(); return false; }
            peer = new CSteamID(hostId); peerByCode = !IsFriend(peer);
            Logger.LogInfo("Joining a public lobby through the bridge; host " + hostId + (peerByCode ? " (not a friend)" : " (friend)"));
            StartBridge(false);
            if (bridge == null) FailModalJoin();
            return false;
        }
        void FailModalJoin()
        {
            if (!modalJoin) return;
            modalJoin = false;
            // Lets the waiting modal close itself and show the game's join error.
            try { GameSignals.Invoke(UNetManager.ClientConnectFailedSelf); }
            catch (Exception e) { Logger.LogWarning("Could not close join window: " + e.Message); }
        }

        static void AbortModalJoin()
        {
            SteamBridgePlugin plugin = discoveryInstance;
            if (plugin == null) return;
            plugin.modalJoin = false;
            if (plugin.bridge != null && !plugin.host)
            { plugin.StopBridge(); plugin.status = "Приєднання скасовано."; }
        }

        static void BeforeJoinSuccess()
        {
            SteamBridgePlugin plugin = discoveryInstance;
            if (plugin != null) plugin.RearmJoinMenus();
        }

        // 0.4.19 JOIN-MENUS: the 0.3.8 version found no menu on any live join ("re-armed 0 of 0" in every log up to 0.4.17: FindObjectsOfType skips inactive objects).
        // Now: every LegacyMultiplayerMenu of the scene (Resources.FindObjectsOfTypeAll, scene objects only); a live one gets its private Disconnect() + Connect(), an inactive one is
        // connected for this one signal and disconnected again in AfterJoinSuccess (so its own OnEnable later does not subscribe it twice). JoinMenuRules decides; tag JOIN-MENUS.
        static MethodInfo lmmConnect, lmmDisconnect; static bool lmmLooked; static int joinSuccessCount;
        static readonly List<LegacyMultiplayerMenu> joinTempMenus = new List<LegacyMultiplayerMenu>();

        void RearmJoinMenus()
        {
            try
            {
                if (!lmmLooked)
                {
                    lmmLooked = true;
                    lmmConnect = AccessTools.Method(typeof(LegacyMultiplayerMenu), "Connect");
                    lmmDisconnect = AccessTools.Method(typeof(LegacyMultiplayerMenu), "Disconnect");
                }
                bool reflect = lmmConnect != null && lmmDisconnect != null;
                joinSuccessCount++;
                joinTempMenus.Clear();
                int found = 0, live = 0, reconnected = 0, temp = 0, failed = 0;
                foreach (LegacyMultiplayerMenu menu in Resources.FindObjectsOfTypeAll<LegacyMultiplayerMenu>())
                {
                    if (menu == null || !menu.gameObject.scene.IsValid()) continue;   // prefabs / assets are not menus on screen
                    found++;
                    bool isLive = menu.isActiveAndEnabled; if (isLive) live++;
                    try
                    {
                        switch (JoinMenuRules.Step(isLive, reflect))
                        {
                            case JoinMenuStep.Reconnect: lmmDisconnect.Invoke(menu, null); lmmConnect.Invoke(menu, null); reconnected++; break;
                            case JoinMenuStep.Toggle: menu.enabled = false; menu.enabled = true; reconnected++; break;
                            case JoinMenuStep.TempConnect: lmmDisconnect.Invoke(menu, null); lmmConnect.Invoke(menu, null); joinTempMenus.Add(menu); temp++; break;
                        }
                    }
                    catch (Exception) { failed++; }
                }
                string line = "Join success #" + joinSuccessCount + ": multiplayer menus found " + found + " (live " + live + "), reconnected " + reconnected + ", connected for this join only " + temp
                    + (failed > 0 ? ", failed " + failed : "") + (reflect ? "" : ", private Connect/Disconnect NOT found (0.3.8 toggle)") + " - so the lobby replaces the server list.";
                Logger.LogInfo(line); PtLog("JOIN-MENUS " + line);
                if (JoinMenuRules.WantDiag(joinSuccessCount, reconnected + temp)) { string d = JoinMenuDiag(); Logger.LogInfo("Join menus on screen: " + d); PtLog("JOIN-MENUS screen: " + d); }
            }
            catch (Exception e) { Logger.LogWarning("Could not re-arm the join menus: " + e.Message); }
        }

        static void AfterJoinSuccess()
        {
            try
            {
                if (joinTempMenus.Count == 0 || lmmConnect == null || lmmDisconnect == null) return;
                foreach (LegacyMultiplayerMenu menu in joinTempMenus)
                {
                    if (menu == null) continue;
                    try { lmmDisconnect.Invoke(menu, null); if (JoinMenuRules.ReconnectAfter(menu.isActiveAndEnabled)) lmmConnect.Invoke(menu, null); } catch (Exception) { }
                }
                joinTempMenus.Clear();
            }
            catch (Exception) { }
        }

        // what the screen really holds at a join success: legacy menus of the multiplayer/lobby family and scene objects named like a server list or lobby (max 16 entries)
        static string JoinMenuDiag()
        {
            List<string> parts = new List<string>();
            try
            {
                foreach (LegacyBaseMenu m in Resources.FindObjectsOfTypeAll<LegacyBaseMenu>())
                {
                    if (m == null || !m.gameObject.scene.IsValid()) continue;
                    string cls = m.GetType().Name;
                    if (cls.IndexOf("Multiplayer", StringComparison.Ordinal) < 0 && cls.IndexOf("Lobby", StringComparison.Ordinal) < 0 && cls.IndexOf("Join", StringComparison.Ordinal) < 0) continue;
                    if (parts.Count >= 16) break;
                    parts.Add(cls + "'" + m.gameObject.name + "' " + (m.gameObject.activeInHierarchy ? "active" : "inactive") + (m.enabled ? "" : "/disabled"));
                }
                foreach (GameObject g in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (parts.Count >= 16) break;
                    if (g == null || !g.scene.IsValid() || !g.activeInHierarchy) continue;
                    string n = g.name;
                    if (n.IndexOf("Server", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (g.transform.parent != null && g.transform.parent.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    parts.Add("GO'" + n + "' active");
                }
            }
            catch (Exception e) { parts.Add("diag failed: " + e.GetType().Name); }
            return parts.Count == 0 ? "none" : string.Join("; ", parts.ToArray());
        }
        static void BeforeMapStart(UNetManager __instance)
        {
            SteamBridgePlugin plugin = discoveryInstance;
            if (plugin == null || __instance.LobbyPlayerList == null || __instance.LobbyPlayerList.Count == 0) return;
            foreach (var player in __instance.LobbyPlayerList) if (!player.readyToBegin) return;
            plugin.gameStarting = true;
            if (SteamManager.Initialized) plugin.Publish(null);
        }

        void ShutdownDiscovery()
        {
            ClearLobbyHostData();
            if (SteamManager.Initialized) SteamFriends.SetRichPresence(PresenceKey, null);
            if (discoveryHarmony != null) discoveryHarmony.UnpatchSelf();
            if (discoveryInstance == this) discoveryInstance = null;
        }
    }
}
