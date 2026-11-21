using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.AI.Nodes;
using CastleStorySteam;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

// BUG PACK part 7: STRIKE COOLDOWN GUARD (bug-intake, Standalone-0.4.13). User 2026-11-06: a spearman hits faster with "strike, cancel, strike, cancel"; the cancel is a NEW ORDER
// (select, Shift, right click). Checked on the REAL Assembly-CSharp-firstpass.dll (ilspycmd + a Cecil call scan; recovered sources only for orientation): the game keeps the melee
// cooldown in the ORDER, not in the unit.
//   A) Unit against unit. Every attack order (Labor.GoAttack*, HoldPositionAndAttack*, MoveAttack, WalkMoveAttack, the danger instincts) runs on the unit's one AttackInfo
//      (Labor.DebugAttack) and starts with AttackInfo.Reset -> ResetObject, which sets CurrentFrame = 0 and NextAttackFrame = 0. AttackInfo.DoMeleeAction strikes when
//      IsReadyToStrike && CurrentFrame >= NextAttackFrame; AttackInfo.Strike calls Locomotion4.DoStrike and sets NextAttackFrame = CurrentFrame + RandomizedAttackCooldown
//      (Profession.AttackCooldown 75 ticks +- AttackCooldownRandomness, shorter with level). So after a new order the next strike comes as soon as the unit is ready.
//   B) BreakObject (Order.Combat.BreakObject: doors, and BuildGoal.AntiBlueprintNode: things in the way of a blueprint). CombatNodes.AttackLoop = Profession.Strike(target)
//      (Wield, StopBlocking, wait IsReadyToStrike max 1 s, DoStrike, wait until the attack is Finished, max 100 ticks) then WaitForTicks(RandomizedAttackCooldown), looped.
//      A new order builds a new loop, which strikes at once.
// The guard ([Fixes] StrikeCooldownGuard, default OFF; HOST only: the AI and the combat run on the host, guests need nothing) keeps ONE stamp per unit (GameObject id): when
// the cooldown that counts started and how long it is. The FIRST strike after a new order waits until factor x that cooldown has passed:
//   A: prefix on the private AttackInfo.Strike(), only while NextAttackFrame == 0 (no strike yet in this order). If the unit must still wait, the game's own per-order cooldown
//      is set to the rest (NextAttackFrame = CurrentFrame + ticks) and this attempt is skipped; the AI then waits, steps and turns exactly as it does between two strikes of one
//      order. Postfix: stamp (now, NextAttackFrame - CurrentFrame ticks) = exactly the cooldown the game chose for this strike. Within one order nothing changes.
//   B: postfix on Profession.Strike(IDamageReceiver, Vector3) (melee units, switch on): the strike node becomes [wait the rest] -> the game's own node -> stamp (now, the
//      shortest cooldown the game can roll) when that node completed. The game's loop waits at least that long after every strike, so a fight without new orders never waits.
// It never makes Locomotion4.DoStrike return null and never touches ranged attacks (AttackInfo.DoRangedAction, ProjectileWeapon, AlchemistBoost).
// One AI tick = one FixedUpdate: GameClock.FixedUpdate -> Behavior.BrixFixedUpdate -> Tick; a Loop runs one pass per tick, so CurrentFrame counts ticks; times here are
// Time.fixedTime seconds (game time, stops while paused).
// perf-trace.txt tag STRIKE-GUARD: one "hold" line for each of the first 20 held-back strikes (then every 100th), one summary line per minute of game time that had melee strikes
// or hits (also while the switch is off, so a live test can compare off and on; hits = MeleeAttack.Apply calls, counted only, never changed), and the first 5 hook errors.
// A hook that throws is caught: the game's own behaviour then applies for that call.
internal static class StrikeGuard
{
    static Func<bool> enabled = delegate { return false; };
    static Func<float> factorCfg = delegate { return 1f; };
    static Action<string> log = delegate(string s) { };
    internal static readonly StrikeStamps Stamps = new StrikeStamps();
    static double windowStart = -1.0, maxWait;
    static int strikesA, strikesB, holdsA, holdsB, hits, holdsTotal, faults, faultsInWindow;
    static readonly Dictionary<int, int> hitsByUnit = new Dictionary<int, int>();
    static readonly Dictionary<int, string> jobByUnit = new Dictionary<int, string>();

