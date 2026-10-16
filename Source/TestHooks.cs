using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.42 test hooks (off by default, [Test] Enabled = true in the config): delays outgoing Steam packets and can hold them back
    // for N seconds to imitate a slow link or a frozen host. Used to measure what UNet does at 0.2 s, 1.5 s, 5 s, 15 s, 35 s.
    public sealed partial class SteamBridgePlugin
    {
        sealed class HeldPacket { internal CSteamID To; internal byte[] Data; internal int Length; internal float Due; }
        ConfigEntry<bool> testEnabled;
        readonly List<HeldPacket> held = new List<HeldPacket>();
        float testHolUntil, testDelaySeconds, testStallUntil, testDropUntil, testLossPercent; readonly System.Random testRng = new System.Random(12345); int testDropped;
        int testHeldTotal;

        void BindTestConfig()
        {
            try { testEnabled = Config.Bind("Test", "Enabled", false, "Shows test buttons in F8 that delay or hold back outgoing packets (for latency tests). Leave false for normal play."); }
            catch (Exception) { }
        }
        bool TestOn { get { return testEnabled != null && testEnabled.Value; } }

        // Returns true when the packet was taken (queued) instead of being sent now.
        bool TestShape(CSteamID recipient, byte[] p, int length)
        {
            if (!TestOn) return false;
            float now = Time.realtimeSinceStartup;
            // 0.3.49: real loss. An unreliable-capable packet (what Mixed mode sends without Steam retransmits) is dropped; a reliable one
            // is only held back (Steam would retransmit it), which is what loss looks like on a reliable link.
            bool gameLive = hosting ? UnityEngine.Networking.NetworkServer.active && JoinSettled(recipient) : UnityEngine.Networking.NetworkClient.active;
            bool unreliable = mixedSend && gameLive && BridgeCore.CanSendUnreliable(p, length);
            if (now < testDropUntil && unreliable) { testDropped++; return true; }
            if (testLossPercent > 0f && testRng.NextDouble() * 100.0 < testLossPercent)
            {
                if (unreliable) { testDropped++; return true; }
                HeldPacket r = new HeldPacket(); r.To = recipient; r.Length = length; r.Data = new byte[length]; Buffer.BlockCopy(p, 0, r.Data, 0, length); r.Due = Math.Max(now + testDelaySeconds, now + 0.3f); held.Add(r); testHeldTotal++; return true;
            }
            bool stalled = now < testStallUntil || now < testHolUntil || (now < testDropUntil && !unreliable);
            if (!stalled && testDelaySeconds <= 0f && held.Count == 0) return false;
            HeldPacket h = new HeldPacket();
            h.To = recipient; h.Length = length; h.Data = new byte[length]; Buffer.BlockCopy(p, 0, h.Data, 0, length);
            h.Due = Math.Max(now + testDelaySeconds, Math.Max(Math.Max(testStallUntil, testHolUntil), testDropUntil));
            held.Add(h); testHeldTotal++;
            return true;
        }

        // Every frame: send what is due, in order.
        void TestFlush()
        {
            if (held.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            int sent = 0;
            while (held.Count > 0 && held[0].Due <= now && sent < 512)
            {
                HeldPacket h = held[0]; held.RemoveAt(0); sent++;
                try { SendRangeToCore(h.To, h.Data, h.Length); } catch (Exception e) { Logger.LogWarning("Test send failed: " + e.Message); held.Clear(); break; }
            }
        }

        // 0.3.47: scripted control for unattended soak tests. A file BepInEx\config\test-control.txt with lines delay_ms=NNN and
        // stall_s=NN is picked up every 2 s (and deleted); once a minute a SOAK line records heap, guests, delay and ping.
        float nextControl, nextSoak;
        // 0.3.48: aligned actor sampler for freeze analysis. Every 2 s on the UTC clock both games log "ACT <ms> id:x,z ..." for the
        // networked units that have a Labor (bricktrons and workers), so host and guest positions can be compared at the same instant.
        long nextActMs; static readonly DateTime ActEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        void ActorSample()
        {
            if (!TestOn) return;
            long now = (long)(DateTime.UtcNow - ActEpoch).TotalMilliseconds;
            if (nextActMs == 0) nextActMs = (now / 2000 + 1) * 2000;
            if (now < nextActMs) return;
            long stamp = nextActMs; nextActMs = (now / 2000 + 1) * 2000;
            try
            {
                System.Collections.Generic.Dictionary<UnityEngine.Networking.NetworkInstanceId, UnityEngine.Networking.NetworkIdentity> objects =
                    UnityEngine.Networking.NetworkServer.active ? UnityEngine.Networking.NetworkServer.objects : UnityEngine.Networking.ClientScene.objects;
                if (objects == null) return;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                System.Collections.Generic.List<uint> ids = new System.Collections.Generic.List<uint>();
                System.Collections.Generic.Dictionary<uint, Vector3> pos = new System.Collections.Generic.Dictionary<uint, Vector3>();
                foreach (System.Collections.Generic.KeyValuePair<UnityEngine.Networking.NetworkInstanceId, UnityEngine.Networking.NetworkIdentity> kv in objects)
                {
                    UnityEngine.Networking.NetworkIdentity ni = kv.Value;
                    if (ni == null || ni.GetComponent<Brix.Game.AI.Labor>() == null) continue;
                    uint id = kv.Key.Value; ids.Add(id); pos[id] = ni.transform.position;
                }
                ids.Sort();
                for (int i = 0; i < ids.Count && i < 40; i++) sb.Append(' ').Append(ids[i]).Append(':').Append(pos[ids[i]].x.ToString("F1")).Append(',').Append(pos[ids[i]].z.ToString("F1"));
                Logger.LogInfo("ACT " + stamp + " n=" + ids.Count + sb);
            }
            catch (Exception e) { Logger.LogWarning("Actor sample failed: " + e.Message); }
        }
        void TestControlTick()
        {
            if (!TestOn) return;
            ActorSample();
            float now = Time.realtimeSinceStartup;
            if (now >= nextControl)
            {
                nextControl = now + 2f;
                try
                {
                    string file = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "test-control.txt");
                    if (System.IO.File.Exists(file))
                    {
                        string[] lines = System.IO.File.ReadAllLines(file); System.IO.File.Delete(file);
                        foreach (string raw in lines)
                        {
                            string line = raw.Trim(); int eq = line.IndexOf('='); if (eq < 0) continue;
                            string key = line.Substring(0, eq).Trim(); float value;
                            if (!Single.TryParse(line.Substring(eq + 1).Trim(), out value)) continue;
                            if (key == "delay_ms") { testDelaySeconds = Math.Max(0f, value) / 1000f; Logger.LogInfo("TEST: delay set to " + (int)value + " ms"); }
                            else if (key == "loss_pct") { testLossPercent = Math.Max(0f, Math.Min(100f, value)); Logger.LogInfo("TEST: packet loss set to " + testLossPercent + " %"); }
                            else if (key == "drop_s") { testDropUntil = now + Math.Max(0f, value); Logger.LogInfo("TEST: outgoing packets DROPPED for " + (int)value + " s"); }
                            else if (key == "extrap") { extrapolateOn = value > 0f; Logger.LogInfo("TEST: unit extrapolation " + (extrapolateOn ? "ON" : "OFF")); }
                            else if (key == "posch") { if (posSideChannel != null) posSideChannel.Value = value > 0f; Logger.LogInfo("TEST: position side channel " + (value > 0f ? "ON" : "OFF")); }
                            else if (key == "hol_s") { testHolUntil = now + Math.Max(0f, value); Logger.LogInfo("TEST: main stream held (side channel keeps flowing) for " + (int)value + " s"); }
                            else if (key == "auth") { posWins = value > 0f; Logger.LogInfo("TEST: side channel wins over game sync " + (posWins ? "ON" : "OFF")); }
                            else if (key == "stall_s") { testStallUntil = now + Math.Max(0f, value); Logger.LogInfo("TEST: outgoing packets held for " + (int)value + " s"); }
                        }
                    }
                }
                catch (Exception e) { Logger.LogWarning("Test control failed: " + e.Message); }
            }
            if (now >= nextSoak)
            {
                nextSoak = now + 60f;
                string peersText = "";
                foreach (KeyValuePair<CSteamID, PingPeer> kv in pings) peersText += " [" + PingName(kv.Key) + " rel last " + kv.Value.Reliable.Last.ToString("F0") + " p95 " + kv.Value.Reliable.Percentile(0.95f).ToString("F0") + " ms, lost " + kv.Value.Reliable.Lost + "]";
                Logger.LogInfo("SOAK t=" + (int)now + "s heap=" + (GC.GetTotalMemory(false) >> 20) + "MB hosting=" + hosting + " guests=" + guests.Count + " bridgeReady=" + (bridge != null && bridge.Ready) + " delay=" + (int)(testDelaySeconds * 1000) + "ms loss=" + testLossPercent + "% dropped=" + testDropped + " mixed=" + mixedSend + " held=" + held.Count + peersText);
            }
        }
        void DrawTestPanel()
        {
            if (!TestOn) return;
            GUILayout.Label("ТЕСТ мережі: затримка " + (int)(testDelaySeconds * 1000) + " мс; утримано пакетів: " + held.Count + (Time.realtimeSinceStartup < testStallUntil ? "; БЛОК ще " + (testStallUntil - Time.realtimeSinceStartup).ToString("F0") + " с" : ""));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("0 мс")) testDelaySeconds = 0f;
            if (GUILayout.Button("+200")) testDelaySeconds = 0.2f;
            if (GUILayout.Button("+1500")) testDelaySeconds = 1.5f;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Блок 5 с")) { testStallUntil = Time.realtimeSinceStartup + 5f; Logger.LogInfo("TEST: outgoing packets held for 5 s"); }
            if (GUILayout.Button("Блок 15 с")) { testStallUntil = Time.realtimeSinceStartup + 15f; Logger.LogInfo("TEST: outgoing packets held for 15 s"); }
            if (GUILayout.Button("Блок 35 с")) { testStallUntil = Time.realtimeSinceStartup + 35f; Logger.LogInfo("TEST: outgoing packets held for 35 s"); }
            GUILayout.EndHorizontal();
        }
    }
}