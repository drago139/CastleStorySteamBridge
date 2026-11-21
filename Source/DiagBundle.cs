using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.59: one ZIP with everything needed to study a problem, saved to the player's Downloads folder (CastleStorySteamBridge-diagnostics).
    // Contents: LogOutput.log, freeze-events.txt, hitch-profile.txt, the last sync-diagnostics reports, the mod config and a short info.txt.
    // It is made on the F8 button, and automatically when a session ends after freezes were recorded. It stays on the player's PC and is never uploaded:
    // the player decides whether to send it. It contains Steam IDs and friend names from the logs. (RAR cannot be written without WinRAR, so ZIP is used.)
    public sealed partial class SteamBridgePlugin
    {
        ConfigEntry<bool> autoBundleEntry; int bundledEvents; float lastBundleAt = -1000f; string bundleStatus = "";
        readonly object bundleLock = new object(); bool bundleWorking; BundleResult bundleResult;
        sealed class BundleSnapshot { internal string Root, ConfigPath, Info, LatestFreeze, JoinLoad; internal int Events; internal bool Manual; }
        sealed class BundleResult { internal string Path, Error; internal BundleSnapshot Snapshot; }

        void InstallDiagBundle()
        {
            try { autoBundleEntry = Config.Bind("Diag", "AutoBundle", true, "After a session with recorded freezes, save a ZIP of the logs to Downloads\\CastleStorySteamBridge-diagnostics (local only, never uploaded)."); }
            catch (Exception) { }
        }

        void DrawDiagBundle()
        {
            if (GUILayout.Button("Зібрати діагностику в ZIP (папка Завантаження)")) MakeDiagBundle(true);
            if (!string.IsNullOrEmpty(bundleStatus)) GUILayout.Label(bundleStatus);
        }

        BundleSnapshot CaptureBundle(bool manual)
        {
            StringBuilder info = new StringBuilder();
            info.Append("Castle Story Steam Bridge ").Append(BridgeVersion.Text).Append(" build ").Append(BridgeVersion.Build).Append("\r\nCreated: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(manual ? " (button)" : " (automatic, after freezes)").Append("\r\n");
            info.Append("Role: ").Append(hosting ? "host" : (bridge != null ? "guest" : "none")).Append("; freeze events this run: ").Append(fzEvents).Append("\r\n");
            info.Append("OS: ").Append(Environment.OSVersion).Append("; RAM free ").Append(FreeRamMb()).Append(" MB; game uptime ").Append((int)Time.realtimeSinceStartup).Append(" s\r\n");
            info.Append("CPU: ").Append(SystemInfo.processorType).Append("; GPU: ").Append(SystemInfo.graphicsDeviceName).Append("; RAM MB: ").Append(SystemInfo.systemMemorySize).Append("\r\n");
            info.Append("The files contain Steam IDs and friend names. The archive was NOT sent anywhere; share it only if you want to.\r\n");
            return new BundleSnapshot { Root = Paths.BepInExRootPath, ConfigPath = Config.ConfigFilePath, Info = info.ToString(), LatestFreeze = fzLatestEntry, Events = fzEvents, Manual = manual, JoinLoad = JoinLoadReport() };
        }

        void DiagBundleTick()
        {
            BundleResult result;
            lock (bundleLock) { result = bundleResult; bundleResult = null; }
            if (result == null) return;
            if (result.Path != null)
            {
                lastBundleAt = Time.realtimeSinceStartup;
                if (!result.Snapshot.Manual) bundledEvents = Math.Max(bundledEvents, result.Snapshot.Events);
                bundleStatus = "Збережено: " + result.Path;
            }
            else bundleStatus = "Не вдалося створити ZIP: " + result.Error;
        }

        void AutoBundleOnSessionEnd(BundleSnapshot snapshot)
        {
            try
            {
                DiagBundleTick();
                if (snapshot == null || autoBundleEntry == null || !autoBundleEntry.Value || snapshot.Events <= bundledEvents) return;
                if (Time.realtimeSinceStartup - lastBundleAt < 60f) return;
                QueueDiagBundle(snapshot);
            }
            catch (Exception) { }
        }

        static string DownloadsFolder()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrEmpty(profile)) { string d = Path.Combine(profile, "Downloads"); if (Directory.Exists(d)) return d; }
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        static byte[] ReadTail(string path, int maxBytes)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length; long start = len > maxBytes ? len - maxBytes : 0; fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[len - start]; int got = 0; while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
                    if (got != buf.Length) Array.Resize(ref buf, got);
                    return buf;
                }
            }
            catch (Exception) { return null; }
        }

        void MakeDiagBundle(bool manual)
        {
            try { DiagBundleTick(); QueueDiagBundle(CaptureBundle(manual)); }
            catch (Exception e) { bundleStatus = "Не вдалося створити ZIP: " + e.Message; Logger.LogWarning("Diagnostics snapshot failed: " + e.Message); }
        }

        void QueueDiagBundle(BundleSnapshot snapshot)
        {
            lock (bundleLock) { if (bundleWorking) return; bundleWorking = true; }
            bundleStatus = "Створення ZIP...";
            BepInEx.Logging.ManualLogSource log = Logger;
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate(object ignored)
                {
                    BundleResult result = new BundleResult { Snapshot = snapshot };
                    try { result.Path = WriteDiagBundle(snapshot); log.LogInfo("Diagnostics bundle saved: " + result.Path); }
                    catch (Exception e) { result.Error = e.Message; log.LogWarning("Diagnostics bundle failed: " + e.Message); }
                    finally { lock (bundleLock) { bundleResult = result; bundleWorking = false; } }
                })) throw new InvalidOperationException("Could not queue diagnostics worker.");
            }
            catch (Exception) { lock (bundleLock) bundleWorking = false; throw; }
        }

        static string WriteDiagBundle(BundleSnapshot snapshot)
        {
                // Worker: paths and immutable metadata only; no Unity, Steam or config access.
                string root = snapshot.Root; List<KeyValuePair<string, byte[]>> files = new List<KeyValuePair<string, byte[]>>();
                string[] names = { "LogOutput.log", "freeze-events.txt", "freeze-events.1.txt", "hitch-profile.txt", "perf-trace.txt", "perf-trace.1.txt" };
                foreach (string n in names) { byte[] d = ReadTail(Path.Combine(root, n), 16 * 1024 * 1024); if (d != null) files.Add(new KeyValuePair<string, byte[]>(n, d)); }
                string diagDir = Path.Combine(root, "SteamBridgeDiagnostics");
                if (Directory.Exists(diagDir))
                {
                    List<FileInfo> reports = new List<FileInfo>(new DirectoryInfo(diagDir).GetFiles("sync-diagnostics-*.txt"));
                    reports.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                    for (int i = 0; i < reports.Count && i < 3; i++) { byte[] d = ReadTail(reports[i].FullName, 700 * 1024); if (d != null) files.Add(new KeyValuePair<string, byte[]>("diagnostics/" + reports[i].Name, d)); }
                }
                if (!string.IsNullOrEmpty(snapshot.JoinLoad)) files.Add(new KeyValuePair<string, byte[]>("JOINLOAD.txt", Encoding.UTF8.GetBytes(snapshot.JoinLoad)));
                foreach (string pattern in new string[] { "joinload-local-*.txt", "peer-joinload-*.txt" })
                {
                    if (!Directory.Exists(diagDir)) continue;
                    FileInfo[] reports = new DirectoryInfo(diagDir).GetFiles(pattern);
                    Array.Sort(reports, delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                    for (int i = 0; i < reports.Length && i < 5; i++) { byte[] d = ReadTail(reports[i].FullName, 16384); if (d != null) files.Add(new KeyValuePair<string, byte[]>("diagnostics/" + reports[i].Name, d)); }
                }
                byte[] cfg = ReadTail(snapshot.ConfigPath, 64 * 1024); if (cfg != null) files.Add(new KeyValuePair<string, byte[]>("config.cfg", cfg));
                files.Add(new KeyValuePair<string, byte[]>("info.txt", Encoding.UTF8.GetBytes(snapshot.Info)));
                // Preserve the newest event even if its asynchronous log append is pending.
                if (!string.IsNullOrEmpty(snapshot.LatestFreeze)) files.Add(new KeyValuePair<string, byte[]>("latest-freeze.txt", Encoding.UTF8.GetBytes(snapshot.LatestFreeze)));
                string dir = Path.Combine(DownloadsFolder(), "CastleStorySteamBridge-diagnostics"); Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, (snapshot.Manual ? "diagnostics-" : "diagnostics-auto-") + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-v" + BridgeVersion.Text + ".zip");
                WriteZip(path, files);
                // Only automatic bundles belong to the retention policy. Preserve all manual and legacy archives.
                try { string[] old = Directory.GetFiles(dir, "diagnostics-auto-*.zip"); Array.Sort(old, StringComparer.Ordinal); for (int i = 0; i < old.Length - 5; i++) File.Delete(old[i]); } catch (Exception) { }
                return path;
        }

        static uint[] crcTable;
        static uint Crc32(byte[] data)
        {
            if (crcTable == null)
            {
                uint[] t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++) crc = crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        static void WriteZip(string path, List<KeyValuePair<string, byte[]>> files)
        {
            DateTime now = DateTime.Now;
            ushort dosTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
            ushort dosDate = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                List<uint> offsets = new List<uint>(); List<uint> crcs = new List<uint>(); List<uint> csizes = new List<uint>();
                foreach (KeyValuePair<string, byte[]> f in files)
                {
                    byte[] data = f.Value; byte[] name = Encoding.UTF8.GetBytes(f.Key); uint crc = Crc32(data);
                    byte[] packed; using (MemoryStream ms = new MemoryStream()) { using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(data, 0, data.Length); packed = ms.ToArray(); }
                    offsets.Add((uint)fs.Position); crcs.Add(crc); csizes.Add((uint)packed.Length);
                    w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)8); w.Write(dosTime); w.Write(dosDate);
                    w.Write(crc); w.Write((uint)packed.Length); w.Write((uint)data.Length); w.Write((ushort)name.Length); w.Write((ushort)0); w.Write(name); w.Write(packed);
                }
                uint cdStart = (uint)fs.Position; int idx = 0;
                foreach (KeyValuePair<string, byte[]> f in files)
                {
                    byte[] name = Encoding.UTF8.GetBytes(f.Key);
                    w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)8); w.Write(dosTime); w.Write(dosDate);
                    w.Write(crcs[idx]); w.Write(csizes[idx]); w.Write((uint)f.Value.Length); w.Write((ushort)name.Length); w.Write((ushort)0); w.Write((ushort)0);
                    w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(offsets[idx]); w.Write(name); idx++;
                }
                uint cdSize = (uint)fs.Position - cdStart;
                w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)files.Count); w.Write((ushort)files.Count); w.Write(cdSize); w.Write(cdStart); w.Write((ushort)0);
            }
        }
    }
}
