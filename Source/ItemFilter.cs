using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using Brix.Lua;
using Brix.UI.Icons;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.53: the host can switch off building items (blueprints from the game's own construction menu). The game builds that menu from
    // LUI/Meta/Meta_Structure.lua, Meta_StoneBlock.lua and Meta_WoodBlock.lua; when those files are loaded, the lines of switched-off items are
    // changed so the item stays known to the game (existing buildings, saves) but is no longer listed in the construction wheel.
    // The host's list is sent to every guest with the same mod version; the effective list is the host's when you are a guest, else your own.
    // The list applies the next time a map is loaded. Nothing outside the construction menu is touched.
    internal static class ItemFilter
    {
        internal sealed class Entry { internal string Id, Name, LocKey, IconField, Section; internal Sprite Icon; internal bool IconTried; }
        static readonly string[,] Files = { { "Meta_Structure.lua", "Будівлі та станції" }, { "Meta_StoneBlock.lua", "Кам'яні блоки" }, { "Meta_WoodBlock.lua", "Дерев'яні блоки" } };
        // Brickton classes are made by crafting a class kit; removing a kit from the station recipe list in Meta_Operable.lua forbids that class.
        static readonly string[,] ClassKits = { { "HalberdierKit", "##recipe_halberdierkit", "_Halberdier_Kit" }, { "KnightKit", "##recipe_knightkit", "_Knight_Kit" }, { "ArcherKit", "##recipe_archerkit", "_Archer_Kit" }, { "ArbalistKit", "##recipe_arbalistkit", "_Arbalist_Kit" }, { "AlchemistKit", "##recipe_alchemistkit", "_Alchemist_Kit" }, { "ArtificerKit", "##recipe_artificerkit", "_Artificer_Kit" } };
        static readonly List<Entry> catalog = new List<Entry>(); static bool catalogTried;
        internal static HashSet<string> Local = new HashSet<string>(StringComparer.Ordinal);
        internal static HashSet<string> Remote;   // set while connected to a host that sent a list
        static ConfigEntry<string> cfg;
        internal static bool WindowOpen; static Rect window = new Rect(620, 60, 640, 560); static Vector2 scroll; static bool changed; internal static float ChangedAt;
        internal static Action<string> Log;
        internal static string Applied = "";

        internal static void Bind(ConfigFile c)
        {
            cfg = c.Bind("Items", "Disabled", "", "Comma-separated ids of construction-menu items switched off (set them in the F8 window, 'Предмети').");
            Local = Parse(cfg.Value);
        }
        internal static HashSet<string> Parse(string text)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(text)) foreach (string p in text.Split(',')) { string t = p.Trim(); if (t.Length > 0 && t.Length < 64) set.Add(t); }
            return set;
        }
        internal static string Join(HashSet<string> set) { List<string> l = new List<string>(set); l.Sort(StringComparer.Ordinal); return string.Join(",", l.ToArray()); }
        internal static HashSet<string> Effective { get { return Remote != null ? Remote : Local; } }

        internal static void Install(Harmony h)
        {
            MethodInfo m = typeof(BrixScriptLoader).GetMethod("MoonSharp.Interpreter.Loaders.IScriptLoader.LoadFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) { if (Log != null) Log("Items: script loader hook not found."); return; }
            h.Patch(m, null, new HarmonyMethod(typeof(ItemFilter).GetMethod("AfterLoadFile", BindingFlags.Static | BindingFlags.NonPublic)));
            if (Log != null) Log("Items: construction menu filter ready (" + Local.Count + " switched off locally).");
        }

        static void AfterLoadFile(string file, ref object __result)
        {
            try
            {
                string code = __result as string; if (code == null || file == null) return;
                string f = file.Replace('\\', '/'); bool match = false;
                for (int i = 0; i < Files.GetLength(0); i++) if (f.EndsWith("/" + Files[i, 0], StringComparison.OrdinalIgnoreCase) || f.EndsWith(Files[i, 0], StringComparison.OrdinalIgnoreCase)) match = true;
                if (f.EndsWith("Meta_Operable.lua", StringComparison.OrdinalIgnoreCase)) { __result = FilterOperable(code, f); return; }
                if (!match) return;
                HashSet<string> off = Effective; if (off.Count == 0) { Applied = ""; return; }
                string[] lines = code.Split('\n'); int hidden = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i]; int k = line.IndexOf("\"Blueprints\"", StringComparison.Ordinal); if (k < 0 || line.IndexOf("_t.Add(", StringComparison.Ordinal) < 0) continue;
                    foreach (string id in off) if (line.IndexOf("\"Blueprints\", \"" + id + "\"", StringComparison.Ordinal) >= 0 || line.IndexOf("\"Blueprints\",\"" + id + "\"", StringComparison.Ordinal) >= 0) { lines[i] = line.Replace("_t.Add(", "_t.AddHidden("); hidden++; break; }
                }
                if (hidden == 0) return;
                string joined = string.Join("\n", lines);
                int lt = joined.IndexOf("local _t = {}", StringComparison.Ordinal);
                if (lt < 0) return;
                joined = joined.Substring(0, lt) + "local _t = {} _t.AddHidden = function(k, v) _t[k] = v end" + joined.Substring(lt + "local _t = {}".Length);
                __result = joined; Applied = (Applied.Length > 0 ? Applied + "; " : "") + f.Substring(f.LastIndexOf('/') + 1) + ": " + hidden;
                if (Log != null) Log("Items: hidden " + hidden + " item(s) in " + f + " (" + (Remote != null ? "host's list" : "own list") + ").");
            }
            catch (Exception e) { if (Log != null) Log("Items: filter failed: " + e.Message); }
        }

        static string FilterOperable(string code, string f)
        {
            HashSet<string> off = Effective; int n = 0;
            for (int i = 0; i < ClassKits.GetLength(0); i++)
            {
                if (!off.Contains("unit:" + ClassKits[i, 0])) continue; string r = "Recipe." + ClassKits[i, 0];
                string c2 = code.Replace(r + ", ", "").Replace(", " + r, "").Replace(r + ",", "").Replace(r, "");
                if (c2 != code) { code = c2; n++; }
            }
            if (n > 0) { Applied = (Applied.Length > 0 ? Applied + "; " : "") + "класи: " + n; if (Log != null) Log("Items: forbidden " + n + " brickton class kit(s) (" + (Remote != null ? "host's list" : "own list") + ")."); }
            return code;
        }

        // ---- catalog (read from the game's own Lua files) and the window with pictures ----
        static void LoadCatalog()
        {
            if (catalogTried) return; catalogTried = true;
            try
            {
                string dir = Path.Combine(BepInEx.Paths.GameRootPath, "Info/Lua/LUI/Meta");
                Regex rx = new Regex("\"Blueprints\",\\s*\"([^\"]+)\"\\)\\s*,\\s*\\{.*?GetLocalized\\(\"([^\"]*)\"\\).*?IconKeys\\.(\\w+)", RegexOptions.Compiled);
                for (int i = 0; i < Files.GetLength(0); i++)
                {
                    string path = Path.Combine(dir, Files[i, 0]); if (!File.Exists(path)) continue;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        if (line.TrimStart().StartsWith("--")) continue;
                        Match m = rx.Match(line); if (!m.Success) continue;
                        Entry e = new Entry(); e.Id = m.Groups[1].Value; e.LocKey = m.Groups[2].Value; e.IconField = m.Groups[3].Value; e.Section = Files[i, 1]; e.Name = Pretty(e.LocKey, e.Id);
                        bool dup = false; foreach (Entry x in catalog) if (x.Id == e.Id) { dup = true; break; }
                        if (!dup) catalog.Add(e);
                    }
                }
                for (int i = 0; i < ClassKits.GetLength(0); i++)
                {
                    Entry ce = new Entry(); ce.Id = "unit:" + ClassKits[i, 0]; ce.LocKey = ClassKits[i, 1]; ce.IconField = ClassKits[i, 2]; ce.Section = "Класи бріктронів (набір для крафту)"; ce.Name = Pretty(ce.LocKey, ClassKits[i, 0]); catalog.Add(ce);
                }
            }
            catch (Exception e) { if (Log != null) Log("Items: catalog failed: " + e.Message); }
        }
        static MethodInfo i2Translate; static bool i2Tried;
        static string Pretty(string locKey, string id)
        {
            try
            {
                if (!i2Tried)
                {
                    i2Tried = true; Type t = Type.GetType("Brix.Utils.I2Helper, Assembly-CSharp-firstpass");
                    if (t != null) i2Translate = t.GetMethod("TryGet", new Type[] { typeof(string) });
                }
                if (i2Translate != null && !string.IsNullOrEmpty(locKey))
                {
                    string r = i2Translate.Invoke(null, new object[] { locKey }) as string;
                    if (!string.IsNullOrEmpty(r) && r.IndexOf("gamemenu_", StringComparison.Ordinal) < 0 && !r.StartsWith("##")) return r;
                }
            }
            catch (Exception) { }
            string s = Regex.Replace(id.Replace('_', ' '), "([a-z])([A-Z])", "$1 $2"); return s;
        }
        static Sprite IconOf(Entry e)
        {
            if (e.IconTried) return e.Icon; e.IconTried = true;
            try
            {
                FieldInfo f = typeof(IconKeys).GetField(e.IconField, BindingFlags.Public | BindingFlags.Static);
                object key = f != null ? f.GetValue(null) : null;
                if (key != null) { MethodInfo g = key.GetType().GetMethod("Get64", Type.EmptyTypes); if (g != null) e.Icon = g.Invoke(key, null) as Sprite; }
            }
            catch (Exception) { }
            return e.Icon;
        }

        internal static bool CanEdit(bool isGuestOfHost) { return !isGuestOfHost; }

        // Returns true when the list was changed this frame (the plugin then saves and broadcasts it).
        internal static bool DrawWindow(bool lockedByHost)
        {
            if (!WindowOpen) return false;
            LoadCatalog();
            window = GUI.Window(227878, window, delegate(int id) { DrawBody(lockedByHost); GUI.DragWindow(new Rect(0, 0, 10000, 22)); }, "Предмети будівництва — вимкни те, що не потрібне");
            bool c = changed; changed = false; return c;
        }

        static void DrawBody(bool locked)
        {
            GUILayout.Space(2);
            if (locked) GUILayout.Label("Список обрав хост; змінювати його може лише хост. Він застосовується при завантаженні карти.");
            else GUILayout.Label("Клацни по предмету, щоб вимкнути його (зникне з меню будівництва; гравці з модом бачать вибір хоста). Застосується при завантаженні карти.");
            if (Applied.Length > 0) GUILayout.Label("У цій грі вже приховано: " + Applied);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Width(620), GUILayout.Height(440));
            string section = null; int col = 0; HashSet<string> set = Effective;
            GUILayout.BeginHorizontal();
            foreach (Entry e in catalog)
            {
                if (e.Section != section) { if (col > 0) { GUILayout.FlexibleSpace(); } GUILayout.EndHorizontal(); GUILayout.Label("— " + e.Section + " —"); GUILayout.BeginHorizontal(); section = e.Section; col = 0; }
                if (col == 6) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); col = 0; }
                bool off = set.Contains(e.Id);
                GUILayout.BeginVertical(GUILayout.Width(96));
                Rect r = GUILayoutUtility.GetRect(64, 64, GUILayout.Width(64), GUILayout.Height(64));
                Sprite sp = IconOf(e); Color old = GUI.color;
                if (sp != null && sp.texture != null)
                {
                    Rect tr = sp.textureRect; Texture2D tex = sp.texture; Rect uv = new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height);
                    GUI.color = off ? new Color(1, 1, 1, 0.25f) : Color.white; GUI.DrawTextureWithTexCoords(r, tex, uv);
                }
                else { GUI.color = off ? new Color(1, 1, 1, 0.3f) : Color.white; GUI.Box(r, "?"); }
                GUI.color = old;
                if (off) { GUI.color = new Color(1f, 0.2f, 0.2f, 1f); GUI.Label(new Rect(r.x + 18, r.y + 20, 40, 30), "✕"); GUI.color = old; }
                bool now = GUILayout.Toggle(!off, "", GUILayout.Width(20));
                GUILayout.Label(e.Name, GUILayout.Width(96));
                if (!locked && (now == off))
                {
                    HashSet<string> mine = new HashSet<string>(Local, StringComparer.Ordinal);
                    if (now) mine.Remove(e.Id); else mine.Add(e.Id);
                    Local = mine; if (cfg != null) cfg.Value = Join(mine); changed = true; ChangedAt = Time.realtimeSinceStartup;
                }
                if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition) && !locked)
                {
                    HashSet<string> mine = new HashSet<string>(Local, StringComparer.Ordinal);
                    if (off) mine.Remove(e.Id); else mine.Add(e.Id);
                    Local = mine; if (cfg != null) cfg.Value = Join(mine); changed = true; ChangedAt = Time.realtimeSinceStartup; Event.current.Use();
                }
                GUILayout.EndVertical(); col++;
            }
            GUILayout.FlexibleSpace(); GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (!locked && GUILayout.Button("Увімкнути всі")) { Local = new HashSet<string>(StringComparer.Ordinal); if (cfg != null) cfg.Value = ""; changed = true; }
            if (GUILayout.Button("Закрити")) WindowOpen = false;
            GUILayout.EndHorizontal();
        }
    }

    public sealed partial class SteamBridgePlugin
    {
        const byte KindItemFilter = 14;
        float nextItemSend; string lastItemSent = "";
        bool PeerHasItemFilter(CSteamID id) { string v; return peerVersions.TryGetValue(id, out v) && UpdateVerifier.IsNewer(v, "0.3.52"); }

        void InstallItemFilter()
        {
            try { ItemFilter.Log = delegate(string s) { Logger.LogInfo(s); }; ItemFilter.Bind(Config); ItemFilter.Install(discoveryHarmony); }
            catch (Exception e) { Logger.LogWarning("Item filter unavailable: " + e.Message); }
        }

        bool ItemFilterLocked { get { return !hosting && bridge != null && ItemFilter.Remote != null; } }

        void ItemFilterGui()
        {
            try { if (ItemFilter.DrawWindow(ItemFilterLocked)) nextItemSend = 0f; } catch (Exception) { }
        }

        void ItemFilterTick()
        {
            float now = Time.realtimeSinceStartup;
            if (!hosting && bridge == null && ItemFilter.Remote != null) { ItemFilter.Remote = null; }
            if (!hosting || now < nextItemSend) return;
            nextItemSend = now + 10f;
            string text = ItemFilter.Join(ItemFilter.Local); byte[] payload = Encoding.UTF8.GetBytes(text);
            foreach (KeyValuePair<CSteamID, BridgeCore> g in guests)
                if (PeerHasItemFilter(g.Key)) { try { SendTo(g.Key, BridgeCore.Packet(KindItemFilter, payload, payload.Length)); } catch (Exception) { } }
        }

        void OnItemFilterPacket(CSteamID from, byte[] payload)
        {
            if (hosting || bridge == null || from != peer || payload == null || payload.Length > 4096) return;
            string text = Encoding.UTF8.GetString(payload); HashSet<string> set = ItemFilter.Parse(text);
            if (ItemFilter.Remote == null || ItemFilter.Join(ItemFilter.Remote) != ItemFilter.Join(set)) Logger.LogInfo("Items: host's list received (" + set.Count + " switched off).");
            ItemFilter.Remote = set;
        }
    }
}
