using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.Legacy;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.3.108: ghost trees. A tree that the host fells (catapult bomb, barrel, worker) shows up on a guest as a spawn message followed at once by the cut RPC, often in the
// same packet. The guest handles both in one receive loop, so DamagedTree.Cut runs after Awake but before Start/Go: _ready is false, the game logs "Cut invoked on a tree that
// wasn't started properly" and drops the cut. The next frame Go() registers the tree and it stands (the voxel batch skips it, its own mesh is drawn, no collider) while the logs lie around;
// when the host releases its tree the guest's OnDestroy sees !_wasCut and ReRoot() puts the tree back into the registry for good.
// This guard (installed on every peer, nothing needed from the other side):
//  A) Cut prefix: a cut that arrives while the tree is not ready is parked (first one wins) instead of being dropped.
//  A2) Go postfix: as soon as Go has made the tree ready the parked cut is replayed through the game's own Cut. Event driven, runs inside Start.
//  B) OnDestroy prefix: a tree destroyed with a parked cut is marked as cut (so ReRoot cannot re-add it) and removed from the registry at the position captured when the cut arrived.
//  Tick: safety net for a Go that threw after setting _ready, dead trees and a tree that never became ready (given up after TreeCutRules.GuestTimeoutSeconds).
// Any exception inside a patch runs the vanilla code and logs once. TREECUT lines: role, position, how long the cut was parked and the outcome.
internal static class TreeCutGuard
{
    sealed class Pending
    {
        internal DamagedTree Tree; internal GameObject Obj; internal XYZ Pos; internal int Origin; internal bool Explosion; internal float T0;
    }

    static Func<bool> enabled;
    static Action<string> sink;
    static bool active;
    static FieldInfo wasCutF;
    static MethodInfo cutM;
    static readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
    static readonly List<int> scratch = new List<int>();
    static int lines, keyLines, guestCreatedLogged; static bool capNoted;
    static readonly HashSet<string> errorsSeen = new HashSet<string>();
    static float nextSweep, nextStats;
    // counters (also printed as one STATS line when they change)
    internal static int Parked, ReplayedGo, ReplayedTick, DestroyedParked, GivenUp, Duplicates, HostQueued, HostFired, HostTimeouts, HostDropped, HostDuplicates, HostImmediate, HostFailed, HostRequeued, GuestCreated;
    static string lastStats = "";

