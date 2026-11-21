// 0.4.10 IN-GAME TEST BOT game layer: taken from the read-only research draft bot-research\saveload-BotSaveLoad.cs (side-session-notes of local_a8d26072, 2026-11-06),
// compiled with the plugin (signatures checked by csc against the real game DLLs). Called only by the bot (Bot.cs / BotActions.cs) while it is armed.
// saveload research proposal (NOT compiled here: the task allowed only reading + the decompiler).
// Every game member used below was checked in the REAL DLL decompile (files saveload-*.cs in this folder).
// C# 5 only. Fits BotRunner.cs (BotOutcome Ok/Fail/Wait, PollSeconds 0.2, DefaultActionTimeout save 120 / load 300).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Brix.Assets;              // Asset_Map, AssetDispensary, AssetQueryParameters, AssetPlayableContext, AssetAuthorType
using Brix.Engine;              // GameSignals, Architecte, Neo (Brix.Engine.Neo)
using Brix.Network;             // UNetManager, LobbyPlayer
using Brix.UI.Builder.Menu;     // BaseMapLibraryMenu, BaseAssetMenu, LobbyMenu
using UnityEngine.Networking;   // NetworkServer, NetworkClient
using UnityEngine.SceneManagement;

namespace CastleStorySteam
{
    internal static class BotSaveLoad
    {
        internal const string Prefix = "BOT_";          // every bot save = folder Info/Saves/BOT_<name>/ ; customName = BOT_<name>
        internal const string Marker = "bot-save.txt";  // optional: proves a folder was written by the bot
        internal const int SceneMenu = 1;               // "SceneMenu": title screen AND the MP lobby
        internal const int SceneGame = 2;               // "ScenePrincipale": the match

        // Same folder Disk_WriteAsNew(Official) picks: AssetDispensary.saves.officialPath ("Info/Saves/") + customName + "/".
        // Asset_Base.path normalises with Path.GetFullPath + '/' separators, so compare against this (case-insensitive).
        internal static string Folder(string name)
        {
            return Path.GetFullPath((AssetDispensary.saves.officialPath + Prefix + name + "/").Replace("\\", "/")).Replace("\\", "/");
        }

        // ---------------------------------------------------------------- SAVE (single player or MP HOST, in a running match)
        // Mirrors the "new save" branch of BaseMapSaverMenu.OnSave and MenuCalls.QuickSave, minus the UI (no menu toggle,
        // no camera screenshot). Synchronous on the main thread: when it returns the folder is complete.
        internal static BotOutcome Save(string name)
        {
            if (!BotRules.ValidName(name, 40)) return BotOutcome.Fail("bad save name");
            if (SceneManager.GetActiveScene().buildIndex != SceneGame) return BotOutcome.Fail("not in a match (scene " + SceneManager.GetActiveScene().name + ")");
            if (!NetworkServer.active) return BotOutcome.Fail("guest: only the host or single player can save");
            if (!GameSignals.Received(GameSignals.LevelReady)) return BotOutcome.Fail("level not ready yet");
            Asset_Map current = GameParam.Map;
            if (current == null || GameParam.Gamemode == null || GameParam.Preset == null) return BotOutcome.Fail("GameParam.Map/Gamemode/Preset missing");
            string folder = Folder(name), custom = Prefix + name;
            // Disk_WriteAsNew -> ApplyStaging -> Disk_Delete() DELETES an existing folder of the same name. Never let it.
            if (Directory.Exists(folder)) return BotOutcome.Fail("folder exists, refusing to overwrite: " + folder);
            if (string.Equals(current.path, folder, StringComparison.OrdinalIgnoreCase)) return BotOutcome.Fail("target is the loaded save");
            int savedSignals = GameSignals.GameSaved._Count;
            Stopwatch sw = Stopwatch.StartNew();
            Asset_Map save = Asset_Map.CreateShellFrom(current, Asset_Map.MapType.Save); // shell + FillFrom(current,false): codename(+new GUID), names, last gamemode/preset, thumbnail ref
            save.customName = custom;                        // folder name + list label
            save.playableContext = AssetPlayableContext.Any; // like BaseMapSaverMenu (255): listed in SP and MP load menus
            save.saveType = Asset_Map.SaveType.Manualsave;
            save.isDebug = current.isDebug;
            try
            {
                save.Disk_WriteAsNew(AssetAuthorType.Official); // stage in %TEMP%, write all files, copy to Info/Saves/<custom>/, GameSaved + "saved" toast
            }
            catch (Exception e)
            {
                try { if (save.IsStaging()) save.CancelStaging(); } catch (Exception) { } // only the temp staging dir this call created
                return BotOutcome.Fail("Disk_WriteAsNew threw " + e.GetType().Name + ": " + e.Message);
            }
            long ms = sw.ElapsedMilliseconds;
            try { File.WriteAllText(folder + Marker, "bot save " + custom + " " + DateTime.Now.ToString("s")); } catch (Exception) { }
            // proof 1: the game wrote where we expected
            if (!string.Equals(save.path, folder, StringComparison.OrdinalIgnoreCase)) return BotOutcome.Fail("written to " + save.path);
            // proof 2: the main files exist
            string[] need = { "meta.json", "gameobjects.json", "PlacedBlocks.json", "Factions.json" };
            long bytes = 0;
            foreach (string f in need) { if (!File.Exists(folder + f)) return BotOutcome.Fail("missing " + f); bytes += new FileInfo(folder + f).Length; }
            // proof 3: the game's own reader accepts it as a current-version save with our name
            Asset_Map back = new Asset_Map(folder);
            if (back.mapType != Asset_Map.MapType.Save || back.customName != custom || !back.IsCurrentVersion())
                return BotOutcome.Fail("meta re-read mismatch type=" + back.mapType + " name=" + back.customName + " v=" + back.version);
            string signal = GameSignals.GameSaved._Count > savedSignals ? "GameSaved" : "no GameSaved signal (soft)";
            return BotOutcome.Ok("saved " + custom + " in " + ms + " ms, " + bytes + " B, " + signal + ", path " + folder);
        }

