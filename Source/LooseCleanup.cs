using System;
using System.Collections.Generic;
using System.Text;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Legacy;
using CastleStorySteam;
using UnityEngine;
using UnityEngine.Networking;

// LOOSE-CLEANUP (bug pack part 6; HOST only; cfg [Fixes] LooseItemCleanup (true), LooseRawMinutes (10), LooseFinishedMinutes (15)). User request, 2026-11-06, after a bomb test left the
// sandbox full of stones: "все що лежить більше 10 хвилин очищається з карти, особливо колоди, камінь, залізо; готові речі - тільки після 15 хвилин".
// What counts as lying loose: an item whose GenericParentTracker says IsOnGround() (not tracked by any stockpile, crate, unit, station or catapult; the game's own test) and whose tracker is an
// item or tool tracker (RecepteurParentTracker / ToolParentTracker; construction-site materials, grips and units are never looked at), plus the rubble pieces (Garnotte) that the game's own
// Mergeback did not sink. Kind by the factory asset key (BugPackRules.ClassifyLoose): RAW (RawResources: stone, corruptron stone, iron, crystals, clay, fiber; the Log blocks; rubble) goes after
// LooseRawMinutes, FINISHED (BuildResources: ingots, glass, fabric, rope, bombs ...; other Blocks: bricks, planks ...; dropped tools and weapons) after LooseFinishedMinutes; everything else
// (structures, barrels, pallets, units, unknown keys) is never removed.
// The clock is game time (a paused game does not count) and restarts whenever the thing is picked up or stored. Removal is the game's own SafeNetworkDestroy (UnSpawn + pool release), so the
// guests see it vanish; they need nothing. A sweep runs every 15 s of game time on the host, only in a match. Log: LOOSE-CLEANUP removed N (raw a, finished b, rubble c): names ... (the first
// 100 sweeps that removed something, then every 10th) and one start line. Known limit: a thing a worker is walking to at that very moment is removed anyway (the worker's job then fails).
internal static class LooseCleanup
{
    const float SweepSeconds = 15f;
    static Func<bool> enabled; static Func<float> rawMinutes, finishedMinutes; static Action<string> note;
    static readonly Dictionary<int, float> since = new Dictionary<int, float>();
    // 0.4.27 review: one scene scan per sweep instead of two. A scan by type walks every loaded object (the CENSUS lines of perf-trace.txt show 8-30 ms for one such scan on the user's PC; B-066),
    // and the sweep made two of them in one frame every 15 s on the host. Now the item sweep and the rubble sweep take turns (each kind is looked at every 30 s); rubble keeps its own clock table.
    static readonly Dictionary<int, float> sinceRubble = new Dictionary<int, float>(); static bool rubbleTurn;
    static float nextSweep; static int sweepsWithRemovals, removedTotal; static bool announced;
    static GameObject poller;

    internal static void Install(Func<bool> isEnabled, Func<float> raw, Func<float> finished, Action<string> log)
    {
        enabled = isEnabled; rawMinutes = raw; finishedMinutes = finished; note = log;
        if (poller != null) return;
        poller = new GameObject("SteamBridge LooseCleanup");
        poller.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(poller);
        poller.AddComponent<LooseCleanupPoller>();
    }

    internal static void Clear() { enabled = null; rawMinutes = null; finishedMinutes = null; note = null; since.Clear(); sinceRubble.Clear(); }

