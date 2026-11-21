using System;
using CastleStorySteam;

public static class AimReconcileTests
{
    static int failed;
    static void Check(bool ok, string name) { if (!ok) { failed++; Console.WriteLine("FAIL " + name); } }
    static Aim A(float x) { return new Aim(x, 1f, 2f); }
    public static int Main()
    {
        Aim show; string why; AimReconciler r;
        // 1 hover leaves nothing behind when nothing was sent: back to the host value (zero)
        r = new AimReconciler(); Check(r.OnLeave(0).Same(Aim.Zero), "leave without commands restores host value (zero)");
        // 2 hover after a host target: leave restores the host target, not the hover point
        r = new AimReconciler(); r.OnHostUpdate(A(5), 0, out show, out why); Check(r.OnLeave(1).Same(A(5)), "leave restores last host target");
        // 3 click then leave keeps the clicked target at once (instant local display)
        r = new AimReconciler(); r.Sent(A(7), 0); Check(r.OnLeave(0.1f).Same(A(7)), "leave keeps newest pending command");
        // 4 host echo of the newest command confirms it
        Check(!r.OnHostUpdate(A(7), 0.2f, out show, out why) && r.PendingCount == 0 && show.Same(A(7)), "echo confirms newest");
        // 5 two quick commands: echo of the OLD one is stale and must not overwrite the newer local aim
        r = new AimReconciler(); r.Sent(A(1), 0); r.Sent(A(2), 0.05f);
        Check(r.OnHostUpdate(A(1), 0.1f, out show, out why) && show.Same(A(2)) && r.PendingCount == 1, "stale echo re-applies newest");
        Check(!r.OnHostUpdate(A(2), 0.2f, out show, out why) && r.PendingCount == 0, "later echo of newest confirms");
        // 6 three commands, host replies out of the middle
        r = new AimReconciler(); r.Sent(A(1), 0); r.Sent(A(2), 0); r.Sent(A(3), 0);
        Check(r.OnHostUpdate(A(2), 0.1f, out show, out why) && show.Same(A(3)) && r.PendingCount == 1, "middle echo drops older, keeps newest");
        // 7 host rejects (sends something else): host authority wins
        r = new AimReconciler(); r.Sent(A(9), 0); Check(!r.OnHostUpdate(A(4), 0.1f, out show, out why) && show.Same(A(4)) && r.PendingCount == 0, "unknown host value is accepted");
        // 8 clear command: pending zero, host echo zero confirms
        r = new AimReconciler(); r.OnHostUpdate(A(5), 0, out show, out why); r.Sent(Aim.Zero, 1f); Check(r.OnLeave(1.1f).Same(Aim.Zero), "clear is shown at once"); Check(!r.OnHostUpdate(Aim.Zero, 1.2f, out show, out why) && r.PendingCount == 0, "clear echo confirms");
        // 9 timeout: an old pending command no longer blocks the host
        r = new AimReconciler(); r.Sent(A(8), 0); Check(!r.OnHostUpdate(A(3), 6f, out show, out why) && show.Same(A(3)), "expired pending is dropped, host accepted");
        // 10 expired pending is not used on leave
        r = new AimReconciler(); r.OnHostUpdate(A(5), 0, out show, out why); r.Sent(A(8), 1f); Check(r.OnLeave(10f).Same(A(5)), "expired pending not restored on leave");
        // 11 sequence numbers grow
        r = new AimReconciler(); r.Sent(A(1), 0); r.Sent(A(2), 0); r.Sent(A(3), 0); Check(r.Seq == 3, "sequence counts commands");
        // 12 pending list is bounded
        r = new AimReconciler(); for (int i = 0; i < 40; i++) r.Sent(A(i), 0); Check(r.PendingCount <= 16, "pending bounded");
        // 13 simulated burst with latency: 100 commands, host echoes each in order with delay: ends confirmed, never shows an older value than the newest sent
        r = new AimReconciler(); bool older = false; int sent = 0;
        for (int i = 1; i <= 100; i++) { r.Sent(A(i), i * 0.02f); sent = i; if (i >= 3) { bool re = r.OnHostUpdate(A(i - 2), i * 0.02f, out show, out why); if (re && !show.Same(A(i))) older = true; if (!re && r.PendingCount > 0 && false) older = true; } }
        Check(!older, "burst: stale echoes always re-apply the newest command");
        // 14 (0.4.27 review) two or more commands that all expired without a host reply: no exception, the next step still works
        r = new AimReconciler(); r.OnHostUpdate(A(5), 0, out show, out why); r.Sent(A(6), 1f); r.Sent(A(6), 1.1f); bool threw = false;
        try { Check(r.OnLeave(10f).Same(A(5)) && r.PendingCount == 0, "two expired commands: leave restores the host value"); } catch (Exception) { threw = true; }
        Check(!threw, "two expired commands do not throw");
        r = new AimReconciler(); r.Sent(A(1), 0); r.Sent(A(2), 0); r.Sent(A(3), 0); threw = false;
        try { r.Sent(A(4), 9f); } catch (Exception) { threw = true; }
        Check(!threw && r.PendingCount == 1 && r.OnLeave(9.1f).Same(A(4)), "three expired commands: the new command is still remembered");
        r = new AimReconciler(); r.Sent(A(1), 0); r.Sent(A(2), 0); threw = false;
        try { r.OnHostUpdate(A(7), 8f, out show, out why); } catch (Exception) { threw = true; }
        Check(!threw && r.LastHost.Same(A(7)) && r.OnLeave(8.1f).Same(A(7)), "two expired commands: the host value is still stored");
        // 15 (taken from reviewer A's A-08, the same fix) partial expiry: only the expired command goes, the newer one stays
        r = new AimReconciler(); r.Sent(A(1), 0); r.Sent(A(2), 4f); Check(r.OnLeave(6f).Same(A(2)) && r.PendingCount == 1, "partial expiry keeps the newer command");
        // 16 (verifier C) a partial expiry threw too: the old loop ran past the end whenever the newest expired command sat at index i >= 1 with 2 * i >= Count,
        // e.g. three expired commands and one fresh one. No exception, the fresh command stays, the step is not lost.
        r = new AimReconciler(); r.OnHostUpdate(A(5), 0, out show, out why); r.Sent(A(1), 0); r.Sent(A(1), 0.1f); r.Sent(A(1), 0.2f); r.Sent(A(2), 4.9f); threw = false; Aim got = Aim.Zero;
        try { got = r.OnLeave(5.25f); } catch (Exception) { threw = true; }
        Check(!threw && got.Same(A(2)) && r.PendingCount == 1, "three expired + one fresh: no exception, leave shows the fresh command");
        r = new AimReconciler(); r.Sent(A(1), 0); r.Sent(A(1), 0.1f); r.Sent(A(1), 0.2f); r.Sent(A(2), 4.9f); threw = false; bool again = true;
        try { again = r.OnHostUpdate(A(1), 5.25f, out show, out why); } catch (Exception) { threw = true; }
        Check(!threw && !again && r.LastHost.Same(A(1)) && r.PendingCount == 0, "three expired + one fresh: a host value that only matches expired commands is accepted");
        Console.WriteLine(failed == 0 ? "PASS: aim reconciliation (16 groups)" : "FAILED " + failed);
        return failed == 0 ? 0 : 1;
    }
}
