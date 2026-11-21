using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

internal sealed class CastleStoryGameplayFixes : IDisposable
{
    static CastleStoryGameplayFixes instance;
    Harmony harmony;
    ConfigEntry<bool> captureFix, supportFix, doorFix, treeBlastFix, deferTreeCut, doorAttackFix;   // doorAttackFix: 0.4.22 DOOR-ATTACK
    ConfigEntry<int> carryAmount; ConfigEntry<bool> hideBoxNotice; ConfigEntry<int> zoneMax, treeRadius, sentinelMode;
    static readonly FieldInfo attack = AccessTools.Field(typeof(CaptureClock), "attack");
    static readonly FieldInfo defense = AccessTools.Field(typeof(CaptureClock), "defense");
    static readonly FieldInfo progress = AccessTools.Field(typeof(CaptureClock), "capture");
    static FieldInfo captureFaction, capturePower;
    [ThreadStatic] static CaptureClock clock;
    int notes, doorNotes, treeNotes;
    readonly HashSet<int> captureNotes = new HashSet<int>();
    readonly Dictionary<XYZ, float> supportNotes = new Dictionary<XYZ, float>();
    readonly ConfigFile Config; readonly BepInEx.Logging.ManualLogSource Logger;
    internal CastleStoryGameplayFixes(ConfigFile config, BepInEx.Logging.ManualLogSource logger)
    {
        Config = config; Logger = logger;
        captureFix = Config.Bind("Fixes", "CaptureWardRecoversNeutralization", true, "When friendly defenders return to an owned crystal, unfinished natural neutralization returns to zero instead of remaining stuck. Enemy contention stays unchanged.");
        supportFix = Config.Bind("Fixes", "KeepStructureTerrainSupports", true, "Reject digging/level digging of terrain used as support by a completed structure. Does not affect filling, combat or ordinary free terrain.");
        doorFix = Config.Bind("Fixes", "DoorDemolitionSideApproach", true, "Allow one assigned worker per door demolition goal, using free adjacent side cells when front/back access is blocked. Does not allow attacks through solid cells or change general door passage.");
        treeBlastFix = Config.Bind("Fixes", "ExplosionsFellTrees", true, "Explosions (catapult bombs, barrels) also fell every tree inside the radius they use for the ground, where the map rules allow explosions and tree cutting. Host required.");
        carryAmount = Config.Bind("Fixes", "WorkerCarryPerResource", 20, "Raise a worker's bag capacity for stone, iron and crystals to this many of one type (the game's own data allows fewer, so workers leave for storage early). 0 = keep the game's value. Every player should have the same value.");
        hideBoxNotice = Config.Bind("Fixes", "HideEditModeBoxSelectionNotice", true, "Hide the game's English notice 'Cannot use box selection in Edit mode' (box selection itself stays disabled by the game in edit / debug mode).");
        sentinelMode = Config.Bind("Fixes", "SentinelWardTargets", 0, "Which factions Sentinel wards (the shooting ones) attack: 0 = the game's own setting (enemies), 1 = enemies, neutral and undefined factions, 2 = every faction including allies. Host only. For maps/sandboxes where wards must hurt all kinds of players.");
        zoneMax = Config.Bind("Fixes", "ZoneMaxSize", 200, "Maximum radius / size of zone tools (cleanup and harvest circles, quarry and landscape areas), practically no limit at 200. The game's own cap is 16. 0 = keep the game's value. The host should have the same value.");
        deferTreeCut = Config.Bind("Fixes", "DeferEarlyTreeCut", true, "Guests keep the trees the host felled: when the host's cut message arrives before the tree object has started (the game then drops the cut and the tree stays up as a ghost), the cut is held back and applied as soon as the tree is ready. The host also waits a moment before cutting a tree an explosion just created. Works on every peer, no host required; false = the game's own behaviour.");
        treeRadius = Config.Bind("Fixes", "ExplosionTreeRadius", 6, "Radius (cells) in which an explosion fells trees. The ground damage radius of a catapult bomb is only 3; this is the larger of the two. Needs Fixes/ExplosionsFellTrees = true. Host only.");
        harmony = new Harmony("local.castlestory.steambridge.gameplay");
        instance = this;
        try
        {
            if (attack == null || defense == null || progress == null) throw new MissingFieldException("CaptureClock layout");
            captureFaction = attack.FieldType.GetField("Faction", BindingFlags.Public | BindingFlags.Instance);
            capturePower = defense.FieldType.GetField("CapturePower", BindingFlags.Public | BindingFlags.Instance);
            if (captureFaction == null || capturePower == null) throw new MissingFieldException("CaptureInfo layout");
            Patch(typeof(CaptureClock), "OnTimeIncrement", "BeforeClock", null, "AfterClock");
            Patch(typeof(CaptureProgress), "Add", "BeforeProgress", null, null);
            Patch(typeof(DigGoal), "IsMatchFor", "BeforeDigMatch", null, null);
            Patch(typeof(DigGoal), "ApplyDamage", "BeforeDigDamage", null, null);
            Patch(typeof(LandscapeGoal), "IsMatchFor", "BeforeLandscapeMatch", null, null);
            Patch(typeof(LandscapeGoal), "DigAction", "BeforeLandscapeDig", null, null);
            Patch(typeof(DigGoal), "GetWorkNode", null, "AfterDigWorkNode", null);
            Patch(typeof(LandscapeGoal), "GetWorkNode", null, "AfterLandscapeWorkNode", null);
            DoorDemolitionFix.Install(harmony, delegate { return doorFix.Value; }, delegate(string t) { if (++doorNotes <= 200) CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX " + t); });
            // 0.4.22 DOOR-ATTACK (B-074): soldiers ordered to break an ENEMY door may stand / strike from its free side cells when the native face cells are unreachable. Host only.
            doorAttackFix = Config.Bind("Fixes", "DoorAttackSideApproach", true, "HOST only: units ordered to attack / break an ENEMY door (combat order) may stand on and strike from the door's free side cells on its bottom row (its ends), when the front / back cells cannot be reached (cliff edge, trees, inside the enemy base). Never for allied, neutral or own doors; units still cannot walk through a closed door. false = the game's behaviour. Restart needed.");
            try
            {
                DoorAttackFix.Install(harmony, delegate { return doorAttackFix.Value; }, delegate(string t) { CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX " + t); });
                string da = "Fix 'enemy door attack from side cells': DoorAttackSideApproach=" + doorAttackFix.Value + " (installed; host only)";
                Logger.LogInfo(da); CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX Door attack: " + da);
            }
            catch (Exception e) { DoorAttackFix.Clear(); Logger.LogWarning("Enemy door attack fix skipped: " + e.Message); CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX Door attack: install SKIPPED " + e.GetType().Name + " " + e.Message); }
            try { CarryCapacityFix.Install(harmony, delegate { return carryAmount.Value; }, Note); }
            catch (Exception e) { Logger.LogWarning("Worker carry capacity fix skipped: " + e.Message); }
            try { ZoneSizeFix.Install(harmony, delegate { return zoneMax.Value; }); }
            catch (Exception e) { Logger.LogWarning("Zone size fix skipped: " + e.Message); }
            try { WardShotLog.Install(harmony); } catch (Exception) { }
            try { SentinelTargets.Install(harmony, delegate { return sentinelMode.Value; }, delegate(int v) { sentinelMode.Value = v; }); ConfigEntry<float> orangeRange = Config.Bind("Fixes", "OrangeWardRange", 60f, "Range of the orange barrel ward (the normal Sentinel ward has 30). A bigger range blows barrels up farther away."); ConfigEntry<bool> orangeCraft = Config.Bind("Fixes", "OrangeWardCraftable", true, "The orange barrel ward has its own recipe at the Altar (6th slot) and its own item. false = the 0.3.108 behaviour (the orange blueprint eats a red ward). Every player must have the same value."); OrangeWardRecipe.Enabled = delegate { return orangeCraft.Value; }; try { OrangeWardExtras.BindConfig(Config); } catch (Exception) { } try { OrangeWardHeld.BindConfig(Config); } catch (Exception) { } try { OrangeWardItem.BindConfig(Config); } catch (Exception) { } OrangeWardItem.Install(harmony, delegate { return orangeRange.Value; });; }
            catch (Exception e) { Logger.LogWarning("Sentinel ward targets skipped: " + e.Message); }
            try { HideEditModeNotice.Install(harmony, delegate { return hideBoxNotice.Value; }); }
            catch (Exception e) { Logger.LogWarning("Edit mode notice hide skipped: " + e.Message); }
            try { StockAcceptFill.Install(harmony, Note); }
            catch (Exception e) { Logger.LogWarning("Stock accept fill skipped: " + e.Message); }
            try
            {
                ConfigEntry<bool> probeGate = Config.Bind("Fixes", "GroupsMenuProbeGate", true, "0.4.24: the unit-groups key menu looks for the game's key-bindings menu only when that menu reports it is enabled (plus a check every 30 s) instead of scanning all objects every 0.4 s in every scene (a frame hitch each time). false = the old 0.4 s scan. Takes effect at the next game start.");
                if (probeGate.Value) { GroupsMenuGate.Install(harmony); Note("GroupsMenuProbeGate: " + (GroupsMenuGate.Hooked ? "installed (menu hooks), the 0.4 s object scan is off while no key-bindings menu is enabled" : "hooks missing, the 0.4 s scan stays")); }
                else Note("GroupsMenuProbeGate: switched off ([Fixes] GroupsMenuProbeGate = false), the 0.4 s object scan stays");
            }
            catch (Exception e) { Logger.LogWarning("Groups menu probe gate skipped: " + e.Message); }
            try { PathFailLog.Install(harmony, Note); }
            catch (Exception e) { Logger.LogWarning("Path fail log skipped: " + e.Message); }
            // 0.4.28 STUCK UNITS (user video 2026-11-13: a worker fell through a placed block into the ground, then 'cannot reach destination'). Host only; each use is logged.
            try
            {
                ConfigEntry<bool> stuckFix = Config.Bind("Fixes", "RescueStuckUnitOnPathFail", true, "Host only: when the game reports a failed path ('cannot reach destination') for a unit that stands inside solid ground or a full block, or where the game has no walkable cell, the unit is moved to the first free cell above it or to the nearest free walkable place. Ordinary path failures are not touched.");
                ConfigEntry<bool> jumpFix = Config.Bind("Fixes", "JumpLandingFallback", true, "Host only: when a unit jumps off a ledge and the game finds no landing cell under it although ground or a block is there (the game then aims 20 cells down, through the ground), the landing height is set to that ground. false = only a log line.");
                StuckUnitRescue.Install(harmony, delegate { return stuckFix.Value; }, delegate { return jumpFix.Value; }, delegate(string t) { Logger.LogInfo("Stuck units: " + t); CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX " + t); });
                string su = "Fix 'stuck units': RescueStuckUnitOnPathFail=" + stuckFix.Value + " (" + (StuckUnitRescue.PathHook ? "installed" : "hook missing") + "), JumpLandingFallback=" + jumpFix.Value + " (" + (StuckUnitRescue.JumpHook ? "installed" : "hook missing") + "); host only";
                Logger.LogInfo(su); CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX " + su);
            }
            catch (Exception e) { StuckUnitRescue.Clear(); Logger.LogWarning("Stuck unit rescue skipped: " + e.Message); }
            try { QuarryPickLog.Install(harmony, delegate { return true; }, Note); }
            catch (Exception e) { Logger.LogWarning("Quarry pick log skipped: " + e.Message); }
            try { BugPack.Install(harmony, Config, Logger); }
            catch (Exception e) { Logger.LogWarning("Bug pack skipped: " + e.Message); }
            try { TreeBlastFix.Install(harmony, delegate { return treeBlastFix.Value; }, delegate(string t) { if (++treeNotes <= 400) CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX " + t); }, delegate { return (float)treeRadius.Value; }, delegate { return deferTreeCut.Value; }); }
            catch (Exception e) { Logger.LogWarning("Explosions-fell-trees fix skipped: " + e.Message); }
            try { TreeCutGuard.Install(harmony, delegate { return deferTreeCut.Value; }, delegate(string t) { CastleStorySteam.SteamBridgePlugin.PtLog(t); }); }
            catch (Exception e) { Logger.LogWarning("Early tree cut guard skipped: " + e.Message); }
            Logger.LogInfo("Gameplay fixes ready: crystal neutralization recovery, terrain support guard, and door demolition side approach. Host required.");
            // 0.3.100: a switch left false in the config silently disables a fix (the doors did not break for days because of one) - print every switch so it is visible at the first glance
            string sw = "Fix switches: DoorDemolitionSideApproach=" + doorFix.Value + ", CaptureWardRecoversNeutralization=" + captureFix.Value + ", KeepStructureTerrainSupports=" + supportFix.Value + ", ExplosionsFellTrees=" + treeBlastFix.Value + ", DeferEarlyTreeCut=" + deferTreeCut.Value + ", WorkerCarryPerResource=" + carryAmount.Value + ", HideEditModeBoxSelectionNotice=" + hideBoxNotice.Value;
            Logger.LogInfo(sw);
            if (!doorFix.Value || !captureFix.Value || !supportFix.Value || !treeBlastFix.Value || !deferTreeCut.Value) Logger.LogWarning("A gameplay fix is switched OFF in the config (see the line above); the behaviour of the vanilla game applies for it.");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf(); DoorDemolitionFix.Clear(); DoorAttackFix.Clear(); TreeBlastFix.Clear(); TreeCutGuard.Clear(); CarryCapacityFix.Clear(); QuarryPickLog.Clear(); PathFailLog.Clear(); StuckUnitRescue.Clear(); StockAcceptFill.Clear(); HideEditModeNotice.Clear(); ZoneSizeFix.Clear(); SentinelTargets.Clear(); instance = null;
            Logger.LogWarning("Gameplay fixes skipped; original behavior retained: " + e.Message);
        }
    }
    void Patch(Type type, string name, string before, string after, string finalizer)
    {
        MethodInfo target = AccessTools.Method(type, name);
        if (target == null) throw new MissingMethodException(type.Name, name);
        harmony.Patch(target, Hook(before), Hook(after), null, Hook(finalizer), null);
    }
    static HarmonyMethod Hook(string name) { return name == null ? null : new HarmonyMethod(AccessTools.Method(typeof(CastleStoryGameplayFixes), name)); }
    void Note(string text) { if (++notes <= 300) CastleStorySteam.SteamBridgePlugin.PtLog("GAMEPLAY-FIX " + text); }
    static void BeforeClock(CaptureClock __instance, out CaptureClock __state) { __state = clock; clock = __instance; }
    static Exception AfterClock(Exception __exception, CaptureClock __state) { clock = __state; return __exception; }
    static void BeforeProgress(CaptureProgress __instance, ref int inc)
    {
        try
        {
            if (instance == null || !instance.captureFix.Value || !NetworkServer.active || clock == null || (CaptureProgress)progress.GetValue(clock) != __instance) return;
            object attacker = attack.GetValue(clock), defender = defense.GetValue(clock);
            Faction neutral = Faction.DefaultNeutral;
            bool owned = clock.faction != null && clock.faction != neutral;
            bool decay = (Faction)captureFaction.GetValue(attacker) == neutral;
            Faction owner = (Faction)captureFaction.GetValue(defender);
            bool protectedByOwner = owner == clock.faction && (int)capturePower.GetValue(defender) > 1;
            int adjusted = GameplayFixRules.CaptureIncrement(inc, owned, decay, protectedByOwner, __instance.current, __instance.max);
            if (adjusted != inc) { inc = adjusted; if (instance.captureNotes.Add(clock.GetInstanceID())) instance.Note("Capture repair: restoring owned crystal after friendly defender returned: " + clock.name); }
        }
        catch (Exception e) { if (instance != null) instance.Note("Capture trial unavailable: " + e.Message); }
    }
    static bool BuiltSupport(IVolume volume)
    {
        if (volume == null || !volume.Registered || volume.GameObject == null || !volume.GameObject.activeInHierarchy) return false;
        ValidatorComponent component = volume.GameObject.GetComponent<ValidatorComponent>();
        // ConcreteValidator is a plain strategy object, not a Unity component.
        return component != null && !component.IsBlueprint && component.Validator is ConcreteValidator;
    }
    static bool TerrainSupportsStructure(XYZ cell)
    {
        if (instance == null || !instance.supportFix.Value || !NetworkServer.active || Voxel.GetBlockInfo(cell).Type != BlockType.Terrain) return false;
        // Support requirements are registered at their actual support cells; do not protect nearby operation spaces.
        foreach (IVolume volume in VolumeRegistry.VolumesWithPropertyAt(cell, VPropertyMask.mainSupportReq | VPropertyMask.auxiliarySupportReq))
            if (BuiltSupport(volume)) return true;
        // Some placed stations register their footprint in the cell immediately above the ground.
        foreach (IVolume volume in VolumeRegistry.VolumesWithPropertyAt(cell + XYZ.up, VProperty.occupy))
            if (BuiltSupport(volume)) return true;
        return false;
    }
    static bool Blocked(Component goal)
    {
        try
        {
            if (goal == null) return false;
            Brix.Game.AI.Goal g = goal as Brix.Game.AI.Goal;
            if (g == null) return false;
            XYZ cell = g.voxel.position;
            bool blocked = TerrainSupportsStructure(cell);
            if (blocked)
            {
                float last;
                if (!instance.supportNotes.TryGetValue(cell, out last) || Time.realtimeSinceStartup - last > 10)
                {
                    if (instance.supportNotes.Count > 128) instance.supportNotes.Clear();
                    instance.supportNotes[cell] = Time.realtimeSinceStartup;
                    instance.Note("Support guard: terrain under completed structure protected at " + cell);
                }
            }
            return blocked;
        }
        catch (Exception e) { if (instance != null) instance.Note("Support check unavailable; original digging retained: " + e.Message); return false; }
    }
    static bool BeforeDigMatch(DigGoal __instance, ref bool __result) { if (!Blocked(__instance)) return true; __result = false; return false; }
    static bool BeforeDigDamage(DigGoal __instance) { if (!Blocked(__instance)) return true; instance.Note("Support guard: terrain dig blocked at " + __instance.voxel.position); return false; }
    static bool BeforeLandscapeMatch(LandscapeGoal __instance, ref bool __result) { if (!__instance.IsDig || !Blocked(__instance)) return true; __result = false; return false; }
    static bool BeforeLandscapeDig(LandscapeGoal __instance, ref StaticNode __result) { if (!__instance.IsDig || !Blocked(__instance)) return true; __result = Node.FailWork<Exceptions.VoxelHasBeenBuildOver>(); instance.Note("Support guard: level dig ended because terrain supports a structure at " + __instance.voxel.position); return false; }
    static bool DigWorkBlocked(DigGoal goal) { return Blocked(goal); }
    static bool LandscapeWorkBlocked(LandscapeGoal goal) { return goal != null && goal.IsDig && Blocked(goal); }
    static void AfterDigWorkNode(DigGoal __instance, ref Node __result)
    {
        if (__result == null || instance == null || !instance.supportFix.Value) return;
        // Manual orders bypass IsMatchFor. Abort the work tree through the game's normal failed-task path,
        // including when a structure is placed after work started; preserve its own cleanup/finalizers.
        __result = __result.FailWhen(SharedWorkException<Exceptions.VoxelHasBeenBuildOver>.Get).Condition(DigWorkBlocked, __instance);
    }
    static void AfterLandscapeWorkNode(LandscapeGoal __instance, ref Node __result)
    {
        if (__result == null || instance == null || !instance.supportFix.Value) return;
        __result = __result.FailWhen(SharedWorkException<Exceptions.VoxelHasBeenBuildOver>.Get).Condition(LandscapeWorkBlocked, __instance);
    }
    public void Dispose() { if (instance == this) instance = null; if (harmony != null) harmony.UnpatchSelf(); DoorDemolitionFix.Clear(); DoorAttackFix.Clear(); TreeBlastFix.Clear(); TreeCutGuard.Clear(); CarryCapacityFix.Clear(); QuarryPickLog.Clear(); PathFailLog.Clear(); StuckUnitRescue.Clear(); StockAcceptFill.Clear(); HideEditModeNotice.Clear(); ZoneSizeFix.Clear(); SentinelTargets.Clear(); }
}
