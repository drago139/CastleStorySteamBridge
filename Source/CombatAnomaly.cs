using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Locomotion;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// BUG PACK part 8: COMBAT ANOMALY LOG (bug-intake, Standalone-0.4.13). User 2026-11-06 via the main/map session: "додати в логі, якщо щось подібне буде, і якщо допустим бомбер
// кидає далі ніж повинен, тоді вже будемо розбиратися чому по логах, а не вигадувати". LOG ONLY: [Diagnostics] CombatAnomalyLog (default true) only decides whether lines are
// written; nothing in the game is changed. HOST only (the locomotion FSM, the archery FSM and the AI exist only where NetworkServer.active; checked on the real DLL:
// Locomotion4.CreateFSM is a ServerCallback). Every hook is wrapped in try / catch (first 5 errors logged, all counted); melee, ranged and the ticker install on their own.
//   MELEE-FAST : a unit STARTS a melee strike (Striking.Strike = the game's real start, run inside Locomotion4.DoStrike for unit fights and BreakObject alike) sooner after its
//                previous start than min(0.8 x Profession.AttackCooldown, AttackCooldown - AttackCooldownRandomness) whole ticks (never below what the game itself can roll).
//                The line says how the previous strike ended (hit = MeleeAttack.Apply: hit or blocked, nohit = StartReacting without a hit, abort = StartAbortingStrike, none =
//                cut or still running) and whether the unit's CURRENT TASK started after the previous strike (Task.TaskStartedTimestamp, Time.time when the task's work began)
//                and who gave it (Task.InitialPackage.DecisionSource: player / own = the unit's instinct / project). A new task resets the game's per-order cooldown (B-075).
//   RANGED-FAR : a unit launches (ProjectileWeapon.Fire, the one launch call of every unit shot or throw: archer, arbalist, alchemist, artificer and the enemy shooters) at a
//                target farther than ProjectileWeapon.MaxRange x 1.05, measured the game's way (horizontal distance from the weapon to targetLocation.Position, as in
//                CanFireAt -> IsInRangeToShoot). The game re-checks that range in the same call just before Fire, so in the unmodified game this never trips.
//   RANGED-FAST: two launches of one unit come sooner than 0.8 x the interval the game set after the first one (Profession.JobRangedCooldown: archer 0.6 s, arbalist 1.0 s,
//                others 0.8 s; read through AlchemistBoost's getter postfix, quietly, so the boost is not an alarm). The animation alone keeps vanilla above that line.
//                Ranged intervals use Time.time like the game's own Archery.CooldownEndTime; a new order does NOT reset the ranged cooldown (it lives in the archery FSM).
// The per-unit record is the one StrikeGuard uses (StrikeGuard.Stamps): one source of truth for "the last strike" of a unit.
// Budget: the first 50 events in full, then at most one per unit per 10 s and at most 10 per summary window (the rest only counted). One COMBAT-ANOM minute line per game minute
// that had melee strike starts or ranged launches, zeros included (a quiet fight shows 0), written by a 2 s ticker (hidden object "SteamBridge CombatAnomaly") and, partial,
// when the game quits. Lines are short (the friend report cuts lines at 300 characters) and go to perf-trace.txt (PtLog -> the friend report filter ssKeep of SessionShare,
// which keeps at most 20 detail lines per report; also the DiagLiveLines ring, which only the disabled diagnostics report reads) and to LogOutput.log. No personal data: a unit
// is written as its job (or enemy type) + instance id, never its name (names come from Info\names.json, saves and mods); other objects by a plain-character name (SafeName).
internal static class CombatAnomaly
{
    static Func<bool> enabled = delegate { return false; };
    static Func<bool> guardOn = delegate { return false; };
    static Func<string> boostNote = delegate { return ""; };
    static Action<string> ptLog = delegate(string s) { };
    static Action<string> outLog = delegate(string s) { };
    static readonly AnomLimiter limiter = new AnomLimiter();
    static GameObject ticker;
    static double windowStart = -1.0, maxRangeRatio;
    static int meleeStarts, meleeFast, fastPlayerTask, fastOwnTask, fastSameTask, rangedLaunches, rangedFar, rangedFast, events, faults, faultsInWindow;

    static StrikeStamps Units { get { return StrikeGuard.Stamps; } }

    // MOD-INV (0.4.14): the install result of the three parts, read by ModInventory's self-off line.
    internal static string InstallStatus = "";

