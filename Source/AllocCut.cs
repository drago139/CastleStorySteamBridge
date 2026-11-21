using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.Components;
using HarmonyLib;
using UnityEngine;

namespace CastleStorySteam
{
    // ALLOCATION-RATE CUT (wave 2 C2 sketch, integrated in the freeze build; pure parts in AllocCutRules.cs).
    // Evidence: in the 2 h guest match the GC interval was about free-after-GC / allocation rate and the rate rose 1.4 -> ~5 MB/s when a SharedFactionVision NullReferenceException storm
    // started (~21:18): a destroyed LocalFactionVision stays in SharedFactionVision.AllVisions, AssignDetectionStatusFromAllVisions (and AssignCollisionsFromPartialGroup / IsInRange) read
    // allVision.transform without a null check, the game's shared enumerator (Common.Enumerator, a struct) is stuck on step 2 and throws again on every BrixUpdate of every faction
    // (7 per frame); each throw is logged through log4net + UnityLogAppender (~9 KB garbage per event, ~16 k events/min, 100 MB brix.log per 4 min).
    // (a) [Fixes] StaleVisionGuard 0 / 1 / 2:  0 = off (nothing installed). 1 = OBSERVE (default): every 10 s count the dead entries in the game's vision lists (read only), count the error
    //     events that mention SharedFactionVision, write SFV-COUNT every 60 s; no Harmony patch, nothing is changed. 2 = ON: also remove the dead entries (every StaleVisionPurgeSeconds and
    //     right after an error) and swallow that one error with a Harmony FINALIZER on SharedFactionVision.BrixUpdate. The empty file BepInEx\stalevision-off.flag turns mode 2 back into observe at once.
    // (b) [Fixes] RepeatedErrorLogLimit (default 0 = off): Harmony PREFIX on log4net Logger.Log(Type, Level, object, Exception); ERROR+ events with the same digit-stripped message / exception
    //     type are logged N times per window, the rest are dropped before LoggingEvent / appenders run (counted in perf-trace.txt as LOG-LIMIT).
    // (c) the HitchProfiler probe fix lives in HitchProfiler.cs (struct __state instead of Stack<long>).
    // Every part falls back to vanilla on any reflection mismatch and never throws into the game.
    public sealed partial class SteamBridgePlugin
    {
        static int acMode, acPurgeEvery, acLogLimit, acLogWindowMs;
        static bool acThrottleOn, acReflectOk, acFinalizerOn;
        static volatile bool acKill;
        static string acKillFile = "";
        static FieldInfo acFCommon, acFShared, acFEdge;
        static SharedFactionVision acAnySfv;
        static float acNextFind, acNextSummary, acNextFlag, acNextScan, acNextStats;
        static int acSwallowed, acPurgedTotal, acProactiveRuns, acProactiveHits, acStaleLogs, acNre, acStaleNow, acStaleMax, acUnits, acFactions;
        static long acDroppedAtSummary;
        static AcThrottle acThrottle;
        static readonly List<string> acPending = new List<string>();
        static readonly Predicate<LocalFactionVision> acAlive = delegate(LocalFactionVision x) { return x != null; };   // Unity's overloaded == (destroyed object counts as null)

