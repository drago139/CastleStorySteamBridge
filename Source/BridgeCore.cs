using System;
using System.Net;
using System.Net.Sockets;

namespace CastleStorySteam
{
    // A single explicitly selected Steam peer; the game sees a loopback UDP endpoint.
    public sealed class BridgeCore : IDisposable
    {
        public delegate bool Transmit(byte[] packet);
        // 0.3.12: hot-path send without a fresh array per packet: the first 'length' bytes of a reused buffer are the packet.
        public delegate bool TransmitRange(byte[] buffer, int length);
        readonly Socket socket;
        readonly Transmit send;
        readonly TransmitRange sendRange;
        readonly byte[] frame = new byte[65512];
        readonly bool host;
        EndPoint game;
        readonly byte[] buffer = new byte[65507];
        public bool Ready { get; private set; }
        public bool RemoteClosed { get; private set; }
        public long Sent, Received;
        public int LastReceiveMilliseconds;
        // Diagnostics side channel (0.3.5+). Kinds 5 and 6 never reach the game; 0.3.3/0.3.4 ignore them.
        public const byte KindDiagnosticsRequest = 5, KindDiagnosticsReport = 6;
        // Friend-to-friend updates: version announcement, update request, update chunk.
        public const byte KindVersion = 7, KindUpdateRequest = 8, KindUpdateChunk = 9;
        // Round-trip meter (0.3.11): ping and its echo. Never reach the game; older bridges ignore them.
        public const byte KindPing = 10, KindPong = 11;
        public const byte KindMapProposal = 12;
        public const byte KindMapChoice = 13;
        public const byte KindItemFilter = 14;
        public const int MaxDiagnosticsBytes = 48 * 1024;
        public delegate void ExtraPacket(byte kind, byte[] payload);
        public ExtraPacket ExtraHandler;
        public int Port { get { return ((IPEndPoint)socket.LocalEndPoint).Port; } }
        public BridgeCore(bool isHost, int gamePort, Transmit transmit, TransmitRange transmitRange = null)
        {
            host = isHost; send = transmit; sendRange = transmitRange;
            frame[0] = 67; frame[1] = 83; frame[2] = 83; frame[3] = 1; frame[4] = 3;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Blocking = false;
            socket.ReceiveBufferSize = 1024 * 1024;
            if (host) game = new IPEndPoint(IPAddress.Loopback, gamePort);
        }
        // Steam unreliable sends are limited to 1200 bytes, including our 5-byte header.
        public const int MaxUnreliablePacket = 1200;
        // Only game data may go unreliable; handshake and goodbye must always arrive.
        public static bool CanSendUnreliable(byte[] packet)
        { return packet != null && CanSendUnreliable(packet, packet.Length); }
        public static bool CanSendUnreliable(byte[] packet, int length)
        { return packet != null && length <= packet.Length && length > 5 && length <= MaxUnreliablePacket && packet[4] == 3; }
        public static byte[] Packet(byte kind, byte[] data, int count)
        {
            byte[] p = new byte[count + 5];
            p[0] = 67; p[1] = 83; p[2] = 83; p[3] = 1; p[4] = kind;
            if (count != 0) Buffer.BlockCopy(data, 0, p, 5, count);
            return p;
        }
        public void Hello() { if (!host) send(Packet(1, null, 0)); }
        public void Goodbye() { send(Packet(4, null, 0)); }
        public void Receive(byte[] p)
        { Receive(p, p == null ? 0 : p.Length); }
        // The caller may reuse a large receive buffer; only count bytes belong to this packet.
        public void Receive(byte[] p, int count)
        {
            if (p == null || count < 5 || count > p.Length || count > 65512 || p[0] != 67 || p[1] != 83 || p[2] != 83 || p[3] != 1) return;
            if (p[4] == 4 && count == 5) { RemoteClosed = true; Ready = false; return; }
            if (RemoteClosed) return;
            if (p[4] == 1 && host && count == 5) { Ready = send(Packet(2, null, 0)); return; }
            if (p[4] == 2 && !host && count == 5) { Ready = true; return; }
            if (p[4] >= KindDiagnosticsRequest && p[4] <= KindItemFilter)
            {
                // Only after the handshake; the payload is copied because the caller reuses its buffer.
                if (!Ready || ExtraHandler == null || count - 5 > MaxDiagnosticsBytes) return;
                byte[] payload = new byte[count - 5];
                Buffer.BlockCopy(p, 5, payload, 0, payload.Length);
                ExtraHandler(p[4], payload);
                return;
            }
            if (p[4] != 3 || !Ready || game == null || count == 5) return;
            socket.SendTo(p, 5, count - 5, SocketFlags.None, game);
            Received++;
            LastReceiveMilliseconds = Environment.TickCount;
        }
        public void Pump()
        {
            if (!Ready) return;
            for (int i = 0; i < 256 && socket.Available > 0; i++)
            {
                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                int size;
                try { size = socket.ReceiveFrom(buffer, ref source); }
                catch (SocketException e)
                {
                    // Windows can report a previous UDP destination's ICMP port-unreachable here.
                    // Leave delivery/timeout detection to the game; this is not a Steam disconnect.
                    if (e.SocketErrorCode == SocketError.ConnectionReset) continue;
                    throw;
                }
                IPEndPoint from = (IPEndPoint)source;
                if (!IPAddress.IsLoopback(from.Address)) continue;
                if (host && !from.Equals(game)) continue;
                if (!host)
                {
                    if (game == null) game = from;
                    if (!from.Equals(game)) continue;
                }
                if (size == 0) continue;
                if (sendRange != null)
                {
                    Buffer.BlockCopy(buffer, 0, frame, 5, size);
                    if (sendRange(frame, size + 5)) Sent++;
                }
                else if (send(Packet(3, buffer, size))) Sent++;
            }
        }
        public void Dispose() { socket.Close(); Ready = false; }
    }
}