    internal static void Install(Harmony harmony, Func<bool> on, Func<bool> guard, Func<string> boost, Action<string> log, Action<string> logOutput)
    {
        if (on != null) enabled = on;
        if (guard != null) guardOn = guard;
        if (boost != null) boostNote = boost;
        if (log != null) ptLog = log;
        if (logOutput != null) outLog = logOutput;
        string melee = Part(delegate { InstallMelee(harmony); });
        string ranged = Part(delegate { InstallRanged(harmony); });
        string tick = Part(InstallTicker);
        bool logOn = false;
        try { logOn = enabled(); } catch (Exception) { }
        InstallStatus = "melee " + melee + ", ranged " + ranged + ", ticker " + tick;
        Write("COMBAT-ANOM installed: " + InstallStatus + "; log " + (logOn ? "on" : "off") + ", host only");
        if (melee != "ok" && ranged != "ok") throw new InvalidOperationException("no part of the combat anomaly log could be installed");
    }

    static string Part(Action install)
    {
        try { install(); return "ok"; }
        catch (Exception e) { return "SKIPPED (" + e.GetType().Name + ": " + e.Message + ")"; }
    }

    static void InstallMelee(Harmony harmony)
    {
        MethodInfo strike = AccessTools.Method(typeof(Striking), "Strike", new Type[] { typeof(IDamageReceiver), typeof(Vector3) });
        MethodInfo reacting = AccessTools.Method(typeof(Striking), "StartReacting", Type.EmptyTypes);
        MethodInfo abort = AccessTools.Method(typeof(Striking), "StartAbortingStrike", Type.EmptyTypes);
        MethodInfo apply = AccessTools.Method(typeof(MeleeAttack), "Apply", new Type[] { typeof(IDamageReceiver) });
        if (strike == null) throw new MissingMethodException("Brix.Game.Locomotion.Striking", "Strike(IDamageReceiver, Vector3)");
        if (reacting == null) throw new MissingMethodException("Brix.Game.Locomotion.Striking", "StartReacting()");
        if (abort == null) throw new MissingMethodException("Brix.Game.Locomotion.Striking", "StartAbortingStrike()");
        if (apply == null) throw new MissingMethodException("Brix.Game.AI.MeleeAttack", "Apply(IDamageReceiver)");
        if (AccessTools.Field(typeof(Task), "TaskStartedTimestamp") == null) throw new MissingFieldException("Brix.Game.AI.Task", "TaskStartedTimestamp");
        harmony.Patch(strike, null, new HarmonyMethod(AccessTools.Method(typeof(CombatAnomaly), "AfterStrikeStart")), null, null, null);
        harmony.Patch(apply, null, new HarmonyMethod(AccessTools.Method(typeof(CombatAnomaly), "AfterHit")), null, null, null);
        harmony.Patch(reacting, null, new HarmonyMethod(AccessTools.Method(typeof(CombatAnomaly), "AfterReacting")), null, null, null);
        harmony.Patch(abort, null, new HarmonyMethod(AccessTools.Method(typeof(CombatAnomaly), "AfterAbort")), null, null, null);
    }

    static void InstallRanged(Harmony harmony)
    {
        MethodInfo fire = AccessTools.Method(typeof(ProjectileWeapon), "Fire");
        if (fire == null) throw new MissingMethodException("Brix.Game.AI.ProjectileWeapon", "Fire");
        harmony.Patch(fire, null, new HarmonyMethod(AccessTools.Method(typeof(CombatAnomaly), "AfterLaunch")), null, null, null);
    }

    // A hidden object (like LooseCleanup's) whose Update writes the minute lines of this part and of StrikeGuard on time, and the partial ones when the game quits.
    static void InstallTicker()
    {
        if (ticker != null) return;
        ticker = new GameObject("SteamBridge CombatAnomaly");
        ticker.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(ticker);
        ticker.AddComponent<CombatAnomalyTicker>();
    }

    internal static void Clear()
    {
        limiter.Clear();
        windowStart = -1.0; maxRangeRatio = 0.0;
        meleeStarts = meleeFast = fastPlayerTask = fastOwnTask = fastSameTask = rangedLaunches = rangedFar = rangedFast = events = faults = faultsInWindow = 0;
    }

    static bool On()
    {
        try { return enabled() && NetworkServer.active; }
        catch (Exception) { return false; }
    }

    // ---- melee --------------------------------------------------------------------------------------------------------------------------------------------------

