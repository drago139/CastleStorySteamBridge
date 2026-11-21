using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.Components.CatapultSpace;
using Brix.Game.Network;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.80 B-046 fix, GUEST ONLY. Host authority is untouched: the host still computes rotation/force and fires; this only keeps the guest's local copy of
    // CatapultControl.cible honest (see AimReconciler for the rules). No network traffic is added or changed.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> arEnabled; static readonly Dictionary<int, AimReconciler> arState = new Dictionary<int, AimReconciler>(); static int arStale, arRestored;
        static bool ArGuest { get { return arEnabled != null && arEnabled.Value && NetworkClient.active && !NetworkServer.active; } }

        static void InstallAimReconcile(Harmony h)
        {
            int ok = 0, bad = 0;
            try { h.Patch(AccessTools.Method(typeof(UNetCatapult), "CallCmdSetTarget"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "ArSent")); ok++; } catch (Exception) { bad++; }
            try { h.Patch(AccessTools.Method(typeof(UNetCatapult), "CallCmdClearTarget"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "ArCleared")); ok++; } catch (Exception) { bad++; }
            try { h.Patch(AccessTools.Method(typeof(Catapult), "ArreterDeViser"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "ArLeft")); ok++; } catch (Exception) { bad++; }
            try { h.Patch(AccessTools.Method(typeof(CatapultControl), "OnDeserialize"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "ArHostUpdate")); ok++; } catch (Exception) { bad++; }
            PtLog("AIM-RECONCILE hooks=" + ok + " failed=" + bad);
        }

        void BindAimReconcile() { arEnabled = Config.Bind("Catapult", "GuestAimReconcile", true, "Guest only: after the mouse leaves a catapult target the guest keeps the correct target (the newest command it sent, else the host's value) and stale host replies do not overwrite a newer local aim. Host authority is unchanged. false = original behaviour."); }

        static AimReconciler ArFor(CatapultControl c) { AimReconciler r; int id = c.GetInstanceID(); if (!arState.TryGetValue(id, out r)) { r = new AimReconciler(); arState[id] = r; } return r; }
        static Aim ArV(Vector3 v) { return new Aim(v.x, v.y, v.z); }
        static CatapultControl ArControl(GameObject go) { return go == null ? null : go.GetComponent<CatapultControl>(); }

        static void ArSent(GameObject instance, Vector3 target, Vector3 normal)
        {
            try { if (!ArGuest) return; CatapultControl c = ArControl(instance); if (c == null) return; ArFor(c).Sent(ArV(target), Time.realtimeSinceStartup); }
            catch (Exception) { }
        }

        static void ArCleared(GameObject instanceGO)
        {
            try { if (!ArGuest) return; CatapultControl c = ArControl(instanceGO); if (c == null) return; ArFor(c).Sent(Aim.Zero, Time.realtimeSinceStartup); }
            catch (Exception) { }
        }

        // The mouse left (also called right after a click): show the newest command we sent, else the host's value.
        static void ArLeft(Catapult __instance)
        {
            try
            {
                if (!ArGuest) return; CatapultControl c = __instance.GetComponent<CatapultControl>(); if (c == null) return;
                Aim a = ArFor(c).OnLeave(Time.realtimeSinceStartup); Vector3 v = new Vector3(a.X, a.Y, a.Z);
                if (c.cible != v) { c.Networkcible = v; arRestored++; if (arRestored <= 30) PtLog("AIM-RECONCILE leave: local cible restored to " + CsV(v) + " (was a hover value) restored#" + arRestored); }
            }
            catch (Exception) { }
        }

        static void ArHostUpdate(CatapultControl __instance)
        {
            try
            {
                if (!ArGuest) return; AimReconciler r = ArFor(__instance); Aim show; string why;
                bool reapply = r.OnHostUpdate(ArV(__instance.cible), Time.realtimeSinceStartup, out show, out why);
                if (reapply) { __instance.Networkcible = new Vector3(show.X, show.Y, show.Z); arStale++; if (arStale <= 30) PtLog("AIM-RECONCILE stale host reply ignored, local aim kept at " + CsV(__instance.cible) + " (" + why + ") stale#" + arStale + " pending=" + r.PendingCount); }
            }
            catch (Exception) { }
        }
    }
}
