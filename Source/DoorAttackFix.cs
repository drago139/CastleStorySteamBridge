using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Legacy;
using Brix.Pathfinding;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.4.22 DOOR-ATTACK (HOST only; cfg [Fixes] DoorAttackSideApproach, default true; perf-trace tag 'GAMEPLAY-FIX Door attack:'). B-074.
// A melee order on an ENEMY door (guest wheel 'Розбити об'єкт' on a non-allied door -> CmdAttack -> AttackInfo.GoAttackDirectly, MustAttackDestination) can only strike from
// the door's native face ACCESS cells. When both faces are unreachable (cliff edge, Forest cells near trees, inside the enemy base) the units walk to the gate's END, fail
// the path 3 times and leave without a swing (wf_ed5a247e-77e). This adds the door's free bottom-row face-adjacent cells (the same test DoorDemolitionFix uses for
// demolition) as stand / strike cells for THAT order only:
//  - postfix AttackInfo.GoAttackDirectly(Labor, GameObject): remembers worker -> (AttackInfo, door) when the target is a Porte;
//  - Ready(): validity is re-checked on every use from the LIVE AttackInfo (same attacker, MustAttackDestination, DestinationTarget == door), switch, host, enemy, not invincible;
//  - DoorDemolitionFix.SideCell / bounds use Ready() as a second source (ObjectLocation.TargetReached for A* + IsAt + strike cell, search bounds +1);
//  - postfix AttackInfo.EnemyAccepter.Visit: accepts a side cell of the target door as a melee target position;
//  - postfix AttackInfo.PassThroughDoor: from a door end the strike midpoint falls into the door's other column; that column is the target itself, so the strike is allowed
//    (the door is NOT opened, PassageEngage is not called);
//  - postfix Task.OnPathFailed (log only, also with the switch off): 'Door attack: PATH-FAIL ...' when a unit attacking a door gives up (the game logs nothing for user orders);
//    the door comes from the exception's Location (the order's AttackInfo is already disposed there).
//  - AI make-way units (Profession.CanMakeWay: Corruptron, Minitron, Biftron) are not changed; registry dropped on every single-mode scene load.
// Never: allied / neutral / own doors, walking through a closed door, opening it, owner checks of CmdAttack, Forest rules, unit-vs-unit combat. Not verified in a live game (T-193).
internal static class DoorAttackFix
{
    sealed class Entry { internal Labor Worker; internal AttackInfo Info; internal GameObject Door; internal IVolume Volume; }
    static readonly Dictionary<int, Entry> combat = new Dictionary<int, Entry>();
    static Func<bool> enabled; static Action<string> note;
    static readonly HashSet<string> logged = new HashSet<string>();
    static readonly HashSet<int> described = new HashSet<int>();
    static int lines, sideAccepted, strikesAllowed, pathFails;

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> logger)
    {
        enabled = isEnabled; note = logger;
        MethodInfo go = AccessTools.Method(typeof(AttackInfo), "GoAttackDirectly", new Type[] { typeof(Labor), typeof(GameObject) });
        MethodInfo visit = AccessTools.Method(typeof(AttackInfo.EnemyAccepter), "Visit");
        MethodInfo pass = AccessTools.Method(typeof(AttackInfo), "PassThroughDoor");
        MethodInfo fail = AccessTools.Method(typeof(Task), "OnPathFailed");
        if (go == null) throw new MissingMethodException("AttackInfo.GoAttackDirectly");
        if (visit == null) throw new MissingMethodException("AttackInfo.EnemyAccepter.Visit");
        if (pass == null) throw new MissingMethodException("AttackInfo.PassThroughDoor");
        harmony.Patch(go, null, H("AfterGoAttackDirectly"), null, null, null);
        harmony.Patch(visit, null, H("AfterVisit"), null, null, null);
        harmony.Patch(pass, null, H("AfterPassThroughDoor"), null, null, null);
        if (fail != null) harmony.Patch(fail, null, H("AfterPathFailed"), null, null, null);
        else Note("Task.OnPathFailed not found: no PATH-FAIL lines for door attacks");
        DoorDemolitionFix.CombatReady = Ready;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded; UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
    }
    static HarmonyMethod H(string name) { return new HarmonyMethod(AccessTools.Method(typeof(DoorAttackFix), name)); }

    static void Note(string text)
    {
        if (note == null || !DoorAttackRules.WantLog(lines) || !logged.Add(text)) return;
        lines++; note("Door attack: " + text);
    }

    internal static void Clear()
    {
        combat.Clear(); logged.Clear(); described.Clear(); lines = 0; sideAccepted = 0; strikesAllowed = 0; pathFails = 0;
        DoorDemolitionFix.CombatReady = null;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    static bool On() { return enabled != null && enabled() && NetworkServer.active; }

    // registers on the host whatever the switch says (Ready() gates the behaviour on the switch), so PATH-FAIL lines also show the vanilla failure with the switch off
    static void AfterGoAttackDirectly(AttackInfo __instance, Labor labor, GameObject target)
    {
        try
        {
            if (__instance == null || labor == null || target == null || !NetworkServer.active) return;
            Porte door = target.GetComponent<Porte>();
            if (door == null) return;
            IVolume volume = target.GetComponent<IVolume>();
            if (volume == null) return;
            // review 2026-11-08: AI units that break their way through (Corruptron, Minitron, Biftron: Profession.CanMakeWay) reach GoAttackDirectly via NavigationNodes.BreakObject; not changed
            if (labor.Profession != null && labor.Profession.CanMakeWay()) { Note("make-way AI unit " + labor.name + " on door " + target.name + ": not changed"); return; }
            bool enemy = Brix.Game.Affiliation.IsEnemy(labor.faction, volume.Faction);
            if (!enemy) { Note("order on a non-enemy door " + target.name + " by " + labor.name + ": not changed (allied / neutral / own)"); return; }
            if (combat.Count >= DoorAttackRules.RegistryCap) Prune();
            combat[labor.gameObject.GetInstanceID()] = new Entry { Worker = labor, Info = __instance, Door = target, Volume = volume };
            bool on = enabled != null && enabled();
            Note("enemy door " + target.name + " #" + target.GetInstanceID() + " ordered: " + labor.name + " (" + labor.Occupation.CurrentOccupation + ") " + (on ? "may stand on free side cells" : "switch OFF: game behaviour (logged only)"));
            Describe(labor, target, volume);
        }
        catch (Exception e) { Note("register failed: " + e.GetType().Name + " " + e.Message); }
    }

    // a new map: drop the old orders (keeps the hook wiring, unlike Clear)
    static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        if (mode == UnityEngine.SceneManagement.LoadSceneMode.Single) { combat.Clear(); described.Clear(); }
    }

    static void Prune()
    {
        List<int> dead = new List<int>();
        foreach (KeyValuePair<int, Entry> p in combat)
        {
            Entry e = p.Value;
            if (e.Worker == null || e.Door == null || e.Info == null || e.Info.DestinationTarget != e.Door) dead.Add(p.Key);
        }
        foreach (int k in dead) combat.Remove(k);
        if (combat.Count >= DoorAttackRules.RegistryCap) combat.Clear();
    }

    // the only gate every hook uses; cheap exit when nobody attacks a door
    internal static bool Ready(GameObject worker, GameObject target)
    {
        if (combat.Count == 0 || worker == null || target == null) return false;
        Entry e;
        if (!combat.TryGetValue(worker.GetInstanceID(), out e)) return false;
        if (!object.ReferenceEquals(e.Door, target) || e.Door == null) return false;
        AttackInfo a = e.Info;
        if (a == null || e.Worker == null) return false;
        IVolume v = e.Volume;
        IDamageReceiver d = target.GetComponent<IDamageReceiver>();
        bool must = a.AttackDestination == AttackInfo.AttackDestinationBehaviour.MustAttackDestination;
        return DoorAttackRules.Allowed(On(), true, v != null && v.Registered, v != null && Brix.Game.Affiliation.IsEnemy(e.Worker.faction, v.Faction),
            d == null || d.IsInvincible, must, a.DestinationTarget == target, a.Attacker == e.Worker);
    }

    static void AfterVisit(AttackInfo.EnemyAccepter __instance, PathLink link, ref bool __result)
    {
        if (__result || combat.Count == 0 || __instance == null || link == null) return;
        try
        {
            AttackInfo a = __instance.Attack;
            if (a == null) return;
            GameObject door = a.DestinationTarget, me = a.GameObject;
            if (door == null || me == null || !Ready(me, door)) return;
            XYZ at = link.head.Node.RepresentativePosition;
            if (!DoorDemolitionFix.SideCellFor(me, door, at)) return;
            __instance.Enemy = combat[me.GetInstanceID()].Volume;
            __instance.Result = link;
            __result = true;
            sideAccepted++;
            if (sideAccepted <= 10 || sideAccepted % 100 == 0) Note("side cell " + at + " accepted as melee position for " + door.name + " #" + door.GetInstanceID() + " (accepted " + sideAccepted + ")");
        }
        catch (Exception) { }
    }

    static void AfterPassThroughDoor(AttackInfo __instance, XYZ at, ref bool __result)
    {
        if (__result || combat.Count == 0 || __instance == null) return;
        try
        {
            GameObject t = __instance.MeleeTarget, me = __instance.GameObject;
            if (t == null || me == null) return;
            bool ready = Ready(me, t);
            if (!ready) return;
            IVolume v = combat[me.GetInstanceID()].Volume;
            if (!DoorAttackRules.StrikeThroughOwnTarget(ready, v != null && v.HasPropertyAt(VProperty.occupy, at))) return;
            __result = true;
            strikesAllowed++;
            if (strikesAllowed <= 10 || strikesAllowed % 50 == 0) Note("strike from the side of " + t.name + " #" + t.GetInstanceID() + " allowed (midpoint " + at + " is the door itself; strikes " + strikesAllowed + ")");
        }
        catch (Exception) { }
    }

    // log only: a unit with an attack order on a door gave up its path (also with the switch off, so the vanilla failure is visible).
    // Review 2026-11-08: the order's AttackInfo is already disposed here (its Finally runs before the Task catcher), so the door comes from the exception's Location
    // (the Destination passed to FailPathTo, as PathFailLog reads it) and 'registered' from the registry entry, not from Ready().
    static void AfterPathFailed(Labor worker, Exceptions.PathException e)
    {
        try
        {
            if (worker == null || e == null || e.Location == null || !NetworkServer.active) return;
            GameObject door = e.Location.Object;
            if (door == null || door.GetComponent<Porte>() == null) return;
            int key = worker.gameObject.GetInstanceID();
            Entry entry; bool registered = combat.TryGetValue(key, out entry) && object.ReferenceEquals(entry.Door, door);
            if (registered) combat.Remove(key);
            pathFails++;
            if (pathFails > 200) return;
            Note("PATH-FAIL " + worker.name + " (" + worker.Occupation.CurrentOccupation + ") -> " + door.name + " #" + door.GetInstanceID() + " at " + worker.voxel.position + " (" + e.GetType().Name + "), switch=" + (enabled != null && enabled()) + ", enemy-door order registered=" + registered + " #" + pathFails);
        }
        catch (Exception) { }
    }

    static void Describe(Labor worker, GameObject door, IVolume volume)
    {
        if (note == null || !described.Add(door.GetInstanceID())) return;
        if (described.Count > 64) described.Clear();
        int bottom = int.MaxValue;
        foreach (XYZ c in volume.GetCases(VProperty.occupy)) if (c.y < bottom) bottom = c.y;
        HashSet<XYZ> cells = new HashSet<XYZ>();
        XYZ[] dirs = { new XYZ(1, 0, 0), new XYZ(-1, 0, 0), new XYZ(0, 0, 1), new XYZ(0, 0, -1) };
        foreach (XYZ c in volume.GetCases(VProperty.occupy)) if (c.y == bottom) foreach (XYZ d in dirs) if (!volume.HasPropertyAt(VProperty.occupy, c + d)) cells.Add(c + d);
        string text = "side candidates of " + door.name + " #" + door.GetInstanceID() + " bottom=" + bottom + " worker " + worker.name + " at " + worker.voxel.position + ":";
        foreach (XYZ c in cells)
            text += " [" + c + " access=" + volume.HasPropertyAt(VProperty.access, c) + " open=" + Voxel.IsOpenAndWalkable(c) + " blocker=" + SearchPathRequest.HasBlockerAt(worker.gameObject, c) + " side=" + DoorDemolitionFix.SideGeometry(worker.gameObject, volume, c) + "]";
        if (note != null && DoorAttackRules.WantLog(lines)) { lines++; note("Door attack: " + text); }
    }
}
