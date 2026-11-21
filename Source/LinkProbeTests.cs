using System;
using CastleStorySteam;

// LinkRules (0.4.5 LINK telemetry): ladder, buckets, address hash, loss share, the late-answer ledger and the alert gate. No Unity, no Steam.
static class LinkProbeTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    static void Main()
    {
        // ladder: the values measured on 2026-11-05 (true unit 4.0 s, sampled 0.2 s low)
        Check(LinkRules.LadderRung(3.8f) == 0 && LinkRules.LadderRung(4.0f) == 0, "rung 0");
        Check(LinkRules.LadderRung(7.8f) == 1 && LinkRules.LadderRung(15.8f) == 2 && LinkRules.LadderRung(31.2f) == 3 && LinkRules.LadderRung(31.8f) == 3 && LinkRules.LadderRung(63.8f) == 4, "rungs 1-4");
        Check(LinkRules.LadderRung(127.7f) == 5 && LinkRules.LadderRung(255.5f) == 6, "rungs 5-6");
        Check(LinkRules.LadderRung(5.7f) == -1 && LinkRules.LadderRung(2.0f) == -1 && LinkRules.LadderRung(11.0f) == -1 && LinkRules.LadderRung(95.9f) == -1 && LinkRules.LadderRung(0f) == -1, "off the ladder");
        // age buckets (one alert per doubling)
        Check(LinkRules.AgeBucket(3.9f) == -1 && LinkRules.AgeBucket(4.0f) == 0 && LinkRules.AgeBucket(7.99f) == 0 && LinkRules.AgeBucket(8f) == 1 && LinkRules.AgeBucket(16f) == 2 && LinkRules.AgeBucket(63.9f) == 3 && LinkRules.AgeBucket(64f) == 4, "age buckets");
        Check(LinkRules.AgeBucket(100000f) == 12 && LinkRules.AgeBucket(-5f) == -1, "bucket clamp");
        // address hash
        string a = LinkRules.AddrHash(0x7F000001u, 27015), b = LinkRules.AddrHash(0x7F000001u, 27016), c = LinkRules.AddrHash(0x7F000002u, 27015);
        Check(a.Length == 6 && a == LinkRules.AddrHash(0x7F000001u, 27015), "hash stable, 6 chars");
        Check(a != b && a != c && b != c, "port or ip change changes the hash");
        Check(LinkRules.AddrHash(0, 0).Length == 6, "hash of zeros");
        // loss share
        Check(Math.Abs(LinkRules.LossPct(41, 59) - 59f) < 0.001f && LinkRules.LossPct(0, 0) == -1f && LinkRules.LossPct(0, 3) == 100f && LinkRules.LossPct(5, 0) == 0f && LinkRules.LossPct(-1, 2) == -1f, "loss pct");
        // ledger: tick = 1 ms for the test
        LinkRules.Ledger l = new LinkRules.Ledger(5, 600000.0, 1.0);
        Check(l.Count == 0 && l.OldestAgeMs(0, 100) == -1, "empty ledger");
        l.Expired((10u << 1) | 0u, 1000, 9000);      // reliable probe #10 sent at 1000, forgotten at 9000
        l.Expired((11u << 1) | 1u, 3000, 9000);      // unreliable probe #11
        Check(l.Count == 2, "two entries");
        Check(Math.Abs(l.OldestAgeMs(0, 17000) - 16000.0) < 0.001 && Math.Abs(l.OldestAgeMs(1, 17000) - 14000.0) < 0.001, "oldest age per mode");
        double rtt;
        Check(!l.Answer((10u << 1) | 0u, 999, 17000, out rtt), "wrong send time is not accepted");
        Check(l.Answer((10u << 1) | 0u, 1000, 16800, out rtt) && Math.Abs(rtt - 15800.0) < 0.001, "late answer gives the true rtt 15.8 s");
        Check(l.LateCount == 1 && Math.Abs(l.LateMaxMs - 15800.0) < 0.001 && l.LateLastMode == 0, "late stats");
        Check(!l.Answer((10u << 1) | 0u, 1000, 17000, out rtt), "duplicate answer ignored");
        Check(l.Count == 1 && l.OldestAgeMs(0, 17000) == -1, "answered entry removed");
        // answers of a second, shorter delay do not lower the max
        l.Expired((12u << 1) | 0u, 20000, 29000);
        Check(l.Answer((12u << 1) | 0u, 20000, 24000, out rtt) && Math.Abs(l.LateMaxMs - 15800.0) < 0.001 && Math.Abs(l.LateLastMs - 4000.0) < 0.001, "max kept, last updated");
        // prune by age (keep 600 s) and by cap (5)
        LinkRules.Ledger p = new LinkRules.Ledger(5, 600000.0, 1.0);
        p.Expired(2u, 0, 10);
        p.Expired(4u, 650000, 650100);               // the first entry is older than 600 s now
        Check(p.Count == 1 && p.OldestAgeMs(0, 650200) > 0, "old entry pruned");
        LinkRules.Ledger q = new LinkRules.Ledger(3, 600000.0, 1.0);
        for (uint i = 1; i <= 6; i++) q.Expired(i << 1, i * 1000, i * 1000 + 10);
        Check(q.Count == 3 && q.OldestAgeMs(0, 7000) == 7000 - 4000, "cap keeps the newest three");
        // server-list join: owner unknown (0) is accepted only for a friend host with a fresh heartbeat
        Check(LinkRules.LobbyHostAccepted(777, 777, false, false) && LinkRules.LobbyHostAccepted(777, 777, true, true), "owner = host is always accepted");
        Check(LinkRules.LobbyHostAccepted(0, 777, true, true), "owner unknown + fresh heartbeat + friend = accepted (the 2026-11-05 case)");
        Check(!LinkRules.LobbyHostAccepted(0, 777, true, false), "owner unknown + stranger = refused");
        Check(!LinkRules.LobbyHostAccepted(0, 777, false, true), "owner unknown + no fresh heartbeat = refused");
        Check(!LinkRules.LobbyHostAccepted(555, 777, true, true), "a different owner = refused");
        Check(!LinkRules.LobbyHostAccepted(0, 0, true, true) && !LinkRules.LobbyHostAccepted(0, 0, false, false), "host id 0 = refused");
        // alert gate
        LinkRules.AlertGate g = new LinkRules.AlertGate();
        Check(g.Allow("a", 100, 10) && !g.Allow("a", 105, 10) && g.Allow("a", 110.5, 10) && g.Allow("b", 105, 10), "gate per key and interval");
        for (int i = 0; i < 1000; i++) g.Allow("k" + i, 1000 + i, 5);
        Check(g.Allow("a", 5000, 10), "gate stays bounded and usable");
        Console.WriteLine("PASS: " + checks + " link rule checks (silence ladder 4*2^n, age buckets, address hash, loss share, late-answer ledger, alert gate).");
    }
}
