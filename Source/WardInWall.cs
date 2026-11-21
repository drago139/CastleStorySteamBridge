using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.Components;
using Brix.Game.Network;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.4.19 WARD-IN-WALL (HOST only; cfg [Fixes] WardInWallRemove (true), WardInWallBlocks (true); perf-trace tag WARD-IN-WALL). User 2026-11-07 via main/map: a Bricktron that walks up to a
// wall and drops a ward leaves the ward inside the wall; "if the ward is gone for the game it is gone: we delete it" (no penalty).
// Why the game allows it (recovered firstpass + installed DLL via ilspycmd, same code): VolumeRegistry.ClosestAvailableDropPosition (VolumeRegistry.cs:413-433) tests only VolumeRegistry
// volumes with occupy|operation plus the item's validator (ConcreteValidator: support below only, IsObstructed = false); terrain voxels are no volumes, so a cliff cell 1 m ahead is accepted.
// WardController.Deposer (WardController.cs:149-170) then looks only 1-2 cells DOWN for an open walkable cell and Architecte.SurfaceRaycast (maxDist 1) up/down 1 cell; when both fail the
// ward is set on top of the full cell it is in (+0.5), i.e. its body sits inside the next solid cell. Placed blocks DO register occupy volumes (PlacedBlockFactoryAssetCreator.cs:25-28).
// What this does: ONLY wards that just went through WardController.Deposer are checked (a postfix; in the installed DLL Deposer is called only from Recepteur.ScatterObjects = an item put
// down on the ground; blueprint-built wards, wall lanterns and wards loaded from a save never pass it, so they are never touched). Every 0.5 s such a ward is checked: on the ground
// (GenericParentTracker.IsOnGround) and its body point (position + 0.3 m) in a FULL voxel (terrain; a placed block too while WardInWallBlocks is on). Not solid on a check = forgotten
// for good (until its next put-down). Solid on two checks >= 1.5 s apart in the same cell = removed by the game's SafeNetworkDestroy (UnSpawn + pool release; WardController.ResetComponent
// fires WardRemoved), so guests lose it too; they need nothing. Why a good drop never has a full body point: when SurfaceRaycast finds a walkable cell X the body point is X (not full) or
// X+1 (empty), see D-076. One log line per removal (first 40, then every 10th). Not verified in a live game (T-189). Wards already inside walls in a save are NOT cleaned.
internal static class WardInWall
{
    sealed class Watched { public WardController Ward; public XYZ Cell; public float First; public int Hits; }