    // Postfix on Striking.Strike(IDamageReceiver strikeTarget, Vector3 endPosition): the game starts a melee strike (called inside Locomotion4.DoStrike, on an AI tick).
    // The unit comes from the non-public base field LocomotionStateUpdater.Locomotion (Harmony ___Locomotion): Striking's own gameObject getter is protected.
    static void AfterStrikeStart(Striking __instance, Locomotion4 ___Locomotion, IDamageReceiver strikeTarget)
    {
        try
        {
            if (__instance == null || ___Locomotion == null || !On()) return;
            GameObject unit = ___Locomotion.gameObject;
            if (unit == null) return;
            int id = unit.GetInstanceID();
            double now = Time.time, tick = Time.fixedDeltaTime;
            Window(now);
            meleeStarts++;
            StrikeEnd prevEnd;
            double prev = Units.NoteStrikeStart(id, now, out prevEnd);
            if (double.IsNaN(prev) || !(tick > 0.0)) return;
            Profession prof = unit.GetComponent<Profession>();
            if (prof == null) return;
            double interval = (now - prev) / tick;
            double threshold = BugPackRules.MeleeFastThresholdTicks(prof.AttackCooldown, prof.AttackCooldownRandomness);
            if (!BugPackRules.MeleeFast(interval, threshold)) return;
            meleeFast++;
            double taskStart; int source;
            bool newTask = TaskAfter(unit, prev - 0.5 * tick, out taskStart, out source);
            if (!newTask) fastSameTask++;
            else if (source == (int)Labor.DecisionSource.User) fastPlayerTask++;
            else fastOwnTask++;
            if (!limiter.Allow(id, now)) return;
            Emit("MELEE-FAST " + Who(unit, id) + "->" + Target(strikeTarget) + " gap " + Math.Round(interval).ToString("0") + "t<" + threshold.ToString("0.#") + "t (cd "
                + prof.AttackCooldown + "+-" + prof.AttackCooldownRandomness + ") prev=" + BugPackRules.StrikeEndShort(prevEnd) + " task="
                + (newTask ? "new:" + SourceShort(source) + "@" + Math.Round((now - taskStart) / tick).ToString("0") + "t" : "same") + " guard=" + (GuardOn() ? "ON" : "off")
                + " t=" + now.ToString("0.0"));
        }
        catch (Exception e) { Fault("melee start", e); }
    }

    // Did the unit's current task start after `since`? Its start time (Time.time) and who gave it.
    static bool TaskAfter(GameObject unit, double since, out double taskStart, out int source)
    {
        taskStart = double.NaN; source = -1;
        Labor labor = unit.GetComponent<Labor>();
        if (labor == null) return false;
        Task task = labor.CurrentTask;
        if (task == null) return false;
        taskStart = task.TaskStartedTimestamp;
        if (task.InitialPackage != null) source = (int)task.InitialPackage.DecisionSource;
        return taskStart >= since;
    }

    static string SourceShort(int source)
    {
        switch (source)
        {
            case (int)Labor.DecisionSource.User: return "player";
            case (int)Labor.DecisionSource.Character: return "own";
            case (int)Labor.DecisionSource.Project: return "project";
            case (int)Labor.DecisionSource.Unspecified: return "unspecified";
            default: return "?";
        }
    }

    // Postfix on MeleeAttack.Apply(IDamageReceiver target): the swing landed, hit or blocked (called by Striking.StartReacting).
    static void AfterHit(MeleeAttack __instance)
    {
        try
        {
            if (__instance == null || __instance.Attacker == null || !On()) return;
            Units.NoteStrikeEnd(__instance.Attacker.GetInstanceID(), Time.time, StrikeEnd.Hit);
        }
        catch (Exception e) { Fault("melee hit", e); }
    }

    // Postfix on Striking.StartReacting(): the strike is over; without a hit noted just before (MeleeAttack.Apply) it finished without one (the target was gone).
    static void AfterReacting(Striking __instance, Locomotion4 ___Locomotion)
    {
        try
        {
            if (__instance == null || ___Locomotion == null || !On()) return;
            GameObject unit = ___Locomotion.gameObject;
            if (unit != null) Units.NoteStrikeEnd(unit.GetInstanceID(), Time.time, StrikeEnd.NoHit);
        }
        catch (Exception e) { Fault("melee end", e); }
    }

