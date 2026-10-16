using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Steamworks;

namespace CastleStorySteam
{
    // The installer saves the game/plugin logs of the previous run (they would be overwritten by the next start).
    // When a friend with a 0.3.5+ bridge connects, a compact network-only summary of each saved log is sent to him once.
    public sealed partial class SteamBridgePlugin
    {
        bool previousScanned;
        readonly List<string> previousPending = new List<string>();

        // Path.Combine has no three-argument form in the game's .NET 2.0 profile.
        static string PreviousFolder() { return Path.Combine(Path.Combine(Paths.BepInExRootPath, "SteamBridgeDiagnostics"), "previous"); }

        void ScanPreviousLogs()
        {
            previousScanned = true;
            try
            {
                string dir = PreviousFolder();
                if (!Directory.Exists(dir)) return;
                previousPending.AddRange(Directory.GetFiles(dir, "*.txt"));
                if (previousPending.Count > 0) Logger.LogInfo("Saved logs of a previous run waiting to be shared: " + previousPending.Count);
            }
            catch (Exception e) { Logger.LogWarning("Previous log scan failed: " + e.Message); }
        }
        // Only called for a peer that announced version 0.3.5 or newer, so it understands the report packet.
        void SharePreviousLogs(CSteamID id)
        {
            if (!AutoShareOn || !IsFriend(id)) return;
            if (!previousScanned) ScanPreviousLogs();
            for (int i = previousPending.Count - 1; i >= 0; i--)
            {
                string file = previousPending[i];
                try
                {
                    byte[] data = Encoding.UTF8.GetBytes("#prev\n" + LogSummary.Build(file, Path.GetFileName(file), 30000));
                    if (data.Length > BridgeCore.MaxDiagnosticsBytes) Array.Resize(ref data, BridgeCore.MaxDiagnosticsBytes);
                    if (SendTo(id, BridgeCore.Packet(BridgeCore.KindDiagnosticsReport, data, data.Length)))
                    {
                        File.Move(file, file + ".sent");
                        previousPending.RemoveAt(i);
                        Logger.LogInfo("Summary of the saved log sent to a friend: " + Path.GetFileName(file));
                    }
                }
                catch (Exception e) { Logger.LogWarning("Sharing a saved log failed: " + e.Message); }
            }
        }
    }
}
