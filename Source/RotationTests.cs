using System;
using System.Collections.Generic;
using CastleStorySteam;

// 0.3.83 B-009 tests: rotation packing, packet layout/compatibility, sequence recovery, stale-write rule, rotation history,
// plus a network simulator comparing the facing a guest draws with 0.3.82 (rotation only on the reliable main stream, 6 Hz)
// and 0.3.83 (rotation in the same side-channel update as the position), with smoothing off (direct) and on (adaptive buffer).
// Metrics while the guest-drawn unit moves (> 0.5 u/s): backwards% = facing more than 90 deg away from the drawn walking direction,
// offDeg = mean angle between facing and walking direction, longest = longest continuous backwards stretch (ms).
public static class RotationTests
{
    static int failed;
    static void Check(bool ok, string name) { if (!ok) { failed++; Console.WriteLine("FAIL " + name); } }
    static bool IsNewer(string a, string b) { Version x = new Version(a), y = new Version(b); return x > y; }

    sealed class Result { public double BackPct, OffDeg, LongestMs; }

    // Host unit walks along x at 3 u/s and turns round every 6 s.
    static float TruthX(double t) { double ph = (t / 1000.0) % 12.0; return (float)(ph < 6 ? ph * 3 : (12 - ph) * 3); }
    // Facing follows the path direction at once (Locomotion4.MaintainDirection sets trf.rotation directly on the host).
    static float TruthYaw(double t) { double ph = (t / 1000.0) % 12.0; return ph < 6 ? 90f : -90f; }

    static Result Simulate(bool newFormat, bool buffered, int jitterMs, int lossPct, int mainExtraMs, int outageFromMs, int outageMs, int seed)
    {
        Random r = new Random(seed); ClockSync clock = new ClockSync(); UnitHistory h = new UnitHistory(); CatchUp cu = new CatchUp();
        var side = new List<KeyValuePair<double, KeyValuePair<double, KeyValuePair<float, float>>>>();   // arrival, (hostMs, (x, yaw))
        var main = new List<KeyValuePair<double, float>>();                                              // arrival, yaw (ordered)
        double frame = 1000.0 / 60.0, lastMainDue = 0; int lastSeq = 0;
        float directX = 0f, directYaw = 90f, streamYaw = 90f, shownX = 0f, shownYaw = 90f, prevX = 0f;
        int moving = 0, back = 0; double off = 0, run = 0, longest = 0;
        for (double t = 0; t < 60000; t += frame)
        {
            bool outage = t >= outageFromMs && t < outageFromMs + outageMs;
            if (Math.Floor(t / 100) != Math.Floor((t - frame) / 100) || t == 0)
            {
                if (!outage && r.Next(100) >= lossPct) side.Add(new KeyValuePair<double, KeyValuePair<double, KeyValuePair<float, float>>>(t + 40 + r.Next(jitterMs + 1), new KeyValuePair<double, KeyValuePair<float, float>>(t, new KeyValuePair<float, float>(TruthX(t), TruthYaw(t)))));
            }
            if (Math.Floor(t / 166.7) != Math.Floor((t - frame) / 166.7))
            {
                // reliable ordered stream: a lost packet is retransmitted (~+200 ms) and holds everything behind it; outage holds all
                double due = t + 40 + mainExtraMs + r.Next(jitterMs + 1) + (r.Next(100) < lossPct ? 200 : 0);
                if (outage) due = Math.Max(due, outageFromMs + outageMs + 40);
                if (due < lastMainDue) due = lastMainDue; lastMainDue = due;
                main.Add(new KeyValuePair<double, float>(due, TruthYaw(t)));
            }
            while (main.Count > 0 && main[0].Key <= t) { streamYaw = main[0].Value; main.RemoveAt(0); }
            for (int i = side.Count - 1; i >= 0; i--)
                if (side[i].Key <= t)
                {
                    double hostMs = side[i].Value.Key; float x = side[i].Value.Value.Key, yaw = side[i].Value.Value.Value; side.RemoveAt(i);
                    int s = (int)(hostMs / 100) + 1; int gap = s - lastSeq; if (gap > 0) lastSeq = s;
                    clock.OnPacket(t, hostMs, gap > 0 ? gap : 0);
                    if (gap > 0) { directX = x; if (newFormat) directYaw = yaw; }
                    h.Add(hostMs, new V3(x, 0, 0), Q4.FromYawDeg(yaw), newFormat);
                }
            float targetX; float targetYaw = newFormat ? directYaw : streamYaw;
            if (buffered)
            {
                double rt = clock.RenderHostMs(t, frame); SampleState st;
                if (double.IsNaN(rt)) targetX = directX;
                else { targetX = cu.Step(h.Sample(rt, 1200, out st), t).X; Q4 q; if (newFormat && h.SampleRotation(rt, out q)) targetYaw = q.YawDeg(); }
            }
            else targetX = directX;
            // the game: lerp toward the targets at rate 9/s
            float k = (float)Math.Min(1.0, 9.0 * frame / 1000.0);
            prevX = shownX; shownX += (targetX - shownX) * k;
            Q4 sq = Q4.Nlerp(Q4.FromYawDeg(shownYaw), Q4.FromYawDeg(targetYaw), k); shownYaw = sq.YawDeg();
            if (t < 2000) continue;
            float vx = (float)((shownX - prevX) / (frame / 1000.0));
            if (Math.Abs(vx) > 0.5f)
            {
                moving++; float walkYaw = vx > 0 ? 90f : -90f; float d = Q4.AngleDeg(Q4.FromYawDeg(shownYaw), Q4.FromYawDeg(walkYaw)); off += d;
                if (d > 90f) { back++; run += frame; if (run > longest) longest = run; } else run = 0;
            }
            else run = 0;
        }
        Result res = new Result(); res.BackPct = moving == 0 ? 0 : 100.0 * back / moving; res.OffDeg = moving == 0 ? 0 : off / moving; res.LongestMs = longest; return res;
    }