    // Postfix on Striking.StartAbortingStrike(): the strike was aborted (FSM state AbortingStrike: target invalid or farther than 9.5 m, StopFighting after a new order, ...).
    // An abort that comes after a hit of the same strike does not count (the first end of a strike wins).
    static void AfterAbort(Striking __instance, Locomotion4 ___Locomotion)
    {
        try
        {
            if (__instance == null || ___Locomotion == null || !On()) return;
            GameObject unit = ___Locomotion.gameObject;
            if (unit != null) Units.NoteStrikeEnd(unit.GetInstanceID(), Time.time, StrikeEnd.Aborted);
        }
        catch (Exception e) { Fault("melee abort", e); }
    }

    // ---- ranged -------------------------------------------------------------------------------------------------------------------------------------------------

    // Postfix on ProjectileWeapon.Fire(origin, aimOrigin, target, source, targetLocation, thrown, ...): one unit launch (Archery.ProjectileFire, on the host). Wards also call
    // Fire (their source has no Labor) and are skipped; a launch that created no projectile (null result) is not counted.
    static void AfterLaunch(ProjectileWeapon __instance, GameObject source, Location targetLocation, ProjectileAttack __result)
    {
        try
        {
            if (__instance == null || __result == null || source == null || !On()) return;
            Labor labor = source.GetComponent<Labor>();
            if (labor == null) return;
            double now = Time.time;
            Window(now);
            rangedLaunches++;
            int id = source.GetInstanceID();
            Profession prof = labor.Profession;
            double setNow = 0.0;
            if (prof != null)
            {
                AlchemistBoost.QuietRead = true;   // the boosted value, without spending AlchemistBoost's own log lines
                try { setNow = prof.JobRangedCooldown; }
                finally { AlchemistBoost.QuietRead = false; }
            }
            double setBefore;
            double prev = Units.NoteLaunch(id, now, setNow, out setBefore);
            double range = __instance.MaxRange, dist = double.NaN;
            if (targetLocation != null)
            {
                Vector3 tp = targetLocation.Position;
                if (tp != Vector3.zero || targetLocation.Object != null)
                {
                    Vector3 d = __instance.transform.position - tp;
                    d.y = 0f;
                    dist = d.magnitude;
                }
            }
            if (!double.IsNaN(dist) && range > 0.0 && dist / range > maxRangeRatio) maxRangeRatio = dist / range;
            double interval = double.IsNaN(prev) ? double.NaN : now - prev;
            bool far = BugPackRules.RangedFar(dist, range);
            bool fast = BugPackRules.RangedFast(interval, setBefore);
            if (!far && !fast) return;
            if (far) rangedFar++;
            if (fast) rangedFast++;
            if (!limiter.Allow(id, now)) return;
            string kind = far && fast ? "RANGED-FAR+FAST" : (far ? "RANGED-FAR" : "RANGED-FAST");
            string ammo = "?";
            try { ammo = BugPackRules.SafeName(__instance.ammoAssetKey.Name); } catch (Exception) { }
            bool alchemist = labor.Occupation != null && labor.Occupation.CurrentJob == Occupation.Job.Alchemist;
            Emit(kind + " " + Who(source, id) + "->" + TargetOf(targetLocation) + " dist " + F(dist, "0.0") + (far ? ">" : "<=") + F(range * BugPackRules.AnomRangeSlack, "0.0")
                + " (max " + F(range, "0.#") + " min " + F(__instance.MinRange, "0.#") + ") gap " + (double.IsNaN(interval) ? "first" : F(interval, "0.00") + "s")
                + (fast ? "<" : ">=") + F(setBefore * BugPackRules.AnomRangedFactor, "0.00") + "s (cd " + F(setBefore, "0.##") + ") ammo " + ammo
                + (alchemist ? " " + Boost() : "") + " t=" + now.ToString("0.0"));
        }
        catch (Exception e) { Fault("ranged launch", e); }
    }

    static string TargetOf(Location target)
    {
        try
        {
            if (target == null) return "none";
            GameObject go = target.Object;
            if (go == null) return "point";
            if (go.GetComponent<Labor>() != null) return JobName(go) + "#" + go.GetInstanceID();
            return BugPackRules.SafeName(go.name) + "#" + go.GetInstanceID();
        }
        catch (Exception) { return "?"; }
    }

    // ---- shared ---------------------------------------------------------------------------------------------------------------------------------------------------

    static bool GuardOn()
    {
        try { return guardOn(); }
        catch (Exception) { return false; }
    }

