using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using CastleStorySteam;

// 0.4.10 unit suite for the in-game test bot: BotScript (parser), BotRules (rules, log ring, colours) and BotRunner (state machine, fake world).
public static class BotTests
{
    static int failed, checks;
    static void Check(bool ok, string name) { checks++; if (!ok) { failed++; Console.WriteLine("FAIL " + name); } }

    sealed class FakeWorld : IBotWorld
    {
        public string role = "host"; public readonly List<string> lines = new List<string>(), started = new List<string>(), marks = new List<string>(), arrivals = new List<string>();
        public Func<BotStep, BotOutcome> onStart; public Func<BotStep, float, BotOutcome> onPoll; public Func<BotCondition, BotEval> onCheck;
        public bool peerArrived; public bool arriveFails; public bool finishedPeer;
        public string Role() { return role; }
        public BotOutcome Start(BotStep s) { started.Add(s.Name); return onStart != null ? onStart(s) : BotOutcome.Ok("done"); }
        public BotOutcome Poll(BotStep s, float el) { return onPoll != null ? onPoll(s, el) : BotOutcome.Ok("done"); }
        public BotEval Check(BotCondition c, BotStep s) { return onCheck != null ? onCheck(c) : BotEval.Unable("no checker"); }
        public bool Arrive(string run, string b, string r, out string error) { error = arriveFails ? "disk" : null; if (arriveFails) return false; arrivals.Add(run + "/" + b + "." + r); return true; }
        public bool AllArrived(string run, string b, string[] roles, out string missing) { missing = peerArrived ? "" : (finishedPeer ? "guest (finished ABORTED)" : "guest"); return peerArrived; }
        public void Write(string line) { lines.Add(line); }
        public void Mark(string name) { marks.Add(name); }
        public string Last { get { return lines.Count == 0 ? "" : lines[lines.Count - 1]; } }
        public bool Has(string prefix, string part) { foreach (string l in lines) if (l.StartsWith(prefix) && l.Contains(part)) return true; return false; }
    }

    static BotRunner Run(string[] script, FakeWorld w, BotLogRing ring, float until, Action<BotRunner, float> each)
    {
        BotRunner r = new BotRunner(BotScript.Parse(script), w, ring, 0f, "test");
        for (int i = 0; i <= (int)(until / 0.05f) && !r.Finished; i++) { float t = i * 0.05f; if (each != null) each(r, t); r.Tick(t); }
        return r;
    }

    public static int Main()
    {
        Parser(); Rules(); Ring(); Colours(); Runner();
        Console.WriteLine(failed == 0 ? "PASS: in-game bot script, rules and runner (" + checks + " checks)" : "FAILED " + failed + " of " + checks);
        return failed == 0 ? 0 : 1;
    }

    static BotStep P(string line) { return BotScript.ParseLine(line, 1); }

