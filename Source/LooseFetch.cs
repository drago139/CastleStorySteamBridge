using System;
using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Legacy;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.4.20 LOOSE-FETCH (HOST only; cfg [Fixes] LooseFetchLimit (true), LooseFetchRadius (60); perf-trace tag LOOSE-FETCH). User 2026-11-07: units walk all over the map for loose stone
// and felled logs; his rule "60 метрів від кристала". Why (SEEN, installed firstpass = recovered, D-078): material fetch (BuildGoal, crafting, catapult ammo, landscape fill,
// Project.VerifyResourceAccessFor) goes Labor.Find -> FindOneResource -> Knowledge.ClosestObjectToPickup, which takes the nearest reachable, faction-visible loose item or storage with
// NO distance limit. Zone pickups (harvest / cleanup / repair projects: CleanUpGoalProvider, beacon radius) are another path and are NOT changed; direct player orders are not changed.
// What this does: a prefix on Knowledge.ClosestObjectToPickup remembers the searching faction; a postfix on its private test Knowledge._ClosestObjectToPickup_Test turns a match false
// when the object is a loose STONE BLOCK (Stone_DoubleBrick) or LOG (LooseFetchRules.Limited; the search only ever sees InterestingResourceObject keys, Knowledge.cs:50-59, so loose
// RawResources such as raw stone or iron never reach it), is LOOSE (no parent recepteur; RecepteurParentTracker/ToolParentTracker on the ground, D-063's test) and lies farther than
// LooseFetchRadius (horizontal) from every crystal (SpawnerCristal) whose faction is the searching one. Verdicts are cached 2 s per item. A faction without an own crystal gets no
// limit. The game then takes the next nearest match (a stockpile, a closer item) or reports missing material. Not verified in a live game (T-191).
internal static class LooseFetch
{
    static Func<bool> enabled; static Func<float> radius; static Action<string> note;
    static Faction searching; static int depth;
    static readonly Dictionary<Faction, float[]> crystalX = new Dictionary<Faction, float[]>(), crystalZ = new Dictionary<Faction, float[]>();
    static readonly Dictionary<Faction, float> crystalUntil = new Dictionary<Faction, float>();
    static readonly HashSet<int> skippedIds = new HashSet<int>(), windowIds = new HashSet<int>();
    static readonly HashSet<int> noCrystalNoted = new HashSet<int>();
    static int searches, windowSearches, skipsTotal; static float nextSummary; static bool announced;   // searches: material searches this match

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Func<float> radiusMeters, Action<string> log)
    {
        enabled = isEnabled; radius = radiusMeters; note = log;
        System.Reflection.MethodInfo search = AccessTools.Method(typeof(Knowledge), "ClosestObjectToPickup");
        System.Reflection.MethodInfo test = AccessTools.Method(typeof(Knowledge), "_ClosestObjectToPickup_Test");
        if (search == null) throw new MissingMethodException("Knowledge.ClosestObjectToPickup");
        if (test == null) throw new MissingMethodException("Knowledge._ClosestObjectToPickup_Test");
        harmony.Patch(test, null, new HarmonyMethod(typeof(LooseFetch), "AfterTest"));
        harmony.Patch(search, new HarmonyMethod(typeof(LooseFetch), "BeforeSearch"), new HarmonyMethod(typeof(LooseFetch), "AfterSearch"));
    }

    static readonly Dictionary<int, float> verdictUntil = new Dictionary<int, float>();
    static readonly Dictionary<int, bool> verdictFar = new Dictionary<int, bool>();
    static float windowStart;

    internal static void Clear()
    {
        FlushSummary(Time.realtimeSinceStartup);   // the last window of a match is written, not lost
        searching = null; depth = 0; crystalX.Clear(); crystalZ.Clear(); crystalUntil.Clear(); noCrystalNoted.Clear();
        verdictUntil.Clear(); verdictFar.Clear(); skippedIds.Clear(); windowIds.Clear(); skipsTotal = 0; searches = 0; windowSearches = 0; windowStart = 0f; nextSummary = 0f; detailed = 0;
    }

    static void BeforeSearch(Faction faction)
    {
        depth++;
        try { searching = (enabled != null && enabled() && NetworkServer.active) ? faction : null; } catch (Exception) { searching = null; }
    }

    static void AfterSearch()
    {
        depth--; if (depth <= 0) { depth = 0; searching = null; }
        try
        {
            if (enabled == null || !enabled() || !NetworkServer.active) return;
            searches++; windowSearches++;
            float now = Time.realtimeSinceStartup;
            if (windowStart == 0f) { windowStart = now; nextSummary = now + LooseFetchRules.SummarySeconds; }
            if (now >= nextSummary) FlushSummary(now);
        }
        catch (Exception) { }
    }

    static void FlushSummary(float now)
    {
        try
        {
            if (windowIds.Count > 0 && note != null && radius != null)
                note("LOOSE-FETCH last " + (now - windowStart).ToString("F0") + " s: skipped " + windowIds.Count + " distinct loose stone block(s)/log(s) farther than " + LooseFetchRules.ClampRadius(radius()) + " m from own crystals in " + windowSearches + " material search(es) | skips this match " + skipsTotal);
        }
        catch (Exception) { }
        windowIds.Clear(); windowSearches = 0; windowStart = now; nextSummary = now + LooseFetchRules.SummarySeconds;
    }

    static int detailed;

    // cheapest first: key -> cached verdict -> crystal distance -> loose test (only for far items)
    static void AfterTest(IGameComponent gc, ref bool __result)
    {
        if (!__result || searching == null) return;
        try
        {
            if (gc == null) return;
            Factory.AssetKey key = gc.AssetKey;
            if (!LooseFetchRules.Limited(key.Factory, key.Name)) return;
            GameObject go = gc.GameObject;
            if (go == null) return;
            int id = go.GetInstanceID();
            float now = Time.realtimeSinceStartup, until; bool far;
            if (!(verdictUntil.TryGetValue(id, out until) && now < until && verdictFar.TryGetValue(id, out far)))
            {
                float[] xs, zs; int n = OwnCrystals(searching, out xs, out zs);
                Vector3 p = go.transform.position;
                float d2 = LooseFetchRules.NearestSqrXZ(p.x, p.z, xs, zs, n);
                float r = LooseFetchRules.ClampRadius(radius());
                far = false;
                if (LooseFetchRules.Veto(true, true, true, true, n, d2, r))
                {
                    // only a far item pays for the loose test: no parent recepteur, an item / tool tracker on the ground (D-063)
                    GameComponent comp = gc as GameComponent;
                    GenericParentTracker t = comp != null ? comp.ParentTracker : go.GetComponent<GenericParentTracker>();
                    far = gc.ParentRecepteur == null && t != null && (t is RecepteurParentTracker || t is ToolParentTracker) && t.IsOnGround();
                    if (far && note != null && detailed < LooseFetchRules.DetailLines && skippedIds.Count < LooseFetchRules.DetailIdsCap && skippedIds.Add(id))
                    {
                        detailed++;
                        note("LOOSE-FETCH skipped " + key.Name + " #" + id + " at " + p.x.ToString("F0") + "," + p.z.ToString("F0") + ": " + Mathf.Sqrt(d2).ToString("F0") + " m from the nearest own crystal (limit " + r + " m), faction " + searching.ID);
                    }
                }
                if (verdictUntil.Count >= 4096) { verdictUntil.Clear(); verdictFar.Clear(); }
                verdictUntil[id] = now + LooseFetchRules.VerdictSeconds; verdictFar[id] = far;
            }
            if (!far) return;
            __result = false;
            skipsTotal++;
            if (windowIds.Count < LooseFetchRules.DetailIdsCap) windowIds.Add(id);
        }
        catch (Exception) { }
    }
    // horizontal positions of the crystals owned by this faction (cached 2 s)
    static int OwnCrystals(Faction f, out float[] xs, out float[] zs)
    {
        float now = Time.realtimeSinceStartup, until;
        if (crystalUntil.TryGetValue(f, out until) && now < until) { xs = crystalX[f]; zs = crystalZ[f]; return xs.Length; }
        List<float> lx = new List<float>(), lz = new List<float>();
        foreach (SpawnerCristal c in SpawnerCristal.AllCrystals)
        {
            if (c == null || !c.gameObject.activeInHierarchy || c.faction != f) continue;
            Vector3 cp = c.transform.position; lx.Add(cp.x); lz.Add(cp.z);
        }
        xs = lx.ToArray(); zs = lz.ToArray();
        crystalX[f] = xs; crystalZ[f] = zs; crystalUntil[f] = now + 2f;
        if (xs.Length == 0 && note != null && noCrystalNoted.Add(f.ID)) note("LOOSE-FETCH faction " + f.ID + " owns no crystal: no distance limit for its workers");
        if (!announced && note != null) { announced = true; note("LOOSE-FETCH on (host only): workers fetching material skip loose raw items farther than " + LooseFetchRules.ClampRadius(radius()) + " m from their faction's own crystals (zone pickups and stockpiles unchanged)"); }
        return xs.Length;
    }
}