        void InstallAllocCut()
        {
            try
            {
                acMode = AcRules.NormalizeMode(Config.Bind("Fixes", "StaleVisionGuard", 1, new ConfigDescription("Fog/vision bug: a destroyed unit's vision component stays in the game's vision lists and makes SharedFactionVision throw an error on every frame (thousands of log lines per minute, much garbage, more frequent freezes). 0 = off. 1 = observe (default): only counts dead entries and error events and writes SFV-COUNT to perf-trace.txt, changes nothing. 2 = on: removes the dead entries and swallows that one error. Restart needed. The empty file BepInEx\\stalevision-off.flag turns 2 back into 1 at once.", new AcceptableValueRange<int>(0, 2))).Value);
                acPurgeEvery = Math.Max(0, Math.Min(60, Config.Bind("Fixes", "StaleVisionPurgeSeconds", 2, "StaleVisionGuard=2: also remove dead vision entries proactively every N seconds (0 = only after an error). Restart needed.").Value));
                acLogLimit = Math.Max(0, Math.Min(1000, Config.Bind("Fixes", "RepeatedErrorLogLimit", 0, "Game error log: log at most this many ERROR events with the same message per window (the rest are counted in perf-trace.txt as LOG-LIMIT). 0 = off (vanilla, every event is logged and costs garbage and disk writes). Try 5 only while the vision error storm is a problem. Restart needed.").Value));
                acLogWindowMs = Math.Max(1, Math.Min(3600, Config.Bind("Fixes", "RepeatedErrorLogWindowSeconds", 60, "Window length (seconds) for RepeatedErrorLogLimit. Restart needed.").Value)) * 1000;
                acKillFile = Path.Combine(BepInEx.Paths.BepInExRootPath, "stalevision-off.flag");
                string guard = "off";
                if (AcRules.Counts(acMode))
                {
                    try
                    {
                        AcPrepareReflection(); Application.logMessageReceivedThreaded += AcOnUnityLog; guard = acMode == AcRules.ModeOn ? "on" : "observe";
                        if (acMode == AcRules.ModeOn) { try { AcPatchVisionGuard(); acFinalizerOn = true; } catch (Exception e) { acFinalizerOn = false; guard = "observe (finalizer unavailable: " + e.GetType().Name + " " + e.Message + ")"; } }
                    }
                    catch (Exception e) { acReflectOk = false; guard = "FAILED (" + e.GetType().Name + " " + e.Message + ")"; }
                }
                if (acLogLimit > 0) { try { AcInstallLogLimiter(); } catch (Exception e) { acThrottleOn = false; Logger.LogWarning("ALLOC-CUT log limiter unavailable: " + e.GetType().Name + " " + e.Message); } }
                string line = "SFV-GUARD mode=" + acMode + " (" + guard + "), reflection " + (acReflectOk ? "ok" : "n/a") + ", finalizer " + (acFinalizerOn ? "on" : "off") + ", purge every " + acPurgeEvery + " s, log limiter " + (acThrottleOn ? "on (" + acLogLimit + " per " + (acLogWindowMs / 1000) + " s)" : "off") + ", kill file " + acKillFile;
                Logger.LogInfo(line); PtLog(line);
            }
            catch (Exception e) { Logger.LogWarning("ALLOC-CUT unavailable: " + e.Message); }
        }

        // ---- (a) StaleVisionGuard -------------------------------------------------------------------------------------------------------------

        // reflection into the game's private vision structures; throws when the layout differs (then the guard stays off)
        void AcPrepareReflection()
        {
            Type sfv = typeof(SharedFactionVision);
            Type common = sfv.GetNestedType("Common", BindingFlags.NonPublic);
            acFCommon = AccessTools.Field(sfv, "_common"); acFEdge = AccessTools.Field(sfv, "_edgeVisions");
            acFShared = common == null ? null : AccessTools.Field(common, "SharedVisions");
            if (acFCommon == null || acFEdge == null || acFShared == null) throw new MissingMemberException("SharedFactionVision layout differs (_common/_edgeVisions/Common.SharedVisions)");
            if (acFShared.FieldType != typeof(List<SharedFactionVision>) || acFEdge.FieldType != typeof(List<LocalFactionVision>) || acFCommon.FieldType != common) throw new MissingMemberException("SharedFactionVision field types differ");
            acReflectOk = true;
        }

