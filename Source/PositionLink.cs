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
    // 0.3.83 (B-009): the entry also carries the unit's rotation (0xB8 packets, 0.3.83+ guests only; older guests get the 0xB7 packet as before),
    // so position and facing travel in one update. The guest writes UNetTransform.LastServerRotation the same way as the position.
    public sealed partial class SteamBridgePlugin
    {
        const int PosChannel = 78, PosMaxEntries = PosWire.MaxEntries; const byte PosMagic = PosWire.MagicPos;
        const float PosFullEvery = 1.0f; static float PosInterval = 0.05f; ConfigEntry<int> posRateCfg;   // 0.3.83: 20 Hz default (was 10 Hz) so guests can buffer less
        ConfigEntry<bool> posSideChannel, rotSideChannel;
        static Func<UNetTransform, Quaternion> getServerRot; static Action<UNetTransform, Quaternion> setServerRot;
        static readonly List<UNetTransform> posRegistry = new List<UNetTransform>();
        sealed class PosEntry { internal uint Id; internal Vector3 Last; internal Quaternion LastRot; internal float SentAt; internal NetworkIdentity Ni; }
        readonly Dictionary<UNetTransform, PosEntry> posSent = new Dictionary<UNetTransform, PosEntry>();
        readonly Dictionary<uint, UNetTransform> posLookup = new Dictionary<uint, UNetTransform>();
        float nextPosSend, nextPosScan; uint posSeq; readonly SideSeq posSeqIn = new SideSeq(); CSteamID posSeqFrom;
        readonly byte[] posBuf = new byte[PosWire.Header + PosMaxEntries * PosWire.EntryPos], rotBuf = new byte[PosWire.Header + PosMaxEntries * PosWire.EntryPosRot]; byte[] posRecv = new byte[2048];
        readonly List<PosEntry> posBatch = new List<PosEntry>(); readonly List<UNetTransform> posBatchT = new List<UNetTransform>();
        int posPacketsOut, posPacketsIn, posApplied, rotApplied, posRotPacketsIn, posDroppedTest, posLost, posLate, posWinLost, posWinGot; float nextPosLog;
        sealed class PosHeld { internal CSteamID To; internal byte[] Data; internal int Length; internal float Due; }
        readonly List<PosHeld> posHeld = new List<PosHeld>();

        void InstallPositionLink()
        {
            try
            {
                posSideChannel = Config.Bind("Network", "PositionSideChannel", true, "Send unit positions on a separate unreliable Steam channel so a delayed or lost packet in the main stream does not freeze units. Both players need this version.");
                MethodInfo awake = typeof(UNetTransform).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (awake == null) { Logger.LogWarning("Position side channel: UNetTransform.Awake not found."); return; }
                posRateCfg = Config.Bind("Network", "PositionRateHz", 20, "Host: how many times per second unit positions go out on the side channel (5-30). Higher = guests can show units with less delay; a bit more upload.");
                PosInterval = 1f / Mathf.Clamp(posRateCfg.Value, 5, 30);
                rotSideChannel = Config.Bind("Network", "RotationSideChannel", true, "Host: also send each unit's facing on the side channel (to 0.3.83+ guests) so units do not walk backwards while the main stream is slow. Guests accept it automatically.");
                FieldInfo rotField = typeof(UNetTransform).GetField("LastServerRotation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (rotField != null) MakeRotAccessors(rotField); else Logger.LogWarning("Position side channel: LastServerRotation not found, rotation stays on the game sync.");
                discoveryHarmony.Patch(awake, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("AfterTransformAwake", BindingFlags.Static | BindingFlags.NonPublic)));
                InstallPositionDiag(awake.DeclaringType); Logger.LogInfo("Position side channel ready (Steam channel " + PosChannel + ", unreliable, " + (posSideChannel.Value ? "on" : "off") + ").");
            }
            catch (Exception e) { Logger.LogWarning("Position side channel unavailable: " + e.Message); }
        }

        static void MakeRotAccessors(FieldInfo f)
        {
            System.Reflection.Emit.DynamicMethod get = new System.Reflection.Emit.DynamicMethod("csb_get_rot", typeof(Quaternion), new Type[] { typeof(UNetTransform) }, typeof(UNetTransform), true);
            System.Reflection.Emit.ILGenerator g = get.GetILGenerator();
            g.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); g.Emit(System.Reflection.Emit.OpCodes.Ldfld, f); g.Emit(System.Reflection.Emit.OpCodes.Ret);
            getServerRot = (Func<UNetTransform, Quaternion>)get.CreateDelegate(typeof(Func<UNetTransform, Quaternion>));
            System.Reflection.Emit.DynamicMethod set = new System.Reflection.Emit.DynamicMethod("csb_set_rot", typeof(void), new Type[] { typeof(UNetTransform), typeof(Quaternion) }, typeof(UNetTransform), true);
            g = set.GetILGenerator();
            g.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); g.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); g.Emit(System.Reflection.Emit.OpCodes.Stfld, f); g.Emit(System.Reflection.Emit.OpCodes.Ret);
            setServerRot = (Action<UNetTransform, Quaternion>)set.CreateDelegate(typeof(Action<UNetTransform, Quaternion>));
        }

        static Q4 ToQ4(Quaternion q) { return new Q4(q.x, q.y, q.z, q.w); }
        static Quaternion FromQ4(Q4 q) { return new Quaternion(q.X, q.Y, q.Z, q.W); }

        static void AfterTransformAwake(UNetTransform __instance) { try { if (__instance != null) posRegistry.Add(__instance); } catch (Exception) { } }

        bool PosPeerReady(CSteamID id) { string v; return peerVersions.TryGetValue(id, out v) && UpdateVerifier.IsNewer(v, "0.3.51"); }
        bool RotPeerReady(CSteamID id) { string v; return rotSideChannel != null && rotSideChannel.Value && getServerRot != null && peerVersions.TryGetValue(id, out v) && PosWire.PeerWantsRotation(v, UpdateVerifier.IsNewer); }

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
                    Logger.LogInfo("Position side channel: sent " + posPacketsOut + " packets, received " + posPacketsIn + " (with rotation " + posRotPacketsIn + ", rotations applied " + rotApplied + ", seq restarts " + posSeqIn.Restarts + "), lost " + posLost + ", late " + posLate + " (" + (posPacketsIn + posLost > 0 ? (100f * posLost / (posPacketsIn + posLost)).ToString("F1") : "0") + "%), applied " + posApplied + " positions" + (posDroppedTest > 0 ? ", test-dropped " + posDroppedTest : ""));
                }
            }
            catch (Exception e) { if (now >= nextPosScan) { nextPosScan = now + 10f; Logger.LogWarning("Position side channel: " + e.Message); } }
        }

        void PosHostSend(float now)
        {
            if (now < nextPosSend) return; nextPosSend = now + PosInterval;
            bool any = false; foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (PosPeerReady(g.Key)) { any = true; break; }
            if (!any) return;
            bool sendRot = false; foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (PosPeerReady(g.Key) && RotPeerReady(g.Key)) { sendRot = true; break; }
            posBatch.Clear(); posBatchT.Clear();
            for (int i = posRegistry.Count - 1; i >= 0; i--)
            {
                UNetTransform t = posRegistry[i];
                if (t == null) { posRegistry.RemoveAt(i); continue; }
                PosEntry e;
                if (!posSent.TryGetValue(t, out e)) { e = new PosEntry(); e.Ni = t.GetComponent<NetworkIdentity>(); posSent[t] = e; e.SentAt = -100f; e.Last = new Vector3(float.NaN, 0, 0); e.LastRot = new Quaternion(0, 0, 0, 0); }
                if (e.Ni == null || e.Ni.netId.Value == 0 || t.UpdateOnlyOnSleep) continue;
                Vector3 p = t.transform.position; Quaternion q = t.transform.rotation;
                bool turned = sendRot && Quaternion.Angle(q, e.LastRot) > RotationRules.SendAngleDeg;
                if (p != e.Last || turned || now - e.SentAt > PosFullEvery) { e.Id = e.Ni.netId.Value; e.Last = p; e.LastRot = q; e.SentAt = now; posBatch.Add(e); }
            }
            if (posSent.Count > posRegistry.Count + 64) { List<UNetTransform> dead = new List<UNetTransform>(); foreach (UNetTransform k in posSent.Keys) if (k == null) dead.Add(k); for (int i = 0; i < dead.Count; i++) posSent.Remove(dead[i]); }
            if (posBatch.Count == 0) return;
            for (int from = 0; from < posBatch.Count; from += PosMaxEntries)
            {
                int n = Math.Min(PosMaxEntries, posBatch.Count - from); int o = 0, r = 0;
                posSeq++; ushort stamp = SmHostStamp();
                posBuf[o++] = PosMagic; posBuf[o++] = (byte)n; WriteU32(posBuf, ref o, posSeq); posBuf[o++] = (byte)stamp; posBuf[o++] = (byte)(stamp >> 8);
                rotBuf[r++] = PosWire.MagicPosRot; rotBuf[r++] = (byte)n; WriteU32(rotBuf, ref r, posSeq); rotBuf[r++] = (byte)stamp; rotBuf[r++] = (byte)(stamp >> 8);
                for (int i = 0; i < n; i++)
                {
                    PosEntry e = posBatch[from + i]; WriteU32(posBuf, ref o, e.Id); WriteF(posBuf, ref o, e.Last.x); WriteF(posBuf, ref o, e.Last.y); WriteF(posBuf, ref o, e.Last.z);
                    if (sendRot) { WriteU32(rotBuf, ref r, e.Id); WriteF(rotBuf, ref r, e.Last.x); WriteF(rotBuf, ref r, e.Last.y); WriteF(rotBuf, ref r, e.Last.z); WriteU32(rotBuf, ref r, RotPack.Pack(ToQ4(e.LastRot))); }
                }
                foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (PosPeerReady(g.Key)) { if (sendRot && RotPeerReady(g.Key)) PosSend(g.Key, rotBuf, r); else PosSend(g.Key, posBuf, o); }
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
            if (testDelaySeconds > 0f || testJitterSeconds > 0f) { PosHeld h = new PosHeld(); h.To = to; h.Length = length; h.Data = new byte[length]; Buffer.BlockCopy(data, 0, h.Data, 0, length); h.Due = now + testDelaySeconds + (float)testRng.NextDouble() * testJitterSeconds; int at = posHeld.Count; while (at > 0 && posHeld[at - 1].Due > h.Due) at--; posHeld.Insert(at, h); return true; }   // jitter reorders unreliable packets like a real network
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

        // 0.4.27 verify (A-03-v): start of a guest session. Resetting the tracker alone is not enough. The host keeps sending position packets until it gets the
        // goodbye (one round trip after an F8 leave, up to 6 s = SessionGoneSeconds after a leave through the game's menu); this PC no longer reads channel 78 then
        // and nothing else ever reads it, so those packets wait in Steam's queue. The first packet of the new session would be one of them, and the jump from its
        // number to the host's current one (the host counted on for the other guests) would be counted as loss all the same. Everything queued now is older than
        // this session: drop it, so the first packet the host sends to this session is the baseline.
        void PosNewSession()
        {
            posSeqIn.Reset();
            try { uint size, read; CSteamID from; for (int i = 0; i < 4096 && SteamNetworking.IsP2PPacketAvailable(out size, PosChannel); i++) SteamNetworking.ReadP2PPacket(posRecv, (uint)posRecv.Length, out read, out from, PosChannel); }
            catch (Exception) { }
        }

        void PosClientReceive()
        {
            uint size;
            for (int pk = 0; pk < 256 && SteamNetworking.IsP2PPacketAvailable(out size, PosChannel); pk++)
            {
                if (size > posRecv.Length) posRecv = new byte[size];
                uint read; CSteamID sender;
                if (!SteamNetworking.ReadP2PPacket(posRecv, size, out read, out sender, PosChannel)) continue;
                if (sender != peer || !PosWire.Valid(posRecv, (int)read)) continue;
                if (sender != posSeqFrom) { posSeqIn.Reset(); posSeqFrom = sender; }   // another host (reconnect elsewhere): its numbers are unrelated
                posPacketsIn++; dgSidePk++; bool rotFormat = posRecv[0] == PosWire.MagicPosRot, withRot = rotFormat && setServerRot != null; if (rotFormat) posRotPacketsIn++;
                int o = 2; uint seq = ReadU32(posRecv, ref o); ushort stamp = (ushort)(posRecv[6] | (posRecv[7] << 8)); o += 2;
                bool first = !posSeqIn.Known; int gap = posSeqIn.Next(seq);
                if (!first)
                {
                    if (gap <= 0) { posLate++; SmOnPacket(posRecv, (int)read, gap, stamp); continue; }   // an older packet that arrived late: only the smoothing history may use it to fill a gap (stale data never overwrites newer)
                    if (gap > 1) { posLost += gap - 1; posWinLost += gap - 1; }   // sequence numbers that never arrived = lost on the way
                }
                posWinGot++;
                if (SmOnPacket(posRecv, (int)read, gap, stamp)) continue;
                int n = posRecv[1];
                for (int i = 0; i < n; i++)
                {
                    uint id = ReadU32(posRecv, ref o); float x = ReadF(posRecv, ref o), y = ReadF(posRecv, ref o), z = ReadF(posRecv, ref o); uint packed = rotFormat ? ReadU32(posRecv, ref o) : 0u;
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
                    if (withRot) { Quaternion q = FromQ4(RotPack.Unpack(packed)); setServerRot(t, q); rotApplied++; RotRecord(t, q); }
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
