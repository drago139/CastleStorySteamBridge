using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.70 extra diagnostics, all written into perf-trace.txt through PtLog (so they travel in the diagnostics ZIP). No personal data, local only.
    //   PROC    every 15 s: process working set / private bytes / virtual, threads, handles, CPU % of one core and of the machine, managed heap, GC count.
    //   LOGS    every 60 s: how many Unity errors / exceptions / warnings were logged and the most repeated messages (log spam costs frame time).
    //   CENSUS  every 120 s: number of live Unity objects per type (leak detector for long matches) with the cost of counting; one type per frame; a slow first scan can hitch, later scans skip that type.
    //   NATIVE  every 15 s when the player reports it: Unity native allocated / reserved / mono heap.
    public sealed partial class SteamBridgePlugin
    {
        static readonly object dxLock = new object(); static readonly Dictionary<string, int> dxMsgs = new Dictionary<string, int>();
        static int dxErr, dxExc, dxWarn, dxInfo; static float dxNextProc, dxNextLogs, dxNextCensus; static bool dxHooked;
        static TimeSpan dxLastCpu; static float dxLastCpuAt; static readonly Type[] dxTypes = { typeof(GameObject), typeof(Transform), typeof(Renderer), typeof(MeshFilter), typeof(Mesh), typeof(Texture), typeof(Material), typeof(AudioSource), typeof(Collider), typeof(Rigidbody), typeof(MonoBehaviour), typeof(AnimationClip) };
        static int dxCensusIdx = -1; static readonly int[] dxCounts = new int[12]; static readonly int[] dxPrev = new int[12]; static double dxCensusMs; static double dxCensusMax; static bool dxHavePrev;
        static readonly double[] dxTypeCost = new double[12];
        static int dxThreads = -1, dxThreadBusy;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct DxThread { internal uint Size, Usage, Id, Owner; internal int BasePriority, Delta; internal uint Flags; }
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool Thread32First(IntPtr snap, ref DxThread entry);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool Thread32Next(IntPtr snap, ref DxThread entry);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetProcessHandleCount(IntPtr process, out uint handles);
        static void DxCountThreads()
        {
            if (System.Threading.Interlocked.CompareExchange(ref dxThreadBusy,1,0)!=0) return;
            uint pid=GetCurrentProcessId();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                IntPtr h=IntPtr.Zero;
                try
                {
                    h=CreateToolhelp32Snapshot(4,0); if(h==new IntPtr(-1))return;
                    DxThread t=new DxThread();t.Size=(uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DxThread));int n=0;
                    if(Thread32First(h,ref t))do { if(t.Owner==pid)n++; }while(Thread32Next(h,ref t));
                    dxThreads=n>0 ? n : -1;
                }
                catch(Exception){dxThreads=-1;}
                finally{if(h!=IntPtr.Zero && h!=new IntPtr(-1))CloseHandle(h);System.Threading.Interlocked.Exchange(ref dxThreadBusy,0);}
            });
        }

        static void DxInstall()
        {
            try
            {
                if (dxHooked) return; dxHooked = true;
                Application.logMessageReceived += DxOnLog;
                DxCountThreads(); // Warm the worker result before the first 15-second PROC record.
                PtLog("GC-ENV initial=" + (Environment.GetEnvironmentVariable("GC_INITIAL_HEAP_SIZE") ?? "unset") + " divisor=" + (Environment.GetEnvironmentVariable("GC_FREE_SPACE_DIVISOR") ?? "unset") + " mono=" + (Environment.GetEnvironmentVariable("MONO_GC_PARAMS") ?? "unset") + "; inherited settings, not proof the runtime honours them");
                PtLog("EXTRA-DIAG on: PROC/LOGS/CENSUS every 15/60/120 s, cores=" + Environment.ProcessorCount + ", os=" + SystemInfo.operatingSystem + ", gpu=" + SystemInfo.graphicsDeviceName + " " + SystemInfo.graphicsMemorySize + " MB, ram=" + SystemInfo.systemMemorySize + " MB, cpu=" + SystemInfo.processorType);
            }
            catch (Exception) { }
        }

        static void DxShutdown() { if (dxHooked) Application.logMessageReceived -= DxOnLog; dxHooked = false; }

        static void DxOnLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                lock (dxLock)
                {
                    if (type == LogType.Exception) dxExc++; else if (type == LogType.Error || type == LogType.Assert) dxErr++; else if (type == LogType.Warning) dxWarn++; else { dxInfo++; return; }
                    string key = (type == LogType.Warning ? "W " : "E ") + (condition == null ? "" : (condition.Length > 90 ? condition.Substring(0, 90) : condition));
                    int c; dxMsgs.TryGetValue(key, out c); if (c > 0 || dxMsgs.Count < 400) dxMsgs[key] = c + 1;
                }
            }
            catch (Exception) { }
        }

        static void PtExtraTick(float t)
        {
            try
            {
                if (!dxHooked) return;
                
#if FOGTRACE
FogTraceTick(t);
#endif

                if (dxNextProc == 0) { dxNextProc = t + 15f; dxNextLogs = t + 60f; dxNextCensus = t + 30f; try { dxLastCpu = Process.GetCurrentProcess().TotalProcessorTime; dxLastCpuAt = t; } catch (Exception) { } }
                if (t >= dxNextProc)
                {
                    dxNextProc = t + 15f;
                    using (Process p = Process.GetCurrentProcess()) { p.Refresh();
                    TimeSpan cpu = p.TotalProcessorTime; double dt = t - dxLastCpuAt; double pct = dt > 0.1 ? (cpu - dxLastCpu).TotalSeconds / dt * 100.0 : 0; dxLastCpu = cpu; dxLastCpuAt = t;
                    MemoryCounters m=new MemoryCounters();m.Size=(uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryCounters));
                    bool memory=GetProcessMemoryInfo(GetCurrentProcessHandle(),out m,m.Size);uint handles;bool hc=GetProcessHandleCount(GetCurrentProcessHandle(),out handles);
                    DxCountThreads();
                    PtLog("PROC ws " + (memory ? (long)m.WorkingSet.ToUInt64()/1048576 : -1) + " MB, commit " + (memory ? (long)m.PagefileUsage.ToUInt64()/1048576 : -1) + " MB, virtual " + (p.VirtualMemorySize64>0 ? p.VirtualMemorySize64/1048576 : -1) + " MB, threads " + dxThreads + ", handles " + (hc ? (long)handles : -1) + ", cpu " + pct.ToString("F0") + "% of one core (" + (pct / Environment.ProcessorCount).ToString("F0") + "% of machine), managed " + (GC.GetTotalMemory(false) / 1048576) + " MB, gc " + GC.CollectionCount(0) + "; -1=unavailable");
                    }
                    try
                    {
                        long na = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemory(); long nr = UnityEngine.Profiling.Profiler.GetTotalReservedMemory(); long mh = UnityEngine.Profiling.Profiler.GetMonoHeapSize(); long mu = UnityEngine.Profiling.Profiler.GetMonoUsedSize();
                        PtLog("NATIVE allocated " + (na / 1048576) + " MB, reserved " + (nr / 1048576) + " MB, mono heap " + (mh > 0 ? mh / 1048576 : -1) + " MB, mono used " + (mu > 0 ? mu / 1048576 : -1) + " MB; -1=player counter unavailable" + (na == 0 && nr == 0 ? " (player counters unavailable)" : ""));
                    }
                    catch (Exception) { PtLog("NATIVE unavailable in this player"); }
                }
                if (t >= dxNextLogs)
                {
                    dxNextLogs = t + 60f;
                    string top = ""; int e, x, w, i;
                    lock (dxLock)
                    {
                        e = dxErr; x = dxExc; w = dxWarn; i = dxInfo;
                        List<KeyValuePair<string, int>> l = new List<KeyValuePair<string, int>>(dxMsgs); l.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
                        StringBuilder sb = new StringBuilder(); for (int k = 0; k < l.Count && k < 4; k++) { if (l[k].Value < 3) break; sb.Append(" | ").Append(l[k].Value).Append("x ").Append(l[k].Key.Replace('\r', ' ').Replace('\n', ' ')); }
                        top = sb.ToString(); dxErr = dxExc = dxWarn = dxInfo = 0; dxMsgs.Clear();
                    }
                    PtLog("LOGS 60s: errors " + e + ", exceptions " + x + ", warnings " + w + ", info " + i + (top.Length > 0 ? ", most repeated:" + top : ""));
                }
                if (dxCensusIdx < 0 && t >= dxNextCensus) { dxCensusIdx = 0; dxCensusMs = dxCensusMax = 0; dxNextCensus = t + 120f; }
                // Enumeration must run on Unity's thread; do not add it to a loading stall.
                if (dxCensusIdx >= 0 && (discoveryInstance == null || !discoveryInstance.loadActive))
                {
                    long s0 = Stopwatch.GetTimestamp();
                    // A slow type is explicitly unavailable on later passes; retain its last count in the log.
                    if (dxTypeCost[dxCensusIdx] > 8) dxCounts[dxCensusIdx] = -2;
                    else try { dxCounts[dxCensusIdx] = Resources.FindObjectsOfTypeAll(dxTypes[dxCensusIdx]).Length; } catch (Exception) { dxCounts[dxCensusIdx] = -1; }
                    double cost = (Stopwatch.GetTimestamp() - s0) * ptTick;
                    if(dxCounts[dxCensusIdx]>=0)dxTypeCost[dxCensusIdx]=cost;
                    dxCensusMs += cost; dxCensusMax = Math.Max(dxCensusMax, cost); dxCensusIdx++;
                    if (dxCensusIdx >= dxTypes.Length)
                    {
                        dxCensusIdx = -1; StringBuilder sb = new StringBuilder("CENSUS (live objects, change since previous): ");
                        for (int k = 0; k < dxTypes.Length; k++) { sb.Append(dxTypes[k].Name).Append('=').Append(dxCounts[k]); if(dxCounts[k]==-2)sb.Append("(budget: last=").Append(dxPrev[k]).Append(",cost=").Append(dxTypeCost[k].ToString("F2")).Append("ms)"); else {if (dxHavePrev && dxPrev[k]>=0 && dxCounts[k]>=0) sb.Append('(').Append(dxCounts[k] - dxPrev[k] >= 0 ? "+" : "").Append(dxCounts[k] - dxPrev[k]).Append(')');dxPrev[k]=dxCounts[k];} sb.Append(k < dxTypes.Length - 1 ? ", " : ""); }
                        dxHavePrev = true; sb.Append("; max per-frame cost ").Append(dxCensusMax.ToString("F2")).Append(" ms; counting cost ").Append(dxCensusMs.ToString("F0")).Append(" ms in total over ").Append(dxTypes.Length).Append(" frames, scene ").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                        PtLog(sb.ToString());
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