    internal static void Install(Harmony harmony, Func<bool> on, Func<float> factor, Action<string> logger)
    {
        MethodInfo strikeA = AccessTools.Method(typeof(AttackInfo), "Strike", Type.EmptyTypes);
        MethodInfo strikeB = AccessTools.Method(typeof(Profession), "Strike", new Type[] { typeof(IDamageReceiver), typeof(Vector3) });
        MethodInfo apply = AccessTools.Method(typeof(MeleeAttack), "Apply", new Type[] { typeof(IDamageReceiver) });
        if (strikeA == null) throw new MissingMethodException("Brix.Game.AI.Nodes.AttackInfo", "Strike()");
        if (strikeB == null) throw new MissingMethodException("Brix.Game.AI.Profession", "Strike(IDamageReceiver, Vector3)");
        if (apply == null) throw new MissingMethodException("Brix.Game.AI.MeleeAttack", "Apply(IDamageReceiver)");
        if (on != null) enabled = on;
        if (factor != null) factorCfg = factor;
        if (logger != null) log = logger;
        harmony.Patch(strikeA, new HarmonyMethod(AccessTools.Method(typeof(StrikeGuard), "BeforeStrikeA")), new HarmonyMethod(AccessTools.Method(typeof(StrikeGuard), "AfterStrikeA")), null, null, null);
        harmony.Patch(strikeB, null, new HarmonyMethod(AccessTools.Method(typeof(StrikeGuard), "AfterStrikeB")), null, null, null);
        harmony.Patch(apply, null, new HarmonyMethod(AccessTools.Method(typeof(StrikeGuard), "AfterApply")), null, null, null);
        bool on0 = false; float f0 = 1f;
        try { on0 = enabled(); f0 = factorCfg(); } catch (Exception) { }
        log("STRIKE-GUARD installed: switch " + (on0 ? "ON" : "off") + ", factor " + BugPackRules.StrikeFactor(f0).ToString("0.##") + " (host only; AttackInfo.Strike, Profession.Strike, MeleeAttack.Apply)");
    }

    internal static void Clear()
    {
        Stamps.Clear();
        windowStart = -1.0; maxWait = 0.0;
        strikesA = strikesB = holdsA = holdsB = hits = holdsTotal = faults = faultsInWindow = 0;
        hitsByUnit.Clear(); jobByUnit.Clear();
    }

    static bool IsHost() { return NetworkServer.active; }

    // ---- A: unit against unit (AttackInfo) --------------------------------------------------------------------------------------------------------------------------

    // Prefix on AttackInfo.Strike(): false = this strike attempt is skipped (the game's own cooldown now holds the unit). __state tells the postfix that no strike happened.
    static bool BeforeStrikeA(AttackInfo __instance, out bool __state)
    {
        __state = false;
        try
        {
            if (__instance == null || __instance.NextAttackFrame != 0 || !IsHost() || !enabled()) return true;
            Locomotion4 loco = __instance.Locomotion;
            if (loco == null) return true;
            GameObject unit = loco.gameObject;
            int id = unit.GetInstanceID();
            double now = Time.fixedTime, wait;
            if (!HoldFirstStrike(__instance, id, now, Time.fixedDeltaTime, BugPackRules.StrikeFactor(factorCfg()), out wait)) return true;
            Hold("unit", id, unit, wait, now);
            __state = true;
            return false;
        }
        catch (Exception e) { Fault("unit gate", e); return true; }
    }

    // The core of gate A without Unity calls (the hook test runs it on a real AttackInfo): true = held back, info.NextAttackFrame then holds the remaining ticks.
    internal static bool HoldFirstStrike(AttackInfo info, int unitId, double now, double tickSeconds, double factor, out double wait)
    {
        wait = 0.0;
        if (info == null || info.NextAttackFrame != 0) return false;
        double w = Stamps.Remaining(unitId, now, factor, BugPackRules.StrikeMarginTicks * tickSeconds);
        int ticks = BugPackRules.StrikeWaitTicks(w, tickSeconds);
        if (ticks <= 0) return false;
        info.NextAttackFrame = info.CurrentFrame + ticks;
        wait = w;
        return true;
    }

