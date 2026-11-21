using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.98 LUACALLS (log only). Which Lua closures does the engine call most, and how long do they run? The managed heap is half MoonSharp objects and a full GC stalls ~1 s every 40-90 s, so the
    // question is where the garbage comes from. Every Closure.Call overload is counted per entry point (EntryPointByteCodeLocation) with the time spent inside it (nested calls are counted in the outer one too).
    // Every Diag/LuaCallStatsSeconds (default 90, 0 = off) one line is written: calls and calls/s in the window, the top entries with count, ms, and the first upvalue name as a hint. Changes nothing.
    public sealed partial class SteamBridgePlugin
    {
        sealed class LcStat { public long calls, ticks; public WeakReference sample; }
        static ConfigEntry<int> lcCfg; static readonly Dictionary<int, LcStat> lcStats = new Dictionary<int, LcStat>(); static readonly object lcLock = new object();
        static PropertyInfo lcLoc; static MethodInfo lcUpName, lcUpCount; static float lcNext, lcWindowStart; static long lcTotal;

        void InstallLuaCallStats()
        {
            try
            {
                lcCfg = Config.Bind("Diag", "LuaCallStatsSeconds", 90, "Every N seconds write LUACALLS to perf-trace.txt: how many Lua closure calls happened and which entry points were called most / ran longest. 0 = off. Log only.");
                if (lcCfg.Value <= 0) return;
                Type closure = AccessTools.TypeByName("MoonSharp.Interpreter.Closure");
                if (closure == null) return;
                lcLoc = closure.GetProperty("EntryPointByteCodeLocation"); lcUpName = closure.GetMethod("GetUpvalueName"); lcUpCount = closure.GetMethod("GetUpvaluesCount");
                if (lcLoc == null) return;
                HarmonyMethod pre = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("LcBefore", BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod post = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("LcAfter", BindingFlags.Static | BindingFlags.NonPublic));
                int n = 0;
                foreach (MethodInfo m in closure.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    if (m.Name == "Call" && m.DeclaringType == closure) { discoveryHarmony.Patch(m, pre, post); n++; }
                lcWindowStart = Time.realtimeSinceStartup; lcNext = lcWindowStart + lcCfg.Value;
                Logger.LogInfo("LUACALLS ready (" + n + " Call overloads)");
            }
            catch (Exception e) { Logger.LogWarning("LUACALLS unavailable: " + e.Message); }
        }

        static void LcBefore(object __instance, out long __state)
        {
            __state = -1;
            try { __state = System.Diagnostics.Stopwatch.GetTimestamp(); LcCount(__instance); } catch (Exception) { }
        }

        static void LcAfter(object __instance, long __state)
        {
            try
            {
                if (__state < 0 || lcLoc == null) return;
                int loc = (int)lcLoc.GetValue(__instance, null); long dt = System.Diagnostics.Stopwatch.GetTimestamp() - __state;
                lock (lcLock) { LcStat s; if (lcStats.TryGetValue(loc, out s)) s.ticks += dt; }
            }
            catch (Exception) { }
        }

        static void LcCount(object closure)
        {
            if (lcLoc == null) return;
            int loc = (int)lcLoc.GetValue(closure, null);
            lock (lcLock)
            {
                lcTotal++; LcStat s;
                if (!lcStats.TryGetValue(loc, out s)) { s = new LcStat(); s.sample = new WeakReference(closure); lcStats[loc] = s; }
                s.calls++;
            }
        }

        void LuaCallStatsTick()
        {
            if (lcCfg == null || lcCfg.Value <= 0 || lcLoc == null || !ptOn || !HpInGame()) return;
            float now = Time.realtimeSinceStartup; if (now < lcNext) return; lcNext = now + lcCfg.Value;
            try
            {
                List<KeyValuePair<int, LcStat>> all; long total;
                lock (lcLock) { all = new List<KeyValuePair<int, LcStat>>(lcStats); total = lcTotal; lcStats.Clear(); lcTotal = 0; }
                float secs = Mathf.Max(0.001f, now - lcWindowStart); lcWindowStart = now;
                double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency; double sumMs = 0; for (int i = 0; i < all.Count; i++) sumMs += all[i].Value.ticks * tickMs;
                StringBuilder sb = new StringBuilder("LUACALLS window ").Append(secs.ToString("F0")).Append(" s: ").Append(total).Append(" calls (").Append((total / secs).ToString("F0")).Append("/s), ").Append(all.Count).Append(" entry points, ").Append(sumMs.ToString("F0")).Append(" ms inside calls (nested calls counted in the outer one too)");
                all.Sort((a, b) => b.Value.calls.CompareTo(a.Value.calls));
                sb.Append("; most called: "); LcAppend(sb, all, 6);
                all.Sort((a, b) => b.Value.ticks.CompareTo(a.Value.ticks));
                sb.Append("; longest: "); LcAppend(sb, all, 6);
                PtLog(sb.ToString());
            }
            catch (Exception) { }
        }

        static void LcAppend(StringBuilder sb, List<KeyValuePair<int, LcStat>> list, int max)
        {
            double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            for (int i = 0; i < list.Count && i < max; i++)
            {
                LcStat s = list[i].Value; string hint = "";
                try { object c = s.sample.Target; if (c != null && lcUpCount != null && lcUpName != null && (int)lcUpCount.Invoke(c, null) > 0) hint = "/" + lcUpName.Invoke(c, new object[] { 0 }); } catch (Exception) { }
                if (i > 0) sb.Append(", ");
                sb.Append('@').Append(list[i].Key).Append(hint).Append(' ').Append(s.calls).Append("x ").Append((s.ticks * tickMs).ToString("F0")).Append("ms");
            }
        }
    }
}