    static string Who(GameObject unit, int id) { return JobName(unit) + "#" + id; }

    static string Target(IDamageReceiver target)
    {
        try
        {
            if (target == null || DamageReceiverExtender.IsNullOrReleased(target)) return "none";
            GameObject go = target.GameObject;
            if (go == null) return "none";
            if (go.GetComponent<Labor>() != null) return JobName(go) + "#" + go.GetInstanceID();
            return BugPackRules.SafeName(go.name) + "#" + go.GetInstanceID();
        }
        catch (Exception) { return "?"; }
    }

    // A unit's job (Bricktrons: Builder, Halberdier, Knight, Archer, Arbalist, Alchemist, Artificer) or, for the enemy kinds whose job stays Builder, their type
    // (Corruptron, Biftron, Minitron, Magitron, Crawler). Never the unit's name. Also used by StrikeGuard and AlchemistBoost.
    internal static string JobName(GameObject go)
    {
        try
        {
            if (go == null) return "?";
            Labor l = go.GetComponent<Labor>();
            if (l == null || l.Occupation == null) return "unit";
            Occupation.Type type = l.Occupation.CurrentOccupation;
            if (type == Occupation.Type.Corruptron || type == Occupation.Type.Biftron || type == Occupation.Type.Minitron || type == Occupation.Type.Magitron || type == Occupation.Type.Crawler)
                return type.ToString();
            return l.Occupation.CurrentJob.ToString();
        }
        catch (Exception) { return "?"; }
    }

    static string F(double v, string format) { return double.IsNaN(v) ? "?" : v.ToString(format); }

    static void Emit(string text)
    {
        events++;
        Write("COMBAT-ANOM " + text);
    }

    static void Write(string line)
    {
        try { ptLog(line); } catch (Exception) { }
        try { outLog(line); } catch (Exception) { }
    }

    // One summary line per game minute that had melee strike starts or ranged launches (zeros included).
    static void Window(double now)
    {
        int action = BugPackRules.StrikeWindowAction(now, windowStart);
        if (action == 0) return;
        if (action > 0) Summary(now, "");
        Restart(now);
    }

    static void Summary(double now, string note)
    {
        if (meleeStarts + rangedLaunches <= 0) return;
        Write("COMBAT-ANOM minute " + (now - windowStart).ToString("0.0") + "s" + note + ": melee " + meleeStarts + " fast " + meleeFast + " (player-task " + fastPlayerTask
            + " own-task " + fastOwnTask + " same-task " + fastSameTask + "); ranged " + rangedLaunches + " far " + rangedFar + " fast " + rangedFast + " max " + (maxRangeRatio * 100.0).ToString("0")
            + "%; lines " + events + " supp " + limiter.TakeSuppressed() + " err " + faultsInWindow + "; guard " + (GuardOn() ? "ON" : "off") + "; " + Boost());
    }

    static void Restart(double now)
    {
        windowStart = now; maxRangeRatio = 0.0;
        meleeStarts = meleeFast = fastPlayerTask = fastOwnTask = fastSameTask = rangedLaunches = rangedFar = rangedFast = events = faultsInWindow = 0;
        limiter.NewWindow();
    }

    // From the ticker: write a finished window even if no event follows it.
    internal static void Tick()
    {
        try
        {
            if (windowStart < 0.0) return;
            double now = Time.time;
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
            double now = Time.time;
            Summary(now, " partial (" + why + ")");
            Restart(now);
        }
        catch (Exception) { }
    }

    static string Boost()
    {
        try { string b = boostNote(); return string.IsNullOrEmpty(b) ? "boost ?" : b; }
        catch (Exception) { return "boost ?"; }
    }

    static void Fault(string where, Exception e)
    {
        faults++; faultsInWindow++;
        if (faults <= 5) Write("COMBAT-ANOM error in " + where + " (log only, the game is not affected): " + e.GetType().Name + ": " + e.Message);
    }
}

// The 2-second ticker of CombatAnomaly and StrikeGuard (their minute lines) and the partial lines at quit. Nothing else.
internal sealed class CombatAnomalyTicker : MonoBehaviour
{
    float next;

    void Update()
    {
        try
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 2f;
            CombatAnomaly.Tick();
            StrikeGuard.Tick();
        }
        catch (Exception) { }
    }

    void OnApplicationQuit()
    {
        try { CombatAnomaly.Flush("quit"); StrikeGuard.Flush("quit"); }
        catch (Exception) { }
    }
}
