using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Brix.Network;
using Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.83 passive diagnostics of the reliable main stream (Steam channel 77). LOG ONLY: nothing is delayed, reordered, split or resent.
    // Question it answers: how long do the player's orders wait in the one ordered stream behind big messages (buildings, voxels)?
    //   CMD-SEND (guest)  every game command the guest sends: name, per-name number, UTC ms, Steam send queue at that moment.
    //   CMD-RECV (host)   every command from a remote guest when the host runs it: name, per-name number, UTC ms.
    //                     On one PC (test setup) both share the clock, so SEND/RECV pairs give the exact order delay; between two PCs
    //                     the numbers still pair up, and the queue/RTT figures below show the waiting without a common clock.
    //   QUEUE (both, 2 s) Steam send queue now/max (bytes, packets), main-stream bytes and datagrams sent, largest datagram,
    //                     reliable ping minus unreliable ping (= time a message waits behind others in the ordered stream, both ways).
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> qdCfg; static bool qdOn;
        static readonly Dictionary<string, int> qdSendSeq = new Dictionary<string, int>(), qdRecvSeq = new Dictionary<string, int>();
        static readonly Dictionary<int, string> qdNames = new Dictionary<int, string>();
        static MethodInfo qdHashName; static NetworkConnection qdConn; static int qdLines; static float qdLinesReset;
        static int qdBytesOut, qdDgOut, qdDgMax;
        int qdQMax, qdQPkMax; float qdNext;

        void InstallQueueDiag()
        {
            try
            {
                qdCfg = Config.Bind("Diag", "QueueDiag", true, "Log (perf-trace.txt) how long game commands wait in the reliable main stream: CMD-SEND/CMD-RECV and QUEUE lines. Passive, changes nothing in the network.");
                qdOn = qdCfg.Value; if (!qdOn) return;
                BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                MethodInfo send = typeof(NetworkBehaviour).GetMethod("SendCommandInternal", all);
                MethodInfo onCmd = typeof(NetworkServer).GetMethod("OnCommandMessage", all);
                MethodInfo handle = typeof(NetworkIdentity).GetMethod("HandleCommand", all);
                qdHashName = typeof(NetworkBehaviour).GetMethod("GetCmdHashCmdName", all);
                int ok = 0;
                if (send != null) { discoveryHarmony.Patch(send, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("QdBeforeSendCommand", BindingFlags.Static | BindingFlags.NonPublic))); ok++; }
                if (onCmd != null && handle != null)
                {
                    discoveryHarmony.Patch(onCmd, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("QdBeforeOnCommand", BindingFlags.Static | BindingFlags.NonPublic)), new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("QdAfterOnCommand", BindingFlags.Static | BindingFlags.NonPublic)));
                    discoveryHarmony.Patch(handle, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("QdBeforeHandleCommand", BindingFlags.Static | BindingFlags.NonPublic))); ok += 2;
                }
                Logger.LogInfo("QUEUE-DIAG ready hooks=" + ok + "/3 names=" + (qdHashName != null) + " (passive, log only)");
            }
            catch (Exception e) { Logger.LogWarning("QUEUE-DIAG unavailable: " + e.Message); }
        }

        static long QdUtcMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
        static bool QdBudget() { float now = Time.realtimeSinceStartup; if (now >= qdLinesReset) { qdLinesReset = now + 1f; qdLines = 0; } return ++qdLines <= 60; }   // at most 60 command lines per second
        static int QdNext(Dictionary<string, int> d, string name) { int n; d.TryGetValue(name, out n); n++; d[name] = n; return n; }

        // Called from SendRangeToCore after a datagram went into the reliable stream (counts only; the packet itself is untouched).
        static void QdCountOut(int length) { qdBytesOut += length; qdDgOut++; if (length > qdDgMax) qdDgMax = length; }

        static void QdBeforeSendCommand(string cmdName)
        {
            try
            {
                if (!qdOn || NetworkServer.active || string.IsNullOrEmpty(cmdName)) return;   // the host's own commands never cross the network
                SteamBridgePlugin p = discoveryInstance; int qb = -1, qp = -1;
                P2PSessionState_t s; if (p != null && p.bridge != null && SteamNetworking.GetP2PSessionState(p.peer, out s)) { qb = s.m_nBytesQueuedForSend; qp = s.m_nPacketsQueuedForSend; }
                int seq = QdNext(qdSendSeq, cmdName);
                if (QdBudget()) PtLog("CMD-SEND " + cmdName + " #" + seq + " utc=" + QdUtcMs() + " q=" + qb + "B/" + qp + "pkt");
            }
            catch (Exception) { }
        }

        static void QdBeforeOnCommand(NetworkMessage __0) { try { qdConn = __0 == null ? null : __0.conn; } catch (Exception) { qdConn = null; } }
        static void QdAfterOnCommand() { qdConn = null; }

        static void QdBeforeHandleCommand(int cmdHash)
        {
            try
            {
                if (!qdOn || !NetworkServer.active || qdConn == null) return;
                if (qdConn.GetType().Name.IndexOf("Local", StringComparison.Ordinal) >= 0) return;    // the host's own client
                string name; if (!qdNames.TryGetValue(cmdHash, out name)) { name = null; try { if (qdHashName != null) name = qdHashName.Invoke(null, new object[] { cmdHash }) as string; } catch (Exception) { } if (string.IsNullOrEmpty(name)) name = "hash" + cmdHash; qdNames[cmdHash] = name; }
                string key = qdConn.connectionId + ":" + name; int seq = QdNext(qdRecvSeq, key);
                if (QdBudget()) PtLog("CMD-RECV " + name + " #" + seq + " conn=" + qdConn.connectionId + " utc=" + QdUtcMs());
            }
            catch (Exception) { }
        }

        void QueueDiagTick()
        {
            if (!qdOn) return;
            try
            {
                bool live = hosting ? guests.Count > 0 : bridge != null;
                if (!live) { qdBytesOut = qdDgOut = qdDgMax = 0; return; }
                int qb = 0, qp = 0; P2PSessionState_t s;
                if (hosting) { foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (SteamNetworking.GetP2PSessionState(g.Key, out s)) { qb = Math.Max(qb, s.m_nBytesQueuedForSend); qp = Math.Max(qp, s.m_nPacketsQueuedForSend); } }
                else if (SteamNetworking.GetP2PSessionState(peer, out s)) { qb = s.m_nBytesQueuedForSend; qp = s.m_nPacketsQueuedForSend; }
                if (qb > qdQMax) qdQMax = qb; if (qp > qdQPkMax) qdQPkMax = qp;
                float now = Time.realtimeSinceStartup; if (now < qdNext) return;
                float span = qdNext <= 0f ? 2f : 2f + (now - qdNext); qdNext = now + 2f;
                string hol = "?";
                CSteamID who = peer; if (hosting) foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) { who = g.Key; break; }
                PingPeer pp; if (pings.TryGetValue(who, out pp) && pp.Answered && pp.Reliable.Received > 0 && pp.Unreliable.Received > 0) hol = (pp.Reliable.Last - pp.Unreliable.Last).ToString("F0");
                PtLog("QUEUE q=" + qb + "B/" + qp + "pkt max=" + qdQMax + "B/" + qdQPkMax + "pkt out=" + (int)(qdBytesOut / span) + "B/s dg=" + (int)(qdDgOut / span) + "/s dgMax=" + qdDgMax + "B relMinusUnrelPing=" + hol + "ms guests=" + (hosting ? guests.Count : 1));
                qdBytesOut = qdDgOut = qdDgMax = 0; qdQMax = qdQPkMax = 0;
            }
            catch (Exception) { }
        }
    }
}
