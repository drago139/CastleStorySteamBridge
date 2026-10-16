using System;
using System.Collections.Generic;
using System.Text;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.11: round-trip meter between the two ends of every bridge link (the host measures each guest, a guest measures the host).
    // Every 2 s a small ping goes out twice: over the reliable Steam channel (what the game's own traffic experiences, including
    // queueing behind game data and retransmits) and over the unreliable one (the raw path; also shows packet loss). The peer's bridge answers at once
    // and echoes the payload, so no clock sync is needed. The answer is produced in the peer's Update, so a spike also means the peer's main
    // loop stalled. Pings are 18 bytes and never reach the game; bridges older than 0.3.11 ignore them.
    public sealed partial class SteamBridgePlugin
    {
        sealed class PingSide
        {
            public int Sent, Received, Lost;
            public float Last, Min = float.MaxValue, Max, Jitter, Prev = -1f;
            public readonly List<float> Ring = new List<float>(128);
            public float LastReplyAt;
            public void Add(float ms)
            {
                Received++; Last = ms;
                LastReplyAt = Time.realtimeSinceStartup;
                if (ms < Min) Min = ms;
                if (ms > Max) Max = ms;
                if (Prev >= 0f) Jitter += 0.1f * (Math.Abs(ms - Prev) - Jitter);
                Prev = ms;
                Ring.Add(ms);
                if (Ring.Count > 120) Ring.RemoveAt(0);
            }
            public float Percentile(float q)
            {
                if (Ring.Count == 0) return 0f;
                List<float> copy = new List<float>(Ring);
                copy.Sort();
                int i = (int)Math.Round((copy.Count - 1) * q);
                return copy[Math.Max(0, Math.Min(copy.Count - 1, i))];
            }
        }

        sealed class PingPeer
        {
            public readonly PingSide Reliable = new PingSide(), Unreliable = new PingSide();
            public uint Seq;
            public readonly Dictionary<uint, long> Out = new Dictionary<uint, long>();
            public float FirstSentAt, LastSpikeLog, LastReplyAt;
            public bool Answered;
        }

        readonly Dictionary<CSteamID, PingPeer> pings = new Dictionary<CSteamID, PingPeer>();
        readonly List<uint> pingExpired = new List<uint>();
        float nextPingAt, nextPingLog, nextPingError;
        const float PingEverySeconds = 2f, PingLostSeconds = 8f, PingSpikeMs = 500f;

        void PingReset() { pings.Clear(); nextPingAt = 0; nextPingLog = 0; }

        // Called about once a second from UpdateTick.
        void PingTick()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < nextPingAt) return;
                nextPingAt = now + PingEverySeconds;
                foreach (CSteamID id in DiagPeers())
                {
                    PingPeer p;
                    if (!pings.TryGetValue(id, out p)) { p = new PingPeer(); p.FirstSentAt = now; pings[id] = p; }
                    PingExpire(p);
                    p.Seq++;
                    PingSend(id, p, 0);
                    PingSend(id, p, 1);
                }
                if (now >= nextPingLog && pings.Count > 0)
                {
                    nextPingLog = now + 60f;
                    foreach (KeyValuePair<CSteamID, PingPeer> kv in pings)
                        Logger.LogInfo("Ping " + PingName(kv.Key) + " [" + PingLink(kv.Key) + "]: reliable " + PingShort(kv.Value.Reliable) + "; unreliable " + PingShort(kv.Value.Unreliable));
                }
            }
            catch (Exception e)
            {
                if (Time.realtimeSinceStartup >= nextPingError) { nextPingError = Time.realtimeSinceStartup + 30f; Logger.LogWarning("Ping meter: " + e.Message); }
            }
        }

        void PingExpire(PingPeer p)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            pingExpired.Clear();
            foreach (KeyValuePair<uint, long> kv in p.Out)
                if ((now - kv.Value) * TickMs > PingLostSeconds * 1000.0) pingExpired.Add(kv.Key);
            foreach (uint key in pingExpired)
            {
                p.Out.Remove(key);
                if ((key & 1) == 0) p.Reliable.Lost++; else p.Unreliable.Lost++;
            }
        }

        void PingSend(CSteamID id, PingPeer p, byte mode)
        {
            byte[] data = new byte[13];
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            data[0] = mode;
            Buffer.BlockCopy(BitConverter.GetBytes(p.Seq), 0, data, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(ts), 0, data, 5, 8);
            p.Out[(p.Seq << 1) | mode] = ts;
            if (mode == 0) p.Reliable.Sent++; else p.Unreliable.Sent++;
            PingTransmit(id, BridgeCore.Packet(BridgeCore.KindPing, data, 13), mode == 1);
        }

        // mode 1 goes out unreliable whatever the game's send mode is; mode 0 follows the normal (reliable) path
        bool PingTransmit(CSteamID id, byte[] packet, bool unreliable)
        {
            if (!unreliable) return SendTo(id, packet);
            if (!SteamNetworking.SendP2PPacket(id, packet, (uint)packet.Length, EP2PSend.k_EP2PSendUnreliable, Channel)) return false;
            statSteamOut++;
            return true;
        }

        void OnPingPacket(CSteamID from, byte kind, byte[] payload)
        {
            try
            {
                if (!IsTrusted(from) || payload == null || payload.Length != 13 || payload[0] > 1) return;
                byte mode = payload[0];
                if (kind == BridgeCore.KindPing)
                {
                    PingTransmit(from, BridgeCore.Packet(BridgeCore.KindPong, payload, 13), mode == 1);
                    return;
                }
                PingPeer p;
                if (!pings.TryGetValue(from, out p)) return;
                uint seq = BitConverter.ToUInt32(payload, 1);
                long ts = BitConverter.ToInt64(payload, 5), sent;
                uint key = (seq << 1) | mode;
                if (!p.Out.TryGetValue(key, out sent) || sent != ts) return;      // answered too late (counted as lost) or a duplicate
                p.Out.Remove(key);
                float ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - ts) * TickMs);
                (mode == 0 ? p.Reliable : p.Unreliable).Add(ms);
                p.Answered = true; p.LastReplyAt = Time.realtimeSinceStartup;
                float now = Time.realtimeSinceStartup;
                if (ms > PingSpikeMs && now - p.LastSpikeLog > 10f)
                {
                    p.LastSpikeLog = now;
                    DiagWarn("Ping spike (" + (mode == 0 ? "reliable" : "unreliable") + "): " + ms.ToString("F0") + " ms to " + PingName(from));
                }
            }
            catch (Exception e) { Logger.LogWarning("Ping packet failed: " + e.Message); }
        }

        bool PingIsFresh(CSteamID id)
        {
            PingPeer p;
            return pings.TryGetValue(id, out p) && p.Answered && Time.realtimeSinceStartup - p.LastReplyAt <= 6f;
        }
        string MissingPingReason(CSteamID id)
        {
            string version;
            if (peerVersions.TryGetValue(id, out version) && !UpdateVerifier.IsNewer(version, "0.3.10"))
                return "пінг недоступний: підтверджена версія друга " + version + " (потрібна 0.3.11+)";
            return "відповідей на пінг немає; причина невідома";
        }
        static string PingName(CSteamID id)
        {
            try { return SteamFriends.GetFriendPersonaName(id); } catch (Exception) { return "?"; }
        }

        static string PingLink(CSteamID id)
        {
            try
            {
                P2PSessionState_t s;
                if (SteamNetworking.GetP2PSessionState(id, out s)) return s.m_bUsingRelay != 0 ? "relay" : "P2P";
            }
            catch (Exception) { }
            return "?";
        }

        static string PingShort(PingSide s)
        {
            if (s.Received == 0) return "-";
            return s.Last.ToString("F0") + " (med " + s.Percentile(0.5f).ToString("F0") + ", p95 " + s.Percentile(0.95f).ToString("F0") + ", max " + s.Max.ToString("F0") + ") ms, jitter " + s.Jitter.ToString("F0");
        }

        static string PingLong(PingSide s, bool unreliable)
        {
            if (s.Received == 0) return "немає відповідей (надіслано " + s.Sent + ")";
            string t = "останній " + s.Last.ToString("F0") + " мс; медіана " + s.Percentile(0.5f).ToString("F0") + "; p95 " + s.Percentile(0.95f).ToString("F0")
                + "; мін " + s.Min.ToString("F0") + "; макс " + s.Max.ToString("F0") + "; джитер " + s.Jitter.ToString("F0") + " мс; відповідей " + s.Received + " з " + s.Sent;
            if (unreliable) { int total = s.Received + s.Lost; t += "; втрати " + s.Lost + (total == 0 ? "" : " (" + (100.0 * s.Lost / total).ToString("F1") + " %)"); }
            else if (s.Lost > 0) t += "; відповідь >" + (int)PingLostSeconds + " с: " + s.Lost + " раз";
            return t;
        }

        void AppendPingReport(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("--- Пінг до гравців (міст міряє кожні 2 с; «надійний» = те, що відчуває гра; потрібна версія 0.3.11 в обох)");
            if (pings.Count == 0) { sb.AppendLine("  вимірювань ще немає"); return; }
            foreach (KeyValuePair<CSteamID, PingPeer> kv in pings)
            {
                PingPeer p = kv.Value;
                sb.AppendLine("  " + PingName(kv.Key) + " [" + PingLink(kv.Key) + "]");
                sb.AppendLine("    надійний:   " + PingLong(p.Reliable, false));
                sb.AppendLine("    ненадійний: " + PingLong(p.Unreliable, true));
                if (!p.Answered && Time.realtimeSinceStartup - p.FirstSentAt > 10f) sb.AppendLine("    " + MissingPingReason(kv.Key));
            }
        }

        float nextPingUi; readonly List<string> pingUi = new List<string>();
        // 0.3.45: the texts are rebuilt once a second (sorting the history on every OnGUI pass produced garbage and hitches).
        void DrawPing()
        {
            if (pings.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextPingUi)
            {
                nextPingUi = now + 1f; pingUi.Clear();
                pingUi.Add("Пінг до гравців (оновлюється кожні 2 с):");
                foreach (KeyValuePair<CSteamID, PingPeer> kv in pings)
                {
                    PingPeer p = kv.Value;
                    if (p.Answered && !PingIsFresh(kv.Key)) { pingUi.Add(PingName(kv.Key) + ": немає свіжої відповіді; останній пінг " + p.Reliable.Last.ToString("F0") + " мс"); continue; }
                    if (!p.Answered && now - p.FirstSentAt > 10f) { pingUi.Add(PingName(kv.Key) + ": " + MissingPingReason(kv.Key)); continue; }
                    pingUi.Add(PingName(kv.Key) + " [" + PingLink(kv.Key) + "]: надійний " + PingShort(p.Reliable) + "; ненадійний " + PingShort(p.Unreliable)
                        + (p.Unreliable.Lost > 0 ? "; втрати " + p.Unreliable.Lost : ""));
                }
            }
            for (int i = 0; i < pingUi.Count; i++) GUILayout.Label(pingUi[i]);
        }    }
}
