using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.89 session report to the friend (the old automatic diagnostics stay compiled out, D-040: they added per-frame work).
    // Every 5 min and when the bridge stops, a background thread reads only what is NEW in this PC's perf-trace.txt and the game's
    // output_log.txt and keeps the important lines: hitches >= 1 s, freezes, per-minute error/exception counts, game exceptions (first line
    // + 3 stack lines, repeats counted), pause / sync / outage / rest-sync / slow-lane lines, level ready. Steam IDs and the Windows user name
    // in paths are replaced. At most 40 KB, sent on the slow lane (channel 80) to connected Steam friends with bridge 0.3.89+ (kind 18).
    // The receiver keeps SteamBridgeDiagnostics\session-from-<friend>-<time>.txt, the last 50 per friend. Config [Diag] ShareSessionReport.
    public sealed partial class SteamBridgePlugin
    {
        const byte KindSessionReport = BridgeCore.KindSessionReport;
        static ConfigEntry<bool> ssCfg; static long ssPtPos = -1, ssOutPos = -1; static float ssNext; static volatile string ssReady; static volatile bool ssBusy; static int ssSent, ssGot;
        static readonly Regex ssKeep = new Regex(@"HITCH \d{4,} ms|FREEZE|LOGS 60s: errors [1-9]|exceptions [1-9]|HOST-PAUSE|PAUSE-SYNC|PAUSE-RESYNC|TIMER-SYNC (large|first|new)|OUTAGE-RESYNC|ATMOSPHERE|REST-SYNC failed|failed:|SLOW-LANE|LEVEL-READY|LOWMEM|LIVEMEM|LINK-ALERT|UNETQ-ALERT|CMD-RECV CmdSetFactionReady|CMD-RECV CmdJoinSpectator|ORANGE-WARD RECIPE|HOSTDROP|STEAMJOIN no answer|STEAMSTATE|BUGPACK|PICKER-GUARD|DAMAGED-BLOCK-FIX|HARVESTER-NULLMAT|CATAPULT-CLICK|STRIKE-GUARD|COMBAT-ANOM|MOD-INV|MOD-CONFLICT|MOD-DATA|MOD-ANCHOR|MOD-MISMATCH|MOD-MATCH|MOD-LOBBY|MODS-HASH|GA-PROBE|GA-GUARD|GA-DROP summary|GCPROBE on|WATCHDOG|RESILIENCE|HOST-REHELLO|P2P-HOLD|SFV-GUARD|SFV-COUNT|SFV-STALE|LOG-LIMIT|GAMEPLAY-FIX Door ", RegexOptions.Compiled);   // 0.4.22: host door lines (attack, demolition, side candidates) reach the guest report (B-074), at most 40 per report

        void InstallSessionShare()
        {
            try
            {
                ssCfg = Config.Bind("Diag", "ShareSessionReport", true, "Every 5 min and at the end of a session send a short technical report (hitches, freezes, game exceptions, pause/sync lines from perf-trace.txt and output_log.txt; Steam IDs and the Windows user name removed) to the connected Steam friend, slowly on a separate channel. The friend keeps the last 50.");
                ssNext = Time.realtimeSinceStartup + 300f;
                Logger.LogInfo("SESSION-REPORT ready (" + (ssCfg.Value ? "on" : "off") + ")");
            }
            catch (Exception e) { Logger.LogWarning("SESSION-REPORT unavailable: " + e.Message); }
        }

        // 0.4.5: reports of one friend are kept for about 13 hours of play (160 x 5 min; it was 50 = 4 h), so a 9-hour session stays covered.
        const int SsKeepFiles = 160;
        static float ssLastNudge;

        // 0.4.5: after a stall ends the next report goes out within seconds instead of up to 5 min later (at most one early report per minute).
        internal static void SessionShareNudge(float now)
        {
            if (ssCfg == null || !ssCfg.Value || now - ssLastNudge < 60f) return;
            ssLastNudge = now;
            if (ssNext > now + 3f) ssNext = now + 3f;
        }

        // 0.4.5: one LINK / UNETQ sample per minute and peer travels in the report (the 5-s lines would flood it): the line whose clock second is 0-4.
        static bool SsPeriodic(string l)
        {
            if (l == null || l.Length < 30 || l[2] != ':' || l[5] != ':' || !char.IsDigit(l[6]) || !char.IsDigit(l[7])) return false;
            if ((l[6] - '0') * 10 + (l[7] - '0') >= 5) return false;
            int t = l.IndexOf(" LINK ", 8, StringComparison.Ordinal);
            if (t < 0) t = l.IndexOf(" UNETQ ", 8, StringComparison.Ordinal);
            return t >= 0 && t < 40;
        }

        static string SsPerfPath() { return Path.Combine(BepInEx.Paths.BepInExRootPath, "perf-trace.txt"); }
        static string SsOutPath() { return Path.Combine(Application.dataPath, "output_log.txt"); }

        void SessionShareTick()
        {
            if (ssCfg == null || !ssCfg.Value) return;
            string ready = ssReady;
            if (ready != null) { ssReady = null; SsSend(ready); }
            float now = Time.realtimeSinceStartup;
            if (now < ssNext) return;
            ssNext = now + 300f;
            SsBuild("every 5 min");
        }

        // bridge is stopping: build synchronously-small and send what is queued (the slow lane may not finish if the game quits at once)
        void SessionShareOnStop()
        {
            try { if (ssCfg == null || !ssCfg.Value || ssBusy || SsTargets().Count == 0) return; string r = SsCollect("session end"); byte[] data = Encoding.UTF8.GetBytes(r); if (data.Length > 40 * 1024) Array.Resize(ref data, 40 * 1024); byte[] pk = BridgeCore.Packet(KindSessionReport, data, data.Length); foreach (CSteamID id in SsTargets()) SendTo(id, pk); PtLog("SESSION-REPORT final " + data.Length + " B sent"); } catch (Exception) { }   // game traffic is over: old channel, at once, before the goodbye
        }

        void SsBuild(string why)
        {
            if (ssBusy || SsTargets().Count == 0) return;
            ssBusy = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                try { ssReady = SsCollect(why); } catch (Exception) { } finally { ssBusy = false; }
            });
        }

        List<CSteamID> SsTargets()
        {
            List<CSteamID> r = new List<CSteamID>();
            try
            {
                if (hosting) { foreach (var g in guests) if (g.Value.Ready && IsFriend(g.Key) && SlowCapable(g.Key)) r.Add(g.Key); }
                else if (bridge != null && bridge.Ready && IsFriend(peer) && SlowCapable(peer)) r.Add(peer);
            }
            catch (Exception) { }
            return r;
        }

        void SsSend(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text); if (data.Length > 40 * 1024) Array.Resize(ref data, 40 * 1024);
            byte[] pk = BridgeCore.Packet(KindSessionReport, data, data.Length); int n = 0;
            foreach (CSteamID id in SsTargets()) if (SendBulk(id, pk)) n++;
            if (n > 0) { ssSent++; PtLog("SESSION-REPORT queued " + data.Length + " B to " + n + " friend(s) (#" + ssSent + ")"); }
        }

        // runs on a worker thread (or once at stop): only file IO and text
        static string SsCollect(string why)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("SESSION-REPORT v1 bridge ").Append(BridgeVersion.Text).Append(" ").Append(BridgeVersion.Build).Append(", ").Append(why).Append(", local time ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            string modLine = ModInventory.ReportLine; if (!string.IsNullOrEmpty(modLine)) sb.Append(modLine).Append('\n');   // 0.4.14 MOD-INV: in EVERY report (startup lines may fall outside the read window)
            sb.Append("== perf-trace (new important lines)\n");
            int keep = 0;
            int anomDetail = 0, doorDetail = 0;   // doorDetail: 0.4.22 cap of GAMEPLAY-FIX Door lines (the demolition "already assigned" note repeats)   // 0.4.13: at most BugPackRules.AnomReportCap COMBAT-ANOM detail lines per report (its minute / installed / error lines always), so a big fight cannot push out FREEZE / HITCH lines
            foreach (string l in SsNewLines(SsPerfPath(), ref ssPtPos, 4 * 1024 * 1024)) if ((ssKeep.IsMatch(l) || SsPeriodic(l)) && keep < 400) { if (!BugPackRules.AnomAlwaysKeep(l) && ++anomDetail > BugPackRules.AnomReportCap) continue; if (l.IndexOf("GAMEPLAY-FIX Door ", StringComparison.Ordinal) >= 0 && ++doorDetail > 40) continue; sb.Append(l.Length > 300 ? l.Substring(0, 300) : l).Append('\n'); keep++; }
            sb.Append("== game exceptions (new, first line + 3 stack lines, repeats counted)\n");
            List<string> lines = SsNewLines(SsOutPath(), ref ssOutPos, 8 * 1024 * 1024);
            Dictionary<string, int> seen = new Dictionary<string, int>(); List<string> order = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i]; if (l.IndexOf("Exception", StringComparison.Ordinal) < 0 || l.StartsWith(" ") || l.IndexOf("GA Error", StringComparison.Ordinal) >= 0) continue;
                StringBuilder block = new StringBuilder(l.Length > 200 ? l.Substring(0, 200) : l);
                for (int k = 1; k <= 3 && i + k < lines.Count; k++) { string s = lines[i + k].Trim(); if (s.Length == 0) break; block.Append("\n    ").Append(s.Length > 200 ? s.Substring(0, 200) : s); }
                string key = block.ToString(); int c; if (seen.TryGetValue(key, out c)) seen[key] = c + 1; else { seen[key] = 1; order.Add(key); }
            }
            foreach (string k in order) { if (sb.Length > 38 * 1024) break; sb.Append(seen[k]).Append("x ").Append(k).Append('\n'); }
            if (keep == 0 && order.Count == 0) sb.Append("(nothing important since the last report)\n");
            return SsClean(sb.ToString());
        }

        static string SsClean(string text)
        {
            text = Regex.Replace(text, @"\b7656\d{13}\b", "<id>");
            return Regex.Replace(text, @"(?i)([a-z]:[\\/]+users[\\/]+)[^\\/\s]+", "$1<user>");
        }

        // new complete lines since the last call; the first call starts at the current end minus 'tail' bytes (this run only, bounded)
        static List<string> SsNewLines(string path, ref long pos, long tail)
        {
            List<string> r = new List<string>();
            try
            {
                if (!File.Exists(path)) return r;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (pos < 0 || pos > len) pos = Math.Max(0, len - Math.Min(tail, 256 * 1024));   // first report or rotated file: recent part only
                    if (len - pos > tail) pos = len - tail;
                    fs.Seek(pos, SeekOrigin.Begin);
                    byte[] buf = new byte[len - pos]; int got = 0; while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
                    int last = Array.LastIndexOf(buf, (byte)'\n', got - 1 < 0 ? 0 : got - 1); if (last < 0) return r;
                    pos += last + 1;
                    foreach (string l in Encoding.UTF8.GetString(buf, 0, last).Split('\n')) r.Add(l.TrimEnd('\r'));
                }
            }
            catch (Exception) { }
            return r;
        }

        void SsForward(CSteamID from, string name, string text)
        {
            try
            {
                if (ssCfg == null || !ssCfg.Value) return;
                byte[] data = Encoding.UTF8.GetBytes("#fwd " + name + "\n" + text); if (data.Length > 40 * 1024) Array.Resize(ref data, 40 * 1024);
                byte[] pk = BridgeCore.Packet(KindSessionReport, data, data.Length); int n = 0;
                foreach (KeyValuePair<CSteamID, BridgeCore> g in guests)
                    if (g.Key != from && g.Value.Ready && IsFriend(g.Key) && SlowCapable(g.Key) && SendBulk(g.Key, pk)) n++;
                if (n > 0) PtLog("SESSION-REPORT passed on from " + name + " to " + n + " friend(s)");
            }
            catch (Exception) { }
        }

        void OnSessionReportPacket(CSteamID from, byte[] payload)
        {
            try
            {
                if (payload == null || payload.Length == 0 || !IsFriend(from)) return;
                string name = DiagSafe(SteamFriends.GetFriendPersonaName(from)), dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "SteamBridgeDiagnostics");
                Directory.CreateDirectory(dir);
                StringBuilder clean = new StringBuilder(payload.Length);
                foreach (char c in Encoding.UTF8.GetString(payload)) if (c >= ' ' || c == '\n' || c == '\r' || c == '\t') clean.Append(c);
                string text = clean.ToString(), origin = name;
                // 0.4.5: the host passes a guest's report on to the other guests (guests have no link to each other); the first line says whose it is
                bool forwarded = text.StartsWith("#fwd ", StringComparison.Ordinal);
                if (forwarded)
                {
                    int nl = text.IndexOf('\n');
                    if (nl > 5 && nl < 90) { origin = DiagSafe(text.Substring(5, nl - 5).Trim()); text = "(passed on by the host " + name + ")\n" + text.Substring(nl + 1); }
                    else forwarded = false;
                }
                else if (hosting) SsForward(from, name, text);
                string prefix = "session-from-" + origin + "-", path = Path.Combine(dir, prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                System.Threading.ThreadPool.QueueUserWorkItem(delegate(object unused)
                {
                    try
                    {
                        File.WriteAllText(path, text, new UTF8Encoding(true));
                        string[] old = Directory.GetFiles(dir, prefix + "*.txt");
                        if (old.Length > SsKeepFiles) { Array.Sort(old, StringComparer.Ordinal); for (int i = 0; i < old.Length - SsKeepFiles; i++) File.Delete(old[i]); }
                    }
                    catch (Exception) { }
                });
                ssGot++; PtLog("SESSION-REPORT received from " + name + " (" + payload.Length + " B) -> " + Path.GetFileName(path));
            }
            catch (Exception e) { PtLog("SESSION-REPORT receive failed: " + e.Message); }
        }
    }
}
