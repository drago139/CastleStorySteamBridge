using System;
using System.Collections.Generic;

namespace CastleStorySteam
{
    // 0.3.82 adaptive smoothing, pure logic (no Unity, unit-tested).
    //   ClockSync    host send times (ms) vs local receive times: windowed minimum one-way offset (drift-safe), delay variation (jitter) p95,
    //                packet loss, outage detection, and an adaptive playout delay that rises at once and falls slowly.
    //   UnitHistory  per-unit (hostMs, position) samples. Sample(renderMs) interpolates between known samples, extrapolates a moving unit for a
    //                bounded time when data is late, then holds. A jump larger than TeleportDistance is a real teleport: history is reset.
    public struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static V3 Lerp(V3 a, V3 b, float t) { return new V3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t); }
        public float Dist(V3 o) { float dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z; return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); }
    }

    public sealed class ClockSync
    {
        public float MinDelayMs = 100f, MaxDelayMs = 500f, TickMs = 100f, FallPerSecondMs = 50f, RisePerSecondMs = 150f;
        public int FixedDelayMs = -1;                     // -1 = adaptive, 0 = smoothing off, >0 = fixed
        readonly double[] bucketMin = new double[10]; readonly long[] bucketId = new long[] { long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue };
        readonly List<float> variation = new List<float>();
        double lastLocal = -1; bool haveOffset; double offset;
        public double LastHostMs = -1; public float DelayMs = 100f, JitterP95Ms, LossPercent; public int Outages; public float LastOutageMs;
        int expected, received; double ignoreVariationUntil;

        public bool Active { get { return FixedDelayMs != 0; } }
        public void Reset()
        {
            for (int i = 0; i < bucketId.Length; i++) bucketId[i] = long.MinValue;
            variation.Clear(); lastLocal = -1; haveOffset = false; DelayMs = MinDelayMs; LastHostMs = -1; JitterP95Ms = 0; expected = received = 0; LossPercent = 0; ignoreVariationUntil = 0;
        }
        public double Offset { get { return offset; } }

        // One packet arrived. localMs = local receive time, hostMs = host send time (unwrapped), seqGap = sequence difference to the previous packet.
        public void OnPacket(double localMs, double hostMs, int seqGap)
        {
            if (seqGap > 0 || expected > received) { if (seqGap > 0) expected += seqGap; received++; if (received > expected) received = expected; if (expected > 300) { expected = expected * 2 / 3; received = received * 2 / 3; } LossPercent = expected > 0 ? 100f * Math.Max(0, expected - received) / expected : 0f; }
            if (lastLocal >= 0 && localMs - lastLocal > 1000) { Outages++; LastOutageMs = (float)(localMs - lastLocal); ignoreVariationUntil = localMs + 1500; }
            lastLocal = localMs;
            double o = localMs - hostMs; long b = (long)Math.Floor(localMs / 2000.0); int k = (int)(b % bucketMin.Length); if (k < 0) k += bucketMin.Length;
            if (bucketId[k] != b) { bucketId[k] = b; bucketMin[k] = o; } else if (o < bucketMin[k]) bucketMin[k] = o;
            double m = double.MaxValue; for (int i = 0; i < bucketMin.Length; i++) if (bucketId[i] != long.MinValue && bucketId[i] > b - bucketMin.Length && bucketId[i] <= b && bucketMin[i] < m) m = bucketMin[i];
            offset = m; haveOffset = true;
            if (hostMs > LastHostMs) LastHostMs = hostMs;
            if (localMs >= ignoreVariationUntil) { variation.Add((float)(o - offset)); if (variation.Count > 300) variation.RemoveAt(0); }
            if (variation.Count >= 5) { List<float> s = new List<float>(variation); s.Sort(); JitterP95Ms = s[(int)Math.Min(s.Count - 1, Math.Floor(s.Count * 0.95))]; }
        }

        // Called every frame with the local time. Returns the host time that should be shown now.
        public double RenderHostMs(double localMs, double frameDtMs)
        {
            float target;
            if (FixedDelayMs > 0) target = FixedDelayMs;
            else target = Clamp(TickMs + JitterP95Ms * 1.2f + (LossPercent > 5f ? TickMs : 0f), MinDelayMs, MaxDelayMs);
            // The delay changes gradually (playback runs a little slower/faster) so the shown time never jumps back or forward.
            float sec = (float)(frameDtMs / 1000.0);
            if (target >= DelayMs) DelayMs = Math.Min(target, DelayMs + RisePerSecondMs * sec); else DelayMs = Math.Max(target, DelayMs - FallPerSecondMs * sec);
            return haveOffset ? localMs - offset - DelayMs : double.NaN;
        }
        static float Clamp(float v, float a, float b) { return v < a ? a : v > b ? b : v; }
    }

    public enum SampleState { None, Interpolated, Extrapolated, Held }

    public sealed class UnitHistory
    {
        public const float TeleportDistance = 10f, MaxSpeed = 7f;
        struct S { public double T; public V3 P; public Q4 R; public bool HasR; }   // 0.3.83: optional rotation from the same update
        readonly List<S> s = new List<S>(16);
        public int Count { get { return s.Count; } }
        public double NewestMs { get { return s.Count == 0 ? double.NegativeInfinity : s[s.Count - 1].T; } }
        public V3 Newest { get { return s[s.Count - 1].P; } }
        public int Teleports;

        // Returns false for a stale sample (not newer than what we have): stale data never overwrites newer data.
        public bool Add(double hostMs, V3 p) { return Add(hostMs, p, Q4.Identity, false); }
        public bool Add(double hostMs, V3 p, Q4 r, bool hasR)
        {
            if (s.Count > 0 && hostMs <= s[s.Count - 1].T)
            {
                // an older sample that arrived late may still fill a gap inside the history
                for (int i = s.Count - 1; i >= 0; i--) { if (s[i].T == hostMs) return false; if (s[i].T < hostMs) { S x; x.T = hostMs; x.P = p; x.R = r; x.HasR = hasR; s.Insert(i + 1, x); return true; } }
                return false;
            }
            if (s.Count > 0 && s[s.Count - 1].P.Dist(p) > TeleportDistance) { s.Clear(); Teleports++; }
            S n; n.T = hostMs; n.P = p; n.R = r; n.HasR = hasR; s.Add(n); if (s.Count > 24) s.RemoveAt(0);
            return true;
        }

        public V3 Sample(double renderMs, float maxExtrapolateMs, out SampleState state)
        {
            state = SampleState.None; if (s.Count == 0) return new V3();
            if (renderMs <= s[0].T) { state = SampleState.Interpolated; return s[0].P; }
            for (int i = s.Count - 1; i > 0; i--)
                if (s[i - 1].T <= renderMs && renderMs <= s[i].T)
                {
                    double span = s[i].T - s[i - 1].T; float f = span <= 0 ? 1f : (float)((renderMs - s[i - 1].T) / span);
                    state = SampleState.Interpolated; return V3.Lerp(s[i - 1].P, s[i].P, f);
                }
            S last = s[s.Count - 1];
            if (s.Count < 2 || maxExtrapolateMs <= 0) { state = SampleState.Held; return last.P; }
            S prev = s[s.Count - 2]; double dt = (last.T - prev.T) / 1000.0;
            if (dt <= 0.02 || dt > 1.0) { state = SampleState.Held; return last.P; }
            float vx = (float)((last.P.X - prev.P.X) / dt), vz = (float)((last.P.Z - prev.P.Z) / dt);
            float sp = (float)Math.Sqrt(vx * vx + vz * vz); if (sp < 0.05f) { state = SampleState.Held; return last.P; }
            if (sp > MaxSpeed) { vx *= MaxSpeed / sp; vz *= MaxSpeed / sp; }
            double ahead = Math.Min(renderMs - last.T, maxExtrapolateMs) / 1000.0;
            state = renderMs - last.T > maxExtrapolateMs ? SampleState.Held : SampleState.Extrapolated;
            return new V3(last.P.X + vx * (float)ahead, last.P.Y, last.P.Z + vz * (float)ahead);
        }

        // The host sends only changed positions; a tick without this unit means it did not move at that tick.
        public void Unchanged(double tickMs) { if (s.Count > 0 && tickMs > s[s.Count - 1].T) Add(tickMs, s[s.Count - 1].P, s[s.Count - 1].R, s[s.Count - 1].HasR); }

        // Facing at the same render time as the position: interpolated between the two samples around it, otherwise the nearest one
        // (a unit drawn ahead of the newest sample keeps the newest facing; the facing is never extrapolated).
        public bool SampleRotation(double renderMs, out Q4 r)
        {
            r = Q4.Identity; int last = s.Count - 1;
            if (last < 0 || !s[last].HasR) return false;      // the newest update has no rotation (0.3.82 host or rotation switched off): leave it to the game
            if (renderMs >= s[last].T) { r = s[last].R; return true; }
            for (int i = last; i > 0; i--)
                if (s[i - 1].T <= renderMs && renderMs <= s[i].T)
                {
                    if (!s[i - 1].HasR) { r = s[i].R; return true; }
                    double span = s[i].T - s[i - 1].T; float f = span <= 0 ? 1f : (float)((renderMs - s[i - 1].T) / span);
                    r = Q4.Nlerp(s[i - 1].R, s[i].R, f); return true;
                }
            for (int i = 0; i <= last; i++) if (s[i].HasR) { r = s[i].R; return true; }
            return false;
        }
        public void Clear() { s.Clear(); }
    }

    // Recovery without jumps: what is shown moves toward the sampled position at most CatchUpSpeed (above any walking speed, so normal motion is
    // never slowed). A difference above TeleportDistance is a real teleport and is applied at once.
    public sealed class CatchUp
    {
        public const float CatchUpSpeed = 9f;
        bool have; V3 shown; double lastMs; public int Limited;
        public V3 Step(V3 target, double nowMs)
        {
            if (!have) { have = true; shown = target; lastMs = nowMs; return target; }
            double dt = Math.Max(0.0, Math.Min(0.25, (nowMs - lastMs) / 1000.0)); lastMs = nowMs;
            float d = shown.Dist(target);
            if (d > UnitHistory.TeleportDistance || d <= CatchUpSpeed * dt) { shown = target; return shown; }
            Limited++; shown = V3.Lerp(shown, target, (float)(CatchUpSpeed * dt / d)); return shown;
        }
        public void Reset() { have = false; }
    }

    // 16-bit host timestamps on the wire (the side-channel header had 2 spare bytes). Unwrap relative to the previous one.
    public sealed class StampUnwrap
    {
        bool have; ushort last; double value;
        public double Next(ushort stamp) { if (!have) { have = true; last = stamp; value = stamp; return value; } int d = (short)(stamp - last); last = stamp; value += d; return value; }
        public void Reset() { have = false; }
    }
}