    public static int Main()
    {
        // packing: every yaw and some tilted rotations survive within 0.2 deg
        float worst = 0f;
        for (int yaw = -180; yaw <= 180; yaw += 3) { Q4 q = Q4.FromYawDeg(yaw); float a = Q4.AngleDeg(q, RotPack.Unpack(RotPack.Pack(q))); if (a > worst) worst = a; }
        Random rr = new Random(7);
        for (int i = 0; i < 2000; i++) { Q4 q = new Q4((float)rr.NextDouble() * 2 - 1, (float)rr.NextDouble() * 2 - 1, (float)rr.NextDouble() * 2 - 1, (float)rr.NextDouble() * 2 - 1).Normalized(); float a = Q4.AngleDeg(q, RotPack.Unpack(RotPack.Pack(q))); if (a > worst) worst = a; }
        Check(worst < 0.25f, "pack precision " + worst);
        Check(Q4.AngleDeg(Q4.Identity, RotPack.Unpack(RotPack.Pack(new Q4(0, 0, 0, -1)))) < 0.25f, "-q == q");
        Check(Math.Abs(Q4.FromYawDeg(90).YawDeg() - 90f) < 0.01f && Math.Abs(Q4.FromYawDeg(-135).YawDeg() + 135f) < 0.01f, "yaw roundtrip");
        Check(Math.Abs(Q4.Nlerp(Q4.FromYawDeg(170), Q4.FromYawDeg(-170), 0.5f).YawDeg()) > 179f, "nlerp short path through 180");

        // packet layout and compatibility
        Check(PosWire.Header + PosWire.MaxEntries * PosWire.EntryPosRot <= 1200, "rotation packet fits 1200 bytes");
        byte[] b = new byte[PosWire.Header + 3 * PosWire.EntryPosRot]; b[0] = PosWire.MagicPosRot; b[1] = 3;
        Check(PosWire.Valid(b, b.Length) && !PosWire.Valid(b, b.Length - 1), "B8 length check");
        b[0] = PosWire.MagicPos; Check(PosWire.Valid(b, PosWire.Header + 3 * 16) && !PosWire.Valid(b, PosWire.Header + 3 * 16 - 1), "B7 length check");
        b[0] = 0x11; Check(!PosWire.Valid(b, b.Length), "unknown magic rejected");
        Check(PosWire.PeerWantsRotation("0.3.83", IsNewer) && !PosWire.PeerWantsRotation("0.3.82", IsNewer) && !PosWire.PeerWantsRotation("0.3.60", IsNewer) && !PosWire.PeerWantsRotation(null, IsNewer), "rotation only to 0.3.83+");
        // a 0.3.82 guest only knows 0xB7 and 16-byte entries: it must never get 0xB8 (checked above); a 0.3.83 guest reads both

        // sequence: late, lost, host restart
        SideSeq q2 = new SideSeq();
        Check(q2.Next(100) == 1 && q2.Next(101) == 1 && q2.Next(104) == 3 && q2.Next(102) == -2 && q2.Last == 104, "seq gap/late");
        int g = 0; for (uint i = 1; i <= 8; i++) g = q2.Next(i); Check(q2.Restarts == 1 && g == 1 && q2.Next(9) == 1 && q2.Next(10) == 1, "host restarted: follow new numbers after 8 older packets in a row");
        SideSeq q3 = new SideSeq(); q3.Next(50000); Check(q3.Next(3) == 1 && q3.Restarts == 1, "host restarted after a long game: at once");
        q2.Reset(); Check(q2.Next(5000) == 1, "reset after reconnect");

        // stale stream write vs fresh side value
        Check(RotationRules.SideWins(10.0f, 9.8f, true, 30f) && !RotationRules.SideWins(10.0f, 9.4f, true, 30f) && !RotationRules.SideWins(10f, 9.9f, false, 30f) && !RotationRules.SideWins(10f, 9.9f, true, 0f), "side wins only while fresh");

        // history: rotation sampled at the same render time as the position, stale ignored, teleport keeps working
        UnitHistory h = new UnitHistory();
        h.Add(1000, new V3(0, 0, 0), Q4.FromYawDeg(0), true); h.Add(1100, new V3(1, 0, 0), Q4.FromYawDeg(90), true);
        Q4 s; Check(h.SampleRotation(1050, out s) && Math.Abs(s.YawDeg() - 45f) < 0.5f, "rotation interpolated");
        Check(h.SampleRotation(5000, out s) && Math.Abs(s.YawDeg() - 90f) < 0.5f, "rotation held after newest");
        Check(!h.Add(1100, new V3(9, 0, 0), Q4.FromYawDeg(180), true) && h.SampleRotation(1100, out s) && Math.Abs(s.YawDeg() - 90f) < 0.5f, "stale rotation never overwrites newer");
        h.Unchanged(1200); Check(h.SampleRotation(1200, out s) && Math.Abs(s.YawDeg() - 90f) < 0.5f, "unchanged tick keeps rotation");
        UnitHistory old = new UnitHistory(); old.Add(1000, new V3(0, 0, 0)); Check(!old.SampleRotation(1000, out s), "no rotation from 0.3.82 host");
        old.Add(1100, new V3(1, 0, 0), Q4.FromYawDeg(90), true); old.Add(1200, new V3(2, 0, 0)); Check(!old.SampleRotation(1150, out s), "rotation switched off on the host: stale rotation not reused");

        // simulator
        var conds = new[] {
            new { L = "clean", J = 0, Ls = 0, M = 0, OF = -1, OM = 0 },
            new { L = "jitter150", J = 150, Ls = 0, M = 0, OF = -1, OM = 0 },
            new { L = "loss15", J = 0, Ls = 15, M = 0, OF = -1, OM = 0 },
            new { L = "main+400ms", J = 0, Ls = 0, M = 400, OF = -1, OM = 0 },
            new { L = "main+1000ms", J = 0, Ls = 0, M = 1000, OF = -1, OM = 0 },
            new { L = "jit150+loss10+main300", J = 150, Ls = 10, M = 300, OF = -1, OM = 0 },
            new { L = "outage3s", J = 0, Ls = 0, M = 0, OF = 20000, OM = 3000 },
        };
        Console.WriteLine("condition | smoothing | 0.3.82 backwards%/offDeg/longestMs | 0.3.83 backwards%/offDeg/longestMs");
        foreach (var c in conds)
            foreach (bool buf in new[] { false, true })
            {
                Result a = Simulate(false, buf, c.J, c.Ls, c.M, c.OF, c.OM, 11), n = Simulate(true, buf, c.J, c.Ls, c.M, c.OF, c.OM, 11);
                Console.WriteLine(c.L + " | " + (buf ? "auto" : "off") + " | " + a.BackPct.ToString("F1") + "/" + a.OffDeg.ToString("F1") + "/" + a.LongestMs.ToString("F0") + " | " + n.BackPct.ToString("F1") + "/" + n.OffDeg.ToString("F1") + "/" + n.LongestMs.ToString("F0"));
                if (c.M >= 400) Check(n.BackPct < a.BackPct, c.L + " " + buf + ": new format walks backwards less");
                Check(n.BackPct <= a.BackPct + 0.5, c.L + " " + buf + ": new format is not worse");
            }
        Console.WriteLine(failed == 0 ? "RotationTests PASS" : "RotationTests FAILED " + failed);
        return failed == 0 ? 0 : 1;
    }
}
