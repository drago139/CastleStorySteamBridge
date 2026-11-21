using System;
using CastleStorySteam;

public static class VirtualPlayerTests
{
    static int failed;
    static void Check(bool ok, string name) { if (!ok) { failed++; Console.WriteLine("FAIL " + name); } }
    public static int Main()
    {
        VirtualAction a;
        a = VirtualPlayerRules.Parse("aim 0 515.5 133 646"); Check(a != null && a.Error == null && a.Name == "aim" && a.Cat == 0 && a.X == 515.5f && a.Z == 646f, "aim parses");
        a = VirtualPlayerRules.Parse("HOVER 1, 10, 20, 30  # comment"); Check(a != null && a.Error == null && a.Name == "hover" && a.Cat == 1 && a.Y == 20f, "hover case/commas/comment");
        a = VirtualPlayerRules.Parse("leave 2"); Check(a != null && a.Error == null && a.Cat == 2, "leave");
        a = VirtualPlayerRules.Parse("clear 0"); Check(a != null && a.Error == null, "clear");
        a = VirtualPlayerRules.Parse("fire 0"); Check(a != null && a.Error == null, "fire");
        a = VirtualPlayerRules.Parse("dig 1 2 3"); Check(a != null && a.Error == null && a.X == 1f && a.Z == 3f, "dig");
        a = VirtualPlayerRules.Parse("wait 2.5"); Check(a != null && a.Error == null && a.Seconds == 2.5f, "wait");
        a = VirtualPlayerRules.Parse("list"); Check(a != null && a.Error == null, "list");
        a = VirtualPlayerRules.Parse("status"); Check(a != null && a.Error == null, "status");
        foreach (string w in new string[] { "join", "joinlan", "snapshot", "heapwalk", "ready", "unready", "start", "lobby" }) { a = VirtualPlayerRules.Parse(w); Check(a != null && a.Error == null && a.Name == w, w + " parses"); }
        a = VirtualPlayerRules.Parse("join now"); Check(a != null && a.Error != null, "join takes no argument");
        a = VirtualPlayerRules.Parse("net jitter_ms 150"); Check(a != null && a.Error == null && a.Key == "jitter_ms" && a.Seconds == 150f, "net parses");
        a = VirtualPlayerRules.Parse("net smooth -1"); Check(a != null && a.Error == null && a.Seconds == -1f, "net negative value");
        a = VirtualPlayerRules.Parse("net rm-rf 1"); Check(a != null && a.Error != null, "bad net key rejected");
        a = VirtualPlayerRules.Parse("walk 500 620"); Check(a != null && a.Error == null && a.X == 500f && a.Z == 620f, "walk parses");
        a = VirtualPlayerRules.Parse("walk 500"); Check(a != null && a.Error != null, "walk needs two numbers");
        a = VirtualPlayerRules.Parse("doors"); Check(a != null && a.Error == null && a.Name == "doors", "doors");
        a = VirtualPlayerRules.Parse("breakdoor"); Check(a != null && a.Error == null && a.Cat == 0, "breakdoor nearest");
        a = VirtualPlayerRules.Parse("breakdoor 2"); Check(a != null && a.Error == null && a.Cat == 2, "breakdoor index");
        a = VirtualPlayerRules.Parse("breakdoor x"); Check(a != null && a.Error != null, "breakdoor bad index");
        a = VirtualPlayerRules.Parse("breakdoor 1 2"); Check(a != null && a.Error != null, "breakdoor too many args");
        a = VirtualPlayerRules.Parse("fillstock"); Check(a != null && a.Error == null && a.Key == null, "fillstock all");
        a = VirtualPlayerRules.Parse("fillstock PlankBlock"); Check(a != null && a.Error == null && a.Key == "PlankBlock", "fillstock one");
        a = VirtualPlayerRules.Parse("fillstock rm-rf"); Check(a != null && a.Error != null, "fillstock bad name");
        a = VirtualPlayerRules.Parse("barrels"); Check(a != null && a.Error == null && a.Name == "barrels", "barrels");
        Check(VirtualPlayerRules.Parse("   ") == null, "blank ignored"); Check(VirtualPlayerRules.Parse("# only comment") == null, "comment ignored"); Check(VirtualPlayerRules.Parse(null) == null, "null ignored");
        a = VirtualPlayerRules.Parse("aim 0 1 2"); Check(a != null && a.Error != null, "aim missing arg rejected");
        a = VirtualPlayerRules.Parse("aim x 1 2 3"); Check(a != null && a.Error != null, "bad catapult index rejected");
        a = VirtualPlayerRules.Parse("aim 0 1 2 NaN"); Check(a != null && a.Error != null, "NaN rejected");
        a = VirtualPlayerRules.Parse("aim 0 99999 2 3"); Check(a != null && a.Error != null, "out of range rejected");
        a = VirtualPlayerRules.Parse("wait 9999"); Check(a != null && a.Error != null, "long wait rejected");
        a = VirtualPlayerRules.Parse("format c:"); Check(a != null && a.Error != null, "unknown command rejected");
        int errs; var s = VirtualPlayerRules.ParseScript(new string[] { "list", "aim 0 1 2 3", "bogus", "# c", "" }, out errs); Check(s.Count == 3 && errs == 1, "script counts");
        Console.WriteLine(failed == 0 ? "PASS: virtual player parser (43 checks)" : "FAILED " + failed);
        return failed == 0 ? 0 : 1;
    }
}
