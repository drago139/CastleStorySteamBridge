using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Brix.Engine;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Components.Tracking;
using Brix.Game.Components.Wards;
using Brix.UI.Icons;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;

// 0.3.99 ORANGE WARD as a real, separate build item (not a mode of the red Sentinel ward).
// The game builds items from templates: every Factory holds template GameObjects (FactoryTemplate) and Factory.AddAsset<CloneTemplate>(go) registers a new one
// (the game itself does this in LuaFactory.CreateItemFrom). The network id of a template is MD5(factory + name) (Factory.AssetKey.AssetId), the same on every PC,
// and Brix.Network.FactoryRegistrator.RegisterFactory(f) registers the UNet spawn handler of every template of a factory. So, right before the game registers a
// factory, this class makes sure two new templates exist (on every PC that runs the bridge, host and guest):
//   Blueprints/OrangeWard   a clone of Blueprints/SentinelWard whose Blueprint.BlueprintResult points at the new ward object
//   <ward factory>/OrangeWard   a clone of the real Sentinel ward object: orange gem and light, tracks every relation and layer, range Fixes/OrangeWardRange (60),
//                               and an OrangeWardTag marker. WardAttack.CanFireAt is filtered to barrels ONLY for wards that carry the marker; the red ward is never touched.
// The menu entry is a plain Meta_Structure.lua line with the real key Blueprints/OrangeWard (see GameLua\ in the version folder) and an icon made from the
// Sentinel icon by recolouring its red pixels (registered with IconDB.AddIcon, fetched in Lua with SB_OrangeIconKey():Get64()).
public sealed class OrangeWardTag : MonoBehaviour { }

internal static class OrangeWardItem
{
    internal static Func<float> getRange;
    static readonly Factory.AssetKey SentinelBp = new Factory.AssetKey("Blueprints", "SentinelWard");
    static readonly Factory.AssetKey OrangeBp = new Factory.AssetKey("Blueprints", "OrangeWard");
    static FieldInfo relF, layF;
    static IconKey orangeIcon; static bool iconFailed;
    static int logs;
    static readonly Color Orange = new Color(1f, 0.42f, 0.02f, 1f);
    // 0.3.105: the game builds its object pools and volume profiles BEFORE the two templates exist (see EnsurePools / CopyVolumeProfiles)
    internal static Factory.AssetKey orangeResult;
    static MethodInfo addPoolM;
    static int poolLogs;
    static bool poolsOk, volumesDone;
    // menu icon: the 57x83 art is drawn about 1.5x bigger than the stock ward icons (screenshot measurement 2026-11-03: stock bbox 23x35-38, orange 36x54-60), so the art is shrunk inside the same canvas
    const float IconScale = 0.65f;

    static void Log(string text) { try { CastleStorySteam.SteamBridgePlugin.PtLog("ORANGE-WARD " + text); } catch (Exception) { } }
    // 0.4.3: the lines of the pack carry the RECIPE tag, so the 2-minute report (dlTags) and the friend report (ssKeep) keep them
    static void LogR(string text) { try { CastleStorySteam.SteamBridgePlugin.PtLog("ORANGE-WARD RECIPE " + text); } catch (Exception) { } }

    internal static void Install(Harmony harmony, Func<float> range)
    {
        getRange = range;
        relF = AccessTools.Field(typeof(TrackingController), "trackedRelations");
        layF = AccessTools.Field(typeof(TrackingController), "trackedLayers");
        try
        {
            MethodInfo reg = AccessTools.Method(typeof(Brix.Network.FactoryRegistrator), "RegisterFactory", new Type[] { typeof(Factory) });
            if (reg != null) harmony.Patch(reg, new HarmonyMethod(AccessTools.Method(typeof(OrangeWardItem), "BeforeRegisterFactory")), null, null, null, null);
            MethodInfo reg2 = AccessTools.Method(typeof(Brix.External.Factories.FactoryRegistrator), "RegisterFactory", new Type[] { typeof(Factory) });
            if (reg2 != null) harmony.Patch(reg2, new HarmonyMethod(AccessTools.Method(typeof(OrangeWardItem), "BeforeRegisterFactory")), null, null, null, null);
        }
        catch (Exception e) { Log("hook failed: " + e.Message); }
        try
        {
            MethodInfo can = AccessTools.Method(typeof(WardAttack), "CanFireAt");
            if (can != null) harmony.Patch(can, null, new HarmonyMethod(AccessTools.Method(typeof(OrangeWardItem), "AfterCanFire")), null, null, null);
        }
        catch (Exception e) { Log("CanFireAt hook failed: " + e.Message); }
        try
        {
            MethodInfo upd = AccessTools.Method(typeof(StateRenderer), "UpdateRenderersMaterial");
            if (upd != null) harmony.Patch(upd, null, new HarmonyMethod(AccessTools.Method(typeof(OrangeWardItem), "AfterUpdateMaterials")), null, null, null);
            else Log("StateRenderer.UpdateRenderersMaterial not found: the ghost stays blue");
        }
        catch (Exception e) { Log("ghost hook failed: " + e.Message); }
        try
        {
            HarmonyMethod post = new HarmonyMethod(AccessTools.Method(typeof(OrangeWardItem), "AfterScript"));
            foreach (ConstructorInfo c in typeof(Script).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) harmony.Patch(c, null, post, null, null, null);
        }
        catch (Exception e) { Log("Lua hook failed: " + e.Message); }
        try { OrangeWardRecipe.Install(harmony); }
        catch (Exception e) { Log("recipe hooks failed: " + e.Message); }
        // 0.4.3 pack: square small icon, orange models, hotkey of slot 6 (each part behind its own config switch, see OrangeWardExtras)
        try { OrangeWardExtras.Install(harmony); }
        catch (Exception e) { Log("pack 0.4.3 hooks failed: " + e.Message); }
    }

