using System;
using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Network;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.98 FLOATING (log only, host and guest). Units hanging in the air / stacked on one spot with a wrong animation (screenshot 2026-11-01): every 3 s each unit gets a ray straight down; a unit whose feet are
    // more than 1.2 cells above the first collider that is not its own, and that is not flailing, is a candidate; it must be one on two checks in a row. One line per unit per 30 s to perf-trace.txt (FLOATING,
    // max 150) with role, position, gap, what is below, the animation clip, parent, how many other units are within 0.4 cells (stacking) and the distance to the position the side channel last sent for it.
    // Nothing is changed. The line also reaches the friend in the automatic report (DiagLiveLines.cs).
    public sealed partial class SteamBridgePlugin
    {
        float floatNext; int floatLines; static readonly RaycastHit[] floatHits = new RaycastHit[16];
        readonly Dictionary<int, int> floatSeen = new Dictionary<int, int>(); readonly Dictionary<int, float> floatCool = new Dictionary<int, float>();

        void FloatLogTick()
        {
            try
            {
                if (!ptOn || !HpInGame()) { if (floatSeen.Count > 0) { floatSeen.Clear(); floatCool.Clear(); } return; }
                float now = Time.realtimeSinceStartup; if (now < floatNext) return; floatNext = now + 6f;
                if (floatLines >= 150) return;
                Locomotion4[] all = Loco4All(2.5f); HashSet<int> hit = new HashSet<int>();
                for (int i = 0; i < all.Length && floatLines < 150; i++)
                {
                    Locomotion4 l = all[i]; if (l == null || !l.gameObject.activeInHierarchy) continue;
                    Vector3 feet = l.transform.position; bool flail = false; try { flail = l.IsFlailing(); } catch (Exception) { }
                    if (flail) continue;
                    int n = Physics.RaycastNonAlloc(new Ray(feet + Vector3.up * 0.3f, Vector3.down), floatHits, 12f, ~0, QueryTriggerInteraction.Ignore);
                    float gap = 99f; string below = "nothing within 12";
                    for (int k = 0; k < n; k++)
                    {
                        Collider c = floatHits[k].collider; if (c == null || c.transform.IsChildOf(l.transform)) continue;
                        float g = floatHits[k].distance - 0.3f; if (g < gap) { gap = g; below = c.name + "[L" + c.gameObject.layer + "]"; }
                    }
                    Array.Clear(floatHits, 0, n);
                    if (gap <= 1.2f || gap >= 90f) continue;
                    int id = l.GetInstanceID(); hit.Add(id); int seen; floatSeen.TryGetValue(id, out seen); floatSeen[id] = seen + 1;
                    if (seen < 1) continue;
                    float cool; if (floatCool.TryGetValue(id, out cool) && now < cool) continue; floatCool[id] = now + 30f;
                    int stacked = 0; for (int j = 0; j < all.Length; j++) { if (j == i || all[j] == null) continue; Vector3 d = all[j].transform.position - feet; if (d.x * d.x + d.z * d.z < 0.16f && Mathf.Abs(d.y) < 1.5f) stacked++; }
                    string clip = "?"; try { Animator a = l.GetComponentInChildren<Animator>(); if (a != null) { AnimatorClipInfo[] ci = a.GetCurrentAnimatorClipInfo(0); if (ci != null && ci.Length > 0 && ci[0].clip != null) clip = ci[0].clip.name; } } catch (Exception) { }
                    string link = ""; try { UNetTransform ut = l.GetComponent<UNetTransform>(); if (ut != null && getServerPos != null) link = " serverPos " + Vector3.Distance(getServerPos(ut), feet).ToString("F1") + " away"; } catch (Exception) { }
                    PtLog("FLOATING " + (NetworkServer.active ? "host" : "guest") + " " + l.name + " feet " + feet.x.ToString("F1") + "," + feet.y.ToString("F1") + "," + feet.z.ToString("F1") + " gap " + gap.ToString("F1") + " above " + below + " clip " + clip
                        + " parent " + (l.transform.parent == null ? "none" : l.transform.parent.name) + " stackedWith " + stacked + link); floatLines++;
                }
                if (floatSeen.Count > 0) { List<int> gone = new List<int>(); foreach (int k in floatSeen.Keys) if (!hit.Contains(k)) gone.Add(k); foreach (int k in gone) floatSeen.Remove(k); if (floatCool.Count > 300) floatCool.Clear(); }
            }
            catch (Exception) { }
        }
    }
}
