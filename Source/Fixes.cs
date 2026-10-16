using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.9 "Fixes": two stock-game bugs found in the friends' sessions, both fixed on the PC that runs the bridge (the host's copy is the one that matters).
    //
    // 1) Ward inside a crystal. A Bricktron that drops a ward at a crystal turns to face it, and the game puts the item 1 m "forward"
    //    (VolumeRegistry.ClosestAvailableDropPosition: first of forward/right/left/back whose cell has no occupy/operation volume). A crystal only
    //    registers volume in its lowest cells, so when the Bricktron stands on a raised structure the cell in front is free and the ward lands inside
    //    the crystal. The item is placed twice (DropAll with the item, then Recepteur.ProcessParentlessObjets with forceDrop and no item - the second
    //    call decides the final spot), so both calls are patched: a spot inside a crystal's keep-out zone (capsule radius + margin) is replaced by the
    //    next candidate outside every zone that passes the game's own checks, or pushed straight out of the zone.
    // 2) Items buried by the terrain "level" tool. LandscapeGoal.FillAction fills the empty voxel an item lies in (CallRpcAddTerrainResources); the
    //    game's BuryBehavior only pops items out when the voxel ABOVE is solid. A fill goal whose voxel holds a loose item is not offered to any
    //    Bricktron, and the fill itself is skipped if an item appeared after the goal was handed out. When the item is moved, the fill goes on.
    // Both can be switched off in BepInEx\config\local.castlestory.steambridge.cfg ([Fixes]). Nothing here touches the network protocol.
    public sealed partial class SteamBridgePlugin
    {
        ConfigEntry<bool> fixWardDrop, fixLandscape;
        ConfigEntry<float> fixMargin;
        static Type fxCristal, fxApparence, fxLabor, fxRegistry, fxXyz;
        static MethodInfo fxFromVector3, fxHasVolumes, fxDropValid, fxRotationIndex;
        static FieldInfo fxAjust, fxRestrict, fxAllCrystals;
        static int fxWardMoved, fxFillBlocked, fxLogged;
        static readonly Dictionary<int, float> fxBlockedUntil = new Dictionary<int, float>();
        static readonly Dictionary<int, bool> fxBlockedValue = new Dictionary<int, bool>();
        static readonly Dictionary<long, float> fxFillSeen = new Dictionary<long, float>();
        static List<FxZone> fxZoneCache = new List<FxZone>();
        static float fxZoneUntil;
        const int FxLogLimit = 60;

        sealed class FxZone { public string Name; public Vector3 Axis; public float Radius; public float HalfHeight; }

        void PatchFixes()
        {
            try
            {
                fixWardDrop = Config.Bind("Fixes", "WardNotInsideCrystal", true, new ConfigDescription(
                    "A ward dropped by a Bricktron next to a crystal is never placed inside the crystal (used on the host; harmless on guests)."));
                fixLandscape = Config.Bind("Fixes", "LevelToolKeepsItems", true, new ConfigDescription(
                    "The terrain level tool does not fill a voxel that holds a loose item (ward, stone, crystal, crate...), so items are not buried."));
                fixMargin = Config.Bind("Fixes", "CrystalKeepOutMargin", 0.9f, new ConfigDescription(
                    "Metres added to the crystal's capsule radius to get the zone where wards are not dropped."));
                fxApparence = FxFindType("Apparence");
                fxLabor = FxFindType("Labor");
                if (fixWardDrop.Value) PatchWardDrop();
                if (fixLandscape.Value) PatchLandscape();
            }
            catch (Exception e) { Logger.LogWarning("Game fixes unavailable: " + e.Message); }
        }

        static Type FxFindType(string simple)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] all;
                try { all = a.GetTypes(); } catch (ReflectionTypeLoadException e) { all = e.Types; } catch { continue; }
                foreach (Type t in all) if (t != null && t.Name == simple) return t;
            }
            return null;
        }

        static string FxV(Vector3 v) { return v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0"); }

        void FxLog(string text)
        {
            if (fxLogged >= FxLogLimit) return;
            fxLogged++;
            Logger.LogInfo("Fix: " + text);
        }

        // the game's voxel-volume registry: the same "occupy|operation" test the game itself uses for drop spots (placed structures register there)
        void FxInitVolumes()
        {
            if (fxHasVolumes != null && fxFromVector3 != null) return;
            BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            fxRegistry = FxFindType("VolumeRegistry");
            if (fxRegistry == null) return;
            fxRestrict = fxRegistry.GetField("RestrictingDropVProperties", all);
            foreach (MethodInfo m in fxRegistry.GetMethods(all))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (m.Name == "HasVolumesWithPropertyAt" && !m.IsGenericMethod && ps.Length == 2 && fxRestrict != null && ps[0].ParameterType.Name == "XYZ" && ps[1].ParameterType == fxRestrict.FieldType) fxHasVolumes = m;
            }
            if (fxHasVolumes == null) return;
            // the exact XYZ type the game's own method takes
            fxXyz = fxHasVolumes.GetParameters()[0].ParameterType;
            foreach (MethodInfo fm in fxXyz.GetMethods(all))
            {
                ParameterInfo[] fps = fm.GetParameters();
                if (fm.Name == "FromVector3" && fps.Length >= 1 && fps[0].ParameterType.Name == "Vector3" && (fxFromVector3 == null || fps.Length == 1)) fxFromVector3 = fm;
            }
        }

        // ------------------------------------------------------------------ 1) ward not inside a crystal

        void PatchWardDrop()
        {
            try
            {
                BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                FxInitVolumes();
                fxCristal = FxFindType("SpawnerCristal");
                Type rotations = FxFindType("PreprocessedRotations");
                if (fxRegistry == null || fxCristal == null) { Logger.LogWarning("Fix 'ward not inside crystal': game types not found; skipped."); return; }
                fxAjust = fxRegistry.GetField("AjustementPositions", all);
                fxAllCrystals = fxCristal.GetField("AllCrystals", all);
                fxDropValid = fxRegistry.GetMethod("DropValidAtPosition", all);
                fxRotationIndex = rotations == null ? null : rotations.GetMethod("RotationToDiscreteIndex", all, null, new Type[] { typeof(Quaternion) }, null);
                MethodInfo target = null;
                foreach (MethodInfo m in fxRegistry.GetMethods(all)) if (m.Name == "ClosestAvailableDropPosition") target = m;
                MethodInfo after = typeof(SteamBridgePlugin).GetMethod("FxAfterDrop", BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null || after == null || fxAjust == null || fxRestrict == null || fxHasVolumes == null || fxFromVector3 == null || fxAllCrystals == null)
                { Logger.LogWarning("Fix 'ward not inside crystal': hook parts missing; skipped."); return; }
                discoveryHarmony.Patch(target, null, new HarmonyMethod(after));
                Logger.LogInfo("Fix 'ward not inside crystal' active (margin " + fixMargin.Value.ToString("0.0#") + " m).");
            }
            catch (Exception e) { Logger.LogWarning("Fix 'ward not inside crystal' unavailable: " + e.Message); }
        }

        void ResetFixCaches()
        {
            fxBlockedUntil.Clear(); fxBlockedValue.Clear(); fxFillSeen.Clear(); fxZoneCache.Clear(); fxZoneUntil = 0;
        }
        static List<FxZone> FxZones()
        {
            if (Time.time < fxZoneUntil) return fxZoneCache;
            List<FxZone> list = new List<FxZone>();
            IEnumerable all = fxAllCrystals.GetValue(null) as IEnumerable;
            if (all != null)
                foreach (object o in all)
                {
                    Component c = o as Component;
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    CapsuleCollider cc = c.GetComponent<CapsuleCollider>();
                    FxZone z = new FxZone();
                    z.Name = c.gameObject.name;
                    if (cc != null)
                    {
                        Vector3 s = cc.transform.lossyScale;
                        z.Axis = cc.transform.TransformPoint(cc.center);
                        z.Radius = cc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                        z.HalfHeight = Mathf.Max(cc.height * Mathf.Abs(s.y), cc.radius * 2f) * 0.5f;
                    }
                    else { z.Axis = c.transform.position; z.Radius = 0.8f; z.HalfHeight = 1.5f; }
                    list.Add(z);
                }
            fxZoneCache = list;
            fxZoneUntil = Time.time + 2f;
            return list;
        }

        static float FxHorizontal(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        static FxZone FxZoneAt(List<FxZone> zones, Vector3 p, float extra)
        {
            foreach (FxZone z in zones)
                if (CrystalKeepOut.Contains(p.x, p.y, p.z, z.Axis.x, z.Axis.y, z.Axis.z, z.Radius, z.HalfHeight, extra)) return z;
            return null;
        }

        static object FxCell(Vector3 v)
        {
            ParameterInfo[] fps = fxFromVector3.GetParameters();
            object[] args = new object[fps.Length];
            args[0] = v;
            for (int i = 1; i < fps.Length; i++) args[i] = fps[i].DefaultValue;
            return fxFromVector3.Invoke(null, args);
        }

        // the game's own acceptance test for a drop spot
        static bool FxGameAccepts(Transform reference, Vector3 v, int count, GameObject subject, bool force)
        {
            object xyz = FxCell(v);
            if ((bool)fxHasVolumes.Invoke(null, new object[] { xyz, fxRestrict.GetValue(null) })) return false;
            if (force || count != 1 || fxDropValid == null || fxRotationIndex == null) return true;
            object rot = fxRotationIndex.Invoke(null, new object[] { reference.rotation });
            return (bool)fxDropValid.Invoke(null, new object[] { subject, xyz, rot });
        }

        static void FxAfterDrop(ref Vector3 __result, Transform reference, int objectCount, GameObject dropSubjectSample, bool forceDrop)
        {
            SteamBridgePlugin plugin = discoveryInstance;
            try
            {
                if (plugin == null || plugin.fixWardDrop == null || !plugin.fixWardDrop.Value || reference == null || __result == Vector3.zero) return;
                List<FxZone> zones = FxZones();
                if (zones.Count == 0) return;
                float margin = plugin.fixMargin.Value;
                FxZone inside = FxZoneAt(zones, __result, margin);
                if (inside == null) return;
                // wards, and the forced no-item call that fixes the final spot (any item thrown into a crystal's footprint is wrong)
                bool ward = dropSubjectSample != null && dropSubjectSample.name.IndexOf("Ward", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!ward && !forceDrop) return;
                Vector3 chosen = Vector3.zero;
                Vector3[] adj = fxAjust.GetValue(null) as Vector3[];
                if (adj != null)
                    foreach (Vector3 a in adj)
                    {
                        Vector3 p = reference.TransformPoint(a) + (objectCount == 1 ? new Vector3(0f, 0.5f, 0f) : Vector3.up);
                        if (FxZoneAt(zones, p, margin) == null && FxGameAccepts(reference, p, objectCount, dropSubjectSample, forceDrop)) { chosen = p; break; }
                    }
                string how = "next free side";
                if (chosen == Vector3.zero)
                {
                    Vector3 dir = reference.position - inside.Axis; dir.y = 0f;
                    if (dir.sqrMagnitude < 0.01f) dir = -reference.forward;
                    dir.Normalize();
                    float r = inside.Radius + margin + 0.15f;
                    // Search around the crystal; every fallback passes the same acceptance check as the four ordinary candidates.
                    for (int ring = 0; ring < 4 && chosen == Vector3.zero; ring++)
                        for (int side = 0; side < 16; side++)
                        {
                            Vector3 radial = Quaternion.Euler(0, side * 22.5f, 0) * dir;
                            Vector3 candidate = new Vector3(inside.Axis.x + radial.x * (r + ring), reference.position.y + 0.5f, inside.Axis.z + radial.z * (r + ring));
                            if (FxZoneAt(zones, candidate, margin) == null && FxGameAccepts(reference, candidate, objectCount, dropSubjectSample, forceDrop))
                            { chosen = candidate; break; }
                        }
                    if (chosen == Vector3.zero) { plugin.FxLog("no validated drop position outside " + inside.Name + "; original drop retained"); return; }
                    how = "validated outer position";
                }
                if (ward || objectCount == 1) fxWardMoved++;
                plugin.FxLog("drop by " + reference.name + " at " + FxV(reference.position) + (dropSubjectSample != null ? " of " + dropSubjectSample.name : " (final spot)")
                    + " would land inside " + inside.Name + " at " + FxV(__result) + "; " + how + ": " + FxV(chosen));
                __result = chosen;
            }
            catch (Exception e) { if (plugin != null) plugin.FxLog("ward drop check failed (drop unchanged): " + e.GetType().Name + " " + e.Message); }
        }

        // ------------------------------------------------------------------ 2) level tool must not bury items

        void PatchLandscape()
        {
            try
            {
                Type goal = FxFindType("LandscapeGoal"), batcher = FxFindType("VoxelRequestBatcher");
                if (fxApparence == null || goal == null || batcher == null) { Logger.LogWarning("Fix 'level tool keeps items': game types not found; skipped."); return; }
                FxInitVolumes();
                MethodInfo match = goal.GetMethod("IsMatchFor", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo add = batcher.GetMethod("CallRpcAddTerrainResources", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo afterMatch = typeof(SteamBridgePlugin).GetMethod("FxAfterMatch", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo beforeAdd = typeof(SteamBridgePlugin).GetMethod("FxBeforeAdd", BindingFlags.Static | BindingFlags.NonPublic);
                if (match == null || add == null) { Logger.LogWarning("Fix 'level tool keeps items': hook not found; skipped."); return; }
                discoveryHarmony.Patch(match, null, new HarmonyMethod(afterMatch));
                discoveryHarmony.Patch(add, new HarmonyMethod(beforeAdd), null);
                Logger.LogInfo("Fix 'level tool keeps items' active.");
            }
            catch (Exception e) { Logger.LogWarning("Fix 'level tool keeps items' unavailable: " + e.Message); }
        }

        static object FxMember(object o, string name)
        {
            if (o == null) return null;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            try
            {
                for (Type t = o.GetType(); t != null; t = t.BaseType)
                {
                    PropertyInfo p = t.GetProperty(name, flags);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                    FieldInfo f = t.GetField(name, flags);
                    if (f != null) return f.GetValue(o);
                }
            }
            catch { }
            return null;
        }

        // is the voxel taken by a placed structure? (furnace, trap, crate... register "occupy"/"operation" volumes in the game's registry)
        static bool FxCellOccupied(Vector3 centre)
        {
            if (fxHasVolumes == null || fxFromVector3 == null || fxRestrict == null) return false;
            ParameterInfo[] fps = fxFromVector3.GetParameters();
            object[] args = new object[fps.Length];
            args[0] = centre;
            for (int i = 1; i < fps.Length; i++) args[i] = fps[i].DefaultValue;
            object xyz = fxFromVector3.Invoke(null, args);
            return (bool)fxHasVolumes.Invoke(null, new object[] { xyz, fxRestrict.GetValue(null) });
        }

        // loose items and placed objects in the voxel: any collider of an item/structure (triggers too: a bear trap is a trigger) that overlaps the voxel
        // box, whatever its origin (a furnace covers several voxels), plus anything the game's volume registry says occupies the voxel.
        static List<string> FxItemsIn(Vector3 centre)
        {
            List<string> found = new List<string>();
            Collider[] hits = Physics.OverlapBox(centre, new Vector3(0.45f, 0.45f, 0.45f), Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            foreach (Collider c in hits)
            {
                if (c == null) continue;
                Vector3 size = c.bounds.size;
                if (size.x > 6f || size.y > 6f || size.z > 6f) continue;               // area colliders (ward auras, the level project, terrain chunks) are not objects
                if (c.isTrigger && (size.x > 2.5f || size.y > 2.5f || size.z > 2.5f)) continue;   // only small triggers count (a trap's), not effect zones
                Component ap = c.GetComponentInParent(fxApparence);
                if (ap == null) continue;
                if (fxLabor != null && ap.GetComponent(fxLabor) != null) continue;      // Bricktrons are pushed out by the game itself
                if (ap.gameObject.name.StartsWith("Landscape")) continue;                // the level project's own object (root at 0,0,0)
                int id = ap.gameObject.GetInstanceID();
                if (seen.ContainsKey(id)) continue;
                seen[id] = true;
                found.Add(ap.gameObject.name);
            }
            try { if (found.Count == 0 && FxCellOccupied(centre)) found.Add("structure (volume)"); } catch { }
            return found;
        }

        void FxNoteBlocked(Vector3 centre, List<string> items, string where)
        {
            long key = ((long)(int)centre.x * 73856093L) ^ ((long)(int)centre.y * 19349663L) ^ ((long)(int)centre.z * 83492791L);
            float last;
            if (fxFillSeen.TryGetValue(key, out last) && Time.realtimeSinceStartup - last < 30f) return;   // count a voxel once per 30 s
            if (fxFillSeen.Count >= 4096) fxFillSeen.Clear();
            fxFillSeen[key] = Time.realtimeSinceStartup;
            fxFillBlocked++;
            FxLog("level tool " + where + " at " + FxV(centre) + " held back: " + string.Join(", ", items.ToArray()));
        }

        static void FxAfterMatch(object __instance, ref bool __result)
        {
            SteamBridgePlugin plugin = discoveryInstance;
            try
            {
                if (!__result || plugin == null || plugin.fixLandscape == null || !plugin.fixLandscape.Value || __instance == null) return;
                object fill = FxMember(__instance, "IsFill");
                if (!(fill is bool) || !(bool)fill) return;
                UnityEngine.Object uo = __instance as UnityEngine.Object;
                int id = uo == null ? 0 : uo.GetInstanceID();
                float until; bool blocked;
                if (id != 0 && fxBlockedUntil.TryGetValue(id, out until) && Time.time < until && fxBlockedValue.TryGetValue(id, out blocked)) { if (blocked) __result = false; return; }
                object pos = FxMember(FxMember(__instance, "voxel"), "position");
                object x = FxMember(pos, "x"), y = FxMember(pos, "y"), z = FxMember(pos, "z");
                if (!(x is int) || !(y is int) || !(z is int)) return;
                Vector3 centre = new Vector3((int)x, (int)y, (int)z);
                List<string> items = FxItemsIn(centre);
                blocked = items.Count > 0;
                if (id != 0) { if (fxBlockedUntil.Count >= 4096) { fxBlockedUntil.Clear(); fxBlockedValue.Clear(); } fxBlockedUntil[id] = Time.time + 0.5f; fxBlockedValue[id] = blocked; }
                if (blocked) { __result = false; plugin.FxNoteBlocked(centre, items, "fill goal"); }
            }
            catch (Exception e) { if (plugin != null) plugin.FxLog("level goal check failed (goal unchanged): " + e.GetType().Name + " " + e.Message); }
        }

        // CallRpcAddTerrainResources(int x, int y, int z, ...): runs only on the server; skipping it means nobody fills the voxel
        static bool FxBeforeAdd(int x, int y, int z)
        {
            SteamBridgePlugin plugin = discoveryInstance;
            try
            {
                if (plugin == null || plugin.fixLandscape == null || !plugin.fixLandscape.Value) return true;
                Vector3 centre = new Vector3(x, y, z);
                List<string> items = FxItemsIn(centre);
                if (items.Count == 0) return true;
                plugin.FxNoteBlocked(centre, items, "terrain fill");
                return false;
            }
            catch (Exception e) { if (plugin != null) plugin.FxLog("terrain fill check failed (fill allowed): " + e.GetType().Name + " " + e.Message); return true; }
        }

        // ------------------------------------------------------------------ F8 panel

        void DrawFixes()
        {
            if (fixWardDrop == null || fixLandscape == null) return;
            GUILayout.Label("Виправлення гри: вард не в кристалі — " + (fixWardDrop.Value ? "увімк." : "вимк.") + " (виправлено " + fxWardMoved
                + "); вирівнювання не закопує предмети — " + (fixLandscape.Value ? "увімк." : "вимк.") + " (утримано клітинок " + fxFillBlocked + ").");
        }
    }
}
