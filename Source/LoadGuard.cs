using System;
using System.Collections.Generic;
using System.Text;
using Brix.Engine;
using Brix.External.Signals;
using Brix.Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Networking.Types;
using UnityEngine.SceneManagement;

namespace CastleStorySteam
{
    // 0.3.8: why does a saved game "load forever"?
    // Source facts (GameLoader): the HOST keeps the loading screen up ("waiting for players") until every entry of
    // UNetManager.UserList has LevelReady = true; a client waits for the host. A player whose connection died without a
    // goodbye (the bridge was stopped, the PC crashed, a relay hiccup) stays in UserList until the UNet disconnect timeout,
    // which the 10 minute patcher raised to 600 s. So one dead guest = a host loading screen that never ends.
    // This file (1) logs the loading steps and WHO the host waits for, (2) disconnects a guest whose bridge is gone
    // (its UNet connection can never recover), and (3) after a long wait, disconnects a not-ready guest that has gone silent.
    public sealed partial class SteamBridgePlugin
    {
        readonly List<SignalConnection> loadSignals = new List<SignalConnection>();
        bool loadActive, loadTrackerReady;
        int loadMax, loadProgress;
        float loadStartedAt, lastLoadProgressAt, nextLoadLog, nextGuard, hostWaitSince;
        readonly Dictionary<int, float> deadPorts = new Dictionary<int, float>();
        readonly Dictionary<int, float> notReadyKickAt = new Dictionary<int, float>();
        readonly Dictionary<int, float> duplicateSeen = new Dictionary<int, float>();
        // A not-ready guest is only removed after the host itself finished loading, waited this long, and the guest was silent this long.
        const float HostWaitBeforeKick = 45f, SilentBeforeKick = 30f, GhostGrace = 3f;

        void InitLoadGuard()
        {
            try
            {
                SceneManager.sceneLoaded += OnGuardSceneLoaded;
                ConnectLoadSignals();
                loadTrackerReady = true;
            }
            catch (Exception e) { Logger.LogWarning("Load tracker unavailable: " + e.Message); }
        }

        void ShutdownLoadGuard()
        {
            try { SceneManager.sceneLoaded -= OnGuardSceneLoaded; } catch (Exception) { }
            DisconnectLoadSignals();
        }

        void DisconnectLoadSignals()
        {
            foreach (SignalConnection c in loadSignals) { try { c.Disconnect(); } catch (Exception) { } }
            loadSignals.Clear();
        }

