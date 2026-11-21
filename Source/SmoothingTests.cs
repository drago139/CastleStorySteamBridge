using System;
using System.Collections.Generic;
using CastleStorySteam;

// Unit tests + a network simulator comparing the 0.3.81 behaviour ("direct": newest packet written at once, game lerps at rate 9/s)
// with the 0.3.82 adaptive buffer under clean, jitter, loss, outage and burst conditions. Metrics on the rendered position vs. the host truth:
// stutter = mean |frame speed - true speed| (smoothness), jumps = frames moving > 1 unit, freezeMs = time a moving unit stood still, lagErr = mean distance to truth.
public static class SmoothingTests
{
    static int failed;
    static void Check(bool ok, string name) { if (!ok) { failed++; Console.WriteLine("FAIL " + name); } }

    sealed class Result { public double Stutter, LagErr, FreezeMs; public int Jumps; }

    // Host unit walks back and forth along x at 3 u/s (turns every 6 s). Host sends every 100 ms with one-way base 40 ms + jitter, loss, outage.
    static Result Simulate(bool buffered, int jitterMs, int lossPct, int outageFromMs, int outageMs, int seed, out ClockSync clockOut)
    {
        Random r = new Random(seed); ClockSync clock = new ClockSync(); UnitHistory h = new UnitHistory(); CatchUp cu = new CatchUp(); clockOut = clock;
        var inflight = new List<KeyValuePair<double, KeyValuePair<double, float>>>(); // arrival, (hostMs, x)
        Func<double, float> truth = t => { double ph = (t / 1000.0) % 12.0; return (float)(ph < 6 ? ph * 3 : (12 - ph) * 3); };
        double frame = 1000.0 / 60.0; float rendered = 0f, direct = 0f, lastRendered = 0f; Result res = new Result(); int frames = 0; double freeze = 0; int seq = 0, lastSeq = 0;
        for (double t = 0; t < 60000; t += frame)
        {
            if (Math.Floor(t / 100) != Math.Floor((t - frame) / 100) || t == 0)
            {
                seq++; bool outage = t >= outageFromMs && t < outageFromMs + outageMs;
                if (!outage && r.Next(100) >= lossPct) inflight.Add(new KeyValuePair<double, KeyValuePair<double, float>>(t + 40 + r.Next(jitterMs + 1), new KeyValuePair<double, float>(t, truth(t))));
                else if (outage && r.Next(100) < 50) inflight.Add(new KeyValuePair<double, KeyValuePair<double, float>>(outageFromMs + outageMs + 40 + r.Next(200), new KeyValuePair<double, float>(t, truth(t)))); // queued, burst after outage
            }
            for (int i = inflight.Count - 1; i >= 0; i--)
                if (inflight[i].Key <= t)
                {
                    double hostMs = inflight[i].Value.Key; float x = inflight[i].Value.Value; inflight.RemoveAt(i);
                    int s = (int)(hostMs / 100) + 1; int gap = s - lastSeq; if (gap > 0) lastSeq = s;
                    clock.OnPacket(t, hostMs, gap > 0 ? gap : 0);
                    if (gap > 0) direct = x;              // 0.3.81: newest-by-sequence wins, written at once
                    h.Add(hostMs, new V3(x, 0, 0));
                }
            float target;
            if (buffered) { double rt = clock.RenderHostMs(t, frame); SampleState st; target = double.IsNaN(rt) ? direct : cu.Step(h.Sample(rt, 1200, out st), t).X; }
            else target = direct;
            rendered += (target - rendered) * (float)Math.Min(1.0, 9.0 * frame / 1000.0);   // the game's own lerp toward LastServerPosition
            if (t > 3000)
            {
                double trueSpeed = Math.Abs(truth(t) - truth(t - frame)) / frame * 1000; double speed = Math.Abs(rendered - lastRendered) / frame * 1000;
                res.Stutter += Math.Abs(speed - trueSpeed); res.LagErr += Math.Abs(rendered - truth(t)); frames++;
                if (Math.Abs(rendered - lastRendered) > 1f) res.Jumps++;
                if (speed < 0.2 && trueSpeed > 1) freeze += frame;
            }
            lastRendered = rendered;
        }
        res.Stutter /= frames; res.LagErr /= frames; res.FreezeMs = freeze; return res;
    }

