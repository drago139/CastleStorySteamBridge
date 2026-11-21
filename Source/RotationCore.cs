using System;

namespace CastleStorySteam
{
    // 0.3.83 B-009 (guest units walk backwards), pure logic (no Unity, unit-tested).
    // The guest gets unit positions on the fast side channel (Steam 78) but the facing only as the LastServerRotation SyncVar on the slow
    // reliable stream, so a unit that turns round is drawn walking backwards until the stream catches up. The host now puts the rotation into
    // the same side-channel entry as the position (one update = one position + one rotation). Old peers keep the 0.3.82 format.
    //   Q4        quaternion (x,y,z,w), normalized lerp with the short-path sign fix, angle between two rotations.
    //   RotPack   "smallest three" compression of a unit quaternion into 32 bits (2-bit index + 3 x 10 bits), worst error ~0.1 deg.
    //   PosWire   packet layout: 0xB7 = 0.3.52..0.3.82 (id + position, 16 bytes), 0xB8 = 0.3.83+ (id + position + rotation, 20 bytes).
    //   SideSeq   side-channel sequence tracking that recovers when the host restarted (sequence numbers start again from 1).
    public struct Q4
    {
        public float X, Y, Z, W;
        public Q4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Q4 Identity = new Q4(0, 0, 0, 1);
        public static float Dot(Q4 a, Q4 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W; }
        public Q4 Normalized()
        {
            float m = (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return m < 1e-6f ? Identity : new Q4(X / m, Y / m, Z / m, W / m);
        }
        // Normalized lerp along the shorter arc; at 10 Hz samples the difference to slerp is far below what is visible.
        public static Q4 Nlerp(Q4 a, Q4 b, float t)
        {
            if (Dot(a, b) < 0f) b = new Q4(-b.X, -b.Y, -b.Z, -b.W);
            return new Q4(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t).Normalized();
        }
        public static float AngleDeg(Q4 a, Q4 b)
        {
            float d = Math.Abs(Dot(a.Normalized(), b.Normalized())); if (d > 1f) d = 1f;
            return (float)(2.0 * Math.Acos(d) * 180.0 / Math.PI);
        }
        public static Q4 FromYawDeg(float yaw) { double h = yaw * Math.PI / 360.0; return new Q4(0f, (float)Math.Sin(h), 0f, (float)Math.Cos(h)); }
        // Heading of the unit's forward (+z) vector on the ground plane, degrees (0 = +z, 90 = +x), as Unity's Quaternion * Vector3.forward.
        public float YawDeg()
        {
            float fx = 2f * (X * Z + W * Y), fz = 1f - 2f * (X * X + Y * Y);
            return (float)(Math.Atan2(fx, fz) * 180.0 / Math.PI);
        }
    }

    public static class RotPack
    {
        const float Range = 0.70710678f; const int Bits = 10, Max = (1 << Bits) - 1;
        public static uint Pack(Q4 q)
        {
            q = q.Normalized();
            float[] c = { q.X, q.Y, q.Z, q.W }; int big = 0;
            for (int i = 1; i < 4; i++) if (Math.Abs(c[i]) > Math.Abs(c[big])) big = i;
            float sign = c[big] < 0f ? -1f : 1f;           // q and -q are the same rotation: make the dropped component positive
            uint v = (uint)big; int k = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == big) continue;
                float f = c[i] * sign; if (f < -Range) f = -Range; if (f > Range) f = Range;
                uint e = (uint)Math.Round((f + Range) / (2f * Range) * Max);
                v |= e << (2 + Bits * k); k++;
            }
            return v;
        }
        public static Q4 Unpack(uint v)
        {
            int big = (int)(v & 3u); float[] c = new float[4]; float sum = 0f; int k = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == big) continue;
                uint e = (v >> (2 + Bits * k)) & (uint)Max; k++;
                c[i] = e / (float)Max * 2f * Range - Range; sum += c[i] * c[i];
            }
            c[big] = (float)Math.Sqrt(Math.Max(0f, 1f - sum));
            return new Q4(c[0], c[1], c[2], c[3]).Normalized();
        }
    }

    public static class PosWire
    {
        public const byte MagicPos = 0xB7, MagicPosRot = 0xB8;
        public const int Header = 8, EntryPos = 16, EntryPosRot = 20;
        // Both formats carry the same entries per packet so one sequence number means the same batch for every guest.
        // 8 + 58 * 20 = 1168 bytes, under the 1200-byte limit of an unreliable Steam P2P packet.
        public const int MaxEntries = 58;
        public static int EntrySize(byte magic) { return magic == MagicPosRot ? EntryPosRot : magic == MagicPos ? EntryPos : 0; }
        public static bool Valid(byte[] b, int read)
        {
            if (b == null || read < Header) return false;
            int es = EntrySize(b[0]); if (es == 0) return false;
            return Header + b[1] * es <= read;
        }
        // Rotation goes only to peers that understand it (0.3.83+); older ones get the 0.3.82 packet unchanged.
        public static bool PeerWantsRotation(string peerVersion, Func<string, string, bool> isNewer) { return peerVersion != null && isNewer(peerVersion, "0.3.82"); }
    }

    public sealed class SideSeq
    {
        public bool Known; public uint Last; public int Restarts; int lateRun;
        // Returns the sequence gap (1 = next packet, >1 = lost packets, <=0 = an older packet arriving late).
        // A gap far below zero cannot be reordering (packets are 100 ms apart): the host started counting again, so follow it.
        public int Next(uint seq)
        {
            if (!Known) { Known = true; Last = seq; lateRun = 0; return 1; }
            int gap = (int)(seq - Last);
            // 8 older packets in a row (0.8 s) is not reordering either: the host restarted shortly after the last game
            if (gap < -600 || (gap <= 0 && ++lateRun >= 8)) { Restarts++; Last = seq; lateRun = 0; return 1; }
            if (gap > 0) { Last = seq; lateRun = 0; }
            return gap;
        }
        public void Reset() { Known = false; }
    }

    // When both the fast channel and the slow stream write the facing of one unit, the newer one must win. The stream value is older by
    // construction while the side channel delivered recently, so it is put back (same rule as PositionSideChannelWins for positions).
    public static class RotationRules
    {
        public const float FreshSeconds = 0.5f;
        public static bool SideWins(float nowSec, float sideAtSec, bool sideKnown, float angleDeg)
        {
            return sideKnown && nowSec - sideAtSec < FreshSeconds && angleDeg > 0.01f;
        }
        // Host: send the entry again when the facing changed by more than this (or the position changed, or once a second).
        public const float SendAngleDeg = 1f;
    }
}
