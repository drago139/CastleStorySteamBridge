using System;
using System.Collections.Generic;
using System.Globalization;

namespace CastleStorySteam
{
    // 0.4.10 IN-GAME TEST BOT, pure part 3: the runner (a state machine fed with the time; no Unity). The game glue (Bot.cs) implements IBotWorld
    // and calls Tick(now) every frame; BotTests.cs drives it with a fake world. Rules:
    //  - steps run in order; a role-prefixed step runs only on that role (declared with 'set role' or detected: host = server active, guest = client only);
    //  - an action is started once (Start) and polled (Poll) while it says Pending, up to its timeout ('within', else DefaultActionTimeout);
    //  - expect / waituntil re-check their condition every PollSeconds until true or 'within' ends (expect without 'within' checks once);
    //    nolog fails at the first matching line and passes when its 'for' window ends without one;
    //  - barrier writes the own arrival file, then waits until every role in 'roles' arrived (default timeout 300 s);
    //  - 'set onfail stop' ends the run at the first FAIL / ERR; 'set timeout' (default 30 min) ends a run that hangs; Abort() = kill switch.
    public enum BotStatus { Done, Failed, Pending }

    public sealed class BotOutcome
    {
        public BotStatus Status; public string Detail;
        public static BotOutcome Ok(string d) { BotOutcome o = new BotOutcome(); o.Status = BotStatus.Done; o.Detail = d; return o; }
        public static BotOutcome Fail(string d) { BotOutcome o = new BotOutcome(); o.Status = BotStatus.Failed; o.Detail = d; return o; }
        public static BotOutcome Wait(string d) { BotOutcome o = new BotOutcome(); o.Status = BotStatus.Pending; o.Detail = d; return o; }
    }

    public sealed class BotEval
    {
        public bool Ok, Cannot; public string Detail;   // Cannot = this condition cannot be evaluated here (no retry helps): the check fails at once
        public static BotEval Yes(string d) { BotEval e = new BotEval(); e.Ok = true; e.Detail = d; return e; }
        public static BotEval No(string d) { BotEval e = new BotEval(); e.Detail = d; return e; }
        public static BotEval Unable(string d) { BotEval e = new BotEval(); e.Cannot = true; e.Detail = d; return e; }
    }

    public interface IBotWorld
    {
        string Role();                                                              // host | guest | single | menu
        BotOutcome Start(BotStep step);                                             // begin an action (native or legacy)
        BotOutcome Poll(BotStep step, float elapsed);                               // continue an action that said Pending
        BotEval Check(BotCondition cond, BotStep step);                             // game-state conditions (everything except log / nolog)
        bool Arrive(string run, string barrier, string role, out string error);    // write the own barrier file
        bool AllArrived(string run, string barrier, string[] roles, out string missing);
        void Write(string line);                                                    // one result line (glue: result file + perf-trace)
        void Mark(string name);                                                     // a MARK line into the game logs (aligns host and guest logs)
    }

    public sealed class BotRunner
    {
        public const float PollSeconds = 0.2f;
        public readonly BotScriptDoc Doc; public readonly BotReport Report = new BotReport();
        public bool Finished; public string RunId, Name, DeclaredRole, OnFail; public float Timeout, Pace; public string[] BarrierRoles;
        readonly IBotWorld world; readonly BotLogRing ring;
        float startedAt, stepStart, nextAt, lastPoll, finishedAt; int idx; bool stepStarted, actionPending;
        long startSeq, stepSeq, actionSeq; readonly Dictionary<string, long> marks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string lastDetail;

        public BotRunner(BotScriptDoc doc, IBotWorld world, BotLogRing ring, float now, string headerInfo)
        {
            Doc = doc; this.world = world; this.ring = ring; startedAt = now; nextAt = now;
            RunId = doc.Get("run", "solo"); Name = doc.Get("name", "unnamed"); DeclaredRole = doc.Get("role", "auto"); OnFail = doc.Get("onfail", "continue");
            Timeout = doc.GetSeconds("timeout", 1800f); Pace = doc.GetSeconds("pace", 0.25f); BarrierRoles = BotRules.ParseRoles(doc.Get("roles", "host,guest")) ?? new string[] { "host", "guest" };
            ring.ClearWatches(); foreach (BotStep st in doc.Steps) if (st.Cond != null && st.Cond.Pattern != null) ring.WatchPattern(st.Cond.Pattern, st.Cond.Since == "boot");   // 0.4.27 review: a 'since boot' pattern is also matched against the lines already in the ring
            startSeq = ring.LastSeq; actionSeq = startSeq;
            Write(BotRules.ResultHeader + " run=" + RunId + " name=" + Name + " role=" + DeclaredRole + "/" + SafeRole() + " steps=" + doc.Steps.Count + " checks=" + doc.CountChecks() + (string.IsNullOrEmpty(headerInfo) ? "" : " " + headerInfo));
            if (doc.Errors.Count > 0)
            {
                Report.Invalid = true;
                foreach (string e in doc.Errors) Write("ERR   " + e);
                Finish(now, "the script has " + doc.Errors.Count + " error(s) and did not run");
            }
        }