    // Postfix on AttackInfo.Strike(): the strike was made; stamp the cooldown the game just chose for it.
    static void AfterStrikeA(AttackInfo __instance, bool __state)
    {
        if (__state) return;
        try
        {
            if (__instance == null || !IsHost()) return;
            Locomotion4 loco = __instance.Locomotion;
            if (loco == null) return;
            double now = Time.fixedTime;
            Window(now);
            StampStrikeA(__instance, loco.gameObject.GetInstanceID(), now, Time.fixedDeltaTime);
            strikesA++;
        }
        catch (Exception e) { Fault("unit stamp", e); }
    }

    internal static void StampStrikeA(AttackInfo info, int unitId, double now, double tickSeconds)
    {
        Stamps.Stamp(unitId, now, Math.Max(0, info.NextAttackFrame - info.CurrentFrame) * tickSeconds);
    }

    // ---- B: BreakObject (Profession.Strike) ---------------------------------------------------------------------------------------------------------------------

    // Postfix on Profession.Strike(IDamageReceiver, Vector3): with the switch on, a melee unit's strike node is wrapped; with it off the game's node is left as it is.
    static void AfterStrikeB(Profession __instance, ref Node __result)
    {
        try
        {
            if (__result == null || __instance == null || !IsHost() || !enabled() || !__instance.HasMeleeAttack()) return;
            Profession prof = __instance;
            GameObject unit = prof.gameObject;
            int id = unit.GetInstanceID();
            __result = Wrap(delegate { return GateNode(id, unit); }, __result, delegate { StampStrikeB(prof, id); });
        }
        catch (Exception e) { Fault("object wrap", e); }
    }

    // [gate] -> [the game's node] -> [stamp] with the game's own sequence node; the gate and the game's node are dynamic nodes, so the gate is decided when it runs.
    // If the game's node fails (no melee weapon, target gone, not ready within 1 s) the stamp step is not reached: only completed strikes count.
    internal static StaticNode Wrap(Func<Node> gate, Node game, Action stamp)
    {
        Func<Node> original = delegate { return game; };
        return Node.Sequence.Do(Node.Do(gate)).Do(Node.Do(original)).Do(stamp);
    }

    static Node GateNode(int id, GameObject unit)
    {
        try
        {
            if (!enabled()) return Node.Empty;
            double now = Time.fixedTime, wait;
            Node n = GateNodeAt(id, now, Time.fixedDeltaTime, BugPackRules.StrikeFactor(factorCfg()), out wait);
            if (wait > 0.0) Hold("object", id, unit, wait, now);
            return n;
        }
        catch (Exception e) { Fault("object gate", e); return Node.Empty; }
    }

    // The core of gate B without Unity calls: Node.Empty (strike now) or Node.WaitForTicks(the rest of the cooldown).
    internal static Node GateNodeAt(int unitId, double now, double tickSeconds, double factor, out double wait)
    {
        wait = 0.0;
        double w = Stamps.Remaining(unitId, now, factor, BugPackRules.StrikeMarginTicks * tickSeconds);
        int ticks = BugPackRules.StrikeWaitTicks(w, tickSeconds);
        if (ticks <= 0) return Node.Empty;
        wait = w;
        return Node.WaitForTicks(ticks);
    }

    static void StampStrikeB(Profession prof, int id)
    {
        try
        {
            if (prof == null || !IsHost()) return;
            double now = Time.fixedTime;
            Window(now);
            Stamps.Stamp(id, now, BugPackRules.StrikeMinCooldownTicks(prof.AttackCooldown, prof.AttackCooldownRandomness) * (double)Time.fixedDeltaTime);
            strikesB++;
        }
        catch (Exception e) { Fault("object stamp", e); }
    }

    // ---- diagnostics ------------------------------------------------------------------------------------------------------------------------------------------------

    // Postfix on MeleeAttack.Apply (called by Striking.StartReacting when a swing lands): counted per attacker for the summary line, nothing is changed.
    static void AfterApply(MeleeAttack __instance)
    {
        try
        {
            if (__instance == null || !IsHost()) return;
            Window(Time.fixedTime);
            hits++;
            GameObject attacker = __instance.Attacker;
            if (attacker == null) return;
            int id = attacker.GetInstanceID(), n;
            if (!hitsByUnit.TryGetValue(id, out n) && hitsByUnit.Count >= BugPackRules.MaxHitUnits) return;
            hitsByUnit[id] = n + 1;
            if (!jobByUnit.ContainsKey(id)) jobByUnit[id] = JobName(attacker);
        }
        catch (Exception e) { Fault("hit count", e); }
    }

