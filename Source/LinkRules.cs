using System;
using System.Collections.Generic;

namespace CastleStorySteam
{
    // 0.4.5 LINK telemetry: the decisions that need neither Unity nor Steam, so LinkProbeTests can drive them. LOG ONLY: nothing here
    // changes how packets are sent, resent, delayed or dropped.
    public static class LinkRules
    {
        // The silence ladder seen on the user <-> host path on 2026-11-05: 4, 8, 16, 32, 64 s (a doubling retransmission timer).
        // FreezeWatch samples every 0.25 s, so a measured silence is up to 0.25 s below the true one (3.8, 7.8, 15.8, 31.8, 63.8).
        // Returns n for the rung 4 * 2^n (n = 0..6), or -1 when the value is not on the ladder.
        public static int LadderRung(float seconds)
        {
            for (int n = 0; n <= 6; n++)
            {
                float target = 4f * (float)Math.Pow(2.0, n);
                if (seconds >= target - 0.9f && seconds <= target + 0.35f) return n;
            }
            return -1;
        }

        // Log2 bucket of an age in seconds: [4,8) = 0, [8,16) = 1, [16,32) = 2 ...; -1 below 4 s. Used to alert once per doubling.
        public static int AgeBucket(float seconds)
        {
            if (seconds < 4f) return -1;
            int b = (int)Math.Floor(Math.Log(seconds / 4.0, 2.0) + 1e-9);
            return b < 0 ? 0 : (b > 12 ? 12 : b);
        }

        // Short hash of the remote address of a Steam P2P session: changes when the IP or the port changes (NAT rebinding, a switch
        // between direct and relay), cannot be turned back into the address. 6 hex digits.
        public static string AddrHash(uint ip, ushort port)
        {
            uint h = ip * 2654435761u ^ ((uint)port * 40503u);
            h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
            return (h & 0xFFFFFFu).ToString("x6");
        }

        // Share of lost probes in a window: lost / (answered + lost) in percent, or -1 when the window has no verdict yet.
        public static float LossPct(int answered, int lost)
        {
            int total = answered + lost;
            if (answered < 0 || lost < 0 || total <= 0) return -1f;
            return 100f * lost / total;
        }

        // Server-list join through the bridge: may a lobby entry be joined? The lobby owner proves who hosts. A lobby read from the list usually
        // has no owner yet (ownerId 0); then a fresh heartbeat (csb_seen) plus a Steam friend as host is enough, a stranger is refused.
        public static bool LobbyHostAccepted(ulong ownerId, ulong hostId, bool freshHeartbeat, bool hostIsFriend)
        {
            if (hostId == 0) return false;
            if (ownerId == hostId) return true;
            return ownerId == 0 && freshHeartbeat && hostIsFriend;
        }

        // Late-answer ledger. A probe that was not answered within the meter's limit (8 s) is counted lost and forgotten by PingMeter;
        // here it is kept until its answer arrives (the true round trip, e.g. 15.8 s) or it is too old. The numbers PingMeter shows
        // are not changed by this: the ledger only records.
        public sealed class Ledger
        {
            readonly Dictionary<uint, long> unanswered = new Dictionary<uint, long>();
            readonly int cap; readonly double keepMs, tickMs;
            public int LateCount; public double LateMaxMs, LateLastMs; public int LateLastMode = -1;
            public Ledger(int cap, double keepMs, double tickMs) { this.cap = cap; this.keepMs = keepMs; this.tickMs = tickMs; }
            public int Count { get { return unanswered.Count; } }

            // key = (seq << 1) | mode (mode 0 reliable, 1 unreliable), sentTicks = Stopwatch timestamp of the send
            public void Expired(uint key, long sentTicks, long nowTicks)
            {
                unanswered[key] = sentTicks;
                Prune(nowTicks);
            }

            // An answer for a probe that PingMeter no longer knows. True when it was one of ours (and not a duplicate).
            public bool Answer(uint key, long sentTicks, long nowTicks, out double rttMs)
            {
                rttMs = 0;
                long sent;
                if (!unanswered.TryGetValue(key, out sent) || sent != sentTicks) return false;
                unanswered.Remove(key);
                rttMs = (nowTicks - sentTicks) * tickMs;
                LateCount++; LateLastMs = rttMs; LateLastMode = (int)(key & 1);
                if (rttMs > LateMaxMs) LateMaxMs = rttMs;
                return true;
            }

            // Age in ms of the oldest unanswered probe of one mode (0 reliable, 1 unreliable); -1 when there is none.
            public double OldestAgeMs(int mode, long nowTicks)
            {
                double best = -1;
                foreach (KeyValuePair<uint, long> kv in unanswered)
                {
                    if ((int)(kv.Key & 1) != mode) continue;
                    double age = (nowTicks - kv.Value) * tickMs;
                    if (age > best) best = age;
                }
                return best;
            }

            void Prune(long nowTicks)
            {
                if (unanswered.Count == 0) return;
                List<uint> drop = null;
                foreach (KeyValuePair<uint, long> kv in unanswered)
                    if ((nowTicks - kv.Value) * tickMs > keepMs) { if (drop == null) drop = new List<uint>(); drop.Add(kv.Key); }
                if (drop != null) foreach (uint k in drop) unanswered.Remove(k);
                while (unanswered.Count > cap)
                {
                    uint oldestKey = 0; long oldest = long.MaxValue; bool any = false;
                    foreach (KeyValuePair<uint, long> kv in unanswered) if (kv.Value < oldest) { oldest = kv.Value; oldestKey = kv.Key; any = true; }
                    if (!any) break;
                    unanswered.Remove(oldestKey);
                }
            }
        }

        // Rate limit for alert lines: one line per key per minimum interval, so a bad minute cannot flood the log.
        public sealed class AlertGate
        {
            readonly Dictionary<string, double> last = new Dictionary<string, double>();
            public bool Allow(string key, double nowSeconds, double minIntervalSeconds)
            {
                double at;
                if (last.TryGetValue(key, out at) && nowSeconds - at < minIntervalSeconds) return false;
                last[key] = nowSeconds;
                if (last.Count > 400) last.Clear();
                return true;
            }
        }
    }
}