    static void Parser()
    {
        string err; List<string> t;
        t = BotScript.Tokenize("expect log \"HELD carried model: orange\" within 30", out err); Check(err == null && t.Count == 5 && t[2] == "HELD carried model: orange", "tokenizer keeps a quoted phrase");
        t = BotScript.Tokenize("note \"say \\\"hi\\\"\" # comment", out err); Check(err == null && t.Count == 2 && t[1] == "say \"hi\"", "escaped quote + comment");
        t = BotScript.Tokenize("expect log \"a\\d+b\"", out err); Check(err == null && t[2] == "a\\d+b", "backslashes kept for regexes");
        t = BotScript.Tokenize("mark a#b", out err); Check(err == null && t.Count == 2 && t[1] == "a#b", "# inside a word is not a comment");
        t = BotScript.Tokenize("note \"open", out err); Check(err != null, "unclosed quote is an error");
        t = BotScript.Tokenize("note \"\"", out err); Check(err == null && t.Count == 2 && t[1] == "", "empty quoted word");
        Check(P("   ") == null && P("# only a comment") == null && P(null) == null, "blank and comment lines skipped");

        BotStep s;
        s = P("host: fire 0"); Check(s.Error == null && s.Only == BotRole.Host && s.Kind == "legacy" && s.Legacy != null && s.Legacy.Name == "fire", "host prefix + legacy fire");
        s = P("guest:aim 0 1 2 3"); Check(s.Error == null && s.Only == BotRole.Guest && s.Legacy != null && s.Legacy.Cat == 0 && s.Legacy.Z == 3f, "glued guest prefix");
        s = P("ALL: wait 2"); Check(s.Error == null && s.Only == BotRole.Any && s.Kind == "wait" && s.Seconds == 2f, "all prefix, case");
        s = P("host:"); Check(s.Error != null, "prefix alone rejected");
        s = P("aim 0 1 2"); Check(s.Error != null && s.Kind == "legacy", "legacy error passes through");
        s = P("aim 0 515.5,133,646"); Check(s.Error == null && s.Legacy.X == 515.5f && s.Legacy.Z == 646f, "legacy commas still work");
        s = P("fillstock"); Check(s.Error == null && s.Kind == "legacy", "fillstock (all types) is legacy");
        s = P("fillstock PlankBlock"); Check(s.Error == null && s.Kind == "legacy" && s.Legacy.Key == "PlankBlock", "fillstock one type is legacy");
        s = P("fillstock OrangeCrystal 5"); Check(s.Error == null && s.Kind == "action" && s.Args[1] == "5", "fillstock with crates is native");
        s = P("fillstock OrangeCrystal 0"); Check(s.Error != null, "fillstock 0 crates rejected"); s = P("fillstock within 5"); Check(s.Error != null && s.Kind == "action", "fillstock with only 'within' is a parse error, not an exception (0.4.27 review B-09)");
        s = P("format c:"); Check(s.Error != null, "unknown command rejected");

        s = P("set run ow-20261106"); Check(s.Error == null && s.Kind == "set", "set run");
        Check(P("set bogus 1").Error != null, "unknown setting rejected");
        Check(P("set role boss").Error != null, "bad role rejected");
        Check(P("set timeout 5").Error != null && P("set timeout 10m").Error == null, "timeout bounds");
        Check(P("set onfail stop").Error == null && P("set onfail maybe").Error != null, "onfail values");
        Check(P("set roles host,guest").Error == null && P("set roles host,host").Error != null && P("set roles boss").Error != null, "roles list");
        Check(P("host: set run x").Error != null, "set with a role prefix rejected");
        Check(P("set run ../x").Error != null && P("set run a b").Error != null, "bad run id rejected");

        s = P("wait 1.5m"); Check(s.Error == null && Math.Abs(s.Seconds - 90f) < 0.01f, "wait 1.5m");
        s = P("wait 500ms"); Check(s.Error == null && Math.Abs(s.Seconds - 0.5f) < 0.001f, "wait 500ms");
        Check(P("wait").Error != null && P("wait -1").Error != null && P("wait x").Error != null && P("wait 3h").Error != null, "bad waits rejected");

        s = P("expect log \"ORANGE-WARD RECIPE HELD carried model: orange\" within 120"); Check(s.Error == null && s.Kind == "expect" && s.Name == "log" && s.Within == 120f && s.Cond.Pattern != null && s.Cond.Since == "action" && s.Cond.From == "any", "expect log defaults");
        s = P("expect log \"x\" from perf since mark:m1 within 5"); Check(s.Error == null && s.Cond.From == "perf" && s.Cond.Since == "mark:m1" && s.Within == 5f, "log from + since mark");
        s = P("expect log within within 5"); Check(s.Error == null && s.Cond.Pattern.IsMatch("within") && s.Within == 5f, "regex token is never a keyword");
        Check(P("expect log \"(\"").Error != null, "bad regex rejected");
        Check(P("expect log \"x\" from mars").Error != null && P("expect log \"x\" since yesterday").Error != null, "bad from / since rejected");
        Check(P("expect not log \"x\"").Error != null, "'not log' rejected");
        s = P("expect nolog \"Exception\" for 30"); Check(s.Error == null && s.Name == "nolog" && s.Cond.Window == 30f && s.Cond.Since == "start", "nolog for");
        Check(P("expect role host for 3").Error != null, "'for' only for nolog");
        s = P("expect state match"); Check(s.Error == null && s.Cond.Args[0] == "match", "state");
        Check(P("expect state party").Error != null, "bad state rejected");
        s = P("expect guests >= 1 within 2m"); Check(s.Error == null && s.Cond.Op == ">=" && s.Cond.Number == 1 && s.Within == 120f, "guests >= 1");
        s = P("expect workers>=3"); Check(s.Error == null && s.Cond.Op == ">=" && s.Cond.Number == 3, "glued operator");
        s = P("expect workers = 3"); Check(s.Error == null && s.Cond.Op == "==", "= means ==");
        Check(P("expect workers 3").Error != null && P("expect workers >= x").Error != null && P("expect workers >=").Error != null, "bad comparisons rejected");
        s = P("expect items OrangeCrystal >= 40"); Check(s.Error == null && s.Cond.Args.Count == 1 && s.Cond.Args[0] == "OrangeCrystal" && s.Cond.Number == 40, "items");
        s = P("expect carry any OrangeWard within 3m"); Check(s.Error == null && s.Cond.Args[0] == "any" && s.Cond.Args[1] == "OrangeWard" && s.Within == 180f, "carry any");
        s = P("expect held \"name=Bravo\" HeldOrangeWard"); Check(s.Error == null && s.Cond.Args[0] == "name=Bravo", "held by name");
        Check(P("expect carry #0 OrangeWard").Error != null, "# selector is a comment, rejected");
        s = P("expect not carry any OrangeWard"); Check(s.Error == null && s.Cond.Negate, "not carry");
        Check(P("expect blueprint Blueprints/OrangeWard exists").Error == null && P("expect blueprint OrangeWard built").Error != null, "blueprint exists / missing only");
        Check(P("expect blueprints OrangeWard == 1").Error == null && P("expect structure OrangeWard >= 1").Error == null && P("expect queue altar >= 1").Error == null, "counts");
        Check(P("expect hologram OrangeWard blue").Error == null && P("expect hologram OrangeWard pink").Error != null, "hologram colours");
        s = P("expect color carry orange >= 2.5"); Check(s.Error == null && s.Cond.Number == 2.5, "color share");
        Check(P("expect color carry orange >= 120").Error != null, "percent bound");
        Check(P("expect weather sunny").Error != null && P("expect").Error != null, "unknown / empty condition rejected");
        s = P("waituntil state match"); Check(s.Error == null && s.Kind == "waituntil" && s.Within == 60f, "waituntil default 60 s");
        s = P("expect role host"); Check(s.Within < 0f, "expect default = check once");

        s = P("barrier start within 2m roles host,guest"); Check(s.Error == null && s.Kind == "barrier" && s.Within == 120f, "barrier options");
        Check(P("barrier start").Within == 300f, "barrier default timeout");
        Check(P("barrier").Error != null && P("barrier a/b").Error != null && P("barrier x roles boss").Error != null && P("barrier x loud").Error != null, "bad barriers rejected");
        Check(P("mark m1").Error == null && P("mark").Error != null && P("mark a b").Error != null, "mark");
        Check(P("note hello world").Error == null && P("note").Error != null, "note");
        Check(P("stop").Error == null && P("stop now").Error != null, "stop");

        Check(P("craft altar 200").Error == null && P("craft altar OrangeWard 2").Error == null && P("craft altar 200 0").Error != null && P("craft altar").Error != null && P("craft 9x 200").Error != null, "craft");
        s = P("place Blueprints/OrangeWard 500 130 640 rot 90"); Check(s.Error == null && s.Kind == "action", "place with rotation");
        Check(P("place OrangeWard 1 2").Error != null && P("place OrangeWard 1 2 3 rot").Error != null && P("place Or@nge 1 2 3").Error != null, "bad place rejected");
        Check(P("placerel Blueprints/OrangeWard 6 0").Error == null && P("placerel OrangeWard 6 0 rot 45").Error == null && P("placerel OrangeWard 900 0").Error != null, "placerel");
        Check(P("demolish OrangeWard").Error == null && P("demolish OrangeWard 2").Error == null && P("demolish OrangeWard x").Error != null, "demolish");
        Check(P("assign 2 build").Error == null && P("assign all craft altar").Error == null && P("assign 0 build").Error != null && P("assign 2 dance").Error != null, "assign");
        Check(P("save bot-ow").Error == null && P("load bot-ow").Error == null && P("save ../x").Error != null && P("save").Error != null, "save / load names");
        Check(P("leavematch").Error == null && P("leavematch now").Error != null, "leavematch");
        s = P("hostlobby \"Test Catapults\" sandbox"); Check(s.Error == null && s.Args[0] == "Test Catapults", "hostlobby with a quoted map");
        Check(P("sandbox ..\\x").Error != null, "map path rejected");
        Check(P("camera worker any").Error == null && P("camera carrier OrangeWard").Error == null && P("camera at 1 2 3").Error == null && P("camera restore").Error == null && P("camera altar").Error == null, "camera targets");
        Check(P("camera worker").Error != null && P("camera at 1 2").Error != null && P("camera moon").Error != null && P("camera anchor extra").Error != null, "bad camera rejected");
        s = P("shot carry focus carrier OrangeWard box 120"); Check(s.Error == null && s.Kind == "action", "shot with focus and box");
        Check(P("shot x focus structure OrangeWard 0").Error == null && P("shot").Error != null && P("shot x box 2").Error != null && P("shot x loud").Error != null, "shot options");
        Check(P("mute on").Error == null && P("mute up").Error != null && P("dump all").Error == null && P("dump secrets").Error != null, "mute / dump");
        s = P("save bot-x within 3m"); Check(s.Error == null && s.Within == 180f && s.Args.Count == 1, "action within");
        Check(P("stock OrangeCrystal 40").Error == null && P("stock Iron 1 other").Error == null && P("stock Iron 0").Error != null && P("stock Iron 5 mine").Error != null, "stock");
        Check(P("assign 1 build exclusive").Error == null && P("assign 1 build loud").Error != null && P("assign 1 craft").Error != null && P("assign all idle").Error == null && P("assign 1 idle altar").Error != null, "assign targets");
        Check(P("hoststart").Error == null && P("leavelobby").Error == null && P("closeintro").Error == null && P("hoststart now").Error != null, "lobby actions");
        Check(P("debug instant").Error == null && P("debug off").Error != null, "debug");
        Check(P("dump orange").Error == null && P("dump projects").Error == null && P("dump camera").Error == null, "dump sections");
        s = P("expect hologram OrangeWard orange anti"); Check(s.Error == null && s.Cond.Anti && s.Cond.Args.Count == 2, "hologram anti");
        s = P("expect blueprint OrangeWard exists anti"); Check(s.Error == null && s.Cond.Anti, "blueprint anti");
        s = P("expect blueprints OrangeWard anti >= 1"); Check(s.Error == null && s.Cond.Anti && s.Cond.Number == 1 && s.Cond.Args.Count == 1, "blueprints anti count");
        s = P("expect blueprints OrangeWard == 0"); Check(s.Error == null && !s.Cond.Anti, "blueprints count");
        Check(P("placerel Altar 8 4 instant").Error == null && P("placerel Altar 8 4 rot 90 instant").Error == null && P("place Altar 1 2 3 instant").Error == null && P("place Altar 1 2 instant").Error != null && P("placerel Altar 8 instant").Error != null, "place instant");
        Check(P("pickup any OrangeWard").Error == null && P("pickup name=Bravo OrangeWard").Error == null && P("pickup any").Error != null && P("pickup #1 X").Error != null, "pickup");
        Check(P("deliver any OrangeWard").Error == null && P("deliver any Blueprints/OrangeWard").Error == null && P("deliver any").Error != null, "deliver");
        Check(P("stock OrangeCrystal 40 altar").Error == null && P("stock OrangeCrystal 40 crate").Error != null, "stock altar");
        Check(P("save ow-{run}").Error == null && P("load ow-{run}").Error == null && P("save ow-{other}").Error != null, "save names with {run}");
        s = P("expect log \"HELD pool ready\" since boot"); Check(s.Error == null && s.Cond.Since == "boot", "since boot");
        Check(P("expect held anyone HeldOrangeWard").Error == null && P("camera worker anyone").Error == null, "anyone selector");
        Check(BotRules.SelectorMatches("anyone", 7, "x"), "anyone matches");
        Check(P("join").Error == null && P("join").Kind == "action" && P("join code").Error == null && P("join 76561198000000001").Error == null && P("join name=gnom").Error == null, "join targets (0.4.11)");
        Check(P("join 12345").Error != null && P("join a b").Error != null && P("join name=").Error != null, "bad join targets rejected");
        Check(P("ready").Kind == "action" && P("ready").Error == null && P("unready").Error == null && P("ready now").Error != null, "native ready / unready (0.4.11)");
        Check(P("harvest pyrite nearest").Error == null && P("harvest tree nearest 8 3").Error == null && P("harvest iron 518 609 10 2").Error == null, "harvest forms (0.4.12)");
        Check(P("harvest gold nearest").Error != null && P("harvest tree").Error != null && P("harvest tree nearest 40").Error != null && P("harvest tree 1").Error != null && P("harvest tree nearest 5 2 9").Error != null, "bad harvest rejected");
        Check(P("unstump").Error == null && P("unstump 20 instant").Error == null && P("unstump 99").Error != null && P("unstump x").Error != null, "unstump (0.4.12)");
        Check(P("autonomy all freeagent").Error == null && P("autonomy any hold").Error == null && P("autonomy all dance").Error != null, "autonomy (0.4.12)");
        Check(P("expect crates >= 3").Error == null && P("expect stumps == 0 within 2m").Error == null && P("expect crates").Error != null, "crates / stumps conditions (0.4.12)");
        // 0.4.18 enemy door test
        Check(P("guest: team 1").Error == null && P("faction 3").Error == null && P("team 0 probe").Error == null && P("faction 0 probe").Kind == "action", "team / faction forms (0.4.18)");
        Check(P("team 4").Error != null && P("faction 8").Error != null && P("team").Error != null && P("team 1 now").Error != null && P("team x").Error != null && P("team -1").Error != null, "team / faction bad forms (0.4.18)");
        Check(P("team 1 within 30").Error == null && P("team 1 within 30").Within == 30f, "team takes within (0.4.18)");
        Check(P("demolishdoor other").Error == null && P("demolishdoor own 2").Error == null && P("demolishdoor any").Error == null && P("demolishdoor enemy").Error != null && P("demolishdoor own 30").Error != null && P("demolishdoor").Error != null, "demolishdoor (0.4.18)");
        Check(P("attackdoor all other siege").Error == null && P("attackdoor 0 own 1 siege").Error == null && P("attackdoor all other siege siege").Error != null && P("attackdoor all other").Error == null && P("attackdoor 0 own 1").Error == null && P("attackdoor name=Bob other").Error == null && P("attackdoor all").Error != null && P("attackdoor all mine").Error != null, "attackdoor (0.4.18)");
        Check(P("firedoor 0 other").Error == null && P("firedoor 1 own 3").Error == null && P("firedoor x other").Error != null && P("firedoor 0").Error != null && P("firedoor 0 other 99").Error != null, "firedoor (0.4.18)");
        Check(P("expect doors own >= 1").Error == null && P("expect doors other == 0 within 5m").Error == null && P("waituntil doors any >0").Error == null && P("expect doors >= 1").Error != null && P("expect doors own").Error != null, "doors condition (0.4.18)");
        Check(P("dump lobby").Error == null && P("dump doors").Error == null, "dump lobby / doors (0.4.18)");
        // 0.4.18 grid spawn helper: parser + pure maths
        Check(P("grid Pallet 3 3").Error == null && P("grid Pallet 5 5 spacing 4 base instant").Error == null && P("grid Pallet 20 20 map").Error == null && P("grid Pallet 2 2 rot 90 spacing 6").Error == null, "grid forms (0.4.18)");
        Check(P("grid Pallet 21 20").Error != null && P("grid Pallet 0 3").Error != null && P("grid Pallet 41 1").Error != null && P("grid Pallet 3").Error != null && P("grid Pallet 3 3 spacing 0").Error != null && P("grid Pallet 3 3 spacing 65").Error != null, "grid caps: 400 cells, sides 1..40, spacing 1..64 (0.4.18)");
        Check(P("grid Pallet 3 3 base map").Error != null && P("grid Pallet 3 3 instant instant").Error != null && P("grid Pallet 3 3 wobble").Error != null && P("grid Pallet 3 3 rot 400").Error != null && P("ungrid").Error == null && P("ungrid now").Error != null, "grid bad options / ungrid (0.4.18)");
        { string ge; BotGridRules.Options go = BotGridRules.Parse(new List<string> { "Pallet", "4", "2", "map", "spacing", "6", "rot", "-90", "instant" }, out ge);
          Check(ge == null && go.Cols == 4 && go.Rows == 2 && go.Map && go.Spacing == 6f && go.Rot == -90f && go.Instant, "grid options in any order (0.4.18)"); }
        { List<float[]> gc = BotGridRules.Cells(3, 2, 4f, 100f, 200f);
          Check(gc.Count == 6 && gc[0][0] == 96f && gc[0][1] == 198f && gc[2][0] == 104f && gc[5][1] == 202f && gc[4][0] == 100f, "grid cells centred on the anchor (0.4.18)");
          int dropped = BotGridRules.Clip(gc, 0f, 101f, 0f, 1000f, 1f);
          Check(dropped == 2 && gc.Count == 4 && gc[1][0] == 100f, "grid clip to the extent with a margin (0.4.18)"); }
        { bool raised; BotGridRules.Options g1 = BotGridRules.Parse(new List<string> { "Pallet", "5", "5" }, out err);
          Check(BotGridRules.Spacing(g1, 2f, 0f, 0f, out raised) == 4f && !raised, "grid default spacing 4 (0.4.18)");
          Check(BotGridRules.Spacing(g1, 5.2f, 0f, 0f, out raised) == 6f && raised, "grid spacing raised to the footprint (0.4.18)");
          BotGridRules.Options g2 = BotGridRules.Parse(new List<string> { "Pallet", "10", "5", "map" }, out err);
          Check(BotGridRules.Spacing(g2, 2f, 356f, 318f, out raised) == 35f && !raised, "grid map spacing spreads over the extent (0.4.18)"); }
        { int x0, x1, y0, y1; bool ok = BotGridRules.LandBox(8, 6, delegate(int x, int y) { return (x >= 2 && x <= 5 && y >= 1 && y <= 3); }, out x0, out x1, out y0, out y1);
          Check(ok && x0 == 2 && x1 == 5 && y0 == 1 && y1 == 3, "grid land box (0.4.18)");
          Check(!BotGridRules.LandBox(4, 4, delegate(int x, int y) { return false; }, out x0, out x1, out y0, out y1), "grid land box: no land (0.4.18)"); }
        Check(BotGridRules.Summary(7, 9, 1, 0, 1, 0) == "GRID placed 7 of 9 (skipped: occupied 1, out of map 0, not buildable 1)", "grid summary text (0.4.18)");
        { BotStep o = P("guest: observe doors other == 0 within 8m"); Check(o.Error == null && o.Kind == "observe" && o.Within == 480f && !o.IsCheck && o.Only == BotRole.Guest, "observe parses, is not a check (0.4.18)"); }
        Check(P("observe doors own >= 1").Within == 60f && P("observe nolog \"x\"").Error != null && P("observe log \"door\" within 10").Error == null, "observe default 60 s, no nolog (0.4.18)");

        BotScriptDoc d = BotScript.Parse(new string[] { "set run r1", "set role guest", "# c", "", "expect role guest", "bogus 1", "wait 1" });
        Check(d.Settings["run"] == "r1" && d.Get("role", "") == "guest" && d.Steps.Count == 3 && d.Errors.Count == 1 && d.Errors[0].StartsWith("L6:"), "doc settings, steps, errors with line numbers");
        Check(BotScript.Parse(new string[] { "# nothing" }).Errors.Count == 1, "empty script is an error");
        Check(d.CountChecks() == 1, "check count");
    }

