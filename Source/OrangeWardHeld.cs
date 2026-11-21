using System;
using System.Text;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStorySteam;
using UnityEngine;

// 0.4.6 HELD ORANGE WARD: the model a worker CARRIES for the orange ward is orange, not the red ward's.
// How the game shows a carried ward (decompiled sources, checked 2026-11-05):
//   Interaction.UpdateCarrying (server only, connected in OnRequested under NetworkServer.active) -> Recepteur.CarryType -> Props.GetProp(adjective)
//   -> Accessories.HeldSentinelWard -> Toolbag.SpawnTool: a pooled object that NetworkServer.Spawn(go, key.AssetId) shows to every guest.
// The model is a SHARED, NETWORKED template (a Base mesh with children Gem = material SentinelWard_Gem, the only red part, and plates), so a texture swap would colour the red wards
// too, or only one PC. The orange ward (Stick identity, OrangeWardRecipe) therefore gets its OWN template Accessories/HeldOrangeWard: a copy of the red one whose Gem has the orange
// material of the placed ward (OrangeWardExtras.OrangeGem), registered like the placed orange ward (Factory.AddAsset<CloneTemplate> right before the game registers a factory with UNet,
// so the spawn handler exists on every PC) and given a pool (AddPoolForKey, see OrangeWardItem.EnsurePools). The Props entry of Stick then returns this key.
// The names are part of the NETWORK format (asset id = MD5(factory + name)): OrangeWardPatchRules.Held*Name, never rename. Everything is all-or-nothing: no Gem to tint, a failed
// registration, a missing pool or the switch off = the red model of 0.4.1 - 0.4.5 and one log line why. A PC without this copy (older version, switch off) cannot draw the model the host
// spawns (UNet logs "Failed to spawn server object"); the unit still carries the ward. Every player should have the same version and the same [Fixes] OrangeWardHeldModel value.
internal static class OrangeWardHeld
{
    internal static Func<bool> HeldOn;   // [Fixes] OrangeWardHeldModel (default true)
    static readonly Factory.AssetKey RedKey = new Factory.AssetKey(OrangeWardPatchRules.HeldFactoryName, OrangeWardPatchRules.HeldRedName);
    static readonly Factory.AssetKey OrangeKey = new Factory.AssetKey(OrangeWardPatchRules.HeldFactoryName, OrangeWardPatchRules.HeldOrangeName);
    static Factory.AssetKey heldOrange;
    static bool cloneReady, poolReady, failed;
    static int useLogs, whyLogs;

    static void Log(string text) { try { SteamBridgePlugin.PtLog("ORANGE-WARD RECIPE HELD " + text); } catch (Exception) { } }

    // ---------------------------------------------------------------- config (called from CastleStoryGameplayFixes right after OrangeWardExtras.BindConfig)
    internal static void BindConfig(ConfigFile config)
    {
        try
        {
            ConfigEntry<bool> held = config.Bind("Fixes", "OrangeWardHeldModel", true, "0.4.6: the orange ward in a worker's hands is orange (a tinted copy of the red held model, registered as Accessories/HeldOrangeWard on every PC). false = the red model as in 0.4.5. Needs Fixes/OrangeWardCraftable = true. Every player should have the same version and the same value: a PC without this copy cannot draw the model (the worker still carries the ward).");
            HeldOn = delegate { return held.Value; };
        }
        catch (Exception e) { Log("config bind failed (" + e.GetType().Name + " " + e.Message + "): the carried model stays red"); }
    }

    static bool SwitchOn()
    {
        try
        {
            bool recipe = OrangeWardRecipe.Enabled == null || OrangeWardRecipe.Enabled();
            return recipe && HeldOn != null && HeldOn();
        }
        catch (Exception) { return false; }
    }

    static void Fail(string why)
    {
        failed = true;
        Log("FAILED, the carried model stays red: " + why);
    }

