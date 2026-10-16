using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using Brix.Game.Network;
using Network;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // Network diagnostics that a player can share with the friend on the other end of the bridge (0.3.7).
    // The report contains only bridge/network figures (pauses, queues, frames, hardware names); it is sent
    // over the existing bridge link and only when a player presses the button or confirms a request.
    public sealed partial class SteamBridgePlugin
    {
        static bool DiagnosticsEnabled { get { return false; } }
        const int DiagKeep = 40;
        const int DiagTextLimit = 40000;
        readonly List<string> diagEvents = new List<string>();
        readonly List<string> diagWindows = new List<string>();
        float diagSessionStart;
        bool diagSessionActive;
        long diagFrames, diagOut, diagIn, diagUnreliable;
        double diagWorkMs, diagMaxMs;
        int diagQueueMax, diagPauses, diagGaps;
        float diagLongestPause, diagLongestGap;
        string diagLink = "";
        CSteamID pendingDiagFrom;
        string pendingDiagName = "";
        string lastDiagFile = "";
        float nextAutoShare;
        readonly PeerReportGate reportGate = new PeerReportGate();
        string lastAutoFrom = "";
        float lastAutoAt;
        // Automatic sharing (0.3.7): the friend never has to find or send a file.
        ConfigEntry<bool> autoShare;
        bool AutoShareOn { get { return DiagnosticsEnabled && autoShare != null && autoShare.Value; } }
        void BindDiagnosticsConfig()
        {
            try
            {
                if (!DiagnosticsEnabled) return;
                autoShare = Config.Bind("Diagnostics", "AutoShare", false, new ConfigDescription(
                    "Send this PC's network diagnostics (pauses, queue sizes, frame counts, OS/CPU/GPU/RAM names; no personal data) "
                    + "to the friend on the other end of the bridge every 2 minutes and when the session ends. The friend's PC keeps only the latest one."));
            }
            catch (Exception e) { Logger.LogWarning("Diagnostics config unavailable, auto-share stays on: " + e.Message); }
            Logger.LogInfo("Diagnostics auto-share: " + (AutoShareOn ? "on" : "off"));
        }
        void AutoShareTick()
        {
            if (!AutoShareOn || Time.realtimeSinceStartup < nextAutoShare) return;
            nextAutoShare = Time.realtimeSinceStartup + 120;
            foreach (CSteamID id in DiagPeers()) SendDiagnosticsTo(id, "автоматично (кожні 2 хв)", true);
        }
        // Before the goodbye packet, so the friend gets the final numbers of this session.
        void DiagShareOnExit()
        {
            if (!AutoShareOn || !diagSessionActive || !SteamManager.Initialized) return;
            try { foreach (CSteamID id in DiagPeers()) SendDiagnosticsTo(id, "автоматично (кінець сесії)", true); }
            catch (Exception e) { Logger.LogWarning("Final diagnostics share failed: " + e.Message); }
        }
        static string DiagSafe(string label)
        {
            string safe = Regex.Replace(label ?? "x", @"[^\w\-]", "_");
            return safe.Length > 24 ? safe.Substring(0, 24) : safe;
        }

        static string DiagClean(string text)
        {
            // Steam IDs are not needed to read a report.
            text = Regex.Replace(text, @"peer=\d+;\s*", "");
            return Regex.Replace(text, @"\b7656\d{13}\b", "<id>");
        }
        string DiagStamp()
        {
            int t = (int)Math.Max(0f, Time.realtimeSinceStartup - diagSessionStart);
            return "[+" + (t / 60).ToString("D2") + ":" + (t % 60).ToString("D2") + "] ";
        }
        void DiagNote(List<string> list, string text)
        {
            if (!DiagnosticsEnabled) return;
            list.Add(DiagStamp() + DiagClean(text));
            if (list.Count > DiagKeep) list.RemoveAt(0);
        }
        void DiagWarn(string text) { if (!DiagnosticsEnabled) return; Logger.LogWarning(text); DiagNote(diagEvents, text); }
        void DiagWindow(string text) { if (!DiagnosticsEnabled) return; Logger.LogInfo(text); DiagNote(diagWindows, text); }
        void DiagAccumulate()
        {
            diagFrames += statFrames; diagWorkMs += statTotalMs; if (statMaxMs > diagMaxMs) diagMaxMs = statMaxMs;
            if (statQueueMax > diagQueueMax) diagQueueMax = statQueueMax;
            diagOut += statSteamOut; diagIn += statSteamIn; diagUnreliable += statUnreliable;
        }
        void DiagBeginSession()
        {
            diagEvents.Clear(); diagWindows.Clear(); versionAnnounced.Clear(); peerVersions.Clear(); reportGate.Clear(); ResetFixCaches(); ResetTrafficWindows();
            diagFrames = diagOut = diagIn = diagUnreliable = 0; diagWorkMs = diagMaxMs = 0;
            diagQueueMax = diagPauses = diagGaps = 0; diagLongestPause = diagLongestGap = 0; diagLink = "";
            PingReset(); HealthReset(); StallReset();
            ResetProposals();
            diagSessionStart = Time.realtimeSinceStartup; diagSessionActive = true;
            nextAutoShare = Time.realtimeSinceStartup + 30;
        }
        // Called when a bridge session ends: keeps a local copy so the numbers survive the next game start.
        void DiagEndSession()
        {
            if (!DiagnosticsEnabled || !diagSessionActive) return;
            diagSessionActive = false;
            if (diagFrames == 0) return;
            try
            {
                string path = DiagFile("own");
                WriteReportAsync(path, BuildDiagnosticsReport("кінець сесії (автоматично)"));
                Logger.LogInfo("Diagnostics saved: " + path);
            }
            catch (Exception e) { Logger.LogWarning("Could not save diagnostics: " + e.Message); }
        }
        void WriteReportAsync(string path, string text)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                try { File.WriteAllText(path, text, new UTF8Encoding(true)); }
                catch (Exception e) { Logger.LogWarning("Report write failed: " + e.Message); }
            });
        }
        static string DiagFolder()
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "SteamBridgeDiagnostics");
            Directory.CreateDirectory(dir);
            return dir;
        }
        static string DiagFile(string label)
        {
            string safe = Regex.Replace(label ?? "x", @"[^\w\-]", "_");
            if (safe.Length > 24) safe = safe.Substring(0, 24);
            return Path.Combine(DiagFolder(), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + safe + ".txt");
        }

        string BuildDiagnosticsReport(string reason)
        {
            StringBuilder sb = new StringBuilder();
            int seconds = (int)Math.Max(0f, Time.realtimeSinceStartup - diagSessionStart);
            sb.AppendLine("Castle Story Steam Bridge " + BridgeVersion.Text + " — діагностика мережі");
            sb.AppendLine("Причина: " + reason);
            sb.AppendLine("Створено: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Роль: " + (hosting ? "хост (гостей: " + guests.Count + ")" : (bridge != null ? (host ? "хост" : "клієнт") : "активної сесії немає")));
            sb.AppendLine("Режим надсилання: " + (mixedSend ? SendMixed : SendReliable) + " (налаштований: " + SendModeName() + ")");
            sb.AppendLine("Тривалість сесії: " + (seconds / 60) + " хв " + (seconds % 60) + " с");
            sb.AppendLine("Останній відомий зв'язок: " + (diagLink.Length == 0 ? "невідомо" : diagLink.TrimStart(';', ' ')));
            sb.AppendLine("Кадри з мостом: " + diagFrames + "; середня робота моста " + (diagFrames == 0 ? 0 : diagWorkMs / diagFrames).ToString("F2") + " мс; максимум " + diagMaxMs.ToString("F1") + " мс");
            sb.AppendLine("Черга Steam (максимум): " + diagQueueMax + " Б");
            sb.AppendLine("Пакети Steam: вийшло " + diagOut + " (ненадійних " + diagUnreliable + "), прийшло " + diagIn);
            sb.AppendLine("Паузи головного циклу гри (≥ 1 с): " + diagPauses + (diagPauses == 0 ? "" : "; найдовша " + diagLongestPause.ToString("F1") + " с"));
            sb.AppendLine("Прогалини ігрових даних від друга (≥ 3 с): " + diagGaps + (diagGaps == 0 ? "" : "; найдовша " + diagLongestGap.ToString("F1") + " с"));
            AppendPingReport(sb);
            AppendHealthReport(sb);
            AppendStallReport(sb);
            try
            {
                sb.AppendLine("Система: " + SystemInfo.operatingSystem + "; процесор " + SystemInfo.processorType + "; відеокарта " + SystemInfo.graphicsDeviceName
                    + "; ОЗП " + SystemInfo.systemMemorySize + " МБ; Unity " + Application.unityVersion);
            }
            catch (Exception) { }
            sb.AppendLine();
            sb.AppendLine("--- Останні події (паузи, прогалини, повільна робота): " + diagEvents.Count);
            foreach (string line in diagEvents) sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("--- Останні вікна статистики по 15 с: " + diagWindows.Count);
            foreach (string line in diagWindows) sb.AppendLine(line);
            string text = sb.ToString();
            return text.Length > DiagTextLimit ? text.Substring(0, DiagTextLimit) : text;
        }

        // Peers with a ready bridge: the single friend for a client, every connected guest for a host.
        List<CSteamID> DiagPeers()
        {
            List<CSteamID> result = new List<CSteamID>();
            if (hosting) { foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) if (g.Value.Ready) result.Add(g.Key); }
            else if (bridge != null && bridge.Ready) result.Add(peer);
            return result;
        }
        bool SendDiagnosticsTo(CSteamID id, string reason, bool auto = false)
        {
            if (!IsFriend(id)) return false;   // 0.3.40: never hand logs to non-friends
            try
            {
                byte[] data = Encoding.UTF8.GetBytes((auto ? "#auto\n" : "") + BuildDiagnosticsReport(reason));
                if (data.Length > BridgeCore.MaxDiagnosticsBytes) Array.Resize(ref data, BridgeCore.MaxDiagnosticsBytes);
                return SendTo(id, BridgeCore.Packet(BridgeCore.KindDiagnosticsReport, data, data.Length));
            }
            catch (Exception e) { Logger.LogWarning("Diagnostics send failed: " + e.Message); return false; }
        }
        void ShareDiagnostics()
        {
            List<CSteamID> peers = DiagPeers();
            if (peers.Count == 0) { status = "Немає підключеного друга, якому можна надіслати діагностику."; return; }
            int ok = 0;
            foreach (CSteamID id in peers) if (SendDiagnosticsTo(id, "надіслано гравцем")) ok++;
            status = "Діагностику надіслано: " + ok + " з " + peers.Count + ". Друг побачить її, якщо в нього версія 0.3.7.";
            Logger.LogInfo(status);
        }
        void RequestDiagnostics()
        {
            List<CSteamID> peers = DiagPeers();
            if (peers.Count == 0) { status = "Немає підключеного друга, в якого можна попросити діагностику."; return; }
            int ok = 0;
            foreach (CSteamID id in peers)
            {
                try { if (SendTo(id, BridgeCore.Packet(BridgeCore.KindDiagnosticsRequest, null, 0))) ok++; }
                catch (Exception e) { Logger.LogWarning("Diagnostics request failed: " + e.Message); }
            }
            status = "Запит надіслано: " + ok + " з " + peers.Count + ". Друг має підтвердити (потрібна версія 0.3.7).";
            Logger.LogInfo(status);
        }
        void OnDiagnosticsPacket(CSteamID from, byte kind, byte[] payload)
        {
            if (!DiagnosticsEnabled) return;
            try
            {
                if (!IsFriend(from)) return;
                if (kind == BridgeCore.KindDiagnosticsRequest)
                {
                    pendingDiagFrom = from; pendingDiagName = SteamFriends.GetFriendPersonaName(from);
                    visible = true;
                    status = "Друг " + pendingDiagName + " просить діагностику мосту. Підтверди або відхили у цьому вікні.";
                    Logger.LogInfo("Diagnostics requested by " + pendingDiagName);
                    return;
                }
                if (kind != BridgeCore.KindDiagnosticsReport || payload == null || payload.Length == 0) return;
                string name = SteamFriends.GetFriendPersonaName(from);
                string text = Encoding.UTF8.GetString(payload);
                bool previous = text.StartsWith("#prev\n");
                if (previous)
                {
                    if (!reportGate.Accept(from.m_SteamID, 2, Time.realtimeSinceStartup, 2f)) return;
                    text = text.Substring(6);
                }
                bool auto = !previous && text.StartsWith("#auto\n");
                if (auto)
                {
                    // Automatic reports keep only the latest one per friend, silently.
                    if (!reportGate.Accept(from.m_SteamID, 1, Time.realtimeSinceStartup, 20f)) return;
                    text = text.Substring(6);
                }
                else
                {
                    if (!previous && !reportGate.Accept(from.m_SteamID, 3, Time.realtimeSinceStartup, 5f)) return;
                }
                StringBuilder clean = new StringBuilder(text.Length);
                foreach (char c in text) if (c >= ' ' || c == '\n' || c == '\r' || c == '\t') clean.Append(c);
                string path = previous ? Path.Combine(DiagFolder(), "previous-from-" + DiagSafe(name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".txt") : (auto ? Path.Combine(DiagFolder(), "latest-from-" + DiagSafe(name) + ".txt") : DiagFile("from-" + name));
                WriteReportAsync(path, clean.ToString());
                lastDiagFile = path;
                if (previous)
                {
                    status = "Отримано звіт зі старого логу друга " + name + ". Файл: " + path;
                    Logger.LogInfo(status);
                }
                else if (auto)
                {
                    lastAutoFrom = name; lastAutoAt = Time.realtimeSinceStartup;
                    Logger.LogInfo("Latest diagnostics of " + name + " saved: " + path);
                }
                else
                {
                    status = "Отримано діагностику від " + name + ". Файл: " + path;
                    Logger.LogInfo(status); visible = true;
                }
            }
            catch (Exception e) { Logger.LogWarning("Diagnostics packet failed: " + e.Message); }
        }
        void DrawDiagnostics(bool connected)
        {
            if (!DiagnosticsEnabled) return;
            if (pendingDiagFrom.m_SteamID != 0)
            {
                GUILayout.Label("Друг " + pendingDiagName + " просить діагностику мосту (лише мережеві показники й назви обладнання).");
                if (GUILayout.Button("Надіслати другові"))
                {
                    SendDiagnosticsTo(pendingDiagFrom, "запит друга " + pendingDiagName);
                    status = "Діагностику надіслано другові " + pendingDiagName + "."; pendingDiagFrom = default(CSteamID);
                }
                if (GUILayout.Button("Відхилити")) { pendingDiagFrom = default(CSteamID); status = "Запит діагностики відхилено."; }
            }
            if (autoShare != null && GUILayout.Button("Автонадсилання діагностики другові: " + (autoShare.Value ? "УВІМКНЕНО" : "вимкнено") + " (натисни, щоб змінити)")) autoShare.Value = !autoShare.Value;
            if (lastAutoFrom.Length > 0) GUILayout.Label("Остання автодіагностика від " + lastAutoFrom + ": " + ((int)(Time.realtimeSinceStartup - lastAutoAt)) + " с тому");
            if (!connected) return;
            GUILayout.Label("Діагностика мережі (потрібна версія 0.3.7 в обох):");
            if (GUILayout.Button("Надіслати мою діагностику другові")) ShareDiagnostics();
            if (GUILayout.Button("Попросити діагностику в друга (він підтвердить)")) RequestDiagnostics();
            if (lastDiagFile.Length > 0) GUILayout.Label("Остання отримана: " + lastDiagFile);
        }
    }
}
