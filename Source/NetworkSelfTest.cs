using System;
using System.Net;
using System.Net.Sockets;
using CastleStorySteam;

static class NetworkSelfTest
{
    internal static void Run()
    {
        using (Socket game = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            game.Bind(new IPEndPoint(IPAddress.Loopback, 0)); game.ReceiveTimeout = 1000;
            using (BridgeCore host = new BridgeCore(true, ((IPEndPoint)game.LocalEndPoint).Port, delegate(byte[] p) { return true; }))
            {
                byte[] shared = new byte[65512];
                byte[] hello = BridgeCore.Packet(1, null, 0);
                Buffer.BlockCopy(hello, 0, shared, 0, hello.Length);
                host.Receive(shared, hello.Length);
                if (!host.Ready) throw new Exception("Reused-buffer handshake failed");
                foreach (int count in new int[] { 1400, 1, 65507, 7 })
                {
                    byte[] payload = new byte[count]; new Random(count).NextBytes(payload);
                    byte[] packet = BridgeCore.Packet(3, payload, count);
                    Buffer.BlockCopy(packet, 0, shared, 0, packet.Length);
                    host.Receive(shared, packet.Length);
                    byte[] received = new byte[65507]; int size = game.Receive(received);
                    if (size != count) throw new Exception("Reused buffer forwarded stale trailing bytes");
                    for (int i = 0; i < count; i++) if (received[i] != payload[i]) throw new Exception("Payload corruption");
                }
                // 0.3.11 ping meter: ping/pong reach the side-channel handler intact, never the game.
                int extraSeen = 0; byte lastKind = 0; byte[] lastPayload = null;
                host.ExtraHandler = delegate(byte k, byte[] pl) { extraSeen++; lastKind = k; lastPayload = pl; };
                byte[] pingData = new byte[13]; new Random(7).NextBytes(pingData);
                foreach (byte kind in new byte[] { BridgeCore.KindPing, BridgeCore.KindPong })
                {
                    byte[] pk = BridgeCore.Packet(kind, pingData, 13);
                    Buffer.BlockCopy(pk, 0, shared, 0, pk.Length);
                    host.Receive(shared, pk.Length);
                    if (lastKind != kind || lastPayload == null || lastPayload.Length != 13) throw new Exception("Ping kind " + kind + " did not reach the side channel");
                    for (int i = 0; i < 13; i++) if (lastPayload[i] != pingData[i]) throw new Exception("Ping payload corrupted");
                }
                if (extraSeen != 2) throw new Exception("Ping handler call count wrong: " + extraSeen);
                if (game.Available != 0) throw new Exception("Ping packet reached the game");
                // 0.3.12 zero-allocation send: Pump hands a reused buffer and a length to the range transmitter; header and payload must be exact.
                byte[] captured = null; int capturedLen = 0;
                using (BridgeCore ranged = new BridgeCore(true, ((IPEndPoint)game.LocalEndPoint).Port, delegate(byte[] p) { return true; },
                    delegate(byte[] b, int n) { captured = new byte[n]; Buffer.BlockCopy(b, 0, captured, 0, n); capturedLen = n; return true; }))
                {
                    ranged.Receive(BridgeCore.Packet(1, null, 0));
                    if (!ranged.Ready) throw new Exception("Range-transmitter bridge handshake failed");
                    byte[] sample = new byte[1300]; new Random(11).NextBytes(sample);
                    game.SendTo(sample, new IPEndPoint(IPAddress.Loopback, ranged.Port));
                    for (int attempt = 0; attempt < 100 && capturedLen == 0; attempt++) { ranged.Pump(); if (capturedLen == 0) System.Threading.Thread.Sleep(5); }
                    if (capturedLen != 1305 || captured[0] != 67 || captured[1] != 83 || captured[2] != 83 || captured[3] != 1 || captured[4] != 3) throw new Exception("Range transmit frame header/length wrong: " + capturedLen);
                    for (int i = 0; i < 1300; i++) if (captured[5 + i] != sample[i]) throw new Exception("Range transmit payload corrupted");
                    if (ranged.Sent != 1) throw new Exception("Range transmit count wrong");
                }
                host.Receive(shared, shared.Length + 1); host.Receive(shared, -1); host.Receive(null, 5);
                if (game.Available != 0) throw new Exception("Invalid count forwarded");
                byte[] goodbye = BridgeCore.Packet(4, null, 0);
                Buffer.BlockCopy(goodbye, 0, shared, 0, 5); host.Receive(shared, 5);
                if (!host.RemoteClosed || host.Ready) throw new Exception("Reused-buffer goodbye failed");
            }
        }
        using (BridgeCore cold = new BridgeCore(true, 1, delegate(byte[] p) { return true; }))
        {
            int early = 0;
            cold.ExtraHandler = delegate(byte k, byte[] pl) { early++; };
            cold.Receive(BridgeCore.Packet(BridgeCore.KindPing, new byte[13], 13));
            if (early != 0) throw new Exception("Ping before the handshake must be ignored");
        }
        // Mixed send mode: only data packets up to 1200 bytes (header included) may go unreliable.
        if (!BridgeCore.CanSendUnreliable(BridgeCore.Packet(3, new byte[1195], 1195))) throw new Exception("1200-byte data packet must be unreliable-capable");
        if (BridgeCore.CanSendUnreliable(BridgeCore.Packet(3, new byte[1196], 1196))) throw new Exception("1201-byte data packet must stay reliable");
        if (!BridgeCore.CanSendUnreliable(BridgeCore.Packet(3, new byte[1], 1))) throw new Exception("Small data packet must be unreliable-capable");
        foreach (byte kind in new byte[] { 1, 2, 4 })
            if (BridgeCore.CanSendUnreliable(BridgeCore.Packet(kind, null, 0))) throw new Exception("Control packet " + kind + " must stay reliable");
        if (BridgeCore.CanSendUnreliable(null)) throw new Exception("Null packet must stay reliable");
    }
}
