namespace CastleStorySteam
{
    // 0.4.20 LOOSE-FETCH, pure part (no Unity; tested by LooseFetchTests.cs). The game part is LooseFetch.cs.
    // User 2026-11-07 (bug-intake chat): "60 метрів від кристала". A worker that looks for material (build site, crafting, catapult ammo, landscape fill) does not take a LOOSE stone block or log
    // item that lies farther than the radius (horizontal) from every crystal its own faction owns. Stockpiles, crates and construction-site contents are never limited.
    internal static class LooseFetchRules
    {
        internal const float DefaultRadius = 60f;
        internal const float MinRadius = 5f, MaxRadius = 2000f;
        internal const int DetailLines = 30;          // the first 30 distinct skipped items get a line each
        internal const float SummarySeconds = 60f;    // then one summary line per minute with skips

        // true = the item is skipped (the game behaves as if it were not there for this search)
        internal static bool Veto(bool enabled, bool server, bool loose, bool raw, int ownCrystals, float nearestSqrXZ, float radius)
        {
            if (!enabled || !server || !loose || !raw) return false;
            if (ownCrystals <= 0) return false;                 // no crystal of our own (lost / map without one): no limit, the game's behaviour
            float r = ClampRadius(radius);
            return nearestSqrXZ > r * r;
        }

        internal static float ClampRadius(float r)
        {
            if (float.IsNaN(r) || r < MinRadius) return MinRadius;
            return r > MaxRadius ? MaxRadius : r;
        }

        // squared horizontal distance from (x, z) to the nearest of n points; float.MaxValue when n == 0
        internal static float NearestSqrXZ(float x, float z, float[] px, float[] pz, int n)
        {
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float dx = x - px[i], dz = z - pz[i];
                float d = dx * dx + dz * dz;
                if (d < best) best = d;
            }
            return best;
        }

        // which loose things this limit covers. The material search only ever sees the game's InterestingResourceObject keys (Knowledge.cs:50-59: Wood_DoublePlank_AA,
        // Stone_DoubleBrick, Wood_DoubleLog_AA, Palette, Toolrack, Furnace, ChoppingBlock); loose RawResources are never candidates there. The user's "stone and felled trees" =
        // loose stone blocks (Stone_DoubleBrick) and felled logs (Blocks with "Log" in the name, D-063 RAW). Planks and the rest are not limited.
        internal static bool Limited(string factory, string name)
        {
            if (factory != "Blocks" || string.IsNullOrEmpty(name)) return false;
            return name == "Stone_DoubleBrick" || name.IndexOf("Log", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal const float VerdictSeconds = 2f;     // a far / near verdict per item is kept 2 s (searches repeat every decision frame for idle workers)
        internal const int DetailIdsCap = 1000;

        internal static bool LogDetail(int distinctSkipped) { return distinctSkipped > 0 && distinctSkipped <= DetailLines; }
    }
}
