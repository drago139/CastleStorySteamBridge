// 0.4.10 IN-GAME TEST BOT game layer: taken from the read-only research draft bot-research\blueprint-BotActions2.cs (side-session-notes of local_a8d26072, 2026-11-06),
// compiled with the plugin (signatures checked by csc against the real game DLLs). Called only by the bot (Bot.cs / BotActions.cs) while it is armed.
// blueprint-BotActions2.cs : research draft v2 for the in-game test bot, area 'blueprint' (2026-11-06). Supersedes blueprint-BotActions.cs
// (v2: hologram main pass index fixed for Two/OnePass, project info + AutoCommit read, colour of the held ghost).
// Compile check only: csc v4 /langversion:5 /nostdlib against the real Castle Story Managed DLLs. Not part of the mod, never loaded in the game.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Brix.Engine;                    // XYZ (Brix.Core), Voxel (firstpass)
using Brix.Engine.Nature;             // NatureRegistry
using Brix.External.Factories;        // Factory, Factory.AssetKey, FactoryImprint (Brix.Core); Blueprints, Pickers, Groupes (firstpass)
using Brix.Game;                      // User, Faction
using Brix.Game.AI;                   // BuildGoalProvider, Project, BuildProject, BuildGoal, Labor
using Brix.Game.Components;           // Blueprint, StateRenderer, BlueprintMaterialCache, BuildGoalMaterialProvider, ValidatorComponent, ValidationResult, IFitter, AffiliationComponent, BaseVoxelTransform, IsNullOrReleased()
using Brix.Game.Components.Volumes;   // IVolume, MovableVolume
using Brix.Game.Factories;            // BlueprintAssetKeyResolver
using Brix.Game.Network;              // UNetBlueprint, UNetGroupe, UNetProjectCmd
using Brix.Game.Rules;                // TaskRules, TaskType, TerrainRules
using Brix.Input;                     // VoxelRaycastHit, LayerBundle, Picker, Picking, GrabberComponentV2, Slave, InputModeController, InputMode
using Brix.Lifecycle.Pooling;         // ObjectPoolSingleton (Brix.Core)
using UnityEngine;
using UnityEngine.Networking;


namespace CastleStorySteam
{
internal static class BotBlueprint
{
    internal static readonly Factory.AssetKey SentinelBp = new Factory.AssetKey("Blueprints", "SentinelWard");   // -> ObjetsDynamiques.SentinelWard
    internal static readonly Factory.AssetKey OrangeBp = new Factory.AssetKey("Blueprints", "OrangeWard");       // -> ObjetsDynamiques.OrangeWard (bridge clone)

    // ---------------------------------------------------------------- preconditions
    internal static string WhyCannotBuild()
    {
        if (!NetworkClient.active) return "NetworkClient not active (CallCmd* logs 'called on server' and does nothing)";
        User u = User.LocalUser;
        if (u == null || u.faction == null) return "no local user / faction";
        if (u.GetComponent<UNetBlueprint>() == null || u.GetComponent<UNetGroupe>() == null) return "local user has no UNetBlueprint / UNetGroupe";
        try { if (TaskRules.Main != null && (TaskRules.Main.AllowedTasks & TaskType.Construction) == 0) return "TaskRules forbid Construction (Picking.OverrideAllowedByTaskRules)"; } catch (Exception) { }
        return null;
    }

    // ---------------------------------------------------------------- projects (Groupes/Construction = BuildProject + BuildGoalProvider on one GameObject)
    internal static List<BuildGoalProvider> OwnBuildProjects()
    {
        List<BuildGoalProvider> res = new List<BuildGoalProvider>();
        User u = User.LocalUser; Faction f = u == null ? null : u.faction;
        foreach (BuildGoalProvider p in UnityEngine.Object.FindObjectsOfType<BuildGoalProvider>())
            if (p != null && f != null && p.faction == f && p.GetComponent<BuildProject>() != null && !p.gameObject.IsNullOrReleased()) res.Add(p);
        return res;
    }

    internal static BuildGoalProvider SelectedOwnBuildProject()
    {
        Project p = UIGameObserver.projects == null ? null : UIGameObserver.projects.CurrentSelected;
        if (p == null || !(p is BuildProject) || User.LocalUser == null || p.faction != User.LocalUser.faction) return null;
        return p.GetComponent<BuildGoalProvider>();
    }

    internal static HashSet<int> SnapshotProjects()
    {
        HashSet<int> ids = new HashSet<int>();
        foreach (BuildGoalProvider p in OwnBuildProjects()) ids.Add(p.gameObject.GetInstanceID());
        return ids;
    }