        void AcPatchVisionGuard()
        {
            Type sfv = typeof(SharedFactionVision);
            MethodInfo bu = sfv.GetMethod("BrixUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            MethodInfo awake = sfv.GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (bu == null || awake == null) throw new MissingMemberException("SharedFactionVision.BrixUpdate/Awake not found");
            discoveryHarmony.Patch(bu, null, null, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AcVisionFinalizer", BindingFlags.Static | BindingFlags.NonPublic)), null);   // 6-argument overload: the 5-argument one is [Obsolete(error)] in this Harmony
            discoveryHarmony.Patch(awake, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AcSfvAwake", BindingFlags.Static | BindingFlags.NonPublic)));
        }

        static void AcSfvAwake(SharedFactionVision __instance) { acAnySfv = __instance; }

        // Unity log callback (any thread): counts error events that mention SharedFactionVision. Allocation free.
        static void AcOnUnityLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (type != LogType.Exception && type != LogType.Error) return;
                if ((condition != null && condition.IndexOf("SharedFactionVision", StringComparison.Ordinal) >= 0) || (stackTrace != null && stackTrace.IndexOf("SharedFactionVision", StringComparison.Ordinal) >= 0)) Interlocked.Increment(ref acNre);
            }
            catch (Exception) { }
        }

        // Runs after every BrixUpdate of a SharedFactionVision (mode 2 only); __exception == null on the healthy path.
        static Exception AcVisionFinalizer(SharedFactionVision __instance, Exception __exception)
        {
            if (__exception == null) return null;
            try
            {
                if (!AcRules.Acts(acMode, acKill)) return __exception;
                bool known = __exception is NullReferenceException || __exception is MissingReferenceException;   // unknown failure -> vanilla path (game logs it)
                if (!known) return __exception;
                int removed = AcScan(__instance, true, true);
                if (!AcRules.ShouldSwallow(known, removed)) return __exception;   // the error does not come from dead entries -> vanilla
                acSwallowed++; acAnySfv = __instance;
                return null;                                                       // the stuck step is idempotent: the next call retries it with a clean list
            }
            catch (Exception) { return __exception; }
        }

        // Counts (and, with remove, deletes) destroyed (UnityEngine.Object == null) entries of every faction's AllVisions; after an aborted step also empties the partial _edgeVisions list.
        // Returns the number of dead entries FOUND. Called only between steps of the game's enumerator (finalizer after the aborted call, or from the plugin's Update), never inside a foreach over AllVisions.
        static int AcScan(SharedFactionVision any, bool remove, bool afterError)
        {
            object common = acFCommon.GetValue(any);
            List<SharedFactionVision> all = common == null ? null : acFShared.GetValue(common) as List<SharedFactionVision>;
            if (all == null) return 0;
            int found = 0, units = 0; string detail = null;
            for (int s = 0; s < all.Count; s++)
            {
                SharedFactionVision sf = all[s]; if (sf == null) continue;
                List<LocalFactionVision> v = sf.AllVisions; if (v == null) continue;
                int c = v.Count; units += c;
                int dead = remove ? AcRules.Compact(v, acAlive) : AcRules.CountDead(v, acAlive);
                if (dead > 0) { found += dead; if (remove && acStaleLogs < 5) detail = (detail == null ? "" : detail + ", ") + "faction " + sf.Faction.ID + " " + dead + "/" + c; }
                if (remove && afterError) { List<LocalFactionVision> e = acFEdge.GetValue(sf) as List<LocalFactionVision>; if (e != null && e.Count > 0) e.Clear(); }
            }
            acUnits = units; acFactions = all.Count; acStaleNow = found; if (found > acStaleMax) acStaleMax = found;
            if (remove && found > 0)
            {
                acPurgedTotal += found;
                if (acStaleLogs < 5) { acStaleLogs++; PtLog("SFV-STALE " + (afterError ? "after error" : "proactive") + ": removed=" + found + " destroyed vision entries (" + detail + "), game time " + Time.time.ToString("F1") + ", totalSwallowed=" + acSwallowed); }
            }
            return found;
        }

        // ---- (b) repeated-error log limiter ----------------------------------------------------------------------------------------------------

        void AcInstallLogLimiter()
        {
            MethodInfo log = typeof(log4net.Repository.Hierarchy.Logger).GetMethod("Log", new Type[] { typeof(Type), typeof(log4net.Core.Level), typeof(object), typeof(Exception) });
            if (log == null) throw new MissingMethodException("log4net Logger.Log(Type, Level, object, Exception)");
            acThrottle = new AcThrottle(acLogLimit, acLogWindowMs);
            discoveryHarmony.Patch(log, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AcLogPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
            acThrottleOn = true;
        }

        // Parameter names must equal the original's (verified with Cecil: callerStackBoundaryDeclaringType, level, message, exception). Return false = event dropped.
        static bool AcLogPrefix(log4net.Core.Level level, object message, Exception exception)
        {
            try
            {
                if (level == null || level.Value < 40000) return true;                       // below ERROR (log4net ERROR = 40000)
                string s = message as string;
                int key = AcRules.ThrottleKey(s, s == null && message != null ? message.GetType().GetHashCode() : 0, exception != null ? exception.GetType().GetHashCode() : 0);
                string closed;
                bool pass = acThrottle.Allow(key, Environment.TickCount, s, s == null && message != null ? message.GetType().Name : null, out closed);
                if (closed != null) lock (acPending) { if (acPending.Count < 64) acPending.Add("LOG-LIMIT " + closed); }
                return pass;
            }
            catch (Exception) { return true; }
        }

        // ---- main-thread tick (plugin Update) --------------------------------------------------------------------------------------------------

        void AllocCutTick()
        {
            if (!acReflectOk && !acThrottleOn) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now >= acNextFlag) { acNextFlag = now + 5f; try { acKill = File.Exists(acKillFile); } catch (Exception) { } }
                if (acReflectOk)
                {
                    bool act = AcRules.Acts(acMode, acKill) && acFinalizerOn;
                    float every = act && acPurgeEvery > 0 ? acPurgeEvery : 10f;
                    if (now >= acNextScan)
                    {
                        acNextScan = now + every;
                        if (acAnySfv == null)   // none created yet, or destroyed with the level: look again at most every 10 s
                        {
                            if (now >= acNextFind) { acNextFind = now + 10f; acAnySfv = UnityEngine.Object.FindObjectOfType<SharedFactionVision>(); }
                        }
                        if (acAnySfv != null) { acProactiveRuns++; if (AcScan(acAnySfv, act, false) > 0 && act) acProactiveHits++; }
                    }
                    if (now >= acNextSummary && acAnySfv != null)
                    {
                        acNextSummary = now + 60f;
                        PtLog("SFV-COUNT staleNow=" + acStaleNow + " staleMax=" + acStaleMax + " nre=" + acNre + " units=" + acUnits + " factions=" + acFactions + " mode=" + acMode + (acKill ? " KILLED" : "") + " swallowed=" + acSwallowed + " purged=" + acPurgedTotal + " logDropped=" + (acThrottle == null ? 0 : acThrottle.DroppedTotal) + " gameTime=" + Time.time.ToString("F0"));
                    }
                }
                if (acThrottleOn)
                {
                    string[] lines = null;
                    lock (acPending) { if (acPending.Count > 0) { lines = acPending.ToArray(); acPending.Clear(); } }
                    if (lines != null) for (int i = 0; i < lines.Length; i++) PtLog(lines[i]);
                    long dropped = acThrottle.DroppedTotal;
                    if (now >= acNextStats && dropped != acDroppedAtSummary)   // at most every 30 s while drops continue
                    {
                        acNextStats = now + 30f; acDroppedAtSummary = dropped;
                        PtLog("ALLOC-CUT stats: finalizer swallowed " + acSwallowed + ", purged entries " + acPurgedTotal + ", proactive runs " + acProactiveRuns + " (hits " + acProactiveHits + "), log events dropped " + dropped + ", game time " + Time.time.ToString("F0"));
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
