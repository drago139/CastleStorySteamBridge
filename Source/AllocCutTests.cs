using System;
using System.Collections.Generic;

// Unit tests for AllocCutRules.cs (guard mode, dead-entry purge, swallow rule, repeated-error throttle). Build + run (no game files needed):
//   csc /nologo /out:AllocCutTests.exe AllocCutRules.cs AllocCutTests.cs  &&  AllocCutTests.exe
namespace CastleStorySteam
{
    static class AllocCutTests
    {
        static int fails, checks;
        static void Eq(long expect, long got, string what) { checks++; if (expect != got) { fails++; Console.WriteLine("FAIL " + what + ": expected " + expect + ", got " + got); } }
        static void EqS(string expect, string got, string what) { checks++; if (expect != got) { fails++; Console.WriteLine("FAIL " + what + ": expected [" + expect + "], got [" + got + "]"); } }
        static void Ok(bool cond, string what) { checks++; if (!cond) { fails++; Console.WriteLine("FAIL " + what); } }

        // stands for UnityEngine.Object: a destroyed component compares equal to null in the game
        sealed class Vision { public string Id; public bool Alive; public Vision(string id, bool alive) { Id = id; Alive = alive; } }
        static readonly Predicate<Vision> alive = delegate(Vision x) { return x != null && x.Alive; };

        static string Ids(List<Vision> l) { string s = ""; for (int i = 0; i < l.Count; i++) s += (i > 0 ? "," : "") + (l[i] == null ? "null" : l[i].Id); return s; }