    internal static bool Active { get { return active; } }

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> logger)
    {
        wasCutF = AccessTools.Field(typeof(DamagedTree), "_wasCut");
        cutM = AccessTools.DeclaredMethod(typeof(DamagedTree), "Cut", null);
        MethodInfo go = AccessTools.DeclaredMethod(typeof(DamagedTree), "Go", null);
        MethodInfo destroy = AccessTools.DeclaredMethod(typeof(DamagedTree), "OnDestroy", null);
        if (wasCutF == null) throw new MissingFieldException("DamagedTree", "_wasCut");
        if (cutM == null) throw new MissingMethodException("DamagedTree", "Cut");
        if (go == null) throw new MissingMethodException("DamagedTree", "Go");
        if (destroy == null) throw new MissingMethodException("DamagedTree", "OnDestroy");
        enabled = isEnabled; sink = logger;
        harmony.Patch(cutM, new HarmonyMethod(AccessTools.Method(typeof(TreeCutGuard), "CutBefore")), null, null, null, null);
        harmony.Patch(go, null, new HarmonyMethod(AccessTools.Method(typeof(TreeCutGuard), "GoAfter")), null, null, null);
        harmony.Patch(destroy, new HarmonyMethod(AccessTools.Method(typeof(TreeCutGuard), "DestroyBefore")), null, null, null, null);
        active = true;
        // log-only probe: vanilla Architecte.CheckArbre runs on guests too and calls GetAtOrCreate there; a tree object made on a client is never networked and nothing ever cuts or releases it
        try
        {
            MethodInfo create = AccessTools.DeclaredMethod(typeof(DamagedTree), "GetAtOrCreate", new Type[] { typeof(XYZ) });
            if (create != null) harmony.Patch(create, new HarmonyMethod(AccessTools.Method(typeof(TreeCutGuard), "CreateBefore")), null, null, null, null);
        }
        catch (Exception e) { ErrorOnce("Install.GetAtOrCreate probe", e); }
    }

    internal static void Clear()
    {
        active = false; enabled = null; sink = null; pending.Clear(); scratch.Clear();
        TreeBlastFix.ClearQueue();
    }

    static bool On() { return active && enabled != null && enabled(); }

    internal static string Role() { return NetworkServer.active ? "host" : (NetworkClient.active ? "guest" : "offline"); }

    internal static string PosText(XYZ p) { return p.ToVector3().ToString("F0"); }

    internal static void Log(string text)
    {
        if (sink == null) return;
        if (!TreeCutRules.UnderLogCap(lines))
        {
            if (!capNoted) { capNoted = true; sink("TREECUT log cap reached (" + TreeCutRules.MaxLogLines + " lines); counters keep running"); }
            return;
        }
        lines++; sink(text);
    }

    // errors, timeouts, failed cuts and the counter lines have their own small budget so a heavy bombing session cannot hide them
    internal static void LogKey(string text)
    {
        if (sink == null || !TreeCutRules.UnderKeyLogCap(keyLines)) return;
        keyLines++; sink(text);
    }

    internal static void ErrorOnce(string where, Exception e)
    {
        try { if (errorsSeen.Add(where)) LogKey("TREECUT error in " + where + ": " + e.GetType().Name + " " + e.Message + " (vanilla behaviour used)"); } catch (Exception) { }
    }

    static bool WasCut(DamagedTree t) { return wasCutF != null && (bool)wasCutF.GetValue(t); }

    // the position DamagedTree.Go would register: the voxel position, else the cell under the transform
    static XYZ PosOf(DamagedTree t)
    {
        try { XYZ v = t.voxel.position; if (!(v == XYZ.zero)) return v; } catch (Exception) { }
        return XYZ.FromVector3(t.transform.position);
    }

    // ---- A ----
    static bool CutBefore(DamagedTree __instance, GameObject TreeGO, int origine, bool explosion)
    {
        try
        {
            if (!On() || __instance == null) return true;
            if (TreeCutRules.OnCut(__instance.Ready, WasCut(__instance)) == CutAction.Vanilla) return true;
            int id = __instance.GetInstanceID();
            if (pending.ContainsKey(id)) { Duplicates++; return false; }   // the vanilla code would have dropped this one too
            Pending r = new Pending();
            r.Tree = __instance; r.Obj = TreeGO != null ? TreeGO : __instance.gameObject; r.Pos = PosOf(__instance); r.Origin = origine; r.Explosion = explosion; r.T0 = Time.realtimeSinceStartup;
            pending[id] = r; Parked++;
            return false;
        }
        catch (Exception e) { ErrorOnce("CutBefore", e); return true; }
    }

    // ---- A2 ----
    static void GoAfter(DamagedTree __instance)
    {
        try
        {
            if (pending.Count == 0 || __instance == null) return;
            int id = __instance.GetInstanceID(); Pending r;
            if (!pending.TryGetValue(id, out r)) return;
            if (!TreeCutRules.RunAfterGo(true, __instance.Ready, WasCut(__instance))) return;   // not ready (host released it): the OnDestroy / Tick paths settle it
            pending.Remove(id);
            Replay(r, "go");
        }
        catch (Exception e) { ErrorOnce("GoAfter", e); }
    }

    static void Replay(Pending r, string via)
    {
        double ms = (Time.realtimeSinceStartup - r.T0) * 1000.0;
        bool ok = false; string failure = null;
        try
        {
            cutM.Invoke(r.Tree, new object[] { r.Obj != null ? r.Obj : r.Tree.gameObject, r.Origin, r.Explosion });
            ok = true;
        }
        catch (Exception e)
        {
            Exception inner = e.InnerException != null ? e.InnerException : e;
            failure = inner.GetType().Name + " " + inner.Message;
        }
        if (ok)
        {
            if (via == "go") ReplayedGo++; else ReplayedTick++;
            Log("TREECUT role=" + Role() + " pos=" + PosText(r.Pos) + " deferred=" + ms.ToString("F0") + "ms outcome=executed via=" + via);
        }
        else LogKey("TREECUT role=" + Role() + " pos=" + PosText(r.Pos) + " deferred=" + ms.ToString("F0") + "ms outcome=cut-failed " + failure);
    }

    // ---- B ----
    static void DestroyBefore(DamagedTree __instance)
    {
        try
        {
            if (pending.Count == 0 || __instance == null) return;
            int id = __instance.GetInstanceID(); Pending r;
            if (!pending.TryGetValue(id, out r)) return;
            pending.Remove(id); DestroyedParked++;
            double ms = (Time.realtimeSinceStartup - r.T0) * 1000.0;
            TreeRegistry registry = TreeRegistry.Instance;
            // Without a registry the vanilla OnDestroy would throw on the "was cut" branch: leave the flag alone then.
            if (registry != null && wasCutF != null)
            {
                wasCutF.SetValue(__instance, true);   // ReRoot must not put the felled tree back
                // DamagedTree.GetAt tolerates a tree one cell above or below, so the registry key may differ from the object's position by one in y (a column holds one tree)
                XYZ key = r.Pos;
                if (!registry.HasTree(key)) { if (registry.HasTree(key + XYZ.up)) key = key + XYZ.up; else if (registry.HasTree(key - XYZ.up)) key = key - XYZ.up; }
                if (registry.HasTree(key)) registry.Remove(key);
            }
            Log("TREECUT role=" + Role() + " pos=" + PosText(r.Pos) + " deferred=" + ms.ToString("F0") + "ms outcome=destroyed-before-ready registry=" + (registry != null));
        }
        catch (Exception e) { ErrorOnce("DestroyBefore", e); }
    }

    // ---- per frame pump (called from SteamBridgePlugin.Update) ----
    internal static void Tick()
    {
        try
        {
            if (!active) return;
            TreeBlastFix.Tick();
            float now = Time.realtimeSinceStartup;
            if (now < nextSweep) return;
            nextSweep = now + 0.5f;
            if (pending.Count > 0)
            {
                if (!NetworkServer.active && !NetworkClient.active) { pending.Clear(); }
                else Sweep(now);
            }
            if (now >= nextStats)
            {
                nextStats = now + 60f;
                string s = "parked=" + Parked + " replayedGo=" + ReplayedGo + " replayedTick=" + ReplayedTick + " destroyedParked=" + DestroyedParked + " givenUp=" + GivenUp + " dup=" + Duplicates
                    + " hostQueued=" + HostQueued + " hostFired=" + HostFired + " hostTimeout=" + HostTimeouts + " hostDropped=" + HostDropped + " hostRequeued=" + HostRequeued + " hostFailed=" + HostFailed
                    + " hostDup=" + HostDuplicates + " hostImmediate=" + HostImmediate + " guestCreated=" + GuestCreated;
                if (s != lastStats && (Parked + HostQueued + HostImmediate + HostDuplicates + GuestCreated) > 0) { lastStats = s; LogKey("TREECUT stats role=" + Role() + " " + s); }
            }
        }
        catch (Exception e) { ErrorOnce("Tick", e); }
    }

    static void Sweep(float now)
    {
        scratch.Clear();
        foreach (int k in pending.Keys) scratch.Add(k);
        for (int i = 0; i < scratch.Count; i++)
        {
            Pending r;
            if (!pending.TryGetValue(scratch[i], out r)) continue;
            bool alive = r.Tree != null;
            bool ready = alive && r.Tree.Ready, cut = alive && WasCut(r.Tree);
            double age = now - r.T0;
            switch (TreeCutRules.Sweep(alive, ready, cut, age))
            {
                case PendingAction.Keep: break;
                case PendingAction.RunNow: pending.Remove(scratch[i]); Replay(r, "tick"); break;
                case PendingAction.DropDone: pending.Remove(scratch[i]); break;
                case PendingAction.DropDead:
                    pending.Remove(scratch[i]); DestroyedParked++;
                    Log("TREECUT role=" + Role() + " pos=" + PosText(r.Pos) + " deferred=" + (age * 1000.0).ToString("F0") + "ms outcome=destroyed-before-ready registry=unknown (tree object gone)");
                    break;
                case PendingAction.DropTimeout:
                    pending.Remove(scratch[i]); GivenUp++;
                    LogKey("TREECUT role=" + Role() + " pos=" + PosText(r.Pos) + " deferred=" + (age * 1000.0).ToString("F0") + "ms outcome=timeout (tree never became ready)");
                    break;
            }
        }
    }

    // ---- log-only probe ----
    // A tree object created on a guest (vanilla Architecte.CheckArbre -> GetAtOrCreate(...).Blast() runs on every peer) is not networked: NetworkServer.Spawn fails, nobody ever cuts or releases it.
    static void CreateBefore(XYZ __0)
    {
        try
        {
            if (!active || NetworkServer.active || !NetworkClient.active) return;
            if (DamagedTree.GetAt(__0) != null) return;   // an existing tree is returned, nothing is created
            GuestCreated++;
            if (guestCreatedLogged < 20) { guestCreatedLogged++; LogKey("TREECUT role=guest pos=" + PosText(__0) + " outcome=guest-created-tree (vanilla CheckArbre on the guest; this tree object is never networked and stays standing)"); }
        }
        catch (Exception e) { ErrorOnce("CreateBefore", e); }
    }
}
