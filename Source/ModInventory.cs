using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Brix.Engine;
using Brix.External.Signals;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CastleStorySteam
{
    // MOD-INV (bug-intake, Standalone-0.4.14; lead's addendum 2, user: "зробити підтримку модів щоб було менше проблем з вставкою всяких модів" / "щоб була підтримка
    // любого мода"). 'Any mod' cannot be guaranteed: other mods change Lua and data or patch the same methods, and every player needs the same data mods. This makes those
    // problems VISIBLE: LOG ONLY, nothing is changed, a join is never blocked. cfg [Diagnostics] ModInventory (default true).
    //   scan 1 (2 s after the first frame, when every BepInEx plugin has loaded and patched; main thread for the Chainloader and Harmony, a worker thread for files):
    //     MOD-INV plugin / patcher lines (GUID, name, version, file relative to the game, MD5 first 8), MOD-CONFLICT (a method WE patch that another mod patches too: its owner
    //     and kinds; ours = a patch method from this assembly, so the user's other 'local.castlestory.*' mod counts as foreign), MOD-DATA (game data vs the baseline built from the
    //     pristine copy, ModBaseline.cs: changed / added / missing, our own known variants not reported), MOD-INV workshop / ugc (Lua mods, modes and languages hashed; maps and
    //     blueprints counted), MOD-INV self-off (which of our parts disabled themselves), MODS-HASH (plugins + data changes that matter, shown in F8, sent in every friend report,
    //     published as the Steam lobby key csb_mods).
    //   scan 2 (20 s after the first match scene): Harmony again (mods that patch at match start), the Lua anchor summary, the self-off summary.
    //   MOD-ANCHOR: every Lua text anchor of our in-memory patches (ItemFilter, the orange ward), found / NOT FOUND / NOT UNIQUE, logged on first sight and on change.
    //   MOD-MATCH / MOD-MISMATCH: a guest compares the host's csb_mods with its own once per lobby (log only; friends who join through rich presence get 'n/a').
    // Privacy: no account names, paths relative to the game folder (outside it the file name only), names through BugPackRules.SafeName.
    internal static class ModInventory
    {
        static ConfigEntry<bool> on;
        static ManualLogSource logger;
        static GameObject ticker;
        static string gameRoot = "";
        static readonly object gate = new object();
        static readonly AnchorBook anchors = new AnchorBook();
        static readonly HashSet<ulong> lobbiesChecked = new HashSet<ulong>();
        static int stage, faults;
        static float scan1At = -1f, scan2At = -1f;
        static bool scan2Done;
        static volatile Scan worker;
        static readonly List<string> pluginFiles = new List<string>(), pluginEntries = new List<string>();
        static readonly HashSet<string> conflictKeys = new HashSet<string>();
        static int conflicts, foreignMethods;
        static string selfOff = "none", pendingLobbyHost = "", pendingLobbyRoute = "";
        static ulong pendingLobby;

        internal static volatile string Hash = "";        // MODS-HASH, empty until scan 1 has finished
        internal static volatile string F8Line = "";      // the F8 label (built once, never in OnGUI)
        internal static volatile string ReportLine = "";  // added to every friend report (SessionShare)

        internal static void Install(ConfigFile config, ManualLogSource log)
        {
            on = config.Bind("Diagnostics", "ModInventory", true, "LOG ONLY, no gameplay effect: lists other BepInEx plugins and patchers, Harmony conflicts on methods this mod patches (MOD-CONFLICT), game data changed against the pristine game (MOD-DATA), Workshop / UGC Lua mods, our Lua text anchors (MOD-ANCHOR) and which of our parts disabled themselves; a short MODS-HASH is shown in F8, sent in the friend report and published in the Steam lobby (csb_mods), and a guest logs MOD-MISMATCH when the host's differs. Never blocks a join. false = nothing is scanned or written.");
            logger = log;
            gameRoot = Paths.GameRootPath ?? "";
            if (ticker != null) return;
            try { GameSignals.Connect(GameSignals.LevelReady, OnLevelReady); } catch (Exception e) { Write("MOD-INV: no level-ready signal, scan 2 off: " + e.GetType().Name); }
            ticker = new GameObject("SteamBridge ModInventory");
            ticker.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(ticker);
            ticker.AddComponent<ModInventoryTicker>();
        }

        // scan 2 runs 20 s after the level is READY (the scene switches about 2 minutes earlier, before Lua menus and match-start patches)
        static void OnLevelReady()
        {
            try { if (scan2At < 0f) scan2At = Time.realtimeSinceStartup + 20f; } catch (Exception) { }
        }

        static bool On()
        {
            try { return on != null && on.Value; }
            catch (Exception) { return false; }
        }

        static void Write(string line)
        {
            string l = ModInventoryRules.Cut(line);
            try { SteamBridgePlugin.PtLog(l); } catch (Exception) { }
            try { if (logger != null) logger.LogInfo(l); } catch (Exception) { }
        }

        static void Fault(string where, Exception e)
        {
            faults++;
            if (faults <= 5) Write("MOD-INV error in " + where + " (log only): " + e.GetType().Name + ": " + e.Message);
        }

        internal static void Tick()
        {
            try
            {
                if (!On()) return;
                float now = Time.realtimeSinceStartup;
                if (stage == 0)
                {
                    if (scan1At < 0f) scan1At = now + 2f;
                    if (now >= scan1At) { stage = 1; Scan1(); }
                    return;
                }
                if (stage == 1)
                {
                    Scan done = worker;
                    if (done != null) { stage = 2; Finish1(done); }
                    return;
                }
                if (!scan2Done)
                {
                    if (scan2At >= 0f && now >= scan2At) { scan2Done = true; Scan2(); }
                }
            }
            catch (Exception e) { Fault("tick", e); }
        }

        // ---- scan 1, main thread: plugins, patchers, Harmony; then the files on a worker -------------------------------------------------------------------------
        static void Scan1()
        {
            Assembly ours = typeof(ModInventory).Assembly;
            List<PluginRow> rows = new List<PluginRow>();
            try
            {
                foreach (PluginInfo info in Chainloader.PluginInfos.Values)
                {
                    if (info == null || info.Metadata == null) continue;
                    PluginRow r = new PluginRow();
                    r.Guid = BugPackRules.SafeName(info.Metadata.GUID);
                    r.Name = BugPackRules.SafeName(info.Metadata.Name);
                    r.Version = info.Metadata.Version == null ? "?" : info.Metadata.Version.ToString();
                    r.Path = info.Location ?? "";
                    r.Ours = info.Instance != null && info.Instance.GetType().Assembly == ours;
                    rows.Add(r);
                }
            }
            catch (Exception e) { Fault("plugin list", e); }
            List<string> patchers = new List<string>();
            try { if (Directory.Exists(Paths.PatcherPluginPath)) patchers.AddRange(Directory.GetFiles(Paths.PatcherPluginPath, "*.dll", SearchOption.AllDirectories)); }
            catch (Exception e) { Fault("patcher list", e); }
            int depErrors = 0;
            try { depErrors = Chainloader.DependencyErrors == null ? 0 : Chainloader.DependencyErrors.Count; } catch (Exception) { }
            string loader = "?", harmonyVer = "?";
            try { loader = typeof(Chainloader).Assembly.GetName().Version.ToString(); harmonyVer = typeof(Harmony).Assembly.GetName().Version.ToString(); } catch (Exception) { }
            Write("MOD-INV start: BepInEx " + loader + ", HarmonyX " + harmonyVer + ", " + rows.Count + " plugin(s), " + patchers.Count + " patcher(s)" + (depErrors > 0 ? ", " + depErrors + " BepInEx dependency error(s) at load (LogOutput.log 'Could not load')" : "") + "; hashing files in the background");
            HarmonyScan(false);
            Scan job = new Scan();
            job.Root = gameRoot; job.Plugins = rows; job.Patchers = patchers;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                Scan s = (Scan)state;
                try { s.Run(); } catch (Exception e) { s.Error = e.GetType().Name + ": " + e.Message; }
                worker = s;
            }, job);
        }

        // Every method WE patch that another owner patches too. ours = the patch method lives in this assembly (our ten Harmony ids, never another 'local.castlestory.*' mod).
        static void HarmonyScan(bool second)
        {
            Assembly ours = typeof(ModInventory).Assembly;
            Dictionary<string, int> foreignOwners = new Dictionary<string, int>();
            Dictionary<string, int> ourOwners = new Dictionary<string, int>();
            int written = 0, fresh = 0;
            foreignMethods = 0;
            foreach (MethodBase m in Harmony.GetAllPatchedMethods())
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(m); } catch (Exception) { continue; }
                if (info == null) continue;
                List<string> mine = new List<string>(), other = new List<string>();
                Collect(info.Prefixes, "pre", ours, mine, other);
                Collect(info.Postfixes, "post", ours, mine, other);
                Collect(info.Transpilers, "trans", ours, mine, other);
                Collect(info.Finalizers, "fin", ours, mine, other);
                Collect(info.ILManipulators, "il", ours, mine, other);
                foreach (string o in mine) { string id = o.Substring(0, o.IndexOf(':')); int c; ourOwners.TryGetValue(id, out c); ourOwners[id] = c + 1; }
                if (other.Count == 0) continue;
                if (mine.Count == 0)
                {
                    foreignMethods++;
                    foreach (string o in other) { string id = o.Substring(0, o.IndexOf(':')); int c; foreignOwners.TryGetValue(id, out c); foreignOwners[id] = c + 1; }
                    continue;
                }
                string name = (m.DeclaringType == null ? "?" : m.DeclaringType.Name) + "." + m.Name;
                string others = string.Join(" ", other.ToArray());
                string key = name + "|" + others;
                bool isNew;
                lock (gate) isNew = conflictKeys.Add(key);
                if (!isNew) continue;
                fresh++;
                conflicts++;
                if (written++ < ModInventoryRules.DetailLimit) Write("MOD-CONFLICT " + (second ? "new at match start: " : "") + name + " ours=" + Short(string.Join(",", Owners(mine).ToArray())) + " other=" + others);
            }
            if (written > ModInventoryRules.DetailLimit) Write("MOD-CONFLICT ... " + (written - ModInventoryRules.DetailLimit) + " more not listed");
            List<string> fo = new List<string>();
            foreach (KeyValuePair<string, int> p in foreignOwners) fo.Add(p.Key + "(" + p.Value + ")");
            fo.Sort(StringComparer.Ordinal);
            Write("MOD-INV harmony" + (second ? " at match start" : "") + ": conflicts with our patches " + (second ? fresh + " new, " : "") + conflicts + " in all; methods patched only by other mods " + foreignMethods
                + (fo.Count > 0 ? " by " + string.Join(" ", fo.ToArray()) : "") + "; our owners " + ourOwners.Count);
            lastOurOwners = ourOwners;
        }

        static Dictionary<string, int> lastOurOwners = new Dictionary<string, int>();

        static void Collect(IEnumerable<Patch> patches, string kind, Assembly ours, List<string> mine, List<string> other)
        {
            if (patches == null) return;
            foreach (Patch p in patches)
            {
                if (p == null) continue;
                bool isOurs = false;
                try { isOurs = p.PatchMethod != null && p.PatchMethod.DeclaringType != null && p.PatchMethod.DeclaringType.Assembly == ours; } catch (Exception) { }
                string owner = isOurs ? (p.owner ?? "?") : BugPackRules.SafeName(p.owner ?? "?");   // our ids raw (SafeName cuts at 40 characters)
                string entry = owner + ":" + kind;
                if (isOurs) { if (!mine.Contains(entry)) mine.Add(entry); }
                else if (!other.Contains(entry)) other.Add(entry);
            }
        }

        static List<string> Owners(List<string> entries)
        {
            List<string> ids = new List<string>();
            foreach (string e in entries) { string id = e.Substring(0, e.IndexOf(':')); if (!ids.Contains(id)) ids.Add(id); }
            return ids;
        }

        static string Short(string owners) { return owners.Replace("local.castlestory.steambridge.", "").Replace("local.castlestory.", ""); }

        // ---- scan 1 results, main thread ------------------------------------------------------------------------------------------------------------------------
        static void Finish1(Scan s)
        {
            if (s.Error != null) Write("MOD-INV scan failed (log only): " + s.Error);
            foreach (string line in s.Lines) Write(line);
            pluginEntries.Clear(); pluginEntries.AddRange(s.PluginEntries);
            selfOff = SelfOff();
            Write("MOD-INV self-off: " + selfOff);
            Hash = ModInventoryRules.ModsHash(s.PluginEntries, s.DataEntries);
            f8Base = "Моди: " + Hash + " (" + s.PluginCount + " плагінів, " + s.DataCountForHash + " змін даних" + (conflicts > 0 ? ", конфліктів " + conflicts : "") + ")";
            F8Line = f8Base;
            ReportLine = Summary(s, false);
            Write(ReportLine);
            if (pendingLobby != 0) { ulong id = pendingLobby; pendingLobby = 0; Verdict(id, pendingLobbyHost, pendingLobbyRoute); }
            lastScan = s;
        }

        static Scan lastScan;
        static string f8Base = "";

        static string Summary(Scan s, bool second)
        {
            return "MODS-HASH " + Hash + " plugins=" + s.PluginCount + " patchers=" + s.PatcherCount + " data=" + s.DataCountForHash + " (changed " + s.Changed + " added " + s.Added + " missing " + s.Missing + " ours "
                + s.Ours + (s.SameBuild ? "" : " BASELINE-NOT-THIS-BUILD") + ") workshop-mods=" + s.WorkshopMods + " conflicts=" + conflicts + " anchors-bad=" + anchors.Bad + "/" + anchors.Count + " self-off=" + (selfOff == "none" ? 0 : selfOff.Split(',').Length)
                + " scan2=" + (second ? "done" : "pending");
        }

        // ---- scan 2: 20 s after the first match scene ------------------------------------------------------------------------------------------------------------
        static void Scan2()
        {
            HarmonyScan(true);
            List<string> bad;
            int count;
            lock (gate) { bad = anchors.BadKeys(); count = anchors.Count; }
            Write("MOD-INV anchors: " + count + " checked, " + (count - bad.Count) + " found, " + bad.Count + " bad" + (bad.Count > 0 ? ": " + string.Join("; ", bad.ToArray()) : "")
                + (OrangeWardRecipe.ModInvStatus() == "ok" ? "" : " (orange ward " + OrangeWardRecipe.ModInvStatus() + ": its anchors are not checked)"));
            selfOff = SelfOff();
            Write("MOD-INV self-off at match start: " + selfOff);
            if (lastScan != null) { ReportLine = Summary(lastScan, true); Write(ReportLine); }
        }

        // Which of our parts disabled themselves (an install failure or a missing hook), apart from switches that are off by config.
        static string SelfOff()
        {
            List<string> off = new List<string>();
            try
            {
                if (SteamBridgePlugin.ModInvDiagOnly()) off.Add("diag-only mode (gameplay and network parts off by design)");
                else
                {
                    string[] expected = { "local.castlestory.steambridge.discovery", "local.castlestory.steambridge.gameplay", "local.castlestory.steambridge.imprint", "local.castlestory.steambridge.legacymodblock", "local.castlestory.steambridge.loggerassemblies", "local.castlestory.steambridge.savepreview" };
                    foreach (string id in expected) { int n; if (!lastOurOwners.TryGetValue(id, out n) || n == 0) off.Add(Short(id) + " (no patches)"); }
                }
                if (BugPack.Skipped.Count > 0) off.Add("bugpack: " + string.Join("/", BugPack.Skipped.ToArray()));
                if (CombatAnomaly.InstallStatus.IndexOf("SKIPPED", StringComparison.Ordinal) >= 0) off.Add("combat log part");
                string ow = SteamBridgePlugin.ModInvDiagOnly() ? "ok" : OrangeWardRecipe.ModInvStatus();   // diag-only: the gameplay pack is never built
                if (ow != "ok" && ow != "off by config" && ow != "lua not exposed yet") off.Add("orange ward recipe (" + ow + ")");
                if (!SteamBridgePlugin.ModInvPerfTraceOn()) off.Add("perf-trace (off or failed: MOD lines only in LogOutput.log)");
            }
            catch (Exception e) { off.Add("? (" + e.GetType().Name + ")"); }
            return off.Count == 0 ? "none" : string.Join(", ", off.ToArray());
        }

        // ---- Lua anchors (called by the LoadFile postfixes of ItemFilter and the orange ward) -------------------------------------------------------------------
        internal static void Anchor(string feature, string luaFile, string anchorName, int count, bool mustBeUnique)
        {
            try
            {
                if (!On()) return;
                string state = ModInventoryRules.AnchorState(count, mustBeUnique);
                string key = feature + " " + luaFile + " '" + anchorName + "'";
                bool write;
                lock (gate) write = anchors.Note(key, state);
                if (write) Write("MOD-ANCHOR " + key + " " + state + (state == "found" ? "" : ": our text patch is a no-op (a mod changed this file?)"));
                if (write && lastScan != null) ReportLine = Summary(lastScan, scan2Done);
            }
            catch (Exception) { }
        }

        // ---- the Steam lobby key -------------------------------------------------------------------------------------------------------------------------------
        // Guest side: compare the host's csb_mods with ours once per lobby. Log only; the join goes on whatever the result.
        internal static void CheckLobby(CSteamID lobby, string route)
        {
            try
            {
                if (!On() || lobby.m_SteamID == 0) return;
                lock (gate) { if (lobbiesChecked.Contains(lobby.m_SteamID)) return; lobbiesChecked.Add(lobby.m_SteamID); }
                string host = SteamMatchmaking.GetLobbyData(lobby, "csb_mods");
                if (Hash.Length == 0 && !string.IsNullOrEmpty(host)) { pendingLobby = lobby.m_SteamID; pendingLobbyHost = host; pendingLobbyRoute = route; return; }
                Verdict(lobby.m_SteamID, host, route);
            }
            catch (Exception e) { Fault("lobby check", e); }
        }

        static void Verdict(ulong lobby, string host, string route)
        {
            string v = ModInventoryRules.LobbyVerdict(host, Hash);
            if (v == "match") { Write("MOD-MATCH host=mine=" + Hash + " route=" + route); F8Line = f8Base; }
            else if (v == "mismatch")
            {
                Write("MOD-MISMATCH host=" + BugPackRules.SafeName(host) + " mine=" + Hash + " route=" + route + " (log only: different plugins or game data; compare both MOD-INV lists)");
                F8Line = f8Base + "; хост: " + BugPackRules.SafeName(host) + " (різні моди!)";
            }
            else Write("MOD-LOBBY n/a route=" + route + " (the host publishes no csb_mods: an older bridge, AllowStrangers off, or a friend join through rich presence)");
        }

        // ---- the worker: files only (no Unity API here) ----------------------------------------------------------------------------------------------------------
        sealed class PluginRow { internal string Guid, Name, Version, Path; internal bool Ours; }

        sealed class Scan
        {
            internal string Root = "", Error;
            internal List<PluginRow> Plugins = new List<PluginRow>();
            internal List<string> Patchers = new List<string>();
            internal readonly List<string> Lines = new List<string>(), PluginEntries = new List<string>(), DataEntries = new List<string>();
            internal int PluginCount, PatcherCount, Changed, Added, Missing, Ours, Same, WorkshopMods, DataCountForHash;
            internal bool SameBuild = true;
            readonly byte[] buffer = new byte[65536];

            internal void Run()
            {
                foreach (PluginRow r in Plugins)
                {
                    string h = HashFile(r.Path), rel = ModInventoryRules.RelativeOrName(r.Path, Root);
                    PluginCount++;
                    PluginEntries.Add(r.Guid + "@" + r.Version + "#" + h);
                    if (PluginCount <= ModInventoryRules.DetailLimit) Lines.Add("MOD-INV plugin " + r.Guid + " '" + r.Name + "' v" + r.Version + " " + rel + " " + h + (r.Ours ? " ours" : ""));
                }
                foreach (string p in Patchers)
                {
                    string h = HashFile(p), rel = ModInventoryRules.RelativeOrName(p, Root);
                    PatcherCount++;
                    string pn = Path.GetFileName(p).ToLowerInvariant();
                    bool ownPatcher = pn == "castlestoryworkshopupdate.dll" || pn == "castlestorynetworktimeout.dll";
                    PluginEntries.Add("patcher:" + pn + "#" + (ownPatcher ? "ours" : h));   // our own patchers: presence only (their builds differ per PC)
                    if (PatcherCount <= ModInventoryRules.DetailLimit) Lines.Add("MOD-INV patcher " + rel + " " + h);
                }
                Data();
                Workshop();
                Ugc();
            }

            void Data()
            {
                ModManifest m = ModInventoryRules.Parse(ModInventoryRules.Unpack(ModBaseline.Packed));
                string version = "";
                try { version = File.ReadAllText(Path.Combine(Root, "version")).Trim(); } catch (Exception) { }
                string firstpass = HashFile(Path.Combine(Path.Combine(Path.Combine(Root, "Castle Story_Data"), "Managed"), "Assembly-CSharp-firstpass.dll"));
                SameBuild = ModInventoryRules.SameBuild(m, version, firstpass);
                if (!string.Equals(m.Version, version, StringComparison.OrdinalIgnoreCase))
                {
                    Lines.Add("MOD-DATA baseline is for " + m.Version + ", this game is " + BugPackRules.SafeName(version) + " (firstpass " + firstpass + "): data check skipped; rebuild ModBaseline.cs from a pristine copy");
                    DataEntries.Add("build " + BugPackRules.SafeName(version) + "#" + firstpass); DataCountForHash++;
                    return;
                }
                if (!SameBuild) Lines.Add("MOD-DATA Assembly-CSharp-firstpass.dll differs from " + m.Version + " (" + firstpass + " vs " + m.Firstpass + "): a modded game DLL; compared file by file below");
                HashSet<string> present = new HashSet<string>();
                int listed = 0;
                foreach (string top in new string[] { Path.Combine(Path.Combine(Root, "Castle Story_Data"), "Managed"), Path.Combine(Root, "Info") })
                {
                    if (!Directory.Exists(top)) continue;
                    foreach (string f in Directory.GetFiles(top, "*", SearchOption.AllDirectories))
                    {
                        string rel = ModInventoryRules.RelativeOrName(f, Root);
                        if (!ModInventoryRules.InScope(rel)) continue;
                        string key = ModInventoryRules.Key(rel), h = HashFile(f);
                        present.Add(key);
                        DataState st = ModInventoryRules.Classify(m, rel, h);
                        if (st == DataState.Same) { Same++; continue; }
                        if (st == DataState.Ours) { Ours++; continue; }
                        if (st == DataState.Changed) Changed++; else Added++;
                        string kind = st == DataState.Changed ? "changed" : "added";
                        if (ModInventoryRules.CountsForModsHash(rel)) { DataEntries.Add(kind + " " + key + "#" + h); DataCountForHash++; }
                        if (listed++ < ModInventoryRules.DetailLimit) Lines.Add("MOD-DATA " + kind + " " + rel + " " + h);
                    }
                }
                foreach (string rel in ModInventoryRules.Missing(m, present))
                {
                    Missing++;
                    if (ModInventoryRules.CountsForModsHash(rel)) { DataEntries.Add("missing " + ModInventoryRules.Key(rel)); DataCountForHash++; }
                    if (listed++ < ModInventoryRules.DetailLimit) Lines.Add("MOD-DATA missing " + rel);
                }
                if (listed > ModInventoryRules.DetailLimit) Lines.Add("MOD-DATA ... " + (listed - ModInventoryRules.DetailLimit) + " more not listed");
                Lines.Add("MOD-INV data: " + (Same + Ours + Changed + Added) + " files in scope: same " + Same + ", ours " + Ours + " (known own changes), changed " + Changed + ", added " + Added + ", missing " + Missing
                    + "; baseline " + m.Version + " firstpass " + m.Firstpass + " = this game");
            }

            // Steam Workshop items next to the game's library (<library>/steamapps/workshop/content/227860/<id>): kind from the file names; mods, modes and languages hashed.
            void Workshop()
            {
                string dir;
                try { dir = Path.GetFullPath(Path.Combine(Path.Combine(Root, Path.Combine("..", "..")), Path.Combine(Path.Combine("workshop", "content"), "227860"))); } catch (Exception) { return; }
                if (!Directory.Exists(dir)) return;
                int items = 0, listed = 0;
                Dictionary<string, int> kinds = new Dictionary<string, int>();
                foreach (string item in Directory.GetDirectories(dir))
                {
                    if (++items > 300) break;
                    string id = Path.GetFileName(item);
                    string[] files;
                    try { files = Directory.GetFiles(item, "*", SearchOption.AllDirectories); } catch (Exception) { continue; }
                    HashSet<string> names = new HashSet<string>();   // the kind comes from the item's top-level files (nested maps of a game mode do not make it a map)
                    long bytes = 0;
                    foreach (string f in files) { if (string.Equals(Path.GetDirectoryName(f), item, StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileName(f).ToLowerInvariant()); try { bytes += new FileInfo(f).Length; } catch (Exception) { } }
                    string kind = ModInventoryRules.WorkshopKind(names);
                    int c; kinds.TryGetValue(kind, out c); kinds[kind] = c + 1;
                    string h = "";
                    if (ModInventoryRules.WorkshopHashed(kind) && bytes < 50L * 1024 * 1024) h = " " + HashTree(item, files);
                    if (kind == "mod" || kind == "gamemode") { WorkshopMods++; PluginEntries.Add("workshop:" + BugPackRules.SafeName(id) + "#" + h.Trim()); }
                    if (ModInventoryRules.WorkshopHashed(kind) && listed++ < ModInventoryRules.DetailLimit) Lines.Add("MOD-INV workshop " + BugPackRules.SafeName(id) + " " + kind + " files=" + files.Length + " bytes=" + bytes + h);
                }
                List<string> k = new List<string>();
                foreach (KeyValuePair<string, int> p in kinds) k.Add(p.Key + " " + p.Value);
                k.Sort(StringComparer.Ordinal);
                Lines.Add("MOD-INV workshop: " + items + " item(s)" + (k.Count > 0 ? " (" + string.Join(", ", k.ToArray()) + ")" : "") + "; Lua mods and game modes from the Workshop run in every game");
            }

            // Info/UGC and Info/Downloads Lua mods, modes and languages (the game loads them like its own): name, file count, hash.
            void Ugc()
            {
                foreach (string area in new string[] { "UGC", "Downloads" })
                    foreach (string kind in new string[] { "Mods", "Gamemodes", "Languages" })
                    {
                        string dir = Path.Combine(Path.Combine(Path.Combine(Root, "Info"), area), kind);
                        if (!Directory.Exists(dir)) continue;
                        foreach (string sub in Directory.GetDirectories(dir))
                        {
                            string[] files;
                            try { files = Directory.GetFiles(sub, "*", SearchOption.AllDirectories); } catch (Exception) { continue; }
                            string h = HashTree(sub, files);
                            if (kind != "Languages") PluginEntries.Add("ugc:" + kind.ToLowerInvariant() + "/" + BugPackRules.SafeName(Path.GetFileName(sub)) + "#" + h);
                            Lines.Add("MOD-INV ugc " + area + "/" + kind + "/" + BugPackRules.SafeName(Path.GetFileName(sub)) + " files=" + files.Length + " " + h);
                        }
                    }
            }

            string HashTree(string root, string[] files)
            {
                List<string> lines = new List<string>();
                foreach (string f in files) lines.Add(ModInventoryRules.Key(ModInventoryRules.RelativeOrName(f, root)) + "|" + HashFile(f));
                lines.Sort(StringComparer.Ordinal);
                return ModInventoryRules.Hash8(string.Join("\n", lines.ToArray()));
            }

            string HashFile(string path)
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "--------";
                    using (MD5 md5 = MD5.Create())
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                    {
                        int n;
                        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0) md5.TransformBlock(buffer, 0, n, null, 0);
                        md5.TransformFinalBlock(buffer, 0, 0);
                        return ModInventoryRules.Hex8(md5.Hash);
                    }
                }
                catch (Exception) { return "????????"; }
            }
        }
    }

    // Drives ModInventory once per frame (cheap: two comparisons until a scan is due).
    internal sealed class ModInventoryTicker : MonoBehaviour
    {
        void Update() { ModInventory.Tick(); }
    }
}
