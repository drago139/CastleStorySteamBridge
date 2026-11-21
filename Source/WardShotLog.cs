using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components.Tracking;
using Brix.Game.Components.Wards;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.3.98 WARDSHOT / UNITSHOT (log only): every shot fired through ProjectileWeapon.Fire (host only) is written to perf-trace.txt with what it aims at and what stands in the way, so it is clear why crossbowmen
// and wards "shoot trees and walls". WARDSHOT = Sentinel ward (position, range, tracked relations); UNITSHOT = a unit's crossbow/bow (shooter, weapon, profession, faction, kind of target location: an object or a bare
// point). Both: the target (object name, parent, layer, components, faction, distance, height) and the first things a ray from the muzzle to the target crosses on the projectile layer mask (name + distance).
// Max 1 line per weapon per 1 s (wards 2 s) and 600 lines per run. Changes nothing.
internal static class WardShotLog
{
    static int total; static readonly Dictionary<int, float> last = new Dictionary<int, float>(); static FieldInfo relations;
    static readonly RaycastHit[] hits = new RaycastHit[24];

    internal static void Install(Harmony harmony)
    {
        relations = AccessTools.Field(typeof(TrackingController), "trackedRelations");
        MethodInfo fire = AccessTools.Method(typeof(ProjectileWeapon), "Fire");
        if (fire == null) return;
        harmony.Patch(fire, new HarmonyMethod(AccessTools.Method(typeof(WardShotLog), "Before")), null, null, null, null);
    }

    static string Name(GameObject go)
    {
        if (go == null) return "null";
        GameObject sp = null; try { sp = SignificantParent.Of(go); } catch (Exception) { }
        return go.name + (sp != null && sp != go ? " <" + sp.name : "") + "[L" + go.layer + "]";
    }

    static void Before(ProjectileWeapon __instance, Vector3 origin, Vector3 target, GameObject source, Location targetLocation)
    {
        try
        {
            if (__instance == null || !NetworkServer.active || total >= 600) return;
            WardAttack wa = __instance as WardAttack; Labor shooter = wa == null && source != null ? source.GetComponent<Labor>() : null;
            int id = __instance.GetInstanceID(); float now = Time.realtimeSinceStartup, t; if (last.TryGetValue(id, out t) && now - t < (wa != null ? 2f : 1f)) return; last[id] = now; total++;
            GameObject tgo = targetLocation == null ? null : targetLocation.Object; StringBuilder sb = new StringBuilder(wa != null ? "WARDSHOT #" : "UNITSHOT #").Append(total);
            Vector3 sp = source != null ? source.transform.position : __instance.transform.position;
            if (wa != null)
            {
                WardController wc = wa.GetComponent<WardController>();
                sb.Append(" ward ").Append(Mathf.Round(sp.x)).Append(',').Append(Mathf.Round(sp.y)).Append(',').Append(Mathf.Round(sp.z));
                sb.Append(" range ").Append(wc == null ? -1f : wc.EffectRange).Append(" tracked ").Append(wc != null && relations != null ? relations.GetValue(wc).ToString() : "?");
            }
            else
            {
                Faction sf = source == null ? null : source.GetComponentInParent<Faction>();
                sb.Append(" shooter ").Append(Name(source)).Append(" weapon ").Append(__instance.name).Append(" profession ").Append(shooter == null || shooter.Profession == null ? "?" : shooter.Profession.GetType().Name).Append(" faction ").Append(sf == null ? "none" : sf.ID.ToString());
                sb.Append(" at ").Append(Mathf.Round(sp.x)).Append(',').Append(Mathf.Round(sp.y)).Append(',').Append(Mathf.Round(sp.z)).Append(" targetKind ").Append(targetLocation == null ? "null" : targetLocation.GetType().Name);
            }
            sb.Append(" -> target ").Append(Name(tgo));
            if (tgo != null)
            {
                sb.Append(" dist ").Append(Vector3.Distance(origin, tgo.transform.position).ToString("F1")).Append(" dy ").Append((tgo.transform.position.y - origin.y).ToString("F1"));
                Faction f = tgo.GetComponentInParent<Faction>(); sb.Append(" faction ").Append(f == null ? "none" : f.ID.ToString());
                sb.Append(" comps ");
                Component[] cs = tgo.GetComponents<Component>(); int n = 0;
                for (int i = 0; i < cs.Length && n < 10; i++) { if (cs[i] == null) continue; string tn = cs[i].GetType().Name; if (tn == "Transform" || tn == "NetworkIdentity") continue; if (n++ > 0) sb.Append('+'); sb.Append(tn); }
            }
            Vector3 aim = tgo != null ? targetLocation.RangeHitPosition : target; Vector3 d = aim - origin; sb.Append(" aim ").Append(aim.x.ToString("F0")).Append(',').Append(aim.y.ToString("F0")).Append(',').Append(aim.z.ToString("F0"));
            int mask = wa != null ? wa.ProjectileLayerMask : (int)__instance.AmmoCollisionLayer.value;
            int cnt = Physics.RaycastNonAlloc(new Ray(origin, d.normalized), hits, d.magnitude, mask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, 0, cnt, new HitDist());
            sb.Append(" ray crosses ").Append(cnt).Append(": ");
            for (int i = 0; i < cnt && i < 5; i++) { if (i > 0) sb.Append(", "); GameObject h = hits[i].collider == null ? null : SignificantParent.Of(hits[i]); sb.Append(Name(h)).Append(h == tgo ? "(TARGET)" : "").Append('@').Append(hits[i].distance.ToString("F1")); }
            Array.Clear(hits, 0, cnt);
            CastleStorySteam.SteamBridgePlugin.PtLog(sb.ToString());
        }
        catch (Exception) { }
    }

    sealed class HitDist : IComparer<RaycastHit> { public int Compare(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); } }
}
