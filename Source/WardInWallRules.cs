namespace CastleStorySteam
{
    // 0.4.19 WARD-IN-WALL, pure part (no Unity: unit-tested by WardInWallTests.cs). The game part is WardInWall.cs.
    // One check of one ward: what to do with it. The glue feeds the facts; this decides.
    internal enum WardWallStep { Forget, Watch, Wait, Remove }

    internal static class WardInWallRules
    {
        internal const float CheckUp = 0.3f;          // the body point checked: ward position + 0.3 m (the game rests a ward 0.5 m above the surface, WardController.Deposer)
        internal const float RecheckSeconds = 1.5f;   // the second in-wall check must come at least this long after the first one
        internal const float QuickSeconds = 0.5f;     // how often wards just put down (WardController.Deposer) are checked; there is no sweep (built wards must never be touched)
        internal const int MaxWatched = 256;          // more watched wards than this = something is wrong: start over
        internal const int LogFirst = 40, LogEvery = 10;

        // enabled/server: the switch and NetworkServer.active. onGround: GenericParentTracker.IsOnGround() (not carried, not stored).
        // solid: the checked cell is FULL and counts (terrain always; a placed block only when blocks count).
        // hitsBefore: in-wall checks of this ward so far (0 = not watched). sameCell: still in the cell of the first in-wall check. sinceFirst: seconds since that check.
        internal static WardWallStep Judge(bool enabled, bool server, bool onGround, bool solid, int hitsBefore, bool sameCell, float sinceFirst)
        {
            if (!enabled || !server || !onGround || !solid) return WardWallStep.Forget;
            if (hitsBefore <= 0 || !sameCell) return WardWallStep.Watch;       // first sighting, or it moved to another solid cell: the clock starts again
            if (sinceFirst < RecheckSeconds) return WardWallStep.Wait;
            return WardWallStep.Remove;                                       // two checks in a row, >= 1.5 s apart, same cell
        }

        // the cell counts as solid: full, and terrain (always) or a placed block (only when blocks count)
        internal static bool Solid(bool full, bool terrain, bool block, bool countBlocks)
        {
            if (!full) return false;
            if (terrain) return true;
            return countBlocks && block;
        }


        internal static string Kind(bool terrain, bool block) { return terrain ? "terrain" : (block ? "block" : "other"); }

        internal static bool LogLine(int n) { return n > 0 && (n <= LogFirst || n % LogEvery == 0); }
    }
}