    static void Rules()
    {
        float f;
        Check(BotRules.TryDuration("30", out f) && f == 30f && BotRules.TryDuration("2m", out f) && f == 120f && BotRules.TryDuration("1h", out f) && f == 3600f && BotRules.TryDuration("250ms", out f) && Math.Abs(f - 0.25f) < 1e-6, "durations");
        Check(!BotRules.TryDuration("", out f) && !BotRules.TryDuration("s", out f) && !BotRules.TryDuration("NaN", out f) && !BotRules.TryDuration("1,5", out f), "bad durations");
        Check(BotRules.Compare(40, ">=", 40) && !BotRules.Compare(39, ">=", 40) && BotRules.Compare(1, "==", 1) && BotRules.Compare(2, "!=", 1) && BotRules.Compare(0, "<", 1) && !BotRules.Compare(1, ">", 1) && BotRules.Compare(1, "<=", 1), "compare");
        string op; Check(BotRules.TryOp("=", out op) && op == "==" && !BotRules.TryOp("=>", out op), "ops");
        Check(BotRules.ValidName("bot-ow_1.a", 40) && !BotRules.ValidName("a b", 40) && !BotRules.ValidName(".x", 40) && !BotRules.ValidName("a..b", 40) && !BotRules.ValidName("a/b", 40) && !BotRules.ValidName(new string('a', 41), 40), "names");
        Check(BotRules.ValidIdent("OrangeCrystal") && !BotRules.ValidIdent("9x") && !BotRules.ValidIdent("a-b") && !BotRules.ValidIdent(""), "identifiers");
        Check(BotRules.ValidKey("Blueprints/OrangeWard") && BotRules.ValidKey("OrangeWard") && !BotRules.ValidKey("a/b/c") && !BotRules.ValidKey("/a") && !BotRules.ValidKey("a/") && !BotRules.ValidKey("a b"), "asset keys");
        Check(BotRules.ValidSelector("any") && BotRules.ValidSelector("3") && BotRules.ValidSelector("name=Bravo") && !BotRules.ValidSelector("name=") && !BotRules.ValidSelector("-1") && !BotRules.ValidSelector("x"), "selectors");
        Check(BotRules.SelectorMatches("any", 5, "x") && BotRules.SelectorMatches("2", 2, "x") && !BotRules.SelectorMatches("2", 1, "x") && BotRules.SelectorMatches("name=bra", 0, "Bravo") && !BotRules.SelectorMatches("name=zz", 0, "Bravo") && !BotRules.SelectorMatches("name=x", 0, null), "selector matching");
        string[] r = BotRules.ParseRoles("guest, host"); Check(r != null && r.Length == 2 && r[0] == "guest" && r[1] == "host", "roles parse with spaces");
        Check(BotRules.ParseRoles("") == null && BotRules.ParseRoles("host,guest,host") == null, "bad roles");
        Check(BotRules.RoleMatches(BotRole.Any, "menu") && BotRules.RoleMatches(BotRole.Host, "single") && BotRules.RoleMatches(BotRole.Host, "host") && !BotRules.RoleMatches(BotRole.Host, "guest") && BotRules.RoleMatches(BotRole.Guest, "guest") && !BotRules.RoleMatches(BotRole.Guest, "menu"), "role matching");
        string why;
        Check(BotRules.VpOutputOk("#3 aim sent Cat -> 1,2,3", out why) && why == null, "vp ok text");
        Check(!BotRules.VpOutputOk("#3 join RESULT=failed reason=0 friend(s)", out why) && why == "RESULT=failed", "vp failure text");
        Check(!BotRules.VpOutputOk("#4 fire: own catapult index 3 does not exist (have 1)", out why), "vp missing catapult");
        Check(!BotRules.VpOutputOk("#5 fillstock all types: stockpiles=0 crates filled=0", out why) && BotRules.VpOutputOk("#5 fillstock all types: stockpiles=2 crates filled=10", out why), "vp fillstock zero is a failure");
        Check(BotRules.VpOutputOk("#2 list: no own catapults", out why), "vp list without catapults is information");
        Check(BotRules.BarrierFile("b1", "host") == "b1.host.arrived", "barrier file name");
        Check(BotRules.BarrierMissing(new string[] { "b1.host.arrived", "B1.GUEST.ARRIVED" }, "b1", new string[] { "host", "guest" }) == "", "barrier complete (case-insensitive)");
        Check(BotRules.BarrierMissing(new string[] { "b1.host.arrived", "b2.guest.arrived" }, "b1", new string[] { "host", "guest" }) == "guest", "barrier missing guest");
        Check(BotRules.Verdict(true, false, 5, 0, 0) == "INVALID" && BotRules.Verdict(false, true, 5, 0, 0) == "ABORTED" && BotRules.Verdict(false, false, 5, 1, 0) == "FAIL" && BotRules.Verdict(false, false, 5, 0, 1) == "FAIL" && BotRules.Verdict(false, false, 0, 0, 0) == "NOCHECKS" && BotRules.Verdict(false, false, 2, 0, 0) == "PASS", "verdicts");
        string line = BotRules.Line("PASS", 12, 34.25f, "expect role host", "ok\r\nnext");
        Check(line == "PASS  L 12 t=34.3s expect role host | ok  next" || line == "PASS  L 12 t=34.2s expect role host | ok  next", "result line format: " + line);
        Check(BotRules.Cut(new string('x', 500), 10).Length == 13, "cut adds ...");
    }

