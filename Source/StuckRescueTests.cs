using System;
using CastleStorySteam;

// 0.4.28: checks of StuckRescueRules (pure rules of StuckUnitRescue.cs / BuriedRescue.cs). The column cases replay the 2026-11-13 video: a unit jumps off a 6-cell cliff
// onto a column that holds a placed block on the ground, and ends inside the ground.
static class StuckRescueTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static bool[] Cells(string s) { bool[] a = new bool[s.Length]; for (int i = 0; i < s.Length; i++) a[i] = s[i] == '#'; return a; }   // '#' = terrain or block, '.' = free

    static void Main()
    {
        // probes
        Check(StuckRescueRules.InsideSolid(true, true), "both probes in full cells = inside");
        Check(!StuckRescueRules.InsideSolid(true, false), "only the low probe (slope, half sunk) = not inside");
        Check(!StuckRescueRules.InsideSolid(false, true), "only the high probe = not inside");
        Check(StuckRescueRules.ProbeLow < StuckRescueRules.FeetBelowCellCentre && StuckRescueRules.ProbeHigh < StuckRescueRules.FeetBelowCellCentre + 0.5f, "a unit sunk one cell has both probes in that cell");

        // states
        Check(StuckRescueRules.MayRescue(true, false, false, false), "standing unit may be rescued");
        Check(!StuckRescueRules.MayRescue(true, true, false, false), "flailing unit is left alone");
        Check(!StuckRescueRules.MayRescue(true, false, true, false), "climbing unit is left alone");
        Check(!StuckRescueRules.MayRescue(true, false, false, true), "jumping unit is left alone");
        Check(!StuckRescueRules.MayRescue(false, false, false, false), "airborne / grabbed / dead unit is left alone");

        // stuck
        Check(StuckRescueRules.Stuck(true, true), "inside solid = stuck even next to a node");
        Check(StuckRescueRules.Stuck(false, false), "no walkable node near = stuck");
        Check(!StuckRescueRules.Stuck(false, true), "standing on a walkable node = an ordinary path failure, untouched");

        // the game's 'no landing node' marker
        Check(StuckRescueRules.IsVanillaNoFloor(26.52f, 7f), "origin 26.52 -> round 27 - 20 = 7");
        Check(StuckRescueRules.IsVanillaNoFloor(26.4f, 6f), "origin 26.4 -> round 26 - 20 = 6");
        Check(!StuckRescueRules.IsVanillaNoFloor(26.52f, 20.525f), "a found landing 6 cells below is not the marker");
        Check(!StuckRescueRules.IsVanillaNoFloor(26.52f, 6.525f), "a found landing 20 cells below is not the marker (adjusted height)");

        // jump column, going DOWN from the cell in front of the unit: 5 free cells, a block, ground
        Check(StuckRescueRules.FirstStandableDown(Cells(".....##########")) == 4, "lands in the free cell on top of the block");
        Check(StuckRescueRules.FirstStandableDown(Cells("....................")) == -1, "nothing below within the array = a real drop");
        Check(StuckRescueRules.FirstStandableDown(Cells("#....#")) == 4, "start cell occupied: the first free cell with a free cell above and ground below");
        Check(StuckRescueRules.FirstStandableDown(Cells(".#.#")) == 0, "start cell free with solid right below");
        Check(StuckRescueRules.FirstStandableDown(Cells("##.#")) == -1, "a one-cell gap under a solid cell has no headroom");
        Check(StuckRescueRules.FirstStandableDown(null) == -1 && StuckRescueRules.FirstStandableDown(new bool[0]) == -1, "empty input");

        // rescue column, going UP from the cell the unit is stuck in
        Check(StuckRescueRules.FirstStandableUp(Cells("###...")) == 3, "three cells deep: the first free cell above the ground");
        Check(StuckRescueRules.FirstStandableUp(Cells("##.#..")) == 4, "a one-cell pocket is skipped, the cell above the upper solid is taken");
        Check(StuckRescueRules.FirstStandableUp(Cells("#.")) == 1, "one cell deep, free above (beyond the array counts as free)");
        Check(StuckRescueRules.FirstStandableUp(Cells("######")) == -1, "no free cell within reach");
        Check(StuckRescueRules.FirstStandableUp(Cells("...")) == -1, "start cell not solid: nothing to stand on");
        Check(StuckRescueRules.FirstStandableUp(null) == -1, "null input");

        // rate limit
        Check(StuckRescueRules.Allow(100.0, 0.0, 5.0), "first action allowed");
        Check(!StuckRescueRules.Allow(103.0, 100.0, 5.0), "second action inside the gap refused");
        Check(StuckRescueRules.Allow(105.0, 100.0, 5.0), "action after the gap allowed");

        Console.WriteLine("PASS StuckRescueTests: " + checks + " checks");
    }
}
