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
        readonly Dictionary<CSteamID, MatchList.MatchData> advertised = new Dictionary<CSteamID, MatchList.MatchData>();

        void Awake()
        {
            BindConfig();
            CleanupUpdateLeftovers();
            NetworkCompat.Apply(Logger);
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
                if (joinSuccess != null) discoveryHarmony.Patch(joinSuccess, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeJoinSuccess", BindingFlags.Static | BindingFlags.NonPublic)));
                else Logger.LogWarning("Join success hook not found; after a second join the lobby may draw over the server list.");
                MethodInfo abort = typeof(NetworkWaitingActionsMenu).GetMethod("OnAbort", BindingFlags.Static | BindingFlags.NonPublic);
                if (abort != null) discoveryHarmony.Patch(abort, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AbortModalJoin", BindingFlags.Static | BindingFlags.NonPublic)));
                else Logger.LogWarning("Join abort hook not found; cancel the Steam join with F8.");
                discoveryReady = true;
                Logger.LogInfo("Server list integration ready. Hosts advertised to Steam friends only.");
                InitLoadGuard();
                InitLobbyPing();
                InstallDiscoveryDiag();
                InstallTimeoutPolicy(); InstallExtrapolation(); InstallPositionLink(); InstallHitchProfiler(); InstallItemFilter(); InstallPeriodicStats(); InstallReconnect(); InstallFreezeWatch(); InstallRunInBackground(); InstallDiagBundle(); InstallRadmin();
                PatchCrateGuard();
                PatchFixes();
                InitGameplayFixes();
            }
            catch (Exception e)
            {
                if (discoveryHarmony != null) discoveryHarmony.UnpatchSelf();
                Logger.LogError("Server discovery unavailable; manual F8 connection remains available: " + e);
            }
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
                if (lobbyPlugin != null && TryLobbyHost(match, out lobbyHost)) return lobbyPlugin.JoinLobbyHost(lobbyHost);
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
                if (!active || !AllowStrangersOn || lobby.m_SteamID == 0 || SteamMatchmaking.GetLobbyOwner(lobby) != SteamUser.GetSteamID()) return;
                string id = SteamUser.GetSteamID().m_SteamID.ToString();
                if (SteamMatchmaking.GetLobbyData(lobby, "csb_host") != id) SteamMatchmaking.SetLobbyData(lobby, "csb_host", id);
                if (SteamMatchmaking.GetLobbyData(lobby, "csb_ver") != BridgeVersion.Text) SteamMatchmaking.SetLobbyData(lobby, "csb_ver", BridgeVersion.Text);
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

        static bool TryLobbyHost(MatchList.MatchData match, out ulong hostId)
        {
            hostId = 0;
            try
            {
                if (!SteamManager.Initialized || match.LobbySteamID.m_SteamID == 0) return false;
                string value = SteamMatchmaking.GetLobbyData(match.LobbySteamID, "csb_host");
                return !String.IsNullOrEmpty(value) && UInt64.TryParse(value, out hostId) && LooksLikeSteamId(hostId);
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

        void RearmJoinMenus()
        {
            try
            {
                LegacyMultiplayerMenu[] menus = UnityEngine.Object.FindObjectsOfType<LegacyMultiplayerMenu>();
                int rearmed = 0;
                foreach (LegacyMultiplayerMenu menu in menus)
                {
                    if (menu == null || !menu.enabled || !menu.gameObject.activeInHierarchy) continue;
                    // OnDisable/OnEnable of the menu drop and re-create its join/host signal subscriptions.
                    menu.enabled = false; menu.enabled = true; rearmed++;
                }
                Logger.LogInfo("Join success: re-armed " + rearmed + " of " + menus.Length + " multiplayer menu(s) so the lobby replaces the server list.");
            }
            catch (Exception e) { Logger.LogWarning("Could not re-arm the join menus: " + e.Message); }
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
            if (SteamManager.Initialized) SteamFriends.SetRichPresence(PresenceKey, null);
            if (discoveryHarmony != null) discoveryHarmony.UnpatchSelf();
            if (discoveryInstance == this) discoveryInstance = null;
        }
    }
}
