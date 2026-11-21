using System;
using System.IO;
using System.Text;
using CastleStorySteam;

// 0.4.10 offline scenario checker for the in-game test bot: the SAME parser the game uses (BotScript.cs), so a scenario with an error is
// refused before any game starts. Not part of the plugin (BuildPlugin.ps1 does not list it). Build + run (Run-BotTest.ps1 does it):
//   csc /out:BotScriptCheck.exe BotScriptCheck.cs BotScript.cs BotRules.cs VirtualPlayerRules.cs
//   BotScriptCheck.exe <scenario.txt> [more files]      exit 0 = all OK, 1 = errors, 2 = bad arguments
public static class BotScriptCheck
{
    public static int Main(string[] args)
    {
        if (args == null || args.Length == 0) { Console.WriteLine("usage: BotScriptCheck <scenario.txt> [...]"); return 2; }
        int bad = 0;
        foreach (string f in args)
        {
            if (!File.Exists(f)) { Console.WriteLine("MISSING " + f); bad++; continue; }
            BotScriptDoc d = BotScript.Parse(File.ReadAllLines(f, Encoding.UTF8));
            int host = 0, guest = 0, barriers = 0;
            foreach (BotStep s in d.Steps) { if (s.Only == BotRole.Host) host++; else if (s.Only == BotRole.Guest) guest++; if (s.Kind == "barrier") barriers++; }
            if (d.Errors.Count == 0)
                Console.WriteLine("OK " + Path.GetFileName(f) + ": steps=" + d.Steps.Count + " checks=" + d.CountChecks() + " host-only=" + host + " guest-only=" + guest + " barriers=" + barriers + " name=" + d.Get("name", "-"));
            else { bad++; Console.WriteLine("ERRORS " + Path.GetFileName(f) + ": " + d.Errors.Count); foreach (string e in d.Errors) Console.WriteLine("  " + e); }
        }
        return bad == 0 ? 0 : 1;
    }
}