    // ---------------------------------------------------------------- the clone (idempotent; called right before the game registers a factory with UNet, on every PC)
    internal static void EnsureClone()
    {
        if (cloneReady || failed) return;
        if (!SwitchOn()) return;
        if (!Factory.Exist(RedKey)) return;   // the Accessories factory is not loaded yet: the next call tries again
        if (Factory.Exist(OrangeKey)) { heldOrange = OrangeKey; cloneReady = true; Log("clone " + OrangeKey + " exists already"); return; }
        GameObject go = null;
        try
        {
            Factory factory = Factory.GetFactory(RedKey);
            go = Factory.Instantiate(RedKey, false);
            if (go == null || factory == null) { Fail("could not copy " + RedKey); return; }
            go.name = OrangeWardPatchRules.HeldOrangeName;
            FactoryImprint imprint = go.GetComponent<FactoryImprint>();
            if (imprint != null) imprint.AssetKey = OrangeKey;
            int tinted = OrangeWardExtras.TintGems(go);
            if (tinted == 0) { UnityEngine.Object.Destroy(go); go = null; Fail("no renderer named Gem in " + RedKey + " (" + Describe(Factory.Peek(RedKey)) + ")"); return; }
            Factory.AssetKey added = factory.AddAsset<CloneTemplate>(go);
            go = null;   // owned by the factory from now on
            if (added == null || !Factory.Exist(added)) { Fail("AddAsset returned " + added); return; }
            if (!(added == OrangeKey)) { Fail("AddAsset returned " + added + " instead of " + OrangeKey + " (the carried tool is found by its key)"); return; }
            GameObject template = Factory.Peek(added);
            FactoryImprint back = template == null ? null : template.GetComponent<FactoryImprint>();
            if (back == null || !(back.AssetKey == added)) { Fail("the template of " + added + " does not carry its own key (" + (back == null ? "no FactoryImprint" : (object.ReferenceEquals(back.AssetKey, null) ? "null" : back.AssetKey.ToString())) + ")"); return; }
            heldOrange = added;
            cloneReady = true;
            Log("clone registered: " + RedKey + " -> " + added + ", gems tinted " + tinted + ", renderers: " + Describe(template));
        }
        catch (Exception e)
        {
            try { if (go != null) UnityEngine.Object.Destroy(go); } catch (Exception) { }
            Fail(e.GetType().Name + " " + e.Message);
        }
    }

    // ---------------------------------------------------------------- the pool (the game builds its pools BEFORE the clone exists, see OrangeWardItem.EnsurePools)
    internal static void EnsurePool()
    {
        if (poolReady || failed || !cloneReady || heldOrange == null) return;
        Brix.Lifecycle.Pooling.IObjectPoolSingleton inst = Brix.Lifecycle.Pooling.ObjectPoolSingleton.Instance;
        // an empty table = the game has not run GeneratePools yet: its own loop will still find the template, and a pool added before it would make IndexedPools.Add throw
        if (inst == null || inst.IndexedPools == null || inst.IndexedPools.Count == 0) return;
        if (!inst.IndexedPools.ContainsKey(heldOrange) && !OrangeWardItem.AddPool(inst, heldOrange)) return;   // AddPool logs the reason; the next call tries again
        poolReady = inst.IndexedPools.ContainsKey(heldOrange);
        if (poolReady) Log("pool ready for " + heldOrange + " (pools total " + inst.IndexedPools.Count + ")");
    }

    // ---------------------------------------------------------------- the Props entry of the orange item (Stick): orange when everything exists on this PC, else exactly the red entry
    internal static Func<Recepteur, Factory.AssetKey> Wrap(Func<Recepteur, Factory.AssetKey> red)
    {
        return delegate(Recepteur r)
        {
            try
            {
                bool on = SwitchOn();
                if (heldOrange != null && OrangeWardPatchRules.UseHeldOrange(on, cloneReady, poolReady, failed))
                {
                    if (useLogs++ < 3) Log("carried model: orange " + heldOrange);
                    return heldOrange;
                }
                if (whyLogs++ < 3) Log("carried model: red (" + OrangeWardPatchRules.HeldWhyRed(on, cloneReady, poolReady, failed) + ")");
            }
            catch (Exception) { }
            return red(r);
        };
    }

    static string Describe(GameObject go)
    {
        if (go == null) return "(no object)";
        StringBuilder sb = new StringBuilder();
        try
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                sb.Append(r.name).Append('(').Append(r.sharedMaterial == null ? "-" : r.sharedMaterial.name).Append(") ");
            }
        }
        catch (Exception) { }
        return sb.ToString().Trim();
    }
}
