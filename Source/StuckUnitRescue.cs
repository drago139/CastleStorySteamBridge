using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.AI.Pathfinding;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.4.28 STUCK UNITS (host only; user video 2026-11-13: a worker jumped off a cliff onto a block placed in the sandbox, went through the block into the ground and then
// showed the bubble "cannot reach destination"; the 2 s BuriedRescue scan did not help and a lean build wrote no trace).
// 1) Rescue on path failure (Fixes/RescueStuckUnitOnPathFail): right after the game reports a failed path (Labor.HandlePathFail), a unit that stands inside full solid cells
//    (terrain or a full placed block), or where the game has no walkable node within one cell, is moved with the game's own Locomotion4.SetPosition(pos, forced) to the first
//    free cell straight above, else to the game's nearest free walkable position. Ordinary path failures (unit on a walkable node) are not touched.
// 2) Jump landing fallback (Fixes/JumpLandingFallback): JumpingOff.StartJumping sets the landing height to "20 cells down" when it finds no landing NODE under the cell in
//    front of the unit, even when solid ground or a block is there; the unit then dives through it. When the cells show something to stand on, the landing height is set there.
//    NOT proven to be the cause of the video case (no trace existed); every use is logged, so the next case shows it.
// Every action is one line in LogOutput.log (also in lean builds) and in perf-trace (GAMEPLAY-FIX STUCK / JUMP), at most 60 lines per game run.
internal static class StuckUnitRescue
{
    const int MaxLines = 60, MaxUp = 30, MaxDown = 21;
    static Func<bool> rescueOn, jumpOn; static Action<string> log;
    static FieldInfo locoField; static int lines;
    static readonly Dictionary<int, float> last = new Dictionary<int, float>();
    internal static bool PathHook, JumpHook;

    internal static void Install(Harmony harmony, Func<bool> rescueEnabled, Func<bool> jumpEnabled, Action<string> logger)
    {
        rescueOn = rescueEnabled; jumpOn = jumpEnabled; log = logger; PathHook = false; JumpHook = false;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Labor), "HandlePathFail"), null, new HarmonyMethod(AccessTools.Method(typeof(StuckUnitRescue), "AfterPathFail")), null, null, null);
            PathHook = true;
        }
        catch (Exception) { }
        try
        {
            locoField = AccessTools.Field(typeof(Brix.Game.Locomotion.LocomotionStateUpdater), "Locomotion");
            MethodInfo start = AccessTools.Method(typeof(Brix.Game.Locomotion.JumpingOff), "StartJumping");
            if (locoField != null && start != null)
            {
                harmony.Patch(start, null, new HarmonyMethod(AccessTools.Method(typeof(StuckUnitRescue), "AfterStartJumping")), null, null, null);
                JumpHook = true;
            }
        }
        catch (Exception) { }
    }

    internal static void Clear() { last.Clear(); lines = 0; }

    static string P(Vector3 v) { return v.x.ToString("F1") + "," + v.y.ToString("F1") + "," + v.z.ToString("F1"); }
    static XYZ Cell(Vector3 feet, float up) { return XYZ.FromVector3(feet + Vector3.up * up); }
    static void Line(string t) { if (lines++ < MaxLines && log != null) log(t); }

    internal static bool InsideSolid(Vector3 feet)
    {
        return StuckRescueRules.InsideSolid(Voxel.IsFull(Cell(feet, StuckRescueRules.ProbeLow)), Voxel.IsFull(Cell(feet, StuckRescueRules.ProbeHigh)));
    }

    // Where a stuck unit can stand. Inside solid: straight up first (first free cell with headroom above something solid, at most 30 cells), on the game's own floor height when
    // a walkable node is there, else the game's nearest free walkable position, else on top of the solid cell. Not inside solid: the game's nearest free walkable position
    // (5 cells). Vector3.zero = nothing found.
    internal static Vector3 FindFree(Locomotion4 l, Vector3 feet, bool inside, out string how)
    {
        how = "none";
        if (inside)
        {
            XYZ start = Cell(feet, StuckRescueRules.ProbeLow);
            bool[] column = new bool[MaxUp + 2];
            for (int k = 0; k < column.Length; k++) column[k] = Voxel.IsNonEmpty(new XYZ(start.x, start.y + k, start.z));
            int up = StuckRescueRules.FirstStandableUp(column);
            if (up < 1) return Vector3.zero;
            Vector3 centre = new Vector3(start.x, start.y + up, start.z), aligned;
            if (FloorPicker.GetNearestAligned(centre, l.FloorOffset, out aligned) && !FloorPicker.IsOccupiedOrInsideGround(l.gameObject, aligned, true)) { how = "walkable cell " + up + " up"; return aligned; }
            Vector3 near = FloorPicker.GetNearestFreePosition(l.gameObject, centre, l.FloorOffset, true, 5);
            if (near != centre) { how = "nearest free place from " + up + " up"; return near; }
            how = "cell " + up + " up, no walkable node near";
            return new Vector3(centre.x, centre.y - StuckRescueRules.FeetBelowCellCentre, centre.z);
        }
        Vector3 free = FloorPicker.GetNearestFreePosition(l.gameObject, feet, l.FloorOffset, true, 5);
        if (free != feet) { how = "nearest free place"; return free; }
        return Vector3.zero;
    }

    // Host only. True when the unit was moved. The caller decides when to ask (a failed path, or the BuriedRescue scan).
    internal static bool TryRescue(Locomotion4 l, string why)
    {
        if (l == null || !l.gameObject.activeInHierarchy) return false;
        if (!StuckRescueRules.MayRescue(l.IsInGroundedState(), l.IsFlailing(), l.IsClimbing(), l.IsJumpingOff)) return false;
        Vector3 feet = l.transform.position;
        bool inside = InsideSolid(feet);
        bool node = PathNodeRegistry.WalkableAt(Cell(feet, StuckRescueRules.ProbeLow)) != null;
        if (!StuckRescueRules.Stuck(inside, node)) return false;
        int id = l.GetInstanceID(); float now = Time.realtimeSinceStartup, before;
        last.TryGetValue(id, out before);
        if (!StuckRescueRules.Allow(now, before, 5.0)) return false;
        if (last.Count > 400) last.Clear();
        last[id] = now;
        string how; Vector3 to = FindFree(l, feet, inside, out how);
        string head = "STUCK " + l.name + " (" + why + ") at " + P(feet) + (inside ? ", inside solid" : ", no walkable node here");
        if (to == Vector3.zero) { Line(head + " -> no free place found, left as is"); return false; }
        l.SetPosition(to, true);
        Line(head + " -> moved to " + P(to) + " [" + how + "]");
        return true;
    }

    static void AfterPathFail(Labor __instance)
    {
        try
        {
            if (rescueOn == null || !rescueOn() || !NetworkServer.active || __instance == null || __instance.navigation == null) return;
            TryRescue(__instance.navigation.Locomotion, "path failed");
        }
        catch (Exception) { }
    }

    static void AfterStartJumping(Brix.Game.Locomotion.JumpingOff __instance)
    {
        try
        {
            if (jumpOn == null || !NetworkServer.active || __instance == null) return;
            Vector3 origin = __instance.JumpOrigin, dest = __instance.JumpDestination;
            if (!StuckRescueRules.IsVanillaNoFloor(origin.y, dest.y)) return;
            Locomotion4 l = locoField.GetValue(__instance) as Locomotion4;
            XYZ top = XYZ.FromVector3(new Vector3(dest.x, dest.y + 20f, dest.z));
            bool[] column = new bool[MaxDown + 1];
            for (int k = 0; k < column.Length; k++) column[k] = Voxel.IsNonEmpty(new XYZ(top.x, top.y - k, top.z));
            int down = StuckRescueRules.FirstStandableDown(column);
            string head = "JUMP " + (l != null ? l.name : "unit") + " from " + P(origin) + ": the game found no landing node in column " + top.x + "," + top.z;
            if (down < 0) { Line(head + " and nothing solid within 20 cells: free fall, as in the game"); return; }
            XYZ cell = new XYZ(top.x, top.y - down, top.z), below = new XYZ(cell.x, cell.y - 1, cell.z);
            string what = (Voxel.IsBlock(below) ? "a placed block" : "terrain") + (Voxel.IsFull(below) ? "" : " (not full)");
            Vector3 centre = new Vector3(cell.x, cell.y, cell.z), aligned;
            float y = centre.y - StuckRescueRules.FeetBelowCellCentre;
            if (FloorPicker.GetNearestAligned(centre, l != null ? l.FloorOffset : 0.05f, out aligned)) y = aligned.y;
            if (!jumpOn()) { Line(head + ", but " + what + " is " + down + " cells down (JumpLandingFallback is off: landing height stays " + dest.y.ToString("F1") + ")"); return; }
            __instance.JumpDestination = new Vector3(dest.x, y, dest.z);
            Line(head + ", but " + what + " is " + down + " cells down: landing height set to " + y.ToString("F1") + " instead of " + dest.y.ToString("F1"));
        }
        catch (Exception) { }
    }
}
