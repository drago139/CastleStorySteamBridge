using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Game.Network;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.52: unit positions on their own Steam channel. The bridge tunnels every UNet datagram through one ordered, reliable Steam
    // stream, so a big message (building, voxels) or one lost packet delays the position updates queued behind it and units stand
    // still while their animation keeps running. Here the host reads the position of every UNetTransform ten times a second and sends
    // the changed ones as small UNRELIABLE packets on Steam channel 78 (independent of the stream); the guest writes them straight into
    // UNetTransform.LastServerPosition, which the game already interpolates toward. Stale or lost packets simply do not matter, the
    // next one replaces them. The game's own updates keep flowing as before, so nothing breaks if a peer has no side channel.
    public sealed partial class SteamBridgePlugin
    {
        const int PosChannel = 78, PosMaxEntries = 70; const byte PosMagic = 0xB7;
        const float PosInterval = 0.1f, PosFullEvery = 1.0f;
        ConfigEntry<bool> posSideChannel;
        static readonly List<UNetTransform> posRegistry = new List<UNetTransform>();
        sealed class PosEntry { internal uint Id; internal Vector3 Last; internal float SentAt; internal NetworkIdentity Ni; }
        readonly Dictionary<UNetTransform, PosEntry> posSent = new Dictionary<UNetTransform, PosEntry>();
        readonly Dictionary<uint, UNetTransform> posLookup = new Dictionary<uint, UNetTransform>();
        float nextPosSend, nextPosScan; uint posSeq, posLastSeq; bool posSeqKnown;
        readonly byte[] posBuf = new byte[8 + PosMaxEntries * 16]; byte[] posRecv = new byte[2048];
        readonly List<PosEntry> posBatch = new List<PosEntry>(); readonly List<UNetTransform> posBatchT = new List<UNetTransform>();
        int posPacketsOut, posPacketsIn, posApplied, posDroppedTest; float nextPosLog;
        sealed class PosHeld { internal CSteamID To; internal byte[] Data; internal int Length; internal float Due; }
        readonly List<PosHeld> posHeld = new List<PosHeld>();

        void InstallPositionLink()
        {
            try
            {
                posSideChannel = Config.Bind("Network", "PositionSideChannel", true, "Send unit positions on a separate unreliable Steam channel so a delayed or lost packet in the main stream does not freeze units. Both players need this version.");
                MethodInfo awake = typeof(UNetTransform).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (awake == null) { Logger.LogWarning("Position side channel: UNetTransform.Awake not found."); return; }
                discoveryHarmony.Patch(awake, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AfterTransformAwake", BindingFlags.Static | BindingFlags.NonPublic)));
                InstallPositionDiag(awake.DeclaringType); Logger.LogInfo("Position side channel ready (Steam channel " + PosChannel + ", unreliable, " + (posSideChannel.Value ? "on" : "off") + ").");
            }
            catch (Exception e) { Logger.LogWarning("Position side channel unavailable: " + e.Message); }
        }

        static void AfterTransformAwake(UNetTransform __instance) { try { if (__instance != null) posRegistry.Add(__instance); } catch (Exception) { } }

        bool PosPeerReady(CSteamID id) { string v; return peerVersions.TryGetValue(id, out v) && UpdateVerifier.IsNewer(v, "0.3.51"); }

        void PositionLinkTick()
        {
            float now = Time.realtimeSinceStartup;
            try { TraceTick(now); } catch (Exception) { }
            if (posSideChannel == null || !posSideChannel.Value || getServerPos == null) return;
            PosFlush(now);
            try
            {
                if (hosting && NetworkServer.active) PosHostSend(now);
                else if (!hosting && bridge != null && NetworkClient.active) PosClientReceive();
                if (now >= nextPosLog && (posPacketsOut > 0 || posPacketsIn > 0))
                {
                    nextPosLog = now + 30f;
                    Logger.LogInfo("Position side channel: sent " + posPacketsOut + " packets, received " + posPacketsIn + ", applied " + posApplied + " positions" + (posDroppedTest > 0 ? ", test-dropped " + posDroppedTest : ""));
                }
            }
            catch (Exception e) { if (now >= nextPosScan) { nextPosScan = now + 10f; Logger.LogWarning("Position side channel: " + e.Message); } }
        }

        void PosHostSend(float now)
        {
            if (now < nextPosSend) return; nextPosSend = now + PosInterval;
            bool any = false; foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (PosPeerReady(g.Key)) { any = true; break; }
            if (!any) return;
            posBatch.Clear(); posBatchT.Clear();
            for (int i = posRegistry.Count - 1; i >= 0; i--)
            {
                UNetTransform t = posRegistry[i];
                if (t == null) { posRegistry.RemoveAt(i); continue; }
                PosEntry e;
                if (!posSent.TryGetValue(t, out e)) { e = new PosEntry(); e.Ni = t.GetComponent<NetworkIdentity>(); posSent[t] = e; e.SentAt = -100f; e.Last = new Vector3(float.NaN, 0, 0); }
                if (e.Ni == null || e.Ni.netId.Value == 0 || t.UpdateOnlyOnSleep) continue;
                Vector3 p = t.transform.position;
                if (p != e.Last || now - e.SentAt > PosFullEvery) { e.Id = e.Ni.netId.Value; e.Last = p; e.SentAt = now; posBatch.Add(e); }
            }
            if (posSent.Count > posRegistry.Count + 64) { List<UNetTransform> dead = new List<UNetTransform>(); foreach (UNetTransform k in posSent.Keys) if (k == null) dead.Add(k); for (int i = 0; i < dead.Count; i++) posSent.Remove(dead[i]); }
            if (posBatch.Count == 0) return;
            for (int from = 0; from < posBatch.Count; from += PosMaxEntries)
            {
                int n = Math.Min(PosMaxEntries, posBatch.Count - from); int o = 0;
                posBuf[o++] = PosMagic; posBuf[o++] = (byte)n; posSeq++; WriteU32(posBuf, ref o, posSeq); posBuf[o++] = 0; posBuf[o++] = 0;
                for (int i = 0; i < n; i++) { PosEntry e = posBatch[from + i]; WriteU32(posBuf, ref o, e.Id); WriteF(posBuf, ref o, e.Last.x); WriteF(posBuf, ref o, e.Last.y); WriteF(posBuf, ref o, e.Last.z); }
                foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (PosPeerReady(g.Key)) PosSend(g.Key, posBuf, o);
            }
        }

        void PosSend(CSteamID to, byte[] data, int length)
        {
            if (PosShape(to, data, length)) return;
            if (SteamNetworking.SendP2PPacket(to, data, (uint)length, EP2PSend.k_EP2PSendUnreliable, PosChannel)) posPacketsOut++;
        }

        // Test hooks: a stall, drop or loss in the test panel also removes side-channel packets (unreliable ones are lost, not retried).
        bool PosShape(CSteamID to, byte[] data, int length)
        {
            if (!TestOn) return false;
            float now = Time.realtimeSinceStartup;
            if (now < testStallUntil || now < testDropUntil || (testLossPercent > 0f && testRng.NextDouble() * 100.0 < testLossPercent)) { posDroppedTest++; return true; }
            if (testDelaySeconds > 0f) { PosHeld h = new PosHeld(); h.To = to; h.Length = length; h.Data = new byte[length]; Buffer.BlockCopy(data, 0, h.Data, 0, length); h.Due = now + testDelaySeconds; posHeld.Add(h); return true; }
            return false;
        }

        void PosFlush(float now)
        {
            while (posHeld.Count > 0 && posHeld[0].Due <= now)
            {
                PosHeld h = posHeld[0]; posHeld.RemoveAt(0);
                if (SteamNetworking.SendP2PPacket(h.To, h.Data, (uint)h.Length, EP2PSend.k_EP2PSendUnreliable, PosChannel)) posPacketsOut++;
            }
        }

        void PosClientReceive()
        {
            uint size;
            for (int pk = 0; pk < 256 && SteamNetworking.IsP2PPacketAvailable(out size, PosChannel); pk++)
            {
                if (size > posRecv.Length) posRecv = new byte[size];
                uint read; CSteamID sender;
                if (!SteamNetworking.ReadP2PPacket(posRecv, size, out read, out sender, PosChannel)) continue;
                if (sender != peer || read < 8 || posRecv[0] != PosMagic) continue;
                posPacketsIn++; dgSidePk++;
                int o = 2; uint seq = ReadU32(posRecv, ref o); o += 2;
                if (posSeqKnown && (int)(seq - posLastSeq) <= 0) continue;     // an older packet that arrived late
                posSeqKnown = true; posLastSeq = seq;
                int n = posRecv[1]; if (8 + n * 16 > read) continue;
                for (int i = 0; i < n; i++)
                {
                    uint id = ReadU32(posRecv, ref o); float x = ReadF(posRecv, ref o), y = ReadF(posRecv, ref o), z = ReadF(posRecv, ref o);
                    UNetTransform t;
                    if (!posLookup.TryGetValue(id, out t) || t == null)
                    {
                        NetworkIdentity ni;
                        if (ClientScene.objects == null || !ClientScene.objects.TryGetValue(new NetworkInstanceId(id), out ni) || ni == null) continue;
                        t = ni.GetComponent<UNetTransform>(); if (t == null) continue;
                        posLookup[id] = t;
                    }
                    if (t.UpdateOnlyOnSleep) continue;
                    Vector3 sv = new Vector3(x, y, z); setServerPos(t, sv); posApplied++; PosRecord(t, sv);
                }
            }
            if (posLookup.Count > 4096) posLookup.Clear();
        }

        static void WriteU32(byte[] b, ref int o, uint v) { b[o++] = (byte)v; b[o++] = (byte)(v >> 8); b[o++] = (byte)(v >> 16); b[o++] = (byte)(v >> 24); }
        static uint ReadU32(byte[] b, ref int o) { uint v = (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); o += 4; return v; }
        static void WriteF(byte[] b, ref int o, float f) { WriteU32(b, ref o, BitConverter.ToUInt32(BitConverter.GetBytes(f), 0)); }
        static float ReadF(byte[] b, ref int o) { return BitConverter.ToSingle(BitConverter.GetBytes(ReadU32(b, ref o)), 0); }
    }
}
