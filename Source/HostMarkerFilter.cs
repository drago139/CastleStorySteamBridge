using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.77 B-043 option 1: on the HOST do not draw other factions' work-order outlines / counters (the host simulates every faction's workers, so the
    // base game shows the guest's "to be broken" marks on the host, through fog). Own and allied orders are unchanged; guests are unchanged;
    // no network traffic, no order, worker or AI change: only the cosmetic highlight wrapper / overhead counter is skipped.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> hmEnabled; static int hmSkipped, hmAllowed, hmOverheadHidden, hmLogged;

        void InstallHostMarkerFilter()
        {
            hmEnabled = Config.Bind("Fog", "HideForeignOrderMarkers", true, "Host: do not draw the outlines and counters of other (non-allied) factions' dig/repair/transform orders. Own and allied orders are unchanged. Set false to get the original game behaviour.");
            int ok = 0, bad = 0;
            string[] goals = { "Brix.Game.AI.DigGoal", "Brix.Game.AI.RepairGoal", "Brix.Game.AI.TransformGoal" };
            string[] wrappers = { "HighlightAtYellow", "HighlightTargetYellow" };
            foreach (string gn in goals)
            {
                Type g = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { try { g = a.GetType(gn, false); } catch (Exception) { } if (g != null) break; }
                if (g == null) { bad++; continue; }
                foreach (string wn in wrappers)
                {
                    try
                    {
                        MethodInfo open = AccessTools.Method(typeof(WorkNodesWrappers), wn);
                        if (open == null || !open.IsGenericMethodDefinition) { bad++; continue; }
                        MethodInfo closed = open.MakeGenericMethod(g);
                        discoveryHarmony.Patch(closed, new HarmonyMethod(typeof(SteamBridgePlugin), "HmWrapperPrefix"));
                        ok++;
                    }
                    catch (Exception e) { bad++; PtLog("HOSTMARKER patch failed " + gn + "/" + wn + ": " + e.GetType().Name + " " + e.Message); }
                }
            }
            try { discoveryHarmony.Patch(AccessTools.Method(typeof(OverheadDisplayDriver), "Init"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "HmOverheadPostfix")); ok++; }
            catch (Exception e) { bad++; PtLog("HOSTMARKER overhead patch failed: " + e.GetType().Name + " " + e.Message); }
            Logger.LogInfo("HOSTMARKER filter hooks=" + ok + " failed=" + bad + " enabled=" + hmEnabled.Value);
            PtLog("HOSTMARKER filter hooks=" + ok + " failed=" + bad + " enabled=" + hmEnabled.Value);
        }

        static bool HmForeign(Faction owner)
        {
            if (hmEnabled == null) return false;
            Faction local = User.LocalUser == null ? null : User.LocalUser.faction;
            bool allied = owner != null && local != null && (owner == local || Affiliation.IsAllied(local, owner));
            return HostMarkerRules.ShouldSkip(hmEnabled.Value, NetworkServer.active, local != null, owner != null, owner != null && owner == local, allied);
        }

        // Replaces the wrapper by "no wrapper": returns the node unchanged, so the work itself runs exactly as before, only without the outline.
        static bool HmWrapperPrefix(Node node, Labor labor, ref Node __result)
        {
            try
            {
                Faction owner = labor == null ? null : labor.faction;
                if (HmForeign(owner))
                {
                    hmSkipped++; __result = node;
                    if (hmLogged < 20) { hmLogged++; PtLog("HOSTMARKER skipped outline of faction " + owner.ID + " (local " + User.LocalUser.faction.ID + ") total skipped " + hmSkipped); }
                    return false;
                }
                hmAllowed++;
            }
            catch (Exception) { }
            return true;
        }

        static void HmOverheadPostfix(OverheadDisplayDriver __instance)
        {
            try
            {
                DigGoal g = __instance.GetComponent<DigGoal>();
                if (g == null || __instance.overheadDisplay == null) return;
                if (HmForeign(g.faction)) { __instance.overheadDisplay.gameObject.SetActive(false); hmOverheadHidden++; if (hmLogged < 20) { hmLogged++; PtLog("HOSTMARKER hid counter of faction " + g.faction.ID + " dig goal"); } }
            }
            catch (Exception) { }
        }
    }
}
