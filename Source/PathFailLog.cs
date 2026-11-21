using System;
using System.Collections.Generic;
using Brix.Game.AI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.3.97 PATH-FAIL (log only). The bubble "Не можу дістатися до місця призначення" is a PathException that really failed. Reviewed: patching the generic factory
// PathException.To<T> logs node construction, not failures, and may change behaviour through shared generic code, so this hooks Labor.HandlePathFail(PathException e),
// which the game calls only when a worker's path failed. One line per worker+place per 4 s: exception type, worker, its occupation, target position, object name and
// its relevant components; for a door also the cells around it (nonEmpty / full / upNonEmpty / open). Own budget (150 lines), written straight to perf-trace.
internal static class PathFailLog
{
    static int lines;
    static readonly Dictionary<string, float> last = new Dictionary<string, float>();

    internal static void Install(Harmony harmony, Action<string> logger)
    {
        harmony.Patch(AccessTools.Method(typeof(Labor), "HandlePathFail"), null, new HarmonyMethod(AccessTools.Method(typeof(PathFailLog), "After")), null, null, null);
    }
    internal static void Clear() { last.Clear(); lines = 0; }

    // for a door: the four side cells of its lowest cell and the cell above each, to see what blocks the approach
    static string DoorCells(GameObject door)
    {
        try
        {
            Brix.Game.Components.GameComponent gc = door.GetComponent<Brix.Game.Components.GameComponent>();
            if (gc == null || gc.volume == null) return "";
            Brix.Game.Components.Volumes.IVolume volume = gc.volume; int bottom = int.MaxValue;
            foreach (Brix.Engine.XYZ c in volume.GetCases(Brix.Game.Components.Volumes.VProperty.occupy)) if (c.y < bottom) bottom = c.y;
            Brix.Engine.XYZ[] dirs = { new Brix.Engine.XYZ(1, 0, 0), new Brix.Engine.XYZ(-1, 0, 0), new Brix.Engine.XYZ(0, 0, 1), new Brix.Engine.XYZ(0, 0, -1) };
            string t = " | door cells bottom=" + bottom;
            foreach (Brix.Engine.XYZ c in volume.GetCases(Brix.Game.Components.Volumes.VProperty.occupy)) if (c.y == bottom)
                foreach (Brix.Engine.XYZ d in dirs)
                {
                    Brix.Engine.XYZ n = c + d; if (volume.HasPropertyAt(Brix.Game.Components.Volumes.VProperty.occupy, n)) continue;
                    t += " [" + n + " nonEmpty=" + Brix.Engine.Voxel.IsNonEmpty(n) + " full=" + Brix.Engine.Voxel.IsFull(n) + " upNonEmpty=" + Brix.Engine.Voxel.IsNonEmpty(n + Brix.Engine.XYZ.up) + " open=" + Brix.Engine.Voxel.IsOpenAndWalkable(n) + "]";
                }
            return t;
        }
        catch (Exception) { return ""; }
    }

    static void After(Labor __instance, Exceptions.PathException e)
    {
        try
        {
            if (lines >= 150 || !NetworkServer.active || __instance == null || e == null) return;
            string pos = "none", obj = "-", extra = "";
            if (e.Location != null)
            {
                try { Vector3 p = e.Location.Position; pos = p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1"); } catch (Exception) { }
                try
                {
                    GameObject o = e.Location.Object;
                    if (o != null)
                    {
                        obj = o.name; bool door = o.GetComponent<Brix.Legacy.Porte>() != null;
                        if (door) { obj += " [Porte]"; extra = DoorCells(o); }
                        else if (o.GetComponent<Brix.Game.Components.CatapultSpace.Catapult>() != null) obj += " [Catapult]";
                    }
                }
                catch (Exception) { }
            }
            string key = __instance.GetInstanceID() + "|" + pos; float t, now = Time.realtimeSinceStartup;
            if (last.TryGetValue(key, out t) && now - t < 4f) return;
            if (last.Count > 400) last.Clear();
            last[key] = now; lines++;
            CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX PATH-FAIL " + e.GetType().Name + " spread=" + e.SpreadExclusions + " worker " + __instance.name + " (" + __instance.Occupation.CurrentOccupation + ") target " + pos + " object " + obj + extra);
        }
        catch (Exception) { }
    }
}
