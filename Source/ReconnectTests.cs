using System;
using CastleStorySteam;

// Drives ReconnectState through whole game lifetimes with a persisted config (two strings, like BepInEx [Network] LastHost/LastHostOpen).
// Covers what the plugin glue in Reconnect.cs does with it; the glue itself (Unity, Steam callbacks) is not exercised here.
static class ReconnectTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    const ulong Self = 76561198000000001UL, HostA = 76561198000000002UL, HostB = 76561198000000003UL;

    // One game process: start reads the config, events write it back the way Reconnect.cs does.
    sealed class Game
    {
        internal string Saved = ""; internal bool SavedOpen;
        internal ReconnectState State; internal bool Prompted;
        internal void Start()
        {
            State = new ReconnectState();
            Prompted = State.Load(Saved, SavedOpen);
            SavedOpen = false;
        }
        internal void Join(ulong host, string name)
        {
            if (!State.Remember(host, name)) return;
            Saved = State.Serialize(); SavedOpen = State.Open;
        }
        internal void CleanEnd() { State.CleanEnd(); SavedOpen = State.Open; }
    }

    public static int Main()
    {
        try
        {
            ulong id; string name;
            Check(!ReconnectState.TryParse(null, out id, out name) && !ReconnectState.TryParse("", out id, out name), "empty");
            Check(ReconnectState.TryParse("76561198000000002;Друг", out id, out name) && id == HostA && name == "Друг", "id;name");
            Check(ReconnectState.TryParse("76561198000000002", out id, out name) && id == HostA && name == "", "id only (hand-edited)");
            Check(ReconnectState.TryParse("76561198000000002;", out id, out name) && id == HostA && name == "", "empty name");
            Check(ReconnectState.TryParse("76561198000000002;a;b", out id, out name) && name == "a b", "separator inside stored name");
            Check(!ReconnectState.TryParse("12345;x", out id, out name) && id == 0, "not a SteamID");
            Check(!ReconnectState.TryParse("0;x", out id, out name), "zero id");
            Check(!ReconnectState.TryParse("abc;x", out id, out name), "garbage");
            Check(!ReconnectState.TryParse(";x", out id, out name), "missing id");
            Check(!ReconnectState.TryParse("99999999999999999999;x", out id, out name), "overflow");
            Check(!ReconnectState.TryParse("-76561198000000002", out id, out name), "negative");
            Check(ReconnectState.CleanName("[unknown]") == "" && ReconnectState.CleanName(null) == "", "unknown persona");
            Check(ReconnectState.CleanName("a\nb\tc;d") == "a b c d", "control chars and separator removed");
            Check(ReconnectState.CleanName("\\n x\\t") == "n x t", "backslash removed: BepInEx config unescapes \\n/\\t on reload");

            ReconnectState s = new ReconnectState();
            Check(!s.Remember(0, "x") && s.HostId == 0 && !s.Open, "zero host not remembered");
            Check(!s.Remember(12345, "x") && s.HostId == 0, "invalid host not remembered");
            Check(s.Remember(HostA, "Ann;X") && s.Serialize() == HostA + ";Ann X" && s.Open, "remembered");
            ReconnectState back = new ReconnectState();
            Check(!back.Load(s.Serialize(), false) && back.HostId == HostA && back.HostName == "Ann X", "serialize round trip");
            Check(back.CanOffer(Self) && !back.CanOffer(HostA), "never offer reconnecting to yourself");
            Check(back.Label("") == "Ann X", "stored name label");
            back.HostName = ""; Check(back.Label("[unknown]") == HostA.ToString() && back.Label("Live") == "Live", "label fallback");

            Game g = new Game();
            g.Start(); Check(!g.Prompted && !g.State.CanOffer(Self), "fresh install: no button, no prompt");
            g.Join(HostA, "Ann"); g.CleanEnd();
            g.Start(); Check(!g.Prompted && g.State.HostId == HostA, "clean exit: button, no crash prompt");
            g.Join(HostA, "Ann");
            g.Start(); Check(g.Prompted && g.State.HostId == HostA, "crash during session: prompt");
            g.Start(); Check(!g.Prompted, "crash prompt shown only once");
            g.Join(HostA, "Ann"); g.CleanEnd(); // host left / Steam failure: StopBridge -> clean end
            g.Start(); Check(!g.Prompted && g.State.HostId == HostA, "host left: no false crash prompt");
            g.Join(HostB, "Bob");
            g.Start(); Check(g.Prompted && g.State.HostId == HostB, "latest host wins");
            g.Saved = "12345;stale"; g.SavedOpen = true;
            g.Start(); Check(!g.Prompted && g.State.HostId == 0 && !g.SavedOpen, "stale/invalid id: no prompt, mark cleared");
            g.Saved = HostA + ";Ann"; g.SavedOpen = true; g.Start(); g.Join(12345, "bad");
            Check(g.Saved == HostA + ";Ann" && !g.SavedOpen, "invalid peer during session does not overwrite or re-arm");

            // Random lifetimes. Invariant: a start prompts iff the previous process remembered a host and never ended cleanly afterwards,
            // and the button always points to the last valid host remembered.
            Random r = new Random(5817);
            for (int run = 0; run < 2000; run++)
            {
                Game h = new Game(); h.Start();
                ulong expectHost = 0; bool expectPrompt = false;
                for (int life = 0; life < 12; life++)
                {
                    bool open = false;
                    int events = r.Next(6);
                    for (int e = 0; e < events; e++)
                    {
                        switch (r.Next(4))
                        {
                            case 0: h.Join(HostA, "A"); expectHost = HostA; open = true; break;
                            case 1: h.Join(HostB, "B"); expectHost = HostB; open = true; break;
                            case 2: h.Join((ulong)r.Next(), "junk"); break;
                            default: h.CleanEnd(); open = false; break;
                        }
                    }
                    bool crash = r.Next(3) == 0;
                    if (!crash) { h.CleanEnd(); open = false; }
                    expectPrompt = open;
                    h.Start();
                    Check(h.Prompted == expectPrompt, "random prompt run " + run + " life " + life);
                    Check(h.State.HostId == expectHost, "random host run " + run + " life " + life);
                    Check(!h.SavedOpen, "open mark consumed at start");
                }
            }
            Console.WriteLine("PASS: " + checks + " reconnect checks (parse, stale ids, self, crash/clean exit/host leave lifetimes, 2000 random lifetimes).");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
    }
}
