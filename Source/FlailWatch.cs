using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.AI;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.97 FLAIL-WATCH (log only). After a fall from a height a unit can stay slumped ("flailing"). The game leaves that state when the body is slow enough
    // (< 0.5 for 1 s) or grounded. Every 2 s, in a match, units that have been flailing for longer than 8 s are written once (and again at 30 s) to perf-trace.txt with
    // position, body speed and whether this PC is the host, so it shows whether the state really stays stuck and on which side. Changes nothing. Max 60 lines.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> flailCfg;
        float flailNext; int flailLogged;
        readonly Dictionary<int, float> flailSince = new Dictionary<int, float>();
        readonly Dictionary<int, int> flailStage = new Dictionary<int, int>();

        void FlailWatchTick()
        {
            try
            {
                if (flailCfg == null) flailCfg = Config.Bind("Diag", "FlailWatch", true, "Log units that stay in the fallen/flailing state for more than 8 s (position, speed, host or guest). Changes nothing in the game.");
                if (!flailCfg.Value || !ptOn || flailLogged >= 60 || !HpInGame()) { if (flailSince.Count > 0) { flailSince.Clear(); flailStage.Clear(); } return; }
                float now = Time.realtimeSinceStartup;
                if (now < flailNext) return;
                flailNext = now + 5f;
                HashSet<int> alive = new HashSet<int>();
                foreach (Locomotion4 l in Loco4All(2.5f))
                {
                    if (l == null || !l.IsFlailing()) continue;
                    int id = l.GetInstanceID(); alive.Add(id);
                    float since; if (!flailSince.TryGetValue(id, out since)) { flailSince[id] = now; continue; }
                    float dur = now - since; int stage; flailStage.TryGetValue(id, out stage);
                    int want = dur >= 30f ? 2 : (dur >= 8f ? 1 : 0);
                    if (want <= stage) continue;
                    flailStage[id] = want; flailLogged++;
                    Rigidbody rb = l.FlailingColliderRigidbody; Vector3 p = l.transform.position;
                    PtLog("FLAIL-WATCH " + l.name + " flailing for " + dur.ToString("F0") + " s at " + p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1") +
                        " speed " + (rb != null ? rb.velocity.magnitude.ToString("F2") : "n/a") + (rb != null && rb.isKinematic ? " (kinematic)" : "") + (NetworkServer.active ? " host" : " guest"));
                }
                if (flailSince.Count > 0)
                {
                    List<int> gone = new List<int>(); foreach (int k in flailSince.Keys) if (!alive.Contains(k)) gone.Add(k);
                    foreach (int k in gone) { flailSince.Remove(k); flailStage.Remove(k); }
                }
            }
            catch (Exception) { }
        }
    }
}
