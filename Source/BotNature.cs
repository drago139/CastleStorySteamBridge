using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.4.12 IN-GAME TEST BOT: player-like work orders (research p12, side-session-notes bot-research\p12-*.cs + the agent report):
    //   harvest  = New task > Harvest > Trees / Brimstone (pyrite = OrangeCrystal) / Iron / Boulders / Plants: UNetGroupe.CmdSpawnHarvestProject
    //              (NatureType bits plant 1, boulder 2, iron 4, pyrite 8, tree 16), radius = UNetProjectCmd.CmdAccepterRayon, crew = CmdChangeProject;
    //              mined items go into OWN CRATES only (NatureHarvestGoal.RequireStorage): build a Pallet first; felled trees drop 3 logs on the ground
    //   unstump  = the remover tool on Blueprints/Wood_Stump voxel blocks (left at the root of every felled tree): UNetBlueprint.CmdPlaceAnti + commit
    //   autonomy = the action wheel's free agent / hold position (UNetProjectCmd.CmdSetAutonomy)
    // A crate (Pallet) costs 1 PlankBlock for a player (tree -> ChoppingBlock -> planks -> pallet); 'placerel Pallet .. instant' is the HOST TEST HELPER.
    public sealed partial class SteamBridgePlugin
    {
        static readonly string[] BotNatureNames = { "plant", "boulder", "iron", "pyrite", "tree" };   // bit = 1 << index
        static readonly string[] BotNatureFiles = { "plants.json", "boulders.json", "iron.json", "pyrite.json", "trees.json" };
        static readonly Regex BotXyz = new Regex("\"x\"\\s*:\\s*(-?[0-9.]+)\\s*,\\s*\"y\"\\s*:\\s*(-?[0-9.]+)\\s*,\\s*\"z\"\\s*:\\s*(-?[0-9.]+)", RegexOptions.CultureInvariant);

        // the deposit / tree nearest to the own workers, from the loaded map's nature file (the start positions; a deposit may be used up later)
        static string BotNearestNature(string type, out Vector3 near, out int count)
        {
            near = Vector3.zero; count = 0;
            int k = Array.IndexOf(BotNatureNames, type); if (k < 0) return "unknown nature type " + type;
            Brix.Assets.Asset_Map map = GameParam.Map; if (map == null || string.IsNullOrEmpty(map.path)) return "no loaded map";
            string file = System.IO.Path.Combine(map.path.Replace('/', '\\'), BotNatureFiles[k]);
            if (!File.Exists(file)) return "the map has no " + BotNatureFiles[k] + " (" + file + ")";
            Vector3 an = VpAnchor(); if (an == Vector3.zero) return "no own workers for the anchor";
            float best = float.MaxValue;
            foreach (Match m in BotXyz.Matches(File.ReadAllText(file)))
            {
                float x, y, z;
                if (!BotRules.TryFloat(m.Groups[1].Value, out x) || !BotRules.TryFloat(m.Groups[2].Value, out y) || !BotRules.TryFloat(m.Groups[3].Value, out z)) continue;
                count++; float d = (x - an.x) * (x - an.x) + (z - an.z) * (z - an.z);
                if (d < best) { best = d; near = new Vector3(x, y, z); }
            }
            return count == 0 ? "no positions in " + BotNatureFiles[k] : null;
        }

        static List<HarvestProject> BotOwnHarvestTasks()
        {
            List<HarvestProject> res = new List<HarvestProject>(); Faction f = BotOwnFaction(); if (f == null) return res;
            foreach (HarvestProject p in UnityEngine.Object.FindObjectsOfType<HarvestProject>()) if (p != null && p.faction == f && !p.gameObject.IsNullOrReleased()) res.Add(p);
            return res;
        }

        // harvest <type> nearest|<x> <z> [radius] [crew]
        BotOutcome BotHarvest(BotStep s, BotGameWorld w)
        {
            User u = User.LocalUser; UNetGroupe g = u == null ? null : u.GetComponent<UNetGroupe>(); UNetProjectCmd pc = u == null ? null : u.GetComponent<UNetProjectCmd>();
            if (g == null || pc == null || !NetworkClient.active) return BotOutcome.Fail("not in a match (no UNetGroupe / UNetProjectCmd)");
            string type = s.Args[0].ToLowerInvariant(); int bit = 1 << Array.IndexOf(BotNatureNames, type);
            float x, z; int i; string where;
            if (s.Args[1].ToLowerInvariant() == "nearest")
            {
                Vector3 near; int n; string e = BotNearestNature(type, out near, out n); if (e != null) return BotOutcome.Fail(e);
                x = near.x; z = near.z; i = 2; where = "nearest " + type + " of " + n + " in the map at " + CsV(near) + ", " + Vector3.Distance(near, VpAnchor()).ToString("F0", BotIc) + " cells from the workers";
            }
            else { BotRules.TryFloat(s.Args[1], out x); BotRules.TryFloat(s.Args[2], out z); i = 3; where = x.ToString("F0", BotIc) + "," + z.ToString("F0", BotIc); }
            int radius = 10, crew = 2;
            if (s.Args.Count > i) BotRules.TryInt(s.Args[i], out radius);
            if (s.Args.Count > i + 1) BotRules.TryInt(s.Args[i + 1], out crew);
            Vector3 ground; if (!BotBlueprint.GroundAt(x, z, out ground)) return BotOutcome.Fail("no ground under " + where);
            HashSet<int> before = new HashSet<int>(); foreach (HarvestProject p in BotOwnHarvestTasks()) before.Add(p.gameObject.GetInstanceID());
            g.CallCmdSpawnHarvestProject(ground, Quaternion.identity, true, bit);   // New task > Harvest > <type>, placed with the beacon picker
            HarvestProject task = null; string crewText = "";
            w.pending = delegate(float el)
            {
                if (task == null)
                {
                    foreach (HarvestProject p in BotOwnHarvestTasks()) if (!before.Contains(p.gameObject.GetInstanceID())) { task = p; break; }
                    if (task == null) return BotOutcome.Wait("waiting for the new harvest task");
                    pc.CallCmdAccepterRayon(task.gameObject, Mathf.Clamp(radius, 1, 30));   // the beacon radius
                    int added = 0;
                    foreach (Labor l in BotCraft.OwnBuilders())
                    {
                        if (added >= crew) break;
                        if (BotIsOperator(l) || w.held.ContainsKey(l) || (l.recepteur != null && !l.recepteur.IsEmpty())) continue;
                        if (BotWorkers.AssignToProject(l, task)) { added++; crewText += (crewText.Length > 0 ? ", " : "") + BotWorkers.DisplayName(l); }
                    }
                    return BotOutcome.Ok("harvest task for " + type + " at " + where + ", radius " + radius + ", crew " + added + (crewText.Length > 0 ? " (" + crewText + ")" : "") + (type == "tree" ? "; felled trees drop 3 logs each on the ground" : "; mined items need an own crate with room"));
                }
                return BotOutcome.Ok("harvest task exists");
            };
            return BotOutcome.Wait("harvest task requested at " + where);
        }

        // own stumps (Blueprints/Wood_Stump voxel blocks) within the radius of the anchor; voxel reads, host reliable (a guest: unknown)
        static List<XYZ> BotFindStumps(int radius)
        {
            List<XYZ> roots = new List<XYZ>(); HashSet<string> seen = new HashSet<string>();
            Vector3 an = VpAnchor(); if (an == Vector3.zero) return roots;
            int cx = Mathf.RoundToInt(an.x), cy = Mathf.RoundToInt(an.y), cz = Mathf.RoundToInt(an.z);
            for (int x = cx - radius; x <= cx + radius; x++)
                for (int z = cz - radius; z <= cz + radius; z++)
                    for (int y = cy - 8; y <= cy + 12; y++)
                    {
                        IVoxel v = Voxel.GetVoxel(new XYZ(x, y, z)); if (v == null) continue;
                        IVoxel r = v.RootOrSelf; if (r == null || r.IsNull) continue;
                        BlockInfo bi = r.BlockInfo; if (bi == null) continue;
                        Factory.AssetKey key = bi.BlueprintKey; if (key == null || key.Name != "Wood_Stump") continue;
                        string id = r.Position.ToString(); if (seen.Add(id)) roots.Add(r.Position);
                    }
            return roots;
        }
        static float botStumpAt = -10f; static int botStumpCount;
        static int BotStumpCountCached(float now)   // a 20-cell scan is about 17 000 voxel reads: at most every 2 s
        {
            if (now - botStumpAt >= 2f) { botStumpAt = now; botStumpCount = BotFindStumps(20).Count; }
            return botStumpCount;
        }

        // unstump [radius] [instant]: the remover tool on every stump near the workers (max 12), committed in the run's build task
        BotOutcome BotUnstump(BotStep s, BotGameWorld w)
        {
            int radius = 15; bool instant = false;
            foreach (string a in s.Args) { int n; if (BotRules.TryInt(a, out n)) radius = n; else if (a.ToLowerInvariant() == "instant") instant = true; }
            if (instant && !NetworkServer.active) return BotOutcome.Fail("instant: host / single player only");
            string why = BotBlueprint.WhyCannotBuild(); if (why != null) return BotOutcome.Fail(why);
            List<XYZ> roots = BotFindStumps(Mathf.Clamp(radius, 1, 40));
            if (roots.Count == 0) return BotOutcome.Ok("no stump within " + radius + " cells of the workers (fell trees first: harvest tree ..)");
            if (roots.Count > 12) roots.RemoveRange(12, roots.Count - 12);
            GameObject tpl = Factory.Peek(Blueprints.Wood_Stump); BaseVoxelTransform vt = tpl == null ? null : tpl.GetComponent<BaseVoxelTransform>();
            Vector3 off = vt == null ? Vector3.zero : vt.CenterOffset;
            BotProjectWaiter pw = new BotProjectWaiter(); int phase = 0;
            w.pending = delegate(float el)
            {
                if (phase == 0) { BotOutcome o = pw.Step(w); if (o != null) return o; phase = 1; }
                if (phase == 1)
                {
                    UNetBlueprint ub = User.LocalUser.GetComponent<UNetBlueprint>();
                    foreach (XYZ r in roots) ub.CallCmdPlaceAnti(Blueprints.Wood_Stump.Factory, Blueprints.Wood_Stump.Name, r.ToVector3() - off, Quaternion.identity, instant, w.project.gameObject);
                    BotBlueprint.SendCommit(w.project); phase = 2;
                    return BotOutcome.Wait("remover sent on " + roots.Count + " stump(s)");
                }
                return BotOutcome.Ok("remover (anti blueprint) on " + roots.Count + " stump(s) within " + radius + " cells, committed" + (instant ? " (instant TEST HELPER)" : "; a builder breaks them: assign .. build"));
            };
            return w.pending(0f);
        }

        // autonomy <selector|all> freeagent|hold: the action wheel's toggle
        BotOutcome BotAutonomy(BotStep s, BotGameWorld w)
        {
            bool all = s.Args[0].ToLowerInvariant() == "all"; bool free = s.Args[1].ToLowerInvariant() == "freeagent";
            List<Labor> who = all ? BotWorkers.OwnWorkers(true) : BotWorkersMatching(s.Args[0]);
            List<string> names = new List<string>();
            foreach (Labor l in who) { if (BotIsOperator(l)) continue; BotWorkers.SetAutonomy(l, free ? AutonomyStatus.Ids.freeAgent : AutonomyStatus.Ids.holdingPosition); names.Add(BotWorkers.DisplayName(l)); }
            return names.Count > 0 ? BotOutcome.Ok((free ? "free agents: " : "hold position: ") + string.Join(", ", names.ToArray())) : BotOutcome.Fail("no matching own worker (station operators are left alone)");
        }

        // own crates (stockpile Recepteurs, ownership as StockFill decides)
        static List<Recepteur> BotOwnCrates()
        {
            List<Recepteur> res = new List<Recepteur>(); Faction f = BotOwnFaction(); if (f == null) return res;
            foreach (StorageLocator loc in UnityEngine.Object.FindObjectsOfType<StorageLocator>())
            {
                if (loc == null || loc.Storage == null) continue;
                foreach (Recepteur c in loc.Storage.GetRecepteurs()) if (c != null && BotCraft.CrateOwnedBy(loc, c, f)) res.Add(c);
            }
            return res;
        }
    }
}
