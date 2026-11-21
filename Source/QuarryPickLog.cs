using System;
using System.Collections.Generic;
using System.Text;
using Brix.External.Factories;
using Brix.Engine;
using Brix.Game.AI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.3.96 DIAGNOSTIC ONLY (changes nothing): why a worker that already carries stone/ore does not take the next quarry dig goal. After DigGoal.IsMatchFor for a
// quarry goal (ResourceSetting is QuarryData) while the worker carries something, one line per worker per 3 s: carried amounts, space left of the 20 pre-check,
// what the goal's voxel would give, whether the bag has room for it, and the verdict. Host only, max 250 lines.
internal static class QuarryPickLog
{
    static Func<bool> enabled; static Action<string> note;
    static readonly Dictionary<int, float> last = new Dictionary<int, float>();
    static int lines;

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> logger)
    {
        enabled = isEnabled; note = logger;
        harmony.Patch(AccessTools.Method(typeof(DigGoal), "IsMatchFor"), null, new HarmonyMethod(AccessTools.Method(typeof(QuarryPickLog), "After")), null, null, null);
    }
    internal static void Clear() { enabled = null; note = null; last.Clear(); lines = 0; }

    static void After(DigGoal __instance, Labor labor, bool __result)
    {
        try
        {
            if (enabled == null || !enabled() || !NetworkServer.active || lines >= 250 || __instance == null || labor == null) return;
            if (!(__instance.ResourceSetting is QuarryData)) return;
            int stones = labor.recepteur.ContentDescription.Value<Stones>(), iron = labor.recepteur.ContentDescription.Value<RawIron>(),
                orange = labor.recepteur.ContentDescription.Value<OrangeCrystal>(), blue = labor.recepteur.ContentDescription.Value<BlueCrystal>();
            int carried = stones + iron + orange + blue;
            if (carried <= 0) return;
            int id = labor.GetInstanceID(); float now = Time.realtimeSinceStartup, t;
            if (last.TryGetValue(id, out t) && now - t < 3f) return;
            last[id] = now; lines++;
            List<Factory.AssetKey> keys = new List<Factory.AssetKey>(); List<int> amounts = new List<int>();
            Voxel.GetGarnotteValue(__instance.voxel.position, keys, amounts);
            StringBuilder v = new StringBuilder();
            for (int i = 0; i < keys.Count; i++) v.Append(keys[i].Name).Append('=').Append(amounts[i]).Append(" room=").Append(labor.recepteur.HasRoomFor(keys[i])).Append(' ');
            if (keys.Count == 0) v.Append("none ");
            note("QUARRY-PICK " + labor.name + " carries stone " + stones + " iron " + iron + " sulfur " + orange + " blue " + blue + " (space of 20: " + (20 - carried) + "), goal voxel gives " + v + "bagFull=" + labor.recepteur.IsFull() + " -> " + (__result ? "TAKES it" : "REJECTS it"));
        }
        catch (Exception e) { if (note != null && lines < 250) { lines = 250; note("quarry pick log failed: " + e.GetType().Name + " " + e.Message); } }
    }
}
