using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.Components;
using Brix.Game.Network;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.74 B-043 (guest side): in the base game the yellow "to be broken" outline of a dig order is created only where the order lives, on the
    // host. The guest who gave the order never sees it. The game already has the pieces (UNetProjectCmd.RpcAddHighlightOnPosition does
    // Highlighter.highlight.At(pos, yellow)) but never calls them. This guest-only helper draws the same outline locally for the guest's OWN
    // order, at the position the host will use, and removes it when the block is gone, the guest leaves, or after a safety timeout.
    // No network traffic, no host behaviour change. It does not draw the host's pickaxe counter label (that is a host-side overhead object).
    public sealed partial class SteamBridgePlugin
    {
        sealed class GuestDigMark { internal int Request; internal float At; }
        static readonly Dictionary<XYZ, GuestDigMark> guestDigMarks = new Dictionary<XYZ, GuestDigMark>();
        static readonly List<XYZ> guestDigDone = new List<XYZ>();
        static float guestDigNext;
        const float GuestDigMarkSeconds = 600f;

        static bool GuestDigOn { get { return NetworkClient.active && !NetworkServer.active; } }

        static void InstallGuestDigMarker(Harmony harmony)
        {
            var m = AccessTools.Method(typeof(UNetProjectCmd), "CallCmdAddBreakVoxelGoal");
            if (m == null) throw new MissingMethodException(typeof(UNetProjectCmd).FullName, "CallCmdAddBreakVoxelGoal");
            harmony.Patch(m, null, new HarmonyMethod(typeof(SteamBridgePlugin), "GuestDigAdded"));
        }

        static void GuestDigAdded(int index)
        {
            try
            {
                if (!GuestDigOn) return;
                XYZ pos = XYZ.FromScalaire(index);
                if (Voxel.IsEmpty(pos)) pos -= XYZ.up;     // same rule as UNetProjectCmd.CmdAddBreakVoxelGoal on the host
                if (guestDigMarks.ContainsKey(pos)) return;
                if (guestDigMarks.Count >= 512) GuestDigClear();
                int id = Highlighter.highlight.At(pos, Color.yellow);
                guestDigMarks[pos] = new GuestDigMark { Request = id, At = Time.realtimeSinceStartup };
            }
            catch (Exception) { }
        }

        static void GuestDigClear()
        {
            foreach (KeyValuePair<XYZ, GuestDigMark> p in guestDigMarks) { try { Highlighter.remove.Request(p.Value.Request); } catch (Exception) { } }
            guestDigMarks.Clear();
        }

        void GuestDigTick()
        {
            if (guestDigMarks.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < guestDigNext) return;
            guestDigNext = now + 1f;
            try
            {
                if (!GuestDigOn) { GuestDigClear(); return; }
                guestDigDone.Clear();
                foreach (KeyValuePair<XYZ, GuestDigMark> p in guestDigMarks)
                    if (now - p.Value.At > GuestDigMarkSeconds || Voxel.IsEmpty(p.Key)) guestDigDone.Add(p.Key);
                for (int i = 0; i < guestDigDone.Count; i++)
                {
                    try { Highlighter.remove.Request(guestDigMarks[guestDigDone[i]].Request); } catch (Exception) { }
                    guestDigMarks.Remove(guestDigDone[i]);
                }
            }
            catch (Exception) { }
        }
    }
}
