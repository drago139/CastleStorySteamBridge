using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CastleStorySteam
{
    // 0.3.68 PerfTrace: measure first, then fix. Writes BepInEx\perf-trace.txt (also put into the diagnostics ZIP) with wall-clock timestamps:
    //   LOAD-BEGIN / STEP / LEVEL-READY  every loading step with the code that raised it and the time since the previous step,
    //   PHASE-BEGIN / PHASE-END / SLICE  the coroutine phases of the game's GameLoader (BuildStep, AwakeStep, StartStep, terrain, trees ...); a SLICE is one
    //                                    uninterrupted main-thread run of a phase (one MoveNext) longer than PerfSliceMs - this is what blocks the game,
    //   SYNC                             the synchronous GameLoader helpers (StartLua, InitNoise, LoadPlants ...) with their duration,
    //   HITCH                            every frame longer than PerfHitchMs with: managed GC runs in that frame, heap change, loader phase, loading step, scene,
    //                                    and the last long slice (so a hitch with gc+0 and no slice is native work: rendering, physics, assets),
    //   SUM                              a 60 s frame-time histogram and how much frame time was spent in hitches WITH and WITHOUT a garbage collection.
    // Cheap by design: one Stopwatch read per frame, a few counters, buffered asynchronous file writes. No personal data.
    public sealed partial class SteamBridgePlugin
    {
        static bool ptOn; static double ptHitchMs = 150, ptSliceMs = 50; static readonly double ptTick = 1000.0 / Stopwatch.Frequency;
        static readonly StringBuilder ptBuf = new StringBuilder(); static readonly object ptLock = new object(); static long ptBytes; static string ptPath; static bool ptWriting; static int ptDropped;
        static readonly List<string> ptPhases = new List<string>();
        static string ptHeader = "", ptGcStatus = "GC-HOOKS unknown";
        static long ptLastFrame, ptLastHeap, ptStepAt, ptLastSliceAt; static int ptLastGc; static string ptLastSlice = ""; static double ptLastSliceMs;
        static readonly int[] ptBucket = new int[7]; static readonly double[] ptBucketMs = new double[7];
        static int ptFrames, ptHitchGc, ptHitchNoGc; static double ptHitchGcMs, ptHitchNoGcMs, ptWorst; static float ptWinStart, ptNextFlush;
        static readonly double[] ptEdges = { 16.7, 33.4, 66.7, 150, 300, 1000 };

        static Type PtFindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = a.GetType(fullName, false); if (t != null) return t; } catch (Exception) { }
            }
            return null;
        }

        void InstallPerfTrace()
        {
            try
            {
                ConfigEntry<bool> on = Config.Bind("Diag", "PerfTrace", true, "Writes BepInEx\\perf-trace.txt: timed loading phases of the game's GameLoader, every loading step, frame hitches with their GC counts and a 60 s summary. Cheap; no personal data. It is added to the diagnostics ZIP.");
                ConfigEntry<int> hm = Config.Bind("Diag", "PerfHitchMs", 150, "Frame length (ms) above which perf-trace.txt records a HITCH line.");
                ConfigEntry<int> sm = Config.Bind("Diag", "PerfSliceMs", 50, "A single uninterrupted run of a loading coroutine longer than this many ms is recorded as SLICE.");
                if (!on.Value) { InstallPerfTraceIndependentFixes(); return; }   // 0.4.25: the fixes below must not depend on the log switch
                ptHitchMs = hm.Value; ptSliceMs = sm.Value;
                ptPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "perf-trace.txt");
                RollingTrace.Append(ptPath, "perf-trace v1, mod " + BridgeVersion.Text + " build " + BridgeVersion.Build + ", started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", hitch>=" + ptHitchMs + " ms, slice>=" + ptSliceMs + " ms\r\n", RollingTrace.SegmentBytes);
                ptOn = true; int n = 0, miss = 0;
                Type loader = PtFindType("Brix.Engine.GameLoader");
                if (loader != null)
                {
                    HarmonyMethod postEnum = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PtAfterEnumerator", BindingFlags.Static | BindingFlags.NonPublic));
                    string[] enums = { "Load", "BuildStep", "AwakeStep", "StartStep", "LoadBasicTerrain", "BuildTerrainMeshes", "LoadGameObjects", "LoadGridGuide", "WaitForVoxelEngine", "InitTerrainGravity", "InitPathfinding", "LoadNature", "LoadTrees", "LoadPlacedBlockData", "LoadingStepsCheckpoint" };
                    foreach (string nm in enums)
                    {
                        MethodInfo m = loader.GetMethod(nm, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (m != null && m.ReturnType == typeof(IEnumerator)) { try { discoveryHarmony.Patch(m, null, postEnum); n++; } catch (Exception) { miss++; } } else miss++;
                    }
                    HarmonyMethod pre = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PtSyncEnter", BindingFlags.Static | BindingFlags.NonPublic));
                    HarmonyMethod post = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PtSyncExit", BindingFlags.Static | BindingFlags.NonPublic));
                    string[] syncs = { "InitNoise", "ReadPresetDeepResourcesSettings", "StartLua", "LoadPlants", "LoadBoulders", "LoadIrons", "LoadPyrites", "SortTreesByMegaVoxel" };
                    foreach (string nm in syncs)
                    {
                        MethodInfo m = loader.GetMethod(nm, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (m != null) { try { discoveryHarmony.Patch(m, pre, post); n++; } catch (Exception) { miss++; } } else miss++;
                    }
                }
                Type pf = PtFindType("Brix.Pathfinding.Test.TestPathfinding");
                if (pf != null)
                {
                    MethodInfo m = pf.GetMethod("Read_Coroutine", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m != null && m.ReturnType == typeof(IEnumerator)) { try { discoveryHarmony.Patch(m, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PtAfterEnumerator", BindingFlags.Static | BindingFlags.NonPublic))); n++; } catch (Exception) { miss++; } }
                }
                PtInstallGcHooks(); DxInstall(); GpInstall(); GaInstall(); FoInstall();
#if FOGTRACE
            FogInstall();
#endif
            try { InstallCatapultSyncDiag(discoveryHarmony); } catch (Exception) { }
                try { BindAimReconcile(); InstallAimReconcile(discoveryHarmony); } catch (Exception e) { PtLog("AIM-RECONCILE install failed: " + e.Message); }
                try { InstallHostMarkerFilter(); } catch (Exception e) { PtLog("HOSTMARKER install failed: " + e.Message); }
                ptHeader="perf-trace v2 mod "+BridgeVersion.Text+" build "+BridgeVersion.Build+"\r\n"+ptGcStatus+"\r\nHW/OS "+SystemInfo.operatingSystem+"; cpu="+SystemInfo.processorType+" cores="+Environment.ProcessorCount+"; gpu="+SystemInfo.graphicsDeviceName+" vramMB="+SystemInfo.graphicsMemorySize+" ramMB="+SystemInfo.systemMemorySize+"; screen="+Screen.currentResolution.width+"x"+Screen.currentResolution.height+" window="+Screen.width+"x"+Screen.height+(Screen.fullScreen?" fullscreen":" windowed")+"\r\n"+GaHeaderText()+"\r\n";
                PtLog("INSTALLED hooks=" + n + " missing=" + miss + " loaderType=" + (loader != null));
                Logger.LogInfo("PerfTrace on (" + n + " hooks) -> perf-trace.txt");
            }
            catch (Exception e) { ptOn = false; Logger.LogWarning("PerfTrace unavailable: " + e.Message); }
        }

        // 0.4.25 LEAN: GameAnalytics guard, guest catapult aim reconcile and the host order-marker filter are FIXES (each has its own [Fixes]/[Catapult]/[Fog] switch), not
        // diagnostics. Until 0.4.24 they were installed only when [Diag] PerfTrace was on, so a lean build (PerfTrace off) would have switched them off silently.
        // Same calls as in the PerfTrace-on path above; PtLog is a no-op while ptOn is false. The log-only installs (GC hooks, DiagExtra, GcProbe, GC-FO, catapult sync diag) stay skipped.
        void InstallPerfTraceIndependentFixes()
        {
            try { GaInstall(); } catch (Exception e) { Logger.LogWarning("GA guard install failed: " + e.Message); }
            try { BindAimReconcile(); InstallAimReconcile(discoveryHarmony); } catch (Exception e) { Logger.LogWarning("AIM-RECONCILE install failed: " + e.Message); }
            try { InstallHostMarkerFilter(); } catch (Exception e) { Logger.LogWarning("HOSTMARKER install failed: " + e.Message); }
            Logger.LogInfo("PerfTrace off: GA guard, aim reconcile and host marker filter installed without the log.");
        }

        internal static void PtLog(string text)
        {
            BotTap("perf", text);   // 0.4.10 in-game bot log tap: stores nothing unless the bot is armed (Bot.cs)
            if (!ptOn) return;
            try
            {
                lock (ptLock)
                {
                    if (ptBuf.Length > 256 * 1024) { ptBuf.Length = 0; ptDropped++; }
                    // Bound memory even if the disk is stalled. The next flush records overflow.
                    string line = DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Time.realtimeSinceStartup.ToString("F2") + " " + text + "\r\n";
                    ptBuf.Append(line); ptBytes += line.Length;
                }
                DlKeep(text);
            }
            catch (Exception) { }
        }

        static void PtFlush()
        {
            if (!ptOn) return;
            lock (ptLock) { if (ptWriting || ptBuf.Length == 0) return; ptWriting = true; }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                try
                {
                    while (true)
                    {
                        string text;
                        lock (ptLock)
                        {
                            if (ptBuf.Length == 0) break;
                            text = (ptDropped > 0 ? "TRACE buffer-overflow batches=" + ptDropped + "\r\n" : "") + ptBuf;
                            ptDropped = 0; ptBuf.Length = 0;
                        }
                        RollingTrace.Append(ptPath, text, RollingTrace.SegmentBytes, ptHeader);
                    }
                }
                catch (Exception) { }
                finally { lock (ptLock) ptWriting = false; }
            });
        }

        static string PtPhase() { return ptPhases.Count == 0 ? "-" : string.Join(">", ptPhases.ToArray()); }

        // Wraps a GameLoader coroutine: times every uninterrupted run (MoveNext) and the whole phase.
        static void PtAfterEnumerator(ref IEnumerator __result, MethodBase __originalMethod)
        {
            try { if (ptOn && __result != null) __result = PtWrap(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name, __result); } catch (Exception) { }
        }

        static IEnumerator PtWrap(string name, IEnumerator inner)
        {
            long t0 = Stopwatch.GetTimestamp(); int moves = 0; int gc0 = GC.CollectionCount(0); double maxMove = 0;
            PtLog("PHASE-BEGIN " + name + " heap " + (GC.GetTotalMemory(false) / 1048576) + " MB");
            ptPhases.Add(name);
            while (true)
            {
                long m0 = Stopwatch.GetTimestamp(); int g0 = GC.CollectionCount(0); bool has;
                try { has = inner.MoveNext(); }
                catch (Exception e) { PtLog("PHASE-ERROR " + name + ": " + e.GetType().Name + " " + e.Message); ptPhases.Remove(name); throw; }
                double ms = (Stopwatch.GetTimestamp() - m0) * ptTick; moves++; if (ms > maxMove) maxMove = ms;
                if (ms >= ptSliceMs)
                {
                    ptLastSlice = name; ptLastSliceMs = ms; ptLastSliceAt = Stopwatch.GetTimestamp();
                    PtLog("SLICE " + name + " " + ms.ToString("F0") + " ms uninterrupted, gc+" + (GC.CollectionCount(0) - g0) + ", heap " + (GC.GetTotalMemory(false) / 1048576) + " MB");
                }
                if (!has) break;
                yield return inner.Current;
            }
            ptPhases.Remove(name);
            PtLog("PHASE-END " + name + " wall " + ((Stopwatch.GetTimestamp() - t0) * ptTick).ToString("F0") + " ms (includes waits), runs " + moves + ", longest run " + maxMove.ToString("F0") + " ms, gc+" + (GC.CollectionCount(0) - gc0) + ", heap " + (GC.GetTotalMemory(false) / 1048576) + " MB");
        }

        // 0.3.70: who starts the full GCs? Log-only prefixes on the explicit collection entry points, with the managed caller stack (B-010).
        static int ptGcCalls; static long ptGcLastAt;
        void PtInstallGcHooks()
        {
            int ok = 0, bad = 0;
            HarmonyMethod pre = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("PtGcCall", BindingFlags.Static | BindingFlags.NonPublic));
            List<MethodBase> targets = new List<MethodBase>();
            try { targets.Add(typeof(GC).GetMethod("Collect", Type.EmptyTypes)); } catch (Exception) { }
            try { targets.Add(typeof(GC).GetMethod("Collect", new Type[] { typeof(int) })); } catch (Exception) { }
            try { targets.Add(typeof(GC).GetMethod("Collect", new Type[] { typeof(int), typeof(GCCollectionMode) })); } catch (Exception) { }
            try { targets.Add(typeof(Resources).GetMethod("UnloadUnusedAssets", Type.EmptyTypes)); } catch (Exception) { }
            foreach (MethodBase m in targets)
            {
                if (m == null) { bad++; continue; }
                try { discoveryHarmony.Patch(m, pre, null); ok++; } catch (Exception e) { bad++; PtLog("GC-HOOK failed " + m.Name + ": " + e.GetType().Name); }
            }
            ptGcStatus="GC-HOOKS installed=" + ok + " failed=" + bad; PtLog(ptGcStatus);
            foreach (string typeName in new string[] { "Brix.Engine.Architecte", "I2.Loc.ResourceManager" })
            {
                Type type = PtFindType(typeName); string name = typeName == "Brix.Engine.Architecte" ? "OnDestroy" : "CleanResourceCache";
                MethodInfo method = type == null ? null : AccessTools.Method(type, name);
                try { if (method == null) throw new MissingMethodException(); discoveryHarmony.Patch(method, pre, null); PtLog("GC-CALLER hook=" + typeName + "." + name + " (caller entry; not proof of a collection)"); }
                catch (Exception e) { PtLog("GC-CALLER unavailable=" + typeName + "." + name + " " + e.GetType().Name); }
            }
        }

        static void PtGcCall(MethodBase __originalMethod)
        {
            try
            {
                if (!ptOn) return;
                ptGcCalls++;
                // Rolling output retains late-match triggers too; do not stop after 300 calls.
                long now = Stopwatch.GetTimestamp(); double since = ptGcLastAt == 0 ? -1 : (now - ptGcLastAt) * ptTick; ptGcLastAt = now;
                StringBuilder sb = new StringBuilder();
                StackTrace st = new StackTrace(2, false);
                for (int i = 0; i < st.FrameCount && i < 7; i++)
                {
                    MethodBase fm = st.GetFrame(i).GetMethod();
                    if (fm == null) continue;
                    if (sb.Length > 0) sb.Append(" < ");
                    sb.Append(fm.DeclaringType != null ? fm.DeclaringType.Name : "?").Append('.').Append(fm.Name);
                }
                PtLog("GC-CALL #" + ptGcCalls + " " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " since-prev " + (since < 0 ? "-" : since.ToString("F0") + " ms") + ", gc0=" + GC.CollectionCount(0) + ", heap " + (GC.GetTotalMemory(false) / 1048576) + " MB, scene " + SceneManager.GetActiveScene().name + ", from " + sb);
            }
            catch (Exception) { }
        }

        static void PtSyncEnter(out long __state) { __state = Stopwatch.GetTimestamp(); }
        static void PtSyncExit(long __state, MethodBase __originalMethod)
        {
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * ptTick;
                if (ms >= 10) { ptLastSlice = "sync:" + __originalMethod.Name; ptLastSliceMs = ms; ptLastSliceAt = Stopwatch.GetTimestamp(); }
                PtLog("SYNC " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " " + ms.ToString("F0") + " ms (blocked the main thread), heap " + (GC.GetTotalMemory(false) / 1048576) + " MB");
            }
            catch (Exception) { }
        }

        void PerfLoadBegin(int max) { ptStepAt = Stopwatch.GetTimestamp(); PtLog("LOAD-BEGIN steps=" + max + " scene=" + SceneManager.GetActiveScene().name + " heap " + (GC.GetTotalMemory(false) / 1048576) + " MB gc " + GC.CollectionCount(0)); }

        void PerfLoadStep(int n)
        {
            if (!ptOn) return;
            try
            {
                long now = Stopwatch.GetTimestamp(); double since = ptStepAt == 0 ? 0 : (now - ptStepAt) * ptTick; ptStepAt = now;
                string caller = "";
                try
                {
                    StackTrace st = new StackTrace(2, false);
                    for (int i = 0; i < st.FrameCount && i < 6; i++)
                    {
                        MethodBase m = st.GetFrame(i).GetMethod(); if (m == null) continue;
                        if (caller.Length > 0) caller += " < ";
                        caller += (m.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + m.Name;
                    }
                }
                catch (Exception) { }
                PtLog("STEP " + loadProgress + "/" + loadMax + " (+" + n + ") " + since.ToString("F0") + " ms after the previous; phase " + PtPhase() + "; heap " + (GC.GetTotalMemory(false) / 1048576) + " MB gc " + GC.CollectionCount(0) + "; caller " + caller);
            }
            catch (Exception) { }
        }

        void PerfLoadEnd() { prLevelReadyAt = Time.realtimeSinceStartup; PtLog("RESYNC-CONFIG FullResyncTest=" + PrFullOn() + " OutageResync=" + (prOutageCfg != null && prOutageCfg.Value) + " PauseResync=" + (prCfg != null && prCfg.Value)); PtLog("LEVEL-READY after " + (loadActive ? (Time.realtimeSinceStartup - loadStartedAt).ToString("F1") + " s" : "?") + " heap " + (GC.GetTotalMemory(false) / 1048576) + " MB gc " + GC.CollectionCount(0)); CatapultCensusArm(); PtFlush(); }

        void PerfFrameTick()
        {
            if (!ptOn) return;
            try
            {
                long now = Stopwatch.GetTimestamp(); int gc = GC.CollectionCount(0); long heap = GC.GetTotalMemory(false);
                if (gpOn) GpSample(heap);
                FoSample(); FrpDrain(); GaHeartbeat();
                if (ptLastFrame != 0)
                {
                    double ms = (now - ptLastFrame) * ptTick; int dg = gc - ptLastGc;
                    if (dg > 0 && gpOn) GpOnGc(ms, dg, gc);
                    if (dg > 0) FoOnGc(dg, ms, gc);
                    int b = 0; while (b < 6 && ms >= ptEdges[b]) b++;
                    ptBucket[b]++; ptBucketMs[b] += ms; ptFrames++; if (ms > ptWorst) ptWorst = ms;
                    if (ms >= ptHitchMs)
                    {
                        if (dg > 0) { ptHitchGc++; ptHitchGcMs += ms; } else { ptHitchNoGc++; ptHitchNoGcMs += ms; }
                        string sl = (ptLastSlice.Length > 0 && (now - ptLastSliceAt) * ptTick < ms + 100) ? (" slice=" + ptLastSlice + ":" + ptLastSliceMs.ToString("F0") + "ms") : "";
                        PtLog("HITCH " + ms.ToString("F0") + " ms gc+" + dg + " heapDelta " + ((heap - ptLastHeap) / 1024) + " KB heap " + (heap / 1048576) + " MB phase=" + PtPhase() + " load=" + (loadActive ? loadProgress + "/" + loadMax : "-") + " scene=" + SceneManager.GetActiveScene().name + sl + GpHitchText());
                    }
                }
                ptLastFrame = now; ptLastGc = gc; ptLastHeap = heap; FoTune(gc);
                float t = Time.realtimeSinceStartup;
                if (ptWinStart == 0) ptWinStart = t;
                if (t >= ptWinStart + 60)
                {
                    StringBuilder sb = new StringBuilder("SUM 60s frames=" + ptFrames + " worst=" + ptWorst.ToString("F0") + " ms; frames by length (count/total ms): ");
                    string[] labels = { "<17", "17-33", "33-67", "67-150", "150-300", "300-1000", ">=1000" };
                    for (int i = 0; i < 7; i++) sb.Append(labels[i]).Append("ms=").Append(ptBucket[i]).Append("/").Append(ptBucketMs[i].ToString("F0")).Append(i < 6 ? "; " : "");
                    sb.Append("; hitches>=").Append(ptHitchMs.ToString("F0")).Append("ms WITH gc: ").Append(ptHitchGc).Append(" (").Append(ptHitchGcMs.ToString("F0")).Append(" ms), WITHOUT gc: ").Append(ptHitchNoGc).Append(" (").Append(ptHitchNoGcMs.ToString("F0")).Append(" ms)");
                    PtLog(sb.ToString());
                    for (int i = 0; i < 7; i++) { ptBucket[i] = 0; ptBucketMs[i] = 0; }
                    ptFrames = ptHitchGc = ptHitchNoGc = 0; ptHitchGcMs = ptHitchNoGcMs = ptWorst = 0; ptWinStart = t;
                }
                PtExtraTick(t);
                if (t >= ptNextFlush) { ptNextFlush = t + 3f; PtFlush(); }
            }
            catch (Exception) { }
        }
    }
}