    static void Hold(string path, int id, GameObject unit, double wait, double now)
    {
        Window(now);
        holdsTotal++;
        if (path == "unit") holdsA++; else holdsB++;
        if (wait > maxWait) maxWait = wait;
        if (!BugPackRules.StrikeLogDelay(holdsTotal)) return;
        double anchor, cooldown;
        Stamps.TryGet(id, out anchor, out cooldown);
        log("STRIKE-GUARD hold #" + holdsTotal + " " + path + " " + JobName(unit) + "#" + id + " wait " + Ms(wait) + "ms (cd " + Ms(cooldown) + "ms, last strike " + Ms(now - anchor)
            + "ms ago, x" + BugPackRules.StrikeFactor(factorCfg()).ToString("0.##") + ") t=" + now.ToString("0.0"));
    }

    // One line per minute of game time that had melee strikes, hits or holds; written by the first event after the minute or by the 2 s ticker of CombatAnomaly.
    static void Window(double now)
    {
        int action = BugPackRules.StrikeWindowAction(now, windowStart);
        if (action == 0) return;
        if (action > 0) Summary(now, "");
        Restart(now);
    }

    static void Summary(double now, string note)
    {
        if (strikesA + strikesB + hits + holdsA + holdsB <= 0) return;
        bool on = false;
        try { on = enabled(); } catch (Exception) { }
        log("STRIKE-GUARD minute " + (now - windowStart).ToString("0.0") + "s" + note + ": guard " + (on ? "ON" : "off") + " x" + BugPackRules.StrikeFactor(factorCfg()).ToString("0.##")
            + "; strikes " + (strikesA + strikesB) + " (unit " + strikesA + " object " + strikesB + ") hits " + hits + "; held " + (holdsA + holdsB) + " (unit " + holdsA + " object " + holdsB
            + ") max " + Ms(maxWait) + "ms; stamped " + Stamps.StampedCount + "; err " + faultsInWindow + "; top " + Top(3));
    }

    static void Restart(double now)
    {
        windowStart = now; maxWait = 0.0;
        strikesA = strikesB = holdsA = holdsB = hits = faultsInWindow = 0;
        hitsByUnit.Clear(); jobByUnit.Clear();
    }

    // From CombatAnomaly's 2 s ticker: write a finished window even if no event follows it (the guard's clock is Time.fixedTime).
    internal static void Tick()
    {
        try
        {
            if (windowStart < 0.0) return;
            double now = Time.fixedTime;
            if (BugPackRules.StrikeWindowAction(now, windowStart) == 1) Window(now);
        }
        catch (Exception) { }
    }

    // When the game quits: write the partial window with its real span.
    internal static void Flush(string why)
    {
        try
        {
            if (windowStart < 0.0) return;
            double now = Time.fixedTime;
            Summary(now, " partial (" + why + ")");
            Restart(now);
        }
        catch (Exception) { }
    }

    static string Top(int count)
    {
        List<KeyValuePair<int, int>> list = new List<KeyValuePair<int, int>>(hitsByUnit);
        list.Sort(delegate(KeyValuePair<int, int> x, KeyValuePair<int, int> y) { return y.Value.CompareTo(x.Value); });
        if (list.Count == 0) return "none";
        string s = "";
        for (int i = 0; i < list.Count && i < count; i++)
        {
            string job;
            if (!jobByUnit.TryGetValue(list[i].Key, out job)) job = "unit";
            s += (i > 0 ? " " : "") + job + "#" + list[i].Key + "=" + list[i].Value;
        }
        return s;
    }

    static string JobName(GameObject go) { return CombatAnomaly.JobName(go); }   // job or enemy type, never the unit's name

    static string Ms(double seconds) { return ((long)Math.Round(seconds * 1000.0)).ToString(); }

    static void Fault(string where, Exception e)
    {
        faults++; faultsInWindow++;
        if (faults <= 5) log("STRIKE-GUARD error in " + where + " (the game's own behaviour applies there): " + e.GetType().Name + ": " + e.Message);
    }
}