        // Signals can be reset between scenes; reconnect on every scene so the log never goes blind.
        void OnGuardSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                ResetFixCaches();
                Logger.LogInfo("Scene loaded: " + scene.name + " (index " + scene.buildIndex + ")" + GameStateNote());
                DisconnectLoadSignals();
                ConnectLoadSignals();
            }
            catch (Exception e) { Logger.LogWarning("Load tracker reconnect failed: " + e.Message); }
        }

        void ConnectLoadSignals()
        {
            loadSignals.Add(GameSignals.Connect(GameSignals.MaxLoadingProgress, OnLoadMax));
            loadSignals.Add(GameSignals.Connect(GameSignals.LoadingProgress, OnLoadStep));
            loadSignals.Add(GameSignals.Connect(GameSignals.LoadingProgressLeap, OnLoadLeap));
            loadSignals.Add(GameSignals.Connect(GameSignals.LevelReady, OnLoadLevelReady));
            loadSignals.Add(GameSignals.Connect(GameSignals.StartError, OnLoadError));
            loadSignals.Add(GameSignals.Connect(UNetManager.ServerPlayersReady, OnLoadPlayersReady));
            loadSignals.Add(GameSignals.Connect(UNetManager.Disconnected, OnLoadDisconnected));
        }

        void OnLoadMax(int max)
        {
            loadActive = true; loadMax = max; loadProgress = 0;
            loadStartedAt = lastLoadProgressAt = Time.realtimeSinceStartup; hostWaitSince = 0; nextLoadLog = loadStartedAt + 10;
            Logger.LogInfo("Loading started: " + max + " steps" + GameStateNote());
        }
        void OnLoadStep() { loadProgress++; lastLoadProgressAt = Time.realtimeSinceStartup; }
        void OnLoadLeap(int n) { loadProgress += n; lastLoadProgressAt = Time.realtimeSinceStartup; }
        void OnLoadLevelReady()
        {
            Logger.LogInfo("Level ready after " + (loadActive ? (Time.realtimeSinceStartup - loadStartedAt).ToString("F1") + " s" : "?") + GameStateNote());
            loadActive = false; notReadyKickAt.Clear();
        }
        void OnLoadError(string error) { Logger.LogWarning("Game start error: " + error + GameStateNote()); }
        void OnLoadPlayersReady() { Logger.LogInfo("All lobby players ready; the game changes scene" + GameStateNote()); }
        void OnLoadDisconnected() { Logger.LogInfo("Game client disconnected" + GameStateNote()); loadActive = false; }

        string GameStateNote()
        {
            try
            {
                UNetManager m = UNetManager.instance;
                return " [server=" + NetworkServer.active + " client=" + NetworkClient.active + " users=" + (m != null && m.UserList != null ? m.UserList.Count : -1)
                    + " lobby=" + (m != null && m.LobbyPlayerList != null ? m.LobbyPlayerList.Count : -1) + "]";
            }
            catch (Exception) { return ""; }
        }

        // Called every frame from Update (cheap when not loading).
        void TrackLoad()
        {
            if (!loadTrackerReady || !loadActive) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextLoadLog) return;
            nextLoadLog = now + 10;
            StringBuilder sb = new StringBuilder();
            sb.Append("Loading: step ").Append(loadProgress).Append("/").Append(loadMax).Append("; ").Append((now - loadStartedAt).ToString("F0")).Append(" s since start, ")
              .Append((now - lastLoadProgressAt).ToString("F0")).Append(" s since the last step").Append(GameStateNote());
            if (NetworkServer.active) sb.Append("; waiting for: ").Append(DescribeUsers());
            if (now - lastLoadProgressAt >= 20) Logger.LogWarning(sb.ToString()); else Logger.LogInfo(sb.ToString());
        }

        string DescribeUsers()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                UNetManager m = UNetManager.instance;
                if (m == null || m.UserList == null) return "(no user list)";
                foreach (Brix.Game.User u in m.UserList)
                {
                    if (u == null) continue;
                    int port; BridgeCore guest = FindGuestOfUser(u, out port);
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(u.ScreenName).Append(u.LevelReady ? "=ready" : "=NOT ready")
                      .Append(" (conn port ").Append(port).Append(guest != null ? ", data " + ReceiveAge(guest).ToString("F1") + " s ago" : ", no bridge").Append(")");
                }
            }
            catch (Exception e) { sb.Append("(user list failed: " + e.Message + ")"); }
            return sb.Length == 0 ? "(nobody)" : sb.ToString();
        }

        // The game sees each bridged guest as 127.0.0.1:<port of that guest's bridge socket>.
        static int ConnectionPort(NetworkConnection c)
        {
            if (c == null || c.hostId < 0) return -1;
            string address; int port; NetworkID network; NodeID node; byte error;
            NetworkTransport.GetConnectionInfo(c.hostId, c.connectionId, out address, out port, out network, out node, out error);
            return error == 0 ? port : -1;
        }

        BridgeCore FindGuestOfUser(Brix.Game.User u, out int port)
        {
            port = -1;
            try
            {
                port = ConnectionPort(u.connectionToClient);
                if (port < 0) return null;
                foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (g.Value.Port == port) return g.Value;
            }
            catch (Exception) { }
            return null;
        }

        // Remember the port of a guest bridge that has been removed: its UNet connection can never come back.
        void NoteDeadGuest(BridgeCore b)
        {
            try { deadPorts[b.Port] = Time.realtimeSinceStartup; } catch (Exception) { }
        }

        // Called about once a second from UpdateGuests (host only).
        void GuardHostConnections()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextGuard) return;
            nextGuard = now + 1;
            try
            {
                UNetManager m = UNetManager.instance;
                if (m == null || !NetworkServer.active) return;
                // (2) connections that belonged to a bridge that is gone
                if (deadPorts.Count > 0)
                {
                    foreach (NetworkConnection c in new List<NetworkConnection>(NetworkServer.connections))
                    {
                        int port = ConnectionPort(c);
                        float since;
                        if (port < 0 || !deadPorts.TryGetValue(port, out since) || now - since < GhostGrace) continue;
                        Logger.LogInfo("Disconnecting the game connection of a guest whose bridge is gone (port " + port + ")");
                        deadPorts.Remove(port);
                        m.SafeDisconnectConnection(c);
                    }
                    // forget very old entries
                    List<int> old = new List<int>();
                    foreach (KeyValuePair<int, float> d in deadPorts) if (now - d.Value > 600) old.Add(d.Key);
                    foreach (int p in old) deadPorts.Remove(p);
                }
                // (2b) one guest bridge = one player = one game connection. A second connection from the same bridge port is the
                // leftover of an abandoned join attempt (the "clone" that fills a lobby slot): keep the newest, drop the older ones.
                Dictionary<int, List<NetworkConnection>> byPort = new Dictionary<int, List<NetworkConnection>>();
                foreach (NetworkConnection c in NetworkServer.connections)
                {
                    int port = ConnectionPort(c);
                    if (port < 0) continue;
                    List<NetworkConnection> list;
                    if (!byPort.TryGetValue(port, out list)) { list = new List<NetworkConnection>(); byPort[port] = list; }
                    list.Add(c);
                }
                foreach (KeyValuePair<int, List<NetworkConnection>> pair in byPort)
                {
                    if (pair.Value.Count < 2) continue;
                    pair.Value.Sort(delegate(NetworkConnection a, NetworkConnection b) { return a.connectionId.CompareTo(b.connectionId); });
                    for (int i = 0; i < pair.Value.Count - 1; i++)
                    {
                        NetworkConnection old = pair.Value[i];
                        float first;
                        if (!duplicateSeen.TryGetValue(old.connectionId, out first)) { duplicateSeen[old.connectionId] = now; continue; }
                        if (now - first < GhostGrace) continue;
                        Logger.LogInfo("Dropping a duplicate game connection #" + old.connectionId + " of bridge port " + pair.Key + " (kept #" + pair.Value[pair.Value.Count - 1].connectionId + ")");
                        duplicateSeen.Remove(old.connectionId);
                        m.SafeDisconnectConnection(old);
                    }
                }
                if (duplicateSeen.Count > 0)
                {
                    List<int> stale = new List<int>();
                    foreach (int cid in duplicateSeen.Keys) { bool alive = false; foreach (NetworkConnection c in NetworkServer.connections) if (c != null && c.connectionId == cid) { alive = true; break; } if (!alive) stale.Add(cid); }
                    foreach (int cid in stale) duplicateSeen.Remove(cid);
                }
                // (3) the host waits in the loading screen for a not-ready guest that has gone silent
                if (!loadActive || m.UserList == null) return;
                bool hostDone = GameParam.instance != null && GameParam.instance.NetworkHostLoaded;
                if (!hostDone) { hostWaitSince = 0; return; }
                if (hostWaitSince == 0) hostWaitSince = now;
                if (now - hostWaitSince < HostWaitBeforeKick) return;
                foreach (Brix.Game.User u in new List<Brix.Game.User>(m.UserList))
                {
                    if (u == null || u.LevelReady) continue;
                    int port; BridgeCore guest = FindGuestOfUser(u, out port);
                    bool silent = guest == null || ReceiveAge(guest) >= SilentBeforeKick;
                    if (!silent) continue;
                    Logger.LogWarning("Host finished loading " + (now - hostWaitSince).ToString("F0") + " s ago; removing not-ready guest " + u.ScreenName
                        + " (conn port " + port + ", " + (guest == null ? "no bridge" : "silent " + ReceiveAge(guest).ToString("F0") + " s") + ")");
                    if (u.connectionToClient != null) m.SafeDisconnectConnection(u.connectionToClient);
                }
            }
            catch (Exception e) { Logger.LogWarning("Host connection guard failed: " + e.Message); }
        }
    }
}
