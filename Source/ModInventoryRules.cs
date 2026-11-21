using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CastleStorySteam
{
    // MOD-INV (bug-intake, 0.4.14): the pure decisions of ModInventory, without Unity, BepInEx or game types, so ModInventoryTests (and Make-ModBaseline.ps1, which compiles this
    // file) use exactly the same rules as the plugin: which game files are in the data scope, the baseline manifest (unpack, parse, compare with our own known variants),
    // MODS-HASH, the lobby compare, the Lua anchor book, Workshop item kinds and the privacy of paths.
    internal static class ModInventoryRules
    {
        internal const int DetailLimit = 30;   // MOD-DATA / MOD-CONFLICT / plugin / workshop lines written in full, then one count line
        internal const int LineMax = 270;      // the friend report cuts lines at 300 characters, the perf-trace time prefix takes about 25

        // ---- data scope (the same list Make-ModBaseline.ps1 uses for the baseline) --------------------------------------------------------------------------
        // In scope: everything under Castle Story_Data/Managed, Info/Lua, Info/Languages, Info/Checksum, Info/Livre, and the files directly in Info. Out: user and run-time data,
        // our backup files, our _removed-by-claude folders. Never a whole folder under Lua/Mods or Lua/Gamemodes: the game loads every folder there.
        static readonly string[] Recursive = { "castle story_data/managed/", "info/lua/", "info/languages/", "info/checksum/", "info/livre/" };
        static readonly string[] SkipDirs = { "saves", "ugc", "downloads", "usersettings", "backups", "screenshots" };
        static readonly string[] SkipNames = { "userconfigs.lua", "stats.json", "test_result.txt", "timestamp.txt", "game_analytics_events.json", "output_log.txt", ".ds_store", "thumbs.db", "desktop.ini" };
        static readonly string[] SkipSuffixes = { ".orig", ".tmp", ".log", ".bytecode", ".codex-patched" };
        static readonly string[] SkipMarkers = { ".bak", ".old", ".disabled", ".before-", ".backup-" };

        internal static string Normalize(string rel)
        {
            if (rel == null) return "";
            string r = rel.Replace('\\', '/');
            while (r.StartsWith("/", StringComparison.Ordinal)) r = r.Substring(1);
            return r;
        }

        internal static string Key(string rel) { return Normalize(rel).ToLowerInvariant(); }

        internal static bool InScope(string rel)
        {
            string r = Key(rel);
            if (r.Length == 0 || Excluded(r)) return false;
            for (int i = 0; i < Recursive.Length; i++) if (r.StartsWith(Recursive[i], StringComparison.Ordinal)) return true;
            return r.StartsWith("info/", StringComparison.Ordinal) && r.IndexOf('/', 5) < 0 && r.Length > 5;
        }

        internal static bool Excluded(string rel)
        {
            string r = Key(rel);
            string[] parts = r.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                for (int k = 0; k < SkipDirs.Length; k++) if (parts[i] == SkipDirs[k]) return true;
                if (parts[i].StartsWith("_removed-by-claude", StringComparison.Ordinal)) return true;
            }
            string name = parts[parts.Length - 1];
            for (int k = 0; k < SkipNames.Length; k++) if (name == SkipNames[k]) return true;
            for (int k = 0; k < SkipSuffixes.Length; k++) if (name.EndsWith(SkipSuffixes[k], StringComparison.Ordinal)) return true;
            for (int k = 0; k < SkipMarkers.Length; k++) if (HasMarker(name, SkipMarkers[k])) return true;
            return false;
        }

        // ".bak" matches "x.bak", "x.lua.bak-0399", "x.bak-orange", but not "x.bakery"; ".old" does not match "x.older.lua"; markers ending in '-' match anywhere.
        static bool HasMarker(string name, string marker)
        {
            if (marker.EndsWith("-", StringComparison.Ordinal)) return name.IndexOf(marker, StringComparison.Ordinal) >= 0;
            return name.EndsWith(marker, StringComparison.Ordinal) || name.IndexOf(marker + "-", StringComparison.Ordinal) >= 0 || name.IndexOf(marker + ".", StringComparison.Ordinal) >= 0;
        }

        // Cosmetic or self-written files: logged, but kept out of MODS-HASH (translations are text only; the game may rewrite its LOD profile).
        internal static bool CountsForModsHash(string rel)
        {
            string r = Key(rel);
            return !r.StartsWith("info/languages/", StringComparison.Ordinal) && r != "info/lodmanagersettings.json";
        }

        // ---- the baseline manifest ---------------------------------------------------------------------------------------------------------------------------
        // Text format (Make-ModBaseline.ps1): "#version v1.8.8", "#firstpass CF5B5406", base lines "relpath|HASH8", our own known variants "+relpath|HASH8".
        // Stored deflated + base64 in ModBaseline.cs (a C# string is UTF-16 and would double the size).
        internal static string Pack(string text)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    byte[] raw = Encoding.UTF8.GetBytes(text);
                    ds.Write(raw, 0, raw.Length);
                }
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        internal static string Unpack(string packed)
        {
            byte[] data = Convert.FromBase64String(packed);
            using (MemoryStream ms = new MemoryStream(data))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (StreamReader sr = new StreamReader(ds, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        internal static ModManifest Parse(string text)
        {
            ModManifest m = new ModManifest();
            if (text == null) return m;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#version ", StringComparison.Ordinal)) { m.Version = line.Substring(9).Trim(); continue; }
                if (line.StartsWith("#firstpass ", StringComparison.Ordinal)) { m.Firstpass = line.Substring(11).Trim().ToUpperInvariant(); continue; }
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                bool ours = line.StartsWith("+", StringComparison.Ordinal);
                if (ours) line = line.Substring(1);
                int bar = line.LastIndexOf('|');
                if (bar <= 0) continue;
                string rel = Normalize(line.Substring(0, bar)), hash = line.Substring(bar + 1).Trim().ToUpperInvariant(), key = rel.ToLowerInvariant();
                if (ours)
                {
                    List<string> list;
                    if (!m.Ours.TryGetValue(key, out list)) { list = new List<string>(); m.Ours[key] = list; }
                    if (!list.Contains(hash)) list.Add(hash);
                }
                else { m.Base[key] = hash; m.Display[key] = rel; }
            }
            return m;
        }

        // A present file: same as the baseline, one of our own known variants, changed (baseline has another hash) or added (not in the baseline).
        internal static DataState Classify(ModManifest m, string rel, string hash8)
        {
            string key = Key(rel), h = (hash8 ?? "").ToUpperInvariant();
            string b;
            bool inBase = m.Base.TryGetValue(key, out b);
            if (inBase && b == h) return DataState.Same;
            List<string> ours;
            if (m.Ours.TryGetValue(key, out ours) && ours.Contains(h)) return DataState.Ours;
            return inBase ? DataState.Changed : DataState.Added;
        }

        // Baseline files that are not present (keys of the present files, lower case).
        internal static List<string> Missing(ModManifest m, ICollection<string> presentKeys)
        {
            List<string> missing = new List<string>();
            foreach (KeyValuePair<string, string> pair in m.Base) if (!presentKeys.Contains(pair.Key)) missing.Add(m.Display[pair.Key]);
            missing.Sort(StringComparer.OrdinalIgnoreCase);
            return missing;
        }

        // Does the installed game match the build the baseline was made from? (else one line instead of hundreds of 'changed')
        internal static bool SameBuild(ModManifest m, string version, string firstpassHash8)
        {
            return m.Version.Length > 0 && m.Firstpass.Length > 0 && string.Equals(m.Version, (version ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Firstpass, (firstpassHash8 ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // ---- hashes ------------------------------------------------------------------------------------------------------------------------------------------
        internal static string Hash8(byte[] data)
        {
            using (MD5 md5 = MD5.Create()) return Hex8(md5.ComputeHash(data ?? new byte[0]));
        }

        internal static string Hash8(string text) { return Hash8(Encoding.UTF8.GetBytes(text ?? "")); }

        internal static string Hex8(byte[] digest)
        {
            StringBuilder sb = new StringBuilder(8);
            for (int i = 0; i < 4 && i < digest.Length; i++) sb.Append(digest[i].ToString("X2"));
            return sb.ToString();
        }

        // MODS-HASH: one short hash over the sorted plugin entries and the sorted data entries that count (not ours, not cosmetic). Equal installs give equal hashes,
        // whatever the order the files were found in.
        internal static string ModsHash(IEnumerable<string> plugins, IEnumerable<string> data)
        {
            List<string> p = new List<string>(plugins ?? new string[0]), d = new List<string>(data ?? new string[0]);
            p.Sort(StringComparer.Ordinal); d.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            foreach (string s in p) sb.Append("P:").Append(s).Append('\n');
            foreach (string s in d) sb.Append("D:").Append(s).Append('\n');
            return Hash8(sb.ToString());
        }

        // ---- lobby -------------------------------------------------------------------------------------------------------------------------------------------
        internal static string LobbyVerdict(string host, string mine)
        {
            if (string.IsNullOrEmpty(host)) return "n/a";
            if (string.IsNullOrEmpty(mine)) return "pending";
            return string.Equals(host.Trim(), mine.Trim(), StringComparison.OrdinalIgnoreCase) ? "match" : "mismatch";
        }

        // ---- Lua anchors -------------------------------------------------------------------------------------------------------------------------------------
        internal static int AnchorCount(string code, string anchor)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(anchor)) return 0;
            int n = 0, i = 0;
            while ((i = code.IndexOf(anchor, i, StringComparison.Ordinal)) >= 0) { n++; i += anchor.Length; if (n > 99) break; }
            return n;
        }

        internal static string AnchorState(int count, bool mustBeUnique)
        {
            if (count <= 0) return "NOT FOUND";
            if (mustBeUnique && count > 1) return "NOT UNIQUE (" + count + ")";
            return "found";
        }

        // ---- Workshop item kind from its file names (lower case) ----------------------------------------------------------------------------------------------
        internal static string WorkshopKind(ICollection<string> names)
        {
            if (names == null || names.Count == 0) return "empty";
            if (names.Contains("map_validator.lua") || names.Contains("factions.lua")) return "gamemode";
            if (names.Contains("clientside.lua") || names.Contains("serverside.lua")) return "mod";
            if (names.Contains("gameobjects.json")) return "map";
            if (names.Contains("graph.json")) return "blueprint";
            if (names.Contains("language.csv")) return "language";
            foreach (string n in names) if (n.EndsWith(".json", StringComparison.Ordinal)) return "preset";
            return "other";
        }

        // Mods, game modes and languages run or show in every game: hash them; maps and blueprints are only counted.
        internal static bool WorkshopHashed(string kind) { return kind == "mod" || kind == "gamemode" || kind == "language"; }

        // ---- privacy -----------------------------------------------------------------------------------------------------------------------------------------
        // A path relative to the game folder; outside it only the file name (never a full path with a user name).
        internal static string RelativeOrName(string full, string root)
        {
            if (string.IsNullOrEmpty(full)) return "";
            string f = full.Replace('\\', '/'), r = (root ?? "").Replace('\\', '/').TrimEnd('/');
            if (r.Length > 0 && f.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)) return f.Substring(r.Length + 1);
            int s = f.LastIndexOf('/');
            return s >= 0 ? f.Substring(s + 1) : f;
        }

        internal static string Cut(string line)
        {
            if (line == null) return "";
            return line.Length <= LineMax ? line : line.Substring(0, LineMax - 3) + "...";
        }
    }

    internal enum DataState { Same, Ours, Changed, Added }

    internal sealed class ModManifest
    {
        internal string Version = "", Firstpass = "";
        internal readonly Dictionary<string, string> Base = new Dictionary<string, string>();               // lower-case path -> HASH8
        internal readonly Dictionary<string, string> Display = new Dictionary<string, string>();            // lower-case path -> the path as written
        internal readonly Dictionary<string, List<string>> Ours = new Dictionary<string, List<string>>();  // lower-case path -> our known HASH8 variants
    }

    // Lua anchors of our text patches: the first result of each anchor is logged, later only a change (the game loads the same files again and again).
    internal sealed class AnchorBook
    {
        readonly Dictionary<string, string> state = new Dictionary<string, string>();

        // true = write a line (first sight, or the state changed)
        internal bool Note(string key, string newState)
        {
            string old;
            if (state.TryGetValue(key, out old) && old == newState) return false;
            state[key] = newState;
            return true;
        }

        internal int Count { get { return state.Count; } }

        internal int Bad
        {
            get { int n = 0; foreach (KeyValuePair<string, string> p in state) if (p.Value != "found") n++; return n; }
        }

        internal List<string> BadKeys()
        {
            List<string> list = new List<string>();
            foreach (KeyValuePair<string, string> p in state) if (p.Value != "found") list.Add(p.Key + " " + p.Value);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }
}
