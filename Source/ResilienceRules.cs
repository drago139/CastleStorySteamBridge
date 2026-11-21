using System;

namespace CastleStorySteam
{
    // 0.4.17 KICK / REJOIN fix. Pure decisions (no Unity, no Steam) so ResilienceTests can drive them. C# 5 only.
    // Evidence (2026-11-07 17:50-17:54, user = guest on 0.4.15, a friend as host on 0.4.11, files in (local evidence notes)):
    //  (1) The link user<->host stalled in BOTH directions (host log: reliable stuck 65 s, unreliable loss 100 %; his link to the third player over relay stayed clean).
    //      ~26 s after the watchdog's close Steam raised P2PSessionConnectFail(4 = timeout); OnFailure then ended the game session at once ("Втрачено зв'язок через Steam (код 4)").
    //  (2) The user could NOT come back: the host kept the old guest bridge of the same Steam id. A repeated hello only re-acked on the OLD BridgeCore, whose loopback
    //      socket (= source port the game server knows) belonged to the old, dead UNET connection (disconnect timeout 600 s, host log: conn=2 ready=True, 1.26 MB queued).
    //      The new client's connect request travelled through that old socket and was swallowed: "Could not connect to server".
    // Fixes: (A) HOST: a hello from a guest whose old bridge carried game data but has been silent for RecycleQuietMs starts a NEW bridge (new loopback port = new UNET connection;
    //        the old port is handed to NoteDeadGuest so the old connection is dropped). (B) GUEST: a Steam P2P failure no longer ends the session at once; the guest keeps the
    //        bridge (its 2 s pings reopen the Steam session) for GuestHoldOnP2PFailureSeconds and leaves only if no data came from the host in that time.
    internal static class ResilienceRules
    {
        // a live guest never sends a hello; a duplicate hello of the same join arrives before any game data (received == 0) or while data still flows (< 3 s old)
        public const int RecycleQuietMs = 3000;

        public static bool RecycleOnHello(bool switchOn, long received, int msSinceLastGuestData)
        {
            return switchOn && received > 0 && msSinceLastGuestData >= RecycleQuietMs;
        }

        // Guest side. One "episode" = the time since the first Steam failure, ended by any data from the host after it.
        // Times are monotonic milliseconds (Stopwatch based); lastDataMs <= episode start means no data since the episode began.
        internal sealed class FailureHold
        {
            const long None = long.MinValue;
            long startMs = None;
            public int Count;

            public bool Open(long lastDataMs) { return startMs != None && lastDataMs <= startMs; }
            public long StartMs { get { return startMs; } }
            public void Reset() { startMs = None; Count = 0; }

            // true = ignore this failure (keep the bridge); false = leave as before
            public bool OnFailure(long nowMs, long lastDataMs, int holdSeconds)
            {
                if (holdSeconds <= 0) return false;
                if (startMs == None || lastDataMs > startMs) { startMs = nowMs; Count = 0; }   // data came after the last failure: a new episode
                Count++;
                return nowMs - startMs < holdSeconds * 1000L;
            }

            // timer fallback (Steam may stop raising failures): the episode is open, nothing came from the host and the hold is over
            public bool Expired(long nowMs, long lastDataMs, int holdSeconds)
            {
                return holdSeconds > 0 && Open(lastDataMs) && nowMs - startMs >= holdSeconds * 1000L;
            }
        }

        // The hold applies only to a session that was really running: handshake done and game data seen. A join that never worked keeps the old immediate failure.
        public static bool HoldApplies(bool bridgeReady, long received)
        {
            return bridgeReady && received > 0;
        }

        // monotonic age of the last datagram: BridgeCore.LastReceiveMilliseconds is Environment.TickCount (int, may wrap), so only the difference is meaningful
        public static long LastDataMs(long nowMs, int tickNow, int tickLast)
        {
            return nowMs - (long)unchecked(tickNow - tickLast);
        }
    }
}
