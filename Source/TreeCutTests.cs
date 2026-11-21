using System;
using System.Collections.Generic;
using CastleStorySteam;

// TreeCutRules plus a small model of one guest tree (Awake, cut RPC, Start/Go, destroy) with and without the guard, so the ghost-tree ordering is replayed without the game.
// The glue in Model mirrors TreeCutGuard.cs (park in the Cut prefix, replay in the Go postfix, mark cut in the OnDestroy prefix); it is a model, not the Unity code.
static class TreeCutTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static HostAction Hs(bool alive, bool ready, double t) { return TreeCutRules.HostStep(alive, ready, t, t); }   // normal game speed: game time == real time

    sealed class Model
    {
        internal bool Guard, Ready, WasCut, Parked, Destroyed, InRegistry = true, Standing;   // Standing: drawn by the tree's own mesh without a cut
        internal double ParkedAt;
        internal int Warnings, ReRoots;

        internal void Cut(double now)
        {
            if (Guard && TreeCutRules.OnCut(Ready, WasCut) == CutAction.Defer) { if (!Parked) { Parked = true; ParkedAt = now; } return; }
            CutVanilla();
        }
        void CutVanilla()
        {
            if (WasCut) return;
            if (!Ready) { Warnings++; return; }
            WasCut = true; InRegistry = false;
        }
        internal void Go()
        {
            Ready = true; Standing = true;
            if (Guard && TreeCutRules.RunAfterGo(Parked, Ready, WasCut)) { Parked = false; CutVanilla(); }
        }
        internal void Destroy()
        {
            if (Guard && Parked) { Parked = false; WasCut = true; InRegistry = false; }
            Destroyed = true;
            if (!WasCut) { if (Ready) { ReRoots++; InRegistry = true; } }   // ReRoot: Remove + Add, the tree stands again
            else InRegistry = false;
        }
    }

    static void Run(string name)
    {
        // Spawn, cut RPC and host release in the orders a guest can see them.
        // 1. cut before Start, then Start, then the host releases its tree
        Model vanilla = new Model(); vanilla.Cut(0); vanilla.Go(); vanilla.Destroy();
        Check(vanilla.Warnings == 1 && vanilla.ReRoots == 1 && vanilla.InRegistry, "vanilla: the early cut is lost and ReRoot puts the tree back (the ghost)");
        Model guarded = new Model(); guarded.Guard = true; guarded.Cut(0); Check(guarded.Parked && guarded.Warnings == 0, "guard parks the early cut without the warning");
        guarded.Go(); Check(guarded.WasCut && !guarded.Parked && !guarded.InRegistry, "guard replays the cut inside Go");
        guarded.Destroy(); Check(guarded.ReRoots == 0 && !guarded.InRegistry, "no ReRoot after a replayed cut");
        // 2. the tree is destroyed before it ever started (stall: spawn, cut and release in one pass)
        Model dead = new Model(); dead.Guard = true; dead.Cut(0); dead.Destroy();
        Check(dead.WasCut && !dead.InRegistry && dead.ReRoots == 0, "destroyed with a parked cut: marked cut, no ReRoot");
        Model deadVanilla = new Model(); deadVanilla.Cut(0); deadVanilla.Destroy();
        Check(deadVanilla.Warnings == 1 && !deadVanilla.WasCut, "vanilla model of the same order drops the cut");
        // 3. cut after Start: the guard stays out of the way
        Model late = new Model(); late.Guard = true; late.Go(); late.Cut(1);
        Check(late.WasCut && !late.Parked && late.Warnings == 0, "cut after Start is vanilla");
        // 4. two cuts before Start: the first wins, the second changes nothing
        Model twice = new Model(); twice.Guard = true; twice.Cut(0); twice.Cut(0.01); twice.Go();
        Check(twice.WasCut && twice.Warnings == 0, "double early cut replays once");
        // 5. a second cut after the replay is a no-op
        twice.Cut(2); Check(twice.Warnings == 0, "repeat cut harmless");
        // 6. never started (terrain not ready) but alive: stays parked until the sweep timeout
        Model slow = new Model(); slow.Guard = true; slow.Cut(0);
        Check(TreeCutRules.Sweep(true, false, false, 10) == PendingAction.Keep, "slow terrain keeps the cut");
    }

    public static int Main()
    {
        try
        {
            // OnCut
            Check(TreeCutRules.OnCut(true, false) == CutAction.Vanilla, "ready tree cut: vanilla");
            Check(TreeCutRules.OnCut(true, true) == CutAction.Vanilla, "already cut: vanilla");
            Check(TreeCutRules.OnCut(false, true) == CutAction.Vanilla, "not ready but cut: vanilla");
            Check(TreeCutRules.OnCut(false, false) == CutAction.Defer, "not ready, not cut: defer");
            // RunAfterGo
            Check(TreeCutRules.RunAfterGo(true, true, false), "replay after Go");
            Check(!TreeCutRules.RunAfterGo(false, true, false), "nothing parked: no replay");
            Check(!TreeCutRules.RunAfterGo(true, false, false), "Go did not make it ready (host released it): no replay");
            Check(!TreeCutRules.RunAfterGo(true, true, true), "already cut: no replay");
            // Sweep
            Check(TreeCutRules.Sweep(false, false, false, 1) == PendingAction.DropDead, "dead tree dropped");
            Check(TreeCutRules.Sweep(false, true, false, 9999) == PendingAction.DropDead, "dead beats timeout");
            Check(TreeCutRules.Sweep(true, true, true, 1) == PendingAction.DropDone, "already cut dropped quietly");
            Check(TreeCutRules.Sweep(true, true, false, 1) == PendingAction.RunNow, "ready but not replayed: run now");
            Check(TreeCutRules.Sweep(true, false, false, TreeCutRules.GuestTimeoutSeconds - 0.01) == PendingAction.Keep, "kept just before the timeout");
            Check(TreeCutRules.Sweep(true, false, false, TreeCutRules.GuestTimeoutSeconds) == PendingAction.DropTimeout, "dropped at the timeout");
            Check(TreeCutRules.Sweep(true, true, false, 9999) == PendingAction.RunNow, "a ready tree is replayed even when old");
            // HostStep
            Check(Hs(false, false, 0) == HostAction.Drop, "host: gone tree dropped");
            Check(Hs(false, true, 5) == HostAction.Drop, "host: gone tree dropped late");
            Check(Hs(true, true, 0.0) == HostAction.Wait, "host: waits the delay even if ready at once");
            Check(Hs(true, true, TreeCutRules.HostDelaySeconds - 0.001) == HostAction.Wait, "host: waits until the delay");
            Check(Hs(true, true, TreeCutRules.HostDelaySeconds) == HostAction.Fire, "host: fires at the delay when ready");
            Check(Hs(true, false, TreeCutRules.HostDelaySeconds) == HostAction.Wait, "host: not ready waits");
            Check(Hs(true, false, TreeCutRules.HostTimeoutSeconds - 0.001) == HostAction.Wait, "host: not ready waits until the timeout");
            Check(Hs(true, false, TreeCutRules.HostTimeoutSeconds) == HostAction.FireTimeout, "host: timeout fallback fires");
            Check(Hs(true, true, TreeCutRules.HostTimeoutSeconds) == HostAction.Fire, "host: a ready tree is a normal fire even late");
            // high game speed (5x): the game clock runs ahead of the real clock, the tree's own 2 s release clock is game time
            Check(TreeCutRules.HostStep(true, true, 0.2, 1.0) == HostAction.Fire, "host 5x: fires at 1 game second although only 0.2 s passed");
            Check(TreeCutRules.HostStep(true, true, 0.19, 0.95) == HostAction.Wait, "host 5x: still waits just before");
            Check(TreeCutRules.HostStep(true, false, 0.3, 1.6) == HostAction.FireTimeout, "host 5x: unready tree is blasted at 1.6 game seconds");
            Check(TreeCutRules.HostStep(true, false, 0.3, 1.59) == HostAction.Wait, "host 5x: unready tree waits just before the game-time timeout");
            // paused game (game time stands still): the real-time limits apply
            Check(TreeCutRules.HostStep(true, true, 0.4, 0.0) == HostAction.Fire, "host paused: fires after the real delay");
            Check(TreeCutRules.HostStep(true, true, 0.39, 0.0) == HostAction.Wait, "host paused: waits before the real delay");
            Check(TreeCutRules.HostStep(true, false, 1.2, 0.0) == HostAction.FireTimeout, "host paused: real timeout fallback");
            Check(TreeCutRules.HostDelayGameSeconds > 0 && TreeCutRules.HostDelayGameSeconds < TreeCutRules.HostTimeoutGameSeconds && TreeCutRules.HostTimeoutGameSeconds < 2.0, "game-time bounds stay below the tree's 2 s release clock");
            Check(TreeCutRules.GuestTimeoutSeconds >= 600, "a parked cut of an alive tree outlives a slow terrain load");
            Check(TreeCutRules.Sweep(true, false, false, 599) == PendingAction.Keep, "alive unready tree is kept after minutes");
            // constants: the delay and the timeout must stay below the 2 s after which an unstruck tree regenerates and releases itself
            Check(TreeCutRules.HostDelaySeconds > 0 && TreeCutRules.HostDelaySeconds < TreeCutRules.HostTimeoutSeconds && TreeCutRules.HostTimeoutSeconds < 1.5, "host timing bounds");
            // host queue walk: a queue entry polled every 16 ms fires exactly once, between the delay and delay + one frame
            for (int readyAtMs = 0; readyAtMs <= 1300; readyAtMs += 50)
            {
                int fired = 0; double firedAt = -1; bool timeout = false;
                for (double t = 0; t < 2.0 && fired == 0; t += 0.016)
                {
                    bool ready = t * 1000 >= readyAtMs;
                    HostAction a = Hs(true, ready, t);
                    if (a == HostAction.Fire || a == HostAction.FireTimeout) { fired++; firedAt = t; timeout = a == HostAction.FireTimeout; }
                }
                Check(fired == 1, "fires once, ready at " + readyAtMs + " ms");
                Check(firedAt >= TreeCutRules.HostDelaySeconds && firedAt < TreeCutRules.HostTimeoutSeconds + 0.02, "fire time bounded, ready at " + readyAtMs + " ms");
                Check(timeout == (readyAtMs >= TreeCutRules.HostTimeoutSeconds * 1000 + 16), "timeout only when the tree really stayed unready, ready at " + readyAtMs + " ms");
            }
            // log cap
            Check(TreeCutRules.UnderLogCap(0) && TreeCutRules.UnderLogCap(199) && !TreeCutRules.UnderLogCap(200), "log cap 200");
            Check(TreeCutRules.UnderKeyLogCap(0) && TreeCutRules.UnderKeyLogCap(79) && !TreeCutRules.UnderKeyLogCap(80), "key log cap 80 (errors, timeouts, counters have their own budget)");
            // guest model
            Run("model");
            Console.WriteLine("PASS: " + checks + " tree cut checks (cut/replay/sweep/host-queue decisions, guest ordering model, timing bounds).");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
    }
}
