using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.4.15 reliable-echo WATCHDOG (roadmap P2.4 of the 2026-11-05 network analysis). Decision rule: WatchdogRules.cs.
    // What it does when it fires: SteamNetworking.CloseP2PSessionWithUser(peer). Steam frees the session; the next SendP2PPacket (the bridge sends every
    // 2 s at the latest: the ping probes) opens a new one, the other end gets a P2PSessionRequest_t and OnRequest accepts it (guest: bridge != null and the sender
    // is the host; host: the sender is a known guest). UNet retransmits its own reliable datagrams, so data queued in the old session is resent by the game.
    // Switch: [Network] ReliableWatchdog = 0 off (default) / 1 guest end only / 2 guest and host. NOT proven to cure anything: it is the cheapest A/B for the
    // 74 s stalls of 2026-11-06 22:47; whether one end is enough depends on Steam internals (not visible in the code): see NOTES.md of 0.4.15.
    // Tag WATCHDOG in perf-trace.txt, LogOutput.log and the friend report.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<int> wdMode, wdSilence, wdHoldOff, wdMax;
        readonly Dictionary<ulong, WatchdogRule> wdRules = new Dictionary<ulong, WatchdogRule>();
        readonly Dictionary<ulong, float> wdClosedAt = new Dictionary<ulong, float>();
        float wdNext, wdLongFrameAt = -1000f, wdNextErr, wdNextHeartbeat; int wdModeNow, wdFiredTotal, wdSuppressedTotal;
        const float WdFailGraceSeconds = 25f;

        void InstallLinkWatchdog()
        {
            try
            {
                wdMode = Config.Bind("Network", "ReliableWatchdog", 0, new ConfigDescription("EXPERIMENT, default 0 = off. When the reliable probe has had no answer for N seconds (ReliableWatchdogSilenceSeconds) while the unreliable probe still answers AND no game data arrives, close the Steam P2P session with that player; the next packet opens a new one (log tag WATCHDOG). 1 = this PC acts only as a guest (the host is never touched), 2 = guest and host. Restart needed.", new AcceptableValueRange<int>(0, 2)));
                wdSilence = Config.Bind("Network", "ReliableWatchdogSilenceSeconds", 8, new ConfigDescription("Seconds without a reliable answer and without game data before the watchdog may close the session (4..120).", new AcceptableValueRange<int>(4, 120)));
                wdHoldOff = Config.Bind("Network", "ReliableWatchdogHoldOffSeconds", 30, new ConfigDescription("Minimum seconds between two closes with the same player (10..600).", new AcceptableValueRange<int>(10, 600)));
                wdMax = Config.Bind("Network", "ReliableWatchdogMaxPer5Min", 3, new ConfigDescription("At most this many closes per 5 minutes and player (1..10).", new AcceptableValueRange<int>(1, 10)));
                wdModeNow = WatchdogRule.NormalizeMode(wdMode.Value);
                Logger.LogInfo("WATCHDOG ready mode=" + wdModeNow + (wdModeNow == 0 ? " (off)" : wdModeNow == 1 ? " (guest end only)" : " (guest and host)") + " silence=" + wdSilence.Value + "s holdOff=" + wdHoldOff.Value + "s max=" + wdMax.Value + "/5min");
            }
            catch (Exception e) { wdModeNow = 0; Logger.LogWarning("WATCHDOG unavailable: " + e.Message); }
        }

        void LinkWatchdogTick()
        {
            if (wdModeNow <= 0) return;
            float now = Time.realtimeSinceStartup;
            if (Time.unscaledDeltaTime >= 1f) wdLongFrameAt = now;
            if (now < wdNext) return;
            wdNext = now + 1f;
            try
            {
                if (!WatchdogRule.MayAct(wdModeNow, hosting)) return;
                List<CSteamID> peers = LinkPeers();
                if (peers.Count == 0) { if (wdRules.Count > 0) wdRules.Clear(); return; }
                for (int i = 0; i < peers.Count; i++) WatchdogOne(peers[i], now);
            }
            catch (Exception e) { if (now >= wdNextErr) { wdNextErr = now + 30f; Logger.LogWarning("Watchdog: " + e.Message); } }
        }

        void WatchdogOne(CSteamID id, float now)
        {
            PingPeer p; if (!pings.TryGetValue(id, out p)) return;
            BridgeCore b = null; if (hosting) guests.TryGetValue(id, out b); else b = bridge;
            if (b == null || !b.Ready || b.Received <= 0) return;
            ulong key = id.m_SteamID;
            WatchdogRule r; if (!wdRules.TryGetValue(key, out r)) { r = new WatchdogRule(wdSilence.Value, wdHoldOff.Value, wdMax.Value); wdRules[key] = r; }
            bool relEver = p.Reliable.Received > 0;
            double relAge = relEver ? now - p.Reliable.LastReplyAt : now - p.FirstSentAt;
            double unrAge = p.Unreliable.Received > 0 ? now - p.Unreliable.LastReplyAt : -1.0;
            double silence = ReceiveAge(b);
            WatchdogRule.Verdict v = r.Evaluate(now, relAge, relEver, now - p.FirstSentAt, unrAge, silence, now - wdLongFrameAt);
            if (v != WatchdogRule.Verdict.Fire)
            {
                // the rule is true but held back: say so once in a while, so the log shows why nothing happened
                if ((v == WatchdogRule.Verdict.Budget || v == WatchdogRule.Verdict.HoldOff) && now >= wdNextHeartbeat)
                { wdNextHeartbeat = now + 30f; PtLog("WATCHDOG held back (" + v + ") " + WdFields(id, p, relAge, unrAge, silence, now)); }
                return;
            }
            string line = "WATCHDOG fire #" + r.Fired + " mode=" + wdModeNow + " close P2P session " + WdFields(id, p, relAge, unrAge, silence, now);
            wdClosedAt[key] = now; wdFiredTotal++;
            PtLog(line); try { Logger.LogWarning(line); } catch (Exception) { }
            bool closed = false;
            try { closed = SteamNetworking.CloseP2PSessionWithUser(id); } catch (Exception e) { PtLog("WATCHDOG close failed: " + e.Message); }
            PtLog("WATCHDOG close result=" + closed + " (the next packet to the peer opens a new session; the peer must accept it)");
            SessionShareNudge(now);   // evidence soon, like after a stall
        }

        string WdFields(CSteamID id, PingPeer p, double relAge, double unrAge, double silence, float now)
        {
            P2PSessionState_t s; bool hasS = false;
            try { hasS = SteamNetworking.GetP2PSessionState(id, out s); } catch (Exception) { s = default(P2PSessionState_t); }
            return "role=" + (hosting ? "host" : "guest") + " peer=" + PingName(id) + " id=" + (id.m_SteamID % 1000000UL).ToString("D6")
                + " relAge=" + relAge.ToString("F1") + "s rel ok=" + p.Reliable.Received + " unrAge=" + (unrAge < 0 ? "-" : unrAge.ToString("F1")) + "s dataSilence=" + silence.ToString("F1") + "s"
                + (hasS ? " route=" + (s.m_bUsingRelay != 0 ? "relay" : "P2P") + " act=" + s.m_bConnectionActive + " con=" + s.m_bConnecting + " err=" + (int)s.m_eP2PSessionError + " q=" + s.m_nBytesQueuedForSend + "B" : " route=none")
                + " frame=" + (Time.unscaledDeltaTime * 1000f).ToString("F0") + "ms";
        }

        // Called from OnFailure: true = the failure belongs to the session the watchdog just reopened; the caller logs it and does NOT end the game session.
        bool WatchdogFailureIsOurs(CSteamID id, string what)
        {
            try
            {
                float at;
                if (wdModeNow <= 0 || !wdClosedAt.TryGetValue(id.m_SteamID, out at)) return false;
                if (!WatchdogRule.FailureIsOurs(Time.realtimeSinceStartup, at, WdFailGraceSeconds)) return false;
                wdSuppressedTotal++;
                PtLog("WATCHDOG ignored Steam failure " + what + " " + (Time.realtimeSinceStartup - at).ToString("F1") + " s after our close (suppressed #" + wdSuppressedTotal + "); the bridge keeps sending, game timeouts still apply");
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