    internal static void Tick()
    {
        if (enabled == null || !enabled() || !NetworkServer.active) { if (since.Count > 0) since.Clear(); if (sinceRubble.Count > 0) sinceRubble.Clear(); return; }
        float now = Time.time;
        if (now < nextSweep) return;
        nextSweep = now + SweepSeconds;
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "ScenePrincipale") { since.Clear(); sinceRubble.Clear(); return; }
        float rawM = rawMinutes == null ? 10f : rawMinutes(), finM = finishedMinutes == null ? 15f : finishedMinutes();
        if (!announced && note != null) { announced = true; note("LOOSE-CLEANUP on (host only): raw things lying loose go after " + rawM + " min, finished things after " + finM + " min (game time)"); }
        HashSet<int> seen = new HashSet<int>();
        Dictionary<string, int> names = new Dictionary<string, int>();
        int raw = 0, fin = 0, rubble = 0;
        bool rubbleNow = rubbleTurn; rubbleTurn = !rubbleTurn;
        if (!rubbleNow) foreach (GenericParentTracker t in UnityEngine.Object.FindObjectsOfType<GenericParentTracker>())
        {
            try
            {
                if (t == null || !(t is RecepteurParentTracker || t is ToolParentTracker) || !t.IsOnGround()) continue;
                Factory.AssetKey key = t.AssetKey;
                LooseKind kind = BugPackRules.ClassifyLoose(key.Factory, key.Name);
                if (kind == LooseKind.Keep || t.GetComponent<Labor>() != null) continue;
                GameObject go = t.gameObject; int id = go.GetInstanceID(); seen.Add(id);
                float first;
                if (!since.TryGetValue(id, out first)) { since[id] = now; continue; }
                if (!BugPackRules.LooseExpired(kind, now - first, rawM, finM)) continue;
                go.SafeNetworkDestroy();
                since.Remove(id); seen.Remove(id);
                if (kind == LooseKind.Raw) raw++; else fin++;
                Count(names, key.Name);
            }
            catch (Exception) { }
        }
        else foreach (Garnotte g in UnityEngine.Object.FindObjectsOfType<Garnotte>())
        {
            try
            {
                if (g == null || g.Sinking) continue;
                GameObject go = g.gameObject; int id = go.GetInstanceID(); seen.Add(id);
                float first;
                if (!sinceRubble.TryGetValue(id, out first)) { sinceRubble[id] = now; continue; }
                if (!BugPackRules.LooseExpired(LooseKind.Raw, now - first, rawM, finM)) continue;
                go.SafeNetworkDestroy();
                sinceRubble.Remove(id); seen.Remove(id);
                rubble++;
                Count(names, "rubble");
            }
            catch (Exception) { }
        }
        // forget what is gone, picked up or stored: its clock starts again if it lands on the ground later
        Dictionary<int, float> table = rubbleNow ? sinceRubble : since;
        if (table.Count > seen.Count)
        {
            List<int> stale = new List<int>();
            foreach (KeyValuePair<int, float> p in table) if (!seen.Contains(p.Key)) stale.Add(p.Key);
            for (int i = 0; i < stale.Count; i++) table.Remove(stale[i]);
        }
        int removed = raw + fin + rubble;
        if (removed == 0) return;
        removedTotal += removed; sweepsWithRemovals++;
        if (note == null || !BugPackRules.LooseLogSweep(sweepsWithRemovals)) return;
        StringBuilder sb = new StringBuilder("LOOSE-CLEANUP removed ").Append(removed).Append(" (raw ").Append(raw).Append(", finished ").Append(fin).Append(", rubble ").Append(rubble).Append("): ");
        int shown = 0;
        foreach (KeyValuePair<string, int> p in names) { if (shown++ >= 8) { sb.Append(" ..."); break; } sb.Append(shown > 1 ? ", " : "").Append(p.Key).Append(" x").Append(p.Value); }
        sb.Append(" | watching ").Append(since.Count + sinceRubble.Count).Append(", removed in all ").Append(removedTotal);
        note(sb.ToString());
    }

    static void Count(Dictionary<string, int> names, string name)
    {
        string k = string.IsNullOrEmpty(name) ? "?" : name;
        int n; names.TryGetValue(k, out n); names[k] = n + 1;
    }
}

internal sealed class LooseCleanupPoller : MonoBehaviour
{
    void Update() { try { LooseCleanup.Tick(); } catch (Exception) { } }
}
