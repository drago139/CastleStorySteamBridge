using System;
using CastleStorySteam;

// 0.4.20 LOOSE-FETCH: the decision only (LooseFetchRules). No Unity.
// Build: csc LooseFetchTests.cs LooseFetchRules.cs
static class LooseFetchTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    static int Main()
    {
        try
        {
            float[] cx = { 0f, 200f }, cz = { 0f, 0f };
            float near = LooseFetchRules.NearestSqrXZ(59f, 0f, cx, cz, 2), far = LooseFetchRules.NearestSqrXZ(100f, 0f, cx, cz, 2);
            Check(Math.Abs(near - 59f * 59f) < 0.01f, "nearest crystal distance (59 m)");
            Check(Math.Abs(far - 100f * 100f) < 0.01f, "100 m from both crystals");
            Check(Math.Abs(LooseFetchRules.NearestSqrXZ(170f, 0f, cx, cz, 2) - 30f * 30f) < 0.01f, "the second crystal counts too");
            Check(LooseFetchRules.NearestSqrXZ(1f, 1f, cx, cz, 0) == float.MaxValue, "no crystal: MaxValue");

            // the user's rule: 60 m
            Check(!LooseFetchRules.Veto(true, true, true, true, 2, near, 60f), "a stone 59 m from the crystal is fetched");
            Check(LooseFetchRules.Veto(true, true, true, true, 2, far, 60f), "a stone 100 m from every own crystal is skipped");
            Check(!LooseFetchRules.Veto(true, true, true, true, 1, 60f * 60f, 60f), "exactly 60 m is still allowed");
            Check(LooseFetchRules.Veto(true, true, true, true, 1, 60.01f * 60.01f, 60f), "just past 60 m is skipped");

            // never skipped
            Check(!LooseFetchRules.Veto(false, true, true, true, 1, far, 60f), "switch off");
            Check(!LooseFetchRules.Veto(true, false, true, true, 1, far, 60f), "not the host");
            Check(!LooseFetchRules.Veto(true, true, false, true, 1, far, 60f), "not loose (stockpile, crate, construction site, carried)");
            Check(!LooseFetchRules.Veto(true, true, true, false, 1, far, 60f), "not raw (finished goods, tools, weapons, wards, barrels)");
            Check(!LooseFetchRules.Veto(true, true, true, true, 0, far, 60f), "faction owns no crystal: the game's behaviour");

            // radius clamp
            Check(LooseFetchRules.ClampRadius(0f) == LooseFetchRules.MinRadius && LooseFetchRules.ClampRadius(-3f) == LooseFetchRules.MinRadius && LooseFetchRules.ClampRadius(float.NaN) == LooseFetchRules.MinRadius, "radius below 5 m / NaN -> 5 m");
            Check(LooseFetchRules.ClampRadius(99999f) == LooseFetchRules.MaxRadius && LooseFetchRules.ClampRadius(60f) == 60f, "radius above 2000 m -> 2000; 60 kept");
            Check(LooseFetchRules.DefaultRadius == 60f, "default radius = the user's 60 m");

            // exhaustive: veto only when all guards hold and it is far
            int vetoes = 0;
            foreach (bool en in new bool[] { false, true }) foreach (bool srv in new bool[] { false, true }) foreach (bool lo in new bool[] { false, true }) foreach (bool raw in new bool[] { false, true })
                foreach (int n in new int[] { 0, 1 }) foreach (float d in new float[] { near, far })
                    if (LooseFetchRules.Veto(en, srv, lo, raw, n, d, 60f)) vetoes++;
            Check(vetoes == 1, "exactly one combination skips: on, host, loose, raw, own crystal, far");

            // what is limited (review 2026-11-07: the search never sees RawResources; loose stone comes as Stone_DoubleBrick blocks)
            Check(LooseFetchRules.Limited("Blocks", "Stone_DoubleBrick"), "loose stone block limited");
            Check(LooseFetchRules.Limited("Blocks", "Wood_DoubleLog_AA"), "felled log limited");
            Check(!LooseFetchRules.Limited("Blocks", "Wood_DoublePlank_AA"), "planks not limited");
            Check(!LooseFetchRules.Limited("Structures", "Palette") && !LooseFetchRules.Limited("Blocks", "") && !LooseFetchRules.Limited(null, "Stone_DoubleBrick"), "structures, empty and null not limited");
            Check(!LooseFetchRules.Limited("RawResources", "RawStone"), "raw resources are never candidates of this search: not claimed");

            Check(LooseFetchRules.LogDetail(1) && LooseFetchRules.LogDetail(30) && !LooseFetchRules.LogDetail(31) && !LooseFetchRules.LogDetail(0), "detail lines for the first 30 distinct items");
            Console.WriteLine("LooseFetchTests: " + checks + " checks PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("LooseFetchTests FAIL after " + checks + " checks: " + e.Message); return 1; }
    }
}
