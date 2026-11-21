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
    // 0.3.82 adaptive smoothing of unit positions on the GUEST (host authority unchanged; compatible with PositionLink and Extrapolation).
    // The host stamps every side-channel position packet with its send time (the 2 spare header bytes). The guest keeps a short history per unit
    // and shows each unit at "host time - playout delay" (interpolation). The delay adapts to the measured jitter/loss (rises at once, falls slowly).
    // When data is late a moving unit is extrapolated for a bounded time, then held; a jump > 10 units is a real teleport (snap). The value is
    // written into UNetTransform.LastServerPosition before every BrixUpdate, so the game's own lerp still runs and stale writes from the main stream
    // are overridden every frame. Units without side-channel data (or an old host without stamps) are left to the game exactly as before.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<string> smCfg; static ConfigEntry<int> smExtrapCfg, smMaxCfg;
        static readonly ClockSync smClock = new ClockSync(); static readonly StampUnwrap smUnwrap = new StampUnwrap();
        static readonly Dictionary<UNetTransform, UnitHistory> smHist = new Dictionary<UNetTransform, UnitHistory>(); static readonly Dictionary<UNetTransform, CatchUp> smCatch = new Dictionary<UNetTransform, CatchUp>();
        static double smRenderMs = double.NaN, smLastTickHostMs = -1, smLastFrameMs; static int smFrame = -1, smInterp, smExtrap, smHeld, smUnitsNow;
        static int smInterpWin, smExtrapWin, smHeldWin, smFramesWin; static float smNextLog; static bool smHostStamps;
        static readonly System.Diagnostics.Stopwatch smWatch = System.Diagnostics.Stopwatch.StartNew();
        static double LocalMs { get { return smWatch.Elapsed.TotalMilliseconds; } }

        void InstallAdaptiveSmoothing()
        {
            try
            {
                smCfg = Config.Bind("Network", "Smoothing", "Auto", new ConfigDescription("Guest: unit position smoothing buffer. Auto = adapts to jitter and loss (100-500 ms); Off = 0.3.81 behaviour; 100/200/300/400 = fixed delay in ms. Host authority is not changed.", new AcceptableValueList<string>("Auto", "Off", "100", "200", "300", "400")));
                smExtrapCfg = Config.Bind("Network", "SmoothingExtrapolateMs", 1200, "Guest: when position data is late, keep a moving unit walking for at most this many ms, then hold it.");
                smMaxCfg = Config.Bind("Network", "SmoothingMaxDelayMs", 500, "Guest: upper limit of the automatic smoothing delay.");
                SmApplyConfig();
                MethodInfo update = typeof(UNetTransform).GetMethod("BrixUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (update == null || setServerPos == null) { Logger.LogWarning("SMOOTH: BrixUpdate or position accessor missing; smoothing disabled."); return; }
                HarmonyMethod pre = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("SmBeforeUnitUpdate", BindingFlags.Static | BindingFlags.NonPublic)); pre.priority = Priority.First;
                discoveryHarmony.Patch(update, pre);
                Logger.LogInfo("SMOOTH ready: mode=" + smCfg.Value + " extrapolate<=" + smExtrapCfg.Value + "ms max delay " + smMaxCfg.Value + "ms");
                PtLog("SMOOTH ready mode=" + smCfg.Value);
            }
            catch (Exception e) { Logger.LogWarning("SMOOTH unavailable: " + e.Message); }
        }

        static void SmApplyConfig()
        {
            string v = smCfg == null ? "Auto" : smCfg.Value; int ms;
            smClock.FixedDelayMs = v == "Off" ? 0 : int.TryParse(v, out ms) ? ms : -1;
            if (smMaxCfg != null) smClock.MaxDelayMs = Mathf.Clamp(smMaxCfg.Value, 150, 1000);
        }

        // test-control / virtual player: smooth = -1 auto, 0 off, N fixed
        static void SmSetMode(int value) { smClock.FixedDelayMs = value < 0 ? -1 : value; PtLog("SMOOTH mode set to " + (value < 0 ? "auto" : value == 0 ? "off" : value + " ms")); }

        static ushort SmHostStamp() { ushort s = (ushort)((long)LocalMs & 0xFFFF); return s == 0 ? (ushort)1 : s; }

        // Guest: one side-channel packet. Returns true when the smoothing path consumed the positions (caller must not write them directly).
        bool SmOnPacket(byte[] b, int read, int gap, ushort stamp)
        {
            if (stamp == 0) return false;
            double local = LocalMs;
            // A 16-bit stamp is only unambiguous for gaps < 32 s; after a long pause, or when smoothing was switched on again, start over.
            if (smLastPacketLocal > 0 && local - smLastPacketLocal > 20000.0) SmReset("no position data for " + ((local - smLastPacketLocal) / 1000.0).ToString("F0") + " s");
            if (smWasActive != smClock.Active) { if (smClock.Active) SmReset("smoothing switched on"); smWasActive = smClock.Active; }
            smLastPacketLocal = local;
            double hostMs = smUnwrap.Next(stamp);                      // tracked even while smoothing is off, so the time base never breaks
            smClock.OnPacket(local, hostMs, gap > 0 ? gap : 0);
            SmTrackRate(hostMs, gap);
            if (!smClock.Active) return false;
            smHostStamps = true;
            if (gap > 0 && hostMs > smLastTickHostMs)
            {
                if (gap == 1 && smLastTickHostMs >= 0) foreach (KeyValuePair<UNetTransform, UnitHistory> kv in smHist) kv.Value.Unchanged(smLastTickHostMs);
                smLastTickHostMs = hostMs;
            }
            if (!PosWire.Valid(b, read)) return true; int n = b[1]; int o = 8; bool rotFormat = b[0] == PosWire.MagicPosRot, withRot = rotFormat && setServerRot != null;
            for (int i = 0; i < n; i++)
            {
                uint id = ReadU32(b, ref o); float x = ReadF(b, ref o), y = ReadF(b, ref o), z = ReadF(b, ref o); uint packed = rotFormat ? ReadU32(b, ref o) : 0u;
                UNetTransform t;
                if (!posLookup.TryGetValue(id, out t) || t == null)
                {
                    NetworkIdentity ni;
                    if (ClientScene.objects == null || !ClientScene.objects.TryGetValue(new NetworkInstanceId(id), out ni) || ni == null) continue;
                    t = ni.GetComponent<UNetTransform>(); if (t == null) continue; posLookup[id] = t;
                }
                if (t.UpdateOnlyOnSleep) continue;
                UnitHistory h; if (!smHist.TryGetValue(t, out h)) { h = new UnitHistory(); smHist[t] = h; }
                Q4 q = withRot ? RotPack.Unpack(packed) : Q4.Identity;
                if (h.Add(hostMs, new V3(x, y, z), q, withRot)) { posApplied++; PosRecord(t, new Vector3(x, y, z)); if (withRot) { rotApplied++; RotRecord(t, FromQ4(q)); } }
            }
            if (smHist.Count > 3000) { List<UNetTransform> dead = new List<UNetTransform>(); foreach (UNetTransform k in smHist.Keys) if (k == null) dead.Add(k); for (int i = 0; i < dead.Count; i++) smHist.Remove(dead[i]); }
            return true;
        }

        static double smLastPacketLocal; static bool smWasActive = true; static double smPrevStampMs = -1, smTickEst = 100;
        // 0.3.83: the host send interval is measured from consecutive packets (10 Hz old hosts, 20 Hz new ones); the minimum playout delay follows it.
        static void SmTrackRate(double hostMs, int gap)
        {
            if (gap == 1 && smPrevStampMs >= 0) { double dt = hostMs - smPrevStampMs; if (dt > 20 && dt < 400) { smTickEst = smTickEst * 0.95 + dt * 0.05; smClock.TickMs = (float)smTickEst; smClock.MinDelayMs = Mathf.Clamp((float)smTickEst * 1.4f, 60f, 100f); } }   // 10 Hz host -> 100 ms exactly as in 0.3.82
            if (gap > 0) smPrevStampMs = hostMs;
        }
        static void SmReset(string why)
        {
            smHist.Clear(); smCatch.Clear(); smUnwrap.Reset(); smLastTickHostMs = -1; smClock.Reset(); smPrevStampMs = -1;
            PtLog("SMOOTH reset: " + why);
        }

        internal static bool SmOwns(UNetTransform t) { return smClock.Active && smHostStamps && !NetworkServer.active && t != null && smHist.ContainsKey(t); }

        static void SmBeforeUnitUpdate(UNetTransform __instance)
        {
            try
            {
                if (!smClock.Active || !smHostStamps || NetworkServer.active || __instance == null) return;
                UnitHistory h; if (!smHist.TryGetValue(__instance, out h) || h.Count == 0) return;
                if (Time.frameCount != smFrame)
                {
                    double now = LocalMs; double dt = smFrame < 0 ? 16.0 : now - smLastFrameMs; smLastFrameMs = now; smFrame = Time.frameCount;
                    smRenderMs = smClock.RenderHostMs(now, Math.Max(0.0, Math.Min(dt, 250.0)));
                    smFramesWin++; smInterpWin += smInterp; smExtrapWin += smExtrap; smHeldWin += smHeld; smUnitsNow = smInterp + smExtrap + smHeld; smInterp = smExtrap = smHeld = 0;
                    if (now >= smNextLog * 1000.0) SmLog(now);
                }
                if (double.IsNaN(smRenderMs)) return;
                SampleState st; V3 p = h.Sample(smRenderMs, smExtrapCfg == null ? 1200 : smExtrapCfg.Value, out st);
                if (st == SampleState.None) return;
                if (st == SampleState.Interpolated) smInterp++; else if (st == SampleState.Extrapolated) smExtrap++; else smHeld++;
                CatchUp cu; if (!smCatch.TryGetValue(__instance, out cu)) { cu = new CatchUp(); smCatch[__instance] = cu; }
                p = cu.Step(p, smLastFrameMs);
                setServerPos(__instance, new Vector3(p.X, p.Y, p.Z));
                // 0.3.83: the facing for the same moment as the position (written every frame, so a late value from the main stream never wins)
                Q4 r; if (setServerRot != null && h.SampleRotation(smRenderMs, out r)) { Quaternion q = FromQ4(r); setServerRot(__instance, q); RotRecord(__instance, q); }
            }
            catch (Exception) { }
        }

        static void SmLog(double now)
        {
            smNextLog = (float)(now / 1000.0) + 5f;
            PtLog("SMOOTH delay=" + smClock.DelayMs.ToString("F0") + "ms hostTick=" + smTickEst.ToString("F0") + "ms jitterP95=" + smClock.JitterP95Ms.ToString("F0") + "ms loss=" + smClock.LossPercent.ToString("F1") + "% outages=" + smClock.Outages + " lastOutage=" + smClock.LastOutageMs.ToString("F0") + "ms units=" + smHist.Count
                + " per-frame interp/extrap/held=" + (smFramesWin == 0 ? "0" : (smInterpWin / smFramesWin).ToString()) + "/" + (smFramesWin == 0 ? "0" : (smExtrapWin / smFramesWin).ToString()) + "/" + (smFramesWin == 0 ? "0" : (smHeldWin / smFramesWin).ToString()) + " mode=" + (smClock.FixedDelayMs < 0 ? "auto" : smClock.FixedDelayMs == 0 ? "off" : smClock.FixedDelayMs + "ms"));
            smFramesWin = smInterpWin = smExtrapWin = smHeldWin = 0;
        }

        // F8 line (guest): what the link looks like and what the smoothing does now.
        string SmStatusLine()
        {
            if (hosting || bridge == null) return null;
            string mode = smClock.FixedDelayMs < 0 ? "авто" : smClock.FixedDelayMs == 0 ? "вимк" : "фікс";
            PingPeer pp; string rtt = "? мс", jit = "? мс", lossU = "?";
            if (pings.TryGetValue(peer, out pp) && pp.Answered)
            {
                // 0.4.5: an empty reliable list used to print "RTT 0 мс" (Percentile of nothing); say that the reliable channel is silent and for how long
                float relAge = pp.Reliable.Received > 0 ? Time.realtimeSinceStartup - pp.Reliable.LastReplyAt : Time.realtimeSinceStartup - pp.FirstSentAt;
                if (relAge > PingLostSeconds) { rtt = "немає надійних відповідей вже " + relAge.ToString("F0") + " с"; jit = "-"; }
                else { rtt = pp.Reliable.Percentile(0.5f).ToString("F0") + " мс"; jit = pp.Reliable.Jitter.ToString("F0") + " мс"; }
                int sent = pp.Unreliable.Sent; lossU = sent > 0 ? (100f * pp.Unreliable.Lost / sent).ToString("F1") : "0";
            }
            return "Мережа: RTT " + rtt + ", джитер " + jit + ", втрати пінгу " + lossU + "%, позиції: втрати " + smClock.LossPercent.ToString("F1") + "%, джитер p95 " + smClock.JitterP95Ms.ToString("F0") + " мс | згладжування " + mode + " " + (smClock.Active ? smClock.DelayMs.ToString("F0") + " мс" : "") + (smExtrap + smHeld > 0 ? ", екстраполяція " + smUnitsNow + " юн." : "") + (smClock.Outages > 0 ? ", обривів " + smClock.Outages + " (останній " + (smClock.LastOutageMs / 1000f).ToString("F1") + " с)" : "") + (smHostStamps ? "" : " (хост без міток часу)");
        }
    }
}
