using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lua;
using Brix.UI.Icons;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;

// 0.4.1: ORANGE WARD AS ITS OWN CRAFTABLE ITEM (recipe at the Altar, wheel slot 6).
// Design (all of it verified against the recovered sources / the real DLLs, none of it run in the game):
//  * item identity: the game decides what a blueprint eats by the Ressource adjective of the item (Recepteur capacity of the blueprint = {SentinelWardAdj:1}) and what a
//    worker / crate / the Altar may hold by explicit capacity lists. The orange clones therefore get the stock adjective Brix.Game.AI.Stick (a Ressource nobody uses: no object,
//    recipe, Lua file or scene mentions it; LuaResource.Stick, AdjectiveInfo "Stick" and Palette capacity Stick:80 already exist). No new adjective class, so no AdjectiveInfo /
//    Adjectif.StringToAdjectif / Json "$type" problem for a plugin assembly.
//  * the red ward is never touched: only the clones (OrangeWardTag object, blueprint whose BlueprintResult is the orange object) are changed.
//  * all-or-nothing (0.4.1, skeptic review): the identity swap only happens when the config switch is on AND the five essential hooks are installed, and it is undone (Revert) when the Lua
//    exposure fails, so the orange blueprint never ends up wanting an item nothing can craft. What a SAVE may refer to (recipe id 200, its RecipeDisplay entry) is registered in every case.
internal static class OrangeWardRecipe
{
    internal const int RecipeId = 200;        // CookBook id. Stock recipes are 1..31; the Altar queue packs one id per 8 bits (CraftingState.ToLong), so 1..255 only. Part of the save/network format: never change.
    internal const int LuaRecipeValue = 37;   // LuaCrafting.LuaRecipe is 0..36. Used only inside one PC (never sent over the network).
    const int IronCost = 1, WorkUnits = 10;   // = the red ward (CookBook.SentinelWard)
    // 0.4.24 (user: "80 of the golden resource, the ward is too strong"): the red ward costs 40 orange crystal, the orange one 80 by default. [Fixes] OrangeWardCrystalCost, read when the
    // recipe is built (once per start). Every player should use the same value (the Altar menu shows the recipe each PC built). The Altar's own per-resource cap is a separate matter (AltarCapacity).
    internal const int DefaultCrystalCost = 80;
    internal static Func<int> CostGetter;
    static int CrystalCost { get { int c = CostGetter == null ? DefaultCrystalCost : CostGetter(); return c < 1 || c > 400 ? DefaultCrystalCost : c; } }
    internal static Func<bool> AltarCapOn;   // [Fixes] OrangeWardAltarCapacity (default true)
    // What AltarCapacity must make the Altar hold: 0 = leave the Altar alone (feature off, hooks missing, or the cost fits the stock cap of 40).
    internal static int WantedCrystals()
    {
        if (!hooksOk || (Enabled != null && !Enabled()) || (AltarCapOn != null && !AltarCapOn())) return 0;
        int c = CrystalCost; return c > 40 ? c : 0;
    }
    const string Marker = "SB-ORANGE-6";

    internal static RecipeInfo Info;
    internal static Func<bool> Enabled;   // BepInEx config [Fixes] OrangeWardCraftable (default true)
    static bool identityOn, done, exposeFailed, hooksOk;
    static int logs, hookLogs, appLogs, capLogs, gameMenuLogs;
    static AdjectiveInfo stickInfo;
    static Factory.AssetKey wardKey, bpKey;   // the two templates, kept for Revert()

    static void Log(string text) { try { CastleStorySteam.SteamBridgePlugin.PtLog("ORANGE-WARD RECIPE " + text); } catch (Exception) { } }

    // MOD-INV (0.4.14): one word for ModInventory's self-off line (no behaviour change).
    internal static string ModInvStatus()
    {
        try
        {
            if (Enabled != null && !Enabled()) return "off by config";
            if (!hooksOk) return "hooks missing";
            if (exposeFailed) return "lua expose failed";
            return done ? "ok" : "lua not exposed yet";
        }
        catch (Exception) { return "?"; }
    }

    static string Dump(Description d)
    {
        if (d == null || d.DicoAdjectif == null) return "[none]";
        StringBuilder sb = new StringBuilder("[");
        bool first = true;
        foreach (KeyValuePair<Type, Adjectif> kv in d.DicoAdjectif)
        {
            if (!first) sb.Append(", "); first = false;
            sb.Append(kv.Key.Name);
            Quantifiable q = kv.Value == null ? null : kv.Value.quantifiable;
            if (q != null) sb.Append(':').Append(q.valeur);
        }
        return sb.Append(']').ToString();
    }

