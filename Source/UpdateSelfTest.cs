using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using CastleStorySteam;

// Self-test for diagnostics/update plumbing (runs in the installer with --self-test).
static class UpdateSelfTest
{
    static bool Same(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    internal static void Run()
    {
        byte[] dll = Setup.Resource("bridge.dll"), sig = Setup.Resource("bridge.dll.sig");
        // Signature: bound to the DLL bytes, the signature bytes and the version text.
        if (!UpdateVerifier.Verify(BridgeVersion.Text, dll, sig)) throw new Exception("Bundled bridge signature does not verify");
        byte[] bad = (byte[])dll.Clone(); bad[bad.Length / 2] ^= 1;
        if (UpdateVerifier.Verify(BridgeVersion.Text, bad, sig)) throw new Exception("Tampered DLL verified");
        byte[] badSig = (byte[])sig.Clone(); badSig[0] ^= 1;
        if (UpdateVerifier.Verify(BridgeVersion.Text, dll, badSig)) throw new Exception("Tampered signature verified");
        if (UpdateVerifier.Verify("9.9.9", dll, sig)) throw new Exception("Signature must be bound to the version");
        if (UpdateVerifier.Verify(BridgeVersion.Text, dll, new byte[10]) || UpdateVerifier.Verify(BridgeVersion.Text, new byte[0], sig)) throw new Exception("Malformed input verified");
        // Version comparison (only strictly newer, strict syntax).
        Version current = new Version(BridgeVersion.Text);
        string newer = new Version(current.Major, current.Minor, current.Build + 1).ToString();
        string older = current.Build > 0 ? new Version(current.Major, current.Minor, current.Build - 1).ToString() : "0.0.0";
        if (!UpdateVerifier.IsNewer(newer, BridgeVersion.Text) || !UpdateVerifier.IsNewer("0.10.0", "0.9.9") || !UpdateVerifier.IsNewer("1.0.0", "0.99.99")) throw new Exception("IsNewer positive cases failed");
        foreach (string notNewer in new string[] { BridgeVersion.Text, older, "0.0.1", "abc", newer + "; calc", "", "0.3", newer + ".1.2", "-1.0.0" })
            if (UpdateVerifier.IsNewer(notNewer, BridgeVersion.Text)) throw new Exception("IsNewer accepted " + notNewer);
        // Package round trip and rejection of damaged packages.
        byte[] blob = UpdatePackage.Build(BridgeVersion.Text, dll, sig);
        string version; byte[] dll2, sig2;
        if (!UpdatePackage.Parse(blob, out version, out dll2, out sig2) || version != BridgeVersion.Text || !Same(dll, dll2) || !Same(sig, sig2)) throw new Exception("Package round trip failed");
        byte[] cut = new byte[blob.Length - 1]; Buffer.BlockCopy(blob, 0, cut, 0, cut.Length);
        byte[] longer = new byte[blob.Length + 1]; Buffer.BlockCopy(blob, 0, longer, 0, blob.Length);
        if (UpdatePackage.Parse(cut, out version, out dll2, out sig2) || UpdatePackage.Parse(longer, out version, out dll2, out sig2) || UpdatePackage.Parse(new byte[5], out version, out dll2, out sig2) || UpdatePackage.Parse(null, out version, out dll2, out sig2))
            throw new Exception("Damaged package accepted");
        // Chunk transfer: in order works; wrong id, wrong order, resized or oversized transfers are refused.
        List<byte[]> chunks = UpdatePackage.Chunks(123456u, blob);
        string error;
        UpdatePackage.Receiver ok = new UpdatePackage.Receiver(123456u);
        foreach (byte[] c in chunks) if (!ok.Accept(c, out error)) throw new Exception("Chunk rejected: " + error);
        if (!ok.Complete || !Same(ok.Blob, blob)) throw new Exception("Reassembly failed");
        if (new UpdatePackage.Receiver(1u).Accept(chunks[0], out error)) throw new Exception("Foreign transfer id accepted");
        if (chunks.Count > 1)
        {
            UpdatePackage.Receiver order = new UpdatePackage.Receiver(123456u);
            if (order.Accept(chunks[1], out error)) throw new Exception("Out-of-order chunk accepted");
            UpdatePackage.Receiver dup = new UpdatePackage.Receiver(123456u);
            dup.Accept(chunks[0], out error);
            if (dup.Accept(chunks[0], out error)) throw new Exception("Repeated chunk accepted");
        }
        byte[] huge = (byte[])chunks[0].Clone(); Buffer.BlockCopy(BitConverter.GetBytes(UpdatePackage.MaxBytes + 1), 0, huge, 4, 4);
        if (new UpdatePackage.Receiver(123456u).Accept(huge, out error)) throw new Exception("Oversized transfer accepted");
        if (new UpdatePackage.Receiver(123456u).Accept(new byte[12], out error) || new UpdatePackage.Receiver(123456u).Accept(null, out error)) throw new Exception("Empty chunk accepted");
        // BridgeCore side channel: kinds 5..9 reach the handler only after the handshake and never reach the game.
        using (Socket game = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            game.Bind(new IPEndPoint(IPAddress.Loopback, 0)); game.ReceiveTimeout = 500;
            using (BridgeCore host = new BridgeCore(true, ((IPEndPoint)game.LocalEndPoint).Port, delegate(byte[] p) { return true; }))
            {
                int calls = 0; byte lastKind = 0; int lastLength = -1;
                host.ExtraHandler = delegate(byte k, byte[] payload) { calls++; lastKind = k; lastLength = payload.Length; };
                byte[] payload3 = new byte[] { 1, 2, 3 };
                host.Receive(BridgeCore.Packet(5, payload3, 3));
                if (calls != 0) throw new Exception("Extra packet handled before the handshake");
                host.Receive(BridgeCore.Packet(1, null, 0));
                if (!host.Ready) throw new Exception("Handshake failed");
                for (byte kind = 5; kind <= 14; kind++)
                {
                    host.Receive(BridgeCore.Packet(kind, payload3, 3));
                    if (lastKind != kind || lastLength != 3) throw new Exception("Extra packet " + kind + " not delivered");
                }
                if (calls != 10) throw new Exception("Unexpected extra packet count " + calls);
                host.Receive(BridgeCore.Packet(15, payload3, 3)); host.Receive(BridgeCore.Packet(0, payload3, 3));
                if (calls != 10) throw new Exception("Unknown kind reached the handler");
                host.Receive(BridgeCore.Packet(5, new byte[BridgeCore.MaxDiagnosticsBytes + 1], BridgeCore.MaxDiagnosticsBytes + 1));
                if (calls != 10) throw new Exception("Oversized extra packet reached the handler");
                if (game.Available != 0 || host.Received != 0) throw new Exception("Extra packets leaked into game traffic");
                host.ExtraHandler = null;
                host.Receive(BridgeCore.Packet(7, payload3, 3));
                if (game.Available != 0 || host.Received != 0) throw new Exception("Extra packet without a handler leaked into game traffic");
                // Game data still flows normally afterwards.
                host.Receive(BridgeCore.Packet(3, payload3, 3));
                byte[] got = new byte[16]; int size = game.Receive(got);
                if (size != 3 || host.Received != 1) throw new Exception("Game data stopped after extra packets");
            }
        }
    }
}