        static int Main()
        {
            // ---- guard mode
            Eq(0, AcRules.NormalizeMode(-3), "mode -3 -> 0"); Eq(0, AcRules.NormalizeMode(0), "mode 0"); Eq(1, AcRules.NormalizeMode(1), "mode 1"); Eq(2, AcRules.NormalizeMode(2), "mode 2"); Eq(2, AcRules.NormalizeMode(9), "mode 9 -> 2");
            Ok(!AcRules.Counts(0), "off does not count"); Ok(AcRules.Counts(1), "observe counts"); Ok(AcRules.Counts(2), "on counts");
            Ok(!AcRules.Acts(0, false), "off never acts"); Ok(!AcRules.Acts(1, false), "observe never acts"); Ok(AcRules.Acts(2, false), "on acts");
            Ok(!AcRules.Acts(2, true), "the kill flag turns 'on' into observe");

            // ---- swallow rule: known error kind AND the purge really removed something
            Ok(AcRules.ShouldSwallow(true, 1), "known kind, removed 1 -> swallow");
            Ok(!AcRules.ShouldSwallow(true, 0), "known kind but nothing removed -> the game must see the error");
            Ok(!AcRules.ShouldSwallow(false, 5), "unknown kind -> never swallow");

            // ---- purge: removes the dead entries in place, keeps the order, touches nothing when nothing is dead
            List<Vision> l = new List<Vision>();
            l.Add(new Vision("a", true)); l.Add(new Vision("b", false)); l.Add(new Vision("c", true)); l.Add(null); l.Add(new Vision("d", false)); l.Add(new Vision("e", true));
            Eq(3, AcRules.CountDead(l, alive), "count dead (read only): b, null, d");
            EqS("a,b,c,null,d,e", Ids(l), "count dead does not change the list");
            Eq(3, AcRules.Compact(l, alive), "compact removes 3");
            EqS("a,c,e", Ids(l), "compact keeps the order of the live entries");
            Eq(0, AcRules.Compact(l, alive), "second compact is a no-op (idempotent)");
            EqS("a,c,e", Ids(l), "list unchanged by the second compact");
            Eq(0, AcRules.CountDead(l, alive), "nothing dead afterwards");

            List<Vision> allDead = new List<Vision>(); allDead.Add(new Vision("x", false)); allDead.Add(null); allDead.Add(new Vision("y", false));
            Eq(3, AcRules.Compact(allDead, alive), "all dead -> all removed"); Eq(0, allDead.Count, "list empty");
            List<Vision> none = new List<Vision>(); Eq(0, AcRules.Compact(none, alive), "empty list"); Eq(0, AcRules.CountDead(none, alive), "empty list count");
            Eq(0, AcRules.Compact<Vision>(null, alive), "null list is tolerated"); Eq(0, AcRules.CountDead<Vision>(null, alive), "null list count is tolerated");
            List<Vision> healthy = new List<Vision>(); healthy.Add(new Vision("p", true)); healthy.Add(new Vision("q", true));
            Eq(0, AcRules.Compact(healthy, alive), "healthy list"); EqS("p,q", Ids(healthy), "healthy list untouched");
            List<Vision> lastDead = new List<Vision>(); lastDead.Add(new Vision("m", true)); lastDead.Add(new Vision("n", false));
            Eq(1, AcRules.Compact(lastDead, alive), "dead entry at the end"); EqS("m", Ids(lastDead), "end trimmed");
            List<Vision> firstDead = new List<Vision>(); firstDead.Add(new Vision("m", false)); firstDead.Add(new Vision("n", true));
            Eq(1, AcRules.Compact(firstDead, alive), "dead entry at the start"); EqS("n", Ids(firstDead), "start trimmed");

            // several factions (the real structure: SharedFactionVision.AllVisions per faction): purge each list, sum the dead
            List<List<Vision>> factions = new List<List<Vision>>();
            for (int f = 0; f < 5; f++) { List<Vision> v = new List<Vision>(); for (int i = 0; i < 40; i++) v.Add(new Vision("f" + f + "u" + i, (i + f) % 4 != 0)); factions.Add(v); }
            int dead = 0, units = 0; for (int f = 0; f < factions.Count; f++) { units += factions[f].Count; dead += AcRules.CountDead(factions[f], alive); }
            Eq(200, units, "units before"); Eq(50, dead, "dead found over 5 factions (every 4th entry)");
            int removed = 0; for (int f = 0; f < factions.Count; f++) removed += AcRules.Compact(factions[f], alive);
            Eq(50, removed, "removed over 5 factions"); int left = 0; for (int f = 0; f < factions.Count; f++) left += factions[f].Count; Eq(150, left, "150 live entries remain");
            // a large list (storm case): 100000 entries, 90 % dead
            List<Vision> big = new List<Vision>(); for (int i = 0; i < 100000; i++) big.Add(new Vision("b" + i, i % 10 == 0));
            Eq(90000, AcRules.Compact(big, alive), "big list compacted"); Eq(10000, big.Count, "10000 remain"); EqS("b0", big[0].Id, "first live entry kept"); EqS("b99990", big[9999].Id, "last live entry kept");

            // ---- throttle key: digits are ignored, text and exception type are not
            Eq(AcRules.ThrottleKey("NullReferenceException at frame 1234 faction 7", 0, 0), AcRules.ThrottleKey("NullReferenceException at frame 98 faction 12345", 0, 0), "digits ignored");
            Ok(AcRules.ThrottleKey("NullReferenceException at frame 1", 0, 0) != AcRules.ThrottleKey("MissingReferenceException at frame 1", 0, 0), "different text, different key");
            Ok(AcRules.ThrottleKey("same", 0, 111) != AcRules.ThrottleKey("same", 0, 222), "different exception type, different key");
            Eq(AcRules.ThrottleKey("same", 0, 111), AcRules.ThrottleKey("same", 0, 111), "same text and type, same key");
            Ok(AcRules.ThrottleKey("same", 0, 0) != AcRules.ThrottleKey("same", 0, 111), "an exception changes the key");
            Eq(5555, AcRules.ThrottleKey(null, 5555, 0), "non-string message: the type hash is the key");
            Ok(AcRules.ThrottleKey(null, 5555, 7) != AcRules.ThrottleKey(null, 5555, 0), "non-string message with exception");
            string head = new string('x', 160);
            Eq(AcRules.ThrottleKey(head + "AAA", 0, 0), AcRules.ThrottleKey(head + "BBB", 0, 0), "only the first 160 characters count");
            Ok(AcRules.ThrottleKey("x" + head, 0, 0) != AcRules.ThrottleKey("y" + head, 0, 0), "a difference inside the first 160 characters counts");
            Eq(AcRules.ThrottleKey("", 0, 0), AcRules.ThrottleKey("12345", 0, 0), "digits-only message equals the empty message");

            // ---- throttle: limit 3 per 60 s window, summary when a window with drops ends
            AcThrottle t = new AcThrottle(3, 60000); string closed; int k = 42;
            Ok(t.Allow(k, 1000, "boom 1", null, out closed), "1st passes"); Ok(closed == null, "no summary yet");
            Ok(t.Allow(k, 1100, "boom 2", null, out closed), "2nd passes"); Ok(t.Allow(k, 1200, "boom 3", null, out closed), "3rd passes");
            for (int i = 0; i < 7; i++) Ok(!t.Allow(k, 1300 + i, "boom", null, out closed), "dropped " + i);
            Eq(7, t.DroppedTotal, "7 dropped"); Ok(closed == null, "no summary inside the window");
            Ok(!t.Allow(k, 60999, "boom", null, out closed), "still the same window at 59.999 s"); Eq(8, t.DroppedTotal, "8 dropped");
            Ok(t.Allow(k, 61000, "boom", null, out closed), "window ended: passes again"); Ok(closed != null && closed.StartsWith("dropped 8 ERROR events in 60 s: boom 1"), "summary of the ended window: [" + closed + "]");
            Ok(t.Allow(k, 61100, "boom", null, out closed), "2nd of the new window passes"); Ok(closed == null, "no second summary");
            // another key is independent
            Ok(t.Allow(7, 61200, "other", null, out closed), "other key passes"); Eq(8, t.DroppedTotal, "drops are counted per throttle, not reset");
            // a window without drops produces no summary
            AcThrottle quiet = new AcThrottle(5, 1000); quiet.Allow(1, 0, "x", null, out closed); quiet.Allow(1, 5000, "x", null, out closed); Ok(closed == null, "no drops, no summary");
            // limit 0 drops everything
            AcThrottle zero = new AcThrottle(0, 1000); Ok(!zero.Allow(1, 0, "x", null, out closed), "limit 0 drops"); Eq(1, zero.DroppedTotal, "limit 0 counts");
            // Environment.TickCount wraps around: the window arithmetic must be wrap safe
            AcThrottle wrap = new AcThrottle(1, 60000); int s0 = int.MaxValue - 10;
            Ok(wrap.Allow(1, s0, "w", null, out closed), "wrap: first"); Ok(!wrap.Allow(1, s0 + 5, "w", null, out closed), "wrap: second dropped");
            Ok(wrap.Allow(1, unchecked(s0 + 60001), "w", null, out closed), "wrap: window ended across the int overflow"); Ok(closed != null && closed.StartsWith("dropped 1 "), "wrap: summary [" + closed + "]");
            // text of the sample: newlines removed, cut to 90, alternative text for non-string messages
            AcThrottle smp = new AcThrottle(1, 1000); smp.Allow(1, 0, "line one\nline two", null, out closed); smp.Allow(1, 1, "x", null, out closed); smp.Allow(1, 1000, "x", null, out closed);
            Ok(closed != null && closed.EndsWith(": line one line two"), "newline replaced: [" + closed + "]");
            AcThrottle lng = new AcThrottle(1, 1000); lng.Allow(1, 0, new string('z', 300), null, out closed); lng.Allow(1, 1, "x", null, out closed); lng.Allow(1, 1000, "x", null, out closed);
            Ok(closed != null && closed.EndsWith(": " + new string('z', 90)), "sample cut to 90 characters");
            AcThrottle alt = new AcThrottle(1, 1000); alt.Allow(1, 0, null, "LoggingEvent", out closed); alt.Allow(1, 1, null, "LoggingEvent", out closed); alt.Allow(1, 1000, null, null, out closed);
            Ok(closed != null && closed.EndsWith(": LoggingEvent"), "non-string message uses the alternative text: [" + closed + "]");
            // memory bound: more than 256 keys clear the table, the throttle keeps working
            AcThrottle many = new AcThrottle(1, 60000); for (int i = 0; i < 300; i++) many.Allow(i, 0, "m" + i, null, out closed);
            Ok(many.SlotCount <= 257, "slot table bounded (" + many.SlotCount + ")"); Ok(many.Allow(5000, 1, "n", null, out closed), "still works after the clear");

            Console.WriteLine((fails == 0 ? "ALL PASS" : "FAILURES: " + fails) + " (" + checks + " checks)");
            return fails == 0 ? 0 : 1;
        }
    }
}
