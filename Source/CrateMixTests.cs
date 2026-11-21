using System;
using System.Collections.Generic;
using CastleStorySteam;

// CrateGuard bookkeeping: item kind names, crate summaries, the re-check verdict and the one-entry-per-crate re-check queue.
static class CrateMixTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    public static int Main()
    {
        try
        {
            Check(CrateMixRules.KindOf("RawStone (12)") == "RawStone", "numbered copy");
            Check(CrateMixRules.KindOf("RawStone(Clone)") == "RawStone", "clone suffix would make a false mixed crate");
            Check(CrateMixRules.KindOf("RawStone (Clone)") == "RawStone", "clone with space");
            Check(CrateMixRules.KindOf("RawStone") == "RawStone" && CrateMixRules.KindOf("RawTronStone") == "RawTronStone", "plain names");
            Check(CrateMixRules.KindOf(null) == "?" && CrateMixRules.KindOf("") == "?" && CrateMixRules.KindOf("(Clone)") == "?", "empty names");

            int kinds;
            List<string> crate = new List<string>();
            for (int i = 0; i < 40; i++) crate.Add(i % 3 == 0 ? "RawStone(Clone)" : "RawStone (" + i + ")");
            Check(CrateMixRules.Summary(crate, out kinds) == "RawStone x40" && kinds == 1, "one kind despite name variants");
            crate.Add("RawTronStone (3)");
            Check(CrateMixRules.Summary(crate, out kinds) == "RawStone x40, RawTronStone x1" && kinds == 2, "the logged 0.3.16 crate");
            List<string> reversed = new List<string>(crate); reversed.Reverse();
            Check(CrateMixRules.Summary(reversed, out kinds) == "RawStone x40, RawTronStone x1", "summary does not depend on item order");
            Check(CrateMixRules.Summary(new string[0], out kinds) == "" && kinds == 0, "empty crate");

            // Adjective types from ResourcesFactory.unity / StoneBlocksFactory.unity (recovered assets, read-only).
            string[] rawStone = { "Stones", "Encombrement", "Granulaire", "Libre", "StoredGeneric" };
            string[] rawTronStone = { "StoredGeneric", "Libre", "Granulaire", "Encombrement", "Stones" };
            string[] stoneBrick = { "Libre", "Encombrement", "StoneBlock" };
            Check(CrateMixRules.GameKind(rawStone) == CrateMixRules.GameKind(rawTronStone), "RawStone and RawTronStone are one game kind (logged 0.3.16 crate is legal)");
            Check(CrateMixRules.GameKind(rawStone) != CrateMixRules.GameKind(stoneBrick), "stone vs stone block are different kinds");
            Check(CrateMixRules.GameKind(new[] { "Libre", "Libre", "", null, "Stones" }) == "Libre+Stones", "duplicates and empty names ignored");
            Check(CrateMixRules.GameKind(new string[0]) == "?" && CrateMixRules.GameKind(null) == "?", "no adjectives");
            List<string> logged = new List<string>();
            for (int i = 0; i < 40; i++) logged.Add(CrateMixRules.GameKind(rawStone));
            logged.Add(CrateMixRules.GameKind(rawTronStone));
            Check(CrateMixRules.CountDistinct(logged) == 1, "the logged 40+1 crate counts as one kind: no warning");
            logged.Add(CrateMixRules.GameKind(stoneBrick));
            Check(CrateMixRules.CountDistinct(logged) == 2, "a really foreign item is still reported");
            Check(CrateMixRules.CountDistinct(new string[] { null, "?" }) == 1 && CrateMixRules.CountDistinct(new string[0]) == 0, "distinct count edge cases");

            Check(CrateMixRules.Verdict(2, 2, false).StartsWith("still mixed"), "lasting");
            Check(CrateMixRules.Verdict(2, 1, false).StartsWith("resolved"), "glitch");
            Check(CrateMixRules.Verdict(2, 0, false).StartsWith("resolved"), "emptied");
            Check(CrateMixRules.Verdict(2, 3, true) == "crate gone", "destroyed crate");

            CrateRecheckQueue<string> q = new CrateRecheckQueue<string>();
            Check(q.Add(7, "crateA", 10f, "a", 1), "first add");
            Check(!q.Add(7, "crateA", 11f, "a2", 2) && q.Count == 1, "same crate not re-armed");
            Check(q.Add(8, "crateB", 12f, "b", 3) && q.Count == 2, "second crate");
            Check(q.TakeDue(14.9f).Count == 0, "nothing due before 5 s");
            List<CrateRecheckQueue<string>.Entry> due = q.TakeDue(15f);
            Check(due.Count == 1 && due[0].Id == 7 && due[0].Before == "a" && due[0].Report == 1 && q.Count == 1, "first due exactly at 5 s");
            Check(q.Add(7, "crateA", 15f, "again", 4), "crate can be armed again after its check");
            due = q.TakeDue(100f);
            Check(due.Count == 2 && due[0].Id == 8 && due[1].Id == 7 && q.Count == 0, "due entries keep arrival order");
            q.Add(1, "x", 0f, "", 1); q.Clear(); Check(q.Count == 0 && q.TakeDue(1000f).Count == 0, "clear");

            // Random flood: every crate is checked exactly once per arming, never early, and the queue never holds a crate twice.
            Random r = new Random(9031);
            CrateRecheckQueue<int> flood = new CrateRecheckQueue<int>();
            Dictionary<int, float> armedAt = new Dictionary<int, float>();
            float now = 0f; int taken = 0, armed = 0;
            for (int step = 0; step < 20000; step++)
            {
                now += (float)r.NextDouble() * 0.5f;
                int id = r.Next(30);
                if (flood.Add(id, id, now, "", step)) { Check(!armedAt.ContainsKey(id), "armed twice"); armedAt[id] = now; armed++; }
                else Check(armedAt.ContainsKey(id), "refused a crate that is not waiting");
                Check(flood.Count == armedAt.Count && flood.Count <= 30, "queue size");
                foreach (CrateRecheckQueue<int>.Entry e in flood.TakeDue(now))
                {
                    Check(armedAt.ContainsKey(e.Id) && now - armedAt[e.Id] >= CrateMixRules.RecheckSeconds - 0.0001f, "checked early");
                    armedAt.Remove(e.Id); taken++;
                }
            }
            taken += flood.TakeDue(float.MaxValue).Count;
            Check(taken == armed, "every arming checked once");
            Console.WriteLine("PASS: " + checks + " crate guard checks (kind names, summaries, verdicts, re-check queue, 20000-step random flood).");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
    }
}