        public float Elapsed(float now) { return (Finished ? finishedAt : now) - startedAt; }
        public int StepIndex { get { return idx; } }

        string SafeRole() { try { return world.Role() ?? "menu"; } catch (Exception) { return "menu"; } }
        string Role() { return DeclaredRole == "host" || DeclaredRole == "guest" ? DeclaredRole : SafeRole(); }
        void Write(string line) { Report.Lines.Add(line); try { world.Write(line); } catch (Exception) { } }

        public void Abort(string reason, float now)
        {
            if (Finished) return;
            Report.Aborted = true; Write(BotRules.Line("ABORT", idx < Doc.Steps.Count ? Doc.Steps[idx].Line : 0, now - startedAt, idx < Doc.Steps.Count ? Doc.Steps[idx].Text : "", reason));
            Finish(now, null);
        }

        void Finish(float now, string why)
        {
            if (Finished) return;
            if (why != null) Write("INFO  " + why);
            Finished = true; finishedAt = now; Write(Report.Summary(now - startedAt));
        }

        public void Tick(float now)
        {
            if (Finished) return;
            if (now - startedAt > Timeout)
            {
                BotStep cur = idx < Doc.Steps.Count ? Doc.Steps[idx] : null;
                Report.Fail++; if (cur != null && cur.IsCheck && stepStarted) Report.Checks++;
                Write(BotRules.Line("FAIL", cur == null ? 0 : cur.Line, now - startedAt, cur == null ? "" : cur.Text, "script timeout after " + Timeout.ToString("F0", CultureInfo.InvariantCulture) + " s"));
                Finish(now, "stopped: script timeout"); return;
            }
            if (now < nextAt) return;
            for (int guard = 0; guard < 64 && !Finished; guard++)   // role-skipped and instant steps run in the same frame, bounded
            {
                if (idx >= Doc.Steps.Count) { Finish(now, null); return; }
                BotStep s = Doc.Steps[idx];
                if (!stepStarted)
                {
                    string role = Role();
                    if (s.Only != BotRole.Any && role == "menu" && DeclaredRole == "auto")
                    {
                        Report.Skip++; Write(BotRules.Line("SKIP", s.Line, now - startedAt, s.Text, "role unknown in the menu: the script needs 'set role host|guest'")); Next(now, false, true); continue;
                    }
                    if (!BotRules.RoleMatches(s.Only, role)) { idx++; continue; }   // the other instance's step: silently skipped
                    stepStarted = true; stepStart = now; lastPoll = now; stepSeq = ring.LastSeq; actionPending = false; lastDetail = null;
                    if (s.IsAction) actionSeq = ring.LastSeq;
                }
                bool done = RunStep(s, now);
                if (!done) return;   // waiting: come back on a later frame
                if (Finished) return;
                if (nextAt > now) return;   // pace between steps
            }
        }

        // returns true when the step is finished (then Next() was called)
        bool RunStep(BotStep s, float now)
        {
            float el = now - stepStart;
            switch (s.Kind)
            {
                case "note": Write(BotRules.Line("INFO", s.Line, now - startedAt, s.Text, null)); Next(now, false, false); return true;
                case "mark":
                    marks[s.Args[0]] = ring.LastSeq;
                    try { world.Mark(s.Args[0]); } catch (Exception) { }
                    Write(BotRules.Line("INFO", s.Line, now - startedAt, s.Text, "log seq " + ring.LastSeq)); Next(now, false, false); return true;
                case "stop": Write(BotRules.Line("INFO", s.Line, now - startedAt, s.Text, "stop")); Finish(now, null); return true;
                case "wait": if (el < s.Seconds) return false; Next(now, false, false); return true;
                case "action": case "legacy": return RunAction(s, now, el);
                case "expect": case "waituntil": case "observe": return RunCheck(s, now, el);
                case "barrier": return RunBarrier(s, now, el);
            }
            Report.Err++; Write(BotRules.Line("ERR", s.Line, now - startedAt, s.Text, "unknown step kind " + s.Kind)); Next(now, true, false); return true;
        }

