using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Factories;
using Brix.Lifecycle.Pooling;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.4.18 GRID SPAWN test helper (user 2026-11-07: "spawn warehouses / structures on a grid near the base or over the map, one word, for tests").
    //   bot command: grid <key> <cols> <rows> [spacing N] [base|map] [rot deg] [instant]   /   ungrid  (removes what the last grid placed)
    //   hotkeys (only while the bot is ARMED: [Test] Enabled + BepInEx\config\virtual-player.enable, no bot.stop, and no bot run active):
    //     Ctrl+Shift+G = grid from cfg [Test] GridKey / GridCols / GridRows / GridSpacing / GridScope (instant on a host, plain blueprints on a guest)
    //     Ctrl+Shift+H = ungrid. A key press writes a one-step script into the bot inbox, so it runs (and reports) like any bot run.
    // Each cell = the same picker maths as 'place' (BotBlueprint.ComputePlacement) + SendPlace, at most 2 sends per frame, max 400 cells.
    // Map extent = the land box of the loaded map's Heightmap.png (red > 0; x = column, z = row from the bottom), cached per map.
    // Coordinates of every placed cell go to perf-trace as 'BOT-GRID placed ...'.
    public sealed partial class SteamBridgePlugin
    {
        sealed class BotGridItem { public Factory.AssetKey Key; public Vector3 Pos; }
        static readonly List<BotGridItem> botGridLast = new List<BotGridItem>();
        static string botGridExtentMap; static bool botGridExtentOk; static float botGridMinX, botGridMaxX, botGridMinZ, botGridMaxZ; static string botGridExtentInfo;
        static ConfigEntry<string> gridKeyCfg, gridScopeCfg; static ConfigEntry<int> gridColsCfg, gridRowsCfg; static ConfigEntry<float> gridSpacingCfg;

        static bool BotGridExtent(out string info)
        {
            Brix.Assets.Asset_Map map = GameParam.Map;
            if (map == null || string.IsNullOrEmpty(map.path)) { info = "no loaded map"; return false; }
            if (botGridExtentMap == map.path) { info = botGridExtentInfo; return botGridExtentOk; }
            botGridExtentMap = map.path; botGridExtentOk = false;
            string file = System.IO.Path.Combine(map.path.Replace('/', '\\'), "Heightmap.png");
            try
            {
                if (!File.Exists(file)) { botGridExtentInfo = "no Heightmap.png in " + map.path; info = botGridExtentInfo; return false; }
                Texture2D tex = new Texture2D(2, 2);
                try
                {
                    if (!tex.LoadImage(File.ReadAllBytes(file))) { botGridExtentInfo = "Heightmap.png not readable"; info = botGridExtentInfo; return false; }
                    Color32[] px = tex.GetPixels32(); int w = tex.width, h = tex.height; int x0, x1, y0, y1;
                    if (!BotGridRules.LandBox(w, h, delegate(int x, int y) { return px[y * w + x].r > 0; }, out x0, out x1, out y0, out y1)) { botGridExtentInfo = "Heightmap.png has no land pixel"; info = botGridExtentInfo; return false; }
                    botGridMinX = x0; botGridMaxX = x1; botGridMinZ = y0; botGridMaxZ = y1; botGridExtentOk = true;
                    botGridExtentInfo = "map extent x " + x0 + ".." + x1 + " z " + y0 + ".." + y1 + " (land box of Heightmap.png " + w + "x" + h + ", " + map.codename + ")";
                }
                finally { UnityEngine.Object.Destroy(tex); }
            }
            catch (Exception e) { botGridExtentInfo = "Heightmap.png failed: " + e.GetType().Name + " " + e.Message; }
            PtLog("BOT-GRID " + botGridExtentInfo);
            info = botGridExtentInfo; return botGridExtentOk;
        }

        // footprint (cells, the larger side) of a key: the placement ghost's volume cells, else its meshes; 0 = unknown
        static float BotGridFootprint(Factory.AssetKey key, float x, float z, out string how)
        {
            how = "unknown"; Vector3 g; if (!BotBlueprint.GroundAt(x, z, out g)) g = new Vector3(x, 100f, z);
            GameObject ghost;
            if (!ObjectPoolSingleton.Request(out ghost, key, g, Quaternion.identity) || ghost == null) { how = "no pool for " + key; return 0f; }
            try
            {
                IVolume vol = ghost.GetComponent(typeof(IVolume)) as IVolume;
                if (vol != null)
                {
                    int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue, n = 0;
                    foreach (VProperty pr in new VProperty[] { VProperty.occupy, VProperty.fantome })
                    {
                        foreach (XYZ c in vol.GetCases(pr)) { n++; if (c.x < minX) minX = c.x; if (c.x > maxX) maxX = c.x; if (c.z < minZ) minZ = c.z; if (c.z > maxZ) maxZ = c.z; }
                        if (n > 0) { how = "volume " + pr + " cells"; return Math.Max(maxX - minX + 1, maxZ - minZ + 1); }
                    }
                }
                float best = 0f;
                foreach (MeshFilter mf in ghost.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    Vector3 sz = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                    best = Mathf.Max(best, Mathf.Max(Mathf.Abs(sz.x), Mathf.Abs(sz.z)));
                }
                if (best > 0f) { how = "mesh bounds (approx)"; return Mathf.Ceil(best); }
                return 0f;
            }
            catch (Exception e) { how = e.GetType().Name; return 0f; }
            finally { ObjectPoolSingleton.Release(ghost); }
        }

        BotOutcome BotGridAction(BotStep s, BotGameWorld w)
        {
            string err; BotGridRules.Options o = BotGridRules.Parse(s.Args, out err); if (o == null) return BotOutcome.Fail(err);
            string why = BotBlueprint.WhyCannotBuild(); if (why != null) return BotOutcome.Fail(why);
            if (o.Instant && !NetworkServer.active) return BotOutcome.Fail("instant: host / single player only");
            Factory.AssetKey key = BotBpKey(o.Key); int turns = (((int)Math.Round(o.Rot / 90f)) % 4 + 4) % 4;
            BotProjectWaiter pw = new BotProjectWaiter(); int phase = 0, next = 0, placed = 0, occupied = 0, outside = 0, notBuildable = 0, other = 0, reasons = 0;
            List<float[]> cells = null; int total = 0; float spacing = 0f; List<BotGridItem> made = new List<BotGridItem>();
            w.pending = delegate(float el)
            {
                {
                    if (phase == 0) { BotOutcome po = pw.Step(w); if (po != null) return po; phase = 1; }
                    if (phase == 1)
                    {
                        string ext; bool hasExt = BotGridExtent(out ext);
                        if (o.Map && !hasExt) { return BotOutcome.Fail("grid map: " + ext); }
                        Vector3 an = VpAnchor();
                        if (!o.Map && an == Vector3.zero) { return BotOutcome.Fail("no own workers for the base anchor"); }
                        float cx = o.Map ? (botGridMinX + botGridMaxX) / 2f : an.x, cz = o.Map ? (botGridMinZ + botGridMaxZ) / 2f : an.z;
                        string how; float fp = BotGridFootprint(key, cx, cz, out how); bool raised;
                        spacing = BotGridRules.Spacing(o, fp, hasExt ? botGridMaxX - botGridMinX : 0f, hasExt ? botGridMaxZ - botGridMinZ : 0f, out raised);
                        cells = BotGridRules.Cells(o.Cols, o.Rows, spacing, cx, cz); total = cells.Count;
                        if (hasExt) outside = BotGridRules.Clip(cells, botGridMinX, botGridMaxX, botGridMinZ, botGridMaxZ, 1f);
                        string head = "grid " + key + " " + o.Cols + "x" + o.Rows + " " + (o.Map ? "map" : "base") + " centre " + cx.ToString("F0", BotIc) + "," + cz.ToString("F0", BotIc)
                            + " spacing " + spacing.ToString("F0", BotIc) + (raised ? " (RAISED to the footprint)" : "") + " footprint " + fp.ToString("F0", BotIc) + " (" + how + ") " + ext + (o.Instant ? " INSTANT (HOST TEST HELPER)" : "");
                        w.Write("INFO  " + BotRules.Cut(head, 600)); PtLog("BOT-GRID " + head);
                        phase = 2; return BotOutcome.Wait("grid: " + cells.Count + " cell(s) to send");
                    }
                    if (phase == 2)
                    {
                        for (int k = 0; k < BotGridRules.SendsPerFrame && next < cells.Count; k++, next++)
                        {
                            float[] c = cells[next]; Factory.AssetKey sent; Vector3 pos; Quaternion rot; ValidationResult res = ValidationResult.Invalid; XYZ vox; string info = null; bool ok = false;
                            float[] radii = spacing >= 4f ? new float[] { 0f, 1f } : new float[] { 0f };
                            foreach (float rad in radii)
                            {
                                for (int d = 0; d < (rad == 0f ? 1 : 8) && !ok; d++)
                                {
                                    float ang = d * Mathf.PI / 4f;
                                    ok = BotBlueprint.ComputePlacement(key, c[0] + rad * Mathf.Cos(ang), c[1] + rad * Mathf.Sin(ang), turns, out sent, out pos, out rot, out res, out vox, out info);
                                    if (ok) { BotBlueprint.SendPlace(sent, pos, rot, w.project, o.Instant); placed++; BotGridItem it = new BotGridItem(); it.Key = sent; it.Pos = pos; made.Add(it); PtLog("BOT-GRID placed " + BotGridRules.Cell(c) + " " + sent + " at " + CsV(pos)); }
                                }
                                if (ok) break;
                            }
                            if (ok) continue;
                            if (info != null && info.StartsWith("ray hit nothing", StringComparison.Ordinal)) outside++;
                            else if (res == ValidationResult.Obstructed) occupied++;
                            else if (res == ValidationResult.Invalid) notBuildable++;
                            else other++;
                            if (reasons < BotGridRules.ReasonLines) { reasons++; w.Write("INFO  grid " + BotGridRules.Cell(c) + " at " + c[0].ToString("F0", BotIc) + "," + c[1].ToString("F0", BotIc) + " not placed: " + res + " | " + BotRules.Cut(info, 300)); }
                        }
                        if (next < cells.Count) return BotOutcome.Wait("grid: sent " + next + " of " + cells.Count);
                        if (!o.Instant && placed > 0) BotBlueprint.SendCommit(w.project);
                        lock (botGridLast) { botGridLast.Clear(); botGridLast.AddRange(made); }
                        phase = 3;
                        string sum = BotGridRules.Summary(placed, total, occupied, outside, notBuildable, other);
                        PtLog("BOT-GRID " + sum);
                        return placed > 0 ? BotOutcome.Ok(sum + (o.Instant ? " INSTANT (HOST TEST HELPER)" : " (blueprints committed; builders build them)")) : BotOutcome.Fail(sum);
                    }
                    return BotOutcome.Ok("grid already finished");
                }
            };
            return w.pending(0f);
        }

        // ungrid: what the last grid placed: a not yet built blueprint is cancelled, a built thing gets the remover (instant on a host)
        BotOutcome BotUngridAction(BotStep s, BotGameWorld w)
        {
            List<BotGridItem> todo; lock (botGridLast) todo = new List<BotGridItem>(botGridLast);
            if (todo.Count == 0) return BotOutcome.Fail("no grid placed in this game session");
            string why = BotBlueprint.WhyCannotBuild(); if (why != null) return BotOutcome.Fail(why);
            bool instant = NetworkServer.active; BotProjectWaiter pw = new BotProjectWaiter(); int phase = 0, next = 0, cancelled = 0, removed = 0, missing = 0;
            w.pending = delegate(float el)
            {
                if (phase == 0) { BotOutcome po = pw.Step(w); if (po != null) return po; phase = 1; }
                for (int k = 0; k < BotGridRules.SendsPerFrame && next < todo.Count; k++, next++)
                {
                    BotGridItem it = todo[next];
                    Blueprint b = BotBlueprint.BlueprintNear(it.Pos, it.Key, false, 1.5f);
                    if (b != null) { BotBlueprint.SendCancel(b); cancelled++; continue; }
                    GameObject target = null; float best = 2.25f;
                    foreach (FactoryImprint fi in UnityEngine.Object.FindObjectsOfType<FactoryImprint>())
                    {
                        if (fi == null || fi.IsNullOrReleased()) continue;
                        float d = (fi.transform.position - it.Pos).sqrMagnitude; if (d > best) continue;
                        Factory.AssetKey bk = null; try { bk = BlueprintAssetKeyResolver.Resolve(fi.AssetKey); } catch (Exception) { }
                        if (bk == null || bk.Name != it.Key.Name) continue;
                        best = d; target = fi.gameObject;
                    }
                    if (target == null) { missing++; continue; }
                    string e = BotBlueprint.SendDemolish(target, w.project, instant); if (e == null) removed++; else missing++;
                }
                if (next < todo.Count) return BotOutcome.Wait("ungrid: " + next + " of " + todo.Count);
                if (!instant && removed > 0) BotBlueprint.SendCommit(w.project);
                lock (botGridLast) botGridLast.Clear();
                string sum = "UNGRID " + todo.Count + " cell(s): blueprints cancelled " + cancelled + ", remover sent " + removed + (instant ? " (instant, HOST TEST HELPER)" : " (builders break them)") + ", not found " + missing;
                PtLog("BOT-GRID " + sum);
                return BotOutcome.Ok(sum);
            };
            return w.pending(0f);
        }

        // hotkeys (called every frame from the bot tick while armed and idle)
        void BotGridHotkeys(float now)
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl), shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift) return;
            bool g = Input.GetKeyDown(KeyCode.G), h = Input.GetKeyDown(KeyCode.H);
            if (!g && !h) return;
            if (BotRunActive) { PtLog("BOT-GRID hotkey ignored: a bot run (or the last grid) is still running"); return; }
            if (gridKeyCfg == null)
            {
                gridKeyCfg = Config.Bind("Test", "GridKey", "Pallet", "0.4.18 test helper: blueprint key the Ctrl+Shift+G grid places (Pallet = crate / warehouse; any key of Meta_Structure.lua). Needs the armed bot ([Test] Enabled + virtual-player.enable).");
                gridColsCfg = Config.Bind("Test", "GridCols", 5, new ConfigDescription("0.4.18 Ctrl+Shift+G grid: columns (cols * rows <= 400).", new AcceptableValueRange<int>(1, BotGridRules.MaxSide)));
                gridRowsCfg = Config.Bind("Test", "GridRows", 5, new ConfigDescription("0.4.18 Ctrl+Shift+G grid: rows.", new AcceptableValueRange<int>(1, BotGridRules.MaxSide)));
                gridSpacingCfg = Config.Bind("Test", "GridSpacing", 4f, new ConfigDescription("0.4.18 Ctrl+Shift+G grid: cells between centres (raised to the structure's footprint).", new AcceptableValueRange<float>(1f, BotGridRules.MaxSpacing)));
                gridScopeCfg = Config.Bind("Test", "GridScope", "base", "0.4.18 Ctrl+Shift+G grid: base (around the own workers) or map (centre of the map's land box).");
            }
            string line = h ? "ungrid" : "grid " + gridKeyCfg.Value + " " + gridColsCfg.Value + " " + gridRowsCfg.Value + " spacing " + gridSpacingCfg.Value.ToString("F0", BotIc) + " "
                + (string.Equals(gridScopeCfg.Value, "map", StringComparison.OrdinalIgnoreCase) ? "map" : "base") + (NetworkServer.active ? " instant" : "");
            try
            {
                File.WriteAllLines(BotConfigFile(BotInboxName), new string[] { "set name hotkey-" + (h ? "ungrid" : "grid"), "set run hotkey", line }, new UTF8Encoding(false));
                botNextFileCheck = 0f; PtLog("BOT-GRID hotkey " + (h ? "Ctrl+Shift+H" : "Ctrl+Shift+G") + ": " + line);
            }
            catch (Exception e) { PtLog("BOT-GRID hotkey failed: " + e.Message); }
        }
    }
}
