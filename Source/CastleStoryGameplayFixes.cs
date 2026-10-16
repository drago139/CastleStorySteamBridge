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
    ConfigEntry<bool> captureFix, supportFix, doorFix;
    static readonly FieldInfo attack = AccessTools.Field(typeof(CaptureClock), "attack");
    static readonly FieldInfo defense = AccessTools.Field(typeof(CaptureClock), "defense");
    static readonly FieldInfo progress = AccessTools.Field(typeof(CaptureClock), "capture");
    static FieldInfo captureFaction, capturePower;
    [ThreadStatic] static CaptureClock clock;
    int notes;
    readonly HashSet<int> captureNotes = new HashSet<int>();
    readonly Dictionary<XYZ, float> supportNotes = new Dictionary<XYZ, float>();
    readonly ConfigFile Config; readonly BepInEx.Logging.ManualLogSource Logger;
    internal CastleStoryGameplayFixes(ConfigFile config, BepInEx.Logging.ManualLogSource logger)
    {
        Config = config; Logger = logger;
        captureFix = Config.Bind("Fixes", "CaptureWardRecoversNeutralization", true, "When friendly defenders return to an owned crystal, unfinished natural neutralization returns to zero instead of remaining stuck. Enemy contention stays unchanged.");
        supportFix = Config.Bind("Fixes", "KeepStructureTerrainSupports", true, "Reject digging/level digging of terrain used as support by a completed structure. Does not affect filling, combat or ordinary free terrain.");
        doorFix = Config.Bind("Fixes", "DoorDemolitionSideApproach", true, "Allow one assigned worker per door demolition goal, using free adjacent side cells when front/back access is blocked. Does not allow attacks through solid cells or change general door passage.");
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
            DoorDemolitionFix.Install(harmony, delegate { return doorFix.Value; }, Note);
            Logger.LogInfo("Gameplay fixes ready: crystal neutralization recovery, terrain support guard, and door demolition side approach. Host required.");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf(); DoorDemolitionFix.Clear(); instance = null;
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
    void Note(string text) { }
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
    public void Dispose() { if (instance == this) instance = null; if (harmony != null) harmony.UnpatchSelf(); DoorDemolitionFix.Clear(); }
}
