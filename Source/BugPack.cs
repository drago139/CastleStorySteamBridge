using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

// BUG PACK entry point (PATCHSET-bug-pack, bug-intake session): the three switchable fixes behind ONE call in CastleStoryGameplayFixes.cs, so that file gets a two-line change only.
//   [Fixes] PickerExceptionGuard       (default true)  PickerGuard.cs          B-069 / B-067: a picker handler that throws no longer blocks every picker after it; dead catapults are pruned from the target picker
//   [Fixes] DamagedBlockWithoutCracks  (default true)  DamagedBlockFix.cs      B-EXPLOSION-MAGENTA (one route): damaged blocks that have no crack materials (clay brick, hedge, mine staircase, wooden brace / stump) keep their normal material instead of turning magenta
//   [Fixes] CatapultClickLocalOnly     (default true)  CatapultClickLocal.cs   B-071: the "target chosen" click is played only on the PC whose player gave the order
//   [Fixes] AlchemistBoost 0/1/2 (default 1 = only in the game's debug mode), AlchemistAmmoFloor (10), AlchemistCooldownFactor (0.5)   AlchemistBoost.cs   HOST only: the alchemist ("bomber") never runs dry and throws faster
//   [Fixes] LooseItemCleanup (default true), LooseRawMinutes (10), LooseFinishedMinutes (15)   LooseCleanup.cs   HOST only: things lying loose on the ground are removed after a while
//   [Fixes] StrikeCooldownGuard (default TRUE since 0.4.17; was FALSE), StrikeCooldownGuardFactor (1.0)   StrikeGuard.cs   HOST only: a melee unit keeps its attack cooldown when it gets a new order
//   [Diagnostics] CombatAnomalyLog (default true, LOG ONLY)   CombatAnomaly.cs   HOST only: COMBAT-ANOM lines for melee strikes faster than the cooldown, ranged launches too far or too fast
// (the click log, [Diag] ClickLog, is not part of this call: ClickLog.cs is a tick inside SteamBridgePlugin.Update)
// Every switch is read at the moment the hooked code runs (false = the game's own behaviour at once). Every fix installs on its own: a missing game member switches off THAT fix only
// (Logger warning "... skipped"), the others stay. perf-trace.txt tags: BUGPACK (one start line), PICKER-GUARD, DAMAGED-BLOCK-FIX, HARVESTER-NULLMAT, CATAPULT-CLICK, ALCHEMIST-BOOST,
// LOOSE-CLEANUP, STRIKE-GUARD, COMBAT-ANOM.
internal static class BugPack
{
    internal static void Install(Harmony harmony, ConfigFile config, ManualLogSource logger)
    {
        ConfigEntry<bool> guard = config.Bind("Fixes", "PickerExceptionGuard", true, "When a mouse-picker handler of the game throws (a hovered object was destroyed), clear that picker's hover state and carry on instead of aborting every picker after it each frame (the zone drag handle missing, 'cannot stretch the zone'); also removes destroyed catapults from the catapult target picker (a catapult that cannot be aimed). false = the game's own behaviour. Local input: every player should have it.");
        ConfigEntry<bool> damaged = config.Bind("Fixes", "DamagedBlockWithoutCracks", true, "Blocks that the game draws without crack materials (clay brick, hedge, mine staircase, wooden brace and stump) keep their normal look when an explosion damages them instead of turning magenta. Beams, logs, planks and ramps are not affected by this switch. false = the game's own behaviour. Local rendering: every player should have it.");
        ConfigEntry<bool> click = config.Bind("Fixes", "CatapultClickLocalOnly", true, "The click that confirms a catapult target is played only on the PC whose player chose the target (other players' target changes are silent). Other catapult sounds are untouched. false = the game's own behaviour. Local sound: every player should have it.");
        ConfigEntry<int> boost = config.Bind("Fixes", "AlchemistBoost", 1, "Alchemist ('bomber') boost, HOST only (guests need nothing): the alchemist never runs out of bombs and throws faster. 0 = off (vanilla); 1 = only in the game's debug mode (Режим налагоджування) - the default, so normal matches do not change; 2 = always for your faction and allies. See AlchemistAmmoFloor and AlchemistCooldownFactor.");
        ConfigEntry<int> boostFloor = config.Bind("Fixes", "AlchemistAmmoFloor", 10, "With AlchemistBoost on: after every throw the alchemist's ammo is topped up to this number (10 = the game's capacity and the full ammo ring; the game's bows use 9999999, which overfills the ring). 1..999.");
        ConfigEntry<float> boostFactor = config.Bind("Fixes", "AlchemistCooldownFactor", 0.5f, "With AlchemistBoost on: the alchemist's throw cooldown (0.8 s) is multiplied by this (never below 0.1 s). 1 = unchanged. The throw animation still limits the rate (about 1 bomb per second at 0.5).");
        ConfigEntry<bool> loose = config.Bind("Fixes", "LooseItemCleanup", true, "HOST only (guests need nothing): things lying loose on the ground are removed from the map - raw materials (logs, stone, iron, crystals, rubble) after LooseRawMinutes, finished things (ingots, planks, bricks, dropped tools and weapons, bombs) after LooseFinishedMinutes of game time. Stockpiles, carried or stored things, construction sites, barrels, pallets, structures and units are never touched. false = the game's own behaviour.");
        ConfigEntry<float> looseRaw = config.Bind("Fixes", "LooseRawMinutes", 10f, "LooseItemCleanup: minutes (game time) a raw material may lie loose before it is removed. 0 = never.");
        ConfigEntry<float> looseFin = config.Bind("Fixes", "LooseFinishedMinutes", 15f, "LooseItemCleanup: minutes (game time) a finished thing may lie loose before it is removed. 0 = never.");
        ConfigEntry<bool> strike = config.Bind("Fixes", "StrikeCooldownGuard", true, "HOST only (guests need nothing; everyone should still run the same version): a melee unit (spearman, knight ...) keeps its attack cooldown when it gets a NEW order. The game keeps that cooldown in the order, so 'strike, new order (select, Shift, right click), strike, new order' lets a unit hit faster than normal. With this on, the first strike after a new order waits until the rest of the unit's cooldown has passed (at most one normal cooldown, about 1.5 s for a fresh unit). A fight without new orders is not changed; ranged attacks are not touched. false = the game's own behaviour. Default ON since 0.4.17 (user decision 2026-11-07: troops must not hit faster than they should); NOT yet tested in a match: if units stop attacking or hit oddly, set false. An existing cfg keeps its old value. See StrikeCooldownGuardFactor.");
        ConfigEntry<float> strikeFactor = config.Bind("Fixes", "StrikeCooldownGuardFactor", 1f, "With StrikeCooldownGuard on: the part of the last strike's cooldown that a new order must still wait out. 1 = all of it (the normal attack rate), 0.5 = half, 0 = nothing (same as off). Values above 1 count as 1.");
        ConfigEntry<bool> anomaly = config.Bind("Diagnostics", "CombatAnomalyLog", true, "LOG ONLY, host side, no gameplay effect: writes COMBAT-ANOM lines to perf-trace.txt and LogOutput.log when a unit strikes in melee sooner than its cooldown allows (MELEE-FAST: with how the previous strike ended and whether a new order came in between), when a ranged unit launches at a target beyond its range (RANGED-FAR) or launches sooner than its interval (RANGED-FAST), plus one summary line per game minute of fighting (zeros included). First 50 events in full, then at most one per unit per 10 s. Units are written as job + instance id, no player or account names. false = no lines.");
        Action<string> log = delegate(string t) { CastleStorySteam.SteamBridgePlugin.PtLog(t); };
        string a = Step(logger, "Picker exception guard", delegate { PickerGuard.Install(harmony, delegate { return guard.Value; }, log); });
        string b = Step(logger, "Catapult target list prune", delegate { PickerGuard.InstallCatapult(harmony, delegate { return guard.Value; }, log); });
        string c = Step(logger, "Damaged block fix", delegate { DamagedBlockFix.Install(harmony, delegate { return damaged.Value; }, log); });
        string d = Step(logger, "Catapult click fix", delegate { CatapultClickLocal.Install(harmony, delegate { return click.Value; }, log); });
        string e = Step(logger, "Alchemist boost", delegate { AlchemistBoost.Install(harmony, delegate { return boost.Value; }, delegate { return boostFloor.Value; }, delegate { return boostFactor.Value; }, log); });
        string f = Step(logger, "Loose item cleanup", delegate { LooseCleanup.Install(delegate { return loose.Value; }, delegate { return looseRaw.Value; }, delegate { return looseFin.Value; }, log); });
        string g = Step(logger, "Strike cooldown guard", delegate { StrikeGuard.Install(harmony, delegate { return strike.Value; }, delegate { return strikeFactor.Value; }, log); });
        Func<string> boostNote = delegate
        {
            bool active = CastleStorySteam.BugPackRules.AlchemistActive(boost.Value, UnityEngine.Networking.NetworkServer.active, Brix.Engine.Architecte.editorMode);
            return "boost " + boost.Value + (active ? "/on" : "/off") + "/x" + boostFactor.Value + "/floor" + boostFloor.Value;   // AlchemistBoost mode / active now / cooldown factor / ammo floor
        };
        string h = Step(logger, "Combat anomaly log", delegate { CombatAnomaly.Install(harmony, delegate { return anomaly.Value; }, delegate { return strike.Value; }, boostNote, log, delegate(string s) { logger.LogInfo(s); }); });
        string line = "Bug pack: PickerExceptionGuard=" + guard.Value + " (hover guard " + a + ", catapult prune " + b + "), DamagedBlockWithoutCracks=" + damaged.Value + " (" + c + "), CatapultClickLocalOnly=" + click.Value + " (" + d + "), AlchemistBoost=" + boost.Value + " (" + e + ", floor " + boostFloor.Value + ", cooldown x" + boostFactor.Value + "), LooseItemCleanup=" + loose.Value + " (" + f + ", raw " + looseRaw.Value + " min, finished " + looseFin.Value + " min), StrikeCooldownGuard=" + strike.Value + " (" + g + ", factor " + strikeFactor.Value + "), CombatAnomalyLog=" + anomaly.Value + " (" + h + ")";
        logger.LogInfo(line);
        log("BUGPACK " + line);
        if (!guard.Value || !damaged.Value || !click.Value) logger.LogWarning("A bug-pack fix is switched OFF in the config (see the line above); the behaviour of the vanilla game applies for it.");
    }

    // MOD-INV (0.4.14): the parts whose install failed in this start (read by ModInventory's self-off line).
    internal static readonly List<string> Skipped = new List<string>();

    internal static void Clear() { PickerGuard.Clear(); DamagedBlockFix.Clear(); CatapultClickLocal.Clear(); AlchemistBoost.Clear(); LooseCleanup.Clear(); StrikeGuard.Clear(); CombatAnomaly.Clear(); }

    static string Step(ManualLogSource logger, string what, Action install)
    {
        try { install(); return "installed"; }
        catch (Exception e) { logger.LogWarning(what + " skipped: " + e.Message + " (a hook patched before the error may still be active)"); Skipped.Add(what); return "SKIPPED"; }
    }
}
