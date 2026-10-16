using System;
using System.IO;
using CastleStorySteam;

static class ReliabilityTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    public static int Main()
    {
        try
        {
            PeerReportGate gate = new PeerReportGate();
            Check(gate.Accept(1, 1, 0, 20), "first peer at startup");
            Check(gate.Accept(2, 1, 0, 20), "second simultaneous peer must not be dropped");
            Check(gate.Accept(3, 1, 0, 20), "third simultaneous peer must not be dropped");
            Check(!gate.Accept(1, 1, 1, 20), "same peer flood blocked");
            Check(gate.Accept(1, 2, 1, 2), "previous report independent from auto report");
            Check(gate.Accept(1, 1, 20, 20), "interval boundary");
            gate.Clear(); Check(gate.Accept(1, 1, 0, 20), "new session reset");
            Check(CrystalKeepOut.Contains(0.5f, 0, 0, 0, 0, 0, 1, 2, 0.9f), "drop inside crystal rejected");
            Check(!CrystalKeepOut.Contains(0.5f, 20, 0, 0, 0, 0, 1, 2, 0.9f), "drop far above crystal allowed");
            Check(!CrystalKeepOut.Contains(4, 0, 0, 0, 0, 0, 1, 2, 0.9f), "drop outside radius allowed");
            Check(!CrystalKeepOut.Contains(0, -20, 0, 0, 0, 0, 1, 2, 0.9f), "drop far below crystal allowed");
            PeerTrafficWindow healthy = new PeerTrafficWindow(), stalled = new PeerTrafficWindow();
            long a = 0, b = 0;
            healthy.Observe(a, 0, 15, true); stalled.Observe(b, 0, 15, true);
            for (int i = 0; i < 4; i++) { a += 600; b += 600; healthy.Observe(a, i, 15, true); stalled.Observe(b, i, 15, true); }
            a += 600; b += 5;
            Check(healthy.Observe(a, 5, 15, true) == 0, "healthy peer stays healthy");
            Check(stalled.Observe(b, 5, 15, true) == 1, "one stalled guest detected despite other guest traffic");
            Check(stalled.StallSeconds == 15, "first elapsed stall window");
            b += 10; Check(stalled.Observe(b, 6, 30, true) == 2 && stalled.StallSeconds == 45, "long frame uses elapsed seconds");
            b += 150; Check(stalled.Observe(b, 7, 15, true) == 2, "partial recovery preserves baseline");
            b += 600; Check(stalled.Observe(b, 8, 15, true) == 3, "recovery detected");
            Check(stalled.Observe(0, 0, 15, true) == 0 && stalled.StallWindows == 0, "reconnect counters reset");
            for (int i = 0; i < 6; i++) Check(stalled.Observe(0, 0, 15, false) == 0, "loading must not report replication stall");
            string dir = Path.Combine(Path.GetTempPath(), "SteamBridgeReliability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "bridge.dll");
            byte[] original = new byte[] { 1, 2, 3 }, originalSig = new byte[] { 4, 5 }, newer = new byte[] { 6, 7, 8 }, newerSig = new byte[] { 9, 10 };
            File.WriteAllBytes(target, original); File.WriteAllBytes(target + ".sig", originalSig);
            bool failed = false;
            try
            {
                UpdateFileStore.Apply(target, newer, newerSig, delegate(string path, byte[] bytes)
                { if (path == target + ".sig") throw new IOException("injected signature write failure"); File.WriteAllBytes(path, bytes); });
            }
            catch (IOException) { failed = true; }
            Check(failed, "injected partial install failure happened");
            Check(File.ReadAllBytes(target)[0] == 1 && File.ReadAllBytes(target + ".sig")[0] == 4, "DLL and signature BOTH restored after partial install");
            UpdateFileStore.Apply(target, newer, newerSig, null);
            Check(File.ReadAllBytes(target)[0] == 6 && File.ReadAllBytes(target + ".sig")[0] == 9, "successful update commits DLL and signature");
            Check(File.ReadAllBytes(target + ".old")[0] == 1 && File.ReadAllBytes(target + ".sig.old")[0] == 4, "previous pair remains recoverable");
            Console.WriteLine("PASS: " + checks + " reliability checks (three simultaneous peers, throttling, single-peer stall, load/reconnect/recovery)."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
