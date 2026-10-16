using System;
using System.Collections.Generic;

namespace CastleStorySteam
{
    internal static class CrystalKeepOut
    {
        internal static bool Contains(float x, float y, float z, float ax, float ay, float az, float radius, float halfHeight, float margin)
        {
            float dx = x - ax, dz = z - az, r = radius + margin;
            return dx * dx + dz * dz < r * r && y > ay - halfHeight - 1f && y < ay + halfHeight + 1f;
        }
    }
    // Pure policy shared by runtime and regression tests. Keys include both peer and report kind.
    internal sealed class PeerReportGate
    {
        readonly Dictionary<string, float> next = new Dictionary<string, float>();
        internal bool Accept(ulong peer, byte kind, float now, float interval)
        {
            string key = peer.ToString() + ":" + kind;
            float until;
            if (next.TryGetValue(key, out until) && now < until) return false;
            next[key] = now + interval;
            return true;
        }
        internal void Clear() { next.Clear(); }
    }

    internal sealed class PeerTrafficWindow
    {
        readonly List<double> history = new List<double>();
        internal long LastSent, LastReceived;
        internal int Sent, Received, StallWindows;
        internal double UsualRate, SentRate, ReceivedRate, StallSeconds;
        bool sampled;
        internal void Reset() { history.Clear(); sampled = false; StallWindows = 0; StallSeconds = 0; }
        // 1 = newly stalled, 2 = still stalled, 3 = recovered, 0 = healthy/warming up.
        internal int Observe(long sent, long received, double seconds, bool worldReady)
        {
            if (!worldReady || !sampled || seconds <= 0 || sent < LastSent || received < LastReceived)
            {
                Reset(); sampled = worldReady; LastSent = sent; LastReceived = received;
                Sent = Received = 0; SentRate = ReceivedRate = 0; return 0;
            }
            Sent = (int)Math.Min(int.MaxValue, sent - LastSent);
            Received = (int)Math.Min(int.MaxValue, received - LastReceived);
            LastSent = sent; LastReceived = received;
            SentRate = Sent / seconds; ReceivedRate = Received / seconds;
            List<double> sorted = new List<double>(history); sorted.Sort();
            UsualRate = sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
            bool stalled = history.Count >= 4 && UsualRate >= 1 && SentRate < UsualRate * 0.15;
            if (stalled)
            {
                StallSeconds += seconds; StallWindows++;
                return StallWindows == 1 ? 1 : 2;
            }
            if (StallWindows > 0)
            {
                // Preserve the original baseline while a link is only partly recovering.
                if (SentRate < UsualRate * 0.5) { StallSeconds += seconds; return 2; }
                StallWindows = 0; StallSeconds = 0;
                Add(SentRate); return 3;
            }
            Add(SentRate); return 0;
        }
        void Add(double rate) { history.Add(rate); if (history.Count > 8) history.RemoveAt(0); }
    }
}