        public static float DefaultActionTimeout(string name)
        {
            switch (name)
            {
                case "save": return 120f;
                case "load": case "hostlobby": case "sandbox": return 300f;
                case "leavematch": return 120f;
                case "shot": return 15f;
                case "join": case "joinlan": return 60f;
                case "pickup": case "deliver": case "hoststart": return 180f;   // workers walk; a lobby waits for its guests
                case "ready": case "unready": return 120f;                       // the game allows it only once in the lobby and the map is there
                case "place": case "placerel": case "demolish": return 90f;    // a new build task is a round trip first
                case "demolishdoor": return 90f;                                 // 0.4.18: build task + anti round trip, then the answer of the host
                case "team": case "faction": return 60f;
                case "grid": return 300f;                                        // 0.4.18: up to 400 cells at 2 per frame + a build task
                case "ungrid": return 120f;                         // 0.4.18: the lobby must be there and the host must answer the Cmd
            }
            return 20f;
        }

        bool RunAction(BotStep s, float now, float el)
        {
            BotOutcome o;
            if (!actionPending)
            {
                Report.Actions++;
                try { o = world.Start(s); } catch (Exception e) { o = BotOutcome.Fail(e.GetType().Name + ": " + e.Message); }
                if (o == null) o = BotOutcome.Fail("no outcome");
                if (o.Status == BotStatus.Pending) { actionPending = true; lastDetail = o.Detail; lastPoll = now; return false; }
            }
            else
            {
                float limit = s.Within > 0f ? s.Within : DefaultActionTimeout(s.Name);
                if (el >= limit) o = BotOutcome.Fail("timeout after " + limit.ToString("F0", CultureInfo.InvariantCulture) + " s" + (string.IsNullOrEmpty(lastDetail) ? "" : " (last: " + lastDetail + ")"));
                else
                {
                    if (now - lastPoll < PollSeconds) return false;
                    lastPoll = now;
                    try { o = world.Poll(s, el); } catch (Exception e) { o = BotOutcome.Fail(e.GetType().Name + ": " + e.Message); }
                    if (o == null) o = BotOutcome.Fail("no outcome");
                    if (o.Status == BotStatus.Pending) { lastDetail = o.Detail; return false; }
                }
            }
            bool ok = o.Status == BotStatus.Done;
            if (ok) Report.Ok++; else Report.Err++;
            Write(BotRules.Line(ok ? "OK" : "ERR", s.Line, now - startedAt, s.Text, o.Detail));
            Next(now, !ok, false); return true;
        }

        BotEval Evaluate(BotCondition c, BotStep s)
        {
            if (c.Kind == "log" || c.Kind == "nolog")
            {
                long after;
                if (c.Since == "boot") after = 0;
                else if (c.Since == "start") after = startSeq;
                else if (c.Since == "step") after = stepSeq;
                else if (c.Since == "action") after = actionSeq;
                else { string m = c.Since.Substring(5); if (!marks.TryGetValue(m, out after)) return BotEval.Unable("no mark '" + m + "' was set before this step"); }
                bool over; BotLogEntry e = ring.Find(c.Pattern, c.From, after, out over);
                // 0.4.27 verify B-10-v: since 'since boot' reaches behind the run, a match that is older than this run says so (the result line has no time of the matched line).
                // In a game that is used for a second run such a line can belong to the earlier run, e.g. the CATAPULT-CENSUS or 'lua exposed' line of the earlier level.
                if (e != null) return BotEval.Yes("[" + e.Source + (c.Since == "boot" && e.Seq <= startSeq ? ", before this run" : "") + "] " + e.Text);
                return BotEval.No(over ? "no match (the log ring overflowed: older lines were dropped)" : "no match yet");
            }
            BotEval r;
            try { r = world.Check(c, s); } catch (Exception ex) { return BotEval.Unable(ex.GetType().Name + ": " + ex.Message); }
            if (r == null) return BotEval.Unable("no answer");
            if (r.Cannot) return r;
            if (c.Negate) { BotEval n = new BotEval(); n.Ok = !r.Ok; n.Detail = "not: " + r.Detail; return n; }
            return r;
        }

