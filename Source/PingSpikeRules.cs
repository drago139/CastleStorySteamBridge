using System;
using System.Collections.Generic;

namespace CastleStorySteam
{
    // 0.3.58: what a ping spike means, without Unity/Steam (PingSpikeTests drives it).
    // A round trip includes both main loops: our Update sends and reads, the peer's Update echoes. In the 0.3.56 logs the reliable and
    // unreliable maxima were equal (7717/7718 ms, 7342/7342 ms) and just under the 8 s "lost" cut, which a lossy network would not give.
    //  local   - our own frame stalled for at least half of the round trip;
    //  burst   - several replies were read in one frame: the peer's main loop (or the whole path) froze and released them together;
    //  network - one slow reply with our loop running: the network or Steam relay.
    internal enum SpikeKind { Local, Burst, Network }

    internal static class PingSpikeRules
    {
        internal const double LateHorizonSeconds = 120;

        internal static SpikeKind Classify(double rttMs, double ownLongestFrameMs, int repliesInFrame)
        {
            if (ownLongestFrameMs >= rttMs * 0.5) return SpikeKind.Local;
            if (repliesInFrame >= 2) return SpikeKind.Burst;
            return SpikeKind.Network;
        }

        internal static string Name(SpikeKind k)
        { return k == SpikeKind.Local ? "local stall (our frame)" : k == SpikeKind.Burst ? "burst (peer's main loop or the path froze)" : "network"; }
    }

    // Long frames of our own main loop (times in seconds of one monotonic clock), bounded.
    internal sealed class FrameLog
    {
        readonly double minMs; readonly int capacity;
        readonly Queue<double[]> frames = new Queue<double[]>();
        internal FrameLog(double minMs, int capacity) { this.minMs = minMs; this.capacity = capacity; }
        internal int Count { get { return frames.Count; } }

        internal void Add(double endSeconds, double ms)
        {
            if (ms < minMs) return;
            frames.Enqueue(new double[] { endSeconds, ms });
            while (frames.Count > capacity) frames.Dequeue();
        }

        // Longest frame that ended after 'since' (a frame that began before it still counts whole: it delayed what we measure).
        internal double LongestSince(double since)
        {
            double max = 0;
            foreach (double[] f in frames) if (f[0] > since && f[1] > max) max = f[1];
            return max;
        }
    }

    // Pings that passed the "lost" cut, so a reply that still comes is reported as late (once) instead of silently dropped.
    internal sealed class ExpiredPings
    {
        readonly Dictionary<uint, double> sentAt = new Dictionary<uint, double>();
        readonly List<uint> drop = new List<uint>();
        internal int Count { get { return sentAt.Count; } }

        internal void Add(uint key, double sentSeconds, double nowSeconds)
        {
            sentAt[key] = sentSeconds;
            drop.Clear();
            foreach (KeyValuePair<uint, double> kv in sentAt) if (nowSeconds - kv.Value > PingSpikeRules.LateHorizonSeconds) drop.Add(kv.Key);
            foreach (uint k in drop) sentAt.Remove(k);
        }

        // True once per expired ping whose echoed send time matches; ageMs = how long the reply really took.
        internal bool TakeLate(uint key, double echoedSentSeconds, double nowSeconds, out double ageMs)
        {
            ageMs = 0;
            double sent;
            if (!sentAt.TryGetValue(key, out sent) || sent != echoedSentSeconds) return false;
            sentAt.Remove(key);
            ageMs = (nowSeconds - sent) * 1000.0;
            return ageMs >= 0;
        }

        internal void Clear() { sentAt.Clear(); }
    }
}