    internal static void Clear() { getRange = null; }

    // ---- registration of the two templates (idempotent; runs before the game registers a factory with UNet)
    static void BeforeRegisterFactory()
    {
        try { EnsureRegistered(); } catch (Exception e) { if (logs++ < 5) Log("register failed: " + e.GetType().Name + " " + e.Message); }
        try { EnsurePools(); } catch (Exception e) { if (poolLogs++ < 5) Log("pool step failed: " + e.GetType().Name + " " + e.Message); }
        // 0.4.6: the orange model a worker carries (Accessories/HeldOrangeWard): a tinted copy of the red held model, registered before the game registers the Accessories factory with UNet
        try { OrangeWardHeld.EnsureClone(); } catch (Exception e) { if (logs++ < 5) Log("held clone step failed: " + e.GetType().Name + " " + e.Message); }
        try { OrangeWardHeld.EnsurePool(); } catch (Exception e) { if (poolLogs++ < 5) Log("held pool step failed: " + e.GetType().Name + " " + e.Message); }
    }

    static void EnsureRegistered()
    {
        if (!Factory.Exist(SentinelBp) || Factory.Exist(OrangeBp)) return;
        GameObject template = Factory.Peek(SentinelBp);
        Blueprint bp = template == null ? null : template.GetComponent<Blueprint>();
        if (bp == null) { Log("SentinelWard blueprint has no Blueprint component"); return; }
        Factory.AssetKey resultKey = bp.BlueprintResult;
        if (resultKey == null || !Factory.Exist(resultKey)) { Log("SentinelWard result " + resultKey + " is not in a factory yet"); return; }
        Factory resultFactory = Factory.GetFactory(resultKey);
        Factory blueprintFactory = Factory.GetFactory(SentinelBp);

        // 1. the ward object
        GameObject ward = Factory.Instantiate(resultKey, false);
        ward.name = "OrangeWard";
        FactoryImprint wardImprint = ward.GetComponent<FactoryImprint>();
        if (wardImprint != null) wardImprint.AssetKey = new Factory.AssetKey(resultFactory.name, "OrangeWard");
        Customize(ward);
        Factory.AssetKey newResult = resultFactory.AddAsset<CloneTemplate>(ward);
        orangeResult = newResult;

        // 2. the blueprint that builds it
        GameObject bpClone = Factory.Instantiate(SentinelBp, false);
        bpClone.name = "OrangeWard";
        FactoryImprint bpImprint = bpClone.GetComponent<FactoryImprint>();
        if (bpImprint != null) bpImprint.AssetKey = OrangeBp;
        Blueprint nb = bpClone.GetComponent<Blueprint>();
        if (nb != null) nb.BlueprintResult = newResult;
        // 0.4.1: own craftable item (Stick identity, registries, Altar recipe); on any failure the clones stay red-identity exactly as in 0.3.108
        bool identity = false;
        try { identity = OrangeWardRecipe.Prepare(newResult) && OrangeWardRecipe.Retarget(ward, bpClone); }
        catch (Exception e) { Log("recipe identity failed: " + e.GetType().Name + " " + e.Message); }
        blueprintFactory.AddAsset<CloneTemplate>(bpClone);
        Log("registered " + OrangeBp + " -> " + newResult + " (range " + RangeValue().ToString("F0") + ", from " + SentinelBp + " -> " + resultKey + ")");
        try { OrangeWardRecipe.Complete(newResult, OrangeBp, identity); }
        catch (Exception e) { Log("recipe completion failed: " + e.GetType().Name + " " + e.Message); }
        CopyVolumeProfiles(resultKey, newResult);
    }

