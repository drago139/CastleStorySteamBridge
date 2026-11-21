namespace CastleStorySteam
{
    // 0.4.28 pure decisions of the stuck-unit fixes (StuckUnitRescue.cs, BuriedRescue.cs). No Unity or game types, so StuckRescueTests.cs can check them without the game.
    internal static class StuckRescueRules
    {
        // Heights of the two body probes above the feet. A unit that stands normally has its feet about 0.5 above the centre of the ground cell, so both probes are in the free
        // cell above it. A unit that sank one whole cell has both probes inside the ground cell.
        internal const float ProbeLow = 0.2f, ProbeHigh = 0.7f;

        // Height of the feet above the centre of the cell a unit stands in, when it stands on a full cell (the game's FloorPicker.GetAdjustedHeight with FloorOffset 0.05).
        internal const float FeetBelowCellCentre = 0.475f;

        // A unit is inside solid only when BOTH probes are in full solid cells (full terrain or a full placed block). Slopes, stairs and doors are not full.
        internal static bool InsideSolid(bool lowFull, bool highFull) { return lowFull && highFull; }

        // The unit may be touched only in the plain standing / walking states.
        internal static bool MayRescue(bool grounded, bool flailing, bool climbing, bool jumpingOff) { return grounded && !flailing && !climbing && !jumpingOff; }

        // Rescue is due when the path failed and the unit is inside solid, or it stands where the game has no walkable node within one cell up or down.
        internal static bool Stuck(bool insideSolid, bool walkableNodeNear) { return insideSolid || !walkableNodeNear; }

        // The game's JumpingOff.StartJumping sets destination.y = round(origin.y) - 20 when it finds no landing node under the cell in front of the unit: a whole number,
        // about 20 below the origin. A landing it did find has the adjusted floor height (cell centre - 0.475 on a full cell), which is not a whole number.
        internal static bool IsVanillaNoFloor(float originY, float destinationY)
        {
            float frac = destinationY - (float)System.Math.Round(destinationY); if (frac < 0f) frac = -frac;
            float drop = originY - destinationY;
            return frac < 0.001f && drop > 19.4f && drop < 20.6f;
        }

        // nonEmpty[k] = the cell k below the start cell holds terrain or a block (k = 0 is the start cell). Returns the first k whose cell is free, has a free cell above it
        // (the cell above the start cell counts as free) and a non-empty cell below it. -1 = no such cell in the array (a real drop into nothing).
        internal static int FirstStandableDown(bool[] nonEmpty)
        {
            if (nonEmpty == null) return -1;
            for (int k = 0; k + 1 < nonEmpty.Length; k++)
            {
                if (nonEmpty[k]) continue;
                if (k > 0 && nonEmpty[k - 1]) continue;
                if (nonEmpty[k + 1]) return k;
            }
            return -1;
        }

        // nonEmpty[k] = the cell k above the start cell holds terrain or a block (k = 0 is the start cell, inside solid). Returns the first k >= 1 whose cell is free, with a
        // free cell above it (beyond the array counts as free) and a non-empty cell below it. -1 = none.
        internal static int FirstStandableUp(bool[] nonEmpty)
        {
            if (nonEmpty == null) return -1;
            for (int k = 1; k < nonEmpty.Length; k++)
            {
                if (nonEmpty[k] || !nonEmpty[k - 1]) continue;
                if (k + 1 < nonEmpty.Length && nonEmpty[k + 1]) continue;
                return k;
            }
            return -1;
        }

        // One action per unit per gap seconds.
        internal static bool Allow(double now, double last, double gap) { return last <= 0.0 || now - last >= gap; }
    }
}
