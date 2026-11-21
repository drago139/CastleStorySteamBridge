// 0.4.10 IN-GAME TEST BOT game layer: taken from the read-only research draft bot-research\menu-BotMenuCalls.cs (side-session-notes of local_a8d26072, 2026-11-06),
// compiled with the plugin (signatures checked by csc against the real game DLLs). Called only by the bot (Bot.cs / BotActions.cs) while it is armed.
// menu-BotMenuCalls.cs - research scratch for the in-game test bot, area 'menu' (2026-11-06).
// NOT compiled (the research brief allows only reading + the decompiler). Every member used below was checked with
// ilspycmd against the REAL DLLs in D:\SteamLibrary\steamapps\common\Castle Story\Castle Story_Data\Managed
// (decompiles: bot-research\menu-*.cs). C# 5 only. Call everything from the Unity main thread (plugin Update tick).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Brix.Assets;
using Brix.Lua;
using Brix.Network;
using Brix.UI.Builder.Menu;
using Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStorySteam
{
    internal static class BotMenu
    {
        public const string MenuScene = "SceneMenu";       // build index 1 = lobbyScene = offlineScene (level3 bytes: "SceneMenu ... ScenePrincipale")
        public const string GameScene = "ScenePrincipale"; // build index 2 = playScene

        // ---------------- assets ----------------
        // Map by folder name ("Test Catapults"), customName, rawDisplayName or exact codename. path = full path with '/' and a trailing '/'.
        public static Asset_Map FindMap(string key, AssetPlayableContext ctx)
        {
            foreach (Asset_Map m in AssetDispensary.maps.SendQuery(AssetQueryParameters.AllSources | AssetQueryParameters.RefreshCache, ctx))
            {
                if (m == null || m.IsShell()) continue;
                string p = (m.path ?? "").TrimEnd('/');
                string folder = p.Substring(p.LastIndexOf('/') + 1);
                if (folder == key || m.customName == key || m.rawDisplayName == key || m.codename == key) return m;
            }
            return null;
        }

        // Saved game for "continue the match" (MpLoadGameLibraryMenu uses AssetDispensary.saves + MenuTypeContext.Existing).
        public static Asset_Map FindSave(string key)
        {
            foreach (Asset_Map m in AssetDispensary.saves.SendQuery(AssetQueryParameters.AllSources | AssetQueryParameters.RefreshCache, AssetPlayableContext.Multiplayer))
                if (m != null && !m.IsShell() && (m.customName == key || m.codename == key)) return m;
            return null;
        }

        public static Asset_Gamemode FindMode(string codename, AssetPlayableContext ctx)
        {
            return AssetDispensary.gamemodes.SendCodenameQuery(codename, AssetQueryParameters.AllSources, ctx);
        }

        // presetCodename null = the gamemode default for this context (SP sandbox: sandbox_default_31f05863-..., MP conquest: conquest_versus_f283061f-...).
        public static Asset_GamemodePreset FindPreset(Asset_Gamemode mode, string presetCodename, AssetPlayableContext ctx)
        {
            if (mode == null) return null;
            if (!string.IsNullOrEmpty(presetCodename)) return AssetDispensary.presets.SendCodenameQuery(presetCodename, mode.codename, AssetQueryParameters.AllSources, ctx);
            return mode.GetDefaultPreset(AssetQueryParameters.AllSources, ctx);
        }

        // What the map library + starter menus end in (BaseMapStarterMenu.OnOk / LobbyMenu.CloseMapLibrary -> TrySetSelectedAsLoaded).
        // Always set the preset explicitly: GetGamemodeDefaultPreset returns the map's LAST PLAYED preset even if it belongs to another mode.
        public static void SelectAndLoad(Asset_Map map, Asset_Gamemode mode, Asset_GamemodePreset preset, AssetPlayableContext ctx, bool existingSave)
        {
            BaseMapLibraryMenu.SetSelectedMap(map, ctx, BaseAssetMenu.MenuGameContext.Normal, existingSave ? BaseAssetMenu.MenuTypeContext.Existing : BaseAssetMenu.MenuTypeContext.New);
            if (!existingSave)
            {
                BaseMapLibraryMenu.SetSelectedGamemode(map, mode, ctx);
                if (preset != null) BaseMapLibraryMenu.SetSelectedPreset(preset);
            }
            BaseMapLibraryMenu.SetSelectedAsLoaded();   // GameParam.Map/Gamemode/Preset; on a host the Preset setter pushes GameParam.Parameters (SyncVar) to guests
        }

        // ---------------- menus (built from scene MenuComponents) ----------------
        public static IMenu FindMenu<T>() where T : class, IMenu
        {
            foreach (Brix.UI.Builder.Menu.Component.MenuComponent mc in Resources.FindObjectsOfTypeAll<Brix.UI.Builder.Menu.Component.MenuComponent>())
            {
                if (mc == null || !mc.gameObject.scene.IsValid()) continue;
                IMenu m = mc.Menu;
                if (m is T) return m;
            }
            return null;
        }
        public static bool MenuLoaded<T>() where T : class, IMenu { IMenu m = FindMenu<T>(); return m != null && m.Loaded; }
        public static bool MenuOpened<T>() where T : class, IMenu { IMenu m = FindMenu<T>(); return m != null && m.Opened; }

        // ---------------- states ----------------
        public enum Phase { Unknown, Boot, MainMenu, Connecting, Lobby, Loading, InMatch, Leaving }
        public static Phase Current()
        {
            string s = SceneManager.GetActiveScene().name;
            UNetManager n = UNetManager.instance;
            if (s == GameScene)
            {
                if (Brix.Engine.GameSignals.Received(Brix.Engine.GameSignals.LeaveGame)) return Phase.Leaving;   // counter resets on every scene (GameSignals cleared)
                return Brix.Audio.SoundEngine.GameIsReady ? Phase.InMatch : Phase.Loading;                      // GameIsReady: last loader step, host AND guest
            }
            if (s == MenuScene && n != null)
            {
                bool net = NetworkServer.active || NetworkClient.active;
                if (net && n.LocalLobbyPlayer != null) return Phase.Lobby;
                if (net) return Phase.Connecting;
                return MenuLoaded<NetworkWaitingModalMenu>() ? Phase.MainMenu : Phase.Boot;
            }
            return Phase.Unknown;
        }

        public static string DescribeLobby()
        {
            UNetManager n = UNetManager.instance;
            if (n == null || n.LobbyPlayerList == null) return "no lobby";
            GameParam gp = GameParam.instance;
            StringBuilder sb = new StringBuilder("rows=" + n.LobbyPlayerList.Count);
            if (gp != null) sb.Append(" params=").Append(gp.Parameters.MapCodeName).Append('/').Append(gp.Parameters.ModeCodeName).Append('/').Append(gp.Parameters.PresetCodeName);
            sb.Append(" localMap=").Append(GameParam.Map != null ? GameParam.Map.codename : "null");
            foreach (LobbyPlayer p in n.LobbyPlayerList)
            {
                if (p == null) continue;
                sb.Append(" | ").Append(p.playerName).Append(p.isHost ? " host" : " guest")
                  .Append(" ready=").Append(p.isHost ? p.IsHostReady : p.readyToBegin)
                  .Append(" dl=").Append(p.downloadProgression).Append(" f=").Append(p.factionNumber).Append(" t=").Append(p.TeamNumber);
            }
            return sb.ToString();
        }

        // ---------------- (a) single player sandbox ----------------
        public static string StartSandboxSp(string mapKey, string presetCodename)
        {
            if (SceneManager.GetActiveScene().name != MenuScene || UNetManager.instance == null || GameParam.instance == null) return "not in main menu";
            if (NetworkServer.active || NetworkClient.active || Brix.Engine.Neo.multiplayer) return "network still active / Neo.multiplayer";
            Asset_Map map = FindMap(mapKey, AssetPlayableContext.Singleplayer);
            Asset_Gamemode mode = FindMode("sandbox", AssetPlayableContext.Singleplayer);
            Asset_GamemodePreset preset = FindPreset(mode, presetCodename, AssetPlayableContext.Singleplayer);
            if (map == null || mode == null || preset == null) return "map/mode/preset not found";
            if (!map.IsCurrentVersion()) return "map needs the update step";
            SelectAndLoad(map, mode, preset, AssetPlayableContext.Singleplayer, false);
            GameParam.StartGame(BaseMapLibraryMenu.EmptySelected);   // = SpNewGameStarterMenu 'Play' (BaseMapStarterMenu.OnOk, Single)
            return null;
        }

        // In-game debug tools (after the level is ready; Architecte.Start resets allowDebugMenu from Map.isDebug).
        public static string EnableDebugTools(bool instantBuild)
        {
            if (Brix.Engine.Architecte.instance == null) return "not in a match";
            if (!Brix.Engine.Architecte.allowDebugMenu) Brix.Engine.Architecte.allowDebugMenu = true;     // settings 'Allow debug menu' = Lua ToggleAllowDebugMenu
            if (instantBuild && !Brix.Engine.Architecte.editorMode)                                       // Debug sidebar 'Instant Build' = Lua ToggleEditorMode
            {
                Brix.Engine.Architecte.editorMode = true;
                Brix.Engine.GameSignals.Invoke(Brix.Engine.GameSignals.OnEditorModeChanged, true);
            }
            return null;
        }

        // Gamemode intro modal shown after loading (Run-TwoUserTest clicked it away). Same hook its button invokes.
        // Conquest SP: the button also calls Lua StartTimers() first; this does not.
        public static void CloseIntroModal() { LuaHookBase.InvokeFromCLR(UIGameObserver.lua_CloseModalMenu); }

        // ---------------- (b) host ----------------
        public static string HostCreateLobby()
        {
            UNetManager n = UNetManager.instance;
            if (SceneManager.GetActiveScene().name != MenuScene || n == null) return "not in main menu";
            if (NetworkServer.active || NetworkClient.active) return "network already active";
            if (NetworkWaitingModalMenu.WaitingToClose) return "waiting modal still closing";
            n.LocalPlayerName = string.IsNullOrEmpty(Brix.Engine.Neo.multiplayerName) ? "Player" : Brix.Engine.Neo.multiplayerName;
            if (MenuLoaded<NetworkWaitingModalMenu>())
            {
                NetworkWaitingModalMenu.OpenAsHostSignal.Invoke();   // = Play>Multiplayer>Create 'Apply' (LegacyMultiplayerMenu) / lobby 'Host' row (LegacyLobbyLeftMenu)
                return null;
            }
            // Fallback = NetworkWaitingModalMenu.ConnectAsHost without the modal. Afterwards, once n.LocalLobbyPlayer != null: LobbyMenu.OpenSignal.Invoke();
            n.maxConnections = UNetManager.HostingMaxConnections;
            return n.StartHostMultiplayer(UNetManager.HostingIP, UNetManager.HostingPort) == null ? "StartHostMultiplayer returned null" : "fallback-started";
        }

        public static bool HostLobbyUp()
        {
            UNetManager n = UNetManager.instance;
            return n != null && SceneManager.GetActiveScene().name == MenuScene && NetworkServer.active && Brix.Engine.Neo.multiplayer
                && n.LocalLobbyPlayer != null && n.LocalLobbyPlayer.isHost;
        }

        // Privacy dropdown of the lobby. Only after the Steam lobby exists (CreateLobby is async).
        public static void HostSetVisibility(ELobbyType type)
        {
            if (Brix.Game.Network.SteamManager.Initialized && Brix.Game.Network.SteamManager.Instance.LobbyId.IsValid()) UNetManager.instance.GameVisibility = type;
        }

        // Call AFTER the lobby menu opened (its OpenSelf selects the top-rated official MP map: Helix and Test Catapults tie at 0.97).
        public static string HostSelectMap(string mapKey, string modeCodename, string presetCodename)
        {
            if (!HostLobbyUp()) return "no host lobby";
            Asset_Map map = FindMap(mapKey, AssetPlayableContext.Multiplayer);
            Asset_Gamemode mode = FindMode(modeCodename, AssetPlayableContext.Multiplayer);
            Asset_GamemodePreset preset = FindPreset(mode, presetCodename, AssetPlayableContext.Multiplayer);
            if (map == null || mode == null || preset == null) return "map/mode/preset not found";
            if (!map.IsCurrentVersion()) return "map needs the update step";
            LobbyMenu.SetIsReady(false);                                          // same order as LobbySocial.SelectConfirmedMap (mod, live-used)
            SelectAndLoad(map, mode, preset, AssetPlayableContext.Multiplayer, false);
            LobbyMenu.CloseMapLibrary(); LobbyMenu.RefreshSignal.Invoke();
            return null;
        }

        public static string HostSelectSave(string saveKey)
        {
            if (!HostLobbyUp()) return "no host lobby";
            Asset_Map save = FindSave(saveKey);
            if (save == null) return "save not found";
            LobbyMenu.SetIsReady(false);
            SelectAndLoad(save, null, null, AssetPlayableContext.Multiplayer, true);   // Existing: mode/preset = the save's last played
            LobbyMenu.CloseMapLibrary(); LobbyMenu.RefreshSignal.Invoke();
            return null;
        }

        // Host and guest. Same guard as the lobby 'Ready' checkbox (BaseMapStarterMenu: if (LobbyMenu.CanSetReady) SetIsReady(!GetIsReady())).
        public static string SetReady(bool ready)
        {
            if (LobbyMenu.Player == null) return "no local lobby player";
            if (ready && !LobbyMenu.CanSetReady) return "cannot set ready (map/mode/preset missing or downloading)";
            LobbyMenu.SetIsReady(ready);
            return null;
        }

        // Host and guest: faction/team pickers of the lobby (LegacyLobbyLeftMenu.PlayerFaction / PlayerTeam setters).
        public static string SetFactionTeam(int faction, int team)
        {
            LobbyPlayer me = LobbyMenu.Player;
            if (me == null) return "no local lobby player";
            if (faction >= 0 && faction != me.factionNumber)
            {
                if (!GameParam.IsFactionAssignable(faction, me.factionNumber)) return "faction not assignable";
                me.CallCmdSetFaction(faction);
            }
            if (team >= 0 && team != me.TeamNumber)
            {
                if (!GameParam.IsTeamAssignable(team, me.TeamNumber)) return "team not assignable";
                me.CallCmdSetTeam(team);
            }
            return null;
        }

        // 0.4.18: what the lobby allows, from the game's own rules (GameParam min/max counts of the preset, IsTeamAssignable / IsFactionAssignable as the
        // pickers use them). A scenario reads this instead of guessing numbers. Conquest Versus: MP_MinTeams 2, MP_MinFactions 2, 4 teams (A .. D),
        // 4 player factions (Blue, Red, Green, Orange); the host's GameParam.FixFactions / FixTeams re-deal players when the counts break the rules.
        public static string DescribeTeams()
        {
            GameParam gp = GameParam.instance;
            if (gp == null || UNetManager.instance == null) return "no GameParam / lobby";
            StringBuilder sb = new StringBuilder();
            int teams = GameParam.CurrentTeamInfos == null ? 0 : GameParam.CurrentTeamInfos.Length;
            int facs = GameParam.CurrentFactionInfos == null ? 0 : GameParam.CurrentFactionInfos.Length;
            try
            {
                sb.Append("teams=").Append(teams).Append(" min/max=").Append(GameParam.MinTeamCount).Append('/').Append(GameParam.MaxTeamCount)
                  .Append(" assigned=").Append(GameParam.AssignedTeamCount).Append(" sharing=").Append(gp.Parameters.AllowSharingTeam)
                  .Append(" | factions=").Append(facs).Append(" min/max=").Append(GameParam.MinFactionCount).Append('/').Append(GameParam.MaxFactionCount)
                  .Append(" assigned=").Append(GameParam.AssignedFactionCount).Append(" sharing=").Append(gp.Parameters.AllowSharingFaction);
            }
            catch (Exception e) { sb.Append(" counts: ").Append(e.GetType().Name); }
            LobbyPlayer me = LobbyMenu.Player;
            if (me == null) { sb.Append(" | no local lobby player"); return sb.ToString(); }
            sb.Append(" | me f=").Append(me.factionNumber).Append(" t=").Append(me.TeamNumber).Append(" assignable teams=[");
            try { bool first = true; for (int i = 0; i < teams; i++) if (i == me.TeamNumber || GameParam.IsTeamAssignable(i, me.TeamNumber)) { if (!first) sb.Append(','); sb.Append(i); first = false; } }
            catch (Exception e) { sb.Append(e.GetType().Name); }
            sb.Append("] factions=[");
            try { bool first = true; for (int i = 0; i < facs; i++) if (i == me.factionNumber || GameParam.IsFactionAssignable(i, me.factionNumber)) { if (!first) sb.Append(','); sb.Append(i); first = false; } }
            catch (Exception e) { sb.Append(e.GetType().Name); }
            sb.Append(']');
            return sb.ToString();
        }

        // 0.4.18: is <n> assignable for the local lobby player right now (team or faction), by the picker's own rule
        public static bool GroupAssignable(bool team, int n, out int current, out string why)
        {
            current = -1; why = null; LobbyPlayer me = LobbyMenu.Player;
            if (me == null || GameParam.instance == null) { why = "no local lobby player"; return false; }
            current = team ? me.TeamNumber : me.factionNumber;
            if (n == current) return true;
            int groups = team ? (GameParam.CurrentTeamInfos == null ? 0 : GameParam.CurrentTeamInfos.Length) : (GameParam.CurrentFactionInfos == null ? 0 : GameParam.CurrentFactionInfos.Length);
            if (n >= groups) { why = (team ? "team " : "faction ") + n + " does not exist (this map/mode has " + groups + ")"; return false; }
            bool ok = team ? GameParam.IsTeamAssignable(n, current) : GameParam.IsFactionAssignable(n, current);
            if (!ok) why = "the game's lobby rule GameParam.Is" + (team ? "Team" : "Faction") + "Assignable(" + n + ", " + current + ") says no";
            return ok;
        }

        // 0.4.18: the Cmd the picker sends (LegacyLobbyLeftMenu.PlayerTeam / PlayerFaction setters), host decides
        public static void SendGroup(bool team, int n)
        {
            LobbyPlayer me = LobbyMenu.Player; if (me == null) return;
            if (team) me.CallCmdSetTeam(n); else me.CallCmdSetFaction(n);
        }

        // = BaseMapStarterMenu State.MpHost_Start (the only state where the lobby Start button is enabled).
        public static string HostStartBlocker()
        {
            if (!LobbyMenu.IsHost || LobbyMenu.Player == null) return "not host / no lobby";
            if (GameParam.Map == null || !GameParam.Map.IsCurrentVersion()) return "no usable map";
            if (LobbyMenu.IsAnyPlayerDownloading) return "a player is downloading the map";
            if (!LobbyMenu.GetIsReady()) return "host not ready";
            if (LobbyMenu.IsAnyClientNotReady) return "a guest is not ready";
            return null;
        }

        public static string HostStart(bool sameAsButton)
        {
            string b = HostStartBlocker();
            if (b != null) return b;
            if (sameAsButton) GameParam.instance.BeginLoadingScreen();   // lobby Start button: synced loading curtain, then host LobbyPlayer.SendReadyToBeginMessage
            else LobbyMenu.StartGame();                                  // mod 'start': host SendReadyToBeginMessage at once (live-verified T-119)
            return null;
        }

        // ---------------- (c) guest ----------------
        // Direct LAN / Radmin / loopback join = the server list 'Join' (NetworkServersActionsMenu.OnJoin) and VpJoinLan.
        // Steam-bridge join is plugin-internal: VirtualPlayer.cs 'join' (RefreshFriends; peer = friends[0]; StartBridge(false)).
        public static string JoinDirect(string host, int port, string label)
        {
            if (SceneManager.GetActiveScene().name != MenuScene || UNetManager.instance == null) return "not in main menu";
            if (NetworkClient.active || NetworkServer.active) return "network already active";
            if (!MenuLoaded<NetworkWaitingModalMenu>()) return "waiting modal not loaded";
            NetworkServersMenu.SelectedMatchData = new MatchList.MatchData { IsLanGame = true, Address = new MatchAddress(host, (ushort)port), MatchName = label };
            NetworkWaitingModalMenu.OpenAsClientSignal.Invoke();
            return null;
        }

        // Lobby 'Leave' row (LegacyLobbyLeftMenu Operation.LeaveLobby). Host or guest, lobby only.
        public static void LeaveLobby()
        {
            NetworkWaitingQuitModalMenu.OpenSignal.Invoke();
            UNetManager.instance.ResetPersistantNetworkSceneObjects();
            UNetManager.instance.Disconnect();
        }

        // ---------------- (c)/(d) leave or end a running match ----------------
        // In-game Quit > 'Leave game' (ContainerGenericPopulator QuitGame). Guest: leaves; host: ends the match for everyone.
        public static string QuitMatchToMenu()
        {
            if (SceneManager.GetActiveScene().name != GameScene || Brix.Engine.Architecte.instance == null) return "not in a match";
            Brix.Engine.Architecte.QuitToMainMenu();   // LeaveGame signal, ProjectDatabase.ClearAll, UNetManager.Disconnect -> offline scene SceneMenu
            return null;
        }

        // ---------------- network error modal ----------------
        public static void DismissErrorModal() { NetworkMessageModalMenu.CloseSignal.Invoke(); }   // = its OK button (OnOk)

        // Harmony postfix to log the error text the modal will show:
        // harmony.Patch(typeof(UNetManager).GetMethod("PopLastBrixNetworkError", BindingFlags.Public | BindingFlags.Static), null,
        //     new HarmonyMethod(typeof(BotMenu).GetMethod("AfterPopNetError", BindingFlags.NonPublic | BindingFlags.Static)));
        static void AfterPopNetError(string __result) { Debug.Log("MENU-NETERR " + __result); }   // replace with the plugin's PtLog
    }
}
