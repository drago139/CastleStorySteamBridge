using System;
using CastleStorySteam;
class RadminTimeoutRulesTests
{
    static int count;
    static void Check(string name, bool actual, bool expected)
    {
        count++;
        if (actual != expected) throw new Exception("radmin timeout mismatch: " + name + " (got " + actual + ")");
    }
    static void Main()
    {
        // typed into the F8 Radmin join box: long by default, the tick switches to the game's own value
        Check("typed mod host", RadminTimeoutRules.UseLong("26.1.2.3", "26.1.2.3", false, ""), true);
        Check("typed vanilla host", RadminTimeoutRules.UseLong("26.1.2.3", "26.1.2.3", true, ""), false);
        Check("typed vanilla host wins over the cfg list", RadminTimeoutRules.UseLong("26.1.2.3", "26.1.2.3", true, "26.1.2.3"), false);
        // review 2026-11-09: the box decides for any typed IPv4 (plain LAN, Hamachi 25.x, ZeroTier), not only 26.x
        Check("typed LAN mod host", RadminTimeoutRules.UseLong("192.168.0.5", "192.168.0.5", false, ""), true);
        Check("typed LAN vanilla host", RadminTimeoutRules.UseLong("192.168.0.5", "192.168.0.5", true, ""), false);
        // the compare is exact: RadminPath canonicalises the typed text before arming it (the game passes RakNet's print of the address)
        Check("non-canonical typed text does not match", RadminTimeoutRules.UseLong("26.1.2.3", "26.001.002.003", false, ""), false);
        // another 26.x address (the game's own LAN list): only hosts in the cfg list
        Check("other host not listed", RadminTimeoutRules.UseLong("26.9.9.9", "26.1.2.3", false, ""), false);
        Check("other host listed", RadminTimeoutRules.UseLong("26.9.9.9", "26.1.2.3", false, "26.9.9.9"), true);
        Check("listed with a port", RadminTimeoutRules.UseLong("26.9.9.9", null, false, "26.9.9.9:3000"), true);
        Check("listed among several", RadminTimeoutRules.UseLong("26.9.9.9", null, false, "26.1.1.1, 26.9.9.9;26.2.2.2"), true);
        Check("a longer address is not a match", RadminTimeoutRules.UseLong("26.9.9.9", null, false, "26.9.9.99"), false);
        // the cfg list is for Radmin (26.x) addresses only; loopback is the bridge and is decided by the caller
        Check("LAN address listed, not typed", RadminTimeoutRules.UseLong("192.168.0.5", null, false, "192.168.0.5"), false);
        Check("loopback listed, not typed", RadminTimeoutRules.UseLong("127.0.0.1", null, false, "127.0.0.1"), false);
        Check("null address", RadminTimeoutRules.UseLong(null, "26.1.2.3", false, ""), false);
        Check("no target and no list", RadminTimeoutRules.UseLong("26.1.2.3", null, false, null), false);
        // the typed choice is used by one connect only
        string target = "26.1.2.3"; bool vanilla = false;
        Check("once: the first connect takes the typed choice", RadminTimeoutRules.UseLongOnce("26.1.2.3", ref target, ref vanilla, ""), true);
        Check("once: the choice is cleared", target == null && vanilla == false, true);
        Check("once: the second connect falls back to the cfg list", RadminTimeoutRules.UseLongOnce("26.1.2.3", ref target, ref vanilla, ""), false);
        target = "26.1.2.3"; vanilla = true;
        Check("once: a connect to another address consumes the choice too", RadminTimeoutRules.UseLongOnce("26.9.9.9", ref target, ref vanilla, "26.9.9.9"), true);
        Check("once: cleared after the other address", target == null && vanilla == false, true);
        Check("once: the stale vanilla tick no longer blocks a listed host", RadminTimeoutRules.UseLongOnce("26.1.2.3", ref target, ref vanilla, "26.1.2.3"), true);
        Console.WriteLine("PASS " + count + " Radmin timeout rule checks; typed host long by default (any IPv4), vanilla tick, cfg list for 26.x only, choice used once.");
    }
}
