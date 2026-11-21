// 0.4.10 IN-GAME TEST BOT game layer: taken from the read-only research draft bot-research\altar-BotCraft.cs (side-session-notes of local_a8d26072, 2026-11-06),
// compiled with the plugin (signatures checked by csc against the real game DLLs). Called only by the bot (Bot.cs / BotActions.cs) while it is armed.
// altar-BotCraft.cs  (research scratch, area 'altar', 2026-11-06). NOT COMPILED: every signature used here was confirmed by decompiling the real
// DLLs in D:\SteamLibrary\steamapps\common\Castle Story\Castle Story_Data\Managed (see the altar-*.cs files next to this one), C# 5 only.
// Purpose: the game calls a test bot needs for 'craft <station> <recipe> [count]', 'assign .. craft altar', 'expect queue altar ..',
// 'expect items <Resource> ..', 'dump altar' and the native 'fillstock <Resource> <crates>' of BotScript.cs.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Brix.External.Factories;          // Factory, ObjetsDynamiques
using Brix.Game;                        // User, Faction, Affiliation, AffiliationComponent
using Brix.Game.AI;                     // Labor, Adjectif, Ressource, Quantifiable, Occupation, AutonomyStatus
using Brix.Game.Components;             // CraftingStation, CraftingState, RecipeInfo, Recipe, CookBook, GameComponentExtender (IsNullOrReleased)
using Brix.Game.Network;                // UNetProjectCmd, UNetContextualCmd
using Brix.Game.Semantique;             // Recepteur
using Brix.Game.Storage;                // StorageLocator, CompoundRecepteur
using Brix.Lifecycle.Pooling;           // ObjectPoolSingleton (Brix.Core.dll)
using Brix.Lua;                         // LuaCrafting
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    internal static class BotCraft
    {
        // ------------------------------------------------------------------ station lookup
        // Altar is 'internal class Altar : CraftingStation' -> not visible to the plugin. Use the public PieMenuTag ("Altar", "Workbench", "Forge",
        // "MachineShop", "Lab", "Loom", "Furnace", "ChoppingBlock"). Ownership = the exact test of the HUD (UIGameSelector.IsYourOwn) and of the host Cmd.
        internal static CraftingStation FindOwnStation(string pieMenuTag)
        {
            User u = User.LocalUser;
            if (u == null) return null;
            CraftingStation best = null;
            foreach (CraftingStation s in UnityEngine.Object.FindObjectsOfType<CraftingStation>())
            {
                if (s == null || s.IsNullOrReleased()) continue;
                if (!string.Equals(s.PieMenuTag, pieMenuTag, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Affiliation.IsOwnedBy(s.gameObject, u.gameObject)) continue;
                Vector3 p = s.transform.position;   // deterministic order on host and guest (instance ids differ between PCs)
                if (best == null || p.x < best.transform.position.x || (p.x == best.transform.position.x && p.z < best.transform.position.z)) best = s;
            }
            return best;
        }

        // ------------------------------------------------------------------ recipe name / id -> CookBook ids (what the HUD sends)
        // HUD: Lua Recipe.X -> LuaCrafting.ToRecipeInfo(X) -> one CallCmd per RecipeInfo (kits = several ids, e.g. ArtificerKit = 12,13,14).
        // OrangeWard (mod): CookBook id 200. The Lua value 37 is local to one PC and never goes over the network.
        internal static List<int> ResolveRecipe(string word, out string error)
        {
            error = null;
            List<int> ids = new List<int>();
            int id;
            if (int.TryParse(word, out id))
            {
                try { CookBook.GetById(id); ids.Add(id); }
                catch (KeyNotFoundException) { error = "recipe id " + id + " is not registered on this PC"; }
                return ids;
            }
            if (string.Equals(word, "OrangeWard", StringComparison.OrdinalIgnoreCase))
            {
                if (OrangeWardRecipe.Info == null) { error = "orange ward recipe (id 200) not registered on this PC"; return ids; }
                ids.Add(OrangeWardRecipe.RecipeId);
                return ids;
            }
            LuaCrafting.LuaRecipe lr;
            try { lr = (LuaCrafting.LuaRecipe)Enum.Parse(typeof(LuaCrafting.LuaRecipe), word, true); }
            catch (ArgumentException) { error = "unknown recipe '" + word + "'"; return ids; }
            try { foreach (RecipeInfo ri in LuaCrafting.ToRecipeInfo(lr)) ids.Add(ri.id); }
            catch (Exception e) { error = "no recipe for " + lr + " (LuaCrafting maps are built by the first Lua Script of a match): " + e.GetType().Name; }
            return ids;
        }

        // Altar recipes a human can click (Info\Lua\LUI\Meta\Meta_Operable.lua:44-48 + the mod's slot 6). The Cmd itself does NOT check this.
        // ids by CookBook declaration order: Staff 12, Sachel 13, ArtificerHat 14, SentinelWard 26, HealingWard 27, CaptureWard 28, PylonWard 30; OrangeWard 200
        internal static int[] AltarIds()
        {
            return new int[] { CookBook.Staff.id, CookBook.Sachel.id, CookBook.ArtificerHat.id, CookBook.SentinelWard.id, CookBook.HealingWard.id,
                               CookBook.CaptureWard.id, CookBook.PylonWard.id, OrangeWardRecipe.RecipeId };
        }

        // The orange recipe is safe to queue only when the Lua maps know it: else the Altar HUD (CraftingStation.lua_GetQueue -> LuaCrafting.ToLuaRecipe)
        // throws KeyNotFoundException as soon as someone selects that Altar.
        internal static bool OrangeRecipeReady(out string why)
        {
            why = null;
            if (OrangeWardRecipe.Info == null) { why = "recipe 200 not registered"; return false; }
            try { LuaCrafting.ToLuaRecipe(OrangeWardRecipe.Info); }
            catch (Exception e) { why = "Lua exposure not done (" + e.GetType().Name + ")"; return false; }
            return true;
        }

        // ------------------------------------------------------------------ queue (the call the HUD recipe button ends in)
        // CraftingStation.lua_AddToQueue: localUserProject.CallCmdcraftingQueueRecipe(craftingStation.gameObject, item.id, 1).
        // Host: runs CmdcraftingQueueRecipe at once. Guest: sends it. The host IGNORES qty (QueueRecipe(_r) without qty) and stops at 8 entries.
        internal static string Queue(CraftingStation s, List<int> ids, int count)
        {
            User u = User.LocalUser;
            UNetProjectCmd pc = u == null ? null : u.GetComponent<UNetProjectCmd>();
            if (s == null) return "no own station";
            if (pc == null) return "no UNetProjectCmd on the local user (not in a match?)";
            if (!Affiliation.IsOwnedBy(s.gameObject, u.gameObject)) return "station not owned by the local user: the host would drop the command silently";
            int free = 8 - s.QueueCount, sent = 0;
            for (int k = 0; k < count; k++)
            {
                foreach (int id in ids)
                {
                    if (sent >= free) return "queue full: sent " + sent + ", the host drops the rest";
                    pc.CallCmdcraftingQueueRecipe(s.gameObject, id, 1);   // host: a bad id throws KeyNotFoundException HERE; guest: on the host
                    sent++;
                }
            }
            return null;
        }

        internal static void ClearQueue(CraftingStation s) { User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdcraftingDoNothing(s.gameObject); }          // HUD 'clear' (L)
        internal static void RemoveAt(CraftingStation s, int zeroBased) { User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdcraftingRemoveAt(s.gameObject, zeroBased); }   // 0 = cancel the current craft
        internal static void SetLoop(CraftingStation s, bool on) { User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdcraftingLoop(s.gameObject, on); }

        // ------------------------------------------------------------------ state for assertions
        internal static List<int> QueueIds(CraftingStation s)   // decoded from the CraftingState.recipes SyncVar: host and guest
        {
            List<int> r = new List<int>();
            foreach (RecipeInfo ri in s.QueuedRecipes) r.Add(ri == null ? -1 : ri.id);
            return r;
        }

        static readonly FieldInfo workField = typeof(Recipe).GetField("work", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static int RemainingWork(CraftingStation s)     // host only (the station's behaviour tree runs on the server); -1 = nothing popped / guest
        {
            Recipe cur = s.CurrentRecipe;
            if (cur == null || workField == null) return -1;
            return (int)workField.GetValue(cur);
        }

        static string Dump(Description d)
        {
            if (d == null || d.DicoAdjectif == null) return "[]";
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (KeyValuePair<Type, Adjectif> kv in d.DicoAdjectif)
            {
                Quantifiable q = kv.Value == null ? null : kv.Value.quantifiable;
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key.Name);
                if (q != null) sb.Append(':').Append(q.valeur);
            }
            return sb.Append(']').ToString();
        }

        internal static string Describe(CraftingStation s)
        {
            StringBuilder sb = new StringBuilder();
            CraftingState st = s.GetComponent<CraftingState>();
            Labor op = s.Operator;
            Recepteur rec = s.recepteur;
            List<int> q = QueueIds(s);
            sb.Append(s.PieMenuTag).Append(" pos=").Append(s.transform.position.ToString("F0"))
              .Append(" faction=").Append(s.faction == null ? "none" : s.faction.ID.ToString())
              .Append(" own=").Append(User.LocalUser != null && Affiliation.IsOwnedBy(s.gameObject, User.LocalUser.gameObject))
              .Append(" queue=[");
            for (int i = 0; i < q.Count; i++) { if (i > 0) sb.Append(','); sb.Append(q[i]); }
            sb.Append("] mode=").Append(s.CurrentMode).Append(" loop=").Append(s.Looping).Append(" autoStore=").Append(s.AutoStore)
              .Append(" operator=").Append(op == null ? "none" : op.name)
              .Append(" working=").Append(st != null && st.Working);
            Description need = s.StuffToFetch();
            sb.Append(" missing=").Append(need == null ? "nothing" : Dump(need));
            sb.Append(" content=").Append(Dump(rec.ContentDescription));
            Recipe cur = s.CurrentRecipe;   // null on a guest
            if (cur != null && cur.Info != null) sb.Append(" current=").Append(cur.Info.id).Append(" workLeft=").Append(RemainingWork(s));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ counts per faction
        // Crates = what the HUD stock bar sums (FactionStorage: every Palette of the faction). Works on host and guest (item parenting is a SyncVar).
        // Resource names: Adjectif.StringToAdjectif("OrangeCrystal" | "Iron" | "Stick" | "SentinelWardAdj" ...). Orange ward item = Stick (identity on).
        // crate ownership as the tested StockFill.cs decides it: the stockpile's AffiliationComponent first, else the crate's own faction; no faction = own
        internal static bool CrateOwnedBy(StorageLocator loc, Recepteur c, Faction f)
        {
            AffiliationComponent a = loc == null ? null : loc.GetComponent<AffiliationComponent>();
            Faction cf = a != null && a.faction != null ? a.faction : (c == null ? null : c.faction);
            return cf == null || cf == f;
        }

        internal static int CountInCrates(Faction f, Ressource r)
        {
            int total = 0;
            foreach (StorageLocator loc in UnityEngine.Object.FindObjectsOfType<StorageLocator>())
            {
                if (loc == null || loc.Storage == null) continue;
                foreach (Recepteur c in loc.Storage.GetRecepteurs())
                    if (c != null && CrateOwnedBy(loc, c, f)) total += c.ContentDescription.Value(r);
            }
            return total;
        }

        internal static int CountCarried(Faction f, Ressource r)   // worker bags of the faction
        {
            int total = 0;
            foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>())
                if (l != null && l.faction == f && l.recepteur != null) total += l.recepteur.ContentDescription.Value(r);
            return total;
        }

        // the other player's faction as seen on the host (2-account test): needed because only the host can fill crates
        internal static Faction OtherPlayerFaction()
        {
            foreach (User u in UnityEngine.Object.FindObjectsOfType<User>())
                if (u != null && u != User.LocalUser && u.faction != null) return u.faction;
            return null;
        }

        // ------------------------------------------------------------------ stock (HOST ONLY: NetworkServer.Spawn)
        // Recepteur.WipeAndFill fills a crate to its base capacity (real level6 data): OrangeCrystal 80, Iron 16, RawIron 80, Stick 80, SentinelWardAdj 4.
        internal static int FillEmptyCrates(Faction f, Ressource r, int crates)
        {
            if (!NetworkServer.active || f == null || r == null) return -1;
            int done = 0;
            foreach (StorageLocator loc in UnityEngine.Object.FindObjectsOfType<StorageLocator>())
            {
                if (loc == null || loc.Storage == null) continue;
                foreach (Recepteur c in loc.Storage.GetRecepteurs())
                {
                    if (done >= crates) return done;
                    if (c == null || !CrateOwnedBy(loc, c, f) || !c.IsEmpty()) continue;
                    c.WipeAndFill(r);
                    done++;
                }
            }
            return done;
        }

        // exact amount into one EMPTY crate: the same three calls as Recepteur.WipeAndFill, with a count instead of the capacity
        internal static int FillExact(Recepteur crate, Ressource r, int count)
        {
            if (!NetworkServer.active || crate == null || r == null || !crate.IsEmpty()) return 0;
            Factory.AssetKey key = Description.RepresentativeOf(r);   // OrangeCrystal -> RawResources.RawOrangeCrystal, Iron -> BuildResources.IronIngot
            if (key == null) return 0;
            int n = Math.Min(count, crate.BaseCapacity.Value(r));
            for (int i = 0; i < n; i++)
            {
                GameObject go = ObjectPoolSingleton.Request_Unsafe(key, true);
                crate.AddObject(go);
                NetworkServer.Spawn(go, key.AssetId);
            }
            return n;
        }

        // ------------------------------------------------------------------ workers
        internal static List<Labor> OwnBuilders()
        {
            List<Labor> res = new List<Labor>();
            User u = User.LocalUser;
            if (u == null) return res;
            foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>())
                if (l != null && Affiliation.IsOwnedBy(l.gameObject, u.gameObject) && l.Profession != null && l.Profession.OccupationType == Occupation.Type.Builder) res.Add(l);
            res.Sort(delegate(Labor a, Labor b) { return string.CompareOrdinal(a.name, b.name); });
            return res;
        }

        // Legacy pie-menu command 'Action_Crafting_AssignTo' (no button in the current LUI, but the host still handles it): Hire + LockedToOperable.
        // ONE operator per station (MaximumCrewSize 1): hiring a second worker fires the first (it falls back to FreeAgent).
        internal static void AssignWorker(CraftingStation s, Labor l) { User.LocalUser.GetComponent<UNetProjectCmd>().CallCmdCraftingAssignWorker(s.gameObject, l.gameObject); }

        // Current UI right-click on the station with the workers selected (PickerOrderGiver.DirectOrder -> BuilderActor.ToCraftingStation ->
        // CraftingStation.GetContextualAction): pick up a finished product, or store what they carry into it, or work if the ingredients are inside.
        internal static void RightClick(CraftingStation s, List<Labor> workers)
        {
            GameObject[] arr = new GameObject[workers.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = workers[i].gameObject;
            User.LocalUser.GetComponent<UNetContextualCmd>().CallCmdBatchedContextualActionGameObject(arr, s.gameObject, Brix.Input.ContextualOrders.ActionType.Primary);
        }

        // ------------------------------------------------------------------ completed crafts (HOST ONLY)
        // Postfix of Recipe.Finish: Info is still valid there. A postfix of CraftingStation.Finish / CraftingLabor.FinishRecipe sees a pooled Recipe with
        // Info == null (FinishRecipe ends with CurrentRecipe = null -> Recipe.Dispose -> ResetObject).
        internal static readonly Dictionary<int, int> Crafted = new Dictionary<int, int>();
        internal static void InstallFinishHook(HarmonyLib.Harmony h)
        {
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Recipe), "Finish"), null,
                new HarmonyLib.HarmonyMethod(typeof(BotCraft).GetMethod("AfterRecipeFinish", BindingFlags.Static | BindingFlags.NonPublic)));
        }
        static void AfterRecipeFinish(Recipe __instance, Recepteur recepteur)
        {
            try
            {
                if (!SteamBridgePlugin.BotArmedNow || __instance == null || __instance.Info == null || !__instance.Finished) return;
                int id = __instance.Info.id, n;
                Crafted.TryGetValue(id, out n);
                Crafted[id] = n + 1;
                // tag "BOT-CRAFTED" (not "BOT "): lines starting with "BOT " are the bot's own echo and are never matched by 'expect log'
                SteamBridgePlugin.PtLog("BOT-CRAFTED recipe=" + id + " at=" + (recepteur == null ? "null" : recepteur.name) + " total=" + (n + 1) + " (host)");
            }
            catch (Exception) { }
        }
    }
}
