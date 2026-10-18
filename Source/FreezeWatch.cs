using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.58: automatic freeze classification, a 2-minute ring buffer, a low-RAM warning and a rough allocation audit of the bridge's own per-frame work.
    // A "freeze" starts when one frame takes >0.4 s (CLIENT_HITCH: this game's main thread), when a connected peer sends nothing for >2 s (NETWORK_STALL)
    // or when Steam holds >1 MB unsent (QUEUE_BACKLOG). Several causes can be true at once and all are written. Repeats close together are merged into one event.
    // HOST_HITCH is not reported yet: telling a stalled host from a dead network needs the host to report its own frame time (a versioned service message, planned),
    // so a network stall without a local hitch is written as "NETWORK_STALL (host hitch possible)". File writes go through the thread pool, never inline in the frame.
    public sealed partial class SteamBridgePlugin
    {
        const int RingSize = 7200;                // about two minutes of frames at 60 fps
        const float FrameBadSeconds = 0.4f, RxBadSeconds = 2f, QueueBadBytes = 1048576f, EventCloseSeconds = 1f;
        ConfigEntry<bool> freezeWatchEntry;
        float[] ringT, ringDt, ringRx; int[] ringQ; int ringPos, ringCount;
        bool fzActive, fzClient, fzNet, fzQueue; float fzStart, fzLastBad, fzWorstFrame, fzWorstRx; int fzMaxQueue, fzRepeats, fzRamAtStart; ulong fzPeer; string fzRing = "";
        float fzNextSample, fzLastRx; int fzLastQueue; ulong fzLastPeer; int fzEvents; float nextRamCheck, nextRamWarn; static readonly object fzFileLock = new object();
        // allocation audit: bytes the managed heap grew inside the bridge's own per-frame sections (0 position link, 1 item filter, 2 periodic stats, 3 freeze watch)
        long[] allocSum = new long[4]; long allocMark;

        void InstallFreezeWatch()
        {
            try
            {
                freezeWatchEntry = Config.Bind("Diag", "FreezeWatch", true, "Classify freezes (client hitch, network stall, queue backlog) and write them to BepInEx\\freeze-events.txt with the last seconds of frame data.");
                ringT = new float[RingSize]; ringDt = new float[RingSize]; ringRx = new float[RingSize]; ringQ = new int[RingSize];
            }
            catch (Exception) { }
        }

        void AllocBegin() { allocMark = GC.GetTotalMemory(false); }
        void AllocEnd(int slot) { long d = GC.GetTotalMemory(false) - allocMark; if (d > 0) allocSum[slot] += d; }
        string AllocReport()
        {
            string s = "alloc[pos=" + (allocSum[0] >> 10) + "KB item=" + (allocSum[1] >> 10) + "KB stats=" + (allocSum[2] >> 10) + "KB freeze=" + (allocSum[3] >> 10) + "KB]";
            for (int i = 0; i < allocSum.Length; i++) allocSum[i] = 0;
            return s;
        }

        // Cheap link sample every 0.25 s: the longest silence of a connected peer and the biggest Steam send queue.
        void FreezeSampleLink()
        {
            float rx = 0f; int queue = 0; ulong who = 0;
            try
            {
                if (bridge != null && bridge.Ready && bridge.Received > 0)
                { rx = ReceiveAge(bridge); who = peer.m_SteamID; P2PSessionState_t s; if (SteamNetworking.GetP2PSessionState(peer, out s)) queue = s.m_nBytesQueuedForSend; }
                else if (hosting)
                    foreach (KeyValuePair<CSteamID, BridgeCore> g in guests)
                    {
                        if (!g.Value.Ready || g.Value.Received <= 0) continue;
                        float a = ReceiveAge(g.Value); if (a > rx) { rx = a; who = g.Key.m_SteamID; }
                        P2PSessionState_t s; if (SteamNetworking.GetP2PSessionState(g.Key, out s) && s.m_nBytesQueuedForSend > queue) queue = s.m_nBytesQueuedForSend;
                    }
            }
            catch (Exception) { }
            fzLastRx = rx; fzLastQueue = queue; fzLastPeer = who;
        }

        void FreezeWatchTick()
        {
            if (freezeWatchEntry == null || !freezeWatchEntry.Value || ringT == null) return;
            float now = Time.realtimeSinceStartup;           // monotonic: unaffected by changes of the system clock
            float dt = Time.unscaledDeltaTime;
            if (now >= fzNextSample) { fzNextSample = now + 0.25f; FreezeSampleLink(); }
            ringT[ringPos] = now; ringDt[ringPos] = dt; ringRx[ringPos] = fzLastRx; ringQ[ringPos] = fzLastQueue;
            ringPos = (ringPos + 1) % RingSize; if (ringCount < RingSize) ringCount++;
            bool c = dt > FrameBadSeconds, n = fzLastRx > RxBadSeconds, q = fzLastQueue > QueueBadBytes;
            if (c || n || q)
            {
                if (!fzActive)
                {
                    fzActive = true; fzStart = now; fzClient = fzNet = fzQueue = false; fzWorstFrame = fzWorstRx = 0f; fzMaxQueue = 0; fzRepeats = 0; fzPeer = 0;
                    fzRamAtStart = FreeRamMb(); fzRing = RingExcerpt(now);
                }
                else if (c && dt > fzWorstFrame * 0.5f && now - fzLastBad > 0.05f) fzRepeats++;
                fzLastBad = now; fzClient |= c; fzNet |= n; fzQueue |= q;
                if (dt > fzWorstFrame) fzWorstFrame = dt; if (fzLastRx > fzWorstRx) { fzWorstRx = fzLastRx; fzPeer = fzLastPeer; } if (fzLastQueue > fzMaxQueue) fzMaxQueue = fzLastQueue;
            }
            else if (fzActive && now - fzLastBad > EventCloseSeconds) EndFreeze(now);
            if (now >= nextRamCheck)
            {
                nextRamCheck = now + 10f; int free = FreeRamMb();
                if (free > 0 && free < 1024 && now >= nextRamWarn) { nextRamWarn = now + 300f; WriteFreezeLine("LOW_RAM t=" + (int)now + "s ramFree=" + free + "MB (game may slow down or stall; close other programs)", true); }
            }
        }

        void EndFreeze(float now)
        {
            fzActive = false; fzEvents++;
            StringBuilder cause = new StringBuilder();
            if (fzClient) cause.Append("CLIENT_HITCH");
            if (fzNet) { if (cause.Length > 0) cause.Append('+'); cause.Append("NETWORK_STALL"); if (!fzClient && !fzQueue) cause.Append(" (host hitch possible)"); }
            if (fzQueue) { if (cause.Length > 0) cause.Append('+'); cause.Append("QUEUE_BACKLOG"); }
            if (cause.Length == 0) cause.Append("UNKNOWN");
            string line = "FREEZE t=" + (int)fzStart + "s dur=" + (fzLastBad - fzStart + 0.0f).ToString("F2") + "s repeats=" + fzRepeats + " cause=" + cause
                + " worstFrame=" + (int)(fzWorstFrame * 1000f) + "ms worstSilence=" + fzWorstRx.ToString("F1") + "s maxQueue=" + fzMaxQueue + "B ramFree=" + fzRamAtStart + "MB peer=" + fzPeer + " role=" + (hosting ? "host" : "guest") + " " + LinkPathNow() + " posLostTotal=" + posLost;
            WriteFreezeLine(line + "\r\n" + fzRing, false);
            Logger.LogWarning(line);
        }

        // The last 30 s before the event: every frame over 100 ms plus one summary per second. Built on the main thread, written by the thread pool.
        string RingExcerpt(float now)
        {
            StringBuilder sb = new StringBuilder("  before the freeze (t, frame ms, silence s, queue B):\r\n");
            int shown = 0; float lastSecond = -1f;
            for (int i = ringCount - 1; i >= 0 && shown < 60; i--)
            {
                int idx = (ringPos - 1 - (ringCount - 1 - i) + 2 * RingSize) % RingSize;
                float t = ringT[idx]; if (now - t > 30f) break;
                bool slow = ringDt[idx] > 0.1f; bool second = (int)t != (int)lastSecond;
                if (!slow && !second) continue; if (second) lastSecond = t;
                sb.Append("    ").Append(t.ToString("F1")).Append("  ").Append((int)(ringDt[idx] * 1000f)).Append("  ").Append(ringRx[idx].ToString("F1")).Append("  ").Append(ringQ[idx]).Append("\r\n"); shown++;
            }
            return sb.ToString();
        }

        void WriteFreezeLine(string text, bool alsoLog)
        {
            string path = Path.Combine(Paths.BepInExRootPath, "freeze-events.txt");
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ";
            string payload = stamp + text + "\r\n";
            if (alsoLog) Logger.LogWarning(text);
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                try { lock (fzFileLock) File.AppendAllText(path, payload, Encoding.UTF8); } catch (Exception) { }
            });
        }

        static int FreeRamMb()
        {
            try { MemStatus m = new MemStatus(); m.dwLength = (uint)Marshal.SizeOf(typeof(MemStatus)); if (GlobalMemoryStatusEx(ref m)) return (int)(m.ullAvailPhys >> 20); } catch (Exception) { }
            return -1;
        }
    }
}