    // ---- 0.3.105 object pools. ObjectPoolSingleton.GeneratePools() builds IndexedPools once (first listener of FactoryLoader.FactoriesReadyEvent, one CloneTemplate = one pool); RegisterFactories
    // (the hook point of this class) is the second listener and Factory.AddAsset never makes a pool. So the new keys had none: ObjectPoolSingleton.Request logged "No GameObject Pool were found for
    // AssetKey Blueprints.OrangeWard" and returned null, GrabberComponentV2.HoldKey passed null to ObjectPlacementPicker.SetHeldObject and threw a NullReferenceException every frame (no hologram,
    // nothing placed; brix.log 17:38 run, 7257 errors, only this key). Both keys need a pool: the blueprint (hologram, placement) and the ward object (build result, UNet spawn on every PC).
    // AddPoolForKey (internal, no callers in the game) is the Dummy-pool branch of GeneratePools, which is what every template without a PoolConfiguration gets (neither Sentinel template has one).
    static void EnsurePools()
    {
        if (poolsOk || orangeResult == null || !Factory.Exist(OrangeBp) || !Factory.Exist(orangeResult)) return;
        Brix.Lifecycle.Pooling.IObjectPoolSingleton inst = Brix.Lifecycle.Pooling.ObjectPoolSingleton.Instance;
        // an empty table = the game has not run GeneratePools yet (or the null singleton): its own loop will still find the templates, and a pool added before it would make its IndexedPools.Add throw
        if (inst == null || inst.IndexedPools == null || inst.IndexedPools.Count == 0) return;
        Factory.AssetKey[] keys = new Factory.AssetKey[] { OrangeBp, orangeResult };
        bool all = true;
        for (int i = 0; i < keys.Length; i++)
        {
            if (inst.IndexedPools.ContainsKey(keys[i])) continue;   // Dictionary.Add throws on a duplicate
            if (!AddPool(inst, keys[i])) all = false;
        }
        if (!all) return;
        poolsOk = true;
        Log("pools ready: " + OrangeBp + "=" + inst.IndexedPools.ContainsKey(OrangeBp) + " " + orangeResult + "=" + inst.IndexedPools.ContainsKey(orangeResult) + " total=" + inst.IndexedPools.Count);
    }

    internal static bool AddPool(Brix.Lifecycle.Pooling.IObjectPoolSingleton inst, Factory.AssetKey key)
    {
        try
        {
            Brix.Lifecycle.Pooling.ObjectPoolSingleton real = inst as Brix.Lifecycle.Pooling.ObjectPoolSingleton;
            if (real != null)
            {
                if (addPoolM == null) addPoolM = AccessTools.Method(typeof(Brix.Lifecycle.Pooling.ObjectPoolSingleton), "AddPoolForKey", new Type[] { typeof(Factory.AssetKey) });
                if (addPoolM != null) { addPoolM.Invoke(real, new object[] { key }); return inst.IndexedPools.ContainsKey(key); }
            }
            // fallback: the public pieces of the same path (the game's CreateDummyObjectPool is private)
            Component c = inst as Component;
            if (c == null) { if (poolLogs++ < 5) Log("add pool " + key + ": no AddPoolForKey and the singleton is not a Component"); return false; }
            inst.IndexedPools.Add(key, new Brix.Lifecycle.Pooling.DummyGameObjectPool(key, c.transform));
            return true;
        }
        catch (Exception e)
        {
            if (poolLogs++ < 5) Log("add pool " + key + " failed: " + e.GetType().Name + " " + (e.InnerException != null ? e.InnerException.Message : e.Message));
            return false;
        }
    }

    // ---- 0.3.105 volume profiles. The cells of a template (access for workers, support, obstruction, per AssetKey) are copied by the game in FactoryLoader's post-process pass, before the clones exist;
    // an unknown key silently gets an EMPTY profile (always buildable, no footprint, no access cells for workers). Same public calls the game uses for derived assets
    // (BlueprintVolumeFactoryPostProcessor, PlacedBlockFactoryAssetCreator.ConfigureVolumeData): blueprint = config 0 (full) + config 1 (anti), ward object = config 0.
    static int Cells(Factory.AssetKey key, int config)
    {
        try { return Brix.Game.Factories.VolumePreprocessor.GetIndexedVSettings(key, 0, config).Count; } catch (Exception) { return -1; }
    }

    static void CopyVolumeProfiles(Factory.AssetKey resultKey, Factory.AssetKey newResult)
    {
        if (volumesDone) return;
        volumesDone = true;   // CreatePreprocessedVolumeSubEntry appends one more config on every call: never run this twice
        try
        {
            if (Brix.Game.Factories.VolumePreprocessor._instance == null) { Log("volume copy skipped: VolumePreprocessor not ready"); return; }
            int redBp = Cells(SentinelBp, 0), redAnti = Cells(SentinelBp, 1), redObj = Cells(resultKey, 0);
            if (redBp > 0)
            {
                Brix.Game.Factories.VolumePreprocessor.CreatePreprocessedVolume(OrangeBp, SentinelBp, 0);
                if (redAnti >= 0) Brix.Game.Factories.VolumePreprocessor.CreatePreprocessedVolumeSubEntry(OrangeBp, SentinelBp, 1);
            }
            if (redObj > 0) Brix.Game.Factories.VolumePreprocessor.CreatePreprocessedVolume(newResult, resultKey, 0);
            Log("volume cells: blueprint " + Cells(OrangeBp, 0) + "/" + Cells(OrangeBp, 1) + " (red " + redBp + "/" + redAnti + "), ward " + Cells(newResult, 0) + " (red " + redObj + ")");
        }
        catch (Exception e) { Log("volume copy failed: " + e.GetType().Name + " " + e.Message); }
    }

