using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.4.10 IN-GAME TEST BOT (glue between the pure runner and the game). OFF for everybody unless ALL of these hold on THIS PC:
    //   [Test] Enabled = true in the cfg, the marker file BepInEx\config\virtual-player.enable exists, BepInEx\config\bot.stop does NOT exist.
    // Then a script dropped as BepInEx\config\bot-script.txt is moved into BepInEx\bot\runs\<stamp>-<run>-<role>\script.txt and run step by step
    // (language: BotScript.cs, rules: BotRunner.cs, README: PROJ\VirtualPlayer\Bot\README.md). Every result line is appended at once to
    // <run folder>\result.txt and logged as "BOT ..." in perf-trace.txt; at the end the file is copied to BepInEx\bot-result.txt (the driver waits for it).
    // Kill switch: create BepInEx\config\bot.stop, press Ctrl+Shift+F10, delete the marker or set [Test] Enabled = false -> the run is aborted at once.
    // The bot only sends the commands a human player's input sends (same Cmd paths) and writes local files; it sends no packet of its own and
    // changes nothing for players without the marker. Barriers: files in BepInEx\bot\sync\<run>\ (+ the folder named in BepInEx\config\bot-peer.txt,
    // used by the host to see the arrival files of a guest that runs in the Sandboxie box).
    public sealed partial class SteamBridgePlugin
    {
        static BotRunner botRun; static BotGameWorld botWorld; static readonly BotLogRing botRing = new BotLogRing(8000);
        static volatile bool botTapOn; static bool botListenerAdded, botArmed, botWaitingLogged, botShotHideGui; static float botNextFileCheck;
        static string botRunDir, botResultPath, botPeerSync, botLastVerdict; static BotLogTap botTap;
        static StringBuilder vpCapture;   // while the bot runs a 0.3.78 command, VpLog writes into this instead of the old result file

        static string BotConfigFile(string name) { return Path.Combine(BepInEx.Paths.ConfigPath, name); }

        // Sandboxie shows the real folder's files inside the box ("read-through") unless the box has its own copy. A game in the box therefore
        // uses its own inbox names (bot-script-box.txt, bot-peer-box.txt): it can never pick up the script meant for the main game. bot.stop and
        // the marker keep one name, so one kill-switch file stops both games.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        static int botSandboxed = -1;
        internal static bool BotInSandbox { get { if (botSandboxed < 0) { try { botSandboxed = GetModuleHandle("SbieDll.dll") != IntPtr.Zero ? 1 : 0; } catch (Exception) { botSandboxed = 0; } } return botSandboxed == 1; } }
        static string BotInboxName { get { return BotInSandbox ? "bot-script-box.txt" : "bot-script.txt"; } }
        static string BotPeerName { get { return BotInSandbox ? "bot-peer-box.txt" : "bot-peer.txt"; } }
        static string BotRoot { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "bot"); } }
        internal static bool BotRunActive { get { return botRun != null && !botRun.Finished; } }
        internal static bool BotArmedNow { get { return botTapOn; } }
        static bool botHooksDone;

        // called from PtLog (PerfTrace.cs) and the log listener; cheap when the bot is not armed
        internal static void BotTap(string source, string text) { if (!botTapOn) return; try { botRing.Add(source, text); } catch (Exception) { } }

        static string BotNetRole()
        {
            try { if (NetworkServer.active) return "host"; if (NetworkClient.active) return "guest"; } catch (Exception) { }
            return "menu";
        }

        void BotTick()
        {
            if (!TestOn) { if (botArmed) BotDisarm("[Test] Enabled is false"); return; }
            float now = Time.realtimeSinceStartup;
            try
            {
                if (now >= botNextFileCheck)
                {
                    botNextFileCheck = now + 0.5f;
                    bool marker = File.Exists(BotConfigFile("virtual-player.enable")), stop = File.Exists(BotConfigFile("bot.stop"));
                    if (!marker || stop) { if (botArmed) BotDisarm(stop ? "kill switch file bot.stop" : "marker virtual-player.enable removed"); }
                    else if (!botArmed) BotArm();
                    // 0.4.11: the driver asks a still running game to switch [Test] Enabled back off (saved to the cfg at once); the bot disarms next tick
                    string off = BotConfigFile("bot-test-off.txt");
                    if (File.Exists(off))
                    {
                        try { File.Delete(off); } catch (Exception) { }
                        if (testEnabled != null && testEnabled.Value) { if (BotRunActive) BotAbort("[Test] Enabled switched off on request"); testEnabled.Value = false; PtLog("BOT [Test] Enabled set to false on request (bot-test-off.txt, the driver's restore)"); return; }
                    }
                    if (botArmed && !BotRunActive) BotTryStart(now);
                    else if (!botArmed && !botWaitingLogged && File.Exists(BotConfigFile(BotInboxName))) { botWaitingLogged = true; PtLog("BOT script waiting but the bot is not armed (needs [Test] Enabled + virtual-player.enable, no bot.stop)"); }
                    if (botTap != null && BotRunActive) botTap.PollBrix();
                }
                if (botArmed && !BotRunActive) BotGridHotkeys(now);   // 0.4.18 Ctrl+Shift+G grid / Ctrl+Shift+H ungrid (BotGrid.cs)
                if (!botArmed || !BotRunActive) return;
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl), shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (ctrl && shift && Input.GetKeyDown(KeyCode.F10)) { BotAbort("Ctrl+Shift+F10 pressed"); return; }
                botWorld.Frame(now);
                botRun.Tick(now);
                if (botRun.Finished) BotFinished();
            }
            catch (Exception e)
            {
                PtLog("BOT tick failed: " + e.GetType().Name + " " + e.Message);
                if (BotRunActive) BotAbort("internal error " + e.GetType().Name + ": " + e.Message);
            }
        }

        void BotArm()
        {
            botArmed = true; botWaitingLogged = false; botTapOn = true;
            if (botTap == null) botTap = new BotLogTap();
            if (!botListenerAdded) { botListenerAdded = true; try { BepInEx.Logging.Logger.Listeners.Add(botTap); } catch (Exception e) { PtLog("BOT log listener not added: " + e.Message); } }
            botTap.ArmUnityFallback();
            if (!botHooksDone)
            {   // log-only Harmony postfix (Recipe.Finish -> "BOT-CRAFTED"), installed only once a bot is armed on this PC
                botHooksDone = true;
                try { BotCraft.InstallFinishHook(new HarmonyLib.Harmony("local.castlestory.steambridge.bot")); } catch (Exception e) { PtLog("BOT craft hook not installed: " + e.Message); }
            }
            PtLog("BOT armed: [Test] Enabled + virtual-player.enable (stop: bot.stop or Ctrl+Shift+F10); inbox BepInEx\\config\\bot-script.txt");
        }

        void BotDisarm(string why)
        {
            if (BotRunActive) BotAbort(why);
            botArmed = false; botTapOn = false;   // the listener stays registered (removing it while another thread logs is not safe) but stores nothing
            PtLog("BOT disarmed: " + why);
        }

        void BotAbort(string why)
        {
            if (!BotRunActive) return;
            botRun.Abort(why, Time.realtimeSinceStartup); BotFinished();
        }

        void BotTryStart(float now)
        {
            string inbox = BotConfigFile(BotInboxName);
            if (!File.Exists(inbox)) return;
            string[] lines;
            try { lines = File.ReadAllLines(inbox, Encoding.UTF8); File.Delete(inbox); }
            catch (Exception e) { PtLog("BOT script read failed: " + e.Message); botNextFileCheck = now + 5f; return; }
            BotScriptDoc doc = BotScript.Parse(lines);
            string run = doc.Get("run", "solo"); if (!BotRules.ValidName(run, 60)) run = "solo";
            string declared = doc.Get("role", "auto"), role = declared == "host" || declared == "guest" ? declared : BotNetRole();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            botRunDir = Path.Combine(Path.Combine(BotRoot, "runs"), stamp + "-" + run + "-" + role);
            try { Directory.CreateDirectory(botRunDir); File.WriteAllLines(Path.Combine(botRunDir, "script.txt"), lines, new UTF8Encoding(true)); } catch (Exception e) { PtLog("BOT run folder failed: " + e.Message); }
            botResultPath = Path.Combine(botRunDir, "result.txt");
            BotTrimFolder(Path.Combine(BotRoot, "runs"), 40); BotTrimFolder(Path.Combine(BotRoot, "sync"), 20);
            botPeerSync = null;
            try { string pf = BotConfigFile(BotPeerName); if (File.Exists(pf)) { string p = File.ReadAllText(pf, Encoding.UTF8).Trim(); if (p.Length > 0 && Path.IsPathRooted(p)) botPeerSync = p; } } catch (Exception) { }
            botWorld = new BotGameWorld(this, run, role);
            // 0.4.11: the own files of an earlier run with the same id would satisfy a barrier falsely; the host publishes its join code for 'join code'
            string syncRole = role == "single" ? "host" : role;
            try
            {
                string sd = Path.Combine(Path.Combine(BotRoot, "sync"), run);
                if (Directory.Exists(sd)) foreach (string old in Directory.GetFiles(sd)) { string fn = Path.GetFileName(old); if (fn.EndsWith("." + syncRole + ".arrived", StringComparison.OrdinalIgnoreCase) || string.Equals(fn, syncRole + ".done", StringComparison.OrdinalIgnoreCase)) File.Delete(old); }
            }
            catch (Exception) { }
            if (syncRole == "host") { try { BotWriteSync(run, "host-code.txt", SteamUser.GetSteamID().m_SteamID.ToString()); } catch (Exception e) { PtLog("BOT host code not written: " + e.Message); } }
            botShotHideGui = false;
            if (botTap != null) botTap.StartBrix();
            string info = "mod=" + BridgeVersion.Text + " build=" + BridgeVersion.Build + " started=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " net=" + BotNetRole() + (botPeerSync != null ? " peer=on" : "");
            botRun = new BotRunner(doc, botWorld, botRing, now, info);
            PtLog("BOT run started: " + botRunDir + " (" + doc.Steps.Count + " steps, " + doc.Errors.Count + " errors, sandboxed=" + BotInSandbox + ")");
            if (botRun.Finished) BotFinished();
        }

        void BotFinished()
        {
            if (botRun == null) return;
            botLastVerdict = botRun.Report.Verdict;
            try { if (botResultPath != null && File.Exists(botResultPath)) File.Copy(botResultPath, Path.Combine(BepInEx.Paths.BepInExRootPath, "bot-result.txt"), true); } catch (Exception e) { PtLog("BOT result copy failed: " + e.Message); }
            if (botWorld != null) { try { BotWriteSync(botWorld.Run, (botWorld.DeclaredRole == "single" ? "host" : botWorld.DeclaredRole) + ".done", botLastVerdict); } catch (Exception) { } botWorld.Finish(); }
            PtLog("BOT run finished: " + botLastVerdict + " -> " + botResultPath);
            try { Logger.LogInfo("BOT run finished: " + botLastVerdict + " (" + botResultPath + ")"); } catch (Exception) { }
        }

        static void BotWriteSync(string run, string name, string text)
        {
            string dir = Path.Combine(Path.Combine(BotRoot, "sync"), run); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text + "\r\n");
        }

        static void BotTrimFolder(string dir, int keep)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                string[] d = Directory.GetDirectories(dir); if (d.Length <= keep) return;
                Array.Sort(d, delegate(string x, string y) { return Directory.GetCreationTimeUtc(x).CompareTo(Directory.GetCreationTimeUtc(y)); });
                for (int i = 0; i < d.Length - keep; i++) { try { Directory.Delete(d[i], true); } catch (Exception) { } }
            }
            catch (Exception) { }
        }

        static void BotAppendResult(string line)
        {
            try { if (botResultPath != null) File.AppendAllText(botResultPath, line + "\r\n", new UTF8Encoding(true)); } catch (Exception) { }
        }

        void BotOnGui()
        {
            if (!BotRunActive || botShotHideGui) return;
            int n = botRun.Doc.Steps.Count, i = Math.Min(botRun.StepIndex + 1, n);
            GUI.Label(new Rect(8f, 4f, 900f, 22f), "БОТ: " + botRun.Name + " — крок " + i + "/" + n + ", перевірок " + botRun.Report.Pass + " ок / " + botRun.Report.Fail + " ні (Ctrl+Shift+F10 = стоп)");
        }

        // ---------- log tap: BepInEx listener (our plugin + Unity via BepInEx's UnityLogSource) and brix.log tail ----------
        sealed class BotLogTap : ILogListener
        {
            bool unityFallback; long brixPos = -1; string brixPath;
            public void LogEvent(object sender, LogEventArgs e)
            {
                if (!botTapOn || e == null || e.Data == null) return;
                string src = e.Source != null && e.Source.SourceName == "Unity Log" ? "unity" : "bepinex";
                if (src == "unity" && unityFallback) return;
                botRing.Add(src, e.Data.ToString());
            }
            public void Dispose() { }
            // BepInEx forwards Unity's log only when [Logging] UnityLogListening is on; otherwise listen ourselves
            public void ArmUnityFallback()
            {
                if (unityFallback) return;
                bool has = false;
                try { foreach (ILogSource s in BepInEx.Logging.Logger.Sources) if (s is UnityLogSource) { has = true; break; } } catch (Exception) { has = true; }
                if (has) return;
                unityFallback = true; Application.logMessageReceivedThreaded += OnUnity;
            }
            static void OnUnity(string message, string stack, LogType type) { if (botTapOn) botRing.Add("unity", message); }
            public void StartBrix()
            {
                try { brixPath = Path.Combine(Path.Combine(Path.Combine(Path.GetTempPath(), "Castle Story"), "logs"), "brix.log"); brixPos = File.Exists(brixPath) ? new FileInfo(brixPath).Length : 0; }
                catch (Exception) { brixPos = -1; }
            }
            // new lines of the game's own log4net file (ERRORs live only there)
            public void PollBrix()
            {
                if (brixPos < 0 || brixPath == null) return;
                try
                {
                    FileInfo fi = new FileInfo(brixPath); if (!fi.Exists) return;
                    if (fi.Length < brixPos) brixPos = 0;
                    if (fi.Length == brixPos) return;
                    long want = Math.Min(fi.Length - brixPos, 4 * 1024 * 1024);
                    using (FileStream fs = new FileStream(brixPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Seek(brixPos, SeekOrigin.Begin); byte[] b = new byte[want]; int got = 0;
                        while (got < want) { int r = fs.Read(b, got, (int)want - got); if (r <= 0) break; got += r; }
                        int last = -1; for (int i = got - 1; i >= 0; i--) if (b[i] == (byte)'\n') { last = i; break; }
                        if (last < 0) { if (got >= 4 * 1024 * 1024) brixPos += got; return; }
                        string text = Encoding.UTF8.GetString(b, 0, last + 1); brixPos += last + 1;
                        foreach (string line in text.Split('\n')) { string l = line.TrimEnd('\r'); if (l.Length > 0) botRing.Add("brix", l); }
                    }
                }
                catch (Exception) { }
            }
        }

        // ---------- the world the runner sees ----------
        sealed partial class BotGameWorld : IBotWorld
        {
            readonly SteamBridgePlugin plugin; public readonly string Run, DeclaredRole;
            internal Func<float, BotOutcome> pending;              // set by an action that answered Pending
            internal readonly Dictionary<string, BotColorStats> shots = new Dictionary<string, BotColorStats>(StringComparer.OrdinalIgnoreCase);
            internal float now;
            public BotGameWorld(SteamBridgePlugin p, string run, string role) { plugin = p; Run = run; DeclaredRole = role; }
            public void Frame(float t) { now = t; }
            public void Finish() { pending = null; try { plugin.BotRunCleanup(this); } catch (Exception) { } botShotHideGui = false; }
            public string Role() { return BotNetRole(); }
            public BotOutcome Start(BotStep s)
            {
                pending = null; botShotHideGui = false;
                if (s.Kind == "legacy") return plugin.BotRunLegacy(s.Legacy);
                BotOutcome o = plugin.BotDoAction(s, this);
                return o ?? BotOutcome.Fail("no outcome");
            }
            public BotOutcome Poll(BotStep s, float elapsed)
            {
                if (pending == null) return BotOutcome.Fail("nothing pending");
                BotOutcome o = pending(elapsed);
                if (o == null) return BotOutcome.Fail("no outcome");
                if (o.Status != BotStatus.Pending) pending = null;
                return o;
            }
            public BotEval Check(BotCondition c, BotStep s) { return plugin.BotCheckCondition(c, this) ?? BotEval.Unable("no answer"); }
            public bool Arrive(string run, string barrier, string role, out string error)
            {
                error = null;
                try
                {
                    string dir = Path.Combine(Path.Combine(BotRoot, "sync"), run); Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, BotRules.BarrierFile(barrier, role)), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + BotNetRole() + "\r\n");
                    return true;
                }
                catch (Exception e) { error = e.Message; return false; }
            }
            public bool AllArrived(string run, string barrier, string[] roles, out string missing)
            {
                List<string> names = new List<string>();
                foreach (string dir in new string[] { Path.Combine(Path.Combine(BotRoot, "sync"), run), botPeerSync == null ? null : Path.Combine(botPeerSync, run) })
                {
                    if (dir == null) continue;
                    foreach (string r in roles) { string f = BotRules.BarrierFile(barrier, r); try { if (File.Exists(Path.Combine(dir, f))) names.Add(f); } catch (Exception) { } }
                }
                missing = BotRules.BarrierMissing(names, barrier, roles);
                if (missing.Length > 0)
                {   // 0.4.11: name a missing role whose run already ended ("(finished <verdict>)"): the runner then fails the barrier at once
                    StringBuilder mb = new StringBuilder();
                    foreach (string r in missing.Split(','))
                    {
                        string done = null;
                        foreach (string dir in new string[] { Path.Combine(Path.Combine(BotRoot, "sync"), run), botPeerSync == null ? null : Path.Combine(botPeerSync, run) })
                        { if (dir == null) continue; try { string df = Path.Combine(dir, r + ".done"); if (File.Exists(df)) { done = File.ReadAllText(df).Trim(); break; } } catch (Exception) { } }
                        if (mb.Length > 0) mb.Append(',');
                        mb.Append(r); if (done != null) mb.Append(" (finished ").Append(done).Append(')');
                    }
                    missing = mb.ToString();
                }
                return missing.Length == 0;
            }
            public void Write(string line) { BotAppendResult(line); PtLog("BOT " + line); }
            public void Mark(string name) { PtLog("BOT MARK " + name + " run=" + Run + " role=" + DeclaredRole); try { plugin.Logger.LogInfo("BOT MARK " + name + " run=" + Run + " role=" + DeclaredRole); } catch (Exception) { } }
        }

        // a 0.3.78 virtual player command, run by its own code (VirtualPlayer.cs); its text output decides OK / ERR
        BotOutcome BotRunLegacy(VirtualAction src)
        {
            if (src == null) return BotOutcome.Fail("no command");
            VirtualAction a = new VirtualAction(); a.Name = src.Name; a.Key = src.Key; a.Cat = src.Cat; a.X = src.X; a.Y = src.Y; a.Z = src.Z; a.Seconds = src.Seconds; a.Error = src.Error;
            string text;
            vpCapture = new StringBuilder();
            try { vpDone++; if (!VpRunInstance(a)) VpRun(a); }
            catch (Exception e) { vpCapture.Append("RESULT=failed reason=" + e.GetType().Name + ": " + e.Message); }
            finally { text = vpCapture.ToString().Trim(); vpCapture = null; }
            string why; bool ok = BotRules.VpOutputOk(text, out why);
            if (text.Length == 0) text = "(no output)";
            return ok ? BotOutcome.Ok(text) : BotOutcome.Fail(text);
        }
    }
}