    static void Ring()
    {
        BotLogRing ring = new BotLogRing(32); bool over;
        Regex re = new Regex("HELD carried model: orange");
        ring.Add("perf", "BOT #3 expect log HELD carried model: orange"); ring.Add("bepinex", "[Info] BOT echo HELD carried model: orange");
        Check(ring.LastSeq == 0 && ring.Find(re, "any", 0, out over) == null, "own BOT lines are never stored");
        ring.Add("perf", "11:00:00.000 t=1.00 ORANGE-WARD RECIPE HELD carried model: orange Accessories.HeldOrangeWard");
        BotLogEntry e = ring.Find(re, "any", 0, out over); Check(e != null && e.Seq == 1 && e.Source == "perf" && !over, "find a perf line");
        Check(ring.Find(re, "unity", 0, out over) == null && ring.Find(re, "perf", 0, out over) != null, "source filter");
        Check(ring.Find(re, "any", 1, out over) == null, "after seq excludes older lines");
        for (int i = 0; i < 100; i++) ring.Add("perf", "noise " + i);
        Check(ring.Find(re, "any", 0, out over) == null && over, "overflow is reported for an unwatched pattern");
        BotLogRing w = new BotLogRing(16); Regex boom = new Regex("Exception"); w.WatchPattern(boom); w.WatchPattern(boom);
        w.Add("unity", "NullReferenceException at X"); for (int i = 0; i < 200; i++) w.Add("perf", "noise " + i);
        e = w.Find(boom, "any", 0, out over); Check(e != null && e.Seq == 1 && !over, "a watched pattern survives the overflow");
        Check(w.Find(boom, "any", 1, out over) == null && !over, "watched: after seq still respected");
        w.ClearWatches(); Check(w.Find(boom, "any", 0, out over) == null && over, "unwatched again after ClearWatches");
        string big = new string('y', 5000); w.Add("perf", big); Check(w.Find(new Regex("^y+$"), "any", w.LastSeq - 1, out over).Text.Length == BotLogRing.MaxLine, "long lines are cut");
        // 0.4.27 review B-10: a watch made with fromBoot also knows the lines that were in the ring before it; when those lines are gone it says so
        BotLogRing b = new BotLogRing(16); b.Add("perf", "HELD pool ready"); Regex early = new Regex("pool ready"), plain = new Regex("pool ready"); b.WatchPattern(early, true); b.WatchPattern(plain);
        e = b.Find(early, "any", 0, out over); Check(e != null && e.Seq == 1 && !over && b.Find(early, "any", 1, out over) == null && b.Find(plain, "any", 0, out over) == null && !over, "fromBoot watch finds the earlier line, a plain watch does not");
        for (int i = 0; i < 40; i++) b.Add("perf", "noise " + i);
        Regex late = new Regex("pool ready"); b.WatchPattern(late, true);
        Check(b.Find(late, "any", 0, out over) == null && over, "fromBoot after an overflow: the gap is reported"); Check(b.Find(late, "any", b.LastSeq, out over) == null && !over && b.Find(early, "any", 0, out over) != null && !over, "no gap for newer lines; the earlier watch kept its hit");
        // many threads add at once: every line gets a unique sequence number and nothing throws
        BotLogRing m = new BotLogRing(100000); Regex hit = new Regex("^t\\d+ hit$"); m.WatchPattern(hit); int errors = 0;
        Thread[] th = new Thread[6];
        for (int k = 0; k < th.Length; k++) { int id = k; th[k] = new Thread(delegate() { try { for (int i = 0; i < 2000; i++) m.Add("bepinex", "t" + id + (i % 100 == 0 ? " hit" : " line " + i)); } catch (Exception) { Interlocked.Increment(ref errors); } }); th[k].Start(); }
        foreach (Thread x in th) x.Join();
        Check(errors == 0 && m.LastSeq == 12000, "threaded adds: 12000 unique sequence numbers");
        int hits = 0; long after = 0; BotLogEntry h;
        while ((h = m.Find(hit, "any", after, out over)) != null && hits < 1000) { hits++; after = h.Seq; }
        Check(hits >= 60 && hits <= 120, "threaded watch hits found (" + hits + ")");
    }