    // ---------------------------------------------------------------- hooks
    internal static void Install(Harmony harmony)
    {
        // essential hooks: if one is missing the feature stays OFF (Prepare) and the clones keep the red identity exactly as in 0.3.108
        bool ok = true;
        ok &= Hook(harmony, AccessTools.Method(typeof(Apparence), "Initialize"), "BeforeApparenceInit", true);
        ok &= Hook(harmony, AccessTools.Method(typeof(Recepteur), "Initialize"), "BeforeReset", true);          // at instantiate: nothing can read the capacity before it is repaired
        ok &= Hook(harmony, AccessTools.Method(typeof(Recepteur), "ResetDescriptions"), "BeforeReset", true);   // backstop (Start and ClearExclusions); BeforeReset is idempotent
        ok &= Hook(harmony, AccessTools.Method(typeof(LuaCrafting), "Register", new Type[] { typeof(Script) }), "AfterRegister", false);
        ok &= Hook(harmony, typeof(BrixScriptLoader).GetMethod("MoonSharp.Interpreter.Loaders.IScriptLoader.LoadFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), "AfterLoadFile", false);
        hooksOk = ok;
        Log(ok ? "essential hooks installed (5)" : "an essential hook is missing: the feature stays OFF (0.3.108 behaviour)");
        // trace only
        Hook(harmony, AccessTools.Method(typeof(CraftingLabor), "QueueRecipe"), "AfterQueue", false);
        Hook(harmony, AccessTools.Method(typeof(Recipe), "Finish"), "AfterFinish", false);
        Hook(harmony, AccessTools.Method(typeof(Recepteur), "OnObjectTracked"), "AfterTracked", false);
        Hook(harmony, AccessTools.Method(typeof(Blueprint), "MorphToObject"), "AfterMorph", false);
        try { AltarCapacity.Install(harmony, WantedCrystals, delegate(string s) { Log(s); }); }
        catch (Exception e) { Log("ALTAR-CAP hook missing (" + e.GetType().Name + " " + e.Message + "): a ward costing more than 40 crystals would not be craftable"); }
    }

    static bool Hook(Harmony h, MethodBase target, string hook, bool prefix)
    {
        try
        {
            if (target == null) { Log("hook " + hook + ": target method not found"); return false; }
            HarmonyMethod m = new HarmonyMethod(typeof(OrangeWardRecipe).GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
            if (prefix) h.Patch(target, m, null); else h.Patch(target, null, m);
            return true;
        }
        catch (Exception e) { Log("hook " + hook + " failed: " + e.GetType().Name + " " + e.Message); return false; }
    }

    // ---------------------------------------------------------------- identity of the clones
    // adjective -> another adjective, same quantity, on the Description of ONE object (the clone's own DicoAdjectif copy), never on a template shared with the red ward.
    // Atomic: the new adjective is built first, and a failed Add puts the old one back.
    static bool SwapAdj(Description d, Type from, Type to)
    {
        Adjectif old = d.Get(from);
        if (old == null) return false;
        Quantifiable q = old.quantifiable;
        Adjectif fresh = Adjectif.New(to, q != null ? q.valeur : 1);   // built first: if this throws nothing was changed
        d.Remove(from);
        try { d.Add(fresh); }
        catch (Exception) { d.Add(old); throw; }
        return true;
    }
    static bool SwapWard(Description d) { return SwapAdj(d, typeof(SentinelWardAdj), typeof(Stick)); }
    static bool UnswapWard(Description d) { return SwapAdj(d, typeof(Stick), typeof(SentinelWardAdj)); }

    // Called by OrangeWardItem.EnsureRegistered with the two clones (ward object already a template, blueprint not yet). Checks first, swaps second: both or none.
    internal static bool Retarget(GameObject ward, GameObject blueprint)
    {
        Apparence ap = null; Recepteur rec = null; FieldInfo principal = null; bool swappedWard = false, swappedCap = false;
        try
        {
            ap = ward == null ? null : ward.GetComponent<Apparence>();
            rec = blueprint == null ? null : blueprint.GetComponent<Recepteur>();
            principal = AccessTools.Field(typeof(Apparence), "_principale");
            if (ap == null || ap.description == null || ap.description.Get(typeof(SentinelWardAdj)) == null) { Log("retarget skipped: the ward clone has no Apparence with SentinelWardAdj"); return false; }
            if (rec == null || rec.BaseCapacity == null || rec.BaseCapacity.Get(typeof(SentinelWardAdj)) == null) { Log("retarget skipped: the blueprint clone has no Recepteur capacity with SentinelWardAdj"); return false; }
            if (principal == null) { Log("retarget skipped: Apparence._principale not found"); return false; }
            string wardBefore = Dump(ap.description), capBefore = Dump(rec.BaseCapacity);
            swappedWard = SwapWard(ap.description);
            swappedCap = SwapWard(rec.BaseCapacity);
            // Apparence caches its principal Ressource in Initialize(); the clone was initialised while it was still red and template readers
            // (Apparence.PrincipaleOf(Factory.Peek(key)) in LuaExtenders.ToDynValue, Recepteur.HasRoomFor(AssetKey)) use that cache
            principal.SetValue(ap, ap.description.Get(typeof(Stick)) as Ressource);
            identityOn = true;
            Log("retarget ok: ward " + wardBefore + " -> " + Dump(ap.description) + " principal=" + (ap.Principale == null ? "null" : ap.Principale.GetType().Name) + "; blueprint capacity " + capBefore + " -> " + Dump(rec.BaseCapacity));
            return true;
        }
        catch (Exception e)
        {
            Log("retarget failed: " + e.GetType().Name + " " + e.Message + " (undoing the swap)");
            identityOn = false;
            try
            {
                if (swappedWard) UnswapWard(ap.description);
                if (swappedCap) UnswapWard(rec.BaseCapacity);
                if (ap != null && principal != null) principal.SetValue(ap, ap.description.Get(typeof(SentinelWardAdj)) as Ressource);
            }
            catch (Exception) { }
            return false;
        }
    }

    // Undo of Retarget on the two templates. Used when the Lua exposure fails: with the Stick identity the orange blueprint would want an item nothing can craft (in 0.3.108 it ate a red ward).
    // Instances that were already built keep their identity (none exist yet at the first Lua Script); the instance hooks stop at once because identityOn is cleared first.
    static void Revert(string why)
    {
        identityOn = false;
        try
        {
            GameObject wt = wardKey == null ? null : Factory.Peek(wardKey);
            GameObject bt = bpKey == null ? null : Factory.Peek(bpKey);
            Apparence ap = wt == null ? null : wt.GetComponent<Apparence>();
            Recepteur rec = bt == null ? null : bt.GetComponent<Recepteur>();
            int n = 0;
            if (ap != null && ap.description != null && UnswapWard(ap.description)) n++;
            if (rec != null && rec.BaseCapacity != null && UnswapWard(rec.BaseCapacity)) n++;
            FieldInfo principal = AccessTools.Field(typeof(Apparence), "_principale");
            if (ap != null && principal != null) principal.SetValue(ap, ap.description.Get(typeof(SentinelWardAdj)) as Ressource);
            Log("revert (" + why + "): " + n + " template(s) back to the red identity, the 0.3.108 behaviour applies; ward " + (ap == null ? "none" : Dump(ap.description)) + ", blueprint capacity " + (rec == null ? "none" : Dump(rec.BaseCapacity)));
        }
        catch (Exception e) { Log("revert failed: " + e.GetType().Name + " " + e.Message); }
    }

    // Instances: Unity builds every pooled object with Object.Instantiate, and DicoAdjectif may hand back the md5-cached ORIGINAL (red) set for a clone whose
    // dictionary was edited. These two hooks make every instance correct no matter what the cache does (same pattern as CarryCapacityFix: edit before ResetDescriptions).
    static void BeforeApparenceInit(Apparence __instance)
    {
        try
        {
            if (!identityOn || __instance == null) return;
            Description d = __instance.description;
            if (d == null || d.DicoAdjectif == null || !d.DicoAdjectif.ContainsKey(typeof(SentinelWardAdj))) return;   // blocks, units, orange (already Stick) and everything else leave here
            if (__instance.GetComponent<OrangeWardTag>() == null) return;                                         // the red ward leaves here
            string before = appLogs < 4 ? Dump(d) : null;
            SwapWard(d);
            if (appLogs++ < 4) Log("hook Apparence.Initialize: orange ward instance " + __instance.name + " came back from the md5 cache as " + before + " -> swapped to " + Dump(d));
        }
        catch (Exception) { }
    }

    static void BeforeReset(Recepteur __instance)
    {
        try
        {
            if (!identityOn || __instance == null) return;
            Description cap = __instance.BaseCapacity;
            if (cap == null || cap.DicoAdjectif == null || !cap.DicoAdjectif.ContainsKey(typeof(SentinelWardAdj))) return;   // only red/orange blueprints, the Bricktron and the stock crate get past this line
            Blueprint bp = __instance.GetComponent<Blueprint>();
            if (bp != null)
            {
                if (OrangeWardItem.orangeResult != null && bp.BlueprintResult == OrangeWardItem.orangeResult)
                {
                    string before = capLogs < 6 ? Dump(cap) : null;
                    SwapWard(cap);
                    if (capLogs++ < 6) Log("hook Recepteur.ResetDescriptions: orange blueprint instance " + __instance.name + " capacity " + before + " -> " + Dump(cap));
                }
                return;   // the red blueprint is never touched
            }
            if (__instance.GetComponent<Labor>() != null && cap.Get(typeof(Stick)) == null)
            {
                cap.Add(Adjectif.New(typeof(Stick), 1));
                if (capLogs++ < 6) Log("hook Recepteur.ResetDescriptions: worker " + __instance.name + " may now carry one Stick (orange ward) -> " + Dump(cap));
            }
        }
        catch (Exception) { }
    }

    // ---------------------------------------------------------------- registries (everything the game looks up by the product / adjective)
    static bool Guard(string what, Action step)
    {
        try { step(); Log("registry " + what + " ok"); return true; }
        catch (Exception e) { Log("registry " + what + " FAILED: " + e.GetType().Name + " " + e.Message); return false; }
    }

    // Called by EnsureRegistered before Retarget; true = the identity swap may go ahead.
    internal static bool Prepare(Factory.AssetKey creates)
    {
        if (creates == null) return false;
        // Always, even when the feature is switched off or a hook is missing: what a SAVE can refer to. A recipe id 200 in an Altar queue is resolved with CookBook.GetById
        // (KeyNotFoundException when absent) and the Altar looks the queued product up in RecipeDisplay.displays (unguarded indexer). Both are harmless while nothing offers the recipe.
        bool basics = Guard("RecipeDisplay.displays", delegate { MapDisplay(creates); });
        basics = basics && Guard("CookBook recipe id " + RecipeId, delegate { BuildRecipe(creates); });
        if (!basics) return false;
        if (Enabled != null && !Enabled()) { Log("switched off by the config ([Fixes] OrangeWardCraftable = false): the orange ward keeps the red identity as in 0.3.108 (recipe id " + RecipeId + " stays registered so a save that queued it still loads)"); return false; }
        if (!hooksOk) { Log("essential hook missing: feature OFF, the orange ward keeps the red identity as in 0.3.108"); return false; }
        bool ok = Guard("Description.reps (Stick -> " + creates + ")", delegate { MapReps(creates); });
        ok = ok && Guard("Props (carried model)", MapProps);
        ok = ok && Guard("AdjectiveInfo Stick", RenameInfo);
        return ok;
    }

    // Description.reps: adjective -> the object that represents it. The Altar (CraftingInventory) finds the finished product, and the build goals (BuildGoalProvider.UniqueItemsNeeded)
    // find what to fetch, through it; without the entry the product is never picked up and the blueprint "needs nothing".
    static void MapReps(Factory.AssetKey creates)
    {
        Description.RepresentativeOf(Adjectif.iron);   // the private static dictionary is built lazily by the first call
        FieldInfo f = AccessTools.Field(typeof(Description), "reps");
        Dictionary<AdjectiveKey, Factory.AssetKey> reps = f == null ? null : f.GetValue(null) as Dictionary<AdjectiveKey, Factory.AssetKey>;
        if (reps == null) throw new MissingFieldException("Description.reps");
        reps[Adjectif.stick.GetKey()] = creates;   // AdjectiveKey has reference equality: use the game's own key instance
    }

    // Props: what a worker holds in the hands. The red ward's model and both-hands rule (0.4.1 - 0.4.5); 0.4.6: the orange copy Accessories/HeldOrangeWard when OrangeWardHeld has
    // registered it on this PC (clone + pool + switch), else exactly the red entry. The entry decides at call time, so a clone that appears later is picked up.
    static void MapProps()
    {
        FieldInfo f = AccessTools.Field(typeof(Props), "props");
        Dictionary<Ressource, Func<Recepteur, Factory.AssetKey>> props = f == null ? null : f.GetValue(null) as Dictionary<Ressource, Func<Recepteur, Factory.AssetKey>>;
        Func<Recepteur, Factory.AssetKey> held;
        if (props == null || !props.TryGetValue(Adjectif.sentinelWardAdj, out held)) throw new MissingFieldException("Props.props / sentinelWardAdj");
        props[Adjectif.stick] = OrangeWardHeld.Wrap(held);
    }

    // RecipeDisplay.displays[key] is an unguarded indexer called from Altar.OnQueueChanged / OnInventoryChanged: an unknown product key throws and aborts CraftingState.Update.
    static void MapDisplay(Factory.AssetKey creates)
    {
        FieldInfo f = AccessTools.Field(typeof(RecipeDisplay), "displays");
        Dictionary<Factory.AssetKey, Func<RecipeDisplay, GameObject>> displays = f == null ? null : f.GetValue(null) as Dictionary<Factory.AssetKey, Func<RecipeDisplay, GameObject>>;
        Func<RecipeDisplay, GameObject> red;
        if (displays == null || !displays.TryGetValue(ObjetsDynamiques.SentinelWard, out red)) throw new MissingFieldException("RecipeDisplay.displays / SentinelWard");
        Func<RecipeDisplay, GameObject> shown = red;
        try { shown = OrangeWardExtras.PreviewFor(red); }
        catch (Exception e) { Log("preview func failed: " + e.GetType().Name + " " + e.Message + " (the original model is mapped as in 0.4.1)"); }
        displays[creates] = shown;   // the Altar preview (a blue hologram while the recipe is the first queue item / being crafted): 0.4.3 an orange hologram twin ([Fixes] OrangeWardModels), else the original one (0.4.1)
    }

    static void RenameInfo()
    {
        AdjectiveInfo info = AdjectiveInfo.GetInfo("Stick");   // falls back to the Adjectives factory before AdjectiveInfo.Register has run; logs an error and returns null if absent
        if (info == null) throw new InvalidOperationException("AdjectiveInfo Stick not found");
        info.descriptiveName = "Orange Ward";
        stickInfo = info;
        if (OrangeWardItem.CurrentIcon != null) OnIconReady();   // the key may already exist: it is created lazily by the first Lua call
    }

    // Called by OrangeWardItem.EnsureIcon once the key "_OrangeWard" exists (and by RenameInfo, whichever comes second). Setting the string earlier would make IconKey.FromString
    // insert IconKeys.Missing under that name and the later "new IconKey(...)" would throw (duplicate key).
    internal static void OnIconReady()
    {
        IconKey key = OrangeWardItem.CurrentIcon;
        if (key == null) return;
        try
        {
            if (stickInfo != null && stickInfo._icon != null && stickInfo._storedIcon != null)
            {
                stickInfo._icon.key = "_OrangeWard"; stickInfo._storedIcon.key = "_OrangeWard";
                Log("AdjectiveInfo Stick now shows the orange icon");
            }
        }
        catch (Exception e) { Log("icon link failed: " + e.GetType().Name + " " + e.Message); }
        try
        {
            if (Info != null)
            {
                // RecipeInfo.icon / drawing are readonly fields; the Altar overhead display (FurnaceOverheadDisplayDriver) reads recipe.icon for every queued recipe
                SetReadonly(Info, "icon", key); SetReadonly(Info, "drawing", key);
                Log("recipe icon and drawing are now orange");
            }
        }
        catch (Exception e) { Log("recipe icon failed: " + e.GetType().Name + " " + e.Message); }
    }

    static void SetReadonly(object target, string field, object value)
    {
        FieldInfo f = typeof(RecipeInfo).GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f == null) throw new MissingFieldException("RecipeInfo." + field);
        f.SetValue(target, value);
    }

    static void BuildRecipe(Factory.AssetKey creates)
    {
        if (Info != null) return;
        RuntimeHelpers.RunClassConstructor(typeof(CookBook).TypeHandle);   // stock recipes 1..31 first
        RecipeInfo existing = null;
        try { existing = CookBook.GetById(RecipeId); } catch (KeyNotFoundException) { }
        if (existing != null) throw new InvalidOperationException("recipe id " + RecipeId + " is already taken by " + existing.Creates);
        Description ingredients = new Description();   // same as RecipeMaker.With(...)
        Adjectif crystal = Adjectif.orangeCrystal.Clone(); crystal.quantifiable.valeur = CrystalCost; ingredients.Add(crystal);
        Adjectif iron = Adjectif.iron.Clone(); iron.quantifiable.valeur = IronCost; ingredients.Add(iron);
        RecipeInfo info = new RecipeInfo(RecipeId, ingredients, 0, WorkUnits, creates, 1, false, IconKeys._Sentinel, IconKeys._Sentinel);   // red placeholders: OnIconReady swaps in the orange key (the Altar overhead display reads recipe.icon)
        CookBook.Register(info);
        Info = info;
    }

    // ---------------------------------------------------------------- after both templates exist
    internal static void Complete(Factory.AssetKey ward, Factory.AssetKey blueprint, bool identity)
    {
        wardKey = ward; bpKey = blueprint;   // kept for Revert()
        if (!identity) { Log("feature OFF: the clones keep the red ward identity (see the lines above); the 0.3.108 behaviour applies"); return; }
        try { Probe(ward, blueprint); } catch (Exception e) { Log("probe failed: " + e.GetType().Name + " " + e.Message); }
        Log("ready for Lua: recipe id " + RecipeId + " creates " + ward + " cost " + CrystalCost + " orange crystal + " + IronCost + " iron, work " + WorkUnits + "; waiting for the first Lua Script");
    }

    // What does a raw Object.Instantiate of the (inactive) template give? That is the path whose md5 cache hands back the ORIGINAL red set when the template's DicoAdjectif was preloaded.
    // Expected: red ("came back"). The two hooks above then repair every real (Factory.Instantiate) instance; the probe runs them on the raw copy to show the result.
    static void Probe(Factory.AssetKey ward, Factory.AssetKey blueprint)
    {
        GameObject wt = Factory.Peek(ward), bt = Factory.Peek(blueprint);
        Ressource tp = Apparence.PrincipaleOf(wt);
        Log("probe template: ward principal=" + (tp == null ? "null" : tp.GetType().Name) + " description=" + Dump(wt.GetComponent<Apparence>().description) + "; blueprint capacity=" + Dump(bt.GetComponent<Recepteur>().BaseCapacity));
        GameObject w = (GameObject)UnityEngine.Object.Instantiate(wt);
        GameObject b = (GameObject)UnityEngine.Object.Instantiate(bt);
        try
        {
            Apparence ap = w.GetComponent<Apparence>(); Recepteur rec = b.GetComponent<Recepteur>();
            string rawWard = Dump(ap.description), rawCap = Dump(rec.BaseCapacity);
            BeforeApparenceInit(ap); BeforeReset(rec);   // the hook bodies, called directly (this does not prove the Harmony wiring: the "hook ..." lines of the first real instances do)
            Log("probe raw Object.Instantiate: ward " + rawWard + (ap.description.Get(typeof(SentinelWardAdj)) == null && rawWard.IndexOf("SentinelWardAdj", StringComparison.Ordinal) >= 0 ? " (red = md5 cache, expected)" : "")
                + " -> after hook " + Dump(ap.description) + "; blueprint capacity " + rawCap + " -> after hook " + Dump(rec.BaseCapacity));
        }
        finally { UnityEngine.Object.Destroy(w); UnityEngine.Object.Destroy(b); }
    }

    // ---------------------------------------------------------------- Lua side
    // The game builds the four Lua<->C# maps inside its first LuaCrafting.Register(Script). Hook = postfix of that method, so the maps exist; it runs for every Script,
    // BEFORE that script's Lua file (preload action of LuaLoader), so Recipe.OrangeWard exists when LUI2.lua loads the Meta_* files.
    static void AfterRegister(Script script) { TryExpose(script); }

    static object StaticOf(string field)
    {
        FieldInfo f = AccessTools.Field(typeof(LuaCrafting), field);
        if (f == null) throw new MissingFieldException("LuaCrafting." + field);
        return f.GetValue(null);
    }

    static void TryExpose(Script script)
    {
        if (done || exposeFailed || script == null || !identityOn || Info == null) return;
        try
        {
            if (!(bool)StaticOf("_isRegistered")) return;
            Dictionary<LuaCrafting.LuaRecipe, List<RecipeInfo>> toInfo = StaticOf("_luaRecipeToRecipeInfo") as Dictionary<LuaCrafting.LuaRecipe, List<RecipeInfo>>;
            Dictionary<RecipeInfo, LuaCrafting.LuaRecipe> fromInfo = StaticOf("_recipeInfoToLuaRecipe") as Dictionary<RecipeInfo, LuaCrafting.LuaRecipe>;
            if (toInfo == null || fromInfo == null) throw new InvalidOperationException("LuaCrafting recipe maps are null");
            LuaCrafting.LuaRecipe lr = (LuaCrafting.LuaRecipe)LuaRecipeValue;
            List<RecipeInfo> one = new List<RecipeInfo>(); one.Add(Info);
            toInfo[lr] = one; fromInfo[Info] = lr;
            // 1. the C# maps round-trip
            if (LuaCrafting.ToLuaRecipe(Info) != lr || LuaCrafting.ToRecipeInfo(lr)[0] != Info) throw new InvalidOperationException("recipe map round trip failed");
            // 2. the game's own code path of Crafting.GetRecipeInfo (Data_Recipe.lua runs it for EVERY Meta.Recipe row while the HUD builds: an exception there would break the whole menu)
            DynValue info = LuaExtenders.ToDynValue(LuaCrafting.ToRecipeInfo(lr), script);
            if (info.Type != DataType.Table || info.Table.Get("input").Type != DataType.Table || info.Table.Get("output").Type != DataType.Table) throw new InvalidOperationException("GetRecipeInfo table is malformed");
            // 3. only now the Lua member (shared owner-less table: every Script sees it)
            DynValue en = (DynValue)StaticOf("RecipeEnum");   // DynValue is a struct: this is a copy that points at the same Table
            if (en.Type != DataType.Table) throw new InvalidOperationException("LuaCrafting.RecipeEnum is not a table");
            if (en.Table.Get("OrangeWard").IsNil()) en.Table.Set("OrangeWard", DynValue.FromObject<LuaCrafting.LuaRecipe>(script, lr));   // never "new LuaEnumDescriptor": its values would not equal the game's as table keys
            done = true;
            Log("lua exposed: Recipe.OrangeWard=" + LuaRecipeValue + " (recipe id " + RecipeId + "); GetRecipeInfo self-check ok: input and output tables built");
        }
        catch (Exception e)
        {
            exposeFailed = true;   // never retry (every later Script would repeat it); the wheel keeps its 5 entries
            Log("lua expose FAILED, wheel stays unchanged: " + e.GetType().Name + " " + e.Message);
            Revert("lua exposure failed");   // else the orange blueprint would want an item nothing can craft (0.3.108: it ate a red ward)
            return;
        }
        // the orange icon key is created lazily by the first SB_OrangeIconKey() call: make it now so the item and recipe icons are orange whenever they are first drawn
        // (own try: a problem here never undoes the exposure; EnsureIcon retries from Lua)
        try { OrangeWardItem.TryIconEarly(); }
        catch (Exception e) { Log("early icon failed: " + e.GetType().Name + " " + e.Message); }
    }

    // Lua text patches, applied in memory to the game's own files while they are loaded (same hook as ItemFilter). No file in Info/Lua is changed.
    static void AfterLoadFile(string file, ref object __result)
    {
        try
        {
            if (!done) return;
            string code = __result as string;
            if (code == null || file == null) return;
            OrangeWardLuaText.ReportAnchors(file, code);   // 0.4.14 MOD-INV: found / NOT FOUND per anchor (log only)
            string patched = OrangeWardLuaText.Patch(file, code);
            if (!object.ReferenceEquals(patched, code)) { __result = patched; if (logs++ < 12) Log("lua patched " + file.Replace('\\', '/') + " (+" + (patched.Length - code.Length) + " chars)"); }
            else if (file.Replace('\\', '/').EndsWith("/GameMenu.lua", StringComparison.OrdinalIgnoreCase) && OrangeWardLuaText.HotkeyKeyed && code.IndexOf(OrangeWardLuaText.Marker, StringComparison.Ordinal) < 0 && gameMenuLogs++ < 3)
                Log("lua: GameMenu.lua came back unchanged (the anchor 'h.HotkeyLabel_Text = ||GetRecipeSlotHotkey(i)' is missing or not unique): slot 6 has no key badge and the key does nothing");
        }
        catch (Exception e) { if (logs++ < 12) Log("lua patch failed: " + e.GetType().Name + " " + e.Message); }
    }

    // ---------------------------------------------------------------- trace (log only; the chain Altar -> worker -> crate -> worker -> blueprint -> ward)
    static void AfterQueue(CraftingLabor __instance, RecipeInfo recipe)
    {
        try { if (recipe != null && recipe.id == RecipeId && hookLogs++ < 150) Log("trace queue: orange recipe queued at " + __instance.name + ", queue now " + __instance.QueuedRecipes.Count); }
        catch (Exception) { }
    }

    static void AfterFinish(Recipe __instance, Recepteur recepteur)
    {
        try { if (__instance != null && __instance.Info != null && __instance.Info.id == RecipeId && hookLogs++ < 150) Log("trace crafted: orange ward produced into " + (recepteur == null ? "null" : recepteur.name) + ", items stored " + (recepteur == null ? -1 : recepteur.StoredItems.Count)); }
        catch (Exception) { }
    }

    static void AfterTracked(Recepteur __instance, GameObject itemGO)
    {
        try
        {
            if (!identityOn || itemGO == null || hookLogs >= 150 || itemGO.GetComponent<OrangeWardTag>() == null) return;
            Apparence a = itemGO.GetComponent<Apparence>();
            hookLogs++;
            Log("trace moved: orange ward item -> " + __instance.name + " (function " + __instance.Fonction + ", worker=" + (__instance.GetComponent<Labor>() != null) + ") principal=" + (a == null || a.Principale == null ? "null" : a.Principale.GetType().Name) + " description=" + (a == null ? "none" : Dump(a.description)));
        }
        catch (Exception) { }
    }

    static void AfterMorph(Blueprint __instance, GameObject __result)
    {
        try
        {
            if (!identityOn || __instance == null || OrangeWardItem.orangeResult == null || __instance.BlueprintResult != OrangeWardItem.orangeResult || hookLogs >= 150) return;
            hookLogs++;
            Log("trace built: orange blueprint finished, spawned " + (__result == null ? "null" : __result.name) + " tag=" + (__result != null && __result.GetComponent<OrangeWardTag>() != null));
        }
        catch (Exception) { }
    }
}

// pure string work, no game types (testable outside the game)
internal static class OrangeWardLuaText
{
    internal const string Marker = "SB-ORANGE-6";
    // 0.4.3: set by OrangeWardExtras.InitKey BEFORE the first Lua file loads. true = slot 6 gets the key badge and the slot action (OrangeWardPatchRules.GameMenuReplacement), false = the 0.4.1 text
    internal static bool HotkeyKeyed;
    // texts as \u escapes so the file encoding cannot matter
    internal const string NameUa = "Оранжевий вард";
    internal const string RecipeInfoUa = "Вард, що стріляє по бочках у польоті. Окремий предмет: підходить лише для оранжевого варда.";
    internal const string ResourceInfoUa = "Готовий оранжевий вард. Підходить лише для будівництва оранжевого варда.";
    const string Icon = "||SB_OrangeIconKey():Get64()";   // the Lua global that OrangeWardItem.AfterScript sets in every Script (same call as the Meta_Structure.lua line)
    const string Nl = "\r\n";   // the game's Lua files use CRLF

    static string Q(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

    internal static readonly string OperableLine =
        "if Recipe.OrangeWard and fy_Altar and _t[fy_Altar] and _t[fy_Altar].Recipes then table.insert(_t[fy_Altar].Recipes, Recipe.OrangeWard) end -- " + Marker;
    internal static readonly string RecipeLine =
        "if Recipe.OrangeWard then _t:Add(Recipe.OrangeWard, {Name = ||" + Q(NameUa) + ", Info = ||" + Q(RecipeInfoUa) + ", Icon = " + Icon + "}) end -- " + Marker;
    internal static readonly string ResourceLine =
        "if Recipe.OrangeWard and Resource.Stick then _t:Add(Resource.Stick, {Name = ||" + Q(NameUa) + ", Info = ||" + Q(ResourceInfoUa) + ", Icon = " + Icon + ", StockpiledIcon = " + Icon + "}) end -- " + Marker;
    internal static readonly string StorageLine =
        "if Recipe.OrangeWard and Resource.Stick then _t.excludedResources[Resource.Stick] = true end -- " + Marker;
    // the GameMenu.lua anchor and its replacement (0.4.1 text without, 0.4.3 text with the key) live in OrangeWardPatchRules (unit-tested); Patch() below only asks for them

    static bool EndsWith(string f, string name) { return f.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) || f.Equals(name, StringComparison.OrdinalIgnoreCase); }

    // returns the same instance when nothing changes
    internal static string Patch(string file, string code)
    {
        if (file == null || code == null || code.IndexOf(Marker, StringComparison.Ordinal) >= 0) return code;
        string f = file.Replace('\\', '/');
        if (EndsWith(f, "Meta_Operable.lua")) return BeforeFinalReturn(code, OperableLine);
        if (EndsWith(f, "Meta_Recipe.lua")) return BeforeFinalReturn(code, RecipeLine);
        if (EndsWith(f, "Meta_Resource.lua")) return BeforeFinalReturn(code, ResourceLine);
        if (EndsWith(f, "Panel_Storage.lua")) return ReplaceOnce(code, "_t.excludedResources = {}", "_t.excludedResources = {}" + Nl + StorageLine);
        if (EndsWith(f, "GameMenu.lua")) return CastleStorySteam.OrangeWardPatchRules.PatchGameMenu(code, HotkeyKeyed, Marker, Nl);
        return code;
    }

    // MOD-INV (0.4.14): the anchor of each of the five in-memory patches, counted in the text this postfix receives (after ItemFilter and any foreign postfix before it).
    internal static void ReportAnchors(string file, string code)
    {
        try
        {
            if (file == null || code == null || code.IndexOf(Marker, StringComparison.Ordinal) >= 0) return;
            string f = file.Replace('\\', '/');
            string name = f.Substring(f.LastIndexOf('/') + 1);
            if (EndsWith(f, "Meta_Operable.lua") || EndsWith(f, "Meta_Recipe.lua") || EndsWith(f, "Meta_Resource.lua"))
                CastleStorySteam.ModInventory.Anchor("OrangeWard", name, "return _t", CastleStorySteam.ModInventoryRules.AnchorCount(code, "return _t"), false);
            else if (EndsWith(f, "Panel_Storage.lua"))
                CastleStorySteam.ModInventory.Anchor("OrangeWard", name, "_t.excludedResources = {}", CastleStorySteam.ModInventoryRules.AnchorCount(code, "_t.excludedResources = {}"), true);
            else if (EndsWith(f, "GameMenu.lua"))
                CastleStorySteam.ModInventory.Anchor("OrangeWard", name, "HotkeyLabel_Text line", CastleStorySteam.ModInventoryRules.AnchorCount(code, CastleStorySteam.OrangeWardPatchRules.GameMenuAnchor), true);
        }
        catch (Exception) { }
    }

    static string BeforeFinalReturn(string code, string line)
    {
        int i = code.LastIndexOf("return _t", StringComparison.Ordinal);
        if (i < 0) return code;
        return code.Substring(0, i) + line + Nl + code.Substring(i);
    }

    static string ReplaceOnce(string code, string anchor, string replacement)
    {
        int i = code.IndexOf(anchor, StringComparison.Ordinal);
        if (i < 0 || code.IndexOf(anchor, i + anchor.Length, StringComparison.Ordinal) >= 0) return code;
        return code.Substring(0, i) + replacement + code.Substring(i + anchor.Length);
    }
}
