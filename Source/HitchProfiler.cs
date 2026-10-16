using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.Components;
using HarmonyLib;

namespace CastleStorySteam
{
    // 0.3.52 hitch profiler ([Diag] HitchProfiler, off by default). Every game component is ticked by GameClock (Update / FixedUpdate /
    // LateUpdate / SlowUpdate signals). This replaces those four loops with identical loops that time every call, then reports each
    // frame longer than HitchProfilerMs: how long the game's scripts took in each phase, which component types were slowest, how many
    // garbage collections ran, and how much of the frame was NOT script time (rendering, physics, GC, the OS). Written to
    // BepInEx\hitch-profile.txt.
    public sealed partial class SteamBridgePlugin
    {
        static bool pfOn; static double pfThresholdMs = 100;
        static Func<object, object> pfGetList; static Func<object, int> pfGetCount;
        static readonly long[] pfPhase = new long[4]; static int pfFixedSteps;
        static readonly Dictionary<Type, long> pfType = new Dictionary<Type, long>(); static readonly Dictionary<Type, int> pfCalls = new Dictionary<Type, int>();
        static long pfLastEnd; static int pfLastG0, pfLastG2, pfLines; static StreamWriter pfW; static double pfTick = 1000.0 / Stopwatch.Frequency;
        static readonly string[] pfNames = { "update", "fixed", "late", "slow" };
        static readonly Dictionary<Type, long> pfAlloc = new Dictionary<Type, long>(); static long pfAllocFrames, pfAllocOther, pfWinStart, pfLastHeap; static int pfWinGc; static bool pfFocusEvent, pfUnfocused; static long pfNextAllocLog; static readonly Dictionary<string, long> pfProbeBytes = new Dictionary<string, long>(); static readonly Dictionary<string, int> pfProbeCalls = new Dictionary<string, int>(); static readonly Stack<long> pfProbeStack = new Stack<long>(); static long pfLastGcTick, pfHeapAfterLastGc; static int pfGcNo;
        void OnApplicationFocus(bool focus) { pfFocusEvent = true; pfUnfocused = !focus; }

