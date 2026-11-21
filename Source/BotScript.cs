using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CastleStorySteam
{
    // 0.4.10 IN-GAME TEST BOT, pure part 1: the script language (tokenizer, parser, validation). No Unity: unit-tested by BotTests.cs.
    // Reference for script writers: PROJ\VirtualPlayer\Bot\README.md. One step per line. '#' at the start of a word starts a comment.
    // "double quotes" keep spaces in one word (\" inside is a quote; every other backslash stays, so regexes need no doubling).
    // A line may start with a role prefix: 'host:' or 'guest:' (only that instance runs it) or 'all:' (default: both).
    // A script with ANY error does not run at all (the result file lists the errors, verdict INVALID).
    public enum BotRole { Any = 0, Host = 1, Guest = 2 }

    public sealed class BotCondition
    {
        public string Kind;                                   // log nolog state scene role guests workers items carry held blueprint blueprints structure queue hologram color
        public List<string> Args = new List<string>();        // kind-specific words, the comparison removed
        public string Op; public double Number; public bool HasNumber;
        public bool Negate;                                   // 'expect not <condition>'
        public string From = "any", Since;                    // log / nolog: source filter and the window start (start | action | step | mark:NAME)
        public float Window;                                  // nolog 'for <s>': keep watching that long
        public bool Anti;                                     // blueprint / blueprints / hologram: the demolish (anti) blueprint instead of the build one
        public Regex Pattern;                                 // log / nolog, compiled once at parse time
    }

    public sealed class BotStep
    {
        public int Line; public BotRole Only;
        public string Kind;            // set wait expect waituntil observe barrier action legacy mark note stop (observe = 0.4.18 FACT: never PASS / FAIL)
        public string Name;            // command word; for expect / waituntil the condition kind
        public List<string> Args = new List<string>();
        public BotCondition Cond;      // expect / waituntil
        public float Within = -1f;     // expect / waituntil / barrier / action timeout in seconds (-1 = default of the kind)
        public float Seconds;          // wait
        public string Text;            // mark / note text, canonical step text for result lines
        public string Error;           // parse / validation error
        public VirtualAction Legacy;   // a 0.3.78 virtual player command (run by VirtualPlayer.cs)
        public bool IsCheck { get { return Kind == "expect" || Kind == "waituntil" || Kind == "barrier"; } }
        public bool IsAction { get { return Kind == "action" || Kind == "legacy"; } }
    }

    public sealed class BotScriptDoc
    {
        public List<BotStep> Steps = new List<BotStep>();
        public Dictionary<string, string> Settings = new Dictionary<string, string>();
        public List<string> Errors = new List<string>();
        public string Get(string key, string def) { string v; return Settings.TryGetValue(key, out v) ? v : def; }
        public float GetSeconds(string key, float def) { string v; float f; return Settings.TryGetValue(key, out v) && BotRules.TryDuration(v, out f) ? f : def; }
        public int CountChecks() { int n = 0; foreach (BotStep s in Steps) if (s.IsCheck) n++; return n; }
    }

    public static class BotScript
    {
        public const int MaxSteps = 600;
        public const float MaxSeconds = 7200f;
        public static readonly string[] LegacyNames = { "list", "hover", "leave", "aim", "aimrel", "clear", "fire", "dig", "digrel", "doors", "breakdoor", "fillstock", "status", "join", "joinlan", "snapshot", "heapwalk", "ready", "unready", "start", "lobby", "net", "walk", "walkrel", "barrels" };
        public static readonly string[] NativeActions = { "craft", "place", "placerel", "demolish", "assign", "pickup", "deliver", "join", "ready", "unready", "harvest", "unstump", "autonomy", "team", "faction", "demolishdoor", "attackdoor", "firedoor", "grid", "ungrid", "save", "load", "leavematch", "leavelobby", "hostlobby", "hoststart", "sandbox", "closeintro", "debug", "camera", "shot", "mute", "dump", "fillstock", "stock" };
        public static readonly string[] ConditionKinds = { "log", "nolog", "state", "scene", "role", "guests", "workers", "items", "carry", "held", "blueprint", "blueprints", "structure", "queue", "hologram", "color", "crates", "stumps", "doors" };
        public static readonly string[] Sources = { "any", "perf", "bepinex", "unity", "brix" };
        public static readonly string[] States = { "menu", "lobby", "loading", "match" };
        public static readonly string[] Roles = { "host", "guest", "single", "menu" };
        public static readonly string[] Colors = { "blue", "orange", "red", "yellow", "green", "white", "gray", "dark", "other" };
        public static readonly string[] DumpWhat = { "workers", "blueprints", "structures", "stock", "altar", "state", "orange", "projects", "camera", "lobby", "doors", "all" };
        public static readonly string[] DoorOwners = { "own", "other", "any" };   // 0.4.18: own = the local faction, other = any other faction
        public static readonly string[] AssignTasks = { "build", "craft", "idle" };
        public static readonly string[] SettingKeys = { "run", "name", "role", "timeout", "onfail", "pace", "roles" };

        // ---------- tokenizer ----------
        public static List<string> Tokenize(string line, out string error)
        {
            error = null; List<string> t = new List<string>(); if (line == null) return t;
            StringBuilder cur = new StringBuilder(); bool inQ = false, had = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQ)
                {
                    if (ch == '\\' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; continue; }
                    if (ch == '"') { inQ = false; continue; }
                    cur.Append(ch); continue;
                }
                if (ch == '"') { inQ = true; had = true; continue; }
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
                {
                    if (cur.Length > 0 || had) { t.Add(cur.ToString()); cur.Length = 0; had = false; }
                    continue;
                }
                if (ch == '#' && cur.Length == 0 && !had) break;   // comment: '#' at the start of a word
                cur.Append(ch);
            }
            if (inQ) { error = "unclosed quote"; return t; }
            if (cur.Length > 0 || had) t.Add(cur.ToString());
            return t;
        }

        static string Quote(string w)
        {
            if (w.Length > 0 && w.IndexOf(' ') < 0 && w.IndexOf('\t') < 0 && w.IndexOf('"') < 0 && w[0] != '#') return w;
            return "\"" + w.Replace("\"", "\\\"") + "\"";
        }

        // ---------- script ----------
        public static BotScriptDoc Parse(string[] lines)
        {
            BotScriptDoc doc = new BotScriptDoc();
            if (lines == null) { doc.Errors.Add("no script"); return doc; }
            for (int i = 0; i < lines.Length; i++)
            {
                BotStep s = ParseLine(lines[i], i + 1);
                if (s == null) continue;
                if (s.Error != null) doc.Errors.Add("L" + s.Line + ": " + s.Error);
                if (s.Kind == "set") { if (s.Error == null) doc.Settings[s.Args[0].ToLowerInvariant()] = s.Args[1]; continue; }
                if (doc.Steps.Count >= MaxSteps) { doc.Errors.Add("L" + s.Line + ": more than " + MaxSteps + " steps"); break; }
                doc.Steps.Add(s);
            }
            if (doc.Steps.Count == 0 && doc.Errors.Count == 0) doc.Errors.Add("the script has no steps");
            return doc;
        }

        public static BotStep ParseLine(string raw, int lineNo)
        {
            if (raw == null) return null;
            string err; List<string> t = Tokenize(raw, out err);
            BotStep s = new BotStep(); s.Line = lineNo; s.Kind = "error"; s.Name = "?";
            if (err != null) { s.Error = err; s.Text = raw.Trim(); return s; }
            if (t.Count == 0) return null;
            string first = t[0].ToLowerInvariant();
            string[] prefixes = { "host:", "guest:", "all:" };
            foreach (string p in prefixes)
            {
                if (!first.StartsWith(p)) continue;
                s.Only = p == "host:" ? BotRole.Host : p == "guest:" ? BotRole.Guest : BotRole.Any;
                if (first.Length == p.Length) t.RemoveAt(0); else t[0] = t[0].Substring(p.Length);
                break;
            }
            if (t.Count == 0) { s.Error = "role prefix without a command"; s.Text = raw.Trim(); return s; }
            s.Name = t[0].ToLowerInvariant(); for (int i = 1; i < t.Count; i++) s.Args.Add(t[i]);
            StringBuilder canon = new StringBuilder(); if (s.Only == BotRole.Host) canon.Append("host: "); else if (s.Only == BotRole.Guest) canon.Append("guest: ");
            canon.Append(s.Name); foreach (string w in s.Args) canon.Append(' ').Append(Quote(w)); s.Text = canon.ToString();
            s.Error = Validate(s, t);
            return s;
        }

        static string Validate(BotStep s, List<string> tokens)
        {
            List<string> a = s.Args; float f;
            switch (s.Name)
            {
                case "set":
                    s.Kind = "set";
                    if (a.Count != 2) return "set needs <key> <value>";
                    { string k = a[0].ToLowerInvariant(), v = a[1];
                      if (Array.IndexOf(SettingKeys, k) < 0) return "unknown setting '" + a[0] + "' (known: " + string.Join(", ", SettingKeys) + ")";
                      if (s.Only != BotRole.Any) return "set cannot have a role prefix";
                      if (k == "run" && !BotRules.ValidName(v, 60)) return "bad run id (letters, digits, - _ . ; up to 60)";
                      if (k == "name" && v.Length > 80) return "name longer than 80";
                      if (k == "role" && v != "host" && v != "guest" && v != "auto") return "role must be host, guest or auto";
                      if (k == "timeout" && (!BotRules.TryDuration(v, out f) || f < 10f || f > MaxSeconds)) return "timeout must be 10 s .. 2 h";
                      if (k == "onfail" && v != "continue" && v != "stop") return "onfail must be continue or stop";
                      if (k == "pace" && (!BotRules.TryDuration(v, out f) || f < 0f || f > 10f)) return "pace must be 0 .. 10 s";
                      if (k == "roles") { string[] r = BotRules.ParseRoles(v); if (r == null) return "roles must be a comma list of host, guest"; }
                    }
                    return null;
                case "wait":
                    s.Kind = "wait";
                    if (a.Count != 1 || !BotRules.TryDuration(a[0], out f) || f < 0f || f > MaxSeconds) return "wait needs a duration 0 .. 2 h (30, 30s, 2m)";
                    s.Seconds = f; return null;
                case "expect": case "waituntil": case "observe":   // observe (0.4.18) = waituntil that records a FACT yes/no and never fails the run
                    s.Kind = s.Name;
                    { string e = ParseCondition(s, a); if (e != null) return e; }
                    if ((s.Kind == "waituntil" || s.Kind == "observe") && s.Within < 0f) s.Within = 60f;
                    if (s.Kind == "observe" && (s.Cond.Kind == "nolog" || s.Cond.Kind == "color")) return "observe takes a state or log condition (not nolog / color)";
                    return null;
                case "barrier":
                    s.Kind = "barrier"; s.Within = 300f;
                    if (a.Count < 1 || !BotRules.ValidName(a[0], 40)) return "barrier needs a name (letters, digits, - _ .)";
                    for (int i = 1; i < a.Count; i++)
                    {
                        string k = a[i].ToLowerInvariant();
                        if (k == "within" && i + 1 < a.Count) { if (!BotRules.TryDuration(a[i + 1], out f) || f <= 0f || f > MaxSeconds) return "bad barrier timeout"; s.Within = f; i++; continue; }
                        if (k == "roles" && i + 1 < a.Count) { if (BotRules.ParseRoles(a[i + 1]) == null) return "barrier roles must be a comma list of host, guest"; i++; continue; }
                        return "barrier takes: <name> [within <s>] [roles host,guest]";
                    }
                    return null;
                case "mark":
                    s.Kind = "mark";
                    if (a.Count != 1 || !BotRules.ValidName(a[0], 40)) return "mark needs one name (letters, digits, - _ .)";
                    return null;
                case "note":
                    s.Kind = "note";
                    if (a.Count == 0) return "note needs text";
                    return null;
                case "stop":
                    s.Kind = "stop"; return a.Count == 0 ? null : "stop takes no argument";
            }
            if (s.Name == "fillstock" && a.Count != 2) return Legacy(s, tokens);   // 0.3.93 forms: fillstock / fillstock <Resource>
            if (Array.IndexOf(NativeActions, s.Name) >= 0) { s.Kind = "action"; return ValidateAction(s); }
            if (Array.IndexOf(LegacyNames, s.Name) >= 0) return Legacy(s, tokens);
            s.Kind = "error"; return "unknown command '" + s.Name + "'";
        }

        static string Legacy(BotStep s, List<string> tokens)
        {
            s.Kind = "legacy";
            StringBuilder b = new StringBuilder(); foreach (string w in tokens) { if (b.Length > 0) b.Append(' '); b.Append(w); }
            s.Legacy = VirtualPlayerRules.Parse(b.ToString());
            if (s.Legacy == null) return "empty legacy command";
            return s.Legacy.Error;
        }

        // 'within <s>' sets the action timeout of an action step (default per action, see BotRunner.DefaultActionTimeout)
        static string TakeWithin(BotStep s, List<string> a)
        {
            for (int i = 0; i < a.Count - 1; i++)
            {
                if (a[i].ToLowerInvariant() != "within") continue;
                float f; if (!BotRules.TryDuration(a[i + 1], out f) || f <= 0f || f > MaxSeconds) return "bad duration '" + a[i + 1] + "'";
                s.Within = f; a.RemoveRange(i, 2); return null;
            }
            return null;
        }

        static string ValidateAction(BotStep s)
        {
            List<string> a = s.Args; float f; int n;
            { string e = TakeWithin(s, a); if (e != null) return e; }
            switch (s.Name)
            {
                case "craft":
                    if (a.Count < 2 || a.Count > 3) return "craft needs <station> <recipe> [count]";
                    if (!BotRules.ValidIdent(a[0])) return "bad station name";
                    if (!BotRules.ValidIdent(a[1]) && !(BotRules.TryInt(a[1], out n) && n >= 0 && n <= 100000)) return "recipe must be an id or a name";
                    if (a.Count == 3 && (!BotRules.TryInt(a[2], out n) || n < 1 || n > 50)) return "count must be 1 .. 50";
                    return null;
                case "place": case "placerel":
                    { int k = a.Count; if (k > 0 && a[k - 1].ToLowerInvariant() == "instant") k--;   // test helper: the host builds it at once, no materials
                      if (s.Name == "place" && k != 4 && k != 6) return "place needs <key> <x> <y> <z> [rot <degrees>] [instant]";
                      if (s.Name == "placerel" && k != 3 && k != 5) return "placerel needs <key> <dx> <dz> [rot <degrees>] [instant]";
                      if (!BotRules.ValidKey(a[0])) return "bad asset key";
                      int last = s.Name == "place" ? 3 : 2; float lim = s.Name == "place" ? 5000f : 300f;
                      for (int i = 1; i <= last; i++) if (!BotRules.TryFloat(a[i], out f) || Math.Abs(f) > lim) return "bad coordinate '" + a[i] + "'" + (s.Name == "placerel" ? " (|d| <= 300)" : "");
                      return k == last + 3 ? Rot(a, last + 1) : null; }
                case "pickup":
                    if (a.Count != 2 || !BotRules.ValidSelector(a[0]) || !BotRules.ValidIdent(a[1])) return "pickup needs <selector> <item> (the worker takes it from an own crate or crafting station)";
                    return null;
                case "deliver":
                    if (a.Count != 2 || !BotRules.ValidSelector(a[0]) || !BotRules.ValidKey(a[1])) return "deliver needs <selector> <blueprint key> (the worker stores its load into the nearest own blueprint)";
                    return null;
                case "demolish":
                    if (a.Count < 1 || a.Count > 2) return "demolish needs <key> [index]";
                    if (!BotRules.ValidKey(a[0])) return "bad asset key";
                    if (a.Count == 2 && (!BotRules.TryInt(a[1], out n) || n < 0 || n > 99)) return "bad index";
                    return null;
                case "assign":
                    if (a.Count < 2 || a.Count > 3) return "assign needs <count|all> <task> [target]";
                    if (a[0].ToLowerInvariant() != "all" && (!BotRules.TryInt(a[0], out n) || n < 1 || n > 50)) return "count must be 1 .. 50 or all";
                    { string task = a[1].ToLowerInvariant();
                      if (Array.IndexOf(AssignTasks, task) < 0) return "task must be one of " + string.Join(", ", AssignTasks);
                      if (task == "build" && a.Count == 3 && a[2].ToLowerInvariant() != "exclusive") return "assign .. build takes only the word exclusive (the other builders hold position)";
                      if (task == "craft" && (a.Count != 3 || !BotRules.ValidIdent(a[2]))) return "assign .. craft needs a station (altar, workbench, ...)";
                      if (task == "idle" && a.Count != 2) return "assign .. idle takes no target"; }
                    return null;
                case "stock":
                    if (a.Count < 2 || a.Count > 3 || !BotRules.ValidIdent(a[0])) return "stock needs <Resource> <amount> [other]";
                    if (!BotRules.TryInt(a[1], out n) || n < 1 || n > 5000) return "amount must be 1 .. 5000";
                    if (a.Count == 3 && a[2].ToLowerInvariant() != "other" && a[2].ToLowerInvariant() != "altar") return "stock takes other (the other player's crates, seen on the host) or altar (straight into the own Altar)";
                    return null;
                case "join":   // 0.4.11: an explicit host (the old join needed exactly one friend in Castle Story)
                    if (a.Count > 1) return "join takes nothing, code, a Steam id (17 digits) or name=<friend name>";
                    if (a.Count == 1)
                    {
                        string j = a[0]; ulong sid;
                        if (j.ToLowerInvariant() == "code") return null;
                        if (j.StartsWith("name=", StringComparison.OrdinalIgnoreCase) && j.Length > 5 && j.Length <= 45) return null;
                        if (ulong.TryParse(j, NumberStyles.Integer, CultureInfo.InvariantCulture, out sid) && sid > 76561190000000000UL && sid < 76561300000000000UL) return null;
                        return "join target must be code, a Steam id (17 digits, 7656119...) or name=<friend name>";
                    }
                    return null;
                case "ready": case "unready":
                    return a.Count == 0 ? null : s.Name + " takes no argument";
                case "harvest":   // 0.4.12: New task > Harvest (trees, pyrite = OrangeCrystal, iron, boulder, plant)
                    {
                        if (a.Count < 2 || Array.IndexOf(new string[] { "tree", "pyrite", "iron", "boulder", "plant" }, a[0].ToLowerInvariant()) < 0) return "harvest needs <tree|pyrite|iron|boulder|plant> nearest|<x> <z> [radius 1..30] [crew 0..20]";
                        int at = 2;
                        if (a[1].ToLowerInvariant() != "nearest") { if (a.Count < 3 || !BotRules.TryFloat(a[1], out f) || !BotRules.TryFloat(a[2], out f)) return "harvest needs nearest or <x> <z>"; at = 3; }
                        if (a.Count > at && (!BotRules.TryInt(a[at], out n) || n < 1 || n > 30)) return "radius must be 1 .. 30";
                        if (a.Count > at + 1 && (!BotRules.TryInt(a[at + 1], out n) || n < 0 || n > 20)) return "crew must be 0 .. 20";
                        if (a.Count > at + 2) return "harvest: too many words";
                        return null;
                    }
                case "unstump":
                    foreach (string w in a) { if (w.ToLowerInvariant() == "instant") continue; if (!BotRules.TryInt(w, out n) || n < 1 || n > 40) return "unstump takes [radius 1..40] [instant]"; }
                    return a.Count <= 2 ? null : "unstump takes [radius] [instant]";
                case "autonomy":
                    if (a.Count != 2 || (a[0].ToLowerInvariant() != "all" && !BotRules.ValidSelector(a[0])) || (a[1].ToLowerInvariant() != "freeagent" && a[1].ToLowerInvariant() != "hold")) return "autonomy needs <selector|all> freeagent|hold";
                    return null;
                case "team": case "faction":   // 0.4.18: the lobby team / faction pickers; 'probe' only asks the game whether the number is assignable
                    if (a.Count < 1 || a.Count > 2 || !BotRules.TryInt(a[0], out n) || n < 0 || n > (s.Name == "team" ? 3 : 7)) return s.Name + " needs a number 0 .. " + (s.Name == "team" ? "3 (Team A .. D)" : "7") + " [probe]";
                    if (a.Count == 2 && a[1].ToLowerInvariant() != "probe") return s.Name + " takes only the word probe after the number";
                    return null;
                case "demolishdoor":   // 0.4.18: the remover on a door (own / other faction), like 'demolish' but for any faction's door
                    if (a.Count < 1 || a.Count > 2 || Array.IndexOf(DoorOwners, a[0].ToLowerInvariant()) < 0) return "demolishdoor needs own|other|any [index]";
                    if (a.Count == 2 && (!BotRules.TryInt(a[1], out n) || n < 0 || n > 29)) return "door index must be 0 .. 29";
                    return null;
                case "attackdoor":     // 0.4.18: UNetProjectCmd.CmdAttack(worker, door) = the player's attack order on that door; 'siege' = CmdBreakObject
                    if (a.Count > 2 && a[a.Count - 1].ToLowerInvariant() == "siege") a.RemoveAt(a.Count - 1);   // re-read by the game part from s.Text
                    if (a.Count < 2 || a.Count > 3 || (a[0].ToLowerInvariant() != "all" && !BotRules.ValidSelector(a[0])) || Array.IndexOf(DoorOwners, a[1].ToLowerInvariant()) < 0) return "attackdoor needs <selector|all> own|other|any [index] [siege]";
                    if (a.Count == 3 && (!BotRules.TryInt(a[2], out n) || n < 0 || n > 29)) return "door index must be 0 .. 29";
                    return null;
                case "firedoor":       // 0.4.18: an own catapult aims at a door and fires once (the legacy aim + fire commands)
                    if (a.Count < 2 || a.Count > 3 || !BotRules.TryInt(a[0], out n) || n < 0 || n > 50 || Array.IndexOf(DoorOwners, a[1].ToLowerInvariant()) < 0) return "firedoor needs <catapult index> own|other|any [door index]";
                    if (a.Count == 3 && (!BotRules.TryInt(a[2], out n) || n < 0 || n > 29)) return "door index must be 0 .. 29";
                    return null;
                case "grid":   // 0.4.18 GRID SPAWN test helper (BotGridRules.Parse)
                    { string ge; BotGridRules.Parse(a, out ge); return ge; }
                case "ungrid":
                    return a.Count == 0 ? null : "ungrid takes no argument";
                case "leavelobby": case "hoststart": case "closeintro":
                    return a.Count == 0 ? null : s.Name + " takes no argument";
                case "debug":
                    return a.Count == 1 && (a[0] == "on" || a[0] == "instant") ? null : "debug needs on or instant";
                case "fillstock":
                    if (a.Count != 2) return "fillstock needs <Resource> <crates>"; if (!BotRules.ValidIdent(a[0])) return "bad resource name";   // 0.4.27 review: 'fillstock within 5' has 2 words, so it comes here; TakeWithin removed both and a[0] threw out of the parser (script consumed, no result file)
                    if (!BotRules.TryInt(a[1], out n) || n < 1 || n > 200) return "crates must be 1 .. 200";
                    return null;
                case "save": case "load":
                    if (a.Count != 1 || !BotRules.ValidName(a[0].Replace("{run}", "run"), 40)) return s.Name + " needs a name (letters, digits, - _ .; up to 40; {run} = the run id)";
                    return null;
                case "leavematch":
                    return a.Count == 0 ? null : "leavematch takes no argument";
                case "hostlobby": case "sandbox":
                    if (a.Count < 1 || a.Count > 2) return s.Name + " needs <map> [mode]";
                    if (a[0].Length == 0 || a[0].Length > 80 || a[0].IndexOfAny(new char[] { '\\', '/', ':', '*', '?', '<', '>', '|' }) >= 0) return "bad map name";
                    if (a.Count == 2 && !BotRules.ValidIdent(a[1])) return "bad mode name";
                    return null;
                case "camera":
                    if (a.Count == 1 && a[0].ToLowerInvariant() == "restore") return null;
                    { int used; string e = ParseTarget(a, 0, out used); if (e != null) return e; if (used != a.Count) return "camera: extra words after the target"; }
                    return null;
                case "shot":
                    if (a.Count < 1 || !BotRules.ValidName(a[0], 40)) return "shot needs a name (letters, digits, - _ .)";
                    for (int i = 1; i < a.Count; i++)
                    {
                        string k = a[i].ToLowerInvariant();
                        if (k == "focus") { int used; string e = ParseTarget(a, i + 1, out used); if (e != null) return e; i += used; continue; }
                        if (k == "box" && i + 1 < a.Count) { if (!BotRules.TryInt(a[i + 1], out n) || n < 8 || n > 1000) return "box must be 8 .. 1000 px"; i++; continue; }
                        return "shot takes: <name> [focus <target>] [box <px>]";
                    }
                    return null;
                case "mute":
                    return a.Count == 1 && (a[0] == "on" || a[0] == "off") ? null : "mute needs on or off";
                case "dump":
                    return a.Count == 1 && Array.IndexOf(DumpWhat, a[0].ToLowerInvariant()) >= 0 ? null : "dump needs one of " + string.Join(", ", DumpWhat);
            }
            return "unknown action";
        }

        static string Rot(List<string> a, int at)
        {
            float f; if (a[at].ToLowerInvariant() != "rot" || !BotRules.TryFloat(a[at + 1], out f) || Math.Abs(f) > 360f) return "rotation must be 'rot <degrees>'";
            return null;
        }

        // target: worker <selector> | carrier <item> | blueprint <key> | structure <key> [index] | at <x> <y> <z> | anchor | altar
        public static string ParseTarget(List<string> a, int at, out int used)
        {
            used = 0; if (at >= a.Count) return "missing target (worker, carrier, blueprint, structure, at, anchor, altar)";
            string k = a[at].ToLowerInvariant(); float f; int n;
            switch (k)
            {
                case "anchor": case "altar": used = 1; return null;
                case "worker":
                    if (at + 1 >= a.Count || !BotRules.ValidSelector(a[at + 1])) return "worker needs a selector: any, <index> or name=<text>";
                    used = 2; return null;
                case "carrier":
                    if (at + 1 >= a.Count || !BotRules.ValidIdent(a[at + 1])) return "carrier needs an item name";
                    used = 2; return null;
                case "blueprint":
                    if (at + 1 >= a.Count || !BotRules.ValidKey(a[at + 1])) return "blueprint needs an asset key";
                    used = 2; return null;
                case "structure":
                    if (at + 1 >= a.Count || !BotRules.ValidKey(a[at + 1])) return "structure needs an asset key";
                    used = 2; if (at + 2 < a.Count && BotRules.TryInt(a[at + 2], out n)) { if (n < 0 || n > 99) return "bad structure index"; used = 3; }
                    return null;
                case "at":
                    if (at + 3 >= a.Count) return "at needs <x> <y> <z>";
                    for (int i = 1; i <= 3; i++) if (!BotRules.TryFloat(a[at + i], out f) || Math.Abs(f) > 5000f) return "bad coordinate '" + a[at + i] + "'";
                    used = 4; return null;
            }
            return "unknown target '" + a[at] + "'";
        }

        // ---------- conditions ----------
        static string ParseCondition(BotStep s, List<string> args)
        {
            List<string> w = new List<string>(args); BotCondition c = new BotCondition(); s.Cond = c; float f;
            if (w.Count > 0 && w[0].ToLowerInvariant() == "not") { c.Negate = true; w.RemoveAt(0); }
            if (w.Count == 0) return s.Name + " needs a condition (" + string.Join(", ", ConditionKinds) + ")";
            { int g = w[0].IndexOfAny(new char[] { '<', '>', '=', '!' }); if (g > 0) { w.Insert(1, w[0].Substring(g)); w[0] = w[0].Substring(0, g); } }   // "workers>=3"
            c.Kind = w[0].ToLowerInvariant(); s.Name = c.Kind;
            if (Array.IndexOf(ConditionKinds, c.Kind) < 0) return "unknown condition '" + w[0] + "' (known: " + string.Join(", ", ConditionKinds) + ")";
            int firstFree = (c.Kind == "log" || c.Kind == "nolog") ? 2 : 1;   // never read the regex as a keyword
            for (int i = firstFree; i < w.Count - 1; i++)
            {
                string k = w[i].ToLowerInvariant();
                if (k != "within" && k != "for") continue;
                if (!BotRules.TryDuration(w[i + 1], out f) || f < 0f || f > MaxSeconds) return "bad duration '" + w[i + 1] + "'";
                if (k == "within") s.Within = f; else { if (c.Kind != "nolog") return "'for' is only for nolog"; c.Window = f; }
                w.RemoveRange(i, 2); i--;
            }
            for (int i = 1; i < w.Count; i++) c.Args.Add(w[i]);
            List<string> a = c.Args;
            switch (c.Kind)
            {
                case "log": case "nolog":
                    if (a.Count < 1 || a[0].Length == 0) return c.Kind + " needs a \"regex\"";
                    try { c.Pattern = new Regex(a[0], RegexOptions.CultureInvariant); } catch (ArgumentException e) { return "bad regex: " + e.Message; }
                    c.Since = c.Kind == "nolog" ? "start" : "action";
                    for (int i = 1; i < a.Count; i++)
                    {
                        string k = a[i].ToLowerInvariant();
                        if (k == "from" && i + 1 < a.Count) { string v = a[i + 1].ToLowerInvariant(); if (Array.IndexOf(Sources, v) < 0) return "from must be one of " + string.Join(", ", Sources); c.From = v; i++; continue; }
                        if (k == "since" && i + 1 < a.Count)
                        {
                            string v = a[i + 1].ToLowerInvariant();
                            if (v == "start" || v == "action" || v == "step" || v == "boot") c.Since = v;   // boot = every line since the bot was armed
                            else if (v.StartsWith("mark:") && BotRules.ValidName(a[i + 1].Substring(5), 40)) c.Since = "mark:" + a[i + 1].Substring(5);
                            else return "since must be boot, start, action, step or mark:<name>";
                            i++; continue;
                        }
                        return c.Kind + " takes: \"regex\" [from <source>] [since <start|action|step|mark:NAME>] [within <s>]" + (c.Kind == "nolog" ? " [for <s>]" : "");
                    }
                    a.RemoveRange(1, a.Count - 1);
                    if (c.Negate) return "use nolog instead of 'not log'";
                    return null;
                case "state":
                    return a.Count == 1 && Array.IndexOf(States, a[0].ToLowerInvariant()) >= 0 ? null : "state must be one of " + string.Join(", ", States);
                case "scene":
                    return a.Count == 1 ? null : "scene needs one name";
                case "role":
                    return a.Count == 1 && Array.IndexOf(Roles, a[0].ToLowerInvariant()) >= 0 ? null : "role must be one of " + string.Join(", ", Roles);
                case "guests": case "workers": case "crates": case "stumps":
                    return Compare(c, 0) ?? (a.Count == 0 ? null : c.Kind + " takes only <op> <number>");
                case "doors":   // 0.4.18: doors standing in the world, own / other faction / any
                    if (a.Count < 1 || Array.IndexOf(DoorOwners, a[0].ToLowerInvariant()) < 0) return "doors needs own|other|any <op> <number>";
                    return Compare(c, 1) ?? (a.Count == 1 ? null : "doors takes own|other|any <op> <number>");
                case "items":
                    if (a.Count < 1 || !BotRules.ValidIdent(a[0])) return "items needs <Resource> <op> <number>";
                    return Compare(c, 1) ?? (a.Count == 1 ? null : "items takes <Resource> <op> <number>");
                case "carry": case "held":
                    if (a.Count != 2 || !BotRules.ValidSelector(a[0]) || !BotRules.ValidIdent(a[1])) return c.Kind + " needs <selector: any | index | name=text> <" + (c.Kind == "carry" ? "item" : "model") + ">";
                    return null;
                case "blueprint":
                    if (a.Count == 3 && a[2].ToLowerInvariant() == "anti") { c.Anti = true; a.RemoveAt(2); }
                    if (a.Count != 2 || !BotRules.ValidKey(a[0]) || (a[1] != "exists" && a[1] != "missing")) return "blueprint needs <key> exists|missing [anti]";
                    return null;
                case "blueprints":
                    if (a.Count >= 2 && a[1].ToLowerInvariant() == "anti") { c.Anti = true; a.RemoveAt(1); }
                    if (a.Count < 1 || !BotRules.ValidKey(a[0])) return "blueprints needs <key> [anti] <op> <number>";
                    return Compare(c, 1) ?? (a.Count == 1 ? null : "blueprints takes <key> [anti] <op> <number>");
                case "structure":
                    if (a.Count < 1 || !BotRules.ValidKey(a[0])) return c.Kind + " needs <key> <op> <number>";
                    return Compare(c, 1) ?? (a.Count == 1 ? null : c.Kind + " takes <key> <op> <number>");
                case "queue":
                    if (a.Count < 1 || !BotRules.ValidIdent(a[0])) return "queue needs <station> <op> <number>";
                    return Compare(c, 1) ?? (a.Count == 1 ? null : "queue takes <station> <op> <number>");
                case "hologram":
                    if (a.Count == 3 && a[2].ToLowerInvariant() == "anti") { c.Anti = true; a.RemoveAt(2); }
                    if (a.Count != 2 || !BotRules.ValidKey(a[0]) || Array.IndexOf(Colors, a[1].ToLowerInvariant()) < 0) return "hologram needs <key> <colour: " + string.Join(", ", Colors) + "> [anti]";
                    return null;
                case "color":
                    if (a.Count < 2 || !BotRules.ValidName(a[0], 40) || Array.IndexOf(Colors, a[1].ToLowerInvariant()) < 0) return "color needs <shot> <colour> <op> <percent>";
                    { string e = Compare(c, 2); if (e != null) return e; }
                    if (c.Number < 0 || c.Number > 100) return "percent must be 0 .. 100";
                    return a.Count == 2 ? null : "color takes <shot> <colour> <op> <percent>";
            }
            return "unknown condition";
        }

        // reads "<op> <number>" (or "<op><number>") at c.Args[at..] and removes it
        static string Compare(BotCondition c, int at)
        {
            List<string> a = c.Args;
            if (at >= a.Count) return c.Kind + ": missing <op> <number> (ops: >= <= == != > <)";
            string op, num;
            if (BotRules.TryOp(a[at], out op)) { if (at + 1 >= a.Count) return "missing number after " + op; num = a[at + 1]; a.RemoveRange(at, 2); }
            else
            {
                string w = a[at]; int k = 0; while (k < w.Length && "<>=!".IndexOf(w[k]) >= 0) k++;
                if (k == 0 || !BotRules.TryOp(w.Substring(0, k), out op)) return c.Kind + ": expected an operator (>= <= == != > <), got '" + w + "'";
                num = w.Substring(k); a.RemoveAt(at);
            }
            double d; if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || double.IsInfinity(d)) return "bad number '" + num + "'";
            c.Op = op; c.Number = d; c.HasNumber = true; return null;
        }
    }
}
