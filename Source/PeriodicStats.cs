using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using Brix.Game;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.54: one STATS line a minute in BepInEx\LogOutput.log (on by default, [Diag] PeriodicStats). It records what grows during a long match
    // so a slowdown can be explained: average fps, heap size, garbage collections, how many networked objects exist and of which kinds,
    // and how many components the game clock ticks every frame. Counting walks the networked object list once a minute (a few ms).
    public sealed partial class SteamBridgePlugin
    {
        ConfigEntry<bool> periodicStats; float nextStats, statsLastTime; int statsLastFrame, statsLastGc; static FieldInfo[] clockFields; static FieldInfo clockInstField; static object clockInstance; static bool clockTried;
        string statsGameDir;
        volatile string statsComputerState = "sys[pending]";
        int statsProbeBusy;

        void InstallPeriodicStats()
        {
            statsGameDir = Application.dataPath;
            try { periodicStats = Config.Bind("Diag", "PeriodicStats", true, "Write a STATS line to the log once a minute during a match (fps, memory, object counts) to help find what makes a long match slow."); }
            catch (Exception) { }
        }

        void PeriodicStatsTick()
        {
            if (periodicStats == null || !periodicStats.Value) return;
            float now = Time.realtimeSinceStartup;
            if (nextStats == 0f) { nextStats = now + 60f; statsLastTime = now; statsLastFrame = Time.frameCount; statsLastGc = GC.CollectionCount(0); return; }
            if (now < nextStats) return;
            nextStats = now + 60f;
            try
            {
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "ScenePrincipale") { statsLastTime = now; statsLastFrame = Time.frameCount; return; }
                float dt = now - statsLastTime; int frames = Time.frameCount - statsLastFrame; statsLastTime = now; statsLastFrame = Time.frameCount;
                int gc = GC.CollectionCount(0); int gcDelta = gc - statsLastGc; statsLastGc = gc;
                System.Collections.Generic.Dictionary<NetworkInstanceId, NetworkIdentity> objs = NetworkServer.active ? NetworkServer.objects : ClientScene.objects;
                int total = 0, labors = 0; Dictionary<string, int> kinds = new Dictionary<string, int>();
                if (objs != null)
                    foreach (KeyValuePair<NetworkInstanceId, NetworkIdentity> kv in objs)
                    {
                        NetworkIdentity ni = kv.Value; if (ni == null) continue; total++;
                        string n = ni.gameObject.name; int p = n.IndexOf('(', 0); if (p > 0) n = n.Substring(0, p).Trim(); if (n.Length > 28) n = n.Substring(0, 28);
                        int c; kinds.TryGetValue(n, out c); kinds[n] = c + 1;
                        if (ni.GetComponent<Brix.Game.AI.Labor>() != null) labors++;
                    }
                List<KeyValuePair<string, int>> top = new List<KeyValuePair<string, int>>(kinds); top.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
                QueueComputerState();
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < top.Count && i < 8; i++) sb.Append(' ').Append(top[i].Key).Append(" x").Append(top[i].Value);
                Logger.LogInfo("STATS t=" + (int)now + "s fps=" + (dt > 0 ? frames / dt : 0).ToString("F0") + " heap=" + (GC.GetTotalMemory(false) >> 20) + "MB gc0+" + gcDelta + " netObjects=" + total + " labor=" + labors + " clock[" + ClockCounts() + "] guests=" + guests.Count + " " + statsComputerState + " " + AllocReport() + " " + PosLossReport() + " " + LinkPathNow() + " top:" + sb);
            }
            catch (Exception e) { Logger.LogWarning("Periodic stats failed: " + e.Message); }
        }

        // 0.3.56: computer state next to the game numbers, so a slowdown can be matched with memory or disk trouble:
        // free physical RAM, this process' working set, free space on the system drive and on the game drive, and how long a tiny
        // write-through file takes on the game drive (a busy or full disk shows up as tens or hundreds of ms).
        [StructLayout(LayoutKind.Sequential)]
        struct MemStatus { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus s);

        [StructLayout(LayoutKind.Sequential)]
        struct ProcMem { public uint cb, PageFaultCount; public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage; }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] static extern bool GetProcessMemoryInfo(IntPtr h, out ProcMem c, uint cb);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetDiskFreeSpaceExW(string dir, out ulong avail, out ulong total, out ulong free);

        static string DriveFree(string path)
        {
            try { ulong a, t, f; if (GetDiskFreeSpaceExW(path, out a, out t, out f)) return (a >> 30) + "GB"; } catch (Exception) { }
            return "?";
        }

        void QueueComputerState()
        {
            if (Interlocked.CompareExchange(ref statsProbeBusy, 1, 0) != 0) return;
            string dir = statsGameDir;
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate(object unused)
                {
                    try { statsComputerState = ComputerState(dir); }
                    catch (Exception) { statsComputerState = "sys[probe failed]"; }
                    finally { Interlocked.Exchange(ref statsProbeBusy, 0); }
                })) Interlocked.Exchange(ref statsProbeBusy, 0);
            }
            catch (Exception) { Interlocked.Exchange(ref statsProbeBusy, 0); }
        }

        // Worker only. STATS uses the previous completed sample (first sample is pending).
        static string ComputerState(string gameDir)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("sys[");
            try { MemStatus m = new MemStatus(); m.dwLength = (uint)Marshal.SizeOf(typeof(MemStatus)); if (GlobalMemoryStatusEx(ref m)) sb.Append("ramFree=" + (m.ullAvailPhys >> 20) + "/" + (m.ullTotalPhys >> 20) + "MB pageFree=" + (m.ullAvailPageFile >> 20) + "MB "); } catch (Exception) { }
            try { ProcMem pm; if (GetProcessMemoryInfo(GetCurrentProcess(), out pm, (uint)Marshal.SizeOf(typeof(ProcMem)))) sb.Append("gameRam=" + (pm.WorkingSetSize.ToUInt64() >> 20) + "MB "); } catch (Exception) { }
            try
            {
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
                sb.Append("free" + sysRoot.TrimEnd('\\') + "=" + DriveFree(sysRoot) + " ");
                if (gameDir != null) { string gameRoot = Path.GetPathRoot(gameDir); if (!string.Equals(sysRoot, gameRoot, StringComparison.OrdinalIgnoreCase)) sb.Append("free" + gameRoot.TrimEnd('\\') + "=" + DriveFree(gameRoot) + " "); }
            }
            catch (Exception) { }
            try
            {
                if (gameDir != null)
                {
                    string tmp = Path.Combine(gameDir, "bridge-disktest-" + Guid.NewGuid().ToString("N") + ".tmp"); byte[] buf = new byte[4096];
                    System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                    try { using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { fs.Write(buf, 0, buf.Length); fs.Flush(); } }
                    finally { try { File.Delete(tmp); } catch (Exception) { } }
                    w.Stop();
                    sb.Append("diskWrite=" + w.ElapsedMilliseconds + "ms");
                }
            }
            catch (Exception) { }
            return sb.ToString().TrimEnd() + "]";
        }
        static string ClockCounts()
        {
            try
            {
                if (!clockTried)
                {
                    clockTried = true;
                    clockInstField = typeof(GameClock).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
                    List<FieldInfo> l = new List<FieldInfo>();
                    foreach (string n in new string[] { "UpdateSig", "FixedUpdateSig", "LateUpdateSig", "SlowUpdateSig_50", "SlowUpdateSig_100", "SlowUpdateSig_200", "SlowUpdateSig_500", "SlowUpdateSig_1s" })
                    { FieldInfo f = typeof(GameClock).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (f != null) l.Add(f); }
                    clockFields = l.ToArray();
                }
                // 0.4.27 review: GameClock.instance is a new object in every match (its OnDestroy sets the static to null); the object cached at the first STATS line made clock[...] show the dead
                // clock of the first match (U=0 FixedU=0 ...) in every later match of the same game run (real logs 2026-11-07). Read the static each time (once a minute).
                clockInstance = clockInstField != null ? clockInstField.GetValue(null) : null;
                if (clockInstance == null || clockFields == null) return "n/a";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < clockFields.Length; i++)
                {
                    object sig = clockFields[i].GetValue(clockInstance); if (sig == null) continue;
                    PropertyInfo pc = sig.GetType().GetProperty("ConnectionCount"); int n = pc != null ? (int)pc.GetValue(sig, null) : -1;
                    sb.Append(clockFields[i].Name.Replace("Sig", "").Replace("Update", "U")).Append('=').Append(n).Append(' ');
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception) { return "err"; }
        }
    }
}
