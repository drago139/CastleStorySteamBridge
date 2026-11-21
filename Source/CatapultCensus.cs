using System;
using System.Text;
using Brix.Game.Components;
using Brix.Game.Components.CatapultSpace;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.75 diagnostics only: at LEVEL-READY (and again 20 s later) log every catapult that exists in the loaded world with its position,
    // owner faction, active state and visible renderers. Used to check maps that contain ready-made catapults.
    public sealed partial class SteamBridgePlugin
    {
        static float catCensusAt;

        static void CatapultCensus(string when)
        {
            // 0.4.27 review: log only (PtLog). With PerfTrace off (lean build) and no armed bot the two scene scans per match (LEVEL-READY and 20 s later) were thrown away.
            if (!ptOn && !botTapOn) return;
            try
            {
                CatapultControl[] all = UnityEngine.Object.FindObjectsOfType<CatapultControl>();
                StringBuilder sb = new StringBuilder("CATAPULT-CENSUS " + when + ": " + all.Length + " catapult(s)");
                for (int i = 0; i < all.Length && i < 12; i++)
                {
                    CatapultControl c = all[i]; if (c == null) continue;
                    AffiliationComponent a = c.GetComponent<AffiliationComponent>();
                    Renderer[] rs = c.GetComponentsInChildren<Renderer>(true); int vis = 0; for (int k = 0; k < rs.Length; k++) if (rs[k] != null && rs[k].enabled) vis++;
                    Vector3 p = c.transform.position;
                    sb.Append(" | ").Append(c.name).Append(" pos=(").Append(p.x.ToString("F1")).Append(',').Append(p.y.ToString("F1")).Append(',').Append(p.z.ToString("F1")).Append(") faction=").Append(a != null && a.faction != null ? a.faction.name : "none").Append(" active=").Append(c.gameObject.activeInHierarchy).Append(" renderers=").Append(vis).Append('/').Append(rs.Length);
                }
                PtLog(sb.ToString());
            }
            catch (Exception e) { PtLog("CATAPULT-CENSUS failed: " + e.GetType().Name + " " + e.Message); }
        }

        void CatapultCensusTick()
        {
            if (catCensusAt > 0f && Time.realtimeSinceStartup >= catCensusAt) { catCensusAt = 0f; CatapultCensus("20 s after LEVEL-READY"); }
        }

        static void CatapultCensusArm() { catCensusAt = Time.realtimeSinceStartup + 20f; CatapultCensus("at LEVEL-READY"); }
    }
}
