using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Lua;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.86 match timer sync. Audit (decompiled game + Lua): the timer on screen is the game mode's Lua timer Registry.gameTimer (LuaTimer,
    // counts scaled Time.time) plus Registry.gameTimerOffset, which the host sends ONCE when the level is ready. After that every PC counts on
    // its own: Unity advances Time.time by at most Time.maximumDeltaTime per frame, so a PC that freezes for longer loses the rest; a pause
    // stops every PC alike. GameClock.Age (what 0.3.84-0.3.85 synced) is a stored value the clock never advances and nothing shows; syncing it
    // changed nothing visible. Simulation, physics and unit movement run on Unity's own time and are not touched here.
    // Host: its displayed time and its realtime clock ride in the 2 s pause-state packet (bytes 9-12, 17-20).
    // Guest: transit delay = (receive time - host send time) - the smallest such value over the last 2 min (= queueing / own freeze), plus half
    // the low round trip; target = host time + delay (no delay while paused); error = target - own displayed time. Errors above 3 s (join,
    // reconnect, long freeze) are corrected at once, smaller ones above 0.1 s are slewed away at up to 0.1 s per second by moving
    // Registry.gameTimerOffset (display only).
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> tsCfg;
        static readonly List<Table> tsRegs = new List<Table>(); static float tsNextFind, tsPending, tsNextReport;
        static bool tsFirst = true; static uint tsSession; static int tsSnaps, tsSlews, tsSamples, tsDupes, tsLastRegCount = -1;
        static readonly List<float> tsErrWindow = new List<float>(64), tsLagRing = new List<float>(64);
        static float tsRawLagMs, tsRawTransitMs, tsRawAt = -1000f, tsSessionMin = float.MaxValue;
        static readonly List<float> tsAllErr = new List<float>(8192);

        void InstallTimeSync()
        {
            try
            {
                tsCfg = Config.Bind("Game", "TimerSync", true, "Guests: the match timer on screen follows the host's (checked every 2 s; display only, the simulation is not touched).");
                Logger.LogInfo("TIMER-SYNC ready (" + (tsCfg.Value ? "on" : "off") + ", Unity maximumDeltaTime " + Time.maximumDeltaTime.ToString("F3") + " s)");
            }
            catch (Exception e) { Logger.LogWarning("TIMER-SYNC unavailable: " + e.Message); }
        }

        // all game mode registries that hold the on-screen match timer (host: server and client side may both have one)
        static List<Table> TsRegistries()
        {
            float now = Time.realtimeSinceStartup;
            if (now < tsNextFind) return tsRegs;
            tsNextFind = now + 5f; tsRegs.Clear();
            lock (lpScripts)
            {
                for (int i = 0; i < lpScripts.Count; i++)
                {
                    Script s = lpScripts[i].Target as Script; if (s == null) continue;
                    try
                    {
                        DynValue reg = s.Globals.Get("Registry"); if (reg.Type != DataType.Table || reg.Table == null) continue;
                        if (TsTimer(reg.Table) == null) continue;
                        bool seen = false; foreach (Table x in tsRegs) if (ReferenceEquals(x, reg.Table)) seen = true;
                        if (seen) { tsDupes++; continue; }   // 0.3.86 T-147: one table reachable from two scripts was shifted twice (+-3.3 s oscillation)
                        tsRegs.Add(reg.Table);
                    }
                    catch (Exception) { }
                }
            }
            if (tsRegs.Count != tsLastRegCount) { tsLastRegCount = tsRegs.Count; PtLog("TIMER-SYNC match timer tables found " + tsRegs.Count + " (same table seen again " + tsDupes + "x, counted once)"); }
            return tsRegs;
        }

        static LuaTimer TsTimer(Table reg)
        {
            DynValue d = reg.Get("gameTimer"); LuaTimer t;
            if (d.Type != DataType.UserData || d.UserData == null || !d.UserData.TryGet<LuaTimer>(out t)) return null;
            return t;
        }

        static bool TsRunning(Table reg, out float shown)
        {
            shown = -1f;
            try
            {
                LuaTimer t = TsTimer(reg); if (t == null || !t.running.Boolean) return false;
                DynValue o = reg.Get("gameTimerOffset"); shown = LuaTimer.clr_ElapsedSeconds(t) + (o.Type == DataType.Number ? (float)o.Number : 0f);
                return true;
            }
            catch (Exception) { return false; }
        }

        static void TsShift(float delta)
        {
            foreach (Table reg in TsRegistries())
            {
                float shown; if (!TsRunning(reg, out shown)) continue;
                try { DynValue o = reg.Get("gameTimerOffset"); double off = o.Type == DataType.Number ? o.Number : 0; reg.Set("gameTimerOffset", DynValue.NewNumber(off + delta)); } catch (Exception) { }
            }
        }

        // host: the time its own screen shows, -1 when no match timer runs
        static float TsHostShown()
        {
            if (tsCfg == null || !tsCfg.Value) return -1f;
            try { foreach (Table reg in TsRegistries()) { float shown; if (TsRunning(reg, out shown)) return shown; } } catch (Exception) { }
            return -1f;
        }

        void TsOnHostTime(float hostShown, float hostReal, uint session, bool paused)
        {
            if (tsCfg == null || !tsCfg.Value || hostShown < 0f) return;
            float now = Time.realtimeSinceStartup;
            if (session != tsSession) { tsSession = session; tsFirst = true; tsLagRing.Clear(); tsSessionMin = float.MaxValue; PtLog("TIMER-SYNC new host session " + session + ": next sample is applied at once"); }
            float mine = -1f; foreach (Table reg in TsRegistries()) { if (TsRunning(reg, out mine)) break; }
            if (mine < 0f) return;
            // queueing delay of this sample relative to the fastest one seen (both clocks are monotonic; their difference is constant)
            float lag = now - hostReal; tsLagRing.Add(lag); if (tsLagRing.Count > 60) tsLagRing.RemoveAt(0);
            float minLag = float.MaxValue; for (int i = 0; i < tsLagRing.Count; i++) if (tsLagRing[i] < minLag) minLag = tsLagRing[i];
            float transit = (lag - minLag) + TsHalfRtt();
            if (lag < tsSessionMin) tsSessionMin = lag;
            tsRawLagMs = (lag - tsSessionMin) * 1000f; tsRawTransitMs = transit * 1000f; tsRawAt = now;   // 0.4.5: queueing against the fastest sample of the whole host session, read by the LINK line (log only)
            float err = hostShown + (paused ? 0f : transit) - mine;
            tsSamples++; tsErrWindow.Add(err); if (tsAllErr.Count < 8192) tsAllErr.Add(err);
            if (Math.Abs(err) > 0.25f) PtLog("TIMER-SYNC sample err " + (err * 1000f).ToString("F0") + " ms (host " + hostShown.ToString("F2") + " mine " + mine.ToString("F2") + " transit " + (transit * 1000f).ToString("F0") + " ms" + (paused ? ", paused" : "") + ")");
            if (tsFirst || Math.Abs(err) > 3f)
            {
                TsShift(err); tsPending = 0f; tsSnaps++;
                PtLog("TIMER-SYNC " + (tsFirst ? "first sample" : "large gap") + ": guest timer moved " + err.ToString("F2") + " s at once (#" + tsSnaps + ")");
                tsFirst = false;
            }
            else tsPending = Math.Abs(err) > 0.1f ? err : 0f;
        }

        static float TsHalfRtt()
        {
            try { PingPeer p; if (discoveryInstance != null && discoveryInstance.pings.TryGetValue(discoveryInstance.peer, out p) && p.Reliable.Ring.Count > 0) return p.Reliable.Percentile(0.1f) / 2000f; } catch (Exception) { }
            return 0.05f;
        }

        static float tsNextClockLog;
        void TimeSyncTick()
        {
            float now = Time.realtimeSinceStartup;
            // diagnostics on every PC (sync on or off): the time this screen shows; both test copies log on one wall clock, so the gap is exact
            if (now >= tsNextClockLog) { tsNextClockLog = now + 10f; try { foreach (Table reg in TsRegistries()) { float shown; if (TsRunning(reg, out shown)) { PtLog("MATCH-CLOCK shown " + shown.ToString("F3") + " s, sync " + (tsCfg != null && tsCfg.Value ? "on" : "off") + ", timeScale " + Time.timeScale.ToString("F2")); break; } } } catch (Exception) { } }
            if (tsCfg == null || !tsCfg.Value || hosting) return;
            try
            {
                if (tsPending != 0f)
                {
                    float step = Math.Min(Math.Abs(tsPending), 0.1f * Math.Min(Time.unscaledDeltaTime, 0.5f)) * Math.Sign(tsPending);
                    TsShift(step); tsPending -= step; if (Math.Abs(tsPending) < 0.001f) { tsPending = 0f; tsSlews++; }
                }
                if (now >= tsNextReport)
                {
                    tsNextReport = now + 60f;
                    if (tsErrWindow.Count > 0)
                    {
                        PtLog("TIMER-SYNC 60s: samples " + tsErrWindow.Count + " |err| " + TsStats(tsErrWindow) + "; corrections at once " + tsSnaps + ", slewed " + tsSlews + ", total samples " + tsSamples + " all " + TsStats(tsAllErr));
                        tsErrWindow.Clear();
                    }
                }
            }
            catch (Exception e) { PtLog("TIMER-SYNC failed: " + e.Message); }
        }

        // mean / p95 / p99 / max of |x| in ms
        static string TsStats(List<float> xs)
        {
            List<float> a = new List<float>(xs.Count); double sum = 0; foreach (float x in xs) { a.Add(Math.Abs(x)); sum += Math.Abs(x); }
            if (a.Count == 0) return "-"; a.Sort();
            Func<float, float> q = delegate(float f) { return a[Math.Min(a.Count - 1, (int)Math.Round((a.Count - 1) * f))]; };
            return "mean " + (sum / a.Count * 1000).ToString("F0") + " p95 " + (q(0.95f) * 1000f).ToString("F0") + " p99 " + (q(0.99f) * 1000f).ToString("F0") + " max " + (a[a.Count - 1] * 1000f).ToString("F0") + " ms";
        }
    }
}
