using System;
using System.Collections.Generic;
using CastleStorySteam;

// 0.4.20 SAVE-MP: the rules only (SaveMapMpRules). No Unity.
// Build: csc SaveMapMpTests.cs SaveMapMpRules.cs
static class SaveMapMpTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static int Count(string s, string sub) { int n = 0, i = 0; while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; } return n; }

    static int Main()
    {
        try
        {
            // guards: only the host of a ready MP match
            Check(SaveMapMpRules.Allowed(true, true, true, true, true, true), "host, MP, in match, level ready, map known: allowed");
            int allowed = 0;
            foreach (bool a in new bool[] { false, true }) foreach (bool b in new bool[] { false, true }) foreach (bool c in new bool[] { false, true })
                foreach (bool d in new bool[] { false, true }) foreach (bool e in new bool[] { false, true }) foreach (bool f in new bool[] { false, true })
                    if (SaveMapMpRules.Allowed(a, b, c, d, e, f)) allowed++;
            Check(allowed == 1, "exactly one of 64 combinations is allowed");
            Check(SaveMapMpRules.Why(true, true, false, true, true, true).Contains("хост"), "guest: 'only the host'");
            Check(SaveMapMpRules.Why(false, true, true, true, true, true).Contains("SaveMapInMultiplayer"), "switch off names the switch");
            Check(SaveMapMpRules.Why(true, false, true, true, true, true) != null, "single player: not this button (the game has its own save)");

            // names: unique, never the same folder twice
            DateTime t = new DateTime(2026, 11, 7, 23, 5, 9);
            Check(SaveMapMpRules.Name(t, 1) == "MP_2026-11-07_23-05-09", "name = MP_<date>_<time>");
            Check(SaveMapMpRules.Name(t, 2) == "MP_2026-11-07_23-05-09_2", "second try adds _2");
            Check(SaveMapMpRules.Name(t, 0) == SaveMapMpRules.Name(t, 1), "attempt 0 = first");
            foreach (char ch in SaveMapMpRules.Name(t, 17)) Check(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-', "only safe folder characters");
            HashSet<string> taken = new HashSet<string>();
            Check(SaveMapMpRules.PickName(t, delegate(string n) { return taken.Contains(n); }) == "MP_2026-11-07_23-05-09", "free name taken first");
            taken.Add("MP_2026-11-07_23-05-09"); taken.Add("MP_2026-11-07_23-05-09_2");
            Check(SaveMapMpRules.PickName(t, delegate(string n) { return taken.Contains(n); }) == "MP_2026-11-07_23-05-09_3", "existing folders are skipped, never overwritten");
            Check(SaveMapMpRules.PickName(t, delegate(string n) { return true; }) == null, "all taken: null (no save, no overwrite)");
            Check(SaveMapMpRules.Name(t, 1) != SaveMapMpRules.Name(t.AddSeconds(1), 1), "next second: a different name");

            // double click
            Check(!SaveMapMpRules.Busy(10f, 0f), "first press");
            Check(SaveMapMpRules.Busy(11f, 10f), "press 1 s later ignored");
            Check(!SaveMapMpRules.Busy(13.5f, 10f), "press 3.5 s later saves again");

            // GameMenu.lua patch
            string crlf = "x\r\nend\r\n\r\n---screenshot\r\ndo\r\nlocal h = ButtonHandle.New()\r\n";
            int c1; string p1 = SaveMapMpRules.Patch(crlf, out c1);
            Check(c1 == 1 && p1 != crlf, "unique anchor: patched");
            Check(p1.IndexOf(SaveMapMpRules.Marker, StringComparison.Ordinal) < p1.IndexOf("\n---screenshot", StringComparison.Ordinal), "block goes before ---screenshot");
            Check(p1.EndsWith("---screenshot\r\ndo\r\nlocal h = ButtonHandle.New()\r\n"), "the screenshot block is unchanged");
            Check(Count(p1, "\r\n") == Count(p1, "\n"), "CRLF file stays CRLF");
            Check(p1.Contains("h.Visible = ||SB_SaveMapAllowed()") && p1.Contains("h.OnAction = ||SB_SaveMap()"), "host-only visibility + action callback");
            Check(p1.Contains("if SB_SaveMapAllowed and SB_SaveMap then"), "no callbacks (old Script): block skipped, no Lua error");
            Check(Count(p1, "\n\tdo") == 1 && Count(p1, "\n\tend") == 1 && Count(p1, "\nend\r\n") == 2, "do/end and if/end balanced");
            int c2; string p2 = SaveMapMpRules.Patch(p1, out c2);
            Check(c2 == 1 && object.ReferenceEquals(p2, p1), "already patched: unchanged");
            string lf = "a\n---screenshot\ndo\n"; int c3; string p3 = SaveMapMpRules.Patch(lf, out c3);
            Check(c3 == 1 && !p3.Contains("\r"), "LF file patched with LF");
            int c4; string two = crlf + "---screenshot\r\n"; Check(SaveMapMpRules.Patch(two, out c4) == two && c4 == 2, "two anchors: unchanged, count 2");
            int c5; Check(SaveMapMpRules.Patch("---screenshot_more\r\n\n---screenshots\r\n", out c5) == "---screenshot_more\r\n\n---screenshots\r\n" && c5 == 0, "similar lines are not the anchor");
            int c6; Check(SaveMapMpRules.Patch(null, out c6) == null && c6 == 0, "null code");

            Check(SaveMapMpRules.LogLine(true, "MP_x", "12 ms").StartsWith("SAVE-MP saved MP_x"), "log tag SAVE-MP");
            Check(SaveMapMpRules.LogLine(false, null, "guest").StartsWith("SAVE-MP FAILED -"), "failure log line");
            Console.WriteLine("SaveMapMpTests: " + checks + " checks PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("SaveMapMpTests FAIL after " + checks + " checks: " + e.Message); return 1; }
    }
}
