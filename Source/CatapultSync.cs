using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Game;
using Brix.Game.Components.CatapultSpace;
using Brix.Game.Network;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.77 log-only catapult sync diagnostics (Task 2). Nothing here changes aiming, commands, SyncVars or timing.
    //   CAT-CMD send   guest: CmdSetTarget leaves the client (seq, target, wall clock + game time).
    //   CAT-CMD recv   host: CmdSetTarget arrives (sender faction, target) and what the host computed from it (rotationVoulue/crinqueVoulue/forceVoulue).
    //   CAT-SYNC       every machine except the server: a CatapultControl/CatapultBody SyncVar update arrived (running update number + received values).
    //   CAT-DRIFT      guest: the local CatapultControl.cible differs from the last value the host sent. Catapult.Viser (called from the target picker while
    //                  the mouse hovers) writes cible locally on the client, and the host never re-sends an unchanged value, so a hover value can stay.
    //   CAT-STATE      (SysDiag) rotation / rotationVoulue / crinque / crinqueVoulue / forceVoulue per catapult on every machine for deviation checks.
    public sealed partial class SteamBridgePlugin
    {
        static int csSendSeq, csRecvSeq; static readonly Dictionary<int, int> csUpdates = new Dictionary<int, int>(); static readonly Dictionary<int, Vector3> csLastRecvCible = new Dictionary<int, Vector3>();
        static string CsStamp() { return DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Time.realtimeSinceStartup.ToString("F2"); }
        static string CsV(Vector3 v) { return v.x.ToString("F1") + "," + v.y.ToString("F1") + "," + v.z.ToString("F1"); }

        static void InstallCatapultSyncDiag(Harmony h)
        {
            int ok = 0, bad = 0;
            try { h.Patch(AccessTools.Method(typeof(UNetCatapult), "CallCmdSetTarget"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "CsSend")); ok++; } catch (Exception) { bad++; }
            try { h.Patch(AccessTools.Method(typeof(UNetCatapult), "CmdSetTarget"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "CsRecv")); ok++; } catch (Exception) { bad++; }
            try { h.Patch(AccessTools.Method(typeof(CatapultControl), "OnDeserialize"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "CsControlSync")); ok++; } catch (Exception) { bad++; }
            try { h.Patch(AccessTools.Method(typeof(CatapultBody), "OnDeserialize"), null, new HarmonyMethod(typeof(SteamBridgePlugin), "CsBodySync")); ok++; } catch (Exception) { bad++; }
            PtLog("CAT-SYNC-DIAG hooks=" + ok + " failed=" + bad);
        }

        static void CsSend(GameObject instance, Vector3 target, Vector3 normal)
        {
            try { csSendSeq++; PtLog("CAT-CMD send #" + csSendSeq + " " + CsStamp() + " catapult=" + (instance == null ? "null" : instance.name + "/" + instance.GetInstanceID()) + " target=" + CsV(target) + " role=" + (NetworkServer.active ? "host" : "guest")); } catch (Exception) { }
        }

        static void CsRecv(UNetCatapult __instance, GameObject instance, Vector3 target, Vector3 normal)
        {
            try
            {
                csRecvSeq++;
                User u = __instance.GetComponent<User>(); string fac = u == null || u.faction == null ? "?" : u.faction.ID.ToString();
                Catapult c = instance == null ? null : instance.GetComponent<Catapult>();
                CatapultControl ctl = instance == null ? null : instance.GetComponent<CatapultControl>(); CatapultBody b = ctl == null ? null : ctl.body;
                string owner = "?"; if (instance != null) { Brix.Game.Components.AffiliationComponent a = instance.GetComponent<Brix.Game.Components.AffiliationComponent>(); if (a != null && a.faction != null) owner = a.faction.ID.ToString(); }
                PtLog("CAT-CMD recv #" + csRecvSeq + " " + CsStamp() + " catapult=" + (instance == null ? "null" : instance.name + "/" + instance.GetInstanceID()) + " senderFaction=" + fac + " catapultFaction=" + owner + " target=" + CsV(target)
                    + (b == null ? "" : " -> rotVoulue=" + b.rotationVoulue.ToString("F2") + " crinqueVoulue=" + b.crinqueVoulue.ToString("F1") + " forceVoulue=" + b.forceVoulue.ToString("F1") + " rot=" + b.rotation.ToString("F2") + " cibleShown=" + b.afficherCible)
                    + (fac != owner && owner != "?" && fac != "?" ? " NOTE sender faction differs from catapult faction" : ""));
            }
            catch (Exception) { }
        }

        static void CsControlSync(CatapultControl __instance)
        {
            try
            {
                if (NetworkServer.active) return; int id = __instance.GetInstanceID(); int n; csUpdates.TryGetValue(id, out n); n++; csUpdates[id] = n;
                Vector3 prev; bool had = csLastRecvCible.TryGetValue(id, out prev); csLastRecvCible[id] = __instance.cible;
                PtLog("CAT-SYNC ctl " + CsStamp() + " " + __instance.name + "/" + id + " update#" + n + " cible=" + CsV(__instance.cible) + " feu=" + __instance.feu + (had && prev != __instance.cible ? " (cible changed by host)" : ""));
            }
            catch (Exception) { }
        }

        static void CsBodySync(CatapultBody __instance)
        {
            try
            {
                if (NetworkServer.active) return; int id = __instance.GetInstanceID(); int n; csUpdates.TryGetValue(id, out n); n++; csUpdates[id] = n;
                PtLog("CAT-SYNC body " + CsStamp() + " " + __instance.name + "/" + id + " update#" + n + " rotVoulue=" + __instance.rotationVoulue.ToString("F2") + " (local rot " + __instance.rotation.ToString("F2") + ") crinqueVoulue=" + __instance.crinqueVoulue.ToString("F1") + " crinque=" + __instance.crinque.ToString("F1") + " cooldown=" + __instance.cooldown.ToString("F1"));
            }
            catch (Exception) { }
        }

        // Called from the 1 s CAT-STATE scan: guest-side drift between the local cible and the last value the host sent.
        static string CsDrift(CatapultControl c)
        {
            if (NetworkServer.active || !NetworkClient.active) return "";
            Vector3 last; if (!csLastRecvCible.TryGetValue(c.GetInstanceID(), out last)) return "";
            return last != c.cible ? " CAT-DRIFT(local cible " + CsV(c.cible) + " != host-sent " + CsV(last) + ")" : "";
        }
    }
}
