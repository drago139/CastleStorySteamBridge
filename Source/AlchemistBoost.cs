using System;
using System.Reflection;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// ALCHEMIST-BOOST (bug pack part 5; HOST only; cfg [Fixes] AlchemistBoost 0/1/2, AlchemistAmmoFloor, AlchemistCooldownFactor). User request, 2026-11-06: make the "bomber" throw many times.
// The "bomber" is the ALCHEMIST (a Bricktron holding the Backpack tool; verified in the decompiled game code). Its bombs are a plain int on the pouch's ProjectileWeapon (AmmoCount; the prefab starts with 3,
// the capacity 10 is a literal in Transaction.ConvertToAmmo, one bomb is spent per throw, and the ONLY resupply is the player's order: select the unit and right-click a stockpile with bombs). The throw, the
// ammo and the blast run on the HOST only (Locomotion4.CreateFSM is a ServerCallback); guests only see the animation and the ammo ring (CharacterState.ammoCount SyncVar, refreshed by the AmmoCountGetter
// setter). So the build is needed on the PC that HOSTS; guests need nothing.
// PART 1, keep-ammo: Harmony postfix on ProjectileWeapon.Fire (public, non-virtual; the bomb is spent inside it, before it returns). After a real throw (__result != null) by an alchemist of the local
// player's faction (or an ally) the ammo is topped up to AlchemistAmmoFloor (default 10 = the game's capacity, so the ring over the unit stays right): the alchemist never runs dry and throws while there
// are targets. The game's own bows have AmmoCount 9999999, so a big number is an engine-supported pattern; the ring is drawn as AmmoCount / 10, so keep the floor at 10 unless you accept an overfull ring.
// PART 2, faster throwing: postfix on the getter Profession.JobRangedCooldown (0.8 s for the alchemist, read once per throw): for such an alchemist the cooldown is multiplied by AlchemistCooldownFactor
// (default 0.5, never below 0.1 s). The animation chain (aim 0.43 s, throw 0.3 s, cooldown clip 0.5 s) and the AI's 10-frame gap still bound the real rate: expect about one bomb per second instead of
// 1.2-1.6 s, not a machine gun.
// MODE (AlchemistBoost): 0 = off (vanilla), 1 = only in the game's debug / editor mode (Architecte.editorMode, "Режим налагоджування"; the convention of InfiniteCatapultInDebug; DEFAULT), 2 = always.
// Enemy AI factions never carry the pouch; only the local player's faction and its allies are affected. Balance: unlimited area damage, friendly fire on own walls and units, more CPU / GC load from many blasts.
// Log: ALCHEMIST-BOOST top-up #N <job>#<instance id> ammo A -> B (the first 20, then every 200th) and ALCHEMIST-BOOST cooldown X -> Y (the first 3). Both hooks resolve first and
// patch afterwards. 0.4.13: the unit is written as job + instance id, never its name (names come from Info\names.json, saves and mods).
internal static class AlchemistBoost
{
    static Func<int> mode, floor; static Func<float> factor; static Action<string> note;
    static int topUps, cooldowns;
    // CombatAnomaly (part 8) sets this around its own read of JobRangedCooldown: the value is still boosted, but that read writes no ALCHEMIST-BOOST line.
    internal static bool QuietRead;

    internal static void Install(Harmony harmony, Func<int> getMode, Func<int> getFloor, Func<float> getFactor, Action<string> log)
    {
        MethodInfo fire = AccessTools.Method(typeof(ProjectileWeapon), "Fire"); if (fire == null) throw new MissingMethodException("ProjectileWeapon", "Fire");
        PropertyInfo prop = typeof(Profession).GetProperty("JobRangedCooldown", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo getter = prop == null ? null : prop.GetGetMethod(true); if (getter == null) throw new MissingMethodException("Profession", "get_JobRangedCooldown");
        mode = getMode; floor = getFloor; factor = getFactor; note = log;
        harmony.Patch(fire, null, new HarmonyMethod(AccessTools.Method(typeof(AlchemistBoost), "AfterFire")), null, null, null);
        harmony.Patch(getter, null, new HarmonyMethod(AccessTools.Method(typeof(AlchemistBoost), "AfterCooldown")), null, null, null);
    }
    internal static void Clear() { mode = null; floor = null; factor = null; note = null; topUps = 0; cooldowns = 0; }

    // An alchemist of the local player's faction or of an ally (the pattern of HostMarkerFilter / StockFill).
    static bool Eligible(Labor l)
    {
        if (l == null || l.Occupation == null || l.Occupation.CurrentJob != Occupation.Job.Alchemist) return false;
        Faction owner = l.faction, local = User.LocalUser == null ? null : User.LocalUser.faction;
        return owner != null && local != null && (owner == local || Affiliation.IsAllied(local, owner));
    }

    static void AfterFire(ProjectileWeapon __instance, GameObject source, ProjectileAttack __result)
    {
        try
        {
            if (mode == null || floor == null || __result == null || __instance == null || source == null) return;
            if (!BugPackRules.AlchemistActive(mode(), NetworkServer.active, Architecte.editorMode)) return;
            Labor shooter = source.GetComponent<Labor>();
            if (!Eligible(shooter)) return;
            int current = __instance.AmmoCountGetter, want = BugPackRules.AlchemistTopUp(current, floor());
            if (want == current) return;
            __instance.AmmoCountGetter = want;
            topUps++;
            if (note != null && BugPackRules.AlchemistLogTopUp(topUps)) note("ALCHEMIST-BOOST top-up #" + topUps + " " + CombatAnomaly.JobName(source) + "#" + source.GetInstanceID() + " ammo " + current + " -> " + want + " (mode " + mode() + ")");
        }
        catch (Exception) { }
    }

    static void AfterCooldown(Profession __instance, ref float __result)
    {
        try
        {
            if (mode == null || factor == null || __instance == null) return;
            float f = factor(); if (!(f > 0f) || f >= 1f) return;
            if (!BugPackRules.AlchemistActive(mode(), NetworkServer.active, Architecte.editorMode)) return;
            Labor owner = __instance.GetComponent<Labor>();
            if (!Eligible(owner)) return;
            float scaled = BugPackRules.AlchemistCooldown(__result, f);
            if (scaled == __result) return;
            float vanilla = __result; __result = scaled;
            if (note != null && !QuietRead && ++cooldowns <= 3) note("ALCHEMIST-BOOST cooldown " + vanilla.ToString("F2") + " -> " + scaled.ToString("F2") + " s (factor " + f.ToString("F2") + ") " + CombatAnomaly.JobName(owner.gameObject) + "#" + owner.gameObject.GetInstanceID());
        }
        catch (Exception) { }
    }
}
