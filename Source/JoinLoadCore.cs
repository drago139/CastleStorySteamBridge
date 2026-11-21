using System;
using System.Text;

namespace CastleStorySteam
{
    internal struct JoinLoadSample
    {
        internal float At, FrameMs, WorstMs, StepAge;
        internal int Rtt, Queue, Bytes, Lost, Gc, Ram, Step, MaxStep, Conn, Host;
        internal bool Server, Ready;
        internal string Route;
        public override string ToString()
        {
            return "JOINLOAD t=" + At.ToString("F1") + " role=" + (Server ? "host" : "guest") + " conn=" + Conn
                + " frameMs=" + FrameMs.ToString("F1") + " maxMs=" + WorstMs.ToString("F1") + " hitch=" + (WorstMs >= 150)
                + " route=" + Route + " ready=" + Ready + " step=" + Step + "/" + MaxStep + " stepAge=" + StepAge.ToString("F1")
                + " rtt=" + Rtt + " hostQueue=" + Queue + " outBytes=" + Bytes + " lost=" + Lost + " gcDelta=" + Gc + " freeMB=" + Ram;
        }
    }

    // No allocation in Add. Formatting occurs once a second or when packing a finished window.
    internal sealed class JoinLoadRing
    {
        readonly JoinLoadSample[] samples = new JoinLoadSample[121];
        int next, count;
        internal int Count { get { return count; } }
        internal void Add(JoinLoadSample sample) { samples[next] = sample; next = (next + 1) % samples.Length; if (count < samples.Length) count++; }
        internal string Report(string reason)
        {
            StringBuilder b = new StringBuilder("JOINLOAD v1; -1=unavailable; hostQueue is per UNET host; host simulation-only CPU unavailable.\r\nColumns: t role/conn frameMs maxMs route ready step/max ageSec RTTms hostQueue outBytes lost gcDelta freeMB. Host step is guest-reported, otherwise unknown.\r\n" + reason + "\r\n");
            for (int i = 0; i < count; i++)
            {
                JoinLoadSample s=samples[(next-count+i+samples.Length)%samples.Length];
                b.Append("t=").Append(s.At.ToString("F1")).Append(' ').Append(s.Server ? 'H' : 'G').Append(s.Conn).Append(' ')
                    .Append(s.FrameMs.ToString("F1")).Append(' ').Append(s.WorstMs.ToString("F1")).Append(' ').Append(s.Route).Append(' ')
                    .Append(s.Ready ? 1 : 0).Append(' ').Append(s.Step).Append('/').Append(s.MaxStep).Append(' ').Append(s.StepAge.ToString("F1")).Append(' ')
                    .Append(s.Rtt).Append(' ').Append(s.Queue).Append(' ').Append(s.Bytes).Append(' ').Append(s.Lost).Append(' ').Append(s.Gc).Append(' ').Append(s.Ram).AppendLine();
            }
            return JoinLoadWire.Clean(b.ToString());
        }
    }

    // Dedicated channel protocol. Exact v1 handshake; reports never enter BridgeCore or game UNET.
    internal static class JoinLoadWire
    {
        internal const int MaxReport = 16384, Chunk = 768, Header = 18, MaxPacket = Header + Chunk;
        internal const byte State = 1, Report = 2, Declined = 3;
        internal static bool CanBind(ulong playerSteamId, ulong senderSteamId, bool bridge, bool bridgePortMatches)
        { return playerSteamId != 0 && playerSteamId == senderSteamId && (!bridge || bridgePortMatches); }
        internal static bool CanDeliver(bool friend, bool consent, bool hostLoading, bool guestLoading, float stateAge, int queue)
        { return friend && consent && !hostLoading && !guestLoading && stateAge >= 0 && stateAge <= 12 && queue >= 0 && queue <= 32768; }
        internal static string Clean(string text)
        {
            // Only allowlisted numeric/route fields are recorded; strip IDs as defense in depth.
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\b7656\d{13}\b", "<id>");
            byte[] data = Encoding.UTF8.GetBytes(text);
            if (data.Length <= MaxReport) return text;
            int start = data.Length - MaxReport + 128;
            while (start < data.Length && data[start] != 10) start++;
            return "JOINLOAD v1 (newest samples; earlier samples trimmed at 16 KB)\r\n" + Encoding.UTF8.GetString(data, Math.Min(start + 1, data.Length), Math.Max(0, data.Length - start - 1));
        }
        internal static byte[] Packet(byte kind, uint token, uint identity, int offset, byte[] payload, int length)
        {
            if (token == 0 || length < 0 || length > Chunk || offset < 0 || offset > MaxReport || (payload != null && length > payload.Length)) throw new ArgumentException("JOINLOAD bounds");
            byte[] p = new byte[Header + length];
            p[0] = 74; p[1] = 76; p[2] = 1; p[3] = kind;
            Put(p, 4, token); Put(p, 8, identity); Put(p, 12, (uint)offset); p[16] = (byte)length; p[17] = (byte)(length >> 8);
            if (length > 0) Buffer.BlockCopy(payload, 0, p, Header, length);
            return p;
        }
        static void Put(byte[] p, int i, uint n) { for (int k = 0; k < 4; k++) p[i+k] = (byte)(n >> (8*k)); }
        internal static uint Get(byte[] p, int i) { return (uint)(p[i] | p[i+1] << 8 | p[i+2] << 16 | p[i+3] << 24); }
        internal static bool Valid(byte[] p, int n)
        {
            return p != null && n >= Header && n <= MaxPacket && n <= p.Length && p[0] == 74 && p[1] == 76 && p[2] == 1
                && p[3] >= State && p[3] <= Declined && Get(p,4) != 0 && Get(p,12) <= MaxReport
                && (p[16] | p[17] << 8) == n - Header;
        }
    }

    internal sealed class JoinLoadReceiver
    {
        readonly byte[] buffer = new byte[JoinLoadWire.MaxReport];
        int count, total;
        uint token;
        bool finished;
        internal void Reset() { count = total = 0; token = 0; finished = false; }
        internal string Accept(byte[] packet, int n, uint expectedToken)
        {
            if (!JoinLoadWire.Valid(packet,n) || packet[3] != JoinLoadWire.Report || JoinLoadWire.Get(packet,4) != expectedToken || finished) return null;
            int size = (int)JoinLoadWire.Get(packet,8), offset = (int)JoinLoadWire.Get(packet,12), len = n - JoinLoadWire.Header;
            if (size < 1 || size > buffer.Length || offset != count || offset + len > size || len < 1) return null;
            if (count == 0) { token = expectedToken; total = size; }
            if (token != expectedToken || total != size) return null;
            Buffer.BlockCopy(packet,JoinLoadWire.Header,buffer,count,len); count += len;
            if (count != total) return null;
            finished = true;
            try { return JoinLoadWire.Clean(new UTF8Encoding(false,true).GetString(buffer,0,count)); }
            catch (ArgumentException) { return null; }
        }
    }
}
