using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx.Configuration;

namespace CastleStorySteam
{
    // 0.3.102 GCPROBE (log only, [Diag] GcProbe, default on, a few microseconds per frame). Why: every stall of 0.6-1.2 s is a full Boehm collection (gc+1), but the logs only show
    // GC.GetTotalMemory (= used bytes). They never showed (1) the COMMITTED heap size, so the free space in the heap when a collection starts was unknown (the trigger rule is unproven),
    // (2) whether the main thread was computing (marking) or waiting (page faults / paging) during the stall.
    // GCPROBE line, once per frame that contains a collection:
    //   used before -> after (freed), heapSize before -> after, free-in-heap before/after, bytes allocated since the previous collection and the rate,
    //   main-thread CPU ms and process CPU ms inside the frame (CPU ~ frame length = marking; CPU << frame length = waiting on memory/disk/locks), page faults inside the frame.
    // HITCH lines get the same CPU/page-fault fields at the end, so stalls WITHOUT a collection can be classified too.
    // mono_gc_get_heap_size / mono_gc_get_used_size are plain getters of the game's own mono.dll (found with GetProcAddress); if they are missing or the sanity check
    // (used size == GC.GetTotalMemory) fails the heap-size fields stay at -1 and everything else still works. Nothing here changes the game.
    public sealed partial class SteamBridgePlugin
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GpSizeFn();
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandleA(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes")] static extern bool GpGetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", EntryPoint = "GetThreadTimes")] static extern bool GpGetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")] static extern IntPtr GpGetCurrentProcess();
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThread")] static extern IntPtr GpGetCurrentThread();
        [StructLayout(LayoutKind.Sequential)] struct GpMem { public uint cb, PageFaultCount; public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage; }
        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] static extern bool GpGetProcessMemoryInfo(IntPtr h, out GpMem c, uint cb);

        static ConfigEntry<bool> gpCfg; static bool gpOn; static GpSizeFn gpHeapFn;
        static long gpHeapSize = -1, gpPrevHeapSize = -1, gpUsed, gpPrevUsed, gpAfterGcUsed = -1, gpAfterGcAt, gpMainCpu, gpProcCpu, gpPrevMainCpu, gpPrevProcCpu, gpLow = long.MaxValue, gpHigh; static uint gpPf, gpPrevPf; static int gpLines;

        void GpInstall()
        {
            try
            {
                gpCfg = Config.Bind("Diag", "GcProbe", true, "Adds GCPROBE records to perf-trace.txt (committed vs used mono heap around each collection, bytes allocated since the previous one, CPU time and page faults during the stall) and CPU/page-fault fields to HITCH lines. Cheap; log only.");
                if (!gpCfg.Value) return;
                string note = "";
                IntPtr mono = GetModuleHandleA("mono.dll");
                IntPtr ph = mono == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(mono, "mono_gc_get_heap_size"), pu = mono == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(mono, "mono_gc_get_used_size");
                if (ph != IntPtr.Zero && pu != IntPtr.Zero)
                {
                    GpSizeFn heapFn = (GpSizeFn)Marshal.GetDelegateForFunctionPointer(ph, typeof(GpSizeFn)), usedFn = (GpSizeFn)Marshal.GetDelegateForFunctionPointer(pu, typeof(GpSizeFn));
                    long hs = heapFn(), us = usedFn(), gt = GC.GetTotalMemory(false);
                    long tol = Math.Max(96L << 20, gt / 4);
                    if (hs >= us && us > 0 && hs < (1L << 40) && Math.Abs(us - gt) <= tol) { gpHeapFn = heapFn; note = "mono heapSize " + (hs >> 20) + " MB, used " + (us >> 20) + " MB, GC.GetTotalMemory " + (gt >> 20) + " MB (sanity ok)"; }
                    else note = "mono getters failed the sanity check (heap " + hs + ", used " + us + ", GC.GetTotalMemory " + gt + "): heap-size fields stay -1";
                }
                else note = "mono_gc_get_heap_size/used_size not found (mono.dll " + (mono == IntPtr.Zero ? "not found" : "loaded") + "): heap-size fields stay -1";
                if (mono != IntPtr.Zero) GpBoehmInit(mono, ref note);
                if (mono != IntPtr.Zero)
                {
                    ConfigEntry<bool> frOn = Config.Bind("Diag", "FreeRunsProbe", true, "Log-only: a background thread walks the Boehm free lists and logs the free-run histogram (fragmentation), allocd/threshold and black-list counters next to GCPROBE (FRP POST lines). Reads memory only, never writes; only for the analysed mono.dll sha256; switches itself off after three implausible walks.");
                    ConfigEntry<int> frMs = Config.Bind("Diag", "FreeRunsIntervalMs", 4000, "Interval of the PRE-collection free-run snapshot (1000..60000 ms).");
                    FrpInit(mono, frOn.Value, frMs.Value, true, ref note);
                }
                gpOn = true; GpSample(GC.GetTotalMemory(false)); gpAfterGcUsed = -1;
                PtLog("GCPROBE on: " + note);
            }
            catch (Exception e) { gpOn = false; PtLog("GCPROBE unavailable: " + e.GetType().Name + " " + e.Message); }
        }

        // every frame (called from PerfFrameTick): remember the previous sample, take the new one
        static void GpSample(long used)
        {
            try
            {
                gpPrevMainCpu = gpMainCpu; gpPrevProcCpu = gpProcCpu; gpPrevPf = gpPf; gpPrevHeapSize = gpHeapSize; gpPrevUsed = gpUsed;
                long c, e, k, u;
                if (GpGetThreadTimes(GpGetCurrentThread(), out c, out e, out k, out u)) gpMainCpu = k + u;
                if (GpGetProcessTimes(GpGetCurrentProcess(), out c, out e, out k, out u)) gpProcCpu = k + u;
                GpMem m = new GpMem(); m.cb = (uint)Marshal.SizeOf(typeof(GpMem));
                if (GpGetProcessMemoryInfo(GpGetCurrentProcess(), out m, m.cb)) gpPf = m.PageFaultCount;
                gpHeapSize = gpHeapFn != null ? gpHeapFn() : -1; gpUsed = used;
                if (used < gpLow) gpLow = used; if (used > gpHigh) gpHigh = used;   // sawtooth floor/peak since the previous collection (Boehm sweeps lazily, so the floor appears only some seconds after a collection)
            }
            catch (Exception) { gpOn = false; }
        }

        static string GpMb(long bytes) { return bytes < 0 ? "?" : (bytes >> 20).ToString(); }

        // appended to HITCH lines: CPU of the main thread / the process and page faults inside the stalled frame, committed mono heap
        static string GpHitchText()
        {
            if (!gpOn) return "";
            return " cpuMain=" + ((gpMainCpu - gpPrevMainCpu) / 10000) + "ms cpuProc=" + ((gpProcCpu - gpPrevProcCpu) / 10000) + "ms pf+" + (gpPf - gpPrevPf) + " heapSz=" + GpMb(gpHeapSize) + "MB";
        }

        // a frame that contained dg collections: record the free space / allocation numbers
        static void GpOnGc(double frameMs, int dg, int gcCount)
        {
            if (!gpOn || gpLines > 20000) return;
            try
            {
                gpLines++;
                long now = Stopwatch.GetTimestamp();
                double sinceS = gpAfterGcAt == 0 ? -1 : (now - gpAfterGcAt) * ptTick / 1000.0;
                long allocSince = gpAfterGcUsed >= 0 ? gpPrevUsed - gpAfterGcUsed : -1;
                string rate = (allocSince >= 0 && sinceS > 0.5) ? ((allocSince / 1048576.0) / sinceS).ToString("F2") : "?";
                long freeBefore = (gpPrevHeapSize >= 0 && gpPrevUsed > 0) ? gpPrevHeapSize - gpPrevUsed : -1, freeAfter = (gpHeapSize >= 0) ? gpHeapSize - gpUsed : -1;
                PtLog("GCPROBE gc#" + gcCount + " (+" + dg + ") frame " + frameMs.ToString("F0") + " ms; used " + GpMb(gpPrevUsed) + " -> " + GpMb(gpUsed) + " MB (freed ~" + GpMb(gpPrevUsed - gpUsed) + "); heapSize " + GpMb(gpPrevHeapSize) + " -> " + GpMb(gpHeapSize) + " MB; free in heap " + GpMb(freeBefore) + " -> " + GpMb(freeAfter) + " MB; since previous GC " + (sinceS < 0 ? "?" : sinceS.ToString("F1")) + " s, +" + GpMb(allocSince) + " MB (" + rate + " MB/s)" + GpBoehmText() + FrpText(freeBefore, gcCount) + "; used low/peak since previous GC " + GpMb(gpLow == long.MaxValue ? -1 : gpLow) + "/" + GpMb(gpHigh) + " MB; cpuMain " + ((gpMainCpu - gpPrevMainCpu) / 10000) + " ms, cpuProc " + ((gpProcCpu - gpPrevProcCpu) / 10000) + " ms, pf+" + (gpPf - gpPrevPf));
                gpAfterGcUsed = gpUsed; gpAfterGcAt = now; gpLow = gpUsed; gpHigh = gpUsed;
            }
            catch (Exception) { }
        }
    }
}
