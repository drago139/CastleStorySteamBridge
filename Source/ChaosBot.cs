using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Legacy;
using Brix.Transactions;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.97 CHAOS BOT (host only, OFF by default): makes trouble in the sandbox. Every Chaos/IntervalSeconds (default 40) it picks one random event near a random player-side unit:
    //   barrel strike  - an explosive barrel is dropped 10 cells above the unit with its fuse lit (it explodes about 5 s later);
    //   squad          - 3 to 5 Corruptrons of the enemy faction appear 14-22 cells away;
    //   barrel rain    - 4 lit barrels scattered over a 6-cell area.
    // Chaos/Level scales the amount (1..3). Config: Chaos/Enabled, Chaos/IntervalSeconds, Chaos/Level, Chaos/Barrels, Chaos/Squads. Everything is logged (CHAOS ...).
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> chaosOn, chaosBarrels, chaosSquads, camLit; static ConfigEntry<int> chaosEvery, chaosLevel, camCount, camEvery; float camNext;
        float chaosNext; int chaosEvents; readonly System.Random chaosRnd = new System.Random();

        void ChaosBotTick()
        {
            try
            {
                if (chaosOn == null)
                {
                    chaosOn = Config.Bind("Chaos", "Enabled", false, "Host only. A bot that causes chaos in the sandbox: lit explosive barrels dropped on your units and squads of enemy Corruptrons. Off by default.");
                    chaosEvery = Config.Bind("Chaos", "IntervalSeconds", 40, "Seconds between two chaos events (10..600).");
                    chaosLevel = Config.Bind("Chaos", "Level", 1, "How much happens per event: 1 = small, 2 = medium, 3 = heavy.");
                    chaosBarrels = Config.Bind("Chaos", "Barrels", true, "Allow barrel strikes and barrel rain.");
                    chaosSquads = Config.Bind("Chaos", "Squads", true, "Allow Corruptron squads.");
                    camCount = Config.Bind("Chaos", "CameraBarrels", 20, "Camera barrel test (host only): Ctrl+Shift+B drops this many explosive barrels (1..200) from above the point the camera looks at. Works even when Chaos/Enabled is false.");
                    camLit = Config.Bind("Chaos", "CameraBarrelsLit", false, "false = plain barrels pile up (a test of how many physical objects the game can hold); true = the fuse of every barrel is lit, they explode about 5 s later.");
                    camEvery = Config.Bind("Chaos", "CameraBarrelsEverySeconds", 0, "0 = only on the hotkey; N = also repeat the drop automatically every N seconds (5..600) while a match runs on the host.");
                }
                ChaosCameraBarrels();
                if (chaosOn.Value && Input.GetKey(KeyCode.F9) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) { chaosOn.Value = false; PtLog("CHAOS emergency stop (Ctrl+Shift+F9)"); status = "Хаос вимкнено (аварійно)."; return; }
                if (!chaosOn.Value || !NetworkServer.active || !HpInGame()) return;
                float now = Time.realtimeSinceStartup;
                if (chaosNext <= 0f) chaosNext = now + Mathf.Clamp(chaosEvery.Value, 10, 600);
                if (now < chaosNext) return;
                chaosNext = now + Mathf.Clamp(chaosEvery.Value, 10, 600);
                // the game is already struggling (a 26 s native freeze and a crash followed ~25 events in the test): do nothing while it runs below 20 fps
                if (Time.smoothDeltaTime > 0.05f) { PtLog("CHAOS skipped, game too slow (" + (1f / Mathf.Max(0.001f, Time.smoothDeltaTime)).ToString("F0") + " fps)"); return; }
                List<Vector3> spots = new List<Vector3>();
                foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>())
                    if (l != null && l.gameObject.activeInHierarchy && l.faction != null && !l.faction.isAI && l.faction != Faction.DefaultNeutral) spots.Add(l.transform.position);
                if (spots.Count == 0) return;
                Vector3 at = spots[chaosRnd.Next(spots.Count)];
                int level = Mathf.Clamp(chaosLevel.Value, 1, 3);
                List<int> kinds = new List<int>(); if (chaosBarrels.Value) { kinds.Add(0); kinds.Add(2); } if (chaosSquads.Value) kinds.Add(1);
                if (kinds.Count == 0) return;
                int kind = kinds[chaosRnd.Next(kinds.Count)]; chaosEvents++;
                if (kind == 0) ChaosBarrel(at + Vector3.up * 10f, "strike");
                else if (kind == 2) { int n = 2 + level * 2; for (int i = 0; i < n; i++) ChaosBarrel(at + new Vector3(Rnd(-6f, 6f), 10f + Rnd(0f, 4f), Rnd(-6f, 6f)), "rain"); }
                else
                {
                    int n = 2 + level + chaosRnd.Next(2);
                    for (int i = 0; i < n; i++)
                    {
                        float a = Rnd(0f, 6.283f), r = Rnd(14f, 22f); Vector3 p = at + new Vector3(Mathf.Cos(a) * r, 6f, Mathf.Sin(a) * r);
                        Transactor.SpawnUNet(Test.Corruptron, Faction.DefaultEnemy, p, Quaternion.identity);
                    }
                    PtLog("CHAOS squad of " + n + " Corruptrons near " + at.x.ToString("F0") + "," + at.y.ToString("F0") + "," + at.z.ToString("F0") + " (event #" + chaosEvents + ")");
                }
            }
            catch (Exception e) { chaosNext = Time.realtimeSinceStartup + 120f; PtLog("CHAOS failed: " + e.GetType().Name + " " + e.Message); }
        }

        // barrels above the point the camera looks at (a stress test of the game: how many physical objects / explosions it survives)
        void ChaosCameraBarrels()
        {
            if (!NetworkServer.active || !HpInGame()) return;
            bool key = Input.GetKey(KeyCode.B) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) && Input.GetKeyDown(KeyCode.B);
            float now = Time.realtimeSinceStartup; bool auto = false;
            if (camEvery.Value >= 5) { if (camNext <= 0f) camNext = now + camEvery.Value; else if (now >= camNext) { auto = true; camNext = now + Mathf.Clamp(camEvery.Value, 5, 600); } }
            if (!key && !auto) return;
            Camera cam = Camera.main; if (cam == null) return;
            Vector3 target = cam.transform.position + cam.transform.forward * 40f; RaycastHit hit;
            if (Physics.Raycast(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)), out hit, 2000f)) target = hit.point;
            int n = Mathf.Clamp(camCount.Value, 1, 200), made = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = target + new Vector3(Rnd(-5f, 5f), 8f + Rnd(0f, 10f), Rnd(-5f, 5f));
                GameObject barrel = Transactor.SpawnUNet(ObjetsDynamiques.Baril, Faction.DefaultEnemy, p, Quaternion.identity);
                if (barrel == null) continue; made++;
                if (camLit.Value) { Explosifs ex = barrel.GetComponent<Explosifs>(); if (ex != null) ex.CallRpcStartFuse(); }
            }
            PtLog("CHAOS camera barrels: " + made + "/" + n + " above " + target.x.ToString("F0") + "," + target.y.ToString("F0") + "," + target.z.ToString("F0") + " lit=" + camLit.Value + (auto ? " (auto)" : " (hotkey)"));
        }

        float Rnd(float a, float b) { return a + (float)chaosRnd.NextDouble() * (b - a); }

        void ChaosBarrel(Vector3 p, string why)
        {
            GameObject barrel = Transactor.SpawnUNet(ObjetsDynamiques.Baril, Faction.DefaultEnemy, p, Quaternion.identity);
            if (barrel == null) return;
            Explosifs ex = barrel.GetComponent<Explosifs>(); if (ex != null) ex.CallRpcStartFuse();
            PtLog("CHAOS barrel " + why + " at " + p.x.ToString("F0") + "," + p.y.ToString("F0") + "," + p.z.ToString("F0") + " fuse=" + (ex != null) + " (event #" + chaosEvents + ")");
        }
    }
}
