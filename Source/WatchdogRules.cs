using System;
using System.Collections.Generic;

namespace CastleStorySteam
{
    // 0.4.15 reliable-echo WATCHDOG: the decision, with no Unity and no Steam, so WatchdogTests can drive it.
    // Evidence (2026-11-06 22:47, guest, 74 s stall): the reliable probe got NO ANSWER for 88 s while the unreliable probe still answered, game data
    // (channel 77, reliable) rx = 0 for 10 -> 46 s, the guest's own frames were short. That is the signature of ONE stuck reliable stream
    // (a retransmission timer that doubles: 3.85 s x 2^n), not of a dead host (a dead host or a host freeze silences both probes) and not of our own
    // freeze (our frames are long). The fix that is tried: close the Steam P2P session with that peer; the next send opens a new one.
    //
    // The rule fires only when ALL of these hold on two consecutive 1 s checks:
    //   1. the last reliable answer is at least SilenceSeconds old (a peer that NEVER answered reliably: at least max(SilenceSeconds, NeverAnsweredGraceSeconds) after the first probe);
    //   2. the last unreliable answer is at most UnrAliveSeconds old (the peer's main loop runs and the path carries packets);
    //   3. no game data (channel 77) arrived for at least SilenceSeconds;
    //   4. no frame of OUR OWN main loop was 1 s or longer in the last QuietFrameSeconds (our own freeze makes every age look old);
    // and it respects a hold-off after a close (HoldOffSeconds) and a budget (MaxPerWindow closes per WindowSeconds). After a close the ages are
    // counted from the close, so the same old silence can never fire twice.
    public sealed class WatchdogRule
    {
        public const double DefaultSilenceSeconds = 8.0, DefaultHoldOffSeconds = 30.0, WindowSeconds = 300.0;
        public const int DefaultMaxPerWindow = 3;
        public const double UnrAliveSeconds = 6.0, NeverAnsweredGraceSeconds = 20.0, QuietFrameSeconds = 3.0;
        public const int DebounceChecks = 2;

        public enum Verdict { Healthy, Debounce, HoldOff, Budget, Fire }

        readonly double silence, holdOff; readonly int maxPer;
        readonly List<double> closes = new List<double>();
        double lastFire = -1e9; int streak;
        public int Fired { get { return closes.Count; } }

        public WatchdogRule(double silenceSeconds, double holdOffSeconds, int maxPerWindow)
        {
            silence = silenceSeconds < 4.0 ? 4.0 : (silenceSeconds > 120.0 ? 120.0 : silenceSeconds);
            holdOff = holdOffSeconds < 10.0 ? 10.0 : (holdOffSeconds > 600.0 ? 600.0 : holdOffSeconds);
            maxPer = maxPerWindow < 1 ? 1 : (maxPerWindow > 10 ? 10 : maxPerWindow);
        }

        // now: seconds on any steady clock. relAge: age of the last reliable answer (or of the first probe when none ever came), relEverAnswered: at least one came.
        // sessionAge: age of the first probe. unrAge: age of the last unreliable answer, < 0 when none ever came. dataSilence: age of the last game datagram, < 0 = unknown.
        // sinceLongFrame: seconds since the last frame of ours that took 1 s or more (large when never).
        public Verdict Evaluate(double now, double relAge, bool relEverAnswered, double sessionAge, double unrAge, double dataSilence, double sinceLongFrame)
        {
            double sinceFire = now - lastFire;
            if (sinceFire < 1e8) { if (relAge > sinceFire) relAge = sinceFire; if (dataSilence > sinceFire) dataSilence = sinceFire; }   // ages count from the last close
            double needRel = relEverAnswered ? silence : Math.Max(silence, NeverAnsweredGraceSeconds);
            double basis = relEverAnswered ? relAge : Math.Min(relAge, sessionAge);
            bool bad = basis >= needRel
                && unrAge >= 0 && unrAge <= UnrAliveSeconds
                && dataSilence >= silence
                && sinceLongFrame >= QuietFrameSeconds;
            if (!bad) { streak = 0; return Verdict.Healthy; }
            streak++;
            if (streak < DebounceChecks) return Verdict.Debounce;
            if (sinceFire < holdOff) return Verdict.HoldOff;
            for (int i = closes.Count - 1; i >= 0; i--) if (now - closes[i] > WindowSeconds) closes.RemoveAt(i);
            if (closes.Count >= maxPer) return Verdict.Budget;
            closes.Add(now); lastFire = now; streak = 0;
            return Verdict.Fire;
        }

        // seconds since the last close (large when none)
        public double SinceFire(double now) { return now - lastFire; }

        // The Steam failure callback that arrives soon after OUR close belongs to the session we just reopened; it must not end the game session.
        public static bool FailureIsOurs(double now, double lastCloseAt, double graceSeconds)
        {
            return lastCloseAt > -1e8 && now - lastCloseAt >= 0 && now - lastCloseAt <= graceSeconds;
        }

        public static int NormalizeMode(int cfg) { return cfg < 0 ? 0 : (cfg > 2 ? 2 : cfg); }

        // mode 1 = the guest end only, mode 2 = guest and host
        public static bool MayAct(int mode, bool hosting) { return mode >= 2 || (mode == 1 && !hosting); }
    }
}