    static Func<bool> enabled, countBlocks; static Action<string> note;
    static readonly Dictionary<int, Watched> watched = new Dictionary<int, Watched>();
    static readonly Dictionary<int, WardController> fresh = new Dictionary<int, WardController>();
    static float nextQuick; static int removedTotal, seenDrops; static bool announced;
    static GameObject poller;

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Func<bool> blocks, Action<string> log)
    {
        enabled = isEnabled; countBlocks = blocks; note = log;
        System.Reflection.MethodInfo target = AccessTools.Method(typeof(WardController), "Deposer");
        if (target == null) throw new MissingMethodException("WardController.Deposer");
        harmony.Patch(target, null, new HarmonyMethod(typeof(WardInWall), "AfterDeposer"));
        if (poller != null) return;
        poller = new GameObject("SteamBridge WardInWall");
        poller.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(poller);
        poller.AddComponent<WardInWallPoller>();
    }

    internal static void Clear() { watched.Clear(); fresh.Clear(); }

    internal static string Status() { return "removed " + removedTotal + " of " + seenDrops + " ward drops, watching " + watched.Count; }

    // every put-down of a ward on the ground (Recepteur.ScatterObjects -> Deposer): check it from the next quick tick on
    static void AfterDeposer(WardController __instance)
    {
        try
        {
            if (__instance == null || enabled == null || !enabled() || !NetworkServer.active || fresh.Count >= WardInWallRules.MaxWatched) return;
            int id = __instance.GetInstanceID();
            watched.Remove(id);   // a new put-down starts over
            fresh[id] = __instance;
            seenDrops++;
        }
        catch (Exception) { }
    }

    internal static void Tick()
    {
        if (watched.Count == 0 && fresh.Count == 0) return;   // nothing dropped: no work, no allocation
        if (enabled == null || !enabled() || !NetworkServer.active) { Clear(); return; }
        float now = Time.time;
        if (now < nextQuick) return;
        nextQuick = now + WardInWallRules.QuickSeconds;
        if (!announced && note != null)
        {
            announced = true;
            note("WARD-IN-WALL on (host only): a ward just put down whose body point (+" + WardInWallRules.CheckUp + " m) is in a full " + (countBlocks != null && countBlocks() ? "terrain or block" : "terrain")
                + " voxel on 2 checks >= " + WardInWallRules.RecheckSeconds + " s apart is removed (Deposer hook only; built wards and saves are never touched)");
        }
        List<WardController> check = new List<WardController>(watched.Count + fresh.Count);
        List<int> dead = null;
        foreach (KeyValuePair<int, Watched> p in watched) { if (p.Value.Ward == null) { if (dead == null) dead = new List<int>(); dead.Add(p.Key); } else check.Add(p.Value.Ward); }
        if (dead != null) for (int i = 0; i < dead.Count; i++) watched.Remove(dead[i]);
        foreach (KeyValuePair<int, WardController> p in fresh) if (p.Value != null && !watched.ContainsKey(p.Key)) check.Add(p.Value);
        fresh.Clear();
        bool blocks = countBlocks == null || countBlocks();
        foreach (WardController w in check)
        {
            try { CheckOne(w, now, blocks); }
            catch (Exception) { }
        }
        if (watched.Count > WardInWallRules.MaxWatched) watched.Clear();
    }

    static void CheckOne(WardController w, float now, bool blocks)
    {
        int id = w.GetInstanceID();
        GameObject go = w.gameObject;
        if (!go.activeInHierarchy) { watched.Remove(id); return; }
        GenericParentTracker t = go.GetComponent<GenericParentTracker>();
        bool onGround = t != null && t.IsOnGround();
        Vector3 pos = w.transform.position;
        XYZ cell = XYZ.FromVector3(pos + Vector3.up * WardInWallRules.CheckUp);
        bool terrain = false, block = false, solid = false;
        if (onGround)
        {
            bool full = Voxel.IsFull(cell);
            if (full) { terrain = Voxel.IsTerrain(cell); block = !terrain && Voxel.IsBlock(cell); }
            solid = WardInWallRules.Solid(full, terrain, block, blocks);
        }
        Watched seen; watched.TryGetValue(id, out seen);
        int hits = seen == null ? 0 : seen.Hits;
        bool sameCell = seen != null && seen.Cell.x == cell.x && seen.Cell.y == cell.y && seen.Cell.z == cell.z;
        float since = seen == null ? 0f : now - seen.First;
        switch (WardInWallRules.Judge(true, true, onGround, solid, hits, sameCell, since))
        {
            case WardWallStep.Forget: watched.Remove(id); return;
            case WardWallStep.Watch: watched[id] = new Watched { Ward = w, Cell = cell, First = now, Hits = 1 }; return;
            case WardWallStep.Wait: seen.Hits = hits + 1; return;
        }
        string what = "ward";
        try { if (t != null) { string n = t.AssetKey.Name; if (!string.IsNullOrEmpty(n)) what = n; } } catch (Exception) { }
        watched.Remove(id);
        go.SafeNetworkDestroy();
        removedTotal++;
        if (note != null && WardInWallRules.LogLine(removedTotal))
            note("WARD-IN-WALL removed " + what + " #" + id + " at " + pos.x.ToString("F1") + "," + pos.y.ToString("F1") + "," + pos.z.ToString("F1") + " cell " + cell.x + "," + cell.y + "," + cell.z
                + " in " + WardInWallRules.Kind(terrain, block) + " after " + since.ToString("F1") + " s | " + Status());
    }
}

internal sealed class WardInWallPoller : MonoBehaviour
{
    void Update() { try { WardInWall.Tick(); } catch (Exception) { } }
}
