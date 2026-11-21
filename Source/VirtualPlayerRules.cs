using System;
using System.Collections.Generic;
using System.Globalization;

namespace CastleStorySteam
{
    // 0.3.78 virtual player: pure parser of one line of BepInEx\config\test-actions.txt (unit-tested, no Unity).
    // Format: "<command> <arg> <arg> ..." ; '#' starts a comment. Numbers use '.' and invariant culture.
    //   list                         write the catapults (index, faction, position) to the result file
    //   hover  <cat> <x> <y> <z>     exactly what the mouse hover does: Catapult.Viser(point, up) (writes cible LOCALLY on a client)
    //   leave  <cat>                 mouse leaves: Catapult.ArreterDeViser()
    //   aim    <cat> <x> <y> <z>     mouse click on the ground: UNetCatapult.CallCmdSetTarget(catapult, point, up) (the real network command)
    //   clear  <cat>                 UNetCatapult.CallCmdClearTarget
    //   fire   <cat>                 UNetCatapult.CallCmdRequestFireOnce
    //   dig    <x> <y> <z>           UNetProjectCmd.CallCmdAddBreakVoxelGoal with the first own worker
    //   doors                        write the doors (index by distance, position, faction) to the result file
    //   breakdoor [n]                demolish the n-th nearest door (0 = nearest): UNetBlueprint.CmdPlaceAnti into an own build project
    //   fillstock [Resource]         test helper: fill own stockpiles (all resource types spread over the crates, or one type)
    //   wait   <seconds>             pause the script
    //   status                       write a one-line status`r`n    //   join                         guest: refresh the friend list and join the Steam host (same as the F8 button "join the selected host"; only if exactly one friend is in the game)`r`n    //   ready / unready              the lobby ready toggle (LobbyMenu.SetIsReady)`r`n    //   start                        host: LobbyMenu.StartGame()`r`n    //   lobby                        log scene, lobby/ready state, map, mode and whether the bridge is up
    public sealed class VirtualAction
    {
        public string Name, Key; public int Cat = -1; public float X, Y, Z, Seconds; public string Error;
    }

    public static class VirtualPlayerRules
    {
        public static VirtualAction Parse(string raw)
        {
            VirtualAction a = new VirtualAction();
            if (raw == null) return null;
            string line = raw; int hash = line.IndexOf('#'); if (hash >= 0) line = line.Substring(0, hash); line = line.Trim();
            if (line.Length == 0) return null;
            string[] p = line.Split(new char[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            a.Name = p[0].ToLowerInvariant();
            switch (a.Name)
            {
                case "list": case "doors": case "barrels": case "status": case "join": case "joinlan": case "snapshot": case "heapwalk": case "ready": case "unready": case "start": case "lobby": return Need(a, p, 0);
                case "leave": case "clear": case "fire": return Need(a, p, 1) == null ? null : CatOnly(a, p);
                case "hover": case "aim": return Need(a, p, 4) == null ? null : CatXyz(a, p, 1);
                case "dig": return Need(a, p, 3) == null ? null : Xyz(a, p, 1);
                case "net":
                    if (p.Length != 3) { a.Error = "net needs <key> <value>"; return a; }
                    a.Key = p[1].ToLowerInvariant(); foreach (char ch in a.Key) if (!(ch >= 'a' && ch <= 'z' || ch == '_')) { a.Error = "bad net key"; return a; }
                    if (!Num(p[2], out a.Seconds) || a.Seconds < -1f || a.Seconds > 100000f) { a.Error = "bad net value"; return a; }
                    return a;
                case "walkrel":
                    if (p.Length != 3 || !Num(p[1], out a.X) || !Num(p[2], out a.Z) || Math.Abs(a.X) > 500f || Math.Abs(a.Z) > 500f) { a.Error = "walkrel needs <dx> <dz>"; return a; }
                    return a;
                case "aimrel":
                    { int c; if (p.Length != 4 || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out c) || c < 0 || c > 99 || !Num(p[2], out a.X) || !Num(p[3], out a.Z) || Math.Abs(a.X) > 500f || Math.Abs(a.Z) > 500f) { a.Error = "aimrel needs <catapult> <dx> <dz>"; return a; } a.Cat = c; return a; }
                case "digrel":
                    if (p.Length != 4 || !Num(p[1], out a.X) || !Num(p[2], out a.Y) || !Num(p[3], out a.Z) || Math.Abs(a.X) > 500f || Math.Abs(a.Y) > 100f || Math.Abs(a.Z) > 500f) { a.Error = "digrel needs <dx> <dy> <dz>"; return a; }
                    return a;
                case "walk":
                    if (p.Length != 3 || !Num(p[1], out a.X) || !Num(p[2], out a.Z) || Math.Abs(a.X) > 5000f || Math.Abs(a.Z) > 5000f) { a.Error = "walk needs <x> <z>"; return a; }
                    return a;
                case "wait":
                    if (p.Length != 2 || !Num(p[1], out a.Seconds) || a.Seconds < 0f || a.Seconds > 600f) { a.Error = "wait needs 0..600 seconds"; return a; }
                    return a;
                case "breakdoor":
                    // breakdoor [n]: order the demolition of the n-th nearest door (0 = nearest) like the remover tool does
                    if (p.Length > 2) { a.Error = "breakdoor needs 0 or 1 argument"; return a; }
                    a.Cat = 0; if (p.Length == 2 && (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out a.Cat) || a.Cat < 0 || a.Cat > 99)) a.Error = "bad door index";
                    return a;
                case "fillstock":
                    // fillstock [ResourceCodeName]: test helper, fill the own stockpiles (every resource type spread over the crates, or one type)
                    if (p.Length > 2) { a.Error = "fillstock needs 0 or 1 argument"; return a; }
                    if (p.Length == 2) { a.Key = p[1]; foreach (char ch in a.Key) if (!(ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z')) { a.Error = "bad resource name"; return a; } }
                    return a;
                default: a.Error = "unknown command '" + p[0] + "'"; return a;
            }
        }
        static VirtualAction Need(VirtualAction a, string[] p, int args) { if (p.Length != args + 1) { a.Error = a.Name + " needs " + args + " argument(s)"; return a; } return a; }
        static VirtualAction CatOnly(VirtualAction a, string[] p) { if (a.Error != null) return a; int c; if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out c) || c < 0 || c > 99) a.Error = "bad catapult index"; else a.Cat = c; return a; }
        static VirtualAction CatXyz(VirtualAction a, string[] p, int at) { if (a.Error != null) return a; int c; if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out c) || c < 0 || c > 99) { a.Error = "bad catapult index"; return a; } a.Cat = c; return Xyz(a, p, 2); }
        static VirtualAction Xyz(VirtualAction a, string[] p, int at) { if (a.Error != null) return a; if (!Num(p[at], out a.X) || !Num(p[at + 1], out a.Y) || !Num(p[at + 2], out a.Z)) a.Error = "bad coordinates"; else if (Math.Abs(a.X) > 5000f || Math.Abs(a.Y) > 1000f || Math.Abs(a.Z) > 5000f) a.Error = "coordinates out of range"; return a; }
        static bool Num(string s, out float v) { return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v); }

        public static List<VirtualAction> ParseScript(string[] lines, out int errors)
        {
            List<VirtualAction> list = new List<VirtualAction>(); errors = 0;
            foreach (string l in lines) { VirtualAction a = Parse(l); if (a == null) continue; if (a.Error != null) errors++; list.Add(a); if (list.Count >= 200) break; }
            return list;
        }
    }
}
