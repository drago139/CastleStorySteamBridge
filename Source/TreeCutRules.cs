using System;

namespace CastleStorySteam
{
    // 0.3.108: the decisions of TreeCutGuard (guest side: a tree's cut RPC that arrives before the tree object has started) and of the host-side
    // tree blast delay (TreeBlastFix), without Unity, so TreeCutTests can drive them. No game types here.
    internal enum CutAction { Vanilla, Defer }
    internal enum PendingAction { Keep, RunNow, DropDead, DropDone, DropTimeout }
    internal enum HostAction { Wait, Fire, FireTimeout, Drop }

    internal static class TreeCutRules
    {
        // Host: a tree created by an explosion gets its cut RPC this long after it was spawned, so the guests have run its Start (their spawn and the cut
        // otherwise arrive in one packet). It must stay far below 2 game seconds: an unstruck tree regenerates and releases itself 2 s (Time.time) after it became ready.
        internal const double HostDelaySeconds = 0.4;
        // Host: if the tree still is not ready by now, do what the vanilla code did at once (blast and unregister), so a stuck tree is never worse than before.
        internal const double HostTimeoutSeconds = 1.2;
        // The same two limits in game time (Time.time): at a high game speed (or while paused) real time and game time drift apart, and the tree's own 2 s clock is game time.
        // Whichever limit is reached first counts; both stay below the tree's 2 game seconds.
        internal const double HostDelayGameSeconds = 1.0;
        internal const double HostTimeoutGameSeconds = 1.6;
        // Guest: a parked cut whose tree is alive but not ready (the terrain is still loading) keeps waiting: Go() replays it the moment the tree starts. This is only a leak guard
        // for a record that nobody settled, far longer than any terrain load.
        internal const double GuestTimeoutSeconds = 3600.0;
        internal const int MaxLogLines = 200;       // per-tree outcome lines
        internal const int MaxKeyLogLines = 80;     // separate budget: errors, timeouts, failed cuts and the counter lines must stay visible after a heavy bombing session

        // The cut RPC reached DamagedTree.Cut. Vanilla returns at once when the tree was cut already; when it is not ready it only logs a warning and drops the cut.
        internal static CutAction OnCut(bool ready, bool wasCut)
        {
            return ready || wasCut ? CutAction.Vanilla : CutAction.Defer;
        }

        // DamagedTree.Go finished: replay the deferred cut now?
        internal static bool RunAfterGo(bool pending, bool ready, bool wasCut)
        {
            return pending && ready && !wasCut;
        }

        // Periodic sweep over a deferred cut.
        internal static PendingAction Sweep(bool alive, bool ready, bool wasCut, double ageSeconds)
        {
            if (!alive) return PendingAction.DropDead;
            if (wasCut) return PendingAction.DropDone;
            if (ready) return PendingAction.RunNow;                       // Go finished but its postfix did not replay the cut (Go threw after it set ready)
            if (ageSeconds >= GuestTimeoutSeconds) return PendingAction.DropTimeout;
            return PendingAction.Keep;
        }

        // Host queue entry for one tree that the blast just created or found. ageSeconds is real time, gameAgeSeconds is Time.time since the entry was queued.
        internal static HostAction HostStep(bool alive, bool ready, double ageSeconds, double gameAgeSeconds)
        {
            if (!alive) return HostAction.Drop;
            if (ageSeconds < HostDelaySeconds && gameAgeSeconds < HostDelayGameSeconds) return HostAction.Wait;
            if (ready) return HostAction.Fire;
            if (ageSeconds >= HostTimeoutSeconds || gameAgeSeconds >= HostTimeoutGameSeconds) return HostAction.FireTimeout;
            return HostAction.Wait;
        }

        internal static bool UnderLogCap(int linesSoFar) { return linesSoFar < MaxLogLines; }
        internal static bool UnderKeyLogCap(int linesSoFar) { return linesSoFar < MaxKeyLogLines; }
    }
}
