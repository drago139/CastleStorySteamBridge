using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.12 "black box". Seen in real sessions: the host's own game looks perfectly normal while the guests receive almost no world state (only UNet
    // heartbeats) for minutes, with both bridges healthy. The cause is on the host's game side (its replication/sending), so the host's bridge now keeps
    // what is needed to find it: (1) the game's own error messages, counted by kind with first/last time, (2) UNet's per-connection figures (RTT, send
    // queue, loss), (3) a detector for "the host suddenly sends a small fraction of its usual packets" that writes a snapshot of all of this to the report
    // and to SteamBridgeDiagnostics\stall-*.txt. It reads counters and log lines only; it changes nothing in the game.
    public sealed partial class SteamBridgePlugin
    {
        sealed class ErrSig { public string Text; public int Count, WindowCount; public float First, Last; }

        readonly Dictionary<string, ErrSig> errSigs = new Dictionary<string, ErrSig>();
        readonly List<string> unetWindows = new List<string>();
        readonly Dictionary<CSteamID, PeerTrafficWindow> trafficWindows = new Dictionary<CSteamID, PeerTrafficWindow>();
        float trafficLastSample;
        int errTotal, errWindow, errLastWindow, newErrNotes;
        bool logHooked;
        string lastStallSnapshot = "";

        // ---- the game's own errors ----

        void HookUnityLog()
        {
            if (logHooked) return;
            logHooked = true;
            Application.logMessageReceived += OnUnityLog;
        }

        void UnhookUnityLog()
        {
            if (!logHooked) return;
            logHooked = false;
            Application.logMessageReceived -= OnUnityLog;
        }

        void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
                errTotal++; errWindow++;
                string sig = condition ?? "";
                int nl = sig.IndexOf('\n');
                if (nl > 0) sig = sig.Substring(0, nl);
                if (sig.Length > 140) sig = sig.Substring(0, 140);
                if (type == LogType.Exception && !string.IsNullOrEmpty(stackTrace))
                {
                    int e = stackTrace.IndexOf('\n');
                    string top = e > 0 ? stackTrace.Substring(0, e) : stackTrace;
                    sig += " @ " + (top.Length > 100 ? top.Substring(0, 100) : top);
                }
                float now = Time.realtimeSinceStartup;
                ErrSig s;
                if (!errSigs.TryGetValue(sig, out s))
                {
                    if (errSigs.Count >= 80)
                    {
                        string oldest = null; float oldestAt = float.MaxValue;
                        foreach (KeyValuePair<string, ErrSig> kv in errSigs) if (kv.Value.Last < oldestAt) { oldestAt = kv.Value.Last; oldest = kv.Key; }
                        if (oldest != null) errSigs.Remove(oldest);
                    }
                    s = new ErrSig(); s.Text = sig; s.First = now; errSigs[sig] = s;
                    // a message seen for the first time, with its time: helps to tell what started a stall
                    if (newErrNotes < 30) { newErrNotes++; DiagNote(diagEvents, "New game error: " + sig); }
                }
                s.Count++; s.WindowCount++; s.Last = now;
            }
            catch (Exception) { }
        }

        string TopErrors(int limit)
        {
            List<ErrSig> list = new List<ErrSig>(errSigs.Values);
            list.Sort(delegate(ErrSig a, ErrSig b) { return b.Last.CompareTo(a.Last); });
            StringBuilder sb = new StringBuilder();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < list.Count && i < limit; i++)
                sb.AppendLine("  x" + list[i].Count + " (у вікні " + list[i].WindowCount + "), востаннє " + ((int)(now - list[i].Last)) + " с тому: " + list[i].Text);
            return sb.Length == 0 ? "  (помилок не було)\n" : sb.ToString();
        }

        // ---- UNet figures per game connection ----

        string UnetLine(NetworkConnection c, string who)
        {
            try
            {
                if (c == null || c.hostId < 0) return who + ": немає з'єднання";
                byte e;
                int rtt = NetworkTransport.GetCurrentRTT(c.hostId, c.connectionId, out e);
                int outMsgs = NetworkTransport.GetOutgoingMessageCountForConnection(c.hostId, c.connectionId, out e);
                if (e != 0) return who + ": UNet counters unavailable, error=" + (NetworkError)e;
                int queue = NetworkTransport.GetOutgoingMessageQueueSize(c.hostId, out e);
                string queueText = e == 0 ? queue.ToString() : "unavailable (" + (NetworkError)e + ")";
                int outBytes = NetworkTransport.GetOutgoingFullBytesCountForConnection(c.hostId, c.connectionId, out e);
                int outPackets = NetworkTransport.GetOutgoingPacketCountForConnection(c.hostId, c.connectionId, out e);
                int inPackets = NetworkTransport.GetIncomingPacketCount(c.hostId, c.connectionId, out e);
                int netLoss = NetworkTransport.GetOutgoingPacketNetworkLossPercent(c.hostId, c.connectionId, out e);
                int ovfLoss = NetworkTransport.GetOutgoingPacketOverflowLossPercent(c.hostId, c.connectionId, out e);
                int inLost = NetworkTransport.GetIncomingPacketLossCount(c.hostId, c.connectionId, out e);
                int ack = NetworkTransport.GetAckBufferCount(c.hostId, c.connectionId, out e);
                return who + ": UNet RTT " + rtt + " мс; у черзі транспортного хоста " + queueText + " повідомл.; від початку з’єднання відправлено " + outMsgs + " повідомл./" + outBytes + " Б; пакетів UNet відправлено " + outPackets + ", прийнято " + inPackets
                    + "; втрати мережі " + netLoss + " %, переповнення " + ovfLoss + " %; втрачено вхідних " + inLost + "; ack-буфер " + ack + "; готовий " + c.isReady + "; помилка " + c.lastError;
            }
            catch (Exception ex) { return who + ": UNet-статистика недоступна (" + ex.Message + ")"; }
        }

        // every 15 s from LogStats: one line per game connection (host: each guest; client: the connection to the host)
        void UnetSample()
        {
            try
            {
                if (hosting)
                {
                    foreach (NetworkConnection c in new List<NetworkConnection>(NetworkServer.connections))
                    {
                        if (c == null) continue;
                        DiagNote(unetWindows, UnetLine(c, GuestNameOfConnection(c)));
                    }
                }
                else if (bridge != null && NetworkClient.allClients != null)
                {
                    foreach (NetworkClient nc in NetworkClient.allClients)
                        if (nc != null && nc.connection != null) { DiagNote(unetWindows, UnetLine(nc.connection, "клієнт↔хост")); break; }
                }
            }
            catch (Exception) { }
        }

        string GuestNameOfConnection(NetworkConnection c)
        {
            int port = ConnectionPort(c);
            foreach (KeyValuePair<CSteamID, BridgeCore> g in guests)
                if (g.Value.Port == port) return "гість " + PingName(g.Key);
            return "підключення #" + c.connectionId;
        }

        // ---- host: the world suddenly goes (almost) silent ----

        void ResetTrafficWindows() { trafficWindows.Clear(); trafficLastSample = Time.realtimeSinceStartup; }
        void StallReset() { unetWindows.Clear(); ResetTrafficWindows(); lastStallSnapshot = ""; }

        bool GuestWorldReady(CSteamID id, BridgeCore b)
        {
            if (!b.Ready || loadActive) return false;
            foreach (NetworkConnection c in NetworkServer.connections)
                if (c != null && c.isReady && ConnectionPort(c) == b.Port) return true;
            return false;
        }
        // Per-peer game datagrams only, with actual elapsed time; pings and updates cannot hide a silent guest.
        void StallCheck(int ignoredTotalPackets)
        {
            try
            {
                errLastWindow = errWindow; errWindow = 0;
                float now = Time.realtimeSinceStartup;
                double seconds = now - trafficLastSample; trafficLastSample = now;
                if (!hosting || seconds <= 0) return;
                foreach (KeyValuePair<CSteamID, BridgeCore> entry in guests)
                {
                    PeerTrafficWindow t;
                    if (!trafficWindows.TryGetValue(entry.Key, out t)) { t = new PeerTrafficWindow(); trafficWindows[entry.Key] = t; }
                    int result = t.Observe(entry.Value.Sent, entry.Value.Received, seconds, GuestWorldReady(entry.Key, entry.Value));
                    DiagNote(unetWindows, "bridge " + PingName(entry.Key) + ": game out " + t.SentRate.ToString("F1") + "/s, in " + t.ReceivedRate.ToString("F1")
                        + "/s; ping replies " + (PingIsFresh(entry.Key) ? "fresh" : "missing") + "; ready " + GuestWorldReady(entry.Key, entry.Value));
                    if (result == 1 || (result == 2 && (t.StallWindows == 4 || t.StallWindows % 16 == 0)))
                        StallSnapshot(entry.Key, t);
                    if (result == 3) DiagWarn("Game traffic to " + PingName(entry.Key) + " recovered");
                }
                foreach (ErrSig sig in errSigs.Values) sig.WindowCount = 0;
            }
            catch (Exception e) { Logger.LogWarning("Per-peer stall check failed: " + e.Message); }
        }

        void StallSnapshot(CSteamID stalledPeer, PeerTrafficWindow traffic)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Падіння ігрового трафіку до " + PingName(stalledPeer) + ": " + traffic.SentRate.ToString("F1") + " пак./с, зазвичай " + traffic.UsualRate.ToString("F1") + "; ~" + traffic.StallSeconds.ToString("F0") + " с. Причина ще не визначена.");
            sb.AppendLine("Час: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "; Time.timeScale " + Time.timeScale + "; NetworkServer.active " + NetworkServer.active
                + "; підключень гри " + NetworkServer.connections.Count);
            sb.AppendLine("UNet по підключеннях:");
            foreach (NetworkConnection c in new List<NetworkConnection>(NetworkServer.connections))
                if (c != null) sb.AppendLine("  " + UnetLine(c, GuestNameOfConnection(c)));
            sb.AppendLine("Steam по гостях:");
            foreach (KeyValuePair<CSteamID, BridgeCore> g in guests)
            {
                P2PSessionState_t s;
                string q = SteamNetworking.GetP2PSessionState(g.Key, out s) ? "черга " + s.m_nBytesQueuedForSend + " Б, " + (s.m_bUsingRelay != 0 ? "relay" : "P2P") + ", помилка " + s.m_eP2PSessionError : "?";
                PingPeer p;
                string ping = pings.TryGetValue(g.Key, out p) ? "; пінг надійний " + PingShort(p.Reliable) + ", ненадійний " + PingShort(p.Unreliable) : "";
                sb.AppendLine("  " + PingName(g.Key) + ": " + q + "; готовий " + g.Value.Ready + "; пакетів моста " + g.Value.Sent + "/" + g.Value.Received + ping);
            }
            sb.AppendLine("Помилки Unity: усього " + errTotal + ", за останнє вікно " + errLastWindow + "; останні різні:");
            sb.Append(TopErrors(10));
            sb.AppendLine("Пам'ять/кадри (останні вікна):");
            sb.Append(HealthLastLines(3));
            string text = sb.ToString();
            lastStallSnapshot = text.Length > 7000 ? text.Substring(0, 7000) : text;
            DiagWarn("Per-peer game traffic drop: " + PingName(stalledPeer) + "; " + traffic.SentRate.ToString("F1") + "/s, usual " + traffic.UsualRate.ToString("F1") + "/s; snapshot queued");
            Logger.LogWarning("Stall snapshot:\n" + text);
            try
            {
                string path = Path.Combine(DiagFolder(), "stall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + DiagSafe(PingName(stalledPeer)) + ".txt");
                WriteReportAsync(path, DiagClean(text));
            }
            catch (Exception e) { Logger.LogWarning("Could not save the stall snapshot: " + e.Message); }
        }

        void AppendStallReport(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("--- Мережа гри (UNet) по вікнах 15 с: " + unetWindows.Count);
            foreach (string line in unetWindows) sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("--- Помилки Unity: усього " + errTotal + "; останні різні (кількість, востаннє):");
            sb.Append(TopErrors(15));
            if (lastStallSnapshot.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- Останній знімок зупинки передачі світу (хост)");
                sb.AppendLine(lastStallSnapshot);
            }
        }

        // 15 s window hook: called from LogStats for regular windows
        void BlackBoxWindow(int outPk)
        {
            HealthSample();
            UnetSample();
            StallCheck(outPk);
        }
    }
}
