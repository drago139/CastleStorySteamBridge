using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.4.5 LINK and UNETQ telemetry. LOG ONLY: it reads Steam and UNet counters and writes lines; nothing is sent, delayed, resent or closed.
    // Why: on 2026-11-05 the link between two PCs stalled for 4, 8, 16, 32, 64 s at a time and the numbers needed to say WHERE (Steam session,
    // reliable stream, UNet queue, the game's own send errors) did not exist in the logs (the F8 meter drops answers older than 8 s).
    //   LINK        every 5 s per peer, perf-trace.txt: Steam P2P session state (active / connecting / error / relay / queue, hashed remote
    //               address), reliable probes (answered, lost, last round trip, age of the last answer, age of the OLDEST UNANSWERED probe),
    //               late answers with their true round trip, unreliable loss in the last window, game-data silence, TimeSync queueing.
    //   LINK-ALERT  the same fields on an event: reliable stream stuck (once per doubling 4, 8, 16 ... s) and recovered, game silence
    //               (once per doubling from 2 s), a late answer of 3 s or more, session state or address change, unreliable loss >= 30 %.
    //               Also in LogOutput.log and in the friend report (SessionShare ssKeep).
    //   UNETQ       every 5 s per game connection (host: each guest and each connection WITHOUT a live bridge = a ghost; guest: the host
    //               connection): UNet RTT, queued messages, bytes, packets, UNet's own loss figures, ack buffer, pending packets of the
    //               first channels (reflection, may be absent), the game's send errors counted from the Unity log (NoResources,
    //               Failed to send internal buffer, buffer limit) with the change since the last sample.
    //   UNETQ-ALERT ghost connection older than 10 s, UNet RTT >= 5 s, send errors piling up.
    // Switch: [Diag] LinkProbe (default true).
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> lkCfg;
        float lkNextCheck, lkNextFull, lkNextErr;
        readonly LinkRules.AlertGate lkGate = new LinkRules.AlertGate();
        readonly Dictionary<ulong, LinkSnap> lkSnaps = new Dictionary<ulong, LinkSnap>();
        readonly Dictionary<int, float> lkGhostSince = new Dictionary<int, float>();
        int lkNoResPrev, lkSendFailPrev, lkBufLimitPrev;

        sealed class LinkSnap
        {
            public bool Known;
            public int UnrRecv, UnrLost, LateCount;
            public byte Active, Connecting, Relay; public int Err; public string Addr = "";
            public bool Stuck; public int StuckBucket = -1; public float StuckPeak, StuckSince;
            public int SilBucket = -1; public float SilPeak;
            public long DgTx, DgRx; public int PosIn, PosLate; public float WinTx, WinRx, WinPos, WinLate, LastFullAt;
        }

        void InstallLinkProbe()
        {
            try
            {
                lkCfg = Config.Bind("Diag", "LinkProbe", true, "Log only: LINK / UNETQ lines every 5 s (perf-trace.txt) and LINK-ALERT / UNETQ-ALERT on events (also LogOutput.log and the friend report): Steam P2P session state, age of the oldest unanswered reliable probe, late answers, unreliable loss, game-data silence, UNet connection figures and the game's send errors. Changes nothing in the network.");
                Logger.LogInfo("LINK-PROBE ready on=" + lkCfg.Value + " (log only)");
            }
            catch (Exception e) { Logger.LogWarning("LINK-PROBE unavailable: " + e.Message); }
        }

        List<CSteamID> LinkPeers()
        {
            List<CSteamID> r = new List<CSteamID>();
            if (hosting) { foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) r.Add(g.Key); }
            else if (bridge != null) r.Add(peer);
            return r;
        }

        void LinkProbeTick()
        {
            if (lkCfg == null || !lkCfg.Value) return;
            float now = Time.realtimeSinceStartup;
            if (now < lkNextCheck) return;
            lkNextCheck = now + 1f;
            try
            {
                bool full = now >= lkNextFull; if (full) lkNextFull = now + 5f;
                List<CSteamID> peers = LinkPeers();
                if (peers.Count == 0) { if (lkSnaps.Count > 0) lkSnaps.Clear(); if (lkGhostSince.Count > 0) lkGhostSince.Clear(); return; }
                for (int i = 0; i < peers.Count; i++) LinkOne(peers[i], now, full);
                if (full) UnetQSample(now);
            }
            catch (Exception e) { if (now >= lkNextErr) { lkNextErr = now + 30f; Logger.LogWarning("LinkProbe: " + e.Message); } }
        }

        static string LinkSec(double ms) { return ms < 0 ? "-" : (ms / 1000.0).ToString("F1"); }

        void LinkOne(CSteamID id, float now, bool full)
        {
            ulong key = id.m_SteamID;
            LinkSnap sn; if (!lkSnaps.TryGetValue(key, out sn)) { sn = new LinkSnap(); lkSnaps[key] = sn; }
            PingPeer p; pings.TryGetValue(id, out p);
            BridgeCore b = null; if (hosting) guests.TryGetValue(id, out b); else b = bridge;
            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            // Steam P2P session
            P2PSessionState_t s = default(P2PSessionState_t); bool hasS = false;
            try { hasS = SteamNetworking.GetP2PSessionState(id, out s); } catch (Exception) { }
            string addr = hasS ? LinkRules.AddrHash(s.m_nRemoteIP, s.m_nRemotePort) : "-";

            // reliable stream: oldest unanswered probe (still in flight or past the 8 s limit) and the last accepted answer
            double unansMs = -1, lastOkMs = -1;
            if (p != null)
            {
                double inflight = -1;
                foreach (KeyValuePair<uint, long> kv in p.Out)
                    if ((kv.Key & 1) == 0) { double a = (nowTicks - kv.Value) * TickMs; if (a > inflight) inflight = a; }
                double expired = p.Late.OldestAgeMs(0, nowTicks);
                unansMs = Math.Max(inflight, expired);
                if (p.Reliable.Received > 0) lastOkMs = (now - p.Reliable.LastReplyAt) * 1000.0;
            }
            float unans = unansMs < 0 ? 0f : (float)(unansMs / 1000.0);
            float silence = (b != null && b.Ready && b.Received > 0) ? ReceiveAge(b) : -1f;
            int unrRecv = p != null ? p.Unreliable.Received : 0, unrLost = p != null ? p.Unreliable.Lost : 0;
            float lossWin = LinkRules.LossPct(unrRecv - sn.UnrRecv, unrLost - sn.UnrLost);

            // events, checked every second
            string who = PingName(id);
            // 1) reliable stream stuck: once per doubling of the age, and the recovery
            int bucket = LinkRules.AgeBucket(unans);
            if (bucket >= 0)
            {
                if (!sn.Stuck) { sn.Stuck = true; sn.StuckSince = now; sn.StuckPeak = 0f; }
                if (unans > sn.StuckPeak) sn.StuckPeak = unans;
                if (bucket > sn.StuckBucket)
                {
                    sn.StuckBucket = bucket;
                    LinkAlert("rel-stuck", "unans=" + unans.ToString("F1") + "s rung=" + LinkRules.LadderRung(unans), id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin);
                }
            }
            else if (sn.Stuck && unans < 2f)
            {
                LinkAlert("rel-recovered", "after=" + (now - sn.StuckSince).ToString("F1") + "s peak=" + sn.StuckPeak.ToString("F1") + "s", id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin);
                sn.Stuck = false; sn.StuckBucket = -1; sn.StuckPeak = 0f;
            }
            // 2) no game data from the peer: once per doubling from 2 s, and the end
            int sb = LinkRules.AgeBucket(silence * 2f);
            if (sb >= 0)
            {
                if (silence > sn.SilPeak) sn.SilPeak = silence;
                if (sb > sn.SilBucket)
                {
                    sn.SilBucket = sb;
                    LinkAlert("silence", "value=" + silence.ToString("F1") + "s ladder=" + LinkRules.LadderRung(silence), id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin);
                }
            }
            else if (sn.SilBucket >= 0 && silence >= 0f && silence < 1f)
            {
                LinkAlert("silence-ended", "peak=" + sn.SilPeak.ToString("F1") + "s", id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin);
                sn.SilBucket = -1; sn.SilPeak = 0f;
            }
            // 3) a late answer (after the 8 s limit) with its true round trip
            if (p != null && p.Late.LateCount > sn.LateCount)
            {
                int added = p.Late.LateCount - sn.LateCount; sn.LateCount = p.Late.LateCount;
                if (p.Late.LateLastMs >= 3000.0 && lkGate.Allow(key + "late", now, 2.0))
                    LinkAlert("late-answer", "rtt=" + LinkSec(p.Late.LateLastMs) + "s mode=" + (p.Late.LateLastMode == 0 ? "rel" : "unr") + " newLate=" + added, id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin);
            }
            // 4) session state or address change
            if (hasS)
            {
                if (sn.Known && (s.m_bConnectionActive != sn.Active || s.m_bConnecting != sn.Connecting || (int)s.m_eP2PSessionError != sn.Err || s.m_bUsingRelay != sn.Relay || addr != sn.Addr) && lkGate.Allow(key + "chg", now, 2.0))
                    LinkAlert("state-change", "was act=" + sn.Active + " con=" + sn.Connecting + " err=" + sn.Err + " relay=" + sn.Relay + " ad=" + sn.Addr, id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin);
                sn.Known = true; sn.Active = s.m_bConnectionActive; sn.Connecting = s.m_bConnecting; sn.Err = (int)s.m_eP2PSessionError; sn.Relay = s.m_bUsingRelay; sn.Addr = addr;
            }
            // 5) periodic line and the unreliable window
            if (full)
            {
                int winN = (unrRecv - sn.UnrRecv) + (unrLost - sn.UnrLost);
                if (lossWin >= 30f && winN >= 3 && lkGate.Allow(key + "unr", now, 20.0))
                    LinkAlert("unreliable-loss", "win=" + lossWin.ToString("F0") + "%", id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin);
                // packet counters of this window (game datagrams over this bridge, position packets on the guest)
                float span = sn.LastFullAt > 0f && now - sn.LastFullAt > 0.5f ? now - sn.LastFullAt : 5f; sn.LastFullAt = now;
                long tx = b != null ? b.Sent : 0, rx = b != null ? b.Received : 0;
                sn.WinTx = (tx - sn.DgTx) / span; sn.WinRx = (rx - sn.DgRx) / span; sn.DgTx = tx; sn.DgRx = rx;
                if (!hosting) { sn.WinPos = (posPacketsIn - sn.PosIn) / span; sn.WinLate = (posLate - sn.PosLate) / span; sn.PosIn = (int)posPacketsIn; sn.PosLate = (int)posLate; }
                PtLog("LINK " + LinkFields(id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin));
                sn.UnrRecv = unrRecv; sn.UnrLost = unrLost;
            }
        }

        string LinkFields(CSteamID id, LinkSnap sn, PingPeer p, bool hasS, P2PSessionState_t s, string addr, float unans, double lastOkMs, float silence, float lossWin)
        {
            StringBuilder sb = new StringBuilder(300);
            sb.Append("role=").Append(hosting ? "host" : "guest").Append(" peer=").Append(PingName(id)).Append(" id=").Append((id.m_SteamID % 1000000UL).ToString("D6"));
            if (hasS) sb.Append(" route=").Append(s.m_bUsingRelay != 0 ? "relay" : "P2P").Append(" act=").Append(s.m_bConnectionActive).Append(" con=").Append(s.m_bConnecting).Append(" err=").Append((int)s.m_eP2PSessionError)
                .Append(" q=").Append(s.m_nBytesQueuedForSend).Append("B/").Append(s.m_nPacketsQueuedForSend).Append("pkt ad=").Append(addr);
            else sb.Append(" route=none");
            if (p != null)
            {
                sb.Append(" | rel ok=").Append(p.Reliable.Received).Append(" lost=").Append(p.Reliable.Lost).Append(" last=").Append(p.Reliable.Received > 0 ? p.Reliable.Last.ToString("F0") : "-").Append("ms okAge=").Append(LinkSec(lastOkMs)).Append("s unans=").Append(unans.ToString("F1")).Append("s");
                sb.Append(" late=").Append(p.Late.LateCount).Append("/").Append(LinkSec(p.Late.LateMaxMs)).Append("s pending=").Append(p.Late.Count);
                sb.Append(" | unr ok=").Append(p.Unreliable.Received).Append(" lost=").Append(p.Unreliable.Lost).Append(" last=").Append(p.Unreliable.Received > 0 ? p.Unreliable.Last.ToString("F0") : "-").Append("ms win=").Append(lossWin < 0f ? "-" : lossWin.ToString("F0") + "%");
            }
            else sb.Append(" | no ping state");
            sb.Append(" | silence=").Append(silence < 0f ? "-" : silence.ToString("F1")).Append("s");
            sb.Append(" dg tx=").Append(sn.WinTx.ToString("F1")).Append("/s rx=").Append(sn.WinRx.ToString("F1")).Append("/s");
            if (!hosting) sb.Append(" pos=").Append(sn.WinPos.ToString("F1")).Append("/s late=").Append(sn.WinLate.ToString("F1")).Append("/s");
            if (!hosting && Time.realtimeSinceStartup - tsRawAt < 10f) sb.Append(" ts=").Append(tsRawLagMs.ToString("F0")).Append("/").Append(tsRawTransitMs.ToString("F0")).Append("ms");
            sb.Append(" frame=").Append((Time.unscaledDeltaTime * 1000f).ToString("F0")).Append("ms");
            return sb.ToString();
        }

        void LinkAlert(string kind, string extra, CSteamID id, LinkSnap sn, PingPeer p, bool hasS, P2PSessionState_t s, string addr, float unans, double lastOkMs, float silence, float lossWin)
        {
            string line = "LINK-ALERT kind=" + kind + " " + extra + " " + LinkFields(id, sn, p, hasS, s, addr, unans, lastOkMs, silence, lossWin);
            PtLog(line);
            try { Logger.LogInfo(line); } catch (Exception) { }
            if (kind == "rel-recovered" || kind == "silence-ended") SessionShareNudge(Time.realtimeSinceStartup);   // send the evidence soon after a stall ends
        }

        // ---- UNet side ----

        static FieldInfo lkChannelsField, lkPendingField, lkBrokenField; static bool lkReflInit;

        static string UnetPending(NetworkConnection c)
        {
            try
            {
                if (!lkReflInit)
                {
                    lkReflInit = true;
                    BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    lkChannelsField = typeof(NetworkConnection).GetField("m_Channels", all);
                    Type cb = typeof(NetworkConnection).Assembly.GetType("UnityEngine.Networking.ChannelBuffer");
                    if (cb != null) { lkPendingField = cb.GetField("m_PendingPackets", all); lkBrokenField = cb.GetField("m_IsBroken", all); }
                }
                if (lkChannelsField == null || lkPendingField == null) return "";
                Array chans = lkChannelsField.GetValue(c) as Array; if (chans == null) return "";
                StringBuilder sb = new StringBuilder(" pend=");
                for (int i = 0; i < chans.Length && i < 4; i++)
                {
                    object ch = chans.GetValue(i);
                    if (ch == null) { sb.Append("-/"); continue; }
                    ICollection q = lkPendingField.GetValue(ch) as ICollection;
                    bool broken = lkBrokenField != null && lkBrokenField.GetValue(ch) is bool && (bool)lkBrokenField.GetValue(ch);
                    sb.Append(q != null ? q.Count.ToString() : "?").Append(broken ? "!" : "").Append('/');
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        void UnetQSample(float now)
        {
            // the game's own send errors, counted from the Unity log by StallBox (host and guest)
            int nores = 0, sendfail = 0, buflimit = 0;
            try
            {
                foreach (KeyValuePair<string, ErrSig> kv in errSigs)
                {
                    string t = kv.Key;
                    if (t.IndexOf("NoResources", StringComparison.Ordinal) >= 0) nores += kv.Value.Count;
                    else if (t.IndexOf("Failed to send internal buffer", StringComparison.Ordinal) >= 0) sendfail += kv.Value.Count;
                    else if (t.IndexOf("buffer limit", StringComparison.OrdinalIgnoreCase) >= 0) buflimit += kv.Value.Count;
                }
            }
            catch (Exception) { }
            int dNo = nores - lkNoResPrev, dFail = sendfail - lkSendFailPrev, dLim = buflimit - lkBufLimitPrev;
            lkNoResPrev = nores; lkSendFailPrev = sendfail; lkBufLimitPrev = buflimit;
            string errs = " nores=" + nores + "(+" + dNo + ") sendfail=" + sendfail + "(+" + dFail + ") buflimit=" + buflimit + "(+" + dLim + ")";
            if (dNo + dFail >= 20 && lkGate.Allow("unetq-errs", now, 30.0))
                LinkUnetAlert("send-errors", "+" + dNo + " NoResources +" + dFail + " send-failed in 5 s");

            if (hosting)
            {
                int conns = 0; HashSet<int> seen = new HashSet<int>();
                List<NetworkConnection> list = new List<NetworkConnection>(NetworkServer.connections);
                foreach (NetworkConnection c in list)
                {
                    if (c == null || c.hostId < 0) continue;
                    conns++; seen.Add(c.connectionId);
                    bool live = false; string who = "#" + c.connectionId;
                    try
                    {
                        int port = ConnectionPort(c);
                        foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (g.Value.Port == port) { live = true; who = PingName(g.Key); break; }
                    }
                    catch (Exception) { }
                    string line = UnetQLine(c, who, live ? "" : " GHOST(no live bridge)", errs, guests.Count, conns);
                    PtLog(line);
                    if (!live)
                    {
                        float since; if (!lkGhostSince.TryGetValue(c.connectionId, out since)) { since = now; lkGhostSince[c.connectionId] = since; }
                        if (now - since >= 10f && lkGate.Allow("ghost" + c.connectionId, now, 60.0)) LinkUnetAlert("ghost", "conn=" + c.connectionId + " has no live bridge for " + (now - since).ToString("F0") + " s; " + line);
                    }
                    else lkGhostSince.Remove(c.connectionId);
                    CheckUnetRtt(c, who, now);
                }
                List<int> gone = null; foreach (int k in lkGhostSince.Keys) if (!seen.Contains(k)) { if (gone == null) gone = new List<int>(); gone.Add(k); }
                if (gone != null) foreach (int k in gone) lkGhostSince.Remove(k);
            }
            else if (bridge != null && NetworkClient.allClients != null)
            {
                foreach (NetworkClient nc in NetworkClient.allClients)
                    if (nc != null && nc.connection != null && nc.connection.hostId >= 0)
                    {
                        PtLog(UnetQLine(nc.connection, "host", "", errs, 1, 1));
                        CheckUnetRtt(nc.connection, "host", now);
                        break;
                    }
            }
        }

        void CheckUnetRtt(NetworkConnection c, string who, float now)
        {
            try
            {
                byte e; int rtt = NetworkTransport.GetCurrentRTT(c.hostId, c.connectionId, out e);
                if (e == 0 && rtt >= 5000 && lkGate.Allow("unetrtt" + c.connectionId, now, 20.0))
                    LinkUnetAlert("unet-rtt", "conn=" + c.connectionId + " who=" + who + " rtt=" + rtt + "ms");
            }
            catch (Exception) { }
        }

        void LinkUnetAlert(string kind, string text)
        {
            string line = "UNETQ-ALERT kind=" + kind + " " + text;
            PtLog(line);
            try { Logger.LogInfo(line); } catch (Exception) { }
        }

        string UnetQLine(NetworkConnection c, string who, string tag, string errs, int bridges, int conns)
        {
            try
            {
                byte e;
                int rtt = NetworkTransport.GetCurrentRTT(c.hostId, c.connectionId, out e);
                int outMsgs = NetworkTransport.GetOutgoingMessageCountForConnection(c.hostId, c.connectionId, out e);
                if (e != 0) return "UNETQ conn=" + c.connectionId + " who=" + who + tag + " counters unavailable error=" + (NetworkError)e;
                int queue = NetworkTransport.GetOutgoingMessageQueueSize(c.hostId, out e);
                int outBytes = NetworkTransport.GetOutgoingFullBytesCountForConnection(c.hostId, c.connectionId, out e);
                int outPk = NetworkTransport.GetOutgoingPacketCountForConnection(c.hostId, c.connectionId, out e);
                int inPk = NetworkTransport.GetIncomingPacketCount(c.hostId, c.connectionId, out e);
                int netLoss = NetworkTransport.GetOutgoingPacketNetworkLossPercent(c.hostId, c.connectionId, out e);
                int ovfLoss = NetworkTransport.GetOutgoingPacketOverflowLossPercent(c.hostId, c.connectionId, out e);
                int inLost = NetworkTransport.GetIncomingPacketLossCount(c.hostId, c.connectionId, out e);
                int ack = NetworkTransport.GetAckBufferCount(c.hostId, c.connectionId, out e);
                return "UNETQ conn=" + c.connectionId + " who=" + who + tag + " rtt=" + rtt + "ms out=" + outMsgs + "msg/" + outBytes + "B pk=" + outPk + "/" + inPk + " loss=" + netLoss + "%/" + ovfLoss + "% inLost=" + inLost
                    + " ack=" + ack + " hostQ=" + queue + " ready=" + c.isReady + " err=" + c.lastError + UnetPending(c) + errs + " conns=" + conns + " bridges=" + bridges;
            }
            catch (Exception ex) { return "UNETQ conn=" + c.connectionId + " who=" + who + tag + " unavailable (" + ex.Message + ")"; }
        }
    }
}
