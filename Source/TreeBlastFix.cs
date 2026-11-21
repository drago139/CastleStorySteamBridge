using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.Components;
using Brix.Game.Rules;
using Brix.Legacy;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.3.95: the game's explosions only damage voxels, units and rigid bodies; a tree is a TreeRegistry entry and falls only when the ground under it loses its
// crust (Architecte.CheckArbre), so a bomb that dents the ground near a tree leaves the tree standing. After the explosion's own terrain damage this fells every
// registered tree inside the same radius the explosion uses for the ground (terrainDamageCurve.maxRange), through the game's own DamagedTree.Blast
// (explosion = true: no stump, logs fall, every peer plays the cut), and only where the map's rules allow explosions and tree cutting. Host only.
internal static class TreeBlastFix
{
    static Func<bool> enabled;
    static Action<string> note;

    static Func<float> extraRadius;
    static Func<bool> deferOn;
    // 0.3.108: a tree this fix creates is spawned and cut in the same frame, so a guest receives the spawn and the cut in one packet and handles the cut before the tree has
    // started (the cut is then dropped and the tree stays up as a ghost). The cut is queued here and sent TreeCutRules.HostDelaySeconds after the spawn, once the host's own
    // tree is ready; a tree that is still not ready after HostTimeoutSeconds is blasted anyway (the vanilla behaviour). TreeCutGuard.Tick drives the queue.
    sealed class Queued { internal DamagedTree Tree; internal XYZ P; internal float T0; internal float T0Game; internal int Id; internal bool Retried; }
    static readonly List<Queued> queue = new List<Queued>();
    static readonly HashSet<int> queuedIds = new HashSet<int>();

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> logger, Func<float> minRadius, Func<bool> deferEarly)
    {
        enabled = isEnabled; note = logger; extraRadius = minRadius; deferOn = deferEarly;
        harmony.Patch(AccessTools.Method(typeof(ExplosionGameplay), "ApplyTerrainDammage"), null, new HarmonyMethod(AccessTools.Method(typeof(TreeBlastFix), "AfterTerrainDamage")), null, null, null);
    }

    internal static void Clear() { enabled = null; note = null; deferOn = null; ClearQueue(); }
    internal static void ClearQueue() { queue.Clear(); queuedIds.Clear(); }

    internal static void Tick()
    {
        if (queue.Count == 0) return;
        try
        {
            if (!NetworkServer.active) { ClearQueue(); return; }
            float now = Time.realtimeSinceStartup, gameNow = Time.time;
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                Queued q = queue[i];
                bool alive = q.Tree != null && q.Tree.gameObject.activeInHierarchy;
                bool ready = alive && q.Tree.Ready;
                double age = now - q.T0;
                HostAction step = TreeCutRules.HostStep(alive, ready, age, gameNow - q.T0Game);
                if (step == HostAction.Wait) continue;
                queue.RemoveAt(i); queuedIds.Remove(q.Id);
                string ms = (age * 1000.0).ToString("F0");
                if (step == HostAction.Drop)
                {
                    TreeCutGuard.HostDropped++;
                    TreeCutGuard.Log("TREECUT role=host pos=" + TreeCutGuard.PosText(q.P) + " deferred=" + ms + "ms outcome=host-dropped (tree object gone before the cut was sent)");
                    RequeueIfStillStanding(q);
                    continue;
                }
                bool failed = false;
                try
                {
                    q.Tree.Blast();
                    TreeRegistry registry = TreeRegistry.Instance;
                    if (registry != null && registry.HasTree(q.P)) registry.Remove(q.P);
                }
                catch (Exception e) { failed = true; TreeCutGuard.ErrorOnce("TreeBlastFix.Tick", e); }
                if (failed) { TreeCutGuard.HostFailed++; TreeCutGuard.LogKey("TREECUT role=host pos=" + TreeCutGuard.PosText(q.P) + " deferred=" + ms + "ms outcome=blast-failed"); continue; }
                if (step == HostAction.FireTimeout) TreeCutGuard.HostTimeouts++; else TreeCutGuard.HostFired++;
                string text = "TREECUT role=host pos=" + TreeCutGuard.PosText(q.P) + " deferred=" + ms + "ms outcome=" + (step == HostAction.FireTimeout ? "timeout (tree not ready, blasted anyway)" : "host-deferred");
                if (step == HostAction.FireTimeout) TreeCutGuard.LogKey(text); else TreeCutGuard.Log(text);
            }
        }
        catch (Exception e) { TreeCutGuard.ErrorOnce("TreeBlastFix.Tick", e); }
    }

    // The queued tree object was released before its cut went out (the vanilla code would still have felled the tree): if the registry still has the tree and nothing else stands for it, queue it once more.
    static void RequeueIfStillStanding(Queued q)
    {
        try
        {
            if (q.Retried) return;
            TreeRegistry registry = TreeRegistry.Instance;
            if (registry == null || !registry.HasTree(q.P)) return;
            DamagedTree other = DamagedTree.GetAt(q.P);
            if (other != null && other.gameObject.activeInHierarchy) return;   // another tree object already represents it
            DamagedTree fresh = DamagedTree.GetAtOrCreate(q.P);
            Queued n = new Queued(); n.Tree = fresh; n.P = q.P; n.T0 = Time.realtimeSinceStartup; n.T0Game = Time.time; n.Id = fresh.GetInstanceID(); n.Retried = true;
            queue.Add(n); queuedIds.Add(n.Id); TreeCutGuard.HostRequeued++;
        }
        catch (Exception e) { TreeCutGuard.ErrorOnce("TreeBlastFix.Requeue", e); }
    }

    static void AfterTerrainDamage(ExplosionGameplay __instance, Vector3 explosionCenter)
    {
        try
        {
            if (enabled == null || !enabled() || !NetworkServer.active || __instance == null || __instance.terrainDamageCurve == null) return;
            TreeRegistry registry = TreeRegistry.Instance;
            if (registry == null) return;
            float radius = Mathf.Max(__instance.terrainDamageCurve.maxRange, extraRadius == null ? 0f : extraRadius());   // the ground radius is only 3 cells: in the logs 114 blasts had a tree 3-6 cells away and felled nothing
            if (radius <= 0f) return;
            float sqr = radius * radius;
            List<XYZ> hit = new List<XYZ>(); float nearest = float.MaxValue; int within8 = 0, total = 0;
            foreach (XYZ p in registry.ListAllTreePosition())
            {
                Vector3 v = p.ToVector3(); total++;
                // a tree is registered at its trunk base but is ~10 cells tall, and a barrel often bursts in the crown: measure sideways only and accept a bomb anywhere from below the base (a pit under a ledge of trees) up to the top of the tree
                float dx = v.x - explosionCenter.x, dz = v.z - explosionCenter.z, dy = explosionCenter.y - v.y;
                float d2 = dx * dx + dz * dz; if (d2 < nearest) nearest = d2; if (d2 <= 64f) within8++;
                if (d2 > sqr || dy > 11f) continue;
                if (!TerrainRules.Main.CanExplodeAt(v) || !TerrainRules.Main.CanCutTreesAt(v)) continue;
                hit.Add(p);
            }
            int felled = 0, deferred = 0;
            bool defer = deferOn != null && deferOn() && TreeCutGuard.Active;
            foreach (XYZ p in hit)
            {
                if (!registry.HasTree(p)) continue;
                if (defer)
                {
                    DamagedTree existing = DamagedTree.GetAt(p);
                    if (existing != null && queuedIds.Contains(existing.GetInstanceID())) { TreeCutGuard.HostDuplicates++; continue; }   // a cut for this tree is already waiting
                    if (existing != null && existing.Ready)   // a running tree: the guests have started it long ago, nothing to wait for
                    {
                        existing.Blast(); registry.Remove(p); felled++; TreeCutGuard.HostImmediate++;
                        continue;
                    }
                    DamagedTree fresh = existing != null ? existing : DamagedTree.GetAtOrCreate(p);
                    Queued q = new Queued(); q.Tree = fresh; q.P = p; q.T0 = Time.realtimeSinceStartup; q.T0Game = Time.time; q.Id = fresh.GetInstanceID();
                    queue.Add(q); queuedIds.Add(q.Id); TreeCutGuard.HostQueued++; deferred++;
                    continue;
                }
                DamagedTree.GetAtOrCreate(p).Blast();
                registry.Remove(p);
                felled++;
            }
            if (note != null) note("tree blast at " + explosionCenter + " radius " + radius.ToString("F1") + ": " + hit.Count + " tree(s) in range, " + felled + " felled" + (deferred > 0 ? ", " + deferred + " cut(s) deferred " + (int)(TreeCutRules.HostDelaySeconds * 1000) + " ms until the tree is ready (see TREECUT)" : "") + (hit.Count == 0 ? "; nearest tree " + (total == 0 ? "none registered" : Mathf.Sqrt(nearest).ToString("F1") + " away") + ", " + within8 + " within 8, " + total + " registered in the world" : ""));
        }
        catch (Exception e) { if (note != null) note("tree blast failed: " + e.GetType().Name + " " + e.Message); }
    }
}
