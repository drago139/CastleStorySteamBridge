using System;
using CastleStorySteam;

// 0.4.22 DOOR-ATTACK: the rules only (DoorAttackRules + DoorApproachRules). No Unity.
// Build: csc DoorAttackTests.cs DoorAttackRules.cs DoorApproachRules.cs
static class DoorAttackTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    static int Main()
    {
        try
        {
            // guards: exactly one combination of the 8 flags passes (invincible must be false)
            Check(DoorAttackRules.Allowed(true, true, true, true, false, true, true, true), "on, host, door, enemy, breakable, must-attack, this door, same attacker");
            int pass = 0;
            for (int m = 0; m < 256; m++)
            {
                bool[] b = new bool[8]; for (int i = 0; i < 8; i++) b[i] = (m & (1 << i)) != 0;
                if (DoorAttackRules.Allowed(b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7])) pass++;
            }
            Check(pass == 1, "exactly one of 256 combinations allowed");
            Check(!DoorAttackRules.Allowed(true, true, true, false, false, true, true, true), "allied / neutral / own door: never");
            Check(!DoorAttackRules.Allowed(true, false, true, true, false, true, true, true), "guest: never");
            Check(!DoorAttackRules.Allowed(false, true, true, true, false, true, true, true), "switch off: never");
            Check(!DoorAttackRules.Allowed(true, true, true, true, false, false, true, true), "AI attack without MustAttackDestination: never");

            // door body like the recovered prefab: x {0,-1}, y {0,1,2}, z 0; bottom row y = 0
            int bottom = 0;
            Check(DoorAttackRules.NormalizeStandY(-1, bottom) == 0, "cell below the feet -> bottom row");
            Check(DoorAttackRules.NormalizeStandY(0, bottom) == 0 && DoorAttackRules.NormalizeStandY(1, bottom) == 1 && DoorAttackRules.NormalizeStandY(-2, bottom) == -2, "other rows unchanged");

            // ends (x = +1 and x = -2 at z 0) are face-adjacent to a door cell
            Check(DoorApproachRules.FaceAdjacent(1, 0, 0, 0, 0, 0), "end +1 adjacent to x0");
            Check(DoorApproachRules.FaceAdjacent(-2, 0, 0, -1, 0, 0), "end -2 adjacent to x-1");
            Check(DoorApproachRules.FaceAdjacent(0, 0, 1, 0, 0, 0) && DoorApproachRules.FaceAdjacent(0, 0, -1, 0, 0, 0), "faces adjacent");
            Check(!DoorApproachRules.FaceAdjacent(1, 0, 1, 0, 0, 0), "diagonal is not adjacent");
            Check(!DoorApproachRules.FaceAdjacent(1, 1, 0, 0, 0, 0), "upper row is not adjacent to a bottom cell");

            Check(DoorAttackRules.SideCell(0, bottom, true, false, true, false, false), "free end cell accepted");
            Check(!DoorAttackRules.SideCell(1, bottom, true, false, true, false, false), "upper row rejected");
            Check(!DoorAttackRules.SideCell(0, bottom, false, false, true, false, false), "diagonal rejected");
            Check(!DoorAttackRules.SideCell(0, bottom, true, true, true, false, false), "inside the door rejected");
            Check(!DoorAttackRules.SideCell(0, bottom, true, false, false, false, false), "full / not walkable (cliff, wall) rejected");
            Check(!DoorAttackRules.SideCell(0, bottom, true, false, true, true, false), "blocker in the cell rejected");
            Check(!DoorAttackRules.SideCell(0, bottom, true, false, true, false, true), "blocker above rejected");
            Check(DoorAttackRules.SideCell(DoorAttackRules.NormalizeStandY(-1, bottom), bottom, true, false, true, false, false), "a node reported one below the feet is accepted after normalising");

            Check(DoorAttackRules.StrikeThroughOwnTarget(true, true), "midpoint inside the target door: strike allowed");
            Check(!DoorAttackRules.StrikeThroughOwnTarget(true, false), "midpoint in another door / wall: native rule");
            Check(!DoorAttackRules.StrikeThroughOwnTarget(false, true), "not a registered door attack: native rule");

            Check(DoorAttackRules.WantLog(0) && DoorAttackRules.WantLog(DoorAttackRules.LogCap - 1) && !DoorAttackRules.WantLog(DoorAttackRules.LogCap), "log cap");
            Check(DoorAttackRules.RegistryCap >= 64, "registry cap sane");
            Console.WriteLine("DoorAttackTests: " + checks + " checks PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("DoorAttackTests FAIL after " + checks + " checks: " + e.Message); return 1; }
    }
}