    static void Colours()
    {
        Check(BotColor.Classify(255, 140, 0) == "orange", "orange 255,140,0");
        Check(BotColor.Classify(255, 100, 20) == "orange", "orange-red tint 255,100,20");
        Check(BotColor.Classify(220, 20, 20) == "red", "red 220,20,20");
        Check(BotColor.Classify(200, 30, 60) == "red", "crimson is red");
        Check(BotColor.Classify(40, 120, 255) == "blue", "blue");
        Check(BotColor.Classify(80, 200, 255) == "blue", "cyan hologram counts as blue");
        Check(BotColor.Classify(240, 220, 30) == "yellow", "yellow");
        Check(BotColor.Classify(30, 180, 40) == "green", "green");
        Check(BotColor.Classify(128, 128, 128) == "gray" && BotColor.Classify(250, 250, 250) == "white" && BotColor.Classify(10, 10, 10) == "dark", "neutral colours");
        Check(BotColor.Classify(160, 40, 200) == "other", "purple is other");
        Check(BotColor.Classify(120, 90, 60) != "orange" && BotColor.Classify(40, 110, 120) != "blue" && BotColor.Classify(200, 220, 235) != "blue", "dark dirt, dark sea and pale sky are not vivid");
        BotColorStats inner = new BotColorStats(), ring = new BotColorStats();
        for (int i = 0; i < 30; i++) inner.Add(255, 110, 10); for (int i = 0; i < 70; i++) inner.Add(90, 90, 90); for (int i = 0; i < 100; i++) ring.Add(90, 90, 90);
        Check(BotColor.Verdict(inner, ring, 2.0, 2.0) == "orange", "verdict: orange gem on grey");
        BotColorStats ring2 = new BotColorStats(); for (int i = 0; i < 100; i++) ring2.Add(255, 110, 10);
        Check(BotColor.Verdict(inner, ring2, 2.0, 2.0) == "background", "verdict: orange all around = background");
        BotColorStats mix = new BotColorStats(); for (int i = 0; i < 20; i++) { mix.Add(255, 110, 10); mix.Add(220, 20, 20); }
        Check(BotColor.Verdict(mix, null, 2.0, 2.0) == "mixed" && BotColor.Verdict(new BotColorStats(), null, 2.0, 2.0) == "none", "verdict: mixed / none");
        float h, s, v; BotColor.ToHsv(0, 0, 255, out h, out s, out v); Check(Math.Abs(h - 240f) < 0.01f && s == 1f && v == 1f, "hsv of pure blue");
        BotColorStats st = new BotColorStats(); for (int i = 0; i < 3; i++) st.Add(255, 140, 0); st.Add(40, 120, 255);
        Check(st.Total == 4 && Math.Abs(st.Percent("orange") - 75.0) < 1e-9 && st.Percent("red") == 0.0, "colour shares");
        Check(st.Summary() == "px=4 orange=75.0% blue=25.0%", "colour summary: " + st.Summary());
    }