        // ---------------------------------------------------------------- find a bot save (fresh disk scan)
        internal static Asset_Map Find(string name)
        {
            string want = Folder(name);
            List<Asset_Map> all = AssetDispensary.saves.SendQuery(AssetQueryParameters.AllSources | AssetQueryParameters.RefreshCache, AssetPlayableContext.Any);
            foreach (Asset_Map m in all)
                if (m != null && string.Equals(m.path, want, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        static string CheckLoadable(Asset_Map save, AssetPlayableContext ctx)
        {
            if (save.mapType != Asset_Map.MapType.Save) return "not a save";
            if (!save.IsCurrentVersion()) return "outdated save (version " + save.version + "); refusing SaveUpdaterRunner (it rewrites the save)";
            if ((save.playableContext & ctx) == 0) return "save not playable in " + ctx;
            if (save.GetLastPlayedGamemode() == null) return "save has no last-played gamemode";
            return null;
        }

        // ---------------------------------------------------------------- LOAD, single player (from the title screen)
        // Same calls as Load Game -> select -> Play: BaseMapLibraryMenu.SetSelectedMap (via the library's SelectedAsset),
        // then BaseMapStarterMenu.OnOk (Single): SetSelectedAsLoaded + GameParam.StartGame(EmptySelected).
        internal static BotOutcome StartSinglePlayerLoad(string name)
        {
            if (SceneManager.GetActiveScene().buildIndex != SceneMenu) return BotOutcome.Fail("not on the title screen: leave the match first (Architecte.QuitToMainMenu)");
            if (NetworkServer.active || NetworkClient.active || Neo.IsMultiplayer) return BotOutcome.Fail("still connected / multiplayer");
            if (GameParam.instance == null) return BotOutcome.Fail("no GameParam");
            Asset_Map save = Find(name);
            if (save == null) return BotOutcome.Fail("no save at " + Folder(name));
            string why = CheckLoadable(save, AssetPlayableContext.Singleplayer);
            if (why != null) return BotOutcome.Fail(why);
            BaseMapLibraryMenu.SetSelectedMap(save, AssetPlayableContext.Singleplayer, BaseAssetMenu.MenuGameContext.Normal, BaseAssetMenu.MenuTypeContext.Existing);
            if (GameParam.SelectedGamemode == null || GameParam.SelectedPreset == null) { BaseMapLibraryMenu.EmptySelected(); return BotOutcome.Fail("no gamemode/preset for the save"); }
            BaseMapLibraryMenu.SetSelectedAsLoaded();
            GameParam.StartGame(BaseMapLibraryMenu.EmptySelected); // -> loading screen -> UNetManager.StartHostLocalOnly -> scene ScenePrincipale -> GameLoader
            return BotOutcome.Wait("start requested");
        }

        // Poll (host, SP and guest): Done when THIS scene received LevelReady for the expected save.
        // GameSignals counters are cleared on every scene change (GameSignals.Awake/OnDestroy -> Signal.Clear -> Reset), so Received() is per match.
        internal static BotOutcome PollInMatch(string expectCodenameOrNull, string expectFolderOrNull, float elapsed)
        {
            Scene s = SceneManager.GetActiveScene();
            if (s.buildIndex != SceneGame)
            {
                if (elapsed > 30f && !NetworkServer.active && !NetworkClient.active) return BotOutcome.Fail("no host started 30 s after the request");
                return BotOutcome.Wait("scene " + s.name);
            }
            if (GameSignals.Received(GameSignals.LoadError)) return BotOutcome.Fail("LoadError (brix.log: Architecte.OnLoadError())");
            if (!GameSignals.Received(GameSignals.LevelReady)) return BotOutcome.Wait("loading");
            Asset_Map m = GameParam.Map;
            if (m == null) return BotOutcome.Fail("GameParam.Map is null after LevelReady");
            if (expectFolderOrNull != null && !string.Equals(m.path, expectFolderOrNull, StringComparison.OrdinalIgnoreCase)) return BotOutcome.Fail("loaded " + m.path);
            if (expectCodenameOrNull != null && m.codename != expectCodenameOrNull) return BotOutcome.Fail("loaded codename " + m.codename);
            if (m.IsNewGame() || !GameSignals.Received(GameSignals.LevelSaveReady)) return BotOutcome.Fail("loaded as a NEW game, not a save");
            if (GameParam.instance == null || !GameParam.instance.HostReady) return BotOutcome.Wait("waiting for host ready");
            return BotOutcome.Ok("save loaded: " + m.customName + " (" + m.codename + ")");
        }

        // ---------------------------------------------------------------- LOAD, MP HOST (inside its own lobby, guests joined)
        // Same 4 calls the mod already uses in LobbySocial.SelectConfirmedMap, but MenuTypeContext.Existing (= MpLoadGameLibraryMenu),
        // so the gamemode/preset come from the save (last played).
        internal static BotOutcome HostSelectSave(string name)
        {
            UNetManager m = UNetManager.instance;
            if (m == null || !NetworkServer.active || m.LocalLobbyPlayer == null || !m.LocalLobbyPlayer.isHost) return BotOutcome.Fail("not hosting a lobby");
            if (SceneManager.GetActiveScene().buildIndex != SceneMenu) return BotOutcome.Fail("match running: leave, host a new lobby, let guests rejoin");
            Asset_Map save = Find(name);
            if (save == null) return BotOutcome.Fail("no save at " + Folder(name));
            string why = CheckLoadable(save, AssetPlayableContext.Multiplayer);
            if (why != null) return BotOutcome.Fail(why);
            LobbyMenu.SetIsReady(false);
            BaseMapLibraryMenu.SetSelectedMap(save, AssetPlayableContext.Multiplayer, BaseAssetMenu.MenuGameContext.Normal, BaseAssetMenu.MenuTypeContext.Existing);
            BaseMapLibraryMenu.SetSelectedAsLoaded();   // GameParam.Map/Gamemode/Preset -> Parameters SyncVar -> guests look it up / request download
            LobbyMenu.CloseMapLibrary();
            LobbyMenu.RefreshSignal.Invoke();
            if (GameParam.Map != save || GameParam.Gamemode == null || GameParam.Preset == null) return BotOutcome.Fail("selection did not stick");
            int factions = GameParam.CurrentFactionInfos == null ? 0 : GameParam.CurrentFactionInfos.Length;
            return BotOutcome.Ok("selected " + save.customName + " codename " + save.codename + "; saved factions " + factions + ", lobby players " + m.LobbyPlayerList.Count);
        }

        // Optional, host: put each lobby player back on the saved faction whose owner name equals his lobby name.
        // (A guest can do the same for himself with UNetManager.instance.LocalLobbyPlayer.CallCmdSetFaction(i); for a save the command accepts any index.)
        internal static string HostRestoreFactions()
        {
            GameParam.FactionInfo[] infos = GameParam.CurrentFactionInfos;
            if (infos == null || infos.Length == 0) return "no saved factions";
            string missing = "";
            foreach (LobbyPlayer p in UNetManager.instance.LobbyPlayerList)
            {
                if (p == null) continue;
                int hit = -1;
                for (int i = 0; i < infos.Length; i++) if (infos[i] != null && infos[i].Name == p.playerName) { hit = i; break; }
                if (hit >= 0) p.NetworkfactionNumber = hit; else missing += " " + p.playerName;
            }
            return missing.Length == 0 ? null : "no saved faction for:" + missing;
        }

        // Poll, host, every 0.2 s after HostSelectSave. Presses Start under exactly the conditions of BaseMapStarterMenu state MpHost_Start.
        internal static BotOutcome HostPollStart(ref bool started, ref float rearmAt, float now, string codename, float elapsed)
        {
            if (started) return PollInMatch(codename, null, elapsed);
            if (GameParam.Map == null || GameParam.Map.codename != codename) return BotOutcome.Fail("lobby selection changed");
            if (LobbyMenu.IsAnyPlayerDownloading) return BotOutcome.Wait("guests downloading the save");
            if (!LobbyMenu.GetIsReady()) { LobbyMenu.SetIsReady(true); rearmAt = now + 5f; return BotOutcome.Wait("host ready (uploads started)"); } // SetIsReady(true) -> SendMapToClients()
            // a guest that asked for the file after the upload round never gets it until the host re-readies (SetIsReady(true) = AbortUploads + SendMapToClients)
            if (now >= rearmAt)
                foreach (LobbyPlayer p in UNetManager.instance.LobbyPlayerList)
                    if (p != null && !p.isHost && p.RequestedMapDownload)
                    { LobbyMenu.SetIsReady(false); LobbyMenu.SetIsReady(true); rearmAt = now + 5f; return BotOutcome.Wait("re-sent the save to " + p.playerName); }
            if (LobbyMenu.IsAnyClientNotReady) return BotOutcome.Wait("guests not ready");
            if (!GameParam.Map.IsCurrentVersion()) return BotOutcome.Fail("outdated");
            GameParam.instance.BeginLoadingScreen(); // the Start button: SyncEvent -> loading screen everywhere -> host SendReadyToBeginMessage -> ServerChangeScene
            started = true;
            return BotOutcome.Wait("start pressed");
        }

        // ---------------------------------------------------------------- MP GUEST in the host's lobby
        // Ready up once the host's selected save is present locally (own copy or the automatic download).
        // The host clears ready flags on every selection change only on ITS side (UNetManager.SetAllClientsAsNotReady),
        // the guest's own readyToBegin may still read true: re-send ready every few seconds until the scene changes.
        internal static BotOutcome GuestPollReady(ref float nextReadyAt, float now)
        {
            UNetManager m = UNetManager.instance;
            if (m == null || m.LocalLobbyPlayer == null) return BotOutcome.Wait("not in a lobby");
            if (m.LocalLobbyPlayer.isHost || NetworkServer.active) return BotOutcome.Fail("this is the host");
            if (SceneManager.GetActiveScene().buildIndex == SceneGame) return BotOutcome.Ok("host started the match");
            GameParam gp = GameParam.instance;
            if (gp == null || string.IsNullOrEmpty(gp.Parameters.MapCodeName)) return BotOutcome.Wait("host has not selected");
            if (LobbyMenu.IsDownloading) return BotOutcome.Wait("downloading the save " + m.LocalLobbyPlayer.downloadProgression.ToString("P0"));
            if (GameParam.Map == null || GameParam.Map.codename != gp.Parameters.MapCodeName) return BotOutcome.Wait("save not matched locally yet");
            if (GameParam.Map.IsNewGame()) return BotOutcome.Wait("host selected a map, not a save");
            if (!LobbyMenu.CanSetReady) return BotOutcome.Wait("cannot set ready yet");
            if (now >= nextReadyAt) { LobbyMenu.SetIsReady(true); nextReadyAt = now + 3f; } // guest branch = LocalLobbyPlayer.SendReadyToBeginMessage()
            return BotOutcome.Wait("ready sent; waiting for host start");
        }
    }
}
