using System;
using System.Collections.Generic;
using System.Text;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.CatapultSpace;
using Brix.Game.Network;
using Brix.Legacy;
using Brix.UI.Builder.Menu;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.4.18 IN-GAME TEST BOT, ENEMY DOOR TEST (scenario VirtualPlayer\Bot\scenarios\door-enemy-2inst.txt). Every action ends in the Cmd a player's input
    // ends in, so the host decides as with a human enemy:
    //   team / faction <n> [probe]        lobby pickers (LegacyLobbyLeftMenu.PlayerTeam / PlayerFaction): GameParam.Is*Assignable, then LobbyPlayer.CmdSetTeam /
    //                                     CmdSetFaction; 'probe' only reports whether the game would allow it (nothing sent)
    //   demolishdoor own|other|any [i]    the remover on a door: UNetBlueprint.CmdPlaceAnti into the bot's own build task + commit (as 'demolish');
    //                                     the answer says whether an anti blueprint showed up near the door (= the host accepted the order)
    //   attackdoor <who|all> own|other [i] UNetProjectCmd.CmdAttack(worker, door) = the attack order on that door
    //   firedoor <cat> own|other [i]      an own catapult: UNetCatapult.CmdSetTarget(door position) + CmdRequestFireOnce (legacy aim + fire)
    //   condition 'doors own|other|any <op> <n>', 'dump doors', 'dump lobby'
    // own = the local player's faction object; other = any other faction (ally or enemy: the line says which, as this PC sees the relation).
    public sealed partial class SteamBridgePlugin
    {
        static List<Porte> BotDoors(string owner)
        {
            string o = owner == null ? "any" : owner.ToLowerInvariant(); Faction own = BotOwnFaction();
            List<Porte> res = new List<Porte>();
            foreach (Porte d in UnityEngine.Object.FindObjectsOfType<Porte>())
            {
                if (d == null || d.IsNullOrReleased() || !d.gameObject.activeInHierarchy) continue;
                Faction f = BotDoorFaction(d);
                if (o == "own" && (own == null || f != own)) continue;
                if (o == "other" && (f == null || f == own)) continue;
                res.Add(d);
            }
            Vector3 an = VpAnchor();
            res.Sort(delegate(Porte x, Porte y) { return (x.transform.position - an).sqrMagnitude.CompareTo((y.transform.position - an).sqrMagnitude); });
            return res;
        }

        static Faction BotDoorFaction(Porte d)
        {
            try { Faction f = Faction.GetFaction(d.gameObject); if (f != null) return f; } catch (Exception) { }
            AffiliationComponent a = d.GetComponentInParent<AffiliationComponent>();
            return a == null ? null : a.faction;
        }

        static string BotDoorLine(Porte d, int i)
        {
            Faction own = BotOwnFaction(), f = BotDoorFaction(d); StringBuilder b = new StringBuilder();
            b.Append("door[").Append(i).Append("] ").Append(d.name).Append(" pos=").Append(CsV(d.transform.position));
            FactoryImprint fi = d.GetComponentInParent<FactoryImprint>(); if (fi != null) b.Append(" key=").Append(fi.AssetKey);
            b.Append(" faction=").Append(f == null ? "none" : f.name + "#" + f.ID);
            b.Append(" mine=").Append(own != null && f == own);
            string rel; try { rel = own == null || f == null ? "?" : Affiliation.GetRelation(own, f).ToString(); } catch (Exception e) { rel = e.GetType().Name; }
            b.Append(" relation(seen here)=").Append(rel);
            try { b.Append(" state=").Append(d._doorState).Append(" locked=").Append(d.locked); } catch (Exception) { }
            try { var dr = d.damageReceiver; b.Append(" hp=").Append(dr == null ? "none" : dr.CurrentHP.ToString("F0", BotIc) + "/" + dr.MaxHP.ToString("F0", BotIc) + (dr.IsInvincible ? " INVINCIBLE" : "")); } catch (Exception e) { b.Append(" hp=").Append(e.GetType().Name); }
            // every anti (demolish) blueprint within 3 m, ANY faction: on the host this shows whether the enemy's remover order exists there
            foreach (Blueprint x in UnityEngine.Object.FindObjectsOfType<Blueprint>())
            {
                if (x == null || x.IsNullOrReleased() || !x.IsAnti || Vector3.Distance(x.transform.position, d.transform.position) > 3f) continue;
                Faction xf = null; try { xf = x.faction; } catch (Exception) { }
                b.Append(" | anti near (faction ").Append(xf == null ? "none" : xf.name + "#" + xf.ID).Append(own != null && xf == own ? ", mine" : "").Append("): ").Append(BotBlueprint.Describe(x));
            }
            return b.ToString();
        }

        static List<string> BotDumpDoors()
        {
            List<string> l = new List<string>(); List<Porte> all = BotDoors("any"); Faction own = BotOwnFaction();
            l.Add("doors total=" + all.Count + " own=" + BotDoors("own").Count + " other=" + BotDoors("other").Count + " localFaction=" + (own == null ? "none" : own.name + "#" + own.ID) + " net=" + BotNetRole());
            for (int i = 0; i < all.Count && i < 30; i++) l.Add(BotDoorLine(all[i], i));
            return l;
        }

        static Porte BotDoorAt(string owner, int index, out string why)
        {
            why = null; List<Porte> l = BotDoors(owner);
            if (index < l.Count) return l[index];
            why = "no " + owner + " door #" + index + " (this PC sees " + l.Count + " " + owner + ", " + BotDoors("any").Count + " in all)"; return null;
        }

        // team / faction <n> [probe]
        BotOutcome BotTeamFaction(BotStep s, BotGameWorld w)
        {
            bool team = s.Name == "team"; int want; BotRules.TryInt(s.Args[0], out want); bool probe = s.Args.Count == 2;
            bool sent = false; float sentAt = 0f;
            w.pending = delegate(float el)
            {
                if (BotMenu.Current() != BotMenu.Phase.Lobby) return BotOutcome.Wait("not in a lobby (phase " + BotMenu.Current() + ")");
                int cur; string why; bool ok = BotMenu.GroupAssignable(team, want, out cur, out why);
                if (cur < 0) return BotOutcome.Wait(why);
                if (probe) return BotOutcome.Ok("PROBE " + s.Name + " " + want + ": " + (want == cur ? "already" : ok ? "ASSIGNABLE" : "NOT ASSIGNABLE (" + why + ")") + " - nothing sent | " + BotMenu.DescribeTeams());
                if (want == cur) return BotOutcome.Ok(s.Name + " " + want + (sent ? " set after " + (el - sentAt).ToString("F1", BotIc) + " s" : " already") + " | " + BotMenu.DescribeLobby() + " | " + BotMenu.DescribeTeams());
                if (sent) return BotOutcome.Wait("sent, the host has not applied it yet (now " + cur + ")");
                if (!ok) return BotOutcome.Fail(s.Name + " " + want + " NOT ASSIGNABLE: " + why + " | " + BotMenu.DescribeTeams());
                BotMenu.SendGroup(team, want); sent = true; sentAt = el;
                return BotOutcome.Wait("LobbyPlayer.CmdSet" + (team ? "Team" : "Faction") + "(" + want + ") sent (now " + cur + ")");
            };
            return w.pending(0f);
        }

        // demolishdoor own|other|any [index]: the remover on the door. Ends Ok in both cases: the answer is the fact (anti accepted or not); the
        // scenario decides with 'doors ..' checks / facts whether the door went.
        BotOutcome BotDemolishDoor(BotStep s, BotGameWorld w)
        {
            string why = BotBlueprint.WhyCannotBuild(); if (why != null) return BotOutcome.Fail(why);
            string owner = s.Args[0].ToLowerInvariant(); int idx = 0; if (s.Args.Count == 2) BotRules.TryInt(s.Args[1], out idx);
            Porte door = BotDoorAt(owner, idx, out why); if (door == null) return BotOutcome.Fail(why);
            string line = BotDoorLine(door, idx); Vector3 tpos = door.transform.position; GameObject target = door.gameObject;
            BotProjectWaiter pw = new BotProjectWaiter(); int phase = 0; float sentAt = 0f;
            w.pending = delegate(float el)
            {
                if (phase == 0) { BotOutcome o = pw.Step(w); if (o != null) return o; phase = 1; }
                if (phase == 1)
                {
                    string e = BotBlueprint.SendDemolish(target, w.project);
                    if (e != null) return BotOutcome.Ok("REMOVER NOT SENT (the tool itself refuses here): " + e + " | " + line);
                    phase = 2; sentAt = el; return BotOutcome.Wait("anti blueprint sent for " + line);
                }
                Blueprint b = BotBlueprint.BlueprintNear(tpos, null, true, 3f);
                bool gone = target == null || target.IsNullOrReleased() || !target.activeInHierarchy;
                if (phase == 2)
                {
                    if (b == null)
                    {
                        if (gone) return BotOutcome.Ok("ANTI ACCEPTED and finished at once: the door is gone | " + line);
                        if (el - sentAt < 25f) return BotOutcome.Wait("anti blueprint not here yet");
                        return BotOutcome.Ok("ANTI NOT SEEN 25 s after CmdPlaceAnti (the host refused or removed it) | " + line);
                    }
                    BotBlueprint.SendCommit(w.project); phase = 3; return BotOutcome.Wait("ANTI ACCEPTED, committing: " + BotBlueprint.Describe(b));
                }
                if (b == null) return BotOutcome.Ok("ANTI ACCEPTED; the anti blueprint already went (door " + (gone ? "gone" : "still there") + ") | " + line);
                if (!b.commited) return BotOutcome.Wait("waiting for the commit");
                return BotOutcome.Ok("ANTI ACCEPTED and committed: " + BotBlueprint.Describe(b) + " | " + BotBlueprint.Hologram(b) + " | " + line);
            };
            return w.pending(0f);
        }

        // attackdoor <selector|all> own|other|any [index]: the attack order of the action wheel on that door
        // 'siege' = UNetProjectCmd.CmdBreakObject(worker, door): the order the game's UI sends only for an ALLIED door (DirectOrderProject.AddBreakObjectGoal:
        // allied -> CmdBreakObject = siege break job, else CmdAttack); the server checks only that the worker is the sender's. For an enemy door this is a
        // PROBE beyond the normal UI: does the host run the siege job on an enemy door at all?
        BotOutcome BotAttackDoor(BotStep s, BotGameWorld w)
        {
            bool siege = s.Text != null && s.Text.TrimEnd().EndsWith(" siege", StringComparison.OrdinalIgnoreCase);
            string owner = s.Args[1].ToLowerInvariant(); int idx = 0; if (s.Args.Count == 3) BotRules.TryInt(s.Args[2], out idx);
            string why; Porte door = BotDoorAt(owner, idx, out why); if (door == null) return BotOutcome.Fail(why);
            User u = User.LocalUser; UNetProjectCmd pc = u == null ? null : u.GetComponent<UNetProjectCmd>();
            if (pc == null) return BotOutcome.Fail("no UNetProjectCmd on the local user");
            bool all = s.Args[0].ToLowerInvariant() == "all";
            List<Labor> who = all ? BotWorkers.OwnWorkers(true) : BotWorkersMatching(s.Args[0]);
            List<string> names = new List<string>();
            foreach (Labor l in who) { if (BotIsOperator(l)) continue; if (siege) pc.CallCmdBreakObject(l.gameObject, door.gameObject); else pc.CallCmdAttack(l.gameObject, door.gameObject); names.Add(BotWorkers.DisplayName(l)); }
            if (names.Count == 0) return BotOutcome.Fail("no matching own unit (station operators are left alone)");
            return BotOutcome.Ok((siege ? "CmdBreakObject (siege, beyond the normal UI for an enemy door) sent for " : "CmdAttack sent for ") + string.Join(", ", names.ToArray()) + " -> " + BotDoorLine(door, idx));
        }

        // firedoor <catapult> own|other|any [index]: aim the own catapult at the door, then fire once (a loaded barrel is needed: the game decides)
        BotOutcome BotFireDoor(BotStep s, BotGameWorld w)
        {
            int cat; BotRules.TryInt(s.Args[0], out cat); string owner = s.Args[1].ToLowerInvariant(); int idx = 0; if (s.Args.Count == 3) BotRules.TryInt(s.Args[2], out idx);
            string why; Porte door = BotDoorAt(owner, idx, out why); if (door == null) return BotOutcome.Fail(why);
            List<CatapultControl> cats = VpOwnCatapults();
            if (cat >= cats.Count) return BotOutcome.Fail("own catapult #" + cat + " does not exist (have " + cats.Count + ")");
            User u = User.LocalUser; UNetCatapult cmd = u == null ? null : u.GetComponent<UNetCatapult>();
            if (cmd == null) return BotOutcome.Fail("no UNetCatapult on the local user");
            CatapultControl cc = cats[cat]; Vector3 pt = door.transform.position; string line = BotDoorLine(door, idx);
            cmd.CallCmdSetTarget(cc.gameObject, pt, Vector3.up);
            bool fired = false;
            w.pending = delegate(float el)
            {
                if (!fired) { if (el < 3f) return BotOutcome.Wait("aimed " + cc.name + " at " + CsV(pt) + ", firing in 3 s"); cmd.CallCmdRequestFireOnce(cc.gameObject); fired = true; }
                return BotOutcome.Ok("CmdSetTarget + CmdRequestFireOnce sent: " + cc.name + " at " + CsV(cc.transform.position) + " -> " + line);
            };
            return w.pending(0f);
        }
    }
}
