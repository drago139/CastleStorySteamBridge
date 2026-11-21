using System;
using CastleStorySteam;

// 0.4.17 kick / rejoin fix: the decisions only (host recycle on a new hello, guest hold on a Steam P2P failure). No Unity, no Steam.
// Build: csc ResilienceTests.cs ResilienceRules.cs
static class ResilienceTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    static int Main()
    {
        try
        {
            // ---- HOST: a new hello from a guest that still has a bridge
            Check(ResilienceRules.RecycleOnHello(true, 5000, 20000), "the 2026-11-07 case: old bridge carried data and was silent 20 s -> recycle");
            Check(ResilienceRules.RecycleOnHello(true, 1, 3000) && !ResilienceRules.RecycleOnHello(true, 1, 2999), "the quiet limit is 3 s, inclusive");
            Check(!ResilienceRules.RecycleOnHello(true, 0, 60000), "an old bridge that never carried game data holds no stale UNET connection: keep it (duplicate hellos of one join)");
            Check(!ResilienceRules.RecycleOnHello(true, 5000, 0) && !ResilienceRules.RecycleOnHello(true, 5000, 800), "a duplicate hello while data still flows never kills a healthy session");
            Check(!ResilienceRules.RecycleOnHello(false, 5000, 20000), "switch off: the old bridge is reused (0.4.15 behaviour)");
            Check(!ResilienceRules.RecycleOnHello(true, 5000, -5), "a negative age (clock wrap) is not 'silent'");
            int recycled = 0;
            foreach (bool on in new bool[] { false, true })
                foreach (long rec in new long[] { 0, 1, 99999 })
                    foreach (int q in new int[] { -1, 0, 2999, 3000, 90000 })
                        if (ResilienceRules.RecycleOnHello(on, rec, q)) recycled++;
            Check(recycled == 4, "over switch x received x quiet only (on, received > 0, quiet >= 3000) recycles: 2 x 2 = 4 combinations");

            // ---- GUEST: the hold episode
            ResilienceRules.FailureHold h = new ResilienceRules.FailureHold();
            long t0 = 1000000;
            long noData = t0 - 60000;   // last host data a minute before the first failure
            Check(h.OnFailure(t0, noData, 120), "first failure of a running session is held");
            Check(h.OnFailure(t0 + 25000, noData, 120) && h.OnFailure(t0 + 119000, noData, 120), "later failures inside the hold are held too");
            Check(!h.OnFailure(t0 + 120000, noData, 120), "at the end of the hold the failure is honoured (leave as before)");
            Check(h.Count == 4, "the failure events are counted");
            Check(h.Open(noData) && !h.Expired(t0 + 119999, noData, 120) && h.Expired(t0 + 120000, noData, 120), "timer fallback: expires exactly when the hold is over and nothing came");
            Check(!h.Open(t0 + 5000) && !h.Expired(t0 + 500000, t0 + 5000, 120), "data from the host after the episode began closes it: no timer leave");

            // the host came back (data at t0+40 s), later a second outage: a NEW episode with its own 120 s
            ResilienceRules.FailureHold g = new ResilienceRules.FailureHold();
            Check(g.OnFailure(t0, noData, 120), "outage 1");
            Check(g.OnFailure(t0 + 300000, t0 + 40000, 120), "outage 2, 300 s later, data came in between: a fresh episode, held again");
            Check(g.StartMs == t0 + 300000 && g.Count == 1, "the new episode starts at its own first failure");
            Check(!g.OnFailure(t0 + 300000 + 130000, t0 + 40000, 120), "and ends 120 s after that");

            // switch off: leave at once
            ResilienceRules.FailureHold z = new ResilienceRules.FailureHold();
            Check(!z.OnFailure(t0, noData, 0) && !z.OnFailure(t0, noData, -5) && !z.Open(noData) && !z.Expired(t0 + 999999, noData, 0), "hold 0 = the 0.4.15 behaviour, no state kept");

            // Reset
            h.Reset(); Check(!h.Open(0) && h.Count == 0, "Reset clears the episode");

            // only a session that really ran is held
            Check(ResilienceRules.HoldApplies(true, 1) && !ResilienceRules.HoldApplies(true, 0) && !ResilienceRules.HoldApplies(false, 100) && !ResilienceRules.HoldApplies(false, 0), "a join that never worked still fails at once");

            // Environment.TickCount arithmetic across the 24.9-day wrap
            Check(ResilienceRules.LastDataMs(1000000, int.MinValue + 5, int.MaxValue - 4) == 1000000 - 10, "tick difference survives the int wrap");
            Check(ResilienceRules.LastDataMs(1000000, 5000, 1000) == 1000000 - 4000, "plain tick difference");

            // simulation: 2000 random outages, the verdict is monotonic (held until the limit, never held after it)
            Random rnd = new Random(20261107);
            for (int i = 0; i < 2000; i++)
            {
                int hold = 1 + rnd.Next(600);
                ResilienceRules.FailureHold s = new ResilienceRules.FailureHold();
                long start = rnd.Next(1000, 100000000), last = start - 1 - rnd.Next(100000);
                Check(s.OnFailure(start, last, hold), "first failure always held when hold > 0 (" + i + ")");
                long ev = start; bool leftNow = false;
                for (int k = 0; k < 12; k++)
                {
                    ev += rnd.Next(1, hold * 400 + 1);
                    bool keep = s.OnFailure(ev, last, hold);
                    bool expect = ev - start < hold * 1000L;
                    Check(keep == expect, "verdict follows the clock, run " + i + " event " + k);
                    if (!keep) leftNow = true; else Check(!leftNow, "never held again after leaving, run " + i);
                }
            }

            Console.WriteLine("ALL PASS (" + checks + " checks; host recycle rule, guest hold episodes, tick wrap, 2000 random outages)");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
    }
}
