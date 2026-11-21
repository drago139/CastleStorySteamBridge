using System;
using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Semantique;
using HarmonyLib;
using UnityEngine;

// 0.3.95: a worker's bag capacity per mined resource is data in the unit prefab (Recepteur._baseCapacity). DigGoal only pre-checks 20 in total, the real limit is
// that capacity, so a worker may leave for the storage with 10 of one type. This raises the base capacity of Stones / RawIron / OrangeCrystal / BlueCrystal to
// the configured amount (default 20) for every recepteur that belongs to a Labor, before the game recomputes its current capacity (Recepteur.ResetDescriptions).
// Only a capacity that is already above 0 and below the target is raised; a type the unit cannot carry stays unusable. The first value seen per type is logged.
// Every peer must run it: the capacity is checked locally when the unit picks up pebbles, the host decides the rest.
internal static class CarryCapacityFix
{
    static Func<int> target;
    static Action<string> note;
    static readonly HashSet<string> logged = new HashSet<string>();
    static readonly Type[] kinds = { typeof(Stones), typeof(RawIron), typeof(OrangeCrystal), typeof(BlueCrystal), typeof(Encombrement) };

    internal static void Install(Harmony harmony, Func<int> targetCapacity, Action<string> logger)
    {
        target = targetCapacity; note = logger;
        harmony.Patch(AccessTools.Method(typeof(Recepteur), "ResetDescriptions"), new HarmonyMethod(AccessTools.Method(typeof(CarryCapacityFix), "BeforeReset")), null, null, null, null);
    }

    internal static void Clear() { target = null; note = null; logged.Clear(); }

    static void BeforeReset(Recepteur __instance)
    {
        try
        {
            if (target == null || __instance == null) return;
            int want = target();
            bool worker = __instance.GetComponent<Labor>() != null;
            if (!worker) return;
            Description cap = __instance.BaseCapacity;
            for (int i = 0; i < kinds.Length; i++)
            {
                Adjectif a = cap.Get(kinds[i]);
                if (a == null || a.quantifiable == null) continue;
                int now = a.quantifiable.valeur;
                string key = kinds[i].Name + "=" + now;
                if (logged.Add(key) && note != null) note("worker capacity " + kinds[i].Name + " = " + now + (kinds[i] == typeof(Encombrement) ? " (not changed)" : ", target " + want));
                if (kinds[i] == typeof(Encombrement)) continue;
                if (now > 0 && now < want) a.quantifiable.valeur = want;
            }
        }
        catch (Exception e) { if (note != null && logged.Add("err" + e.GetType().Name)) note("carry capacity fix failed: " + e.GetType().Name + " " + e.Message); }
    }
}
