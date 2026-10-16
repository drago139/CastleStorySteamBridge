using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Game.Network;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.52 diagnostics for the position side channel.
    //  * A game position write (OnDeserialize) that lands within 0.5 s of a side-channel write and differs from it is an "overwrite":
    //    the stock sync replaced a fresher coordinate. With PositionSideChannelWins=true (default) the side-channel value is put back (a "rejection");
    //    after 0.5 s without side-channel data the game's value is trusted again.
    //  * With [Test] Enabled the plugin writes BepInEx\unit-trace.txt: TRC lines with the rendered x,z of every unit every 250 ms (UTC grid) and PST lines
    //    once a second (side packets, applied, game writes, overwrites, rejections), so host and guest traces can be compared offline.
    public sealed partial class SteamBridgePlugin
    {
        const float PosFreshSeconds = 0.5f;
        sealed class SideRec { internal Vector3 Val; internal float T; }
        static readonly Dictionary<UNetTransform, SideRec> sideRecs = new Dictionary<UNetTransform, SideRec>();
        static bool posWins = true;
        static int dgSidePk, dgSideApplied, dgGameWrites, dgOverwrites, dgRejected; static double dgOverDist;
        ConfigEntry<bool> posWinsCfg;
        StreamWriter traceW; long nextTraceMs; float nextPst; int pstSidePk, pstApplied, pstGame, pstOver, pstRej; double pstDist;
        readonly Dictionary<UNetTransform, NetworkIdentity> trcNi = new Dictionary<UNetTransform, NetworkIdentity>();
        readonly Dictionary<UNetTransform, bool> trcUnit = new Dictionary<UNetTransform, bool>();

        void InstallPositionDiag(Type transformType)
        {
            posWinsCfg = Config.Bind("Network", "PositionSideChannelWins", true, "When the side channel delivered a unit position in the last 0.5 s, a different position from the game's own sync is ignored (it is older).");
            posWins = posWinsCfg.Value;
            MethodInfo des = transformType.GetMethod("OnDeserialize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (des != null) discoveryHarmony.Patch(des, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AfterTransformDeserialize", BindingFlags.Static | BindingFlags.NonPublic)));
        }

        static void PosRecord(UNetTransform t, Vector3 v)
        {
            SideRec r; if (!sideRecs.TryGetValue(t, out r)) { r = new SideRec(); sideRecs[t] = r; }
            r.Val = v; r.T = Time.realtimeSinceStartup; dgSideApplied++;
            if (sideRecs.Count > 2048) { List<UNetTransform> dead = new List<UNetTransform>(); foreach (UNetTransform k in sideRecs.Keys) if (k == null) dead.Add(k); for (int i = 0; i < dead.Count; i++) sideRecs.Remove(dead[i]); }
        }

        static void AfterTransformDeserialize(UNetTransform __instance, bool initialState)
        {
            try
            {
                if (initialState || NetworkServer.active || getServerPos == null) return;
                SideRec r; if (!sideRecs.TryGetValue(__instance, out r)) return;
                Vector3 cur = getServerPos(__instance); if (cur == r.Val) return;
                dgGameWrites++;
                if (Time.realtimeSinceStartup - r.T < PosFreshSeconds)
                {
                    dgOverwrites++; dgOverDist += (cur - r.Val).magnitude;
                    if (posWins) { setServerPos(__instance, r.Val); dgRejected++; }
                }
            }
            catch (Exception) { }
        }

        void TraceTick(float now)
        {
            if (!TestOn) return;
            long ms = (long)(DateTime.UtcNow - ActEpoch).TotalMilliseconds;
            if (traceW == null) { try { traceW = new StreamWriter(Path.Combine(BepInEx.Paths.BepInExRootPath, "unit-trace.txt"), false); } catch (Exception) { return; } }
            if (nextTraceMs == 0) nextTraceMs = (ms / 250 + 1) * 250;
            if (ms >= nextTraceMs)
            {
                long stamp = nextTraceMs; nextTraceMs = (ms / 250 + 1) * 250;
                System.Text.StringBuilder sb = new System.Text.StringBuilder("TRC ").Append(stamp);
                for (int i = 0; i < posRegistry.Count; i++)
                {
                    UNetTransform t = posRegistry[i]; if (t == null) continue;
                    bool unit; if (!trcUnit.TryGetValue(t, out unit)) { unit = t.GetComponent<Brix.Game.AI.Labor>() != null; trcUnit[t] = unit; }
                    if (!unit) continue;
                    NetworkIdentity ni; if (!trcNi.TryGetValue(t, out ni)) { ni = t.GetComponent<NetworkIdentity>(); trcNi[t] = ni; }
                    if (ni == null || ni.netId.Value == 0) continue;
                    Vector3 p = t.transform.position; sb.Append(' ').Append(ni.netId.Value).Append(':').Append(p.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(p.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                traceW.WriteLine(sb.ToString());
            }
            if (now >= nextPst)
            {
                nextPst = now + 1f;
                traceW.WriteLine("PST " + ms + " side=" + (dgSidePk - pstSidePk) + " applied=" + (dgSideApplied - pstApplied) + " game=" + (dgGameWrites - pstGame) + " over=" + (dgOverwrites - pstOver) + " rej=" + (dgRejected - pstRej) + " overDist=" + (dgOverDist - pstDist).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                pstSidePk = dgSidePk; pstApplied = dgSideApplied; pstGame = dgGameWrites; pstOver = dgOverwrites; pstRej = dgRejected; pstDist = dgOverDist;
                traceW.Flush();
            }
        }
    }
}
