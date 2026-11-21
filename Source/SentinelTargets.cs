using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Brix.Engine;
using Brix.Game;
using Brix.Game.Components.Tracking;
using Brix.Game.Components.Wards;
using Brix.UI.Builder.Menu;
using Brix.UI.GameSelector;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;

// 0.3.98: a Sentinel ward (the one that shoots) only tracks the factions its prefab lists in TrackingController.trackedRelations, normally Enemy. This widens that mask per ward:
//   0 = the game's own value (enemies), 1 = enemies, neutral and undefined factions, 2 = every faction including allies.
// Chosen per ward: select a Sentinel ward, then type /tm 0, /tm 1 or /tm 2 in the chat (/tm alone shows the choice, /tm off removes it so the ward follows the default again). The choice is stored by the ward's
// position in BepInEx\config\SentinelWardTargets.txt and survives a restart; Fixes/SentinelWardTargets is the default for wards that have no choice of their own.
// The Lua global SB_SentinelMode(n) sets that default too. Only the host's tracking and shooting matter (commands are applied on the host). The ward still needs sight and a clear line to the target.
internal static class SentinelTargets
{
    static Func<int> getMode; static Action<int> setMode;
    static FieldInfo relations;
    static readonly Dictionary<int, Affiliation.Relation> original = new Dictionary<int, Affiliation.Relation>();
    static readonly Dictionary<string, int> perWard = new Dictionary<string, int>();
    static readonly HashSet<WardController> selected = new HashSet<WardController>();
    static string file;

    internal static void Install(Harmony harmony, Func<int> get, Action<int> set)
    {
        getMode = get; setMode = set;
        relations = AccessTools.Field(typeof(TrackingController), "trackedRelations");
        if (relations == null || relations.FieldType != typeof(Affiliation.Relation)) { relations = null; return; }
        try { file = Path.Combine(BepInEx.Paths.ConfigPath, "SentinelWardTargets.txt"); Load(); } catch (Exception) { }
        harmony.Patch(AccessTools.Method(typeof(WardController), "Initialize"), null, new HarmonyMethod(AccessTools.Method(typeof(SentinelTargets), "AfterInit")), null, null, null);
        harmony.Patch(AccessTools.Method(typeof(TrackingController), "BrixSlowUpdate"), null, new HarmonyMethod(AccessTools.Method(typeof(SentinelTargets), "AfterSlowUpdate")), null, null, null);
        harmony.Patch(AccessTools.Method(typeof(UISelectable), "OnSelect"), null, new HarmonyMethod(AccessTools.Method(typeof(SentinelTargets), "AfterSelect")), null, null, null);
        harmony.Patch(AccessTools.Method(typeof(UISelectable), "OnUnselect"), new HarmonyMethod(AccessTools.Method(typeof(SentinelTargets), "BeforeUnselect")), null, null, null, null);
        harmony.Patch(AccessTools.Method(typeof(NetworkChatMenu), "SendChatMessage"), new HarmonyMethod(AccessTools.Method(typeof(SentinelTargets), "BeforeChat")), null, null, null, null);
        HarmonyMethod post = new HarmonyMethod(AccessTools.Method(typeof(SentinelTargets), "AfterScript"));
        foreach (ConstructorInfo c in typeof(Script).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) harmony.Patch(c, null, post, null, null, null);
    }

    internal static void Clear() { getMode = null; setMode = null; original.Clear(); selected.Clear(); }

