using System;
using System.Collections.Generic;
using System.Reflection;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;

// DAMAGED-BLOCK-FIX (bug pack, switch [Fixes] DamagedBlockWithoutCracks, default true) + HARVESTER-NULLMAT probe. Installed by BugPack.cs. Local rendering only: every PC that plays needs it.
// Problem (B-EXPLOSION-MAGENTA, magenta blocks after explosions - ONE of several possible routes, see below): DefaultVoxelDisplayDataGenerator takes the cracked-block materials from
// BlockInfoMeta.CrackMatHolder.DamagedMats and only when the holder exists and has exactly 4 materials. Blocks drawn by this generator WITHOUT a holder keep damagedMat1..4 = {null}: in the recovered
// data Clay_Brick, Hedge_Block, Mine_Staircase, Mine_Staircase_Block, Wood_Brace and Wood_Stump (the other Wood_* blocks - beams, logs, planks, ramps - and the rope bridge plank use their own generators
// that ignore damage and are NOT affected by this fix). GenerateVoxelDisplayData then does "array = ResolveDamageMaterial(bd) ?? array", but ResolveDamageMaterial returns that non-null array holding a
// null, so the fallback never happens, the block is drawn with Material[]{null} by a MaterialCombineHarvester (GameObject Jeu/MegaVoxels/Harvester; PINK-SCAN shows it with shader=-) and Unity paints
// it magenta as soon as the block has any damage (an explosion that does not destroy it).
// Fix: postfix on ResolveDamageMaterial: if the result is empty or its first material is null, return null so the caller keeps the intact materials (such a block just shows no cracks).
// Without damaged blocks of that kind nothing changes. NOT covered (other possible magenta sources, found by the review; grep PINK-SCAN no-material lines after a fight): break particles of crystals
// (ParticulesCrystal has null material slots) and free (physicalised) wooden blocks whose prefab material is empty.
// Probe: ConfigureHarvester runs once per block key and material set on every chunk redraw (after any block change or damage); the check is a loop over one to three materials, and a null material is
// logged once per block key and slot as HARVESTER-NULLMAT. It names only pink blocks drawn through a harvester.
internal static class DamagedBlockFix
{
    static Func<bool> enabled; static Action<string> note; static FieldInfo idField;
    static readonly HashSet<string> fixedIds = new HashSet<string>(), probedIds = new HashSet<string>();
    static int fixedCount;

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> log)
    {
        Type gen = AccessTools.TypeByName("Brix.Engine.Blocks.DefaultVoxelDisplayDataGenerator"); if (gen == null) throw new MissingMemberException("DefaultVoxelDisplayDataGenerator");
        MethodInfo resolve = AccessTools.Method(gen, "ResolveDamageMaterial"); if (resolve == null) throw new MissingMethodException("DefaultVoxelDisplayDataGenerator", "ResolveDamageMaterial");
        Type harvester = AccessTools.TypeByName("Brix.Engine.MaterialCombineHarvester");
        MethodInfo configure = harvester == null ? null : AccessTools.Method(harvester, "ConfigureHarvester");
        enabled = isEnabled; note = log; idField = AccessTools.Field(gen, "ID");
        harmony.Patch(resolve, null, new HarmonyMethod(AccessTools.Method(typeof(DamagedBlockFix), "AfterResolve")), null, null, null);
        if (configure != null) harmony.Patch(configure, null, new HarmonyMethod(AccessTools.Method(typeof(DamagedBlockFix), "AfterConfigure")), null, null, null);
    }
    internal static void Clear() { enabled = null; note = null; idField = null; fixedIds.Clear(); probedIds.Clear(); }

    static void AfterResolve(object __instance, ref Material[] __result)
    {
        try
        {
            if (enabled == null || !enabled() || __result == null) return;
            if (!BugPackRules.UnusableDamageMaterials(__result.Length, __result.Length > 0 && __result[0] == null)) return;
            __result = null; fixedCount++;
            string id = idField != null && __instance != null ? idField.GetValue(__instance) as string : null;
            if (note != null && fixedIds.Add(id ?? "?")) note("DAMAGED-BLOCK-FIX " + (id ?? "?") + " has no crack material: damaged blocks keep their normal material (#" + fixedCount + " draws)");
        }
        catch (Exception) { }
    }

    static void AfterConfigure(string id, Material[] mats)
    {
        try
        {
            if (note == null || probedIds.Count >= 40) return;
            int bad = -1;
            if (mats == null || mats.Length == 0) bad = -2; else for (int i = 0; i < mats.Length; i++) if (mats[i] == null) { bad = i; break; }
            if (bad != -1 && probedIds.Add((id ?? "?") + "/" + bad))
                note("HARVESTER-NULLMAT id=" + (id ?? "?") + " mats=" + (mats == null ? "null" : mats.Length.ToString()) + " nullIdx=" + bad + " (a block drawn with a missing material = magenta)");
        }
        catch (Exception) { }
    }
}
