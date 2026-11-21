namespace CastleStorySteam
{
    // 0.4.22 DOOR-ATTACK, pure part (no Unity; tested by DoorAttackTests.cs). The game part is DoorAttackFix.cs (+ the shared side-cell test in DoorDemolitionFix.cs).
    // B-074 (user 2026-11-08: he deliberately ordered his soldiers to break an ENEMY gate, they 'ignored it'). Combat path (CmdAttack -> AttackInfo.GoAttackDirectly) strikes a
    // door only from its native face ACCESS cells (recovered door prefab: two rows on the faces, none at the ends; spear reach ignored for object targets). When neither face is
    // reachable (cliff edge, trees = Forest cells, enemy base) the units walk to the gate's end, fail the path 3 times and leave (wf_ed5a247e-77e, ~70 %, not proven live).
    internal static class DoorAttackRules
    {
        internal const int RegistryCap = 256;   // workers with a live attack-a-door order kept; invalid entries are pruned past this
        internal const int LogCap = 80;         // distinct GAMEPLAY-FIX Door attack lines per run

        // every condition must hold before an extra (side) cell is accepted for a combat order on a door
        internal static bool Allowed(bool enabled, bool server, bool isDoor, bool isEnemy, bool invincible, bool mustAttackDestination, bool destinationIsThisDoor, bool sameAttacker)
        {
            return enabled && server && isDoor && isEnemy && !invincible && mustAttackDestination && destinationIsThisDoor && sameAttacker;
        }

        // A* / BFS nodes may report the cell below the feet (the native door access rows sit at bottom and bottom-1); both mean "standing on the door's bottom row".
        internal static int NormalizeStandY(int y, int bottom)
        {
            return y == bottom - 1 ? bottom : y;
        }

        // a stand cell is a side cell: on the bottom row, face-adjacent (no diagonal) to a door cell, not inside the door, open and walkable, no blocker in it or above it
        internal static bool SideCell(int y, int bottom, bool faceAdjacent, bool insideDoor, bool openWalkable, bool blocker, bool blockerAbove)
        {
            return y == bottom && faceAdjacent && !insideDoor && openWalkable && !blocker && !blockerAbove;
        }

        // the strike's midpoint test (AttackInfo.DoMeleeAction) fails from a door END because the midpoint falls into the door's other column; that column is the target itself
        internal static bool StrikeThroughOwnTarget(bool ready, bool midpointInTargetDoor)
        {
            return ready && midpointInTargetDoor;
        }

        internal static bool WantLog(int linesSoFar) { return linesSoFar < LogCap; }
    }
}
