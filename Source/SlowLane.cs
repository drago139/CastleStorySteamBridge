using BepInEx;
using BepInEx.Configuration;
using Brix.Game.Network;
using Brix.Network;
using Brix.UI.Builder.Menu;
using Network;
using UnityEngine.Networking;
using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.89 slow lane: big non-game data (diagnostics reports up to 48 KB, signed update chunks of 16 KB) no longer shares the game's
    // Steam channel 77, where it queued in front of game packets. It goes on its own reliable channel 80, cut into 1000-byte pieces,
    // at most 2 KB/s while a match runs (64 KB/s in menus and lobby), and it waits while the Steam send queue to that friend holds more
    // than 16 KB (game traffic first) or the friend has not answered pings for 3 s. The receiver reassembles the original bridge packet
    // and hands it to the same handler as before. Only friends whose bridge announced 0.3.89+ get it; older ones get the old path.
    public sealed partial class SteamBridgePlugin
    {
        const int SlowChannel = 80, SlowPiece = 1000, SlowHeader = 10, SlowQueueMax = 4 * 1024 * 1024;
        sealed class SlowOut { public CSteamID To; public uint Id; public byte[] Packet; public int Next, Count; }
        sealed class SlowIn { public byte[][] Parts; public int Got, Bytes; public float Since; }
        static readonly List<SlowOut> slowQueue = new List<SlowOut>();
        static readonly Dictionary<string, SlowIn> slowIn = new Dictionary<string, SlowIn>();
        static uint slowNextId; static float slowTokens, slowLastRefill, slowNextLog; static int slowQueued, slowPiecesOut, slowPiecesIn, slowDone, slowFallback;
        static readonly byte[] slowRecv = new byte[2048];

        bool SlowCapable(CSteamID id)
        {
            string v; return peerVersions.TryGetValue(id, out v) && UpdateVerifier.IsNewer(v, "0.3.88");
        }

        // big extra packet: slow lane for 0.3.89+ friends, the old channel for older ones
        bool SendBulk(CSteamID to, byte[] packet)
        {
            if (!SlowCapable(to)) { slowFallback++; return SendTo(to, packet); }
            if (slowQueued + packet.Length > SlowQueueMax) { Logger.LogWarning("Slow lane full, packet dropped (" + packet.Length + " B)"); return false; }
            SlowOut o = new SlowOut(); o.To = to; o.Id = ++slowNextId; o.Packet = packet; o.Count = (packet.Length + SlowPiece - 1) / SlowPiece;
            slowQueue.Add(o); slowQueued += packet.Length;
            return true;
        }

        int SlowQueuedFor(CSteamID id) { int n = 0; foreach (SlowOut o in slowQueue) if (o.To == id) n += o.Packet.Length - o.Next * SlowPiece; return n; }

        static bool SlowInMatch()
        {
            try { return HpInGame() && (UnityEngine.Networking.NetworkServer.active || UnityEngine.Networking.NetworkClient.active); } catch (Exception) { return false; }
        }

        void SlowLaneTick()
        {
            if (!SteamManager.Initialized) return;
            float now = Time.realtimeSinceStartup;
            try
            {
                float rate = SlowInMatch() ? 2048f : 65536f;
                slowTokens = Math.Min(slowTokens + rate * Math.Min(1f, now - slowLastRefill), rate + SlowPiece + SlowHeader); slowLastRefill = now;
                for (int guard = 0; guard < 128 && slowQueue.Count > 0 && slowTokens >= SlowPiece + SlowHeader; guard++)
                {
                    SlowOut o = slowQueue[0];
                    if (!SlowLinked(o.To)) { slowQueue.RemoveAt(0); slowQueued -= o.Packet.Length; continue; }
                    P2PSessionState_t st;
                    if (SteamNetworking.GetP2PSessionState(o.To, out st) && st.m_nBytesQueuedForSend > 16 * 1024) break;   // game traffic first
                    PingPeer pp; if (pings.TryGetValue(o.To, out pp) && pp.Reliable.LastReplyAt > 0 && now - pp.Reliable.LastReplyAt > 3f) break;   // outage
                    int off = o.Next * SlowPiece, len = Math.Min(SlowPiece, o.Packet.Length - off);
                    byte[] piece = new byte[SlowHeader + len];
                    piece[0] = 0x53; piece[1] = 0x4C; Buffer.BlockCopy(BitConverter.GetBytes(o.Id), 0, piece, 2, 4);
                    piece[6] = (byte)o.Next; piece[7] = (byte)(o.Next >> 8); piece[8] = (byte)o.Count; piece[9] = (byte)(o.Count >> 8);
                    Buffer.BlockCopy(o.Packet, off, piece, SlowHeader, len);
                    if (!SteamNetworking.SendP2PPacket(o.To, piece, (uint)piece.Length, EP2PSend.k_EP2PSendReliable, SlowChannel)) break;
                    slowTokens -= piece.Length; slowPiecesOut++; o.Next++;
                    if (o.Next >= o.Count) { slowQueue.RemoveAt(0); slowQueued -= o.Packet.Length; slowDone++; }
                }
                SlowReceive(now);
                if (now >= slowNextLog && (slowPiecesOut > 0 || slowPiecesIn > 0)) { slowNextLog = now + 60f; PtLog("SLOW-LANE pieces out " + slowPiecesOut + " in " + slowPiecesIn + ", packets done " + slowDone + ", queued " + slowQueued + " B, old-path sends " + slowFallback + ", rate " + (SlowInMatch() ? "2 KB/s (match)" : "64 KB/s")); }
            }
            catch (Exception e) { PtLog("SLOW-LANE failed: " + e.Message); }
        }

        bool SlowLinked(CSteamID id) { return hosting ? guests.ContainsKey(id) : (bridge != null && id == peer); }

        void SlowReceive(float now)
        {
            uint size;
            for (int i = 0; i < 64 && SteamNetworking.IsP2PPacketAvailable(out size, SlowChannel); i++)
            {
                uint read; CSteamID from;
                if (size > slowRecv.Length) { byte[] skip = new byte[size]; SteamNetworking.ReadP2PPacket(skip, size, out read, out from, SlowChannel); continue; }
                if (!SteamNetworking.ReadP2PPacket(slowRecv, size, out read, out from, SlowChannel) || read < SlowHeader + 1 || slowRecv[0] != 0x53 || slowRecv[1] != 0x4C) continue;
                if (!SlowLinked(from)) continue;
                slowPiecesIn++;
                uint id = BitConverter.ToUInt32(slowRecv, 2); int idx = slowRecv[6] | (slowRecv[7] << 8), count = slowRecv[8] | (slowRecv[9] << 8);
                if (count <= 0 || count > 4200 || idx >= count) continue;   // 4200 pieces = 4 MB
                string key = from.m_SteamID + ":" + id; SlowIn s;
                if (!slowIn.TryGetValue(key, out s)) { s = new SlowIn(); s.Parts = new byte[count][]; s.Since = now; slowIn[key] = s; }
                if (s.Parts.Length != count || s.Parts[idx] != null) continue;
                byte[] part = new byte[read - SlowHeader]; Buffer.BlockCopy(slowRecv, SlowHeader, part, 0, part.Length);
                s.Parts[idx] = part; s.Got++; s.Bytes += part.Length;
                if (s.Got < count) continue;
                slowIn.Remove(key);
                byte[] packet = new byte[s.Bytes]; int o = 0; foreach (byte[] p in s.Parts) { Buffer.BlockCopy(p, 0, packet, o, p.Length); o += p.Length; }
                BridgeCore b = null; if (hosting) guests.TryGetValue(from, out b); else if (from == peer) b = bridge;
                if (b != null) b.Receive(packet, packet.Length);   // same checks and handlers as packets from channel 77
            }
            if (slowIn.Count > 0)
            {
                List<string> old = null;
                foreach (KeyValuePair<string, SlowIn> kv in slowIn) if (now - kv.Value.Since > 300f) { if (old == null) old = new List<string>(); old.Add(kv.Key); }
                if (old != null) foreach (string k in old) slowIn.Remove(k);
            }
        }
    }
}
