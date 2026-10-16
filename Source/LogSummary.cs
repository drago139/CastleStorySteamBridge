using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CastleStorySteam
{
    // Compact network-only summary of an old game/plugin log (0.3.7). The raw log never leaves the PC:
    // only counts and the lines about pauses, data gaps, slow work and connection failures are kept.
    internal static class LogSummary
    {
        static readonly string[] EventMarkers = new string[]
        {
            "Main game loop pause", "Game data gap", "Slow bridge work", "Steam P2P failure", "Guest Steam failure",
            "Хост покинув гру", "Друг покинув гру", "Міст зупинено", "Втрачено зв'язок", "Connecting game to loopback", "Multi-client host ready"
        };

        internal static string Clean(string line)
        {
            line = Regex.Replace(line, @"peer=\d+;\s*", "");
            line = Regex.Replace(line, @"\b7656\d{13}\b", "<id>");
            line = Regex.Replace(line, @"[A-Za-z]:\\[^\s;,\)""']+", "<path>");
            return line.Length > 300 ? line.Substring(0, 300) : line;
        }

        internal static string Build(string path, string label, int maxChars)
        {
            FileInfo info = new FileInfo(path);
            List<KeyValuePair<int, string>> events = new List<KeyValuePair<int, string>>();
            List<KeyValuePair<int, string>> stats = new List<KeyValuePair<int, string>>();
            int total = 0, nre = 0, statLines = 0, pauses = 0, gaps = 0, slow = 0;
            string loading = null;
            using (StreamReader reader = new StreamReader(path, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    total++;
                    if (line.IndexOf("NullReferenceException", StringComparison.Ordinal) >= 0) nre++;
                    if (loading == null && line.IndexOf("Loading [Castle Story Steam Bridge", StringComparison.Ordinal) >= 0) loading = Clean(line);
                    if (line.IndexOf("Bridge stats", StringComparison.Ordinal) >= 0) { statLines++; stats.Add(new KeyValuePair<int, string>(total, Clean(line))); continue; }
                    foreach (string marker in EventMarkers)
                        if (line.IndexOf(marker, StringComparison.Ordinal) >= 0)
                        {
                            if (marker == "Main game loop pause") pauses++;
                            else if (marker == "Game data gap") gaps++;
                            else if (marker == "Slow bridge work") slow++;
                            events.Add(new KeyValuePair<int, string>(total, Clean(line)));
                            break;
                        }
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Звіт зі старого логу: " + label);
            sb.AppendLine("Файл: " + Path.GetFileName(path) + "; розмір " + info.Length + " Б; змінено " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + "; рядків " + total);
            if (loading != null) sb.AppendLine("Версія моста в тому запуску: " + loading);
            sb.AppendLine("Лічильники: Main game loop pause " + pauses + "; Game data gap " + gaps + "; Slow bridge work " + slow + "; рядків Bridge stats " + statLines + "; NullReferenceException " + nre);
            sb.AppendLine();
            sb.AppendLine("--- Події (до 80, номер рядка / усього рядків)");
            int step = events.Count <= 80 ? 1 : (events.Count + 79) / 80;
            for (int i = 0; i < events.Count; i += step) sb.AppendLine("[" + events[i].Key + "/" + total + "] " + events[i].Value);
            sb.AppendLine();
            sb.AppendLine("--- Вибірка Bridge stats (до 40)");
            step = stats.Count <= 40 ? 1 : (stats.Count + 39) / 40;
            for (int i = 0; i < stats.Count; i += step) sb.AppendLine("[" + stats[i].Key + "/" + total + "] " + stats[i].Value);
            string text = sb.ToString();
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }
    }
}
