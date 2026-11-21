using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game.AI;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.97 BURIED RESCUE (host only). After a fall a unit can end up inside the terrain; the game then cannot path from its cell ("Не можу дістатися до місця призначення")
    // and it looks like the unit disappeared underground. Every 2 s the host looks at its units: a unit is buried only if two points on its body (0.3 and 0.9 above the feet) are
    // both inside TERRAIN voxels that are full. It must be buried on two checks in a row (>= 2 s). The rescue lifts it, through the game's own Locomotion4.SetPosition(pos, forced),
    // to the first free column cell above (at most 12 cells) that has solid ground under it and two free cells for the body, then waits 10 s before touching the same unit again.
    // One unit per check, every case written to perf-trace.txt (BURIED), max 80 lines. Config Fixes/RescueBuriedUnits (default true). Placed blocks, doors and stairs are never counted
    // (terrain only). Not verified in a live game.
    // 0.4.28: the place to stand comes from StuckUnitRescue.FindFree (up to 30 cells up, on a walkable cell or the game's nearest free place, never inside a placed block:
    // the old test '!IsFull' could pick the cell of a block that is not full). Each case is also one line in LogOutput.log: a lean build has perf-trace off.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> buriedCfg;
        float buriedNext; int buriedLines;
        readonly Dictionary<int, int> buriedSeen = new Dictionary<int, int>();
        readonly Dictionary<int, float> buriedCool = new Dictionary<int, float>();

        static bool BuriedAt(Vector3 feet, float up)
        {
            XYZ c = XYZ.FromVector3(feet + Vector3.up * up);
            return Voxel.IsTerrain(c) && Voxel.IsFull(c);
        }

        static string BuriedCarry(Locomotion4 l)
        {
            try
            {
                Brix.Game.Semantique.Recepteur r = l.GetComponent<Brix.Game.Semantique.Recepteur>();
                if (r == null || r.IsEmpty()) return " carries nothing";
                return " carries " + r.ContentDescription.GetPrintableDescription();
            }
            catch (Exception) { return ""; }
        }

        // 0.4.28: perf-trace line plus one line in LogOutput.log (rule: a fix logs when it acts; a lean build writes no perf-trace).
        void BuriedNote(string t) { if (buriedLines++ >= 80) return; PtLog(t); try { Logger.LogInfo("Buried units: " + t); } catch (Exception) { } }

        void BuriedRescueTick()
        {
            try
            {
                if (buriedCfg == null) buriedCfg = Config.Bind("Fixes", "RescueBuriedUnits", true, "Host only: a unit stuck inside the terrain (two body points inside full terrain voxels for 2 s, after a fall) is lifted to the first free cell above it. Terrain only; placed blocks, doors and stairs are ignored.");
                if (!buriedCfg.Value || !NetworkServer.active || !HpInGame()) { if (buriedSeen.Count > 0) { buriedSeen.Clear(); buriedCool.Clear(); } return; }
                float now = Time.realtimeSinceStartup;
                if (now < buriedNext) return;
                buriedNext = now + 2f;
                HashSet<int> hit = new HashSet<int>(); bool done = false;
                // 0.4.27 review: the unit list may be up to 10 s old (was 1 s = a new scan of all loaded objects on every 2 s check; with the diagnostics off this fix was the only caller, so the
                // host kept the 2 s scan comb of B-066). Dead or pooled units in an old list are skipped below; a unit younger than the list is first looked at up to 10 s later.
                foreach (Locomotion4 l in Loco4All(10f))
                {
                    if (l == null || !l.gameObject.activeInHierarchy) continue;
                    Vector3 feet = l.transform.position;
                    if (!BuriedAt(feet, 0.3f) || !BuriedAt(feet, 0.9f)) continue;
                    int id = l.GetInstanceID(); hit.Add(id);
                    int seen; buriedSeen.TryGetValue(id, out seen); buriedSeen[id] = seen + 1;
                    if (seen < 1 || done) continue;
                    float cool; if (buriedCool.TryGetValue(id, out cool) && now < cool) continue;
                    string how; Vector3 to = StuckUnitRescue.FindFree(l, feet, true, out how);
                    buriedCool[id] = now + 10f; done = true;
                    string head = "BURIED " + l.name + " at " + feet.x.ToString("F1") + "," + feet.y.ToString("F1") + "," + feet.z.ToString("F1") + (l.IsFlailing() ? " (flailing)" : "") + BuriedCarry(l);
                    if (to == Vector3.zero) { BuriedNote(head + " -> no free cell within 30 above, left as is"); continue; }
                    l.SetPosition(to, true);
                    BuriedNote(head + " -> lifted to " + to.x.ToString("F1") + "," + to.y.ToString("F1") + "," + to.z.ToString("F1") + " [" + how + "]");
                }
                if (buriedSeen.Count > 0)
                {
                    List<int> gone = new List<int>(); foreach (int k in buriedSeen.Keys) if (!hit.Contains(k)) gone.Add(k);
                    foreach (int k in gone) buriedSeen.Remove(k);
                    if (buriedCool.Count > 200) buriedCool.Clear();
                }
            }
            catch (Exception) { }
        }
    }
}
