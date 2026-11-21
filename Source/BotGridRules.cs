using System;
using System.Collections.Generic;
using System.Globalization;

namespace CastleStorySteam
{
    // 0.4.18 GRID SPAWN test helper, pure part (no Unity: unit-tested by BotTests.cs). The game part is BotGrid.cs.
    //   grid <key> <cols> <rows> [spacing N] [base|map] [rot deg] [instant]
    // Cells are centred on the anchor (base = centre of the own workers, map = centre of the land extent), row-major from the -x/-z corner.
    public static class BotGridRules
    {
        public const int MaxCells = 400;      // per command (cols * rows)
        public const int MaxSide = 40;        // cols, rows
        public const float MaxSpacing = 64f;
        public const int SendsPerFrame = 2;   // the game part never sends more placements in one frame
        public const int ReasonLines = 20;    // per-cell failure reasons written to the result

        public sealed class Options
        {
            public string Key; public int Cols, Rows; public float Spacing = -1f; public bool Map; public float Rot; public bool Instant;
        }

        // parses the words after 'grid' (the BotScript validator and the game part both use it); null error = ok
        public static Options Parse(IList<string> a, out string error)
        {
            error = null; Options o = new Options(); int n; float f;
            if (a == null || a.Count < 3) { error = "grid needs <key> <cols> <rows> [spacing N] [base|map] [rot deg] [instant]"; return null; }
            o.Key = a[0];
            if (!BotRules.ValidKey(o.Key)) { error = "bad asset key"; return null; }
            if (!BotRules.TryInt(a[1], out n) || n < 1 || n > MaxSide) { error = "cols must be 1 .. " + MaxSide; return null; } o.Cols = n;
            if (!BotRules.TryInt(a[2], out n) || n < 1 || n > MaxSide) { error = "rows must be 1 .. " + MaxSide; return null; } o.Rows = n;
            if (o.Cols * o.Rows > MaxCells) { error = "cols * rows must be <= " + MaxCells + " (asked " + (o.Cols * o.Rows) + ")"; return null; }
            bool scopeSet = false, rotSet = false, instSet = false;
            for (int i = 3; i < a.Count; i++)
            {
                string k = a[i].ToLowerInvariant();
                if (k == "spacing" && i + 1 < a.Count && o.Spacing < 0f) { if (!BotRules.TryFloat(a[i + 1], out f) || f < 1f || f > MaxSpacing) { error = "spacing must be 1 .. " + MaxSpacing; return null; } o.Spacing = f; i++; continue; }
                if ((k == "base" || k == "map") && !scopeSet) { o.Map = k == "map"; scopeSet = true; continue; }
                if (k == "rot" && i + 1 < a.Count && !rotSet) { if (!BotRules.TryFloat(a[i + 1], out f) || Math.Abs(f) > 360f) { error = "rot must be -360 .. 360 degrees"; return null; } o.Rot = f; rotSet = true; i++; continue; }
                if (k == "instant" && !instSet) { o.Instant = true; instSet = true; continue; }
                error = "grid: unexpected '" + a[i] + "' (options: spacing N, base|map, rot deg, instant; each once)"; return null;
            }
            return o;
        }

        // the spacing used: the asked one (default 4 for base) raised to the footprint; for map without spacing the grid spreads over the extent
        public static float Spacing(Options o, float footprint, float extX, float extZ, out bool raised)
        {
            raised = false; float s = o.Spacing;
            if (s < 0f)
            {
                if (o.Map && extX > 0f && extZ > 0f) s = (float)Math.Floor(Math.Min(extX / o.Cols, extZ / o.Rows));
                else s = 4f;
            }
            if (footprint > 0f && s < footprint) { s = (float)Math.Ceiling(footprint); raised = true; }
            if (s < 1f) s = 1f;
            return s;
        }

        // centred cell list: x = cx + (c - (cols-1)/2) * spacing, z likewise; [0] = x, [1] = z, [2] = col, [3] = row
        public static List<float[]> Cells(int cols, int rows, float spacing, float cx, float cz)
        {
            List<float[]> res = new List<float[]>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    res.Add(new float[] { cx + (c - (cols - 1) / 2f) * spacing, cz + (r - (rows - 1) / 2f) * spacing, c, r });
            return res;
        }

        // drops cells outside [min .. max] shrunk by margin; returns how many were dropped
        public static int Clip(List<float[]> cells, float minX, float maxX, float minZ, float maxZ, float margin)
        {
            int before = cells.Count;
            cells.RemoveAll(delegate(float[] p) { return p[0] < minX + margin || p[0] > maxX - margin || p[1] < minZ + margin || p[1] > maxZ - margin; });
            return before - cells.Count;
        }

        // the land box of a height map: every pixel with land(x, y) true; false = no land at all
        public static bool LandBox(int w, int h, Func<int, int, bool> land, out int minX, out int maxX, out int minY, out int maxY)
        {
            minX = int.MaxValue; maxX = -1; minY = int.MaxValue; maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!land(x, y)) continue;
                    if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            return maxX >= 0;
        }

        public static string Summary(int placed, int total, int occupied, int outside, int notBuildable, int other)
        {
            return "GRID placed " + placed + " of " + total + " (skipped: occupied " + occupied + ", out of map " + outside + ", not buildable " + notBuildable + (other > 0 ? ", other " + other : "") + ")";
        }

        public static string Cell(float[] p) { return "cell " + ((int)p[2]).ToString(CultureInfo.InvariantCulture) + "," + ((int)p[3]).ToString(CultureInfo.InvariantCulture); }
    }
}
