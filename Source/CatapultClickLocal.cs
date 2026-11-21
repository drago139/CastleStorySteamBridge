using System;
using System.Reflection;
using Brix.Audio;
using Brix.Game.Components.CatapultSpace;
using Brix.Game.Network;
using CastleStorySteam;
using HarmonyLib;
using UnityEngine;

// CATAPULT-CLICK-LOCAL (bug pack, switch [Fixes] CatapultClickLocalOnly, default true). Installed by BugPack.cs. B-071. Local sound only: every PC that plays needs it.
// Problem: choosing a catapult target plays the click "Catapult_Set_Target" (SoundEngine.Play in CatapultBody.React_Healthy_Viser) for everybody. Vanilla: the message runs where Catapult.SetTarget runs =
// on the HOST for every player's CmdSetTarget (the host hears every guest's clicks). Bridge: CatapultMarker.cs re-dispatches MessageViser on every GUEST whenever the synced target of any catapult changes
// (marker fix), so every guest hears every target change of every player (the host's own hover also writes the synced target, so probably one click per update), and at spawn / join for every catapult that
// already has a target.
// Fix: the click is only played on the machine whose player gave the order. CallCmdSetTarget runs only on the commanding machine (host-local or guest): it marks the catapult for 6 s. React_Healthy_Viser
// then mutes the click when there is no fresh mark (remote order, init, marker re-dispatch of someone else's order); the muting is done by skipping SoundEngine.Play("Catapult_Set_Target") only while
// that one reaction runs, so the marker/aim logic itself is untouched. Every other catapult sound (turning, winding, firing) stays as it is.
// Log: CATAPULT-CLICK allowed (own order) / muted (...) <catapult> (allowed N, muted M) id=<instance id>, two separate token buckets (own orders 20 then 1 per second, muted decisions 10 then 1 per
// 10 s) so join-time and hover decisions cannot hide the own-order lines; "+N similar not logged" counts what a bucket refused.
// Known limits: the mark is time based (6 s) and not consumed: two players ordering the same catapult within 6 s hear each other's click; an order the host rejects still leaves a mark; if the host
// stalls longer than 6 s a guest's own click is muted (watch for "muted" right after your own click and raise OwnOrderSeconds if it happens); an order path that bypasses CallCmdSetTarget is silent.
// All three hooks are resolved before the first one is patched.
internal static class CatapultClickLocal
{
    static Func<bool> enabled; static Action<string> note;
    static bool muteNext;
    static readonly OrderMarks own = new OrderMarks();
    static readonly TokenBucket allowedBucket = new TokenBucket(BugPackRules.CatapultAllowedBurst, BugPackRules.CatapultAllowedPerSecond), mutedBucket = new TokenBucket(BugPackRules.CatapultMutedBurst, BugPackRules.CatapultMutedPerSecond);
    static int logged, muted, allowed;

    internal static void Install(Harmony harmony, Func<bool> isEnabled, Action<string> log)
    {
        MethodInfo cmd = AccessTools.Method(typeof(UNetCatapult), "CallCmdSetTarget"); if (cmd == null) throw new MissingMethodException("UNetCatapult", "CallCmdSetTarget");
        MethodInfo viser = AccessTools.Method(typeof(CatapultBody), "React_Healthy_Viser"); if (viser == null) throw new MissingMethodException("CatapultBody", "React_Healthy_Viser");
        MethodInfo play = AccessTools.Method(typeof(SoundEngine), "Play", new Type[] { typeof(string), typeof(GameObject) }); if (play == null) throw new MissingMethodException("SoundEngine", "Play(string, GameObject)");
        enabled = isEnabled; note = log;
        harmony.Patch(cmd, new HarmonyMethod(AccessTools.Method(typeof(CatapultClickLocal), "BeforeCmd")), null, null, null, null);
        harmony.Patch(viser, new HarmonyMethod(AccessTools.Method(typeof(CatapultClickLocal), "BeforeViser")), null, null, new HarmonyMethod(AccessTools.Method(typeof(CatapultClickLocal), "AfterViser")), null);
        harmony.Patch(play, new HarmonyMethod(AccessTools.Method(typeof(CatapultClickLocal), "BeforePlay")), null, null, null, null);
    }
    internal static void Clear() { enabled = null; note = null; muteNext = false; own.Clear(); allowedBucket.Reset(); mutedBucket.Reset(); }

    // Commanding machine only: UNet's generated CallCmdSetTarget (client call or host-local call). The host never runs it for a guest's order.
    static void BeforeCmd(GameObject instance)
    {
        try
        {
            if (enabled == null || !enabled() || instance == null) return;
            own.Mark(instance.GetInstanceID(), Time.unscaledTime);
        }
        catch (Exception) { }
    }

    static void BeforeViser(CatapultBody __instance)
    {
        try
        {
            muteNext = false;
            if (enabled == null || !enabled() || __instance == null) return;
            int gid = __instance.gameObject.GetInstanceID(); float now = Time.unscaledTime;
            bool mine = own.IsFresh(gid, now);
            if (mine) { allowed++; } else { muteNext = true; muted++; }
            TokenBucket bucket = mine ? allowedBucket : mutedBucket;
            if (note != null && bucket.Allow(now))
            {
                logged++; int skipped = bucket.TakeDropped();
                note("CATAPULT-CLICK " + (mine ? "allowed (own order)" : "muted (not my order / init / marker re-dispatch)") + " " + __instance.name + " (allowed " + allowed + ", muted " + muted + ") id=" + gid + (skipped > 0 ? " +" + skipped + " similar not logged" : ""));
            }
        }
        catch (Exception) { muteNext = false; }
    }

    static Exception AfterViser(Exception __exception) { muteNext = false; return __exception; }

    static bool BeforePlay(string name)
    {
        return BugPackRules.SoundAllowed(muteNext, name);
    }
}