        bool RunCheck(BotStep s, float now, float el)
        {
            BotCondition c = s.Cond;
            bool first = el <= 0f || lastDetail == null;
            if (!first && now - lastPoll < PollSeconds) return false;
            lastPoll = now;
            BotEval r = Evaluate(c, s); lastDetail = r.Detail ?? "";
            if (c.Kind == "nolog")
            {
                if (r.Ok) return Verdict(s, now, false, "matched: " + r.Detail);
                if (r.Cannot) return Verdict(s, now, false, r.Detail);
                if (r.Detail != null && r.Detail.IndexOf("overflowed", StringComparison.Ordinal) >= 0) return Verdict(s, now, false, "cannot prove absence: " + r.Detail);
                if (el >= c.Window) return Verdict(s, now, true, "no match" + (c.Window > 0f ? " in " + c.Window.ToString("F0", CultureInfo.InvariantCulture) + " s" : ""));
                return false;
            }
            if (r.Ok) return Verdict(s, now, true, (el > 0.05f ? "after " + el.ToString("F1", CultureInfo.InvariantCulture) + " s: " : "") + r.Detail);
            if (r.Cannot) return Verdict(s, now, false, r.Detail);
            if (s.Within <= 0f || el >= s.Within) return Verdict(s, now, false, (s.Within > 0f ? "timeout " + s.Within.ToString("F0", CultureInfo.InvariantCulture) + " s; last: " : "") + r.Detail);
            return false;
        }

        bool RunBarrier(BotStep s, float now, float el)
        {
            string[] roles = BarrierRoles; string name = s.Args[0];
            for (int i = 1; i < s.Args.Count - 1; i++) if (s.Args[i].ToLowerInvariant() == "roles") roles = BotRules.ParseRoles(s.Args[i + 1]) ?? roles;
            string role = Role();
            if (role == "menu") return Verdict(s, now, false, "role unknown in the menu: a barrier needs 'set role host|guest'");
            if (lastDetail == null)
            {
                string err; bool wrote;
                try { wrote = world.Arrive(RunId, name, role == "single" ? "host" : role, out err); } catch (Exception e) { wrote = false; err = e.Message; }
                if (!wrote) return Verdict(s, now, false, "cannot write the arrival file: " + err);
                lastDetail = "arrived"; lastPoll = now - PollSeconds;
            }
            if (now - lastPoll < PollSeconds) return false;
            lastPoll = now;
            string missing; bool all;
            try { all = world.AllArrived(RunId, name, roles, out missing); } catch (Exception e) { all = false; missing = "error " + e.Message; }
            if (all) return Verdict(s, now, true, "all of " + string.Join(",", roles) + " arrived after " + el.ToString("F1", CultureInfo.InvariantCulture) + " s");
            lastDetail = "waiting for " + missing;
            if (missing.IndexOf("(finished", StringComparison.Ordinal) >= 0) return Verdict(s, now, false, "the other instance already ended its run: " + missing);
            if (el >= s.Within) return Verdict(s, now, false, "timeout " + s.Within.ToString("F0", CultureInfo.InvariantCulture) + " s; missing: " + missing);
            return false;
        }

        bool Verdict(BotStep s, float now, bool pass, string detail)
        {
            if (s.Kind == "observe")   // 0.4.18: a FACT line (YES / NO); it never counts as a check, never fails the run, never stops it
            {
                Report.Facts++;
                Write(BotRules.Line("FACT", s.Line, now - startedAt, s.Text, (pass ? "YES " : "NO ") + detail));
                Next(now, false, false); return true;
            }
            Report.Checks++; if (pass) Report.Pass++; else Report.Fail++;
            Write(BotRules.Line(pass ? "PASS" : "FAIL", s.Line, now - startedAt, s.Text, detail));
            Next(now, !pass, false); return true;
        }

        void Next(float now, bool failed, bool skipped)
        {
            idx++; stepStarted = false; actionPending = false; lastDetail = null; nextAt = skipped ? now : now + Pace;
            if (failed && OnFail == "stop") Finish(now, "stopped at the first failure (set onfail stop)");
        }
    }
}
