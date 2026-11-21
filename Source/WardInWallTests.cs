using System;
using CastleStorySteam;

// 0.4.19 WARD-IN-WALL: the decision only (WardInWallRules). No Unity.
// Build: csc WardInWallTests.cs WardInWallRules.cs
static class WardInWallTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    static int Main()
    {
        try
        {
            // ---- the 2026-11-07 case: dropped into a terrain wall, seen twice 1.5 s apart in the same cell -> removed
            Check(WardInWallRules.Judge(true, true, true, true, 0, false, 0f) == WardWallStep.Watch, "first in-wall check only starts watching");
            Check(WardInWallRules.Judge(true, true, true, true, 1, true, 0.5f) == WardWallStep.Wait, "second check sooner than 1.5 s waits");
            Check(WardInWallRules.Judge(true, true, true, true, 2, true, 1.5f) == WardWallStep.Remove, "in the same solid cell 1.5 s later -> remove");
            Check(WardInWallRules.Judge(true, true, true, true, 1, true, 30f) == WardWallStep.Remove, "a ward still in the same solid cell much later is removed on its second check");

            // ---- never remove
            Check(WardInWallRules.Judge(false, true, true, true, 5, true, 99f) == WardWallStep.Forget, "switch off: the game's behaviour");
            Check(WardInWallRules.Judge(true, false, true, true, 5, true, 99f) == WardWallStep.Forget, "not the host: guests never remove anything");
            Check(WardInWallRules.Judge(true, true, false, true, 5, true, 99f) == WardWallStep.Forget, "carried or stored ward (not on the ground) is never touched");
            Check(WardInWallRules.Judge(true, true, true, false, 5, true, 99f) == WardWallStep.Forget, "a ward whose body point is in a free or partial cell is fine (on the ground, on a block, on a tower)");
            Check(WardInWallRules.Judge(true, true, true, true, 1, false, 99f) == WardWallStep.Watch, "moved to another solid cell: the clock starts again, no removal on a single check");
            Check(WardInWallRules.Judge(true, true, true, true, 0, true, 99f) == WardWallStep.Watch, "no earlier hit: watch, even if the cell happens to match");
            Check(WardInWallRules.Judge(true, true, true, true, -3, true, 99f) == WardWallStep.Watch, "a negative count is no hit");
            Check(WardInWallRules.Judge(true, true, true, true, 1, true, -1f) == WardWallStep.Wait, "a negative age (clock reset) is not old enough");

            // exhaustive: Remove only when every guard holds
            int removes = 0;
            foreach (bool en in new bool[] { false, true })
                foreach (bool srv in new bool[] { false, true })
                    foreach (bool gr in new bool[] { false, true })
                        foreach (bool so in new bool[] { false, true })
                            foreach (int h in new int[] { 0, 1, 3 })
                                foreach (bool same in new bool[] { false, true })
                                    foreach (float s in new float[] { 0f, 1.49f, 1.5f, 20f })
                                        if (WardInWallRules.Judge(en, srv, gr, so, h, same, s) == WardWallStep.Remove) removes++;
            Check(removes == 4, "Remove = enabled, host, on ground, solid, hits>0 (1 or 3), same cell, >= 1.5 s (1.5 or 20): 2 x 2 = 4");

            // ---- what counts as solid
            Check(WardInWallRules.Solid(true, true, false, false), "full terrain counts even with blocks off");
            Check(WardInWallRules.Solid(true, false, true, true) && !WardInWallRules.Solid(true, false, true, false), "a full placed block counts only while WardInWallBlocks is on");
            Check(!WardInWallRules.Solid(false, true, false, true) && !WardInWallRules.Solid(false, false, true, true), "a partial cell (slope, stairs, ramp, plank) never counts");
            Check(!WardInWallRules.Solid(true, false, false, true), "full but neither terrain nor block: not counted");
            Check(WardInWallRules.Kind(true, false) == "terrain" && WardInWallRules.Kind(false, true) == "block" && WardInWallRules.Kind(false, false) == "other", "kind names for the log");

            // ---- the body point of a good drop (D-076 proof): Deposer rests the ward at surface + 0.5; checked point = + 0.3 more. Surface heights in a walkable cell X are
            // -0.5, -1/3, -1/6, 0, 1/6, 1/3 (niveaux levels/3 - 0.5): the point is X.y + s + 0.8, never on a .5 boundary, so it rounds to X (s = -0.5 or -1/3) or X+1 (empty above X).
            foreach (double s in new double[] { -0.5, -1.0 / 3, -1.0 / 6, 0, 1.0 / 6, 1.0 / 3 })
            {
                double p = s + 0.5 + WardInWallRules.CheckUp;   // relative to X.y
                double frac = p - Math.Floor(p);
                Check(Math.Abs(frac - 0.5) > 0.01, "surface " + s + ": the body point is never on a cell boundary");
                long r = (long)Math.Round(p, MidpointRounding.AwayFromZero);
                Check(r == 0 || r == 1, "surface " + s + ": the body point is in X or X+1");
            }
            // stuck case: no walkable cell found, the ward is set on top of the full cell R (s = +0.5): body point = R + 1.3 -> R+1 (inside the wall when the wall is >= 2 high)
            Check((long)Math.Round(0.5 + 0.5 + WardInWallRules.CheckUp, MidpointRounding.AwayFromZero) == 1, "stuck ward: body point in the cell above the full one");

            // ---- log limiter: first 40 in full, then every 10th
            Check(!WardInWallRules.LogLine(0) && WardInWallRules.LogLine(1) && WardInWallRules.LogLine(40), "1..40 logged");
            Check(!WardInWallRules.LogLine(41) && !WardInWallRules.LogLine(49) && WardInWallRules.LogLine(50) && WardInWallRules.LogLine(1000), "then every 10th");

            // ---- constants the glue relies on
            Check(WardInWallRules.CheckUp > 0f && WardInWallRules.CheckUp < 0.5f, "body point stays inside the cell the game rests the ward in (+0.5 above the surface)");
            Check(WardInWallRules.QuickSeconds < WardInWallRules.RecheckSeconds, "quick < recheck");

            Console.WriteLine("WardInWallTests: " + checks + " checks PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("WardInWallTests FAIL after " + checks + " checks: " + e.Message); return 1; }
    }
}