        void InstallHitchProfiler()
        {
            try
            {
                ConfigEntry<bool> on = Config.Bind("Diag", "HitchProfiler", false, "Writes BepInEx\\hitch-profile.txt: for every frame longer than HitchProfilerMs, which game components and phases took the time. Adds a little overhead.");
                ConfigEntry<int> ms = Config.Bind("Diag", "HitchProfilerMs", 100, "Frame length (ms) above which the profiler writes a report.");
                if (!on.Value) return;
                pfThresholdMs = ms.Value; pfOn = true;
                Type sigBase = typeof(GameClock).GetNestedType("ClockSignal", BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo list = sigBase.GetField("UpdateList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo count = sigBase.GetField("Count", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                DynamicMethod gl = new DynamicMethod("pf_list", typeof(object), new Type[] { typeof(object) }, sigBase, true); ILGenerator g = gl.GetILGenerator(); g.Emit(OpCodes.Ldarg_0); g.Emit(OpCodes.Castclass, sigBase); g.Emit(OpCodes.Ldfld, list); g.Emit(OpCodes.Ret); pfGetList = (Func<object, object>)gl.CreateDelegate(typeof(Func<object, object>));
                DynamicMethod gc = new DynamicMethod("pf_count", typeof(int), new Type[] { typeof(object) }, sigBase, true); g = gc.GetILGenerator(); g.Emit(OpCodes.Ldarg_0); g.Emit(OpCodes.Castclass, sigBase); g.Emit(OpCodes.Ldfld, count); g.Emit(OpCodes.Ret); pfGetCount = (Func<object, int>)gc.CreateDelegate(typeof(Func<object, int>));
                string[,] targets = { { "UpdateSignal", "SafeInvoke", "PfUpdate" }, { "FixedUpdateSignal", "UnorderedSafeInvoke", "PfFixed" }, { "LateUpdateSignal", "SafeInvoke", "PfLate" }, { "SlowUpdateSignal", "SafeInvoke", "PfSlow" } };
                for (int i = 0; i < 4; i++)
                {
                    Type t = typeof(GameClock).GetNestedType(targets[i, 0], BindingFlags.Public | BindingFlags.NonPublic);
                    MethodInfo m = t.GetMethod(targets[i, 1], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    discoveryHarmony.Patch(m, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod(targets[i, 2], BindingFlags.Static | BindingFlags.NonPublic)));
                }
                MethodInfo fixedUpd = typeof(GameClock).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo late = typeof(GameClock).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                discoveryHarmony.Patch(fixedUpd, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PfFixedStep", BindingFlags.Static | BindingFlags.NonPublic)));
                discoveryHarmony.Patch(late, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PfFrameEnd", BindingFlags.Static | BindingFlags.NonPublic)));
                try { PfInstallProbes(); } catch (Exception pe) { Logger.LogWarning("Allocation probes unavailable: " + pe.Message); }
                pfW = new StreamWriter(Path.Combine(BepInEx.Paths.BepInExRootPath, "hitch-profile.txt"), false); pfW.WriteLine("threshold " + pfThresholdMs + " ms"); pfW.Flush();
                Logger.LogInfo("Hitch profiler on (threshold " + pfThresholdMs + " ms) -> hitch-profile.txt");
            }
            catch (Exception e) { pfOn = false; Logger.LogWarning("Hitch profiler unavailable: " + e.Message); }
        }

        // Inclusive allocation probes (heap growth while a method runs) on the suspected allocators: SharedFactionVision internals and this plugin.
        void PfInstallProbes()
        {
            HarmonyMethod pre = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PfProbeEnter", BindingFlags.Static | BindingFlags.NonPublic));
            HarmonyMethod post = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PfProbeExit", BindingFlags.Static | BindingFlags.NonPublic));
            int n = 0;
            Type sfv = typeof(SharedFactionVision);
            List<Type> types = new List<Type>(); types.Add(sfv); foreach (Type nt in sfv.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)) types.Add(nt);
            foreach (Type ty in types)
                foreach (MethodInfo m in ty.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.Name == "Awake" || m.Name == "OnDestroy") continue;
                    try { discoveryHarmony.Patch(m, pre, post); n++; } catch (Exception) { }
                }
            foreach (string nm in new string[] { "Update", "OnGUI", "LateUpdate", "FixedUpdate" })
            {
                MethodInfo m = typeof(SteamBridgePlugin).GetMethod(nm, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (m != null) { try { discoveryHarmony.Patch(m, pre, post); n++; } catch (Exception) { } }
            }
            foreach (string nm in new string[] { "PosClientReceive", "PosHostSend", "TraceTick", "PositionLinkTick" })
            {
                MethodInfo m = typeof(SteamBridgePlugin).GetMethod(nm, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (m != null) { try { discoveryHarmony.Patch(m, pre, post); n++; } catch (Exception) { } }
            }
            Logger.LogInfo("Allocation probes on " + n + " methods");
        }

        static void PfProbeEnter() { pfProbeStack.Push(GC.GetTotalMemory(false) * 4 + 0); pfProbeStack.Push(GC.CollectionCount(0)); }
        static void PfProbeExit(MethodBase __originalMethod)
        {
            try
            {
                long c0 = pfProbeStack.Pop(); long m0 = pfProbeStack.Pop() / 4;
                if (GC.CollectionCount(0) != c0) return;
                long d = GC.GetTotalMemory(false) - m0; string key = (__originalMethod.DeclaringType != null ? __originalMethod.DeclaringType.Name : "?") + "." + __originalMethod.Name;
                long cur; pfProbeBytes.TryGetValue(key, out cur); pfProbeBytes[key] = cur + (d > 0 ? d : 0); int n; pfProbeCalls.TryGetValue(key, out n); pfProbeCalls[key] = n + 1;
            }
            catch (Exception) { }
        }

        static void PfFixedStep() { pfFixedSteps++; }
        static bool PfUpdate(object __instance) { PfRun(__instance, 0); return false; }
        static bool PfFixed(object __instance) { PfRun(__instance, 1); return false; }
        static bool PfLate(object __instance) { PfRun(__instance, 2); return false; }
        static bool PfSlow(object __instance) { PfRun(__instance, 3); return false; }

        static void PfRun(object signal, int kind)
        {
            long phaseStart = Stopwatch.GetTimestamp();
            IUpdatableComponent[] list = (IUpdatableComponent[])pfGetList(signal);
            for (int i = 0; i < pfGetCount(signal); i++)
            {
                if (i >= list.Length) break;
                IUpdatableComponent u = list[i];
                long t0 = Stopwatch.GetTimestamp(); long m0 = GC.GetTotalMemory(false); int c0 = GC.CollectionCount(0);
                try
                {
                    if (kind == 0) u.BrixUpdate(); else if (kind == 1) u.BrixFixedUpdate(); else if (kind == 2) u.BrixLateUpdate(); else u.BrixSlowUpdate();
                }
                catch (Exception e)
                {
                    try { if (kind == 0) u.OnUpdateError(e); else if (kind == 1) u.OnFixedUpdateError(e); else if (kind == 2) u.OnLateUpdateError(e); else u.OnSlowUpdateError(e); } catch (Exception) { }
                }
                long dt = Stopwatch.GetTimestamp() - t0;
                if (GC.CollectionCount(0) == c0) { long da = GC.GetTotalMemory(false) - m0; if (da > 0) { Type ta = u.GetType(); long ca; pfAlloc.TryGetValue(ta, out ca); pfAlloc[ta] = ca + da; pfAllocFrames += da; } }
                if (dt > 0)
                {
                    Type ty = u.GetType(); long cur; pfType.TryGetValue(ty, out cur); pfType[ty] = cur + dt;
                    int n; pfCalls.TryGetValue(ty, out n); pfCalls[ty] = n + 1;
                }
                if (list.Length != ((IUpdatableComponent[])pfGetList(signal)).Length) list = (IUpdatableComponent[])pfGetList(signal);
            }
            pfPhase[kind] += Stopwatch.GetTimestamp() - phaseStart;
        }

        static void PfFrameEnd()
        {
            if (!pfOn) return;
            long now = Stopwatch.GetTimestamp();
            try
            {
                if (pfLastEnd != 0)
                {
                    double frameMs = (now - pfLastEnd) * pfTick;
                    if (pfFocusEvent || pfUnfocused) { pfFocusEvent = false; frameMs = 0; }
                    if (frameMs >= pfThresholdMs && pfLines < 600 && pfW != null)
                    {
                        pfLines++;
                        int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2);
                        double script = 0; for (int i = 0; i < 4; i++) script += pfPhase[i] * pfTick;
                        List<KeyValuePair<Type, long>> top = new List<KeyValuePair<Type, long>>(pfType); top.Sort(delegate(KeyValuePair<Type, long> a, KeyValuePair<Type, long> b) { return b.Value.CompareTo(a.Value); });
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" HITCH ").Append(frameMs.ToString("F0")).Append(" ms | scripts ").Append(script.ToString("F0"));
                        for (int i = 0; i < 4; i++) sb.Append(' ').Append(pfNames[i]).Append('=').Append((pfPhase[i] * pfTick).ToString("F0"));
                        sb.Append(" fixedSteps=").Append(pfFixedSteps).Append(" | NOT script: ").Append((frameMs - script).ToString("F0")).Append(" ms | GC gen0+").Append(g0 - pfLastG0).Append(" gen2+").Append(g2 - pfLastG2).Append(" heap ").Append(GC.GetTotalMemory(false) >> 20).Append(" MB | top:");
                        for (int i = 0; i < top.Count && i < 4; i++) { int n; pfCalls.TryGetValue(top[i].Key, out n); sb.Append(' ').Append(top[i].Key.Name).Append(' ').Append((top[i].Value * pfTick).ToString("F0")).Append("ms/").Append(n); }
                        pfW.WriteLine(sb.ToString()); pfW.Flush();
                    }
                    int gNow = GC.CollectionCount(0); long heapNow = GC.GetTotalMemory(false);
                    if (gNow != pfLastG0 && pfW != null)
                    {
                        pfGcNo++; double sinceGc = pfLastGcTick == 0 ? 0 : (now - pfLastGcTick) * pfTick / 1000.0;
                        pfW.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " GCEVT #" + pfGcNo + " +" + (gNow - pfLastG0) + " frame " + frameMs.ToString("F0") + " ms; since previous GC " + sinceGc.ToString("F1") + " s; heap before " + (pfLastHeap >> 20) + " MB, after " + (heapNow >> 20) + " MB; allocated since previous GC " + ((pfLastHeap - pfHeapAfterLastGc) >> 20) + " MB");
                        pfLastGcTick = now; pfHeapAfterLastGc = heapNow;
                    }
                    if (gNow == pfLastG0 && pfLastHeap > 0 && heapNow > pfLastHeap) pfAllocOther += heapNow - pfLastHeap;
                    pfLastHeap = heapNow;
                    long nowMs = (long)(now * pfTick);
                    if (pfWinStart == 0) { pfWinStart = nowMs; pfWinGc = gNow; }
                    if (nowMs - pfWinStart >= 30000 && pfW != null)
                    {
                        List<KeyValuePair<Type, long>> al = new List<KeyValuePair<Type, long>>(pfAlloc); al.Sort(delegate(KeyValuePair<Type, long> a, KeyValuePair<Type, long> b) { return b.Value.CompareTo(a.Value); });
                        double secs = (nowMs - pfWinStart) / 1000.0; long total = pfAllocFrames + pfAllocOther;
                        System.Text.StringBuilder sa = new System.Text.StringBuilder(DateTime.Now.ToString("HH:mm:ss") + " ALLOC " + secs.ToString("F0") + " s: " + (total / 1048576.0).ToString("F0") + " MB (" + (total / 1048576.0 / secs).ToString("F1") + " MB/s) in tracked frames; scripts " + (pfAllocFrames / 1048576.0).ToString("F0") + " MB, outside scripts " + (pfAllocOther / 1048576.0).ToString("F0") + " MB; full GCs " + (gNow - pfWinGc) + " | top scripts:");
                        for (int i = 0; i < al.Count && i < 8; i++) sa.Append(" ").Append(al[i].Key.Name).Append(" ").Append((al[i].Value / 1048576.0).ToString("F1")).Append("MB");
                        pfW.WriteLine(sa.ToString());
                        List<KeyValuePair<string, long>> pr = new List<KeyValuePair<string, long>>(pfProbeBytes); pr.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
                        System.Text.StringBuilder sp = new System.Text.StringBuilder("          PROBE (inclusive, MB/calls):");
                        for (int i = 0; i < pr.Count && i < 12; i++) { int pc; pfProbeCalls.TryGetValue(pr[i].Key, out pc); sp.Append(" ").Append(pr[i].Key).Append(" ").Append((pr[i].Value / 1048576.0).ToString("F1")).Append("/").Append(pc); }
                        pfW.WriteLine(sp.ToString()); pfProbeBytes.Clear(); pfProbeCalls.Clear(); pfW.Flush();
                        pfAlloc.Clear(); pfAllocFrames = 0; pfAllocOther = 0; pfWinStart = nowMs; pfWinGc = gNow;
                    }
                    pfLastG0 = gNow; pfLastG2 = GC.CollectionCount(2);
                }
            }
            catch (Exception) { }
            for (int i = 0; i < 4; i++) pfPhase[i] = 0; pfFixedSteps = 0; pfType.Clear(); pfCalls.Clear();
            pfLastEnd = Stopwatch.GetTimestamp();
        }
    }
}
