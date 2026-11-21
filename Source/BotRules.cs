using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CastleStorySteam
{
    // 0.4.10 IN-GAME TEST BOT, pure part 2: small rules shared by the parser, the runner and the game glue (no Unity, unit-tested by BotTests.cs):
    // durations, comparisons, names, role matching, the old virtual-player output classifier, barrier file names, result lines and the verdict,
    // the in-memory log ring that 'expect log' searches, and the pixel colour classifier used by 'shot' / 'expect color'.
    public static class BotRules
    {
        public const string ResultHeader = "BOT RESULT v1";

        public static bool TryDuration(string s, out float seconds)
        {
            seconds = 0f; if (string.IsNullOrEmpty(s)) return false;
            string v = s.Trim().ToLowerInvariant(); float mul = 1f;
            if (v.EndsWith("ms")) { mul = 0.001f; v = v.Substring(0, v.Length - 2); }
            else if (v.EndsWith("s")) v = v.Substring(0, v.Length - 1);
            else if (v.EndsWith("m")) { mul = 60f; v = v.Substring(0, v.Length - 1); }
            else if (v.EndsWith("h")) { mul = 3600f; v = v.Substring(0, v.Length - 1); }
            float f; if (v.Length == 0 || !float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f) || float.IsNaN(f) || float.IsInfinity(f)) return false;
            seconds = f * mul; return true;
        }

        public static bool TryInt(string s, out int v) { return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v); }
        public static bool TryFloat(string s, out float v) { return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v); }

        public static bool TryOp(string s, out string op)
        {
            op = null;
            switch (s) { case ">=": case "<=": case "==": case "!=": case ">": case "<": op = s; return true; case "=": op = "=="; return true; }
            return false;
        }

        public static bool Compare(double actual, string op, double expected)
        {
            const double eps = 1e-9;
            switch (op)
            {
                case ">=": return actual >= expected - eps;
                case "<=": return actual <= expected + eps;
                case "==": return Math.Abs(actual - expected) <= eps;
                case "!=": return Math.Abs(actual - expected) > eps;
                case ">": return actual > expected + eps;
                case "<": return actual < expected - eps;
            }
            return false;
        }

        static bool NameChar(char c) { return c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-' || c == '.'; }

        // file-safe name: letters, digits, '-', '_', '.', 1..max chars, not starting with '.'
        public static bool ValidName(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length > max || s[0] == '.') return false;
            foreach (char c in s) if (!NameChar(c)) return false;
            return s.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        // identifier: a letter first, then letters, digits, '_'; up to 60 chars (resource, recipe, station, task names)
        public static bool ValidIdent(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 60 || !(s[0] >= 'a' && s[0] <= 'z' || s[0] >= 'A' && s[0] <= 'Z')) return false;
            foreach (char c in s) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) return false;
            return true;
        }

        // asset key: Name or Factory/Name (letters, digits, '_', '.', '-'), up to 80 chars
        public static bool ValidKey(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 80) return false;
            int slash = s.IndexOf('/'); if (slash != s.LastIndexOf('/') || slash == 0 || slash == s.Length - 1) return false;
            foreach (char c in s) if (!(NameChar(c) || c == '/')) return false;
            return true;
        }

        // worker selector: any (own) | anyone (every faction: a guest looking at the host's workers) | <index 0..999> | name=<text>
        public static bool ValidSelector(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.ToLowerInvariant() == "any" || s.ToLowerInvariant() == "anyone") return true;
            int n; if (TryInt(s, out n)) return n >= 0 && n <= 999;
            return s.StartsWith("name=", StringComparison.OrdinalIgnoreCase) && s.Length > 5 && s.Length <= 45;
        }

        public static bool SelectorMatches(string selector, int index, string name)
        {
            if (string.IsNullOrEmpty(selector)) return false;
            if (selector.ToLowerInvariant() == "any" || selector.ToLowerInvariant() == "anyone") return true;
            int n; if (TryInt(selector, out n)) return n == index;
            if (selector.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) return name != null && name.IndexOf(selector.Substring(5), StringComparison.OrdinalIgnoreCase) >= 0;
            return false;
        }

        // "host,guest" -> {"host","guest"}; null when empty, unknown or duplicated
        public static string[] ParseRoles(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] p = s.ToLowerInvariant().Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || p.Length > 2) return null;
            for (int i = 0; i < p.Length; i++) { p[i] = p[i].Trim(); if (p[i] != "host" && p[i] != "guest") return null; for (int j = 0; j < i; j++) if (p[j] == p[i]) return null; }
            return p;
        }

        // actual role: host (server active; a single player match counts as host), guest (client only), single, menu (no network)
        public static bool RoleMatches(BotRole only, string role)
        {
            if (only == BotRole.Any) return true;
            if (only == BotRole.Host) return role == "host" || role == "single";
            return role == "guest";
        }

        // the 0.3.78 virtual player reports by text only: these phrases mean the command did not do its job
        static readonly string[] VpFailMarkers = { "RESULT=failed", "RESULT=rejected", " does not exist", ": no own worker", ": no UNetCatapult", ": no UNetBlueprint", " has no FactoryImprint", ": no blueprint key", ": no own build project", "not in this build", "crates filled=0", "script read failed" };
        public static bool VpOutputOk(string text, out string why)
        {
            why = null; if (text == null) return true;
            foreach (string m in VpFailMarkers) if (text.IndexOf(m, StringComparison.Ordinal) >= 0) { why = m.Trim(); return false; }
            return true;
        }

        // barrier files live in BepInEx\bot\sync\<run>\ : one empty file per role that arrived
        public static string BarrierFile(string barrier, string role) { return barrier + "." + role + ".arrived"; }
        public static string BarrierMissing(ICollection<string> presentFileNames, string barrier, string[] roles)
        {
            StringBuilder miss = new StringBuilder();
            foreach (string r in roles)
            {
                bool found = false; string want = BarrierFile(barrier, r);
                foreach (string f in presentFileNames) if (string.Equals(f, want, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                if (!found) { if (miss.Length > 0) miss.Append(','); miss.Append(r); }
            }
            return miss.ToString();
        }

        public static string Verdict(bool invalid, bool aborted, int checks, int fail, int err)
        {
            if (invalid) return "INVALID";
            if (aborted) return "ABORTED";
            if (fail > 0 || err > 0) return "FAIL";
            if (checks == 0) return "NOCHECKS";
            return "PASS";
        }

        public static string Cut(string s, int max) { if (s == null) return ""; s = s.Replace("\r", " ").Replace("\n", " "); return s.Length <= max ? s : s.Substring(0, max) + "..."; }

        // one result line: STATUS (5 wide) Lnnn t=12.3s <step> | <detail>
        public static string Line(string status, int line, float t, string text, string detail)
        {
            StringBuilder b = new StringBuilder(); b.Append(status.PadRight(5)).Append(" L").Append(line.ToString(CultureInfo.InvariantCulture).PadLeft(3))
                .Append(" t=").Append(t.ToString("F1", CultureInfo.InvariantCulture)).Append("s ").Append(Cut(text, 200));
            if (!string.IsNullOrEmpty(detail)) b.Append(" | ").Append(Cut(detail, 400));
            return b.ToString();
        }
    }

    // the result of one run: counts + the lines (the glue appends every line to the result file at once, so a crash still leaves evidence)
    public sealed class BotReport
    {
        public int Checks, Pass, Fail, Actions, Ok, Err, Skip, Facts; public bool Invalid, Aborted;
        public readonly List<string> Lines = new List<string>();
        public string Verdict { get { return BotRules.Verdict(Invalid, Aborted, Checks, Fail, Err); } }
        public string Summary(float duration)
        {
            return "SUMMARY " + Verdict + " checks=" + Checks + " pass=" + Pass + " fail=" + Fail + " actions=" + Actions + " ok=" + Ok + " err=" + Err + " skipped=" + Skip + " duration=" + duration.ToString("F1", CultureInfo.InvariantCulture) + "s" + (Facts > 0 ? " facts=" + Facts : "");
        }
    }

    public sealed class BotLogEntry { public long Seq; public string Source; public string Text; }

    // thread-safe ring of recent log lines from every source (perf-trace PtLog, BepInEx, Unity, brix.log). Lines the bot writes itself start with
    // "BOT " and are never stored, so a regex can never match its own echo. Old lines fall out when the ring is full; the patterns of the running
    // script are WATCHED: every new line is matched against them as it arrives and the last WatchKeep hits are kept, so 'expect log' and 'nolog'
    // stay exact in a long run even after the ring overflowed. Add() may be called from any thread (BepInEx / Unity threaded log callbacks).
    public sealed class BotLogRing
    {
        sealed class Watch { public Regex Re; public readonly List<BotLogEntry> Hits = new List<BotLogEntry>(); public long From, Known; public bool Back; }   // 0.4.27 review: From = last line before the watch existed; Back = also filled from the ring ('since boot'), back to line Known
        readonly object gate = new object(); readonly BotLogEntry[] buf; long next = 1; public readonly int Capacity; public const int MaxLine = 1500, WatchKeep = 64;
        readonly List<Watch> watches = new List<Watch>(); Watch[] watchSnap = new Watch[0];
        public BotLogRing(int capacity) { Capacity = Math.Max(16, capacity); buf = new BotLogEntry[Capacity]; }
        public long LastSeq { get { lock (gate) return next - 1; } }
        public long OldestSeq { get { lock (gate) return Math.Max(1, next - Capacity); } }
        public static bool IsOwn(string text) { return text != null && (text.StartsWith("BOT ", StringComparison.Ordinal) || text.IndexOf("] BOT ", StringComparison.Ordinal) >= 0); }
        public void WatchPattern(Regex re) { WatchPattern(re, false); }
        // 0.4.27 review: fromBoot = also match the lines that are already in the ring ('since boot'). A watch collects only the lines that arrive after it was made and Find() never
        // scans the ring for a watched pattern, so 'since boot' saw nothing older than the run itself: 'log' failed and 'nolog' passed against a line logged before the script started.
        public void WatchPattern(Regex re, bool fromBoot)
        {
            if (re == null) return;
            Watch w = null; List<BotLogEntry> old = null;
            lock (gate)
            {
                foreach (Watch x in watches) if (ReferenceEquals(x.Re, re)) { w = x; break; }
                if (w == null) { w = new Watch(); w.Re = re; w.From = next - 1; w.Known = next; watches.Add(w); watchSnap = watches.ToArray(); }
                if (!fromBoot || w.Back) return;
                w.Back = true; w.Known = Math.Min(w.From + 1, Math.Max(1, next - Capacity)); old = new List<BotLogEntry>();
                for (long s = w.Known; s <= w.From; s++) { BotLogEntry e = buf[(int)(s % Capacity)]; if (e != null && e.Seq == s) old.Add(e); }
            }
            List<BotLogEntry> hits = new List<BotLogEntry>();
            for (int i = 0; i < old.Count; i++) if (re.IsMatch(old[i].Text)) hits.Add(old[i]);   // outside the lock, like Add()
            if (hits.Count == 0) return; if (hits.Count > WatchKeep) hits.RemoveRange(0, hits.Count - WatchKeep);   // keep the newest, as Add() does
            lock (gate) { w.Hits.InsertRange(0, hits); while (w.Hits.Count > WatchKeep) w.Hits.RemoveAt(0); }
        }
        public void ClearWatches() { lock (gate) { watches.Clear(); watchSnap = new Watch[0]; } }
        public void Add(string source, string text)
        {
            if (text == null || IsOwn(text)) return;
            if (text.Length > MaxLine) text = text.Substring(0, MaxLine);
            BotLogEntry e = new BotLogEntry(); e.Source = source ?? "any"; e.Text = text; Watch[] ws;
            lock (gate) { e.Seq = next; buf[(int)(next % Capacity)] = e; next++; ws = watchSnap; }
            for (int i = 0; i < ws.Length; i++)
            {
                if (!ws[i].Re.IsMatch(text)) continue;
                lock (gate) { ws[i].Hits.Add(e); if (ws[i].Hits.Count > WatchKeep) ws[i].Hits.RemoveAt(0); }
            }
        }
        public void Clear() { lock (gate) { for (int i = 0; i < buf.Length; i++) buf[i] = null; foreach (Watch w in watches) w.Hits.Clear(); } }
        // first line after afterSeq that matches (and comes from 'from'); overflowed = some lines after afterSeq were dropped and the pattern was not watched
        public BotLogEntry Find(Regex re, string from, long afterSeq, out bool overflowed)
        {
            List<BotLogEntry> snap = new List<BotLogEntry>(); long oldest; bool watched = false, lost = false;
            lock (gate)
            {
                foreach (Watch w in watches)
                {
                    if (!ReferenceEquals(w.Re, re)) continue;
                    watched = true; foreach (BotLogEntry h in w.Hits) if (h.Seq > afterSeq) snap.Add(h);
                    lost = w.Back && afterSeq + 1 < w.Known;   // 0.4.27 review: 'since boot' asked for lines that had left the ring before the watch was filled
                    break;
                }
                oldest = Math.Max(1, next - Capacity);
                if (!watched) for (long s = Math.Max(afterSeq + 1, oldest); s < next; s++) { BotLogEntry e = buf[(int)(s % Capacity)]; if (e != null && e.Seq == s) snap.Add(e); }
            }
            overflowed = watched ? lost : afterSeq + 1 < oldest;
            foreach (BotLogEntry e in snap)
            {
                if (from != null && from != "any" && e.Source != from) continue;
                if (watched || re.IsMatch(e.Text)) return e;
            }
            return null;
        }
    }

    // pixel colour classes for screenshots (pure; hue in degrees). Tuned for the game's blue blueprint hologram, the orange anti hologram and
    // the red vs orange ward gem; the class shares are evidence next to the PNG, the lead still LOOKS at the picture.
    public static class BotColor
    {
        public static void ToHsv(int r, int g, int b, out float h, out float s, out float v)
        {
            float rf = r / 255f, gf = g / 255f, bf = b / 255f;
            float max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
            v = max; s = max <= 0f ? 0f : d / max; h = 0f;
            if (d <= 0f) return;
            if (max == rf) h = 60f * (((gf - bf) / d) % 6f);
            else if (max == gf) h = 60f * ((bf - rf) / d + 2f);
            else h = 60f * ((rf - gf) / d + 4f);
            if (h < 0f) h += 360f;
        }

        // vivid gates measured by the research agent on 5 real in-game screenshots (bot-research\evidence-BotEvidence.cs): with them the whole-frame
        // background stays <= 2 % per class, while a loose s >= 0.18 rule called the sea 91 % "blue" and brown dirt 7 % "orange".
        public static string Classify(int r, int g, int b)
        {
            float h, s, v; ToHsv(r, g, b, out h, out s, out v);
            if ((h >= 335f || h < 15f) && s >= 0.50f && v >= 0.40f) return "red";
            if (h >= 15f && h < 45f && s >= 0.55f && v >= 0.50f) return "orange";     // brown dirt is about h 26, s 0.5-0.57: borderline, hence the ring
            if (h >= 180f && h < 250f && s >= 0.45f && v >= 0.60f) return "blue";     // dark sea, sky and snow stay out
            if (v < 0.12f) return "dark";
            if (s < 0.18f) return v > 0.85f ? "white" : "gray";
            if (h >= 45f && h < 70f && s >= 0.45f && v >= 0.45f) return "yellow";
            if (h >= 70f && h < 170f && s >= 0.35f && v >= 0.30f) return "green";
            return "other";
        }

        // the box around the target against the ring of background around it: "red" / "orange" / "blue" only when that class owns >= minPct of the
        // box, is margin x the runner-up and stands out from the ring; else "none", "mixed" or "background" (ore, pumpkins, crystals, water around it)
        public static string Verdict(BotColorStats inner, BotColorStats ring, double minPct, double margin)
        {
            string[] vivid = { "red", "orange", "blue" }; string best = null; double bestP = 0, second = 0;
            foreach (string c in vivid) { double p = inner.Percent(c); if (p > bestP) { second = bestP; bestP = p; best = c; } else if (p > second) second = p; }
            if (best == null || bestP < minPct) return "none";
            if (second > 0 && bestP < second * margin) return "mixed";
            if (ring != null && ring.Total > 0 && bestP < ring.Percent(best) * margin + minPct) return "background";
            return best;
        }
    }

    public sealed class BotColorStats
    {
        readonly Dictionary<string, int> counts = new Dictionary<string, int>(); public int Total;
        public void Add(int r, int g, int b) { string c = BotColor.Classify(r, g, b); int n; counts.TryGetValue(c, out n); counts[c] = n + 1; Total++; }
        public double Percent(string colour) { int n; if (Total == 0 || !counts.TryGetValue(colour, out n)) return 0.0; return 100.0 * n / Total; }
        public string Summary()
        {
            List<KeyValuePair<string, int>> l = new List<KeyValuePair<string, int>>(counts);
            l.Sort(delegate(KeyValuePair<string, int> x, KeyValuePair<string, int> y) { int c = y.Value.CompareTo(x.Value); return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key); });
            StringBuilder b = new StringBuilder(); b.Append("px=").Append(Total);
            foreach (KeyValuePair<string, int> kv in l) b.Append(' ').Append(kv.Key).Append('=').Append((100.0 * kv.Value / Math.Max(1, Total)).ToString("F1", CultureInfo.InvariantCulture)).Append('%');
            return b.ToString();
        }
    }
}