    static float RangeValue() { float r = getRange == null ? 60f : getRange(); return r < 1f ? 60f : r; }

    // 0.3.106: the stock ward waits WakeUpDelay = 3 s (+-0.25) after it picks a target before its first shot (it "wakes up"). A barrel in flight is gone by then, so the orange ward wakes up fast.
    const float OrangeWakeUp = 0.4f;

    static void Customize(GameObject ward)
    {
        ward.AddComponent<OrangeWardTag>();
        WardAttack wa = ward.GetComponentInChildren<WardAttack>(true);
        if (wa != null) { Log("ward wake-up delay " + wa.WakeUpDelay.ToString("F2") + " s -> " + OrangeWakeUp.ToString("F2") + " s"); wa.WakeUpDelay = OrangeWakeUp; }
        // 0.4.24 (user: "the cooldown should be longer, 1 ward counters 2 catapults"): WardAttack fires when Time.fixedTime > lastAttack + 1 / AttacksPerSecond, so dividing AttacksPerSecond by the multiplier
        // lengthens the time between two shots by that factor. The host fires, so only the host's value matters. 1 = the stock rate.
        float cdMul = CooldownMulValue();
        if (wa != null) { Log("ward fire rate " + wa.AttacksPerSecond.ToString("F2") + "/s (interval " + (wa.AttacksPerSecond > 0f ? (1f / wa.AttacksPerSecond).ToString("F2") : "n/a") + " s), heat/shot " + wa.HeatPerShot.ToString("F3") + ", cooling " + wa.CooldownPerSecond.ToString("F2") + "/s; cooldown multiplier " + cdMul.ToString("F2")); if (cdMul != 1f && wa.AttacksPerSecond > 0f) wa.AttacksPerSecond = wa.AttacksPerSecond / cdMul; }
        WardController wc = ward.GetComponent<WardController>();
        if (wc != null)
        {
            if (relF != null) relF.SetValue(wc, Affiliation.Relation.All);
            if (layF != null) layF.SetValue(wc, TrackingLayer.All);
            wc.EffectRange = RangeValue();
        }
        foreach (Renderer r in ward.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r.name != "Gem" || r.sharedMaterial == null) continue;
            Material copy = new Material(r.sharedMaterial); copy.name = "OrangeWard_Gem";
            if (copy.HasProperty("_Color")) copy.SetColor("_Color", Orange);
            if (copy.HasProperty("_EmissionColor")) copy.SetColor("_EmissionColor", Orange);
            r.sharedMaterial = copy;
        }
        foreach (Light l in ward.GetComponentsInChildren<Light>(true)) if (l != null) l.color = Orange;
    }

    // ---- targets: only barrels IN FLIGHT, only for wards that carry the marker
    internal static bool IsBarrel(GameObject unit)
    {
        if (unit == null) return false;
        ExplodeOnDeath e = unit.GetComponent<ExplodeOnDeath>(); if (e == null) e = unit.GetComponentInParent<ExplodeOnDeath>();
        if (e == null) return false;
        string n = e.gameObject.name; if (n == null) return false; n = n.ToLowerInvariant();
        return n.Contains("baril") || n.Contains("barrel");
    }

    // 0.3.106 (user: "remove targets that do not fly": a barrel on the ground or loaded in a catapult must not be shot). A barrel is in flight once a catapult has launched it:
    // CatapultBody.RpcExit_Firing (server only) adds CatapultProjectile to the launched ammo and the barrel keeps it until it explodes on impact; a barrel waiting in a catapult carries
    // CatapultAmmoComponent instead (and no live rigidbody), a barrel on the ground or in a worker's hands has neither. A sleeping rigidbody means it lies still. Anything else that is airborne
    // (a barrel falling off a ledge: BarilScript.ChuteLibre adds a rigidbody; one thrown by a blast) counts too when its rigidbody is awake, free and moving.
    internal static bool IsFlying(GameObject unit)
    {
        if (unit == null) return false;
        ExplodeOnDeath e = unit.GetComponent<ExplodeOnDeath>(); if (e == null) e = unit.GetComponentInParent<ExplodeOnDeath>();
        GameObject barrel = e != null ? e.gameObject : unit;
        Rigidbody rb = barrel.GetComponent<Rigidbody>();
        if (barrel.GetComponent<Brix.Game.Components.CatapultSpace.CatapultProjectile>() != null) return rb == null || !rb.IsSleeping();
        if (barrel.GetComponent<CatapultAmmoComponent>() != null) return false;
        return rb != null && !rb.isKinematic && !rb.IsSleeping() && rb.velocity.sqrMagnitude > 4f;
    }

    // what the decision was based on (log only)
    static string FlyInfo(GameObject unit)
    {
        ExplodeOnDeath e = unit.GetComponent<ExplodeOnDeath>(); if (e == null) e = unit.GetComponentInParent<ExplodeOnDeath>();
        GameObject barrel = e != null ? e.gameObject : unit;
        Rigidbody rb = barrel.GetComponent<Rigidbody>();
        return " launched=" + (barrel.GetComponent<Brix.Game.Components.CatapultSpace.CatapultProjectile>() != null) + " inCatapult=" + (barrel.GetComponent<CatapultAmmoComponent>() != null)
            + " body=" + (rb == null ? "none" : rb.isKinematic ? "kinematic" : rb.IsSleeping() ? "asleep" : "v" + rb.velocity.magnitude.ToString("F1"));
    }

    static int flyLogs, groundLogs, skipLogs;

    // ---- 0.4.16 team filter (user 2026-11-07: "the orange wards must see which team launches the barrel, now they hit all"). Before this the ward took every barrel in flight,
    // its own side's catapult shots included. The launching catapult is known: CatapultBody.RpcExit_Firing sets CatapultProjectile.sourceCatapult (server). The ward skips the barrel when the
    // game's own relation between the ward's faction and the catapult's faction is Allied (the same faction is allied: Affiliation.GetRelation(a, b) returns Allied for a == b). Unknown origin
    // (no catapult: a barrel that fell off a ledge, a blast, chaos strikes), a missing faction, enemy / neutral / undefined relation = the ward shoots as before. Decision: OrangeWardPatchRules.SkipAlliedBarrel.
    internal static Func<bool> SkipAlliedOn;   // [Fixes] OrangeWardSkipAllied (default true), read on the host where the ward decides

    internal static Func<float> CooldownMul;   // [Fixes] OrangeWardCooldownMul (default 2): time between two shots = stock interval x this
    static float CooldownMulValue() { float m = CooldownMul == null ? 2f : CooldownMul(); return m < 0.1f || m > 20f ? 2f : m; }

    internal static void BindConfig(BepInEx.Configuration.ConfigFile config)
    {
        try
        {
            BepInEx.Configuration.ConfigEntry<float> cd = config.Bind("Fixes", "OrangeWardCooldownMul", 2f, "0.4.24: the orange ward waits this many times longer between two shots than the stock ward (2 = half the fire rate; 1 = the stock rate). The host fires, so it matters on the host. Takes effect at the next game start.");
            CooldownMul = delegate { return cd.Value; };
            BepInEx.Configuration.ConfigEntry<int> cost = config.Bind("Fixes", "OrangeWardCrystalCost", OrangeWardRecipe.DefaultCrystalCost, "0.4.24: orange crystal (sulphur) the orange ward costs at the Altar (the stock red ward: 40; 1..400). Every player should use the same value. Takes effect at the next game start.");
            OrangeWardRecipe.CostGetter = delegate { return cost.Value; };
            BepInEx.Configuration.ConfigEntry<bool> altarCap = config.Bind("Fixes", "OrangeWardAltarCapacity", true, "0.4.24: the Altar holds as many orange crystals as the orange ward costs when that is above the stock 40 (the stock Altar fetches at most 40 of one resource and would wait for ever for an 80-crystal recipe). false = the stock cap: set OrangeWardCrystalCost to 40 or lower then. Every player should use the same value.");
            OrangeWardRecipe.AltarCapOn = delegate { return altarCap.Value; };
        }
        catch (Exception e) { Log("cooldown/cost config bind failed (" + e.GetType().Name + " " + e.Message + "): defaults apply"); }
        try
        {
            BepInEx.Configuration.ConfigEntry<bool> skip = config.Bind("Fixes", "OrangeWardSkipAllied", CastleStorySteam.OrangeWardPatchRules.SkipAlliedDefault, "0.4.16: an orange ward does not shoot a barrel launched by a catapult of its own side (the game's Allied relation; the same faction is allied). Barrels of enemy or neutral catapults and barrels with no catapult (falling, blast, chaos strikes) are shot as before. false = the ward shoots every barrel in flight (0.4.15). The host decides, so it matters on the host.");
            SkipAlliedOn = delegate { return skip.Value; };
        }
        catch (Exception e) { Log("config bind failed (" + e.GetType().Name + " " + e.Message + "): the ward shoots every barrel in flight"); }
    }

    // the faction of a thing (its own AffiliationComponent or its parent's); null when it has none or only the default neutral one
    static Faction FactionOf(GameObject go)
    {
        if (go == null) return null;
        AffiliationComponent a = go.GetComponentInParent<AffiliationComponent>();
        if (a == null) return null;
        Faction f = a.faction;
        return (f == null || f == Faction.DefaultNeutral) ? null : f;
    }

    // true when the launching catapult and both factions are known; relation = (int)Affiliation.Relation between the ward and that catapult. src / team = log text.
    static bool LaunchRelation(WardAttack ward, GameObject unit, out int relation, out string info)
    {
        relation = 0; info = "src=none";
        try
        {
            ExplodeOnDeath e = unit.GetComponent<ExplodeOnDeath>(); if (e == null) e = unit.GetComponentInParent<ExplodeOnDeath>();
            GameObject barrel = e != null ? e.gameObject : unit;
            Brix.Game.Components.CatapultSpace.CatapultProjectile cp = barrel.GetComponent<Brix.Game.Components.CatapultSpace.CatapultProjectile>();
            if (cp == null) return false;
            Component src = cp.sourceCatapult;
            if (src == null) { info = "src=destroyed"; return false; }
            WardController wc = ward.GetComponentInParent<WardController>();
            GameObject wardGo = wc != null ? wc.gameObject : ward.gameObject;
            Faction fw = FactionOf(wardGo), fs = FactionOf(src.gameObject);
            info = "src=" + src.gameObject.name + " srcTeam=" + (fs == null ? "none" : fs.gameObject.name) + " wardTeam=" + (fw == null ? "none" : fw.gameObject.name);
            if (fw == null || fs == null) return false;
            relation = (int)Affiliation.GetRelation(fw, fs);
            return true;
        }
        catch (Exception) { return false; }
    }

    static void AfterCanFire(WardAttack __instance, GameObject unit, ref bool __result)
    {
        try
        {
            if (__instance == null || __instance.GetComponent<OrangeWardTag>() == null) return;
            bool barrel = IsBarrel(unit);
            bool flying = barrel && IsFlying(unit);
            bool skipAllied = false; int rel = 0; string team = "";
            if (flying)
            {
                bool known = LaunchRelation(__instance, unit, out rel, out team);
                skipAllied = CastleStorySteam.OrangeWardPatchRules.SkipAlliedBarrel(SkipAlliedOn != null && SkipAlliedOn(), known, rel);
            }
            if (barrel && (flying ? flyLogs++ < 60 : groundLogs++ < 12)) Log("CanFireAt " + unit.name + " flying=" + flying + FlyInfo(unit) + " " + team + " rel=" + rel + " skipAllied=" + skipAllied + " gameSays=" + __result + " dist=" + Vector3.Distance(unit.transform.position, __instance.transform.position).ToString("F1"));
            if (skipAllied && skipLogs++ < 10) Log("skipped an ALLIED barrel (" + team + ", relation " + rel + "): the ward does not shoot its own side's catapult shots (Fixes/OrangeWardSkipAllied)");
            if (!flying || skipAllied) __result = false;
        }
        catch (Exception) { }
    }

    // ---- the placement ghost / blueprint hologram: the game gives every blueprint the shared blue hologram materials (and red ones for an invalid spot) and
    // re-assigns them on every state change; for the OrangeWard blueprint the blue ones are swapped for orange copies right after that, so it is orange from the first frame
    static readonly Dictionary<Material, Material> tinted = new Dictionary<Material, Material>();
    static readonly string[] hologramColors = { "_Color", "_OverlayColor", "_RimColor", "_TintColor" };

    static bool Bluish(Color c) { return c.b > 0.55f && c.b > c.r * 1.6f; }

    internal static Material OrangeCopy(Material m)   // 0.4.3: also used for the orange Altar preview (OrangeWardExtras)
    {
        if (m == null) return null;
        Material c; if (tinted.TryGetValue(m, out c) && c != null) return c;
        bool any = false; c = new Material(m); c.name = m.name + "_Orange";
        foreach (string prop in hologramColors)
        {
            if (!c.HasProperty(prop)) continue; Color col = c.GetColor(prop);
            if (Bluish(col)) { c.SetColor(prop, new Color(Orange.r, Orange.g, Orange.b, col.a)); any = true; }
        }
        if (!any) c = m;   // red (invalid spot) and other states keep the game's own material
        tinted[m] = c; return c;
    }

    static void AfterUpdateMaterials(StateRenderer __instance)
    {
        try
        {
            if (__instance == null) return;
            if (!OrangeWardExtras.OrangeHolograms()) return;   // 0.4.7: the game's own blue hologram unless [Fixes] OrangeWardOrangeHologram = true
            Blueprint bp = __instance.GetComponentInParent<Blueprint>(); if (bp == null) return;
            AssetKeyHolder holder = bp.GetComponent<AssetKeyHolder>(); if (holder == null || holder.AssetKey != OrangeBp) return;
            foreach (MeshRenderer r in __instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null) continue; Material[] ms = r.sharedMaterials; bool changed = false;
                for (int i = 0; i < ms.Length; i++) { Material o = OrangeCopy(ms[i]); if (o != ms[i]) { ms[i] = o; changed = true; } }
                if (changed) r.sharedMaterials = ms;
            }
        }
        catch (Exception) { }
    }

    // ---- icon: the Sentinel icon with its red pixels turned orange
    internal static IconKey CurrentIcon { get { return orangeIcon; } }

    // 0.4.1: OrangeWardRecipe calls this once the Lua exposure worked, so the item and recipe icons are orange even before any Lua code asks for the icon. A failure here must not
    // poison the later call from Lua (EnsureIcon gives up for good after an exception), so the flag is cleared again.
    internal static void TryIconEarly()
    {
        if (orangeIcon != null) return;
        IconKey k = EnsureIcon();
        if (k == null) { iconFailed = false; Log("icon not ready yet, it is created by the first SB_OrangeIconKey() call from Lua"); }
    }

    internal static IconKey EnsureIcon()
    {
        if (orangeIcon != null || iconFailed) return orangeIcon;
        try
        {
            MetaIcon mi = new MetaIcon(); mi.Name = "_OrangeWard";
            Sprite small;
            Sprite emb = EmbeddedIcon(out small);
            if (emb != null) { mi.Image64 = emb; mi.Image32 = small != null ? small : emb; Log("icon: embedded picture" + (small != null ? " (+ square small icon)" : "")); }
            else
            {
                MetaIcon src = IconDB.GetIcon(IconKeys._Sentinel);
                if (src == null || src.Image64 == null) return null;
                mi.Image64 = Recolor(src.Image64); mi.Image32 = src.Image32 != null ? Recolor(src.Image32) : mi.Image64;
                Log("icon: recoloured Sentinel (embedded picture failed)");
            }
            IconDB.AddIcon("SpriteCompiler", "_OrangeWard", mi);
            orangeIcon = new IconKey("SpriteCompiler", "_OrangeWard");
            Log("icon registered");
            OrangeWardRecipe.OnIconReady();
        }
        catch (Exception e) { iconFailed = true; Log("icon failed: " + e.GetType().Name + " " + e.Message); }
        return orangeIcon;
    }

    const string IconPng = "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";

    // the finished orange icon (57x83, same size as the game's ward icons) is stored in the plugin so it does not depend on reading the game's atlas
    // 0.4.3: small = the same art on a SQUARE canvas for the Get32() users (the queue strip and the item counters draw it into a square cell without keeping the aspect, which
    // stretched the 57x83 picture 1.46 times sideways); null = the small icon stays the big one (switch off / any problem)
    static Sprite EmbeddedIcon(out Sprite small)
    {
        small = null;
        try
        {
            Texture2D t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            if (!t.LoadImage(Convert.FromBase64String(IconPng)) || t.width < 8) return null;
            t.filterMode = FilterMode.Bilinear; t.wrapMode = TextureWrapMode.Clamp;
            Texture2D t2 = ShrinkInCanvas(t, IconScale, t.width, t.height);
            Log("icon scale " + IconScale.ToString("F2") + " canvas " + t2.width + "x" + t2.height);
            Sprite big = Sprite.Create(t2, new Rect(0, 0, t2.width, t2.height), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
            try { small = SquareSmallIcon(t); }
            catch (Exception e) { small = null; LogR("icon32: square icon failed (" + e.GetType().Name + " " + e.Message + "), the small icon stays the big one"); }
            return big;
        }
        catch (Exception e) { Log("embedded icon failed: " + e.Message); return null; }
    }

    static Sprite SquareSmallIcon(Texture2D art)
    {
        if (!OrangeWardExtras.On(OrangeWardExtras.SquareIcon)) { LogR("icon32: switched off by the config ([Fixes] OrangeWardSquareIcon = false): the small icon is the 0.4.1 portrait picture"); return null; }
        float aspect = StockSmallAspect();
        int dh = art.height, dw = CastleStorySteam.OrangeWardPatchRules.CanvasWidth(dh, aspect);
        Texture2D sq = ShrinkInCanvas(art, IconScale, dw, dh);
        LogR("icon32: square canvas " + dw + "x" + dh + " (stock Image32 aspect " + aspect.ToString("F2") + "), art scale " + IconScale.ToString("F2"));
        return Sprite.Create(sq, new Rect(0, 0, dw, dh), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
    }

    // width / height of the stock small icon of the red ward (1 when it cannot be read). The stock rects are logged once: they are the evidence for the canvas.
    static float StockSmallAspect()
    {
        try
        {
            MetaIcon stock = IconDB.GetIcon(IconKeys._Sentinel);
            if (stock != null && stock.Image32 != null)
            {
                Rect r = stock.Image32.rect;
                string big = stock.Image64 != null ? stock.Image64.rect.width.ToString("F1") + "x" + stock.Image64.rect.height.ToString("F1") + " ppu " + stock.Image64.pixelsPerUnit.ToString("F0") : "none";
                LogR("icon32: stock _Sentinel Image32 " + r.width.ToString("F1") + "x" + r.height.ToString("F1") + " ppu " + stock.Image32.pixelsPerUnit.ToString("F0") + ", Image64 " + big);
                if (r.height > 0.5f) return r.width / r.height;
            }
        }
        catch (Exception e) { LogR("icon32: stock icon not readable: " + e.GetType().Name + " " + e.Message); }
        return 1f;
    }

    // 0.3.105: the art shrunk by k around the centre of the canvas, 2x2 supersampled with premultiplied alpha. Whatever rule the menu uses to fit the sprite into its box, the drawn art
    // is exactly k times smaller than before. 0.4.3: the destination canvas (dw x dh) may differ from the source (57x83) in size; the arithmetic is OrangeWardPatchRules.SourcePixel
    // (identical to the 0.3.105 one when the sizes are equal: unit-tested).
    static Texture2D ShrinkInCanvas(Texture2D src, float k, int dw, int dh)
    {
        int sw = src.width, sh = src.height;
        Texture2D dst = new Texture2D(dw, dh, TextureFormat.ARGB32, false);
        Color32[] px = new Color32[dw * dh];
        float[] o = new float[] { -0.25f, 0.25f };
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float r = 0f, g = 0f, b = 0f, a = 0f; int n = 0;
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                    {
                        n++;
                        float sx, sy;
                        CastleStorySteam.OrangeWardPatchRules.SourcePixel(x, y, o[i], o[j], dw, dh, sw, sh, k, out sx, out sy);
                        if (!CastleStorySteam.OrangeWardPatchRules.InsideSource(sx, sy, sw, sh)) continue;
                        Color c = src.GetPixelBilinear((sx + 0.5f) / sw, (sy + 0.5f) / sh);
                        r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
                    }
                if (a > 0.0001f)
                    px[y * dw + x] = new Color32((byte)Mathf.Clamp(r / a * 255f, 0f, 255f), (byte)Mathf.Clamp(g / a * 255f, 0f, 255f), (byte)Mathf.Clamp(b / a * 255f, 0f, 255f), (byte)Mathf.Clamp(a / n * 255f, 0f, 255f));
            }
        dst.SetPixels32(px); dst.Apply(false);
        dst.filterMode = FilterMode.Bilinear; dst.wrapMode = TextureWrapMode.Clamp;
        return dst;
    }

    static Sprite Recolor(Sprite s)
    {
        Texture tex = s.texture; Rect r = s.textureRect;
        RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32); Graphics.Blit(tex, rt);
        RenderTexture prev = RenderTexture.active; RenderTexture.active = rt;
        Texture2D t2 = new Texture2D((int)r.width, (int)r.height, TextureFormat.ARGB32, false);
        t2.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0); RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
        Color32[] px = t2.GetPixels32();
        for (int i = 0; i < px.Length; i++)
        {
            Color32 c = px[i]; if (c.a < 20) continue;
            if (c.r > c.g * 1.3f && c.r > c.b * 1.3f) { c.g = (byte)Mathf.Min(255, c.g + (int)((c.r - c.g) * 0.55f)); px[i] = c; }
        }
        t2.SetPixels32(px); t2.Apply();
        return Sprite.Create(t2, new Rect(0, 0, t2.width, t2.height), new Vector2(0.5f, 0.5f), s.pixelsPerUnit, 0u, SpriteMeshType.FullRect);
    }

    // Lua: SB_OrangeIconKey():Get64()  (falls back to the Lantern icon key when the recolour fails)
    static void AfterScript(Script __instance)
    {
        try
        {
            if (__instance == null || __instance.Globals == null) return;
            __instance.Globals.Set("SB_OrangeIconKey", DynValue.NewCallback((ScriptExecutionContext exec, CallbackArguments args) =>
            {
                IconKey k = EnsureIcon();
                return DynValue.FromObject(exec.GetScript(), k != null ? k : IconKeys._Lantern);
            }, "SB_OrangeIconKey"));
            OrangeWardExtras.OnScript(__instance);   // 0.4.3: label callback of the hotkey badge + remembers the Script for the key
        }
        catch (Exception) { }
    }

    // ---- for the virtual player: list the barrels and the orange wards
    internal static string Describe()
    {
        StringBuilder sb = new StringBuilder(); int n = 0;
        foreach (ExplodeOnDeath b in UnityEngine.Object.FindObjectsOfType<ExplodeOnDeath>())
        {
            if (b == null || !b.gameObject.activeInHierarchy || !IsBarrel(b.gameObject)) continue; n++;
            if (n > 10) continue; Vector3 p = b.transform.position;
            sb.Append(" | ").Append(b.name).Append(" (").Append(p.x.ToString("F1")).Append(',').Append(p.y.ToString("F1")).Append(',').Append(p.z.ToString("F1")).Append(") trackable=").Append(b.GetComponent("Trackable") != null).Append(" flying=").Append(IsFlying(b.gameObject));
        }
        int w = 0; foreach (OrangeWardTag t in UnityEngine.Object.FindObjectsOfType<OrangeWardTag>()) if (t != null && t.gameObject.activeInHierarchy) w++;
        return "barrels=" + n + " orangeWards=" + w + " blueprintRegistered=" + Factory.Exist(OrangeBp) + sb;
    }
}
