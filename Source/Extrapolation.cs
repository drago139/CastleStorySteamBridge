using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Game.Network;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.51: units standing still on a client while the host hitches or the link is slow.
    // The game syncs a unit's position as UNetTransform.LastServerPosition about 6 times a second and the client only lerps toward the
    // last value it got, so when updates stop arriving every unit stops dead and then jumps. This keeps the units walking: when ALL
    // position updates have gone quiet (a network/host stall, not a single unit that simply stopped) a unit that was moving just before
    // keeps going along its last measured horizontal velocity, for at most MaxHorizon seconds. The real position is restored right after
    // each BrixUpdate, so the game's own state is never changed and the next real update pulls the unit to where it truly is.
    public sealed partial class SteamBridgePlugin
    {
        const float StallAfter = 0.35f;     // seconds without any position update before we start predicting
        const float MovingWindow = 0.45f;   // a unit counts as moving if it got an update this recently before the stall began
        const float MaxHorizon = 1.6f;      // do not predict further than this
        const float MaxSpeed = 7f;          // world units per second, clamps bad samples
        ConfigEntry<bool> extrapolateUnits;
        static Func<UNetTransform, Vector3> getServerPos; static Action<UNetTransform, Vector3> setServerPos;
        static bool extrapolateOn;
        static float globalLastUpdate;
        static int extrapolatedFrames, extrapolatedUnitsNow;
        static float nextExtrapolationLog, nextTrackPrune;
        sealed class UnitTrack { public Vector3 last; public float lastTime; public Vector3 vel; public Vector3 real; public bool shifted; }
        static readonly Dictionary<UNetTransform, UnitTrack> unitTracks = new Dictionary<UNetTransform, UnitTrack>();

        void InstallExtrapolation()
        {
            try
            {
                extrapolateUnits = Config.Bind("Network", "ExtrapolateUnits", false, "Experimental: while position updates from the host are late keep moving units along their last velocity for up to ~1.5 s. Off by default, tests showed it adds error when packets are lost.");
                extrapolateOn = extrapolateUnits.Value;
                FieldInfo posField = typeof(UNetTransform).GetField("LastServerPosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (posField != null) MakeAccessors(posField);
                MethodInfo update = typeof(UNetTransform).GetMethod("BrixUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (update == null || getServerPos == null) { Logger.LogWarning("Unit extrapolation hooks not found; units are interpolated as in the vanilla game."); return; }
                discoveryHarmony.Patch(update,
                    new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeUnitUpdate", BindingFlags.Static | BindingFlags.NonPublic)),
                    new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AfterUnitUpdate", BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo("Unit extrapolation ready (units keep walking through short network stalls).");
            }
            catch (Exception e) { Logger.LogWarning("Unit extrapolation unavailable: " + e.Message); }
        }

        static void MakeAccessors(FieldInfo f)
        {
            System.Reflection.Emit.DynamicMethod get = new System.Reflection.Emit.DynamicMethod("csb_get_pos", typeof(Vector3), new Type[] { typeof(UNetTransform) }, typeof(UNetTransform), true);
            System.Reflection.Emit.ILGenerator g = get.GetILGenerator();
            g.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); g.Emit(System.Reflection.Emit.OpCodes.Ldfld, f); g.Emit(System.Reflection.Emit.OpCodes.Ret);
            getServerPos = (Func<UNetTransform, Vector3>)get.CreateDelegate(typeof(Func<UNetTransform, Vector3>));
            System.Reflection.Emit.DynamicMethod set = new System.Reflection.Emit.DynamicMethod("csb_set_pos", typeof(void), new Type[] { typeof(UNetTransform), typeof(Vector3) }, typeof(UNetTransform), true);
            g = set.GetILGenerator();
            g.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); g.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); g.Emit(System.Reflection.Emit.OpCodes.Stfld, f); g.Emit(System.Reflection.Emit.OpCodes.Ret);
            setServerPos = (Action<UNetTransform, Vector3>)set.CreateDelegate(typeof(Action<UNetTransform, Vector3>));
        }

        static void BeforeUnitUpdate(UNetTransform __instance)
        {
            try
            {
                if (!extrapolateOn || NetworkServer.active || __instance == null || __instance.UpdateOnlyOnSleep) return;
                float now = Time.unscaledTime;
                Vector3 real = getServerPos(__instance);
                UnitTrack t;
                if (!unitTracks.TryGetValue(__instance, out t))
                {
                    t = new UnitTrack(); t.last = real; t.lastTime = now; unitTracks[__instance] = t;
                }
                if (real != t.last)
                {
                    float dt = now - t.lastTime;
                    if (dt >= 0.05f && dt < 1f)
                    {
                        Vector3 v = (real - t.last) / dt; v.y = 0f;
                        if (v.magnitude > MaxSpeed) v = v.normalized * MaxSpeed;
                        t.vel = v;
                    }
                    else if (dt >= 1f) t.vel = Vector3.zero;
                    t.last = real; t.lastTime = now; globalLastUpdate = now;
                }
                if (now >= nextTrackPrune) PruneUnitTracks(now);
                float quiet = now - globalLastUpdate;
                if (quiet < StallAfter || t.vel == Vector3.zero) return;
                if (t.lastTime < globalLastUpdate - MovingWindow) return;      // this unit had already stopped before the stall
                if (Time.timeScale < 0.01f) return;
                float ahead = Mathf.Min(now - t.lastTime, MaxHorizon);
                if (ahead <= 0f) return;
                t.real = real; t.shifted = true;
                setServerPos(__instance, real + t.vel * ahead);
                extrapolatedFrames++; extrapolatedUnitsNow++;
                if (now >= nextExtrapolationLog)
                {
                    nextExtrapolationLog = now + 5f;
                    SteamBridgePlugin p = discoveryInstance;
                    if (p != null) p.Logger.LogInfo("Extrapolating units: quiet " + quiet.ToString("F1") + " s, ahead " + ahead.ToString("F1") + " s, frames so far " + extrapolatedFrames);
                }
            }
            catch (Exception) { }
        }

        static void AfterUnitUpdate(UNetTransform __instance)
        {
            try
            {
                UnitTrack t;
                if (unitTracks.TryGetValue(__instance, out t) && t.shifted) { setServerPos(__instance, t.real); t.shifted = false; }
            }
            catch (Exception) { }
        }

        static void PruneUnitTracks(float now)
        {
            nextTrackPrune = now + 30f;
            List<UNetTransform> dead = null;
            foreach (KeyValuePair<UNetTransform, UnitTrack> kv in unitTracks)
                if (kv.Key == null) { if (dead == null) dead = new List<UNetTransform>(); dead.Add(kv.Key); }
            if (dead != null) for (int i = 0; i < dead.Count; i++) unitTracks.Remove(dead[i]);
        }
    }
}
