using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Brix.Assets;
using Brix.Engine;
using Brix.Game;
using Brix.Lua;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStorySteam
{
    // 0.4.20 SAVE-MP (cfg [Fixes] SaveMapInMultiplayer, default true; perf-trace tag SAVE-MP). Host-only "Зберегти карту" button in the game menu's settings section
    // during a multiplayer match (the game hides Quicksave there). Rules in SaveMapMpRules.cs.
    // Text: a postfix on BrixScriptLoader.LoadFile inserts the button block before "---screenshot" in GameMenu.lua (MOD-INV anchor "SaveMapMp").
    // Lua: every Script gets SB_SaveMapAllowed() (bool) and SB_SaveMap() (postfix on the Script constructors, like OrangeWardItem.AfterScript).
    // Save: the "new save" branch of BaseMapSaverMenu.OnSave without the UI (same as BotSaveLoad.Save, mirrored, not shared: that file belongs to the bot):
    // StatsTracker.SaveStats, Asset_Map.CreateShellFrom(GameParam.Map, Save), customName MP_<time>, playableContext Any (listed in SP and MP load menus), Manualsave,
    // Disk_WriteAsNew(Official). A unique name is picked first (Disk_WriteAsNew -> ApplyStaging deletes a same-name folder). Result = a toast (GameSignals.OnAutoFadeDialog,
    // the same signal Lua UIGame.DisplayNotification uses) + one log line. NOT verified live: whether a save made in MP restores the players' factions / owners when loaded
    // from the MP lobby is UNKNOWN (T-192).
    internal static class SaveMapMp
    {
        static Func<bool> enabled; static Action<string> note;
        static float lastPress; static int saves;
        const int SceneGame = 2;

        internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> log)
        {
            enabled = isEnabled; note = log;
            MethodInfo load = typeof(BrixScriptLoader).GetMethod("MoonSharp.Interpreter.Loaders.IScriptLoader.LoadFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (load == null) throw new MissingMethodException("BrixScriptLoader.IScriptLoader.LoadFile");
            harmony.Patch(load, null, new HarmonyMethod(typeof(SaveMapMp).GetMethod("AfterLoadFile", BindingFlags.Static | BindingFlags.NonPublic)));
            HarmonyMethod post = new HarmonyMethod(typeof(SaveMapMp).GetMethod("AfterScript", BindingFlags.Static | BindingFlags.NonPublic));
            int n = 0;
            foreach (ConstructorInfo c in typeof(Script).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) { harmony.Patch(c, null, post); n++; }
            if (n == 0) throw new MissingMethodException("MoonSharp Script constructors");
        }

        static void AfterLoadFile(string file, ref object __result)
        {
            try
            {
                string code = __result as string; if (code == null || file == null) return;
                string f = file.Replace('\\', '/');
                if (!f.EndsWith("/GameMenu.lua", StringComparison.OrdinalIgnoreCase)) return;   // exact file: the title menus end in GameMenu.lua too
                int count; string patched = SaveMapMpRules.Patch(code, out count);
                ModInventory.Anchor("SaveMapMp", "GameMenu.lua", "---screenshot line", count, true);
                if (enabled == null || !enabled()) return;
                if (count != 1) { if (note != null) note("SAVE-MP GameMenu.lua: found " + count + " '---screenshot' anchor line(s), need exactly 1: no button"); return; }
                if (!object.ReferenceEquals(patched, code)) { __result = patched; if (note != null) note("SAVE-MP GameMenu.lua: host-only '" + SaveMapMpRules.Label + "' button added to the settings section"); }
            }
            catch (Exception e) { if (note != null) note("SAVE-MP GameMenu.lua patch failed: " + e.GetType().Name + " " + e.Message); }
        }

        static void AfterScript(Script __instance)
        {
            try
            {
                if (__instance == null || __instance.Globals == null) return;
                __instance.Globals.Set("SB_SaveMapAllowed", DynValue.NewCallback(delegate(ScriptExecutionContext ctx, CallbackArguments args) { return DynValue.NewBoolean(WhyNot() == null); }, "SB_SaveMapAllowed"));
                __instance.Globals.Set("SB_SaveMap", DynValue.NewCallback(delegate(ScriptExecutionContext ctx, CallbackArguments args) { Press(); return DynValue.Void; }, "SB_SaveMap"));
            }
            catch (Exception) { }   // a Script the game already killed has no Globals
        }

        static string WhyNot()
        {
            try
            {
                bool inMatch = SceneManager.GetActiveScene().buildIndex == SceneGame;
                return SaveMapMpRules.Why(enabled != null && enabled(), Neo.IsMultiplayer, NetworkServer.active, inMatch,
                    inMatch && GameSignals.Received(GameSignals.LevelReady), GameParam.Map != null && GameParam.Gamemode != null && GameParam.Preset != null);
            }
            catch (Exception e) { return "помилка перевірки: " + e.GetType().Name; }
        }

        static string Folder(string name)
        {
            return Path.GetFullPath((AssetDispensary.saves.officialPath + name + "/").Replace("\\", "/")).Replace("\\", "/");
        }

        static void Toast(string text, float seconds)
        {
            try { GameSignals.Invoke(GameSignals.OnAutoFadeDialog, text, seconds); } catch (Exception) { }
        }

        static void Press()
        {
            float now = Time.realtimeSinceStartup;
            if (SaveMapMpRules.Busy(now, lastPress)) return;
            lastPress = now;
            string name = null;
            try
            {
                string why = WhyNot();
                if (why != null) { Toast("Карту не збережено: " + why, 4f); if (note != null) note(SaveMapMpRules.LogLine(false, null, why)); return; }
                Asset_Map current = GameParam.Map;
                name = SaveMapMpRules.PickName(DateTime.Now, delegate(string n) { return Directory.Exists(Folder(n)); });
                if (name == null) { Toast("Карту не збережено: усі імена на цю секунду зайняті", 4f); if (note != null) note(SaveMapMpRules.LogLine(false, null, "no free name")); return; }
                string folder = Folder(name);
                if (string.Equals(current.path, folder, StringComparison.OrdinalIgnoreCase)) { Toast("Карту не збережено: збіг з відкритим збереженням", 4f); if (note != null) note(SaveMapMpRules.LogLine(false, name, "target is the loaded save")); return; }
                Stopwatch sw = Stopwatch.StartNew();
                try { StatsTracker.SaveStats(); } catch (Exception) { }
                Asset_Map save = Asset_Map.CreateShellFrom(current, Asset_Map.MapType.Save);
                save.customName = name;
                save.playableContext = AssetPlayableContext.Any;
                save.saveType = Asset_Map.SaveType.Manualsave;
                save.isDebug = current.isDebug;
                try { save.Disk_WriteAsNew(AssetAuthorType.Official); }
                catch (Exception e)
                {
                    try { if (save.IsStaging()) save.CancelStaging(); } catch (Exception) { }
                    Toast("Карту не збережено: " + e.GetType().Name, 5f);
                    if (note != null) note(SaveMapMpRules.LogLine(false, name, "Disk_WriteAsNew threw " + e.GetType().Name + ": " + e.Message));
                    return;
                }
                long ms = sw.ElapsedMilliseconds;
                string[] need = { "meta.json", "gameobjects.json", "PlacedBlocks.json", "Factions.json" };
                long bytes = 0; string missing = null;
                foreach (string f in need) { if (!File.Exists(folder + f)) { missing = f; break; } bytes += new FileInfo(folder + f).Length; }
                if (missing != null) { Toast("Карту не збережено: немає " + missing, 5f); if (note != null) note(SaveMapMpRules.LogLine(false, name, "missing " + missing + " in " + folder)); return; }
                saves++;
                Toast("Карту збережено: " + name, 4f);
                if (note != null) note(SaveMapMpRules.LogLine(true, name, ms + " ms, " + bytes + " B, " + folder + ", saves this session " + saves + ", map " + current.customName + " / " + current.codename));
            }
            catch (Exception e)
            {
                Toast("Карту не збережено: " + e.GetType().Name, 5f);
                if (note != null) note(SaveMapMpRules.LogLine(false, name, e.GetType().Name + ": " + e.Message));
            }
        }
    }
}