    static void Runner()
    {
        FakeWorld w; BotRunner r; BotLogRing ring;

        w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return c.Kind == "role" ? (c.Args[0] == "host" ? BotEval.Yes("role host") : BotEval.No("role host")) : BotEval.Unable("?"); };
        r = Run(new string[] { "set name t1", "expect role host", "expect role guest" }, w, new BotLogRing(64), 5f, null);
        Check(r.Finished && r.Report.Pass == 1 && r.Report.Fail == 1 && r.Report.Verdict == "FAIL", "pass + immediate fail");
        Check(w.lines[0].StartsWith("BOT RESULT v1 run=solo name=t1 role=auto/host steps=2 checks=2 test"), "header line: " + w.lines[0]);
        Check(w.Last.StartsWith("SUMMARY FAIL checks=2 pass=1 fail=1"), "summary line: " + w.Last);

        w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return BotEval.Yes("x"); };
        r = Run(new string[] { "expect role host" }, w, new BotLogRing(64), 5f, null); Check(r.Report.Verdict == "PASS", "single pass -> PASS");

        // a condition that becomes true after 1 s; and one that never does
        float clock = 0f; w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return c.Kind == "workers" && clock >= 1f ? BotEval.Yes("workers=3") : BotEval.No("workers=0"); };
        r = Run(new string[] { "expect workers >= 3 within 5", "expect guests >= 1 within 2" }, w, new BotLogRing(64), 20f, delegate(BotRunner x, float t) { clock = t; });
        Check(r.Report.Pass == 1 && r.Report.Fail == 1 && w.Has("PASS", "after 1.") && w.Has("FAIL", "timeout 2 s; last: workers=0"), "within: late pass and timeout");

        // actions: done, failed, pending -> done, pending -> timeout, exception
        w = new FakeWorld(); int polls = 0;
        w.onStart = delegate(BotStep s) { if (s.Name == "save") return BotOutcome.Wait("saving"); if (s.Name == "load") return BotOutcome.Wait("loading"); if (s.Name == "mute") throw new InvalidOperationException("boom"); return s.Name == "dump" ? BotOutcome.Fail("nothing") : BotOutcome.Ok("sent"); };
        w.onPoll = delegate(BotStep s, float el) { polls++; return s.Name == "save" && el >= 1f ? BotOutcome.Ok("saved") : BotOutcome.Wait("still"); };
        r = Run(new string[] { "craft altar 200", "dump all", "save bot-a", "load bot-a within 2", "mute on", "aim 0 1 2 3" }, w, new BotLogRing(64), 30f, null);
        Check(r.Report.Actions == 6 && r.Report.Ok == 3 && r.Report.Err == 3 && polls > 3, "action outcomes counted (ok=" + r.Report.Ok + " err=" + r.Report.Err + ")");
        Check(w.Has("OK", "save bot-a | saved") && w.Has("ERR", "load bot-a within 2 | timeout after 2 s (last: still)") && w.Has("ERR", "InvalidOperationException: boom"), "pending action lines");
        Check(w.started.Contains("aim") && r.Report.Verdict == "FAIL" && r.Report.Checks == 0, "legacy goes through Start; errors make FAIL");

        w = new FakeWorld();
        r = Run(new string[] { "craft altar 200", "wait 1" }, w, new BotLogRing(64), 5f, null); Check(r.Report.Verdict == "NOCHECKS", "actions only -> NOCHECKS");

        // 0.4.18 observe: FACT YES / NO lines that never fail the run and are not checks
        clock = 0f; w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return c.Args.Count > 0 && c.Args[0] == "own" ? (clock >= 3f ? BotEval.Yes("own doors=0") : BotEval.No("own doors=1")) : BotEval.No("other doors=1"); };
        r = Run(new string[] { "set onfail stop", "observe doors other == 0 within 2", "observe doors own == 0 within 5", "expect workers >= 0" }, w, new BotLogRing(64), 20f, delegate(BotRunner x, float tt) { clock = tt; });
        Check(r.Finished && r.Report.Facts == 2 && r.Report.Checks == 1 && r.Report.Fail == 1 && w.Has("FACT", "| NO timeout 2 s") && w.Has("FACT", "| YES after"), "observe writes FACT NO / YES, onfail stop ignores it (facts=" + r.Report.Facts + ")");
        Check(w.Last.Contains(" facts=2") && w.Last.StartsWith("SUMMARY FAIL checks=1 pass=0 fail=1"), "summary carries facts: " + w.Last);
        w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return BotEval.No("x"); };
        r = Run(new string[] { "observe doors own >= 1 within 1" }, w, new BotLogRing(64), 5f, null);
        Check(r.Report.Verdict == "NOCHECKS" && r.Report.Facts == 1 && !w.Last.Contains("FAIL"), "facts only -> NOCHECKS, never FAIL");

        // onfail stop
        w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return BotEval.No("no"); };
        r = Run(new string[] { "set onfail stop", "expect workers >= 1", "craft altar 200" }, w, new BotLogRing(64), 5f, null);
        Check(r.Report.Fail == 1 && r.Report.Actions == 0 && w.Has("INFO", "first failure"), "onfail stop ends the run");

        // script timeout while waiting
        w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return BotEval.No("not yet"); };
        r = Run(new string[] { "set timeout 10", "waituntil state match within 1h" }, w, new BotLogRing(64), 30f, null);
        Check(r.Finished && r.Report.Fail == 1 && w.Has("FAIL", "script timeout after 10 s") && r.Elapsed(99f) < 11f, "script timeout");

        // roles: host-only steps skipped on the guest, guest steps run
        w = new FakeWorld(); w.role = "guest"; w.onCheck = delegate(BotCondition c) { return BotEval.Yes("ok"); };
        r = Run(new string[] { "host: craft altar 200", "guest: expect role guest", "expect workers >= 0" }, w, new BotLogRing(64), 5f, null);
        Check(r.Report.Actions == 0 && r.Report.Pass == 2 && !w.started.Contains("craft"), "role filter on a guest");
        w = new FakeWorld(); w.role = "menu";
        r = Run(new string[] { "host: craft altar 200" }, w, new BotLogRing(64), 5f, null);
        Check(r.Report.Skip == 1 && w.Has("SKIP", "set role host|guest"), "role unknown in the menu -> SKIP");
        w = new FakeWorld(); w.role = "menu";
        r = Run(new string[] { "set role host", "host: craft altar 200" }, w, new BotLogRing(64), 5f, null);
        Check(r.Report.Actions == 1, "declared role runs in the menu");

        // barrier: waits for the peer, then passes; timeout names the missing role; arrival failure
        w = new FakeWorld(); w.role = "host";
        r = Run(new string[] { "set run r9", "barrier b1 within 10" }, w, new BotLogRing(64), 20f, delegate(BotRunner x, float t) { if (t >= 2f) w.peerArrived = true; });
        Check(r.Report.Pass == 1 && w.arrivals.Count == 1 && w.arrivals[0] == "r9/b1.host" && w.Has("PASS", "arrived after 2."), "barrier passes when the peer arrives");
        w = new FakeWorld(); r = Run(new string[] { "barrier b2 within 3" }, w, new BotLogRing(64), 10f, null);
        Check(r.Report.Fail == 1 && w.Has("FAIL", "missing: guest"), "barrier timeout");
        w = new FakeWorld(); w.arriveFails = true; r = Run(new string[] { "barrier b3" }, w, new BotLogRing(64), 10f, null);
        Check(r.Report.Fail == 1 && w.Has("FAIL", "cannot write"), "barrier arrival failure");
        w = new FakeWorld(); w.finishedPeer = true; r = Run(new string[] { "barrier b5 within 60" }, w, new BotLogRing(64), 10f, null);
        Check(r.Report.Fail == 1 && w.Has("FAIL", "already ended its run: guest (finished ABORTED)") && r.Elapsed(99f) < 2f, "barrier fails at once when the peer's run ended (0.4.11)");
        w = new FakeWorld(); w.role = "menu"; r = Run(new string[] { "barrier b4" }, w, new BotLogRing(64), 10f, null);
        Check(r.Report.Fail == 1 && w.Has("FAIL", "role unknown"), "barrier needs a role");

        // log checks: since action, own lines ignored, marks, nolog
        ring = new BotLogRing(256); ring.Add("perf", "old ORANGE line before the run");
        w = new FakeWorld();
        w.onStart = delegate(BotStep s) { ring.Add("perf", "BOT echo trace queue"); ring.Add("perf", "ORANGE-WARD RECIPE trace queue id=200"); return BotOutcome.Ok("sent"); };
        r = Run(new string[] { "craft altar 200", "expect log \"trace queue\" within 5", "expect log \"before the run\"", "expect log \"before the run\" since start" }, w, ring, 10f, null);
        Check(r.Report.Pass == 1 && r.Report.Fail == 2 && w.Has("PASS", "ORANGE-WARD RECIPE trace queue id=200"), "log since action finds the action's line only");
        ring = new BotLogRing(256); w = new FakeWorld();
        r = Run(new string[] { "mark m1", "wait 1", "expect log \"late line\" since mark:m1 within 5", "expect log \"x\" since mark:nope" }, w, ring, 10f, delegate(BotRunner x, float t) { if (Math.Abs(t - 2f) < 0.01f) ring.Add("unity", "late line"); });
        Check(r.Report.Pass == 1 && r.Report.Fail == 1 && w.marks.Count == 1 && w.Has("FAIL", "no mark 'nope'"), "marks");
        ring = new BotLogRing(256); ring.Add("unity", "NullReferenceException early"); w = new FakeWorld();
        r = Run(new string[] { "expect nolog \"NullReference\"" }, w, ring, 5f, null); Check(r.Report.Pass == 1, "nolog since start ignores lines before the run");
        ring = new BotLogRing(256); ring.Add("perf", "HELD pool ready"); ring.Add("unity", "lua expose FAILED"); w = new FakeWorld();   // 0.4.27 review B-10
        r = Run(new string[] { "expect log \"pool ready\" since boot", "expect nolog \"expose FAILED\" since boot", "expect log \"pool ready\" since start", "expect nolog \"expose FAILED\"" }, w, ring, 5f, null);
        Check(r.Report.Pass == 2 && r.Report.Fail == 2 && w.Has("PASS", "since boot | [perf, before this run] HELD pool ready") && w.Has("FAIL", "since boot | matched: [unity, before this run] lua expose FAILED"), "since boot reaches the lines logged before the run, since start does not");
        ring = new BotLogRing(256); ring.Add("perf", "CENSUS: 2 catapult(s)"); w = new FakeWorld();   // 0.4.27 verify B-10-v: a second run in the same game; the line of the earlier level still satisfies 'since boot', so it is marked
        r = Run(new string[] { "expect log \"CENSUS: [1-9]\" since boot within 3", "expect log \"CENSUS: 0\" since boot within 3", "expect log \"CENSUS: [1-9]\" since start within 1" }, w, ring, 10f, delegate(BotRunner x, float t) { if (Math.Abs(t - 1f) < 0.01f) ring.Add("perf", "CENSUS: 0 catapult(s)"); });
        Check(r.Report.Pass == 2 && r.Report.Fail == 1 && w.Has("PASS", "[perf, before this run] CENSUS: 2 catapult(s)") && w.Has("PASS", "[perf] CENSUS: 0 catapult(s)") && !w.Has("PASS", "before this run] CENSUS: 0"), "a since boot match older than the run is marked 'before this run', a line of the run is not");
        ring = new BotLogRing(16); ring.Add("unity", "lua expose FAILED"); for (int i = 0; i < 40; i++) ring.Add("perf", "noise " + i); w = new FakeWorld();
        r = Run(new string[] { "expect nolog \"expose FAILED\" since boot" }, w, ring, 5f, null); Check(r.Report.Fail == 1 && w.Has("FAIL", "cannot prove absence"), "nolog since boot after a ring overflow cannot pass");
        ring = new BotLogRing(256); w = new FakeWorld();
        r = Run(new string[] { "expect nolog \"Exception\" for 5" }, w, ring, 10f, delegate(BotRunner x, float t) { if (Math.Abs(t - 1f) < 0.01f) ring.Add("bepinex", "ArgumentException in X"); });
        Check(r.Report.Fail == 1 && w.Has("FAIL", "matched: [bepinex] ArgumentException") && r.Elapsed(99f) < 2f, "nolog fails at the first match");
        ring = new BotLogRing(256); w = new FakeWorld();
        r = Run(new string[] { "expect nolog \"Exception\" for 3" }, w, ring, 10f, null);
        Check(r.Report.Pass == 1 && r.Elapsed(99f) >= 3f, "nolog passes after its window");

        // negation, unable, invalid script, abort, pace, notes
        w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return c.Kind == "carry" ? BotEval.No("nobody carries") : BotEval.Unable("hologram not readable on this role"); };
        r = Run(new string[] { "expect not carry any OrangeWard", "expect hologram OrangeWard blue within 30" }, w, new BotLogRing(64), 40f, null);
        Check(r.Report.Pass == 1 && r.Report.Fail == 1 && r.Elapsed(99f) < 2f, "negation passes; Unable fails at once without waiting");
        w = new FakeWorld(); r = Run(new string[] { "craft altar 200", "bogus" }, w, new BotLogRing(64), 5f, null);
        Check(r.Report.Verdict == "INVALID" && w.started.Count == 0 && w.Has("ERR", "L2:"), "an invalid script does not run");
        w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return BotEval.No("no"); };
        r = new BotRunner(BotScript.Parse(new string[] { "waituntil state match within 60" }), w, new BotLogRing(64), 0f, null);
        r.Tick(0f); r.Tick(1f); r.Abort("kill switch", 1.5f); r.Tick(2f);
        Check(r.Finished && r.Report.Verdict == "ABORTED" && w.Has("ABORT", "kill switch") && w.Last.StartsWith("SUMMARY ABORTED"), "abort");
        w = new FakeWorld(); w.onCheck = delegate(BotCondition c) { return BotEval.Yes("ok"); };
        r = new BotRunner(BotScript.Parse(new string[] { "set pace 1", "note a", "mark m", "expect workers >= 0", "stop", "note never" }), w, new BotLogRing(64), 0f, null);
        r.Tick(0f); int afterFirst = r.StepIndex; for (int i = 1; i <= 100 && !r.Finished; i++) r.Tick(i * 0.1f);
        Check(afterFirst == 1 && r.Finished && r.Report.Pass == 1 && w.Has("INFO", "note a") && w.Has("INFO", "stop") && !w.Has("INFO", "never"), "pace, note, mark, stop");
    }
}
