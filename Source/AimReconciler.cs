using System.Collections.Generic;

namespace CastleStorySteam
{
    // 0.3.80 B-046 guest-side aim reconciliation, pure logic (no Unity), unit-tested.
    // Problem proven by CAT-DRIFT logs: Catapult.Viser (mouse hover) writes CatapultControl.cible LOCALLY on the guest and the host never re-sends an
    // unchanged value, so the hover value stays after the mouse leaves. Rules (host stays the authority for the real target):
    //  1. Every CmdSetTarget/CmdClearTarget the guest sends is remembered as "pending" (instant local display of the newest command).
    //  2. When the mouse leaves, the local cible becomes the newest pending command, or the last value the host sent (nothing sent -> host value).
    //  3. A host update that equals the newest pending command confirms it; one that equals an OLDER pending command is a stale reply and must not
    //     overwrite the newer local aim; any other value (host rejected / another player / reconnect) is accepted as authoritative.
    //  4. Pending commands older than TimeoutSeconds are dropped (host rejected silently or the reply was lost).
    public struct Aim
    {
        public float X, Y, Z;
        public Aim(float x, float y, float z) { X = x; Y = y; Z = z; }
        public bool Same(Aim o) { return System.Math.Abs(X - o.X) < 0.001f && System.Math.Abs(Y - o.Y) < 0.001f && System.Math.Abs(Z - o.Z) < 0.001f; }
        public static Aim Zero { get { return new Aim(0f, 0f, 0f); } }
    }

    public sealed class AimReconciler
    {
        public const float TimeoutSeconds = 5f;
        struct Pending { public Aim Value; public float At; public int Seq; }
        readonly List<Pending> pending = new List<Pending>(); Aim lastHost = Aim.Zero; int seq;
        public int PendingCount { get { return pending.Count; } }
        public Aim LastHost { get { return lastHost; } }
        public int Seq { get { return seq; } }

        public void Sent(Aim value, float now) { Expire(now); seq++; Pending p; p.Value = value; p.At = now; p.Seq = seq; pending.Add(p); if (pending.Count > 16) pending.RemoveAt(0); }

        // Aim the guest should show right after the mouse left.
        public Aim OnLeave(float now) { Expire(now); return pending.Count > 0 ? pending[pending.Count - 1].Value : lastHost; }

        // A host update arrived. Returns true when the LOCAL value must be re-applied (stale reply); 'show' is then the value to apply.
        public bool OnHostUpdate(Aim host, float now, out Aim show, out string why)
        {
            Expire(now); lastHost = host; show = host; why = "accepted";
            if (pending.Count == 0) return false;
            int last = pending.Count - 1;
            if (pending[last].Value.Same(host)) { pending.Clear(); why = "confirmed newest command"; return false; }
            for (int i = last - 1; i >= 0; i--)
                if (pending[i].Value.Same(host)) { pending.RemoveRange(0, i + 1); show = pending[pending.Count - 1].Value; why = "stale reply (command #" + "older" + ") ignored"; return true; }
            pending.Clear(); why = "host value differs from every pending command: host is authoritative"; return false;
        }

        // 0.4.27 review: stop after the removal. Without the break the loop went on with i - 1 on the shortened list and threw ArgumentOutOfRangeException whenever the newest expired
        // command sat at index i >= 1 with 2 * i >= Count: two or more commands that had all expired (two clicks on an unchanged target, no host reply), and also a partial expiry such as
        // three expired commands plus one fresh one. The caller swallowed it and lost that step (command not remembered / host value not stored / hover value not put back).
        void Expire(float now) { for (int i = pending.Count - 1; i >= 0; i--) if (now - pending[i].At > TimeoutSeconds) { pending.RemoveRange(0, i + 1); break; } }
    }
}
