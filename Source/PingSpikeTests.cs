using System;
using System.Collections.Generic;
using CastleStorySteam;

// PingSpikeRules plus a frame-level simulation of the ping meter: our loop and the peer's loop run in 10 ms frames, pings go out every
// 2 s on two channels, the network has a one-way delay, and either loop (or one packet) can stall. The glue mirrors PingMeter.cs
// (batch per frame, expiry at 8 s, late replies); it is a model, not the Unity code and not a real Steam link.
static class PingSpikeTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    sealed class Result
    {
        internal int Local, Burst, Network, Late; internal double MaxReliable, MaxUnreliable, LateMax;
        internal List<double> Rtts = new List<double>();
    }

    // Times in seconds. peerStall/ownStall: [from, to) intervals where that main loop runs no frames. slowPacketAt: one ping whose way out takes +extra.
    static Result Simulate(double oneWay, double peerFrom, double peerTo, double ownFrom, double ownTo, double slowPacketAt, double slowExtra, double length)
    {
        const double Frame = 0.01, Every = 2, Lost = 8, SpikeMs = 500;
        Result r = new Result();
        FrameLog frames = new FrameLog(50, 256);
        ExpiredPings expired = new ExpiredPings();
        Dictionary<uint, double> outstanding = new Dictionary<uint, double>();
        List<double[]> toPeer = new List<double[]>(), toUs = new List<double[]>();   // {arrival, key, sentAt}
        uint seq = 0; long nextPingFrame = 0, everyFrames = (long)Math.Round(Every / Frame); double lastFrame = -1;
        int batchCount = 0; double batchMax = 0, batchOwn = 0;
        long frameCount = (long)Math.Round(length / Frame);
        for (long f = 0; f < frameCount; f++)
        {
            double t = f * Frame;                     // integer frames: no drift of the ping schedule
            bool ownRuns = !(t >= ownFrom && t < ownTo), peerRuns = !(t >= peerFrom && t < peerTo);
            if (peerRuns)
                for (int i = toPeer.Count - 1; i >= 0; i--)
                    if (toPeer[i][0] <= t) { toUs.Add(new double[] { t + oneWay, toPeer[i][1], toPeer[i][2] }); toPeer.RemoveAt(i); }
            if (!ownRuns) continue;
            // PingFrame: record our frame, then judge the replies read in the previous frame.
            if (lastFrame >= 0) frames.Add(t, (t - lastFrame) * 1000);
            lastFrame = t;
            if (batchCount > 0)
            {
                if (batchMax > SpikeMs)
                {
                    SpikeKind k = PingSpikeRules.Classify(batchMax, batchOwn, batchCount);
                    if (k == SpikeKind.Local) r.Local++; else if (k == SpikeKind.Burst) r.Burst++; else r.Network++;
                }
                batchCount = 0; batchMax = 0; batchOwn = 0;
            }
            // PingTick: expire, then send a reliable (even key) and an unreliable (odd key) ping.
            if (f >= nextPingFrame)
            {
                nextPingFrame = f + everyFrames;
                List<uint> gone = new List<uint>();
                foreach (KeyValuePair<uint, double> kv in outstanding) if (t - kv.Value > Lost) gone.Add(kv.Key);
                foreach (uint k in gone) { expired.Add(k, outstanding[k], t); outstanding.Remove(k); }
                seq++;
                for (uint mode = 0; mode < 2; mode++)
                {
                    uint key = (seq << 1) | mode;
                    outstanding[key] = t;
                    double extra = slowPacketAt >= 0 && Math.Abs(t - slowPacketAt) < Frame / 2 && mode == 0 ? slowExtra : 0;
                    toPeer.Add(new double[] { t + oneWay + extra, key, t });
                }
            }
            // Read replies (OnPingPacket).
            for (int i = toUs.Count - 1; i >= 0; i--)
            {
                if (toUs[i][0] > t) continue;
                uint key = (uint)toUs[i][1]; double sentAt = toUs[i][2]; toUs.RemoveAt(i);
                double ms, sent;
                if (outstanding.TryGetValue(key, out sent) && sent == sentAt)
                {
                    outstanding.Remove(key);
                    ms = (t - sentAt) * 1000;
                    r.Rtts.Add(ms);
                    if ((key & 1) == 0) r.MaxReliable = Math.Max(r.MaxReliable, ms); else r.MaxUnreliable = Math.Max(r.MaxUnreliable, ms);
                }
                else if (expired.TakeLate(key, sentAt, t, out ms)) { r.Late++; r.LateMax = Math.Max(r.LateMax, ms); }
                else continue;
                batchCount++;
                if (ms > batchMax) { batchMax = ms; batchOwn = frames.LongestSince(sentAt); }
            }
        }
        return r;
    }

    public static int Main()
    {
        try
        {
            Check(PingSpikeRules.Classify(7700, 7600, 1) == SpikeKind.Local, "own stall covers the round trip");
            Check(PingSpikeRules.Classify(7700, 3850, 4) == SpikeKind.Local, "half of the round trip is ours: local wins");
            Check(PingSpikeRules.Classify(7700, 40, 4) == SpikeKind.Burst, "several replies in one frame");
            Check(PingSpikeRules.Classify(7700, 40, 2) == SpikeKind.Burst, "both channels of one ping held back together (the 0.3.56 equal maxima) is already a burst");
            Check(PingSpikeRules.Classify(1500, 40, 1) == SpikeKind.Network, "one slow reply");

            FrameLog log = new FrameLog(50, 3);
            log.Add(1, 20); Check(log.Count == 0, "short frames not kept");
            log.Add(2, 300); log.Add(3, 900); log.Add(4, 100);
            Check(log.LongestSince(0) == 900 && log.LongestSince(2.5) == 900 && log.LongestSince(3) == 100 && log.LongestSince(4) == 0, "longest since");
            log.Add(5, 60); Check(log.Count == 3 && log.LongestSince(0) == 900, "bounded, oldest dropped");

            ExpiredPings ex = new ExpiredPings();
            ex.Add(10, 1.0, 9.5);
            double age;
            Check(!ex.TakeLate(10, 1.5, 12, out age), "echoed time must match");
            Check(ex.TakeLate(10, 1.0, 12, out age) && Math.Abs(age - 11000) < 1e-6, "late reply age");
            Check(!ex.TakeLate(10, 1.0, 12.1, out age), "a late reply counts once (duplicate)");
            ex.Add(11, 0, 0); ex.Add(12, 200, 200); Check(ex.Count == 1, "entries older than the horizon dropped");

            // 1) The 0.3.56 picture: the peer's loop freezes ~7.7 s. Equal maxima on both channels, a saw-tooth, judged "burst", nothing "local".
            Result peer = Simulate(0.03, 20.02, 27.72, -1, -1, -1, 0, 40);
            Check(Math.Abs(peer.MaxReliable - peer.MaxUnreliable) < 1 && peer.MaxReliable > 7000 && peer.MaxReliable < 8000, "peer stall: equal maxima just under 8 s (" + peer.MaxReliable + ")");
            int saw = 0; foreach (double ms in peer.Rtts) if (ms > 1000 && ms < 7000) saw++;
            Check(saw >= 4, "peer stall: saw-tooth of 1..7 s replies");
            Check(peer.Burst == 1 && peer.Local == 0 && peer.Network == 0, "peer stall judged burst (" + peer.Burst + "/" + peer.Local + "/" + peer.Network + ")");

            // 2) Same length, but OUR loop freezes: also equal maxima, but it must be called local, not the peer.
            Result own = Simulate(0.03, -1, -1, 20.02, 27.72, -1, 0, 40);
            Check(own.Local >= 1 && own.Burst == 0 && own.Network == 0, "own stall judged local (" + own.Burst + "/" + own.Local + "/" + own.Network + ")");

            // 3) One slow packet on the network, both loops running: network.
            Result net = Simulate(0.03, -1, -1, -1, -1, 20, 1.5, 40);
            Check(net.Network == 1 && net.Burst == 0 && net.Local == 0 && net.MaxReliable > 1500 && net.MaxUnreliable < 200, "single slow packet judged network");

            // 4) A 12 s peer freeze (expiry runs only when the next ping goes out, so the plain max can reach 8-10 s): the old meter capped the max and silently called the rest lost; now they are late with their real age.
            Result longPeer = Simulate(0.03, 20.02, 32.02, -1, -1, -1, 0, 50);
            Check(longPeer.MaxReliable < 10000 && longPeer.Late >= 2 && longPeer.LateMax > 11000 && longPeer.LateMax < 12500, "long stall: late replies keep real age (" + longPeer.Late + ", " + longPeer.LateMax + ")");
            Check(longPeer.Burst == 1 && longPeer.Local == 0, "long stall is one burst");

            // 5) Calm link: no spikes at all.
            Result calm = Simulate(0.03, -1, -1, -1, -1, -1, 0, 60);
            Check(calm.Local + calm.Burst + calm.Network + calm.Late == 0 && calm.MaxReliable < 100, "calm link");

            Console.WriteLine("PASS: " + checks + " ping spike checks (rules, frame log, late replies, simulated peer/own/network stalls, 12 s stall past the 8 s cut).");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
    }
}
