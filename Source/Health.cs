using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.12: memory, garbage-collector and frame figures in the same 15 s windows as the bridge statistics, so a report shows whether the
    // game's own heap/GC (the ~1 s main-loop pauses seen in long sessions) lines up with a stall or a gap. Reads counters only.
    public sealed partial class SteamBridgePlugin
    {
        readonly List<string> healthWindows = new List<string>();
        int healthGc0, healthGc1, healthGc2, healthFrame;
        float healthLast, healthMaxDt;

        // every frame (from Update)
        void HealthFrame()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > healthMaxDt) healthMaxDt = dt;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryCounters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool, QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage;
        }
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")] static extern IntPtr GetCurrentProcessHandle();
        [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
        void HealthReset() { healthWindows.Clear(); healthLast = 0f; healthMaxDt = 0f; }

        // every 15 s (from LogStats)
        void HealthSample()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                if (healthLast <= 0f || now - healthLast < 1f)
                {
                    healthGc0 = g0; healthGc1 = g1; healthGc2 = g2; healthFrame = Time.frameCount; healthLast = now; healthMaxDt = 0f;
                    return;
                }
                float secs = now - healthLast;
                long managed = GC.GetTotalMemory(false);
                long workingSet = 0;
                try
                {
                    MemoryCounters m = new MemoryCounters(); m.Size = (uint)Marshal.SizeOf(typeof(MemoryCounters));
                    if (GetProcessMemoryInfo(GetCurrentProcessHandle(), out m, m.Size)) workingSet = (long)m.WorkingSet.ToUInt64();
                }
                catch (Exception) { }
                string ram = workingSet > 0 ? (workingSet / 1048576) + " MB" : "unavailable";
                float fps = (Time.frameCount - healthFrame) / secs;
                DiagNote(healthWindows, "RAM " + ram + "; managed heap " + (managed / 1048576) + " MB; GC gen0/1/2 +" + (g0 - healthGc0) + "/" + (g1 - healthGc1) + "/" + (g2 - healthGc2)
                    + "; FPS " + fps.ToString("F0") + "; longest frame " + (healthMaxDt * 1000f).ToString("F0") + " ms");
                healthGc0 = g0; healthGc1 = g1; healthGc2 = g2; healthFrame = Time.frameCount; healthLast = now; healthMaxDt = 0f;
            }
            catch (Exception) { }
        }

        void AppendHealthReport(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("--- Пам'ять, збирач сміття, кадри (вікна по 15 с): " + healthWindows.Count);
            foreach (string line in healthWindows) sb.AppendLine(line);
        }

        string HealthLastLines(int count)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = Math.Max(0, healthWindows.Count - count); i < healthWindows.Count; i++) sb.AppendLine(healthWindows[i]);
            return sb.ToString();
        }
    }
}