    // ---- storage (key = rounded position of the ward)
    static string Key(WardController w) { Vector3 p = w.transform.position; return Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.y) + "," + Mathf.RoundToInt(p.z); }

    static void Load()
    {
        perWard.Clear(); if (file == null || !File.Exists(file)) return;
        foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
        {
            int i = line.IndexOf('='); if (i <= 0 || line.StartsWith("#")) continue;
            int m; if (int.TryParse(line.Substring(i + 1).Trim(), out m)) perWard[line.Substring(0, i).Trim()] = Mathf.Clamp(m, 0, 2);
        }
    }

    static void Save()
    {
        if (file == null) return;
        try
        {
            StringBuilder sb = new StringBuilder("# x,y,z of a Sentinel ward = who it shoots (0 enemies, 1 +neutral, 2 everyone). Written by the mod; edit with the game closed.\r\n");
            foreach (KeyValuePair<string, int> kv in perWard) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    // ---- selection tracking
    static void AfterSelect(UISelectable __instance)
    {
        try { WardController w = __instance == null ? null : __instance.GetComponent<WardController>(); if (w != null && w.GetComponent<WardAttack>() != null) selected.Add(w); } catch (Exception) { }
    }

    static void BeforeUnselect(UISelectable __instance)
    {
        try { WardController w = __instance == null ? null : __instance.GetComponent<WardController>(); if (w != null) selected.Remove(w); } catch (Exception) { }
    }

    // ---- chat command  /tm [0|1|2|off]
    static bool BeforeChat(string line) { return !Handle(line); }

    // returns true when the line was a /tm command (handled), false when it is ordinary text
    internal static bool Handle(string line)
    {
        try
        {
            if (line == null) return false;
            string t = line.Trim(); if (!(t.Equals("/tm", StringComparison.OrdinalIgnoreCase) || t.StartsWith("/tm ", StringComparison.OrdinalIgnoreCase))) return false;
            string arg = t.Length > 3 ? t.Substring(3).Trim() : "";
            selected.RemoveWhere(delegate(WardController w) { return w == null; });
            if (selected.Count == 0) { Say("/tm: select a Sentinel ward first (the one that shoots)"); return true; }
            if (arg.Length == 0)
            {
                foreach (WardController w in selected) { int cur; Say("Ward " + Key(w) + ": " + (perWard.TryGetValue(Key(w), out cur) ? "mode " + cur : "default mode " + (getMode == null ? 0 : getMode()))); break; }
                return true;
            }
            bool off = arg.Equals("off", StringComparison.OrdinalIgnoreCase) || arg == "-1";
            int m; if (!off && !(int.TryParse(arg, out m) && m >= 0 && m <= 2)) { Say("/tm 0 = enemies, /tm 1 = enemies + neutral, /tm 2 = everyone, /tm off = default"); return true; }
            m = off ? -1 : int.Parse(arg);
            if (!NetworkServer.active) { Say("/tm works on the host only"); return true; }
            int n = 0;
            foreach (WardController w in selected) { if (w == null) continue; if (off) perWard.Remove(Key(w)); else perWard[Key(w)] = m; Apply(w); n++; }
            Save();
            Say("Ward targets " + (off ? "default" : "mode " + m) + " for " + n + " ward(s)");
            return true;
        }
        catch (Exception) { return false; }
    }

    // ---- mouse box for wards: hold Ctrl+Shift and drag with the left button; every Sentinel ward whose base is inside the rectangle becomes the selection (the game's own box selects units)
    static bool dragging; static Vector2 dragA;
    static bool CtrlShift() { return (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)); }

    internal static void DrawBox()
    {
        try
        {
            if (!dragging || Event.current == null || Event.current.type != EventType.Repaint) return;
            Vector2 b = Input.mousePosition; float x0 = Mathf.Min(dragA.x, b.x), x1 = Mathf.Max(dragA.x, b.x), y0 = Screen.height - Mathf.Max(dragA.y, b.y), y1 = Screen.height - Mathf.Min(dragA.y, b.y);
            Color old = GUI.color; Texture2D w = Texture2D.whiteTexture;
            GUI.color = new Color(1f, 0.75f, 0f, 0.15f); GUI.DrawTexture(new Rect(x0, y0, x1 - x0, y1 - y0), w);
            GUI.color = new Color(1f, 0.75f, 0f, 0.9f);
            GUI.DrawTexture(new Rect(x0, y0, x1 - x0, 2f), w); GUI.DrawTexture(new Rect(x0, y1 - 2f, x1 - x0, 2f), w); GUI.DrawTexture(new Rect(x0, y0, 2f, y1 - y0), w); GUI.DrawTexture(new Rect(x1 - 2f, y0, 2f, y1 - y0), w);
            GUI.color = old;
        }
        catch (Exception) { }
    }

    static void BoxSelect(Vector2 a, Vector2 b)
    {
        Camera cam = Camera.main; if (cam == null) return;
        Rect r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        selected.Clear(); int n = 0;
        foreach (WardController w in UnityEngine.Object.FindObjectsOfType<WardController>())
        {
            if (w == null || w.GetComponent<WardAttack>() == null) continue;
            Vector3 s = cam.WorldToScreenPoint(w.transform.position); if (s.z > 0f && r.Contains(new Vector2(s.x, s.y))) { selected.Add(w); n++; }
        }
        Say(n == 0 ? "No Sentinel wards in the box" : n + " Sentinel ward(s) selected: Ctrl+Shift+W or the chat /tm 0|1|2|off");
    }

    // Ctrl+Shift+W (single-player sandbox has no chat) cycles the selected ward(s): default -> 0 -> 1 -> 2 -> default
    internal static void Poll()
    {
        try
        {
            if (!NetworkServer.active) { dragging = false; return; }
            bool mod = CtrlShift();
            if (mod && Input.GetMouseButtonDown(0)) { dragging = true; dragA = Input.mousePosition; }
            if (dragging && (Input.GetMouseButtonUp(0) || !mod))
            {
                Vector2 b = Input.mousePosition; dragging = false;
                if (Input.GetMouseButtonUp(0) && (b - dragA).sqrMagnitude > 64f) BoxSelect(dragA, b);
            }
            if (selected.Count == 0) return;
            if (!(Input.GetKeyDown(KeyCode.W) && mod)) return;
            selected.RemoveWhere(delegate(WardController w) { return w == null; }); if (selected.Count == 0) return;
            WardController first = null; foreach (WardController w in selected) { first = w; break; }
            int cur; int next = perWard.TryGetValue(Key(first), out cur) ? cur + 1 : 0; if (next > 2) next = -1;
            foreach (WardController w in selected) { if (next < 0) perWard.Remove(Key(w)); else perWard[Key(w)] = next; Apply(w); }
            Save();
            Say("Ward targets: " + (next < 0 ? "default" : next == 0 ? "0 enemies" : next == 1 ? "1 enemies + neutral" : "2 everyone") + " (" + selected.Count + " ward)");
        }
        catch (Exception) { }
    }
    static void Say(string text) { try { GameSignals.Invoke(GameSignals.OnAutoFadeDialog, text, 4f); } catch (Exception) { } }

    // ---- Lua global: the default for wards without their own choice
    static void AfterScript(Script __instance)
    {
        try
        {
            if (__instance == null || __instance.Globals == null) return; EditorBlocks.Register(__instance);
            __instance.Globals.Set("SB_Chat", DynValue.NewCallback((ScriptExecutionContext exec, CallbackArguments args) => { bool ok = false; if (args.Count > 0 && args[0].Type == DataType.String) ok = Handle(args[0].String); if (!ok) Say("Commands: /tm 0|1|2|off (select a Sentinel ward first)"); return DynValue.NewBoolean(ok); }, "SB_Chat"));
            __instance.Globals.Set("SB_SentinelMode", DynValue.NewCallback((ScriptExecutionContext exec, CallbackArguments args) =>
            {
                if (args.Count > 0 && args[0].Type == DataType.Number) SetDefault((int)args[0].Number);
                return DynValue.NewNumber(getMode == null ? 0 : getMode());
            }, "SB_SentinelMode"));
        }
        catch (Exception) { }
    }

    static void SetDefault(int m)
    {
        if (setMode == null) return;
        setMode(Mathf.Clamp(m, 0, 2));
        try { foreach (WardController w in UnityEngine.Object.FindObjectsOfType<WardController>()) Apply(w); } catch (Exception) { }
    }

    static void AfterInit(WardController __instance) { try { Apply(__instance); } catch (Exception) { } }

    // the server refreshes a tracker twice a second; the ward's position is final by then, so a stored choice is found even for pooled wards
    static void AfterSlowUpdate(TrackingController __instance)
    {
        try { WardController w = __instance as WardController; if (w != null && NetworkServer.active) Apply(w); } catch (Exception) { }
    }

    static void Apply(WardController w)
    {
        if (relations == null || w == null || w.GetComponent<WardAttack>() == null) return;
        int id = w.GetInstanceID(); Affiliation.Relation cur = (Affiliation.Relation)relations.GetValue(w), orig;
        if (!original.TryGetValue(id, out orig)) { orig = cur; original[id] = cur; }
        int m; if (!perWard.TryGetValue(Key(w), out m)) m = getMode == null ? 0 : getMode();
        Affiliation.Relation want = m >= 2 ? Affiliation.Relation.All : m == 1 ? (Affiliation.Relation.Enemy | Affiliation.Relation.Neutral | Affiliation.Relation.Undefined) : Affiliation.Relation.None;
        Affiliation.Relation target = orig | want;
        if (cur != target) relations.SetValue(w, target);
    }
}
