using System;
using CastleStorySteam;

// WatchdogRule (0.4.15 reliable-echo watchdog): the decision only. No Unity, no Steam. Build: csc WatchdogTests.cs WatchdogRules.cs
static class WatchdogTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    const WatchdogRule.Verdict F = WatchdogRule.Verdict.Fire, H = WatchdogRule.Verdict.Healthy, D = WatchdogRule.Verdict.Debounce, O = WatchdogRule.Verdict.HoldOff, B = WatchdogRule.Verdict.Budget;
    const double Never = 1e6;   // "no long frame of ours"

    // a stalled reliable stream: reliable silent relAge, unreliable alive 1 s, no game data for relAge, own frames fine
    static WatchdogRule.Verdict Stuck(WatchdogRule r, double now, double relAge)
    { return r.Evaluate(now, relAge, true, 600.0, 1.0, relAge, Never); }

    static void Main()
    {
        // ---- the 2026-11-06 22:47 picture: reliable NO ANSWER, unreliable alive, rx = 0
        WatchdogRule r = new WatchdogRule(8, 30, 3);
        Check(Stuck(r, 100, 3.8) == H, "3.8 s is below the 8 s limit");
        Check(Stuck(r, 101, 7.8) == H, "7.8 s still below");
        Check(Stuck(r, 102, 8.2) == D, "first check over the limit only debounces");
        Check(Stuck(r, 103, 9.2) == F, "second check fires");
        Check(r.Fired == 1, "one close counted");
        // the same old silence cannot fire again at once: ages count from the close
        Check(Stuck(r, 104, 10.2) == H, "ages restart at the close (1 s since the close)");
        Check(Stuck(r, 120, 26.0) == D, "17 s after the close the capped age (17 s) is over the limit: the debounce starts again");
        Check(Stuck(r, 121, 27.0) == O, "second check inside the 30 s hold-off");
        Check(Stuck(r, 132, 38.0) == O, "29 s after the close: still held off");
        Check(Stuck(r, 133, 39.0) == F, "30 s after the close it fires again (the stream is still dead)");

        // ---- the path is dead for everything: unreliable silent too (host freeze / dead host / wifi gone): never fires
        WatchdogRule d = new WatchdogRule(8, 30, 3);
        for (int i = 0; i < 6; i++) Check(d.Evaluate(100 + i, 20.0, true, 600.0, 20.0, 20.0, Never) == H, "unreliable silent -> healthy verdict (no close)");
        Check(d.Evaluate(110, 20.0, true, 600.0, -1.0, 20.0, Never) == H, "no unreliable answer ever -> no close");
        Check(d.Fired == 0, "nothing fired");

        // ---- the unreliable answer must be fresh (6 s)
        WatchdogRule u = new WatchdogRule(8, 30, 3);
        Check(u.Evaluate(100, 12, true, 600, 6.0, 12, Never) == D, "unreliable age 6.0 is alive");
        Check(u.Evaluate(101, 13, true, 600, 6.5, 13, Never) == H, "unreliable age 6.5 is not");

        // ---- game data still flowing: reliable answers are slow (a big burst ahead of the probe) but the stream moves
        WatchdogRule f = new WatchdogRule(8, 30, 3);
        for (int i = 0; i < 6; i++) Check(f.Evaluate(100 + i, 15.0, true, 600.0, 1.0, 0.4, Never) == H, "data flowing -> no close");

        // ---- our own freeze: every age looks old for a moment
        WatchdogRule o = new WatchdogRule(8, 30, 3);
        Check(o.Evaluate(100, 14, true, 600, 1, 14, 0.5) == H, "long frame of ours half a second ago -> no close");
        Check(o.Evaluate(101, 15, true, 600, 1, 15, 2.9) == H, "2.9 s after a long frame -> still no close");
        Check(o.Evaluate(102, 16, true, 600, 1, 16, 3.0) == D, "3 s after our long frame the evidence counts");
        Check(o.Evaluate(103, 17, true, 600, 1, 17, 4.0) == F, "and it fires");

        // ---- debounce resets on a healthy check
        WatchdogRule g = new WatchdogRule(8, 30, 3);
        Check(Stuck(g, 100, 9) == D, "debounce 1");
        Check(Stuck(g, 101, 2) == H, "a reliable answer arrived");
        Check(Stuck(g, 102, 11) == D, "streak restarted");
        Check(Stuck(g, 103, 12) == F, "fires after two in a row again");

        // ---- never answered reliably (the lobby where Ready could not be pressed): 20 s grace after the first probe
        WatchdogRule n = new WatchdogRule(8, 30, 3);
        Check(n.Evaluate(100, 9, false, 9, 1, 9, Never) == H, "9 s after the first probe: handshake may still run");
        Check(n.Evaluate(101, 19.5, false, 19.5, 1, 19.5, Never) == H, "19.5 s: still inside the grace");
        Check(n.Evaluate(102, 20.5, false, 20.5, 1, 20.5, Never) == D, "20.5 s: counts");
        Check(n.Evaluate(103, 21.5, false, 21.5, 1, 21.5, Never) == F, "fires");

        // ---- budget: at most 3 per 300 s
        WatchdogRule b = new WatchdogRule(8, 30, 3);
        double t = 1000; int fires = 0, early = 0, budget = 0;
        for (int i = 0; i < 400; i++) { WatchdogRule.Verdict v = b.Evaluate(t, 1e5, true, 1e5, 1.0, 1e5, Never); if (v == F) { fires++; if (t < 1300) early++; } if (v == B) budget++; t += 1.0; }
        Check(early == 3 && budget > 0 && fires == 6, "dead stream for 400 s: 3 closes in the first 300 s (hold-off 30 s), then refused, then allowed again as the old ones age out (early=" + early + " fires=" + fires + ")");
        WatchdogRule c = new WatchdogRule(8, 30, 3);
        t = 1000; fires = 0;
        for (int i = 0; i < 250; i++) { if (c.Evaluate(t, 1e5, true, 1e5, 1.0, 1e5, Never) == F) fires++; t += 1.0; }
        Check(fires == 3 && c.Evaluate(t, 1e5, true, 1e5, 1.0, 1e5, Never) == B, "250 s: exactly 3 closes, the 4th is refused by the budget");

        // ---- parameters are clamped
        WatchdogRule x = new WatchdogRule(0, 0, 0);
        Check(x.Evaluate(100, 3.9, true, 600, 1, 3.9, Never) == H && x.Evaluate(101, 4.1, true, 600, 1, 4.1, Never) == D, "silence limit clamped to 4 s");
        Check(x.Evaluate(102, 5.1, true, 600, 1, 5.1, Never) == F && x.Evaluate(103, 6.1, true, 600, 1, 6.1, Never) == H, "max per window clamped to 1 and the ages restart at the close");

        // ---- the failure callback belongs to us only shortly after our own close
        Check(WatchdogRule.FailureIsOurs(110, 100, 25) && WatchdogRule.FailureIsOurs(125, 100, 25), "inside the grace");
        Check(!WatchdogRule.FailureIsOurs(125.5, 100, 25) && !WatchdogRule.FailureIsOurs(110, -1e9, 25) && !WatchdogRule.FailureIsOurs(90, 100, 25), "outside the grace / never closed / clock before the close");

        // ---- mode: 0 off, 1 guest end only, 2 both ends
        Check(WatchdogRule.NormalizeMode(-5) == 0 && WatchdogRule.NormalizeMode(1) == 1 && WatchdogRule.NormalizeMode(7) == 2, "mode clamp");
        Check(!WatchdogRule.MayAct(0, false) && !WatchdogRule.MayAct(0, true), "mode 0 never acts");
        Check(WatchdogRule.MayAct(1, false) && !WatchdogRule.MayAct(1, true), "mode 1 acts as a guest only");
        Check(WatchdogRule.MayAct(2, false) && WatchdogRule.MayAct(2, true), "mode 2 acts on both ends");

        Console.WriteLine("ALL PASS (" + checks + " checks)");
    }
}
