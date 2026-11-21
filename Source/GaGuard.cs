using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CastleStorySteam
{
    // GaGuard (design GA4, reviewed by V2, 2026-11-04; patchset PATCHSET-freeze). Replaces the first half of GaProbe.cs (GaInstall / GaPre / GaPost / gaOffCfg); the Locomotion4 cache and the Boehm readers stay in GaProbe.cs.
    // Why: the game's GameAnalytics SDK (Assembly-CSharp-firstpass: GA_Queue / GA_Submit) keeps queueing events although the analytics server never answers, and every 10 s of game time
    // GA_Queue.ForceSubmit builds one JSON string with repeated string += (quadratic garbage, strings > 2 KB are large objects in Boehm) plus UTF8 bytes plus a WWW. The evening run
    // 2026-11-04 had full collections INSIDE ForceSubmit every 4th call. The old switch DisableGameAnalyticsSubmit only skipped ForceSubmit, so with it on GA_Queue._tempQueue (no cap,
    // drained only inside Submitted()) would grow for the whole match. This guard drops the events at the source instead.
    //
    // [Fixes] GameAnalyticsMode (int, restart needed; default 2 since the freeze patchset; an invalid value counts as 1):
    //   0 = vanilla: only the old GA-SUBMIT probe line (ForceSubmit pre/post, no change of behaviour, nothing patched on AddItem, no queue reads).
    //   1 = observe: GA-SUBMIT line + GA-QUEUE line per tick (queue sizes, estimated JSON size, allocation delta), AddItem counted by type. Behaviour unchanged (prefixes return true).
    //   2 = drop: GA_Queue.AddItem prefix returns false (no Item, no list growth, no later JSON), ForceSubmit prefix returns false. Per-type counts, first 5 drops logged, one summary per minute.
    // [Fixes] DisableGameAnalyticsSubmit (old key, kept): true = mode 2. Mode 2 is only honoured when the AddItem patch was installed; otherwise ForceSubmit is NOT skipped (an
    // AddItem-less skip would let _tempQueue grow without limit) and the mode falls back to 1.
    // Every prefix/postfix is wrapped in try/catch; on an exception the prefix returns true (vanilla), after 50 faults the guard switches itself to mode 0.
    // Arguments of AddItem are injected by INDEX with their exact types (__0 Hashtable, __1 GA_Submit.CategoryType): Assembly-CSharp-firstpass is already a reference of BuildPlugin.ps1.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> gaOffCfg; static ConfigEntry<int> gaModeCfg;
        static int gaMode, gaCalls, gaSkipped, gaFaults; static bool gaAddPatched; static long gaAlloc0 = -1, gaLarge0 = -1;
        // GA_Submit.CategoryType: GA_User = 0, GA_Event = 1, GA_Purchase = 2, GA_Error = 3 (slot 4 = anything else)
        static readonly int[] gaAdd = new int[5];            // AddItem calls since the last ForceSubmit tick (mode 1)
        static readonly int[] gaDropWin = new int[5];        // dropped since the last summary (mode 2)
        static readonly long[] gaDropTot = new long[5];      // dropped since start (mode 2)
        static long gaDropCount; static int gaLastSummary; static bool gaSummaryInit;
        static FieldInfo gaFQueue, gaFTemp, gaFErr, gaFUser, gaFSubm;
        static long gaMemBefore = -1, gaEstJson, gaEstGarbageKb, gaEstLarge, gaT0; static int gaQ = -1, gaT = -1, gaE = -1, gaAddedPrev; static string gaByTypePrev = "", gaStatePrev = "";
        const int GaAddedFieldsChars = 120;   // Submit() adds user_id, session_id, build to every item (about 120 chars as JSON); estimate only
        static readonly string[] GaTypeNames = { "User", "Event", "Purchase", "Error", "Other" };

        void GaInstall()
        {
            try
            {
                gaModeCfg = Config.Bind("Fixes", "GameAnalyticsMode", 2, "GameAnalytics SDK guard (restart needed; an invalid value counts as 1). 2 = drop (default): GA_Queue.AddItem drops every event and ForceSubmit does nothing. The analytics server never answers (every attempt of the game fails with Could not connect to server / Timeout), no game logic reads GA, and the 10 s submit job builds its JSON with repeated string += (quadratic garbage) which caused full-GC stalls. 1 = observe: GA-QUEUE lines (queue sizes, estimated JSON size, allocation per submit tick), nothing is changed. 0 = vanilla, only the GA-SUBMIT probe line. In DiagnosticsOnly mode the value is limited to 1.");
                gaOffCfg = Config.Bind("Fixes", "DisableGameAnalyticsSubmit", false, "OLD KEY, kept. true = same as GameAnalyticsMode 2 (drop events at AddItem and skip ForceSubmit).");
                int m = gaModeCfg.Value; if (m < 0 || m > 2) m = 1;
                if (gaOffCfg.Value && m < 2) m = 2;
                if (diagOnly && m > 1) { m = 1; PtLog("GA-GUARD: DiagnosticsOnly mode, GameAnalyticsMode limited to 1 (observe)"); }
                Type q = PtFindType("GA_Queue");
                MethodInfo fs = q == null ? null : q.GetMethod("ForceSubmit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (fs == null) { gaMode = 0; PtLog("GA-PROBE: GA_Queue.ForceSubmit not found"); return; }
                bool addOk = false;
                if (m >= 1)
                {
                    try
                    {
                        MethodInfo add = q.GetMethod("AddItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { typeof(Hashtable), typeof(GA_Submit.CategoryType), typeof(bool) }, null);
                        if (add == null) PtLog("GA-GUARD: GA_Queue.AddItem(Hashtable, CategoryType, bool) not found");
                        else
                        {
                            discoveryHarmony.Patch(add, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("GaAddPre", BindingFlags.Static | BindingFlags.NonPublic)));
                            addOk = true;
                        }
                    }
                    catch (Exception e) { PtLog("GA-GUARD: AddItem patch failed: " + e.GetType().Name + " " + e.Message); }
                    gaFQueue = q.GetField("_queue", BindingFlags.Static | BindingFlags.NonPublic); gaFTemp = q.GetField("_tempQueue", BindingFlags.Static | BindingFlags.NonPublic);
                    gaFErr = q.GetField("_errorQueue", BindingFlags.Static | BindingFlags.NonPublic); gaFUser = q.GetField("_userEventSuccess", BindingFlags.Static | BindingFlags.NonPublic);
                    gaFSubm = q.GetField("_submittingData", BindingFlags.Static | BindingFlags.NonPublic);
                }
                if (m >= 2 && !addOk) { PtLog("GA-GUARD: mode 2 refused (AddItem not patched, an AddItem-less skip would let the queue grow); using mode 1 = observe"); m = 1; }
                gaMode = m; gaAddPatched = addOk;
                discoveryHarmony.Patch(fs, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("GaPre", BindingFlags.Static | BindingFlags.NonPublic)), new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("GaPost", BindingFlags.Static | BindingFlags.NonPublic)));
                PtLog("GA-PROBE installed on GA_Queue.ForceSubmit; GameAnalyticsMode=" + gaMode + " (cfg " + gaModeCfg.Value + ", DisableGameAnalyticsSubmit=" + gaOffCfg.Value + ") AddItem patched=" + addOk
                    + " fields=" + (gaFQueue != null ? "q" : "-") + (gaFTemp != null ? "t" : "-") + (gaFErr != null ? "e" : "-") + (gaFUser != null ? "u" : "-") + (gaFSubm != null ? "s" : "-"));
            }
            catch (Exception e) { gaMode = 0; PtLog("GA-PROBE install failed: " + e.GetType().Name + " " + e.Message); }
        }

        // ---- GA_Queue.AddItem(Hashtable parameters, GA_Submit.CategoryType type, bool stack). Mode 1: count by type. Mode 2: drop (return false = original not run).
        static bool GaAddPre(Hashtable __0, GA_Submit.CategoryType __1)
        {
            try
            {
                int ti = (int)__1; if (ti < 0 || ti > 3) ti = 4;
                if (gaMode >= 2)
                {
                    Interlocked.Increment(ref gaDropWin[ti]);
                    long n = Interlocked.Increment(ref gaDropCount);
                    gaDropTot[ti]++;
                    if (n <= 5) GaLogDrop(n, ti, __0);
                    else if (Environment.TickCount - gaLastSummary >= 60000 || !gaSummaryInit) GaDropSummary();   // no ForceSubmit clock in mode 2: its periodic loop never starts
                    return false;
                }
                if (gaMode == 1) Interlocked.Increment(ref gaAdd[ti]);
            }
            catch (Exception e) { GaFault("AddItem", e); }
            return true;
        }

        static void GaLogDrop(long n, int ti, Hashtable h)
        {
            string id = "";
            if (ti == 1 && h != null) { string s = h["event_id"] as string; if (s != null) id = " event_id=" + (s.Length > 48 ? s.Substring(0, 48) : s); }
            PtLog("GA-DROP #" + n + " type=" + GaTypeNames[ti] + " keys=" + (h == null ? -1 : h.Count) + id);
        }

        static void GaFault(string where, Exception e)
        {
            gaFaults++;
            if (gaFaults <= 3) PtLog("GA-GUARD fault in " + where + ": " + e.GetType().Name + " " + e.Message);
            if (gaFaults == 50) { gaMode = 0; PtLog("GA-GUARD 50 faults: switched to mode 0 (vanilla)"); }
        }

        // ---- GA_Queue.ForceSubmit(). Prefix: observe numbers (mode 1) or skip (mode 2). Postfix always logs the GA-SUBMIT line (also when skipped: Harmony runs postfixes after a skipping prefix).
        static bool GaPre(out int __state)
        {
            __state = GC.CollectionCount(0); gaCalls++;
            if (gaMode >= 2)
            {
                gaSkipped++;
                try { GaDropSummary(); } catch (Exception e) { GaFault("summary", e); }
                return false;
            }
            gaAlloc0 = FrpAllocdNow(); gaLarge0 = FrpLargeObjNow();   // FreeRunsProbe getters, -1 when the probe is off
            if (gaMode == 1) { try { GaObservePre(); } catch (Exception e) { gaQ = -1; gaMemBefore = -1; GaFault("observe", e); } }
            return true;
        }

        static void GaObservePre()
        {
            gaQ = gaT = gaE = -1; gaEstJson = gaEstGarbageKb = gaEstLarge = -1;
            List<GA_Submit.Item> q = gaFQueue == null ? null : gaFQueue.GetValue(null) as List<GA_Submit.Item>;
            List<GA_Submit.Item> t = gaFTemp == null ? null : gaFTemp.GetValue(null) as List<GA_Submit.Item>;
            List<GA_Submit.Item> er = gaFErr == null ? null : gaFErr.GetValue(null) as List<GA_Submit.Item>;
            if (q != null) { gaQ = q.Count; GaEstimate(q); }
            if (t != null) gaT = t.Count;
            if (er != null) gaE = er.Count;
            int e0 = Interlocked.Exchange(ref gaAdd[0], 0), e1 = Interlocked.Exchange(ref gaAdd[1], 0), e2 = Interlocked.Exchange(ref gaAdd[2], 0), e3 = Interlocked.Exchange(ref gaAdd[3], 0), e4 = Interlocked.Exchange(ref gaAdd[4], 0);
            gaAddedPrev = e0 + e1 + e2 + e3 + e4;
            gaByTypePrev = "Event:" + e1 + ",Error:" + e3 + ",User:" + e0 + ",Purchase:" + e2 + (e4 > 0 ? ",Other:" + e4 : "");
            bool uo = gaFUser != null && (bool)gaFUser.GetValue(null), sb = gaFSubm != null && (bool)gaFSubm.GetValue(null);
            gaStatePrev = " userOk=" + (gaFUser == null ? "?" : (uo ? "1" : "0")) + " subm=" + (gaFSubm == null ? "?" : (sb ? "1" : "0"));
            gaMemBefore = GC.GetTotalMemory(false);   // last: the numbers above allocate a little themselves
            gaT0 = Stopwatch.GetTimestamp();
        }

        // What GA_Submit.Submit() will do with this queue, estimated without touching the game's data (at most 40 sampled items, one pass over the item types):
        //  estJsonChars   = characters of all JSON strings (one per category type): payload of the sampled items scaled up, + about 120 chars per item for user_id / session_id / build that Submit() adds
        //  estGarbageKB   = DictToJson builds each JSON with repeated string +=: about (2 * keys + 2) concatenations per item, each copies the string built so far (average length = json / 2 chars = json bytes),
        //                   so garbage bytes per category = concatenations * json chars (checked against the offline stand-in: 0.98 - 1.00 of the measured value when one category dominates)
        //  estLargeObj    = those concatenation results that are larger than 2 KB (large objects in Boehm)
        static void GaEstimate(List<GA_Submit.Item> q)
        {
            int n = q.Count; gaEstJson = 0; gaEstGarbageKb = 0; gaEstLarge = 0;
            if (n == 0) return;
            int step = n <= 40 ? 1 : n / 40;
            int[] cnt = new int[5], seen = new int[5]; long[] chars = new long[5], cats = new long[5];
            for (int i = 0; i < n; i++)
            {
                GA_Submit.Item it = q[i]; int ti = (int)it.Type; if (ti < 0 || ti > 3) ti = 4;
                cnt[ti]++;
                if (i % step != 0) continue;
                Hashtable h = it.Parameters; seen[ti]++;
                long c = 3; int keys = 0;
                if (h != null)
                {
                    keys = h.Count;
                    IDictionaryEnumerator en = h.GetEnumerator();
                    while (en.MoveNext()) { string k = en.Key as string, v = en.Value as string; c += (k != null ? k.Length : 8) + (v != null ? v.Length : 10) + 7; }
                    if (!h.ContainsKey("user_id")) { c += GaAddedFieldsChars; keys += 3; }
                }
                chars[ti] += c; cats[ti] += 2 * keys + 2;
            }
            double garbage = 0, large = 0;
            for (int t = 0; t < 5; t++)
            {
                if (cnt[t] == 0 || seen[t] == 0) continue;
                double json = 2.0 + (double)chars[t] * cnt[t] / seen[t];
                double concats = (double)cats[t] * cnt[t] / seen[t];
                gaEstJson += (long)json; garbage += concats * json;
                if (json > 1014.0) large += concats * (1.0 - 1014.0 / json);
            }
            gaEstGarbageKb = (long)(garbage / 1024.0); gaEstLarge = (long)large;
        }

        static void GaPost(int __state)
        {
            try
            {
                long memAfter = GC.GetTotalMemory(false);   // first thing: before the log line allocates
                double callMs = gaMode == 1 && gaMemBefore >= 0 ? (Stopwatch.GetTimestamp() - gaT0) * 1000.0 / Stopwatch.Frequency : -1.0;
                int d = GC.CollectionCount(0) - __state;
                bool skipped = gaMode >= 2;
                string tail = "";
                long allocKb = -1;
                if (gaMode == 1 && gaMemBefore >= 0) allocKb = d != 0 ? -1 : (memAfter - gaMemBefore) / 1024;
                if (gaMode == 1) tail = " queue=" + gaQ + " temp=" + gaT + " err=" + gaE + " estJsonChars=" + gaEstJson + " allocKB=" + allocKb + " estGarbageKB=" + gaEstGarbageKb + " estLargeObj=" + gaEstLarge + " callMs=" + callMs.ToString("F1");
                if (!skipped && d == 0 && gaAlloc0 >= 0)   // bytes allocated (all threads) while ForceSubmit ran, from the Boehm counter; only meaningful when no collection ran inside
                {
                    long a1 = FrpAllocdNow(), l1 = FrpLargeObjNow();
                    if (a1 >= gaAlloc0) tail += " allocd+" + ((a1 - gaAlloc0) >> 10) + "KB" + (l1 >= 0 && gaLarge0 >= 0 ? " largeObj" + (l1 >= gaLarge0 ? "+" : "") + ((l1 - gaLarge0) >> 10) + "KB" : "");
                }
                PtLog("GA-SUBMIT #" + gaCalls + (skipped ? " SKIPPED" : "") + " game time " + Time.time.ToString("F1") + " timeScale " + Time.timeScale.ToString("F2") + " gc inside +" + d + " gc total " + GC.CollectionCount(0) + tail);
                if (gaMode == 1 && (gaCalls <= 30 || gaCalls % 6 == 0 || gaQ > 50 || allocKb > 2048 || gaEstGarbageKb > 2048 || callMs > 50.0))
                    PtLog("GA-QUEUE tick=" + gaCalls + " queue=" + gaQ + " temp=" + gaT + " err=" + gaE + " estJsonChars=" + gaEstJson + " allocKB=" + allocKb + " addItems=" + gaAddedPrev + " byType=" + gaByTypePrev + gaStatePrev + " estGarbageKB=" + gaEstGarbageKb + " estLargeObj=" + gaEstLarge + " callMs=" + callMs.ToString("F1"));
            }
            catch (Exception) { }
        }

        // V2 review fix: heartbeat for the mode 2 summary. In mode 2 ForceSubmit is skipped, so the SDK's own AverageFPS event (added inside ForceSubmit) is gone and AddItem is called rarely
        // (CriticalFPS only when fps <= 30, LevelStarted, a few error items). The minute summary used to hang on those calls, so it could stay silent for long stretches and the acceptance check
        // 'queue/temp/err stay 0' had no data. GaHeartbeat() is cheap (one Environment.TickCount read per frame in mode 2, nothing in other modes). Optional one-line call from PerfFrameTick (PerfTrace.cs), main thread.
        internal static void GaHeartbeat()
        {
            if (gaMode < 2) return;
            try { GaDropSummary(); } catch (Exception e) { GaFault("heartbeat", e); }
        }

        // V2 review fix: one line for the perf-trace segment header. RollingTrace repeats the header in every segment, but the boot-time GA-PROBE / GA-DROP #n / GA-SUBMIT #1 lines roll away after about
        // an hour (the evening run lost its first hour), and then nobody can tell which mode the file was recorded with. Optional edit in PerfTrace.cs, in the ptHeader assignment: append + GaHeaderText() + backslash-r backslash-n (CRLF).
        internal static string GaHeaderText() { return "GA-GUARD mode=" + gaMode + " addItemPatched=" + gaAddPatched; }

        // ---- mode 2: first 5 drops are logged by GaLogDrop, then one summary per minute (called from the AddItem prefix; in mode 2 the game's periodic submit loop never starts, so ForceSubmit is no clock)
        static void GaDropSummary()
        {
            int now = Environment.TickCount;
            if (!gaSummaryInit) { gaSummaryInit = true; gaLastSummary = now; return; }
            if (now - gaLastSummary < 60000) return;
            gaLastSummary = now;
            int a0 = Interlocked.Exchange(ref gaDropWin[0], 0), a1 = Interlocked.Exchange(ref gaDropWin[1], 0), a2 = Interlocked.Exchange(ref gaDropWin[2], 0), a3 = Interlocked.Exchange(ref gaDropWin[3], 0), a4 = Interlocked.Exchange(ref gaDropWin[4], 0);
            int q = -1, t = -1, e = -1;
            try
            {
                List<GA_Submit.Item> lq = gaFQueue == null ? null : gaFQueue.GetValue(null) as List<GA_Submit.Item>; if (lq != null) q = lq.Count;
                List<GA_Submit.Item> lt = gaFTemp == null ? null : gaFTemp.GetValue(null) as List<GA_Submit.Item>; if (lt != null) t = lt.Count;
                List<GA_Submit.Item> le = gaFErr == null ? null : gaFErr.GetValue(null) as List<GA_Submit.Item>; if (le != null) e = le.Count;
            }
            catch (Exception) { }
            PtLog("GA-DROP summary 60s: dropped=" + (a0 + a1 + a2 + a3 + a4) + " (Event:" + a1 + ",Error:" + a3 + ",User:" + a0 + ",Purchase:" + a2 + (a4 > 0 ? ",Other:" + a4 : "") + ") total=" + gaDropCount
                + " submitSkipped=" + gaSkipped + " queue=" + q + " temp=" + t + " err=" + e);
        }
    }
}