    public static int Main()
    {
        // core behaviour
        UnitHistory h = new UnitHistory(); SampleState st;
        h.Add(0, new V3(0, 0, 0)); h.Add(100, new V3(0.5f, 0, 0));
        Check(Math.Abs(h.Sample(50, 500, out st).X - 0.25f) < 0.001f && st == SampleState.Interpolated, "interpolates between samples");
        Check(Math.Abs(h.Sample(300, 500, out st).X - 1.5f) < 0.01f && st == SampleState.Extrapolated, "extrapolates a moving unit");
        Check(Math.Abs(h.Sample(2000, 500, out st).X - 3f) < 0.01f && st == SampleState.Held, "extrapolation is bounded");
        Check(!h.Add(100, new V3(9, 0, 0)), "duplicate/stale sample rejected");
        Check(h.Add(50, new V3(0.25f, 0, 0)), "late sample fills a gap");
        Check(Math.Abs(h.Sample(150, 500, out st).X - 0.75f) < 0.01f, "stale data does not move the newest sample");
        h.Add(200, new V3(50, 0, 0)); Check(h.Teleports == 1 && h.Count == 1 && Math.Abs(h.Sample(150, 500, out st).X - 50f) < 0.01f, "teleport resets history (snap, no slide)");
        UnitHistory still = new UnitHistory(); still.Add(0, new V3(5, 0, 5)); still.Add(100, new V3(5, 0, 5)); Check(Math.Abs(still.Sample(400, 500, out st).X - 5f) < 0.001f && st == SampleState.Held, "standing unit is not extrapolated");
        UnitHistory un = new UnitHistory(); un.Add(0, new V3(0, 0, 0)); un.Add(100, new V3(1, 0, 0)); un.Unchanged(200); Check(Math.Abs(un.Sample(400, 500, out st).X - 1f) < 0.001f, "tick without the unit = stopped, no extrapolation");
        CatchUp cc = new CatchUp(); cc.Step(new V3(0, 0, 0), 0); V3 s1 = cc.Step(new V3(5, 0, 0), 100); Check(Math.Abs(s1.X - 0.9f) < 0.01f, "catch-up limits a 5-unit correction to 9 u/s");
        V3 s2 = cc.Step(new V3(30, 0, 0), 200); Check(Math.Abs(s2.X - 30f) < 0.01f, "teleport is applied at once");
        V3 s3 = cc.Step(new V3(30.5f, 0, 0), 300); Check(Math.Abs(s3.X - 30.5f) < 0.01f, "normal walking is not slowed");
        StampUnwrap w = new StampUnwrap(); w.Next(65530); Check(w.Next(4) == 65540, "16-bit timestamp unwraps");
        ClockSync c = new ClockSync(); for (int i = 0; i < 50; i++) c.OnPacket(1000 + i * 100 + 40, i * 100, 1);
        c.RenderHostMs(6000, 16); Check(c.DelayMs <= 101f && c.JitterP95Ms < 1f, "clean link -> minimum delay");
        ClockSync j = new ClockSync(); Random rr = new Random(1); for (int i = 0; i < 100; i++) j.OnPacket(1000 + i * 100 + 40 + rr.Next(200), i * 100, 1);
        double prevR = double.NaN; bool back = false; for (int f = 0; f < 60 * 5; f++) { double rr2 = j.RenderHostMs(12000 + f * 16.6, 16.6); if (!double.IsNaN(prevR) && rr2 < prevR) back = true; prevR = rr2; }
        Check(!back, "render time never goes backwards while the delay rises");
        Check(j.DelayMs > 250f && j.DelayMs <= 500f, "jitter raises the delay (" + j.DelayMs + ")");
        for (int i = 100; i < 400; i++) j.OnPacket(1000 + i * 100 + 40, i * 100, 1); for (int f = 0; f < 60 * 30; f++) j.RenderHostMs(41000 + f * 16, 16.6);
        Check(j.DelayMs < 150f, "delay falls back after the link calms down (" + j.DelayMs + ")");
        ClockSync o = new ClockSync(); for (int i = 0; i < 30; i++) o.OnPacket(1000 + i * 100 + 40, i * 100, 1); o.OnPacket(1000 + 30 * 100 + 3040, 3000, 1);
        Check(o.Outages == 1, "outage detected");

        // regression (live bug 2026-10-23): 16-bit stamps across a 40 s gap must not be unwrapped relative to the old value
        ClockSync rc = new ClockSync(); rc.OnPacket(1000, 0, 1); rc.Reset(); Check(double.IsNaN(rc.RenderHostMs(2000, 16)), "reset forgets the old time base");

        // simulator comparison
        string[] names = { "clean", "jitter 0-150ms", "jitter 0-300ms", "loss 20%", "loss 20% + jitter 150", "outage 2 s" };
        int[,] cond = { { 0, 0, 0, 0 }, { 150, 0, 0, 0 }, { 300, 0, 0, 0 }, { 0, 20, 0, 0 }, { 150, 20, 0, 0 }, { 0, 0, 20000, 2000 } };
        Console.WriteLine("condition | direct(0.3.81) stutter/jumps/freezeMs/lagErr | adaptive(0.3.82) stutter/jumps/freezeMs/lagErr/delay");
        for (int i = 0; i < names.Length; i++)
        {
            ClockSync cs; Result a = Simulate(false, cond[i, 0], cond[i, 1], cond[i, 2], cond[i, 3], 7, out cs); Result b = Simulate(true, cond[i, 0], cond[i, 1], cond[i, 2], cond[i, 3], 7, out cs);
            Console.WriteLine(names[i].PadRight(22) + " | " + a.Stutter.ToString("F2") + " / " + a.Jumps + " / " + a.FreezeMs.ToString("F0") + " / " + a.LagErr.ToString("F2") + " | " + b.Stutter.ToString("F2") + " / " + b.Jumps + " / " + b.FreezeMs.ToString("F0") + " / " + b.LagErr.ToString("F2") + " / " + cs.DelayMs.ToString("F0") + "ms");
            if (i >= 1 && i <= 4) Check(b.Stutter < a.Stutter, names[i] + ": adaptive is smoother");
            Check(b.Jumps <= a.Jumps, names[i] + ": adaptive has no more jumps");
        }
        Console.WriteLine(failed == 0 ? "PASS: smoothing core + simulator" : "FAILED " + failed);
        return failed == 0 ? 0 : 1;
    }
}
