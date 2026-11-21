using System;
using CastleStorySteam;

// 0.4.19 JOIN-MENUS: the decision only (JoinMenuRules). No Unity.
// Build: csc JoinMenuTests.cs JoinMenuRules.cs
static class JoinMenuTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    static int Main()
    {
        try
        {
            Check(JoinMenuRules.Step(true, true) == JoinMenuStep.Reconnect, "live menu: Disconnect + Connect (one fresh set of subscriptions)");
            Check(JoinMenuRules.Step(true, false) == JoinMenuStep.Toggle, "live menu without the private methods: the 0.3.8 enabled toggle");
            Check(JoinMenuRules.Step(false, true) == JoinMenuStep.TempConnect, "inactive menu: connected for this signal only");
            Check(JoinMenuRules.Step(false, false) == JoinMenuStep.Skip, "inactive menu without the private methods: nothing (a toggle would not subscribe it)");
            Check(!JoinMenuRules.ReconnectAfter(false), "still inactive after the signal: stays disconnected (its OnEnable connects it later, no double)");
            Check(JoinMenuRules.ReconnectAfter(true), "became live meanwhile: one fresh set again after the cleanup");
            Check(JoinMenuRules.WantDiag(1, 3) && JoinMenuRules.WantDiag(10, 3), "menu list on the first 10 joins");
            Check(!JoinMenuRules.WantDiag(11, 1) && JoinMenuRules.WantDiag(11, 0) && JoinMenuRules.WantDiag(500, 0), "later only when nothing was reconnected");
            int kinds = 0;
            foreach (bool live in new bool[] { false, true }) foreach (bool r in new bool[] { false, true }) if (JoinMenuRules.Step(live, r) != JoinMenuStep.Skip) kinds++;
            Check(kinds == 3, "only an inactive menu without reflection is skipped");
            Console.WriteLine("JoinMenuTests: " + checks + " checks PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("JoinMenuTests FAIL after " + checks + " checks: " + e.Message); return 1; }
    }
}
