using System;
using CastleStorySteam;

public static class HostMarkerTests
{
    static int failed;
    static void Check(bool ok, string name) { if (!ok) { failed++; Console.WriteLine("FAIL " + name); } }
    public static int Main()
    {
        // enabled, server, haveLocal, haveOwner, ownerIsLocal, ownerIsAllied
        Check(HostMarkerRules.ShouldSkip(true, true, true, true, false, false), "foreign order on host is skipped");
        Check(!HostMarkerRules.ShouldSkip(true, true, true, true, true, false), "own order is kept");
        Check(!HostMarkerRules.ShouldSkip(true, true, true, true, false, true), "allied order is kept");
        Check(!HostMarkerRules.ShouldSkip(false, true, true, true, false, false), "filter off keeps everything");
        Check(!HostMarkerRules.ShouldSkip(true, false, true, true, false, false), "client machine is never filtered here");
        Check(!HostMarkerRules.ShouldSkip(true, true, false, true, false, false), "unknown local faction keeps marker");
        Check(!HostMarkerRules.ShouldSkip(true, true, true, false, false, false), "unknown owner keeps marker");
        int combos = 0;
        for (int i = 0; i < 64; i++)
        {
            bool e = (i & 1) != 0, s = (i & 2) != 0, l = (i & 4) != 0, o = (i & 8) != 0, own = (i & 16) != 0, al = (i & 32) != 0;
            bool r = HostMarkerRules.ShouldSkip(e, s, l, o, own, al);
            bool expect = e && s && l && o && !own && !al;
            Check(r == expect, "truth table " + i); combos++;
        }
        Console.WriteLine(failed == 0 ? "PASS: host marker rules (7 cases + " + combos + " truth-table rows)" : "FAILED " + failed);
        return failed == 0 ? 0 : 1;
    }
}