    internal static string RequestNewProject()
    {
        if (!Project.ProjectUnderCap()) return "project cap reached (Project.projectCap = 16, the UI greys the button)";
        User.LocalUser.GetComponent<UNetGroupe>().CallCmdCreate();
        return null;
    }

    internal static BuildGoalProvider NewProjectSince(HashSet<int> before)
    {
        GameObject me = User.LocalUser == null ? null : User.LocalUser.gameObject;
        foreach (BuildGoalProvider p in OwnBuildProjects())
        {
            if (before.Contains(p.gameObject.GetInstanceID())) continue;
            AffiliationComponent a = p.GetComponent<AffiliationComponent>();
            if (a == null || a.UserCreator == null || a.UserCreator == me) return p;
        }
        return null;
    }

    static readonly FieldInfo bgpAutoCommit = typeof(BuildGoalProvider).GetField("AutoCommit", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static string ProjectInfo(BuildGoalProvider prov)
    {
        if (prov == null) return "none";
        Project p = prov.GetComponent<Project>();
        object ac = null; try { ac = bgpAutoCommit == null ? null : bgpAutoCommit.GetValue(prov); } catch (Exception) { }
        return prov.name + " id=" + prov.gameObject.GetInstanceID() + " blueprints=" + prov.TrackedBlueprints.Count + " goals(buildable)=" + prov.GoalCount()
            + " uncommitted=" + prov.HasUncommited() + " selected=" + prov.HasFocus + " autoCommit=" + (ac ?? "?")
            + (p == null ? "" : " status=" + p.Commited + " projCommitted=" + (p.State != null && p.State.Commited) + " crew=" + p.CrewCount() + " ready=" + p.ReadyToAssignGoals() + " autoDeleteOff=" + p.AutoDeleteDisabled);
    }

    // ---------------------------------------------------------------- placement (the picker's own maths)
    static LayerBundle PickerLayers()
    {
        LayerBundle lb = new LayerBundle();
        try
        {
            GameObject t = Factory.Peek(Pickers.BlueprintPlacement);
            Picker pk = t == null ? null : t.GetComponent<Picker>();
            if (pk != null && pk.ActiveLayers != null && pk.ActiveLayers.GetLayers() != 0) { lb.AddAsIs(pk.ActiveLayers.GetLayers()); return lb; }
        }
        catch (Exception) { }
        lb.Add(10, 17);   // Terrain + Nature, as BlueprintPMPicker.terrainLayer
        return lb;
    }

    // 0.4.12: the ground point under x / z with the placement picker's layers (harvest beacons)
    internal static bool GroundAt(float x, float z, out Vector3 p)
    {
        p = Vector3.zero; VoxelRaycastHit hit = new VoxelRaycastHit();
        hit.PlaceAtRaycast(new Ray(new Vector3(x, 300f, z), Vector3.down), 1000f, PickerLayers());
        if (!hit.HasHit) return false;
        p = hit.HitVector; return true;
    }

    // GrabberComponentV2.HoldKey + Slave(..) + BlueprintPlacementPicker.OnHoverAny, with a straight-down ray instead of the mouse ray.
    // sendKey = the key the picker would send (a Pylon/Lantern fitter can switch the ghost to a wall variant).
    internal static bool ComputePlacement(Factory.AssetKey key, float x, float z, int quarterTurns,
        out Factory.AssetKey sendKey, out Vector3 pos, out Quaternion rot, out ValidationResult result, out XYZ voxel, out string info)
    {
        sendKey = key; pos = Vector3.zero; rot = Quaternion.identity; result = ValidationResult.Invalid; voxel = XYZ.zero; info = null;
        VoxelRaycastHit hit = new VoxelRaycastHit();
        hit.PlaceAtRaycast(new Ray(new Vector3(x, 300f, z), Vector3.down), 1000f, PickerLayers());
        if (!hit.HasHit) { info = "ray hit nothing at " + x + "," + z; return false; }
        GameObject ghost;
        if (!ObjectPoolSingleton.Request(out ghost, key, hit.HitVector, Quaternion.identity) || ghost == null) { info = "no pool for " + key; return false; }
        try
        {
            User u = User.LocalUser;
            IVolume vol = ghost.GetComponent(typeof(IVolume)) as IVolume;
            if (vol != null && u != null) vol.Faction = u.faction;   // Slave(..) does this: own blueprints obstruct, other factions' do not
            IFitter fitter = ghost.GetComponent(typeof(IFitter)) as IFitter;
            if (fitter == null) { info = "no IFitter on " + key; return false; }
            GameObject tpl = Factory.Peek(key);
            IFitter tplFitter = tpl == null ? null : tpl.GetComponent(typeof(IFitter)) as IFitter;
            if (tplFitter != null) fitter.CloneInternals(tplFitter);   // reset a rotation left in the pooled object
            for (int i = 0; i < quarterTurns; i++) fitter.ChangeFit();  // = right click (cam_action2) while holding
            fitter.FitAndOrient(hit);
            ValidatorComponent vc = ghost.GetComponent<ValidatorComponent>();
            result = vc == null ? ValidationResult.Valid : vc.Validate();
            pos = ghost.transform.position; rot = ghost.transform.rotation;
            Blueprint gb = ghost.GetComponent<Blueprint>();
            if (gb != null) sendKey = gb.AssetKey;
            voxel = gb != null && gb.voxel != null ? gb.voxel.position : XYZ.FromVector3(pos);
            info = "hit=" + hit.VoxelPosition + " layer=" + hit.HitLayer + " normal=" + hit.VoxelNormal + " fitter=" + fitter.GetType().Name + " result=" + result
                + " voxel=" + voxel + " flags=" + (gb == null ? "?" : gb.State.ToString()) + " key=" + sendKey + " pos=" + pos.ToString("F2") + " yaw=" + rot.eulerAngles.y.ToString("F0");
            return result != ValidationResult.Obstructed && (vc == null || vc.CanPlaceInvalid || result != ValidationResult.Invalid);
        }
        finally { ObjectPoolSingleton.Release(ghost); }
    }

    // instant = test helper only (the host morphs it into the real object at once, no materials; a player's click always sends false)
    internal static void SendPlace(Factory.AssetKey key, Vector3 pos, Quaternion rot, BuildGoalProvider project, bool instant)
    {
        User.LocalUser.GetComponent<UNetBlueprint>().CallCmdSpawnPlacedBlueprint(key.Factory, key.Name, pos, rot, instant, project.gameObject, false);
    }

    internal static void SendCommit(BuildGoalProvider project)
    {
        User.LocalUser.GetComponent<UNetGroupe>().CallCmdCommit(project.gameObject);
    }

    internal static void SendAssign(Labor l, BuildGoalProvider project)
    {
        User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdChangeProject(l.gameObject, project.gameObject);
    }

    internal static string SendDemolish(GameObject target, BuildGoalProvider project, bool instant = false)   // 0.4.18: instant = HOST TEST HELPER (ungrid)
    {
        FactoryImprint imprint = target == null ? null : target.GetComponentInParent<FactoryImprint>();
        if (imprint == null) return "target has no FactoryImprint";
        Factory.AssetKey key = BlueprintAssetKeyResolver.Resolve(imprint.AssetKey);
        if (key == null) return "no Blueprints template has BlueprintResult == " + imprint.AssetKey;
        if (project == null || project.gameObject.IsNullOrReleased()) return "no own build project (a null project makes CmdPlaceAnti throw on the host after the anti is spawned)";
        if (InputModeController.DefaultMode == InputMode.worldEditor) return "world editor: the remover places no anti";
        Vector3 p = imprint.transform.position; Quaternion r = imprint.transform.rotation;
        GameObject tpl = Factory.Peek(key);
        BaseVoxelTransform vt = tpl == null ? null : tpl.GetComponent<BaseVoxelTransform>();
        XYZ cell = XYZ.FromVector3(p + r * (vt == null ? Vector3.zero : vt.CenterOffset));
        if (NatureRegistry.HasNatureBlockerAt(cell)) return "nature blocker at " + cell;
        if (!(Voxel.IsEmpty(cell) || TerrainRules.Main.CanDigAt(cell.ToVector3()))) return "cannot dig at " + cell + " (SetDiggingFlags clears 'valid')";
        User.LocalUser.GetComponent<UNetBlueprint>().CallCmdPlaceAnti(key.Factory, key.Name, p, r, instant, project.gameObject);
        return null;
    }

    internal static void SendCancel(Blueprint bp)
    {
        User.LocalUser.GetComponent<UNetBlueprint>().CallCmdDestroyBluePrint(bp.gameObject);
    }

    // ---------------------------------------------------------------- state reads
    internal static List<Blueprint> OwnBlueprints(bool includeUnplaced)
    {
        List<Blueprint> res = new List<Blueprint>();
        User u = User.LocalUser; Faction f = u == null ? null : u.faction;
        foreach (Blueprint b in UnityEngine.Object.FindObjectsOfType<Blueprint>())
        {
            if (b == null || b.IsNullOrReleased()) continue;
            if (f != null && b.faction != f) continue;
            if (!includeUnplaced && !b.isPlaced) continue;   // the picker's ghost is never 'placed'
            res.Add(b);
        }
        return res;
    }

    internal static Blueprint BlueprintNear(Vector3 at, Factory.AssetKey key, bool anti, float radius)
    {
        Blueprint best = null; float bestD = radius;
        foreach (Blueprint b in OwnBlueprints(false))
        {
            if (b.IsAnti != anti || (key != null && b.AssetKey != key)) continue;
            float d = Vector3.Distance(b.transform.position, at);
            if (d <= bestD) { best = b; bestD = d; }
        }
        return best;
    }

    internal static string Phase(Blueprint b)
    {
        if (b == null || b.IsNullOrReleased()) return "gone";
        PlacementFlags s = b.State;
        if ((s & PlacementFlags.placed) == 0) return "unplaced";
        string c = b.commited ? "committed" : "uncommitted";
        if (b.IsAnti) return ((s & PlacementFlags.buildable) != 0 ? "anti-ready" : "anti-waiting") + "/" + c;
        string fill = b.recepteur == null ? "no-recepteur" : b.recepteur.IsFull() ? "full" : b.recepteur.IsEmpty() ? "empty" : "partial(" + b.recepteur.StoredItems.Count + ")";
        string v = (s & PlacementFlags.valid) == 0 ? "invalid" : (s & PlacementFlags.buildable) == 0 ? "valid-not-buildable" : "buildable";
        return v + "/" + c + "/" + fill;
    }

    internal static string Describe(Blueprint b)
    {
        StringBuilder sb = new StringBuilder();
        Project p = b.Project;
        sb.Append(b.AssetKey).Append(" -> ").Append(b.BlueprintResult)
          .Append(" pos=").Append(b.transform.position.ToString("F2")).Append(" voxel=").Append(b.voxel == null ? "?" : b.voxel.position.ToString())
          .Append(" flags=").Append(b.State).Append(" phase=").Append(Phase(b));
        sb.Append(" project=").Append(p == null ? "none" : p.name + "#" + p.gameObject.GetInstanceID());
        ValidatorComponent vc = b.GetComponent<ValidatorComponent>();
        if (vc != null) sb.Append(" lastValidation=").Append(vc.ValidateCache);
        BuildGoal g = b.GetComponent<BuildGoal>();
        if (g != null) sb.Append(" goalCommited=").Append(g.Commited).Append(" goalEnabled=").Append(g.enabled);
        return sb.ToString();
    }

    internal static WardController WardNear(Vector3 at, Factory.AssetKey resultKey, float radius)
    {
        WardController best = null; float bestD = radius;
        foreach (WardController w in UnityEngine.Object.FindObjectsOfType<WardController>())
        {
            if (w == null || w.gameObject.IsNullOrReleased()) continue;
            FactoryImprint fi = w.GetComponent<FactoryImprint>();
            if (resultKey != null && (fi == null || fi.AssetKey != resultKey)) continue;
            float d = Vector3.Distance(w.transform.position, at);
            if (d <= bestD) { best = w; bestD = d; }
        }
        return best;
    }

    internal static string WardInfo(WardController w)
    {
        if (w == null) return "none";
        FactoryImprint fi = w.GetComponent<FactoryImprint>();
        IVolume v = w.GetComponent(typeof(IVolume)) as IVolume;
        return (fi == null ? "?" : fi.AssetKey.ToString()) + " pos=" + w.transform.position.ToString("F2") + " faction=" + (w.faction == null ? "none" : w.faction.name)
            + " orangeTag=" + (w.GetComponent<OrangeWardTag>() != null) + " volume=" + (v == null ? "none" : v.GetType().Name + (v.Registered ? " registered" : " unregistered"));
    }

    // ---------------------------------------------------------------- hologram colour (local visuals, host and guest alike)
    static readonly FieldInfo srRenderers = typeof(StateRenderer).GetField("_renderers", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo bgpMat = typeof(BuildGoalProvider).GetField("materialProvider", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo bgmFocused = typeof(BuildGoalMaterialProvider).GetField("focused", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static string HueName(Color c)
    {
        float h, s, v; Color.RGBToHSV(c, out h, out s, out v);
        if (s < 0.15f) return v > 0.8f ? "white" : "grey";
        float d = h * 360f;
        if (d < 15f || d >= 340f) return "red"; if (d < 45f) return "orange"; if (d < 70f) return "yellow";
        if (d < 170f) return "green"; if (d < 260f) return "blue"; return "purple";
    }

    // ThreePass = [prepass, main, underground]; TwoPass = [main, underground]; OnePass = [main] (BlueprintMaterialCache.MaterialTrio.WithPasses)
    internal static Material MainPass(Material[] ms)
    {
        if (ms == null || ms.Length == 0) return null;
        return ms.Length >= 3 ? ms[1] : ms[0];
    }

    internal static List<MeshRenderer> HologramRenderers(Blueprint b)
    {
        StateRenderer sr = b == null ? null : b.GetComponent<StateRenderer>();
        List<MeshRenderer> list = null;
        try { list = srRenderers == null || sr == null ? null : srRenderers.GetValue(sr) as List<MeshRenderer>; } catch (Exception) { }
        if (list == null && b != null) list = new List<MeshRenderer>(b.GetComponentsInChildren<MeshRenderer>(true));
        return list ?? new List<MeshRenderer>();
    }

    // the colour actually assigned to the hologram right now: _Color of the main pass of the first renderer (the only colour property of the
    // "Shader Forge/ScreenSpaceTiled*" hologram shaders)
    internal static Color HologramColor(Blueprint b, out string materialName)
    {
        materialName = null;
        foreach (MeshRenderer r in HologramRenderers(b))
        {
            if (r == null) continue;
            Material m = MainPass(r.sharedMaterials);
            if (m == null || !m.HasProperty("_Color")) continue;
            materialName = m.name;
            return m.GetColor("_Color");
        }
        return Color.clear;
    }

    internal static string Hologram(Blueprint b)
    {
        StringBuilder sb = new StringBuilder();
        BlueprintMaterialCache.BlueprintDisplay d = null;
        try { d = BlueprintMaterialCache.ForMode(b.State); } catch (Exception e) { sb.Append("ForMode failed ").Append(e.GetType().Name).Append(' '); }
        if (d != null) sb.Append("display=").Append(d.ToString()).Append(" displayColor=").Append(HueName(d.color)).Append(d.color).Append(" displayScale=").Append(d.Scale.ToString("F2"));
        sb.Append(" localScale=").Append(b.transform.localScale.x.ToString("F3"));
        StateRenderer sr = b.GetComponent<StateRenderer>();
        sb.Append(" visible=").Append(sr != null && sr.IsVisible);
        Project p = b.Project; BuildGoalProvider prov = p == null ? null : p.GetComponent<BuildGoalProvider>();
        if (prov != null)
        {
            bool focused = false;
            try { object mp = bgpMat == null ? null : bgpMat.GetValue(prov); if (mp != null && bgmFocused != null) focused = (bool)bgmFocused.GetValue(mp); } catch (Exception) { }
            sb.Append(" materialFocus=").Append(focused).Append(" projectSelected=").Append(prov.HasFocus);
        }
        else sb.Append(" provider=none(global BlueprintMaterialCache)");
        foreach (MeshRenderer r in HologramRenderers(b))
        {
            if (r == null) continue;
            Material[] ms = r.sharedMaterials;
            Material main = MainPass(ms);
            sb.Append(" | ").Append(r.name).Append(" enabled=").Append(r.enabled).Append(" layer=").Append(r.gameObject.layer).Append(" mats=").Append(ms.Length);
            if (main == null) continue;
            sb.Append(" main=").Append(main.name).Append(" shader=").Append(main.shader == null ? "?" : main.shader.name);
            if (main.HasProperty("_Color")) { Color c = main.GetColor("_Color"); sb.Append(" _Color=").Append(HueName(c)).Append(c); }
            if (main.HasProperty("_FillOpacity")) sb.Append(" _FillOpacity=").Append(main.GetFloat("_FillOpacity").ToString("F2"));
            sb.Append(" hasOverlay=").Append(main.HasProperty("_OverlayColor")).Append(" hasRim=").Append(main.HasProperty("_RimColor"));
        }
        return sb.ToString();
    }

    internal static Blueprint HeldGhost()
    {
        Picking pk = Picking.Instance;
        Picker over = pk == null ? null : pk.OverridePicker;
        GrabberComponentV2 g = over == null ? null : over.CurrentPickerComponent as GrabberComponentV2;
        Slave s = g == null ? null : g.GetSlave();
        return s == null || s.HeldObject == null ? null : s.HeldObject.GetComponent<Blueprint>();
    }
}

}
