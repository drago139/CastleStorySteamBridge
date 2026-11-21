using System;
using System.Collections.Generic;
using System.Text;

namespace CastleStorySteam
{
    // 0.3.98: the detailed trace lines (LUALIVE, LUACALLS, UNITSHOT, WARDSHOT, MEMSTATS, STATS, LUA-PRUNE, GAMEPLAY-FIX, SENTINEL and every HITCH >= 700 ms) are also kept in a small ring (~16 KB, newest last) and
    // appended to the automatic diagnostics report that goes to the Steam friend every 2 minutes and at the end of the session (Diagnostics/AutoShareToFriend), so the user gets them during the game without
    // asking for a file. Pure copy of text that is already written to perf-trace.txt; no personal data (no Steam IDs / names in these lines).
    public sealed partial class SteamBridgePlugin
    {
        static readonly Queue<string> dlLines = new Queue<string>(); static int dlChars;
        static readonly string[] dlTags = { "LUALIVE", "LUACALLS", "UNITSHOT", "WARDSHOT", "MEMSTATS", "STATS t=", "LUA-PRUNE", "GAMEPLAY-FIX", "SENTINEL", "FLOATING", "BURIED", "GCPROBE", "TREECUT", "ORANGE-WARD RECIPE", "COMBAT-ANOM", "WATCHDOG", "SFV-GUARD", "SFV-COUNT", "SFV-STALE", "LOG-LIMIT", "HITCH " };
        const int DlMaxChars = 16000;

        static void DlKeep(string text)
        {
            try
            {
                bool ok = false;
                for (int i = 0; i < dlTags.Length; i++) if (text.StartsWith(dlTags[i], StringComparison.Ordinal)) { ok = true; break; }
                if (!ok) return;
                if (text.StartsWith("HITCH ", StringComparison.Ordinal))
                {
                    int sp = text.IndexOf(' ', 6); int ms;
                    if (sp < 0 || !int.TryParse(text.Substring(6, sp - 6), out ms) || ms < 700) return;
                }
                if (text.Length > 1500) text = text.Substring(0, 1500);
                string line = DateTime.Now.ToString("HH:mm:ss") + " " + text;
                lock (dlLines)
                {
                    dlLines.Enqueue(line); dlChars += line.Length + 2;
                    while (dlChars > DlMaxChars && dlLines.Count > 1) dlChars -= dlLines.Dequeue().Length + 2;
                }
            }
            catch (Exception) { }
        }

        static string DlSection()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                lock (dlLines)
                {
                    sb.AppendLine("--- Live trace lines (newest last): " + dlLines.Count);
                    foreach (string l in dlLines) sb.AppendLine(l);
                }
                sb.AppendLine();
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }
    }
}
