using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.Components.CatapultSpace;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.83 B-048: on a GUEST the catapult aim line disappears for minutes after every shot (seen live T-129: 14:30:17 shot -> back 14:33:22).
    // Cause (game code): RpcEntry_Firing sets afficherTrajets=false; only Entry_Relaxed sets it back. Cooldown->Relaxed waits for
    // Time.time >= cooldown, and the SyncVar cooldown is the HOST's Time.time + 2 s. Time.time is local (seconds since that PC started the game,
    // minus clamped hitches), so on a guest whose clock is behind the host the wait is (host - guest) + 2 s - minutes.
    // Fix, guest only, visual state machine only: leave Cooldown after the same 2 s measured on the guest's own clock. The host, the shot,
    // the projectile, the cooldown SyncVar and every authoritative value are untouched.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> gcdCfg; static readonly Dictionary<int, float> gcdEntered = new Dictionary<int, float>(); static int gcdFixed;
        const float GcdSeconds = 2f;   // RpcExit_Firing: Networkcooldown = Time.time + 2f

        void InstallGuestCatapultCooldown()
        {
            try
            {
                gcdCfg = Config.Bind("Catapult", "GuestCooldownFix", true, "Guest only: after a shot the aim line comes back after 2 s of the guest's own time instead of waiting for the host's clock (vanilla multiplayer bug). false = original behaviour.");
                var enter = AccessTools.Method(typeof(CatapultBody), "Trans_Firing_Cooldown");
                var leave = AccessTools.Method(typeof(CatapultBody), "Trans_Cooldown_Relaxed");
                if (enter == null || leave == null) { Logger.LogWarning("CAT-COOLDOWN-FIX: methods not found"); return; }
                discoveryHarmony.Patch(enter, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("GcdAfterFiringCooldown", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                discoveryHarmony.Patch(leave, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("GcdAfterCooldownRelaxed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                Logger.LogInfo("CAT-COOLDOWN-FIX guest-only ready (" + (gcdCfg.Value ? "on" : "off") + ")");
            }
            catch (Exception e) { Logger.LogWarning("CAT-COOLDOWN-FIX unavailable: " + e.Message); }
        }

        static bool GcdGuest { get { return NetworkClient.active && !NetworkServer.active; } }

        // Firing -> Cooldown taken: remember when (guest clock)
        static void GcdAfterFiringCooldown(CatapultBody __instance, bool __result)
        {
            try { if (__result && __instance != null && GcdGuest) gcdEntered[__instance.GetInstanceID()] = Time.time; } catch (Exception) { }
        }

        static void GcdAfterCooldownRelaxed(CatapultBody __instance, ref bool __result)
        {
            try
            {
                if (__instance == null || !GcdGuest || gcdCfg == null || !gcdCfg.Value) return;
                int id = __instance.GetInstanceID(); float at;
                if (__result) { gcdEntered.Remove(id); return; }
                if (!gcdEntered.TryGetValue(id, out at)) return;
                float waited = Time.time - at;
                if (waited < GcdSeconds) return;
                __result = true; gcdEntered.Remove(id); gcdFixed++;
                PtLog("CAT-COOLDOWN-FIX " + __instance.name + " relaxed after " + waited.ToString("F1") + " s guest time; host cooldown was " + (__instance.cooldown - Time.time).ToString("F1") + " s away on this clock (fix #" + gcdFixed + ")");
                if (gcdEntered.Count > 64) gcdEntered.Clear();
            }
            catch (Exception) { }
        }
    }
}
