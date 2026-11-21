using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Brix.Game.Components.CatapultSpace;
using CastleStorySteam;
using HarmonyLib;

// PICKER-GUARD (bug pack, switch [Fixes] PickerExceptionGuard, default true). Installed by BugPack.cs. Local input only: every PC that plays needs it.
// Problem (B-069 zone drag handle missing / "cannot stretch the zone", same family as B-067): Picking.OnUpdate walks all active pickers and calls Picker.EnterStandBy() or Picker.OnAction() with no
// try/catch. EnterStandBy (when not already on standby) starts with Picker.CloseAllInouts(); OnAction first calls LeaveStandBy() and then runs the five hover Inout.SetTo() calls itself (CloseAllInouts
// only in its no-hit branch). Inout<T>.SetTo() first runs the "last" handlers on the old target (_last.Invoke(_lastHit)) and only AFTER that clears _lastHit/_hadTarget. If a handler throws
// (BoxDragPicker.OnLeaveGameObject does GetComponent on a destroyed target -> exception) the state is never cleared and the same exception repeats every frame until the Picker is recreated or the
// handler stops throwing: Picking.OnUpdate is aborted before it sets "actif = true", so the pickers after it never run (no hover, no zone drag handle, no clicks).
// PART 1, hover guard: a Harmony finalizer on Picker.OnAction and Picker.EnterStandBy. On an exception it clears the 5 hover Inouts of that picker (_lastHit = default, _hadTarget = false), swallows
// the exception and logs it as PICKER-GUARD swallowed (first 20 in all, plus the first sight of every new exception type + top frame, the 100th repeat of one and every 1000th: type, picker, first 4
// frames, repeat count, message, and which hover Inouts held a stale target). Limits: the Leave handlers of the cleared Inouts are NOT run (a hover highlight can stay until the next hover); an exception
// thrown by a "first" or "continued" handler (the state is clean already) is swallowed too, but OnAction still stops at that point for that frame; the click Inouts (InoutClick) are not reset; Picker.Activate /
// Deactivate (which also call CloseAllInouts) are not guarded. The log lines name the failing handler. With the switch off the finalizer hands the exception back unchanged, but HarmonyX then throws it
// again, which drops the inner stack frames in brix.log.
// PART 2, catapult list prune (B-067, catapult cannot be aimed/fired): CatapultTargetPicker keeps a private List<Catapult> built from the selected objects (GetComponent<Catapult>(), so a non-catapult
// gives null; a catapult that dies after the selection stays in it). OnHoverAny/OnLeaveAny/OnFirstActionAny loop over it without a null check; the NRE in OnHoverAny aborts Picker.OnAction BEFORE its
// click handling, so a left click can never reach CallCmdSetTarget (part 1 alone would not help: it swallows the exception but OnAction still stops before the click section). A prefix on the three
// methods removes null/destroyed entries first (PICKER-GUARD pruned, with the split into "no Catapult component" and "destroyed"). Without an exception / dead entry nothing runs, so vanilla behaviour
// is unchanged. Both parts resolve every target first and patch afterwards: a missing member throws before anything is patched.
internal static class PickerGuard
{
    static Func<bool> enabled; static Action<string> note; static FieldInfo[] inouts; static int swallowed;
    static FieldInfo catapultsField; static int prunedLogged;
    static readonly Dictionary<string, int> seen = new Dictionary<string, int>();

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> log)
    {
        Type picker = AccessTools.TypeByName("Brix.Input.Picker"); if (picker == null) throw new MissingMemberException("Picker");
        string[] names = { "_onHoverAny", "_onHoverTerrain", "_onHoverGameObject", "_onHoverBlueprint", "_onHoverCharacter" };
        FieldInfo[] found = new FieldInfo[names.Length];
        for (int i = 0; i < names.Length; i++) { found[i] = AccessTools.Field(picker, names[i]); if (found[i] == null) throw new MissingFieldException("Picker", names[i]); }
        string[] methods = { "OnAction", "EnterStandBy" };
        MethodInfo[] targets = new MethodInfo[methods.Length];
        for (int i = 0; i < methods.Length; i++) { targets[i] = AccessTools.Method(picker, methods[i]); if (targets[i] == null) throw new MissingMethodException("Picker", methods[i]); }
        enabled = isEnabled; note = log; inouts = found;
        HarmonyMethod fin = new HarmonyMethod(AccessTools.Method(typeof(PickerGuard), "Finalizer"));
        for (int i = 0; i < targets.Length; i++) harmony.Patch(targets[i], null, null, null, fin, null);
    }

    internal static void InstallCatapult(Harmony harmony, Func<bool> isEnabled, Action<string> log)
    {
        Type pk = typeof(Brix.Input.CatapultTargetPicker);
        FieldInfo field = AccessTools.Field(pk, "catapults");
        if (field == null || !typeof(List<Catapult>).IsAssignableFrom(field.FieldType)) throw new MissingFieldException("CatapultTargetPicker", "catapults");
        string[] methods = { "OnHoverAny", "OnLeaveAny", "OnFirstActionAny" };
        MethodInfo[] targets = new MethodInfo[methods.Length];
        for (int i = 0; i < methods.Length; i++) { targets[i] = AccessTools.Method(pk, methods[i]); if (targets[i] == null) throw new MissingMethodException("CatapultTargetPicker", methods[i]); }
        enabled = isEnabled; note = log; catapultsField = field;
        HarmonyMethod pre = new HarmonyMethod(AccessTools.Method(typeof(PickerGuard), "PruneCatapults"));
        for (int i = 0; i < targets.Length; i++) harmony.Patch(targets[i], pre, null, null, null, null);
    }

    internal static void Clear() { enabled = null; note = null; inouts = null; catapultsField = null; seen.Clear(); }

    static void PruneCatapults(object __instance)
    {
        try
        {
            if (enabled == null || !enabled() || __instance == null || catapultsField == null) return;
            List<Catapult> list = catapultsField.GetValue(__instance) as List<Catapult>; if (list == null) return;
            int noComponent = 0, destroyed = 0;
            int removed = BugPackRules.RemoveDead(list, delegate(Catapult c) { if (c != null) return false; if ((object)c == null) noComponent++; else destroyed++; return true; });
            if (removed > 0 && note != null && prunedLogged++ < 10) note("PICKER-GUARD pruned " + removed + " dead or non-catapult entr" + (removed == 1 ? "y" : "ies") + " from the catapult target picker list (" + list.Count + " left) [no Catapult component " + noComponent + ", destroyed " + destroyed + "]");
        }
        catch (Exception) { }
    }

    static Exception Finalizer(Exception __exception, object __instance)
    {
        if (__exception == null || enabled == null || !enabled() || inouts == null || __instance == null) return __exception;
        try
        {
            string stale = Stale(__instance);
            for (int i = 0; i < inouts.Length; i++) { try { Reset(inouts[i].GetValue(__instance)); } catch (Exception) { } }
            swallowed++;
            string[] frames = (__exception.StackTrace ?? "").Split('\n');
            string key = __exception.GetType().Name + "@" + (frames.Length > 0 ? frames[0].Trim() : "");
            int n = 0; bool counted = seen.ContainsKey(key) || seen.Count < BugPackRules.GuardMaxSignatures;
            if (counted) { seen.TryGetValue(key, out n); seen[key] = ++n; }
            if (note != null && (BugPackRules.LogSwallowed(swallowed) || (counted && BugPackRules.LogSignature(n))))
            {
                UnityEngine.Object o = __instance as UnityEngine.Object; StringBuilder sb = new StringBuilder("PICKER-GUARD swallowed #").Append(swallowed).Append(' ').Append(__exception.GetType().Name)
                    .Append(" in picker '").Append(o != null ? o.name : "?").Append("': ");
                for (int i = 0; i < frames.Length && i < 4; i++) sb.Append(i > 0 ? " < " : "").Append(frames[i].Trim());
                if (counted) sb.Append(" | x").Append(n);
                sb.Append(" | ").Append(Cut(__exception.Message, 100));
                if (stale.Length > 0) sb.Append(" | stale: ").Append(stale);
                note(sb.ToString());
            }
            return null;
        }
        catch (Exception) { return __exception; }
    }

    static string Cut(string s, int max)
    {
        if (s == null) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length > max ? s.Substring(0, max) : s;
    }

    // Which hover Inouts still hold a target (not the bool "any" one), with the target's type and "(destroyed)" for a destroyed Unity object. Read BEFORE the reset; evidence only.
    static string Stale(object picker)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < inouts.Length; i++)
        {
            try
            {
                object inout = inouts[i].GetValue(picker); if (inout == null) continue;
                Type t = inout.GetType();
                FieldInfo had = AccessTools.Field(t, "_hadTarget"), last = AccessTools.Field(t, "_lastHit");
                if (had == null || last == null || !(bool)had.GetValue(inout)) continue;
                object target = last.GetValue(inout); if (target == null || target is bool) continue;
                bool gone = target is UnityEngine.Object && (UnityEngine.Object)target == null;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(inouts[i].Name).Append('=').Append(target.GetType().Name).Append(gone ? "(destroyed)" : "");
            }
            catch (Exception) { }
        }
        return sb.ToString();
    }

    // The default value of a field type; null for references AND for Nullable<T> (Activator.CreateInstance on a Nullable differs between runtimes).
    static object Zero(Type t)
    {
        return t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;
    }

    // Inout<T>: private T _lastHit; private bool _hadTarget (Picker.cs, Brix.Input). Fields are looked up on the runtime type of the instance (a closed generic). Each write is separate: one failing write
    // must not leave the rest of the Inouts dirty.
    static void Reset(object inout)
    {
        if (inout == null) return;
        Type t = inout.GetType();
        FieldInfo had = AccessTools.Field(t, "_hadTarget"), last = AccessTools.Field(t, "_lastHit");
        try { if (had != null) had.SetValue(inout, false); } catch (Exception) { }
        try { if (last != null) last.SetValue(inout, Zero(last.FieldType)); } catch (Exception) { }
    }
}
