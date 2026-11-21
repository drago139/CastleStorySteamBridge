using System;
using System.Diagnostics;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.4.17 KICK / REJOIN fix, plugin part. Decisions: ResilienceRules.cs (tests: ResilienceTests.cs). Tags RESILIENCE / HOST-REHELLO / P2P-HOLD in
    // perf-trace.txt, LogOutput.log and the friend report (SessionShare ssKeep). Both switches are in [Network].
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<int> holdCfg;
        static ConfigEntry<bool> rehelloCfg;
        readonly ResilienceRules.FailureHold failHold = new ResilienceRules.FailureHold();
        int holdLeftTotal, holdKeptTotal, rehelloTotal; long holdNextLogMs;

        static long ResNowMs() { return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency; }
        // 0.4.27 review: these rare session events went only to perf-trace.txt (PtLog), which the lean build (0.4.25+) switches off, and to DiagWarn, which never
        // logs (DiagnosticsEnabled is false): a guest dropped after a Steam failure left no line in LogOutput.log. They are written there as well.
        void ResLog(string text) { PtLog(text); try { Logger.LogWarning(text); } catch (Exception) { } }

        void InstallLinkResilience()
        {
            try
            {
                holdCfg = Config.Bind("Network", "GuestHoldOnP2PFailureSeconds", 120, new ConfigDescription("0.4.17, guest side. When Steam reports a P2P failure (code 4 = timeout) for the host, the bridge no longer leaves the game at once: it keeps running (its 2 s pings reopen the Steam session) and leaves only if nothing came from the host for this many seconds. 0 = leave at once (0.4.15 and older). Applies only to a session that was really running.", new AcceptableValueRange<int>(0, 600)));
                rehelloCfg = Config.Bind("Network", "HostRecycleOnRehello", true, "0.4.17, host side. A guest that comes back (new hello) while its old bridge carried game data and has been silent for 3 s gets a NEW bridge, so the game server sees a new connection and the rejoin works at once instead of waiting for the 600 s UNET timeout. false = the old bridge is reused (0.4.15 and older). The host needs this version for it.");
                Logger.LogInfo("RESILIENCE ready: guest holds on a Steam P2P failure for " + holdCfg.Value + " s (0 = leave at once); host recycles a silent guest bridge on a new hello = " + rehelloCfg.Value);
            }
            catch (Exception e) { Logger.LogWarning("RESILIENCE unavailable: " + e.Message); }
        }

        int HoldSeconds { get { return holdCfg == null ? 0 : holdCfg.Value; } }

        long GuestLastDataMs()
        {
            return ResilienceRules.LastDataMs(ResNowMs(), Environment.TickCount, bridge == null ? 0 : bridge.LastReceiveMilliseconds);
        }

        // OnFailure (guest): true = ignore this Steam failure, keep the bridge
        bool GuestHoldsOnFailure(string what)
        {
            try
            {
                if (hosting || bridge == null) return false;
                int hold = HoldSeconds;
                if (hold <= 0 || !ResilienceRules.HoldApplies(bridge.Ready, bridge.Received)) return false;
                long now = ResNowMs();
                bool keep = failHold.OnFailure(now, GuestLastDataMs(), hold);
                if (keep)
                {
                    holdKeptTotal++;
                    string keepLine = "P2P-HOLD keep #" + failHold.Count + " Steam failure " + what + " " + ((now - failHold.StartMs) / 1000.0).ToString("F1") + " s into the episode (hold " + hold + " s); the bridge keeps running and the 2 s pings reopen the Steam session";
                    // 0.4.27 verify (A-04-v): this line is written once per Steam failure callback and nothing here bounds how many arrive during the hold. Measured once
                    // (2026-11-07): one failure about 25 s after the session closed. Not measured: a host that fails fast (game closed, Steam offline); the Steam SDK says
                    // further sends retry the connection, and the bridge sends every frame. So LogOutput.log gets the first failure of an episode and then at most one
                    // line per 10 s (it must not fill up again like LOBBY-AUDIT did); perf-trace keeps every line.
                    if (failHold.Count <= 1 || now >= holdNextLogMs) { holdNextLogMs = now + 10000; ResLog(keepLine); } else PtLog(keepLine);
                    status = "Зв'язок із хостом перервано (Steam: " + what + "). Чекаю відновлення до " + hold + " с; F8 -> закрити гру, якщо не хочеш чекати.";
                }
                else ResLog("P2P-HOLD over: " + what + " after " + ((now - failHold.StartMs) / 1000.0).ToString("F1") + " s without data from the host (hold " + hold + " s); leaving as before");
                return keep;
            }
            catch (Exception e) { PtLog("P2P-HOLD error " + e.GetType().Name + " " + e.Message + "; leaving as before"); return false; }
        }

        // every frame (guest): the timer fallback. Returns true when it ended the session.
        bool GuestHoldTick()
        {
            try
            {
                if (hosting || bridge == null) { if (failHold.StartMs != long.MinValue) failHold.Reset(); return false; }
                if (!failHold.Open(GuestLastDataMs())) return false;
                int hold = HoldSeconds; long now = ResNowMs();
                if (!failHold.Expired(now, GuestLastDataMs(), hold)) return false;
                holdLeftTotal++;
                ResLog("P2P-HOLD expired after " + ((now - failHold.StartMs) / 1000.0).ToString("F0") + " s without data from the host (hold " + hold + " s, " + failHold.Count + " failure event(s)); leaving");
                DiagWarn("Steam P2P failure held for " + hold + " s, no data from the host: leaving");
                StopBridge(false); LeaveGameSession();
                status = "Втрачено зв'язок через Steam: хост мовчав " + hold + " с."; visible = true;
                failHold.Reset();
                return true;
            }
            catch (Exception) { return false; }
        }

        // UpdateGuests (host): a hello from a guest that already has a bridge. true = the old bridge was dropped, create a new one.
        bool HostRecyclesOnHello(CSteamID sender, BridgeCore old)
        {
            try
            {
                if (old == null || rehelloCfg == null) return false;
                int quiet = unchecked(Environment.TickCount - old.LastReceiveMilliseconds);
                if (!ResilienceRules.RecycleOnHello(rehelloCfg.Value, old.Received, quiet)) return false;
                rehelloTotal++;
                ResLog("HOST-REHELLO new hello from " + sender + " while its old bridge (loopback " + old.Port + ") carried " + old.Received + " game packets and was silent for " + (quiet / 1000.0).ToString("F1") + " s: the old bridge is dropped, a new one is created (#" + rehelloTotal + ")");
                RemoveGuest(sender, false);
                closeGuests.Remove(sender);   // RemoveGuest queued a delayed channel close; the same guest is joining again right now
                return true;
            }
            catch (Exception e) { PtLog("HOST-REHELLO error " + e.GetType().Name + " " + e.Message + "; the old bridge is kept"); return false; }
        }
    }
}
