using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Brix.External.Factories;
using Brix.Game.Semantique;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.8 (log only): a stockpile crate (Palette) must hold ONE resource type. The game narrows a crate's capacity to the type of
    // the first item (Recepteur.AddTo / DealSingleResourceRecepteur), so a crate with two types means an item was added past that rule
    // (a batch add, a replicated re-parent, a save restore ...). This records the first few such crates with the stack of whatever
    // tracked the foreign item, on whichever PC the bridge runs (the host is the one that matters). It changes nothing in the game.
    public sealed partial class SteamBridgePlugin
    {
        int crateMixLogged;
        const int CrateMixLogLimit = 10;
        readonly Dictionary<int, float> crateMixSeen = new Dictionary<int, float>();

        void PatchCrateGuard()
        {
            try
            {
                MethodInfo tracked = typeof(Recepteur).GetMethod("OnObjectTracked", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo after = typeof(SteamBridgePlugin).GetMethod("AfterObjectTracked", BindingFlags.Static | BindingFlags.NonPublic);
                if (tracked == null || after == null) { Logger.LogWarning("Crate guard hook not found; mixed crates will not be logged."); return; }
                discoveryHarmony.Patch(tracked, null, new HarmonyMethod(after));
            }
            catch (Exception e) { Logger.LogWarning("Crate guard unavailable: " + e.Message); }
        }

        static void AfterObjectTracked(Recepteur __instance, GameObject itemGO)
        {
            SteamBridgePlugin plugin = discoveryInstance;
            if (plugin != null) plugin.CheckCrateMix(__instance, itemGO);
        }

        static string ResourceName(GameObject go)
        {
            if (go == null) return "?";
            string n = go.name;
            int cut = n.IndexOf(" (", StringComparison.Ordinal);
            return cut > 0 ? n.Substring(0, cut) : n;
        }

        void CheckCrateMix(Recepteur crate, GameObject added)
        {
            try
            {
                if (crateMixLogged >= CrateMixLogLimit || crate == null || crate.AssetKey != ObjetsDynamiques.Palette) return;
                if (crate.StoredItems == null || crate.StoredItems.Count < 2) return;
                Dictionary<string, int> kinds = new Dictionary<string, int>();
                foreach (Brix.Game.Semantique.IDescriptor item in crate.StoredItems)
                {
                    if (item == null) continue;
                    string name = ResourceName(item.GameObject);
                    int c; kinds.TryGetValue(name, out c); kinds[name] = c + 1;
                }
                if (kinds.Count < 2) return;
                int id = crate.GetInstanceID();
                float now = Time.realtimeSinceStartup, last;
                if (crateMixSeen.TryGetValue(id, out last) && now - last < 60) return;   // one report per crate per minute
                crateMixSeen[id] = now; crateMixLogged++;
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, int> k in kinds) { if (sb.Length > 0) sb.Append(", "); sb.Append(k.Key).Append(" x").Append(k.Value); }
                Logger.LogWarning("Mixed crate #" + crateMixLogged + ": " + crate.name + " at " + crate.transform.position + " holds [" + sb + "]; just added "
                    + ResourceName(added) + "; server=" + NetworkServer.active + " client=" + NetworkClient.active
                    + "; capacity now: " + crate.CurrentCapacity + "\nstack:\n" + new System.Diagnostics.StackTrace(2, false));
            }
            catch (Exception e) { Logger.LogWarning("Crate guard failed: " + e.Message); }
        }
    }
}
