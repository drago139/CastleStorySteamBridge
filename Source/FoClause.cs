using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.104 GC-cause probe + GC pacing switch. Evidence (offline lab + side-session reports A6/A11, and the first in-game run of 0.3.103 on 2026-11-03 13:15-13:55, solo match):
    //  * The game's mono.dll (sha256 5661FA36...295EA) has a Mono libgc patch in GC_collect_or_expand (RVA 0x157168): when the allocator finds no free block it COLLECTS instead of expanding the heap if
    //    GC_fo_entries > last_fo_entries + 500 && (last_words_finalized | GC_words_finalized) != 0. The game's MoonSharp registers 10^2..10^3 finalizable objects per second (fo_entries ~1.1M, 26-30k new
    //    registrations between two collections), so the clause is ALWAYS true: 143 of 150 collections in the first run were verdict FO-CLAUSE.
    //  * GCPROBE showed the effect: the committed heap never grows ("heapSize 1680 -> 1680 MB"), free space is 0-6 MB when a collection starts and ~105 MB after it, so a collection comes as soon as ~100 MB
    //    were allocated (every ~28 s at 3.6 MB/s) although 4 GB of RAM were free. Every collection is a ~0.7 s stop (cpuMain ~ frame length: CPU-bound mark, not paging).
    //  1) READ-ONLY probe (needs [Diag] GcProbe and the analysed mono.dll): GC-CAUSE line per collection frame (EXPLICIT / FO-CLAUSE / ALLOCATOR other from the induced counter, fo_entries, last_fo_entries)
    //     and FO-RATE every 30 s. Only plain memory reads of the runtime's own globals.
    //  2) SWITCH [Fixes] GcMinCollectIntervalSeconds (default 0 = off = vanilla). With T > 0 (20..900) the mod holds the finalizer clause OFF for T seconds after every collection by keeping
    //     last_fo_entries at a huge value (one 8-byte data write per collection), so for T seconds the allocator EXPANDS the heap instead of collecting; Boehm's own rule (collect after live/2 bytes were
    //     allocated) still applies as a cap. After T seconds last_fo_entries is set to 0 (clause true again): the next allocator failure collects, the collection rewrites last_fo_entries, a new hold starts.
    //     Effect: at most one clause-driven collection per T seconds (stalls/hour 130 -> 3600/T), costs heap of about (allocation rate x T), capped by live/2 (~0.8 GB at 1.6 GB live). Stands down
    //     (vanilla, last_fo_entries = fo_entries) while free RAM < [Fixes] GcMinCollectMinFreeRamMB. Pause LENGTH does not change.
    public sealed partial class SteamBridgePlugin
    {
        const long FoRvaFoEntries = 0x266CD8, FoRvaLastFo = 0x266BE0, FoRvaLastWords = 0x266BE8, FoRvaWords = 0x279838, FoRvaPerf = 0x265B18, FoRvaGcNo = 0x266B88;
        const long FoHuge = 0x4000000000000000L;
        static bool foOk; static IntPtr foBase; static ConfigEntry<int> foIntCfg, foRamCfg;
        static long foFo, foLast, foLw, foW, foPFo, foPLast, foPLw, foPW; static int foInd = -1, foPInd = -1, foLastGc; static bool foRamOk = true, foHeld, foActive; static int foPokes, foLines;
        static float foNextRate, foNextRam, foGcAt; static long foRateFo; static float foRateT; static int foRateGc, foRateInd;

        static long FoRd(long rva) { return Marshal.ReadInt64(new IntPtr(foBase.ToInt64() + rva)); }
        static int FoRdInduced()
        {
            long perf = Marshal.ReadInt64(new IntPtr(foBase.ToInt64() + FoRvaPerf));
            return perf == 0 ? -1 : Marshal.ReadInt32(new IntPtr(perf + 0x5C));
        }
        static void FoWriteLast(long value) { Marshal.WriteInt64(new IntPtr(foBase.ToInt64() + FoRvaLastFo), value); }

        void FoInstall()
        {
            try
            {
                foIntCfg = Config.Bind("Fixes", "GcMinCollectIntervalSeconds", 0, "EXPERIMENT, 0 = off (vanilla). The game's Mono collects the whole heap whenever the allocator finds no free block (a libgc rule triggered by the game's constant finalizer registrations), so the heap never grows and a 0.7 s stall comes every ~20-40 s. With a value (20..900, try 120) the mod keeps that rule off for this many seconds after each collection: the heap grows instead (about allocation rate x seconds, capped by Boehm's live/2 rule), stalls come at most that often. Stands down when free RAM is below GcMinCollectMinFreeRamMB. Only works with the analysed mono.dll (sha256 checked).");
                foRamCfg = Config.Bind("Fixes", "GcMinCollectMinFreeRamMB", 2500, "GcMinCollectIntervalSeconds stands down (vanilla behaviour) while the system has less free RAM than this.");
                if (!foOk) { PtLog("GC-FO probe off: mono.dll is not the analysed build (or GcProbe is off)"); return; }
                foFo = FoRd(FoRvaFoEntries); foLast = FoRd(FoRvaLastFo); foLw = FoRd(FoRvaLastWords); foW = FoRd(FoRvaWords); foInd = FoRdInduced();
                if (foFo < 0 || foFo > 100000000L || foLast < 0 || foLast > 100000000L || foInd < 0 || foInd > 100000000) { foOk = false; PtLog("GC-FO probe disabled: implausible values fo=" + foFo + " last=" + foLast + " induced=" + foInd); return; }
                foPFo = foFo; foPLast = foLast; foPLw = foLw; foPW = foW; foPInd = foInd; foLastGc = GC.CollectionCount(0); foRateFo = foFo; foRateT = Time.realtimeSinceStartup; foRateGc = foLastGc; foRateInd = foInd;
                foNextRate = foRateT + 30f; foNextRam = foRateT; foGcAt = foRateT;
                PtLog("GC-FO probe on: fo_entries " + foFo + ", last_fo_entries " + foLast + ", last_words_finalized " + foLw + ", words_finalized " + foW + ", induced collections so far " + foInd + "; GcMinCollectIntervalSeconds=" + foIntCfg.Value + " (0 = off), min free RAM " + foRamCfg.Value + " MB");
            }
            catch (Exception e) { foOk = false; PtLog("GC-FO install failed: " + e.GetType().Name + " " + e.Message); }
        }

        // every frame, before the frame is evaluated: remember the previous sample, read the new one
        static void FoSample()
        {
            if (!foOk) return;
            try
            {
                foPFo = foFo; foPLast = foLast; foPLw = foLw; foPW = foW; foPInd = foInd;
                foFo = FoRd(FoRvaFoEntries); foLast = FoRd(FoRvaLastFo); foLw = FoRd(FoRvaLastWords); foW = FoRd(FoRvaWords); foInd = FoRdInduced();
            }
            catch (Exception) { foOk = false; }
        }

        // a frame that contained dg collections: why?
        static void FoOnGc(int dg, double frameMs, int gcCount)
        {
            if (!foOk || foLines > 30000) return;
            try
            {
                foLines++;
                long deltaPre = foPFo - foPLast; bool flags = (foPLw | foPW) != 0; bool clausePre = deltaPre > 500 && flags;
                int dInd = foInd - foPInd; bool allocatorGc = foLast != foPLast;
                string verdict = dInd > 0 ? "EXPLICIT (GC.Collect / GetTotalMemory(true) / mono_gc_collect, induced +" + dInd + ")" : (allocatorGc ? (clausePre ? "FO-CLAUSE (allocator, " + deltaPre + " finalizer registrations since the last collection)" : (foPLast >= FoHuge ? "ALLOCATOR other (clause held off by the mod; Boehm's live/2 rule or a failed big allocation)" : "ALLOCATOR other (rule live/2 or free-block failure, fo delta " + deltaPre + ")")) : "OTHER (induced flat, allocator bookkeeping unchanged)");
                PtLog("GC-CAUSE gc#" + gcCount + " (+" + dg + ") frame " + frameMs.ToString("F0") + " ms: " + verdict + "; fo_entries " + foPFo + " -> " + foFo + ", last_fo_entries " + (foPLast >= FoHuge ? "HELD" : foPLast.ToString()) + " -> " + foLast + ", flags(last_words|words) " + (flags ? "set" : "zero") + (foActive ? ", pacing " + foIntCfg.Value + " s active" : "") + ", game time " + Time.time.ToString("F1") + " timeScale " + Time.timeScale.ToString("F2"));
            }
            catch (Exception) { }
        }

        // every frame, after the frame: rate line, RAM guard, pacing writes
        static void FoTune(int gcNow)
        {
            if (!foOk) return;
            try
            {
                float t = Time.realtimeSinceStartup;
                if (gcNow != foLastGc) { foLastGc = gcNow; foGcAt = t; foHeld = false; }
                if (t >= foNextRate)
                {
                    float dt = t - foRateT; if (dt > 1f) PtLog("FO-RATE " + dt.ToString("F0") + " s: fo_entries " + foRateFo + " -> " + foFo + " (" + ((foFo - foRateFo) / dt).ToString("F1") + "/s net), collections +" + (gcNow - foRateGc) + ", explicit +" + (foInd - foRateInd) + ", last_fo " + (foLast >= FoHuge ? "HELD" : foLast.ToString()) + ", game time " + Time.time.ToString("F0") + " scale " + Time.timeScale.ToString("F2"));
                    foRateFo = foFo; foRateT = t; foRateGc = gcNow; foRateInd = foInd; foNextRate = t + 30f;
                }
                int T = foIntCfg == null ? 0 : foIntCfg.Value;
                if (T <= 0) { if (foActive) { foActive = false; if (foLast >= FoHuge || foLast < foFo) { FoWriteLast(foFo); foLast = foFo; } PtLog("GC-PACE off (value " + T + "): last_fo_entries restored"); } return; }
                if (T > 900) T = 900; if (T < 20) T = 20;
                if (t >= foNextRam)
                {
                    foNextRam = t + 10f; MemStatusEx m = new MemStatusEx(); bool ok = !GlobalMemoryStatusEx(m) || (long)(m.ullAvailPhys / 1048576UL) >= foRamCfg.Value;
                    if (ok != foRamOk) { foRamOk = ok; PtLog("GC-PACE " + (ok ? "resumed" : "standing down") + ": free RAM " + (m.ullAvailPhys / 1048576UL) + " MB, limit " + foRamCfg.Value + " MB"); if (!ok) { foActive = false; FoWriteLast(foFo); foLast = foFo; } }
                }
                if (!foRamOk) return;
                if (t - foGcAt < T)
                {
                    if (foLast != FoHuge) { FoWriteLast(FoHuge); foLast = FoHuge; foPokes++; foHeld = true; if (!foActive) { foActive = true; PtLog("GC-PACE active: clause held off for " + T + " s after each collection (heap " + (gpHeapSize >> 20) + " MB, used " + (gpUsed >> 20) + " MB)"); } else if (foPokes % 50 == 1) PtLog("GC-PACE hold #" + foPokes + " started (heap " + (gpHeapSize >> 20) + " MB, used " + (gpUsed >> 20) + " MB, fo " + foFo + ")"); }
                }
                else if (foLast == FoHuge)
                {
                    FoWriteLast(0); foLast = 0; foHeld = false; if (foPokes % 50 == 1) PtLog("GC-PACE released after " + T + " s: the next allocator failure collects (heap " + (gpHeapSize >> 20) + " MB, used " + (gpUsed >> 20) + " MB)");
                }
            }
            catch (Exception) { foOk = false; PtLog("GC-FO probe stopped after an exception"); }
        }
    }
}