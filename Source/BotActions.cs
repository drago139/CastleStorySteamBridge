using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using Brix.Input;
using Brix.UI.Builder.Menu;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Steamworks;

namespace CastleStorySteam
{
    // 0.4.10 IN-GAME TEST BOT: native actions and state checks. Maps the commands of BotScript.cs onto the game layer (BotCraft, BotBlueprint,
    // BotWorkers, BotSaveLoad, BotMenu, OrangeBot, BotEvidence). Every action ends in the same call or Cmd a player's input ends in, so a guest
    // behaves as with a human (host authority unchanged). The two HOST TEST HELPERS (stock, fillstock) do what no player can: put items into crates.
    // Long actions answer Pending and keep their progress in a closure (world.pending), polled by BotRunner every 0.2 s up to their timeout.
    public sealed partial class SteamBridgePlugin
    {
        sealed partial class BotGameWorld
        {
            internal BuildGoalProvider project;          // the build task the bot created in this run (place, demolish, assign build)
            internal BotCamState cam;                    // camera before the first camera/shot step (restored at the end)
            internal int muteSaved = -1;                 // SoundEngine.mute before 'mute' (restored at the end)
            internal readonly Dictionary<Labor, AutonomyStatus.Ids> held = new Dictionary<Labor, AutonomyStatus.Ids>();   // workers the bot put on hold (pickup, assign .. exclusive) -> autonomy before (restored at the end)
            internal Labor carrier;                                  // the worker of the last 'pickup' ('deliver any' takes it first)
            internal int editorSaved = -1, debugMenuSaved = -1;      // Architecte.editorMode / allowDebugMenu before 'debug' (restored at the end)
            internal string camScene;                                // scene of the saved camera: never restored into another map
        }

        static void BotSaveCam(BotGameWorld w, CameraScript cs) { if (w.cam != null) return; w.cam = BotCamera.Save(cs); w.camScene = SceneManager.GetActiveScene().name; }
        static bool BotIsOperator(Labor l) { try { return l.Autonomy != null && l.Autonomy.Id == AutonomyStatus.Ids.lockedToOperable; } catch (Exception) { return false; } }
        // hold position (no autonomous work) so a carried load stays in the hands; the old autonomy comes back at the end of the run
        static void BotHold(BotGameWorld w, Labor l)
        {
            if (l == null || w.held.ContainsKey(l)) return;
            AutonomyStatus.Ids before = AutonomyStatus.Ids.freeAgent;
            try { if (l.Autonomy != null) before = l.Autonomy.Id; } catch (Exception) { }
            w.held[l] = before; BotWorkers.SetAutonomy(l, AutonomyStatus.Ids.holdingPosition);
        }
        static GameObject BotNetFind(uint id) { NetworkInstanceId nid = new NetworkInstanceId(id); return NetworkServer.active ? NetworkServer.FindLocalObject(nid) : ClientScene.FindLocalObject(nid); }

        static BotEvidence botEvidence;
        [DllImport("user32.dll", EntryPoint = "IsIconic")] static extern bool BotIsIconic(IntPtr h);
        static readonly CultureInfo BotIc = CultureInfo.InvariantCulture;

        static Faction BotOwnFaction() { User u = User.LocalUser; return u == null ? null : u.faction; }
        static bool BotProjectAlive(BotGameWorld w) { return w.project != null && w.project.gameObject != null && !w.project.gameObject.IsNullOrReleased(); }

        // "OrangeWard" -> Blueprints/OrangeWard; "Factory/Name" as written
        static Factory.AssetKey BotBpKey(string word)
        {
            int i = word.IndexOf('/');
            return i > 0 ? new Factory.AssetKey(word.Substring(0, i), word.Substring(i + 1)) : new Factory.AssetKey("Blueprints", word);
        }
        // a key word matches by name (any factory) or by Factory/Name
        static bool BotKeyIs(Factory.AssetKey k, string word)
        {
            if (k == null || string.IsNullOrEmpty(word)) return false;
            int i = word.IndexOf('/');
            if (i > 0) return string.Equals(k.Factory, word.Substring(0, i), StringComparison.OrdinalIgnoreCase) && string.Equals(k.Name, word.Substring(i + 1), StringComparison.OrdinalIgnoreCase);
            return string.Equals(k.Name, word, StringComparison.OrdinalIgnoreCase);
        }
        static string BotStationTag(string w)
        {
            switch (w.ToLowerInvariant())
            {
                case "altar": return "Altar"; case "workbench": return "Workbench"; case "forge": return "Forge"; case "machineshop": return "MachineShop";
                case "lab": return "Lab"; case "loom": return "Loom"; case "furnace": return "Furnace"; case "choppingblock": return "ChoppingBlock";
            }
            return w;
        }
        // resource words; the crafted orange ward lives in crates and bags with the Stick identity (OrangeWardRecipe.cs), the red ward as SentinelWardAdj
        static Ressource BotResource(string name)
        {
            string n = name;
            if (string.Equals(n, "OrangeWard", StringComparison.OrdinalIgnoreCase)) n = "Stick";
            else if (string.Equals(n, "SentinelWard", StringComparison.OrdinalIgnoreCase)) n = "SentinelWardAdj";
            try { return Adjectif.StringToAdjectif(n) as Ressource; } catch (Exception) { return null; }
        }

        static List<Labor> BotWorkersMatching(string selector)
        {
            if (string.Equals(selector, "anyone", StringComparison.OrdinalIgnoreCase))
            {   // every faction (a guest checks what the HOST's workers hold): sorted by netId, the same order on host and guest
                List<Labor> every = new List<Labor>();
                foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>()) if (l != null && l.gameObject.activeInHierarchy) every.Add(l);
                every.Sort(delegate(Labor x, Labor y) { return BotWorkers.NetIdOf(x.gameObject).CompareTo(BotWorkers.NetIdOf(y.gameObject)); });
                return every;
            }
            List<Labor> all = BotWorkers.OwnWorkers(false), res = new List<Labor>();
            for (int i = 0; i < all.Count; i++) if (BotRules.SelectorMatches(selector, i, BotWorkers.DisplayName(all[i]))) res.Add(all[i]);
            return res;
        }
        static string BotCarryText(Labor l) { Factory.AssetKey k; BotWorkers.HeldProp(l, out k); return BotWorkers.BagText(l) + " held=" + (k == null ? "none" : k.ToString()); }
        // a resource word is compared by type ("Iron" is not RawIron); any other word by the bag's item keys and the held model key
        static bool BotCarries(Labor l, string item)
        {
            Ressource r = BotResource(item);
            if (r != null) { try { return l.recepteur != null && l.recepteur.ContentDescription.Value(r) > 0; } catch (Exception) { return false; } }
            return BotCarryText(l).IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        static bool BotHolds(Labor l, string model, out string what)
        {
            Factory.AssetKey k; GameObject g = BotWorkers.HeldProp(l, out k);
            what = k == null ? "none" : k.ToString();
            if (string.Equals(model, "none", StringComparison.OrdinalIgnoreCase)) return k == null;
            return (k != null && k.ToString().IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0) || (g != null && g.name.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // own built things: wards (WardController) and crafting stations, nearest to the anchor first
        static List<GameObject> BotOwnStructures(string key)
        {
            List<GameObject> res = new List<GameObject>(); Faction f = BotOwnFaction(); if (f == null) return res;
            foreach (WardController w in UnityEngine.Object.FindObjectsOfType<WardController>())
            {
                if (w == null || w.faction != f || w.gameObject.IsNullOrReleased()) continue;
                FactoryImprint fi = w.GetComponentInParent<FactoryImprint>();
                if (fi != null && BotKeyIs(fi.AssetKey, key)) res.Add(w.gameObject);
            }
            foreach (CraftingStation s in UnityEngine.Object.FindObjectsOfType<CraftingStation>())
            {
                if (s == null || s.faction != f || s.IsNullOrReleased()) continue;
                FactoryImprint fi = s.GetComponentInParent<FactoryImprint>();
                if (string.Equals(s.PieMenuTag, key, StringComparison.OrdinalIgnoreCase) || (fi != null && BotKeyIs(fi.AssetKey, key))) res.Add(s.gameObject);
            }
            if (string.Equals(key, "Pallet", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "Palette", StringComparison.OrdinalIgnoreCase))
                foreach (Recepteur c in BotOwnCrates()) res.Add(c.gameObject);   // 0.4.12: a crate (Pallet) counts as a structure (instant check, structure Pallet)
            Vector3 an = VpAnchor();
            res.Sort(delegate(GameObject x, GameObject y) { return (x.transform.position - an).sqrMagnitude.CompareTo((y.transform.position - an).sqrMagnitude); });
            return res;
        }
        static Blueprint BotFirstBlueprint(string key, bool anti)
        {
            foreach (Blueprint b in BotBlueprint.OwnBlueprints(false)) if (b.IsAnti == anti && BotKeyIs(b.AssetKey, key)) return b;
            return null;
        }
        static string BotHue(Color c) { string h = BotBlueprint.HueName(c); return h == "grey" ? "gray" : h == "purple" ? "other" : h; }

        static int BotGuestCount()
        {
            int n = 0;
            try { if (NetworkServer.active) foreach (NetworkConnection c in NetworkServer.connections) if (c != null && c.connectionId > 0) n++; } catch (Exception) { }
            try { Brix.Network.UNetManager m = Brix.Network.UNetManager.instance; if (m != null && m.LobbyPlayerList != null && NetworkServer.active && m.LobbyPlayerList.Count - 1 > n) n = m.LobbyPlayerList.Count - 1; } catch (Exception) { }
            return n;
        }
        static string BotState()
        {
            switch (BotMenu.Current())
            {
                case BotMenu.Phase.MainMenu: return "menu";
                case BotMenu.Phase.Lobby: return "lobby";
                case BotMenu.Phase.InMatch: return "match";
            }
            return "loading";
        }
        bool BotWindowMinimized() { try { IntPtr h = GameWindow(); return h != IntPtr.Zero && BotIsIconic(h); } catch (Exception) { return false; } }

        // ---------------------------------------------------------------- actions
        BotOutcome BotDoAction(BotStep s, BotGameWorld w)
        {
            List<string> a = s.Args; int n; float f;
            switch (s.Name)
            {
                case "craft":
                {
                    string tag = BotStationTag(a[0]); CraftingStation st = BotCraft.FindOwnStation(tag);
                    if (st == null) return BotOutcome.Fail("no own built " + tag);
                    string err; List<int> ids = BotCraft.ResolveRecipe(a[1], out err);
                    if (err != null) return BotOutcome.Fail(err);
                    if (ids.Count == 0) return BotOutcome.Fail("recipe " + a[1] + " resolves to nothing");
                    if (ids.Contains(OrangeWardRecipe.RecipeId)) { string why; if (!BotCraft.OrangeRecipeReady(out why)) return BotOutcome.Fail("orange recipe not ready on this PC: " + why); }
                    int count = 1; if (a.Count == 3) BotRules.TryInt(a[2], out count);
                    int before = st.QueueCount;
                    string q = BotCraft.Queue(st, ids, count);
                    if (q != null && !q.StartsWith("queue full", StringComparison.Ordinal)) return BotOutcome.Fail(q);
                    StringBuilder idText = new StringBuilder(); foreach (int id in ids) { if (idText.Length > 0) idText.Append(','); idText.Append(id); }
                    return BotOutcome.Ok("sent " + count + " x [" + idText + "] to " + tag + "; queue " + before + " -> " + st.QueueCount + (NetworkServer.active ? "" : " (a guest sees new entries after the host's update)") + (q == null ? "" : "; " + q));
                }
                case "fillstock":   // HOST TEST HELPER: empty own crates filled to capacity
                {
                    if (!NetworkServer.active) return BotOutcome.Fail("host only: crates are filled on the server");
                    Ressource r = BotResource(a[0]); if (r == null) return BotOutcome.Fail("unknown resource " + a[0]);
                    BotRules.TryInt(a[1], out n); Faction own = BotOwnFaction();
                    int done = BotCraft.FillEmptyCrates(own, r, n);
                    return done > 0 ? BotOutcome.Ok("HOST TEST HELPER: filled " + done + " empty own crate(s) with " + r.GetType().Name + " to capacity; in crates now " + BotCraft.CountInCrates(own, r))
                                    : BotOutcome.Fail("no empty own crate");
                }
                case "stock":       // HOST TEST HELPER: an exact amount into empty crates (own faction, or 'other' = the other player's)
                {
                    if (!NetworkServer.active) return BotOutcome.Fail("host only: crates are filled on the server");
                    Ressource r = BotResource(a[0]); if (r == null) return BotOutcome.Fail("unknown resource " + a[0]);
                    BotRules.TryInt(a[1], out n);
                    if (a.Count == 3 && a[2].ToLowerInvariant() == "altar")
                    {   // straight into the own Altar's ingredient store (same three calls as Recepteur.WipeAndFill, up to the free capacity): no crate needed
                        CraftingStation al = BotCraft.FindOwnStation("Altar"); if (al == null || al.recepteur == null) return BotOutcome.Fail("no own built Altar");
                        Factory.AssetKey rk = Description.RepresentativeOf(r); if (rk == null) return BotOutcome.Fail("no item model for " + a[0]);
                        // 0.4.12: the Altar's capacity is "Ressource:40" for ANY resource and Description.Value looks up the exact type, so Value(OrangeCrystal)
                        // was 0 (live 0.4.11 runs). What the queued recipe still needs = StuffToFetch() (the station's own fetch list), else the per-resource cap.
                        Description need = null; try { need = al.StuffToFetch(); } catch (Exception) { }
                        int cap = al.recepteur.BaseCapacity.Value(Adjectif.ressource); if (cap <= 0) cap = 40;
                        int room = need != null ? need.Value(r) : cap - al.recepteur.ContentDescription.Value(r), put = Math.Min(n, Math.Max(0, room));
                        for (int i = 0; i < put; i++) { GameObject go = Brix.Lifecycle.Pooling.ObjectPoolSingleton.Request_Unsafe(rk, true); al.recepteur.AddObject(go); NetworkServer.Spawn(go, rk.AssetId); }
                        string held = " (Altar holds " + al.recepteur.ContentDescription.Value(r) + ", cap " + cap + (need != null ? ", the queued recipe still needed " + room : ", nothing queued") + ")";
                        return put >= n ? BotOutcome.Ok("HOST TEST HELPER: put " + put + " " + r.GetType().Name + " into the own Altar" + held) : BotOutcome.Fail("the Altar had room for " + room + " of " + n + held);
                    }
                    Faction fac = a.Count == 3 ? BotCraft.OtherPlayerFaction() : BotOwnFaction();
                    if (fac == null) return BotOutcome.Fail(a.Count == 3 ? "no other player in the match" : "no local faction");
                    int left = n, crates = 0;
                    foreach (StorageLocator loc in UnityEngine.Object.FindObjectsOfType<StorageLocator>())
                    {
                        if (left <= 0) break;
                        if (loc == null || loc.Storage == null) continue;
                        foreach (Recepteur c in loc.Storage.GetRecepteurs())
                        {
                            if (left <= 0) break;
                            if (c == null || !BotCraft.CrateOwnedBy(loc, c, fac) || !c.IsEmpty()) continue;
                            int put = BotCraft.FillExact(c, r, left);
                            if (put > 0) { left -= put; crates++; }
                        }
                    }
                    string total = " (in crates now " + BotCraft.CountInCrates(fac, r) + ")";
                    return left <= 0 ? BotOutcome.Ok("HOST TEST HELPER: put " + n + " " + r.GetType().Name + " into " + crates + " empty crate(s) of faction " + fac.ID + total)
                                     : BotOutcome.Fail("put only " + (n - left) + " of " + n + ": not enough empty crates" + total);
                }
                case "place": case "placerel": return BotPlace(s, w);
                case "demolish": return BotDemolish(s, w);
                case "assign": return BotAssign(s, w);
                case "pickup": return BotPickup(s, w);
                case "harvest": return BotHarvest(s, w);
                case "unstump": return BotUnstump(s, w);
                case "autonomy": return BotAutonomy(s, w);
                case "team": case "faction": return BotTeamFaction(s, w);      // 0.4.18 (BotDoors.cs)
                case "demolishdoor": return BotDemolishDoor(s, w);
                case "attackdoor": return BotAttackDoor(s, w);
                case "firedoor": return BotFireDoor(s, w);
                case "grid": return BotGridAction(s, w);       // 0.4.18 (BotGrid.cs)
                case "ungrid": return BotUngridAction(s, w);
                case "join": return BotJoin(s, w);
                case "ready": case "unready":
                {   // 0.4.11: the lobby checkbox's own guard, retried until the game allows it (just joined, map downloading); the old 'ready'
                    // answered OK even when the game refused ("can set ready: False")
                    bool on = s.Name == "ready";
                    w.pending = delegate(float el)
                    {
                        string e = BotMenu.SetReady(on);
                        return e == null ? BotOutcome.Ok(s.Name + " sent | " + BotMenu.DescribeLobby()) : BotOutcome.Wait(e + " | " + BotMenu.DescribeLobby());
                    };
                    return w.pending(0f);
                }
                case "deliver": return BotDeliver(s, w);
                case "save": return BotSaveLoad.Save(a[0].Replace("{run}", w.Run));
                case "load": return BotLoad(a[0].Replace("{run}", w.Run), w);
                case "leavematch":
                {
                    string e = BotMenu.QuitMatchToMenu(); if (e != null) return BotOutcome.Fail(e);
                    w.pending = delegate(float el) { BotMenu.Phase p = BotMenu.Current(); return p == BotMenu.Phase.MainMenu ? BotOutcome.Ok("back in the main menu after " + el.ToString("F1", BotIc) + " s") : BotOutcome.Wait("phase " + p); };
                    return BotOutcome.Wait("leaving the match");
                }
                case "leavelobby":
                {
                    if (BotMenu.Current() != BotMenu.Phase.Lobby) return BotOutcome.Fail("not in a lobby (phase " + BotMenu.Current() + ")");
                    BotMenu.LeaveLobby();
                    w.pending = delegate(float el) { BotMenu.Phase p = BotMenu.Current(); return p == BotMenu.Phase.MainMenu ? BotOutcome.Ok("back in the main menu") : BotOutcome.Wait("phase " + p); };
                    return BotOutcome.Wait("leaving the lobby");
                }
                case "hostlobby": return BotHostLobby(s, w);
                case "hoststart":
                {
                    bool pressed = false;
                    w.pending = delegate(float el)
                    {
                        BotMenu.Phase p = BotMenu.Current();
                        if (p == BotMenu.Phase.Loading || p == BotMenu.Phase.InMatch) return BotOutcome.Ok("match starting (phase " + p + ")");
                        if (pressed) return BotOutcome.Wait("start pressed, phase " + p);
                        string b = BotMenu.HostStartBlocker(); if (b != null) return BotOutcome.Wait(b);
                        string e = BotMenu.HostStart(true); if (e != null) return BotOutcome.Wait(e);
                        pressed = true; return BotOutcome.Wait("start pressed (the lobby Start button)");
                    };
                    return w.pending(0f);
                }
                case "sandbox":   // single player match: map + mode (default sandbox), the same calls as Single player > map > Play
                {
                    string mode = a.Count == 2 ? a[1] : "sandbox";
                    string e = BotStartSinglePlayer(a[0], mode); if (e != null) return BotOutcome.Fail(e);
                    w.pending = delegate(float el) { BotMenu.Phase p = BotMenu.Current(); return p == BotMenu.Phase.InMatch ? BotOutcome.Ok("single player " + mode + " on " + a[0] + " running after " + el.ToString("F0", BotIc) + " s") : BotOutcome.Wait("phase " + p); };
                    return BotOutcome.Wait("starting single player " + mode + " on " + a[0]);
                }
                case "closeintro": BotMenu.CloseIntroModal(); return BotOutcome.Ok("intro modal close requested (the button's own hook)");
                case "debug":
                {
                    if (a[0] == "instant" && !NetworkServer.active) return BotOutcome.Fail("debug instant: host / single player only (editor mode changes the picker and building of this PC)");
                    if (w.editorSaved < 0) { w.editorSaved = Brix.Engine.Architecte.editorMode ? 1 : 0; w.debugMenuSaved = Brix.Engine.Architecte.allowDebugMenu ? 1 : 0; }
                    string e = BotMenu.EnableDebugTools(a[0] == "instant");
                    return e == null ? BotOutcome.Ok("debug menu allowed" + (a[0] == "instant" ? " + instant build (editorMode; host decides in a match)" : "")) : BotOutcome.Fail(e);
                }
                case "camera": return BotCameraAction(s, w);
                case "shot": return BotShot(s, w);
                case "mute":
                {
                    bool on = a[0] == "on";
                    if (w.muteSaved < 0) w.muteSaved = Brix.Audio.SoundEngine.mute ? 1 : 0;
                    Brix.Audio.SoundEngine.mute = on;
                    return BotOutcome.Ok("game sound " + (on ? "muted" : "on") + " (set back to " + (w.muteSaved == 1 ? "muted" : "on") + " when the run ends)");
                }
                case "dump": return BotDump(a[0].ToLowerInvariant(), w);
            }
            f = 0f;
            return BotOutcome.Fail("action not implemented: " + s.Name + (f > 0f ? "" : ""));
        }

        // build task of this run: created once with the task panel's own command (UNetGroupe.CmdCreate), then reused
        sealed class BotProjectWaiter
        {
            HashSet<int> before;
            internal BotOutcome Step(BotGameWorld w)   // null = the task exists
            {
                if (BotProjectAlive(w)) return null;
                if (before == null)
                {
                    before = BotBlueprint.SnapshotProjects();
                    string e = BotBlueprint.RequestNewProject(); if (e != null) return BotOutcome.Fail(e);
                    return BotOutcome.Wait("creating a build task");
                }
                BuildGoalProvider p = BotBlueprint.NewProjectSince(before);
                if (p == null) return BotOutcome.Wait("waiting for the new build task");
                w.project = p; return null;
            }
        }

        BotOutcome BotPlace(BotStep s, BotGameWorld w)
        {
            string why = BotBlueprint.WhyCannotBuild(); if (why != null) return BotOutcome.Fail(why);
            List<string> a = new List<string>(s.Args); bool instant = a.Count > 0 && a[a.Count - 1].ToLowerInvariant() == "instant";
            if (instant) a.RemoveAt(a.Count - 1);
            if (instant && !NetworkServer.active) return BotOutcome.Fail("instant: host / single player only (the host would build a guest's blueprint at once without asking)");
            Factory.AssetKey key = BotBpKey(a[0]); float x, z, deg = 0f, placedAt = 0f;
            if (s.Name == "placerel")
            {
                Vector3 an = VpAnchor(); if (an == Vector3.zero) return BotOutcome.Fail("no own workers for the anchor");
                float dx, dz; BotRules.TryFloat(a[1], out dx); BotRules.TryFloat(a[2], out dz); x = an.x + dx; z = an.z + dz;
                if (a.Count == 5) BotRules.TryFloat(a[4], out deg);
            }
            else { BotRules.TryFloat(a[1], out x); BotRules.TryFloat(a[3], out z); if (a.Count == 6) BotRules.TryFloat(a[5], out deg); }
            int turns = (((int)Math.Round(deg / 90f)) % 4 + 4) % 4;
            BotProjectWaiter pw = new BotProjectWaiter(); int phase = 0; Vector3 pos = Vector3.zero; Factory.AssetKey sent = key;
            w.pending = delegate(float el)
            {
                if (phase == 0) { BotOutcome o = pw.Step(w); if (o != null) return o; phase = 1; }
                if (phase == 1)
                {
                    Quaternion rot = Quaternion.identity; ValidationResult res; XYZ voxel; string info = null, firstInfo = null; bool okp = false; int tried = 0;
                    // the asked spot first, then a spiral (8 directions, every 2 cells up to 12): an unattended run should not stop on one tree or rock
                    float[] radii = { 0f, 2f, 4f, 6f, 8f, 10f, 12f };
                    foreach (float rad in radii)
                    {
                        int steps = rad == 0f ? 1 : 8;
                        for (int k = 0; k < steps && !okp; k++)
                        {
                            float ang = k * Mathf.PI / 4f; tried++;
                            if (BotBlueprint.ComputePlacement(key, x + rad * Mathf.Cos(ang), z + rad * Mathf.Sin(ang), turns, out sent, out pos, out rot, out res, out voxel, out info)) okp = true;
                            else if (firstInfo == null) firstInfo = info;
                        }
                        if (okp) break;
                    }
                    if (!okp) return BotOutcome.Fail("cannot place near " + x.ToString("F0", BotIc) + "," + z.ToString("F0", BotIc) + " (" + tried + " spots up to 12 cells): " + firstInfo);
                    if (tried > 1) info = "spot #" + tried + " of the spiral; " + info;
                    BotBlueprint.SendPlace(sent, pos, rot, w.project, instant);
                    if (instant) { phase = 9; placedAt = el; return BotOutcome.Wait("TEST HELPER instant placement sent (" + info + ")"); }
                    phase = 2;
                    return BotOutcome.Wait("placed (" + info + "), waiting for the blueprint");
                }
                if (phase == 9)
                {   // proof: an own ward / crafting station of that kind stands where it was placed (other kinds: reported unverified after 5 s)
                    foreach (GameObject g in BotOwnStructures(sent.Name))
                        if ((g.transform.position - pos).sqrMagnitude < 16f) return BotOutcome.Ok("TEST HELPER: " + sent + " built at once at " + CsV(pos) + " -> " + g.name + " (no materials, not a player action)");
                    if (el - placedAt < 5f) return BotOutcome.Wait("instant placement: waiting for the built " + sent.Name);
                    return BotOutcome.Ok("TEST HELPER: " + sent + " sent with instant at " + CsV(pos) + "; NOT verified (only wards and crafting stations are found by the bot)");
                }
                Blueprint b = BotBlueprint.BlueprintNear(pos, sent, false, 1.5f);
                if (phase == 2) { if (b == null) return BotOutcome.Wait("blueprint not here yet"); BotBlueprint.SendCommit(w.project); phase = 3; return BotOutcome.Wait("committing (the task panel OK)"); }
                if (b == null) return BotOutcome.Fail("the blueprint vanished after the commit");
                if (!b.commited) return BotOutcome.Wait("waiting for the commit");
                return BotOutcome.Ok(BotBlueprint.Describe(b) + " | task " + BotBlueprint.ProjectInfo(w.project));
            };
            return w.pending(0f);
        }

        BotOutcome BotDemolish(BotStep s, BotGameWorld w)
        {
            string why = BotBlueprint.WhyCannotBuild(); if (why != null) return BotOutcome.Fail(why);
            int idx = 0; if (s.Args.Count == 2) BotRules.TryInt(s.Args[1], out idx);
            List<GameObject> targets = BotOwnStructures(s.Args[0]);
            if (idx >= targets.Count) return BotOutcome.Fail("own structure " + s.Args[0] + " #" + idx + " not found (have " + targets.Count + ")");
            GameObject target = targets[idx]; Vector3 tpos = target.transform.position;
            BotProjectWaiter pw = new BotProjectWaiter(); int phase = 0;
            w.pending = delegate(float el)
            {
                if (phase == 0) { BotOutcome o = pw.Step(w); if (o != null) return o; phase = 1; }
                if (phase == 1) { string e = BotBlueprint.SendDemolish(target, w.project); if (e != null) return BotOutcome.Fail(e); phase = 2; return BotOutcome.Wait("demolish (anti) blueprint sent"); }
                Blueprint b = BotBlueprint.BlueprintNear(tpos, null, true, 3f);
                if (phase == 2) { if (b == null) return BotOutcome.Wait("anti blueprint not here yet"); BotBlueprint.SendCommit(w.project); phase = 3; return BotOutcome.Wait("committing the demolish order"); }
                if (b == null) return BotOutcome.Ok("anti blueprint already finished (structure " + (target == null || !target.activeInHierarchy ? "gone" : "still there") + ")");
                if (!b.commited) return BotOutcome.Wait("waiting for the commit");
                return BotOutcome.Ok(BotBlueprint.Describe(b) + " | " + BotBlueprint.Hologram(b));
            };
            return w.pending(0f);
        }

        BotOutcome BotAssign(BotStep s, BotGameWorld w)
        {
            List<string> a = s.Args; string task = a[1].ToLowerInvariant(); bool all = a[0].ToLowerInvariant() == "all"; int n = 0;
            if (!all) BotRules.TryInt(a[0], out n);
            List<Labor> builders = BotCraft.OwnBuilders();
            if (builders.Count == 0) return BotOutcome.Fail("no own builders");
            List<string> names = new List<string>();
            if (task == "craft")
            {
                string tag = BotStationTag(a[2]); CraftingStation st = BotCraft.FindOwnStation(tag);
                if (st == null) return BotOutcome.Fail("no own built " + tag);
                foreach (Labor l in builders) { if (!all && names.Count >= n) break; BotCraft.AssignWorker(st, l); names.Add(BotWorkers.DisplayName(l)); }
                return BotOutcome.Ok("assigned to " + tag + ": " + string.Join(", ", names.ToArray()) + " (one operator per station: each new one replaces the previous)");
            }
            Project proj = BotProjectAlive(w) ? w.project.GetComponent<Project>() : null;
            if (task == "idle")
            {
                foreach (Labor l in builders) { if (!all && names.Count >= n) break; if (proj != null && l.Project != proj) continue; BotWorkers.Unassign(l); names.Add(BotWorkers.DisplayName(l)); }
                return names.Count > 0 ? BotOutcome.Ok("free agents again: " + string.Join(", ", names.ToArray())) : BotOutcome.Fail("nobody to release");
            }
            if (proj == null) return BotOutcome.Fail("no build task of this run yet: place a blueprint first");
            List<Labor> pick = new List<Labor>();
            foreach (Labor l in builders) if (l.Project != proj && l.recepteur != null && l.recepteur.IsEmpty()) pick.Add(l);   // empty bags first
            foreach (Labor l in builders) if (l.Project != proj && !pick.Contains(l)) pick.Add(l);
            int want = all ? pick.Count : Math.Min(n, pick.Count);
            if (want == 0) return BotOutcome.Fail("every builder is in the task already");
            List<Labor> chosen = pick.GetRange(0, want);
            foreach (Labor l in chosen) { BotWorkers.AssignToProject(l, proj); names.Add(BotWorkers.DisplayName(l)); }
            int heldNow = 0;
            if (a.Count == 3)
                foreach (Labor l in builders)
                    if (!chosen.Contains(l) && l.Project != proj && !w.held.ContainsKey(l) && !BotIsOperator(l)) { BotHold(w, l); heldNow++; }   // a station operator keeps crafting
            return BotOutcome.Ok("assigned " + string.Join(", ", names.ToArray()) + " to the bot task " + BotBlueprint.ProjectInfo(w.project) + (a.Count == 3 ? "; " + heldNow + " other builder(s) hold position until the run ends" : ""));
        }

        // Action wheel 'Take from' (DirectOrderProject.PickUp -> CmdPickup): the chosen own worker walks to an own container that holds the item (crate
        // first, else a crafting station: a finished product waits on the Altar when no crate has room) and takes it. It then carries it until told
        // otherwise, so the carry lasts as long as a test needs (screenshots, a late join, a save). 'any' = the first own builder with an empty bag.
        BotOutcome BotPickup(BotStep s, BotGameWorld w)
        {
            string item = s.Args[1]; Ressource r = BotResource(item); if (r == null) return BotOutcome.Fail("unknown item " + item);
            Faction own = BotOwnFaction(); if (own == null) return BotOutcome.Fail("no local faction");
            GameObject src = null; Vector3 an = VpAnchor(); float best = float.MaxValue;
            foreach (StorageLocator loc in UnityEngine.Object.FindObjectsOfType<StorageLocator>())
            {
                if (loc == null || loc.Storage == null) continue;
                foreach (Recepteur c in loc.Storage.GetRecepteurs())
                {
                    if (c == null || !BotCraft.CrateOwnedBy(loc, c, own) || c.ContentDescription.Value(r) <= 0) continue;
                    float d = (c.transform.position - an).sqrMagnitude; if (d < best) { best = d; src = c.gameObject; }
                }
            }
            bool fromStation = false;
            if (src == null)
                foreach (CraftingStation st in UnityEngine.Object.FindObjectsOfType<CraftingStation>())
                    if (st != null && st.faction == own && st.recepteur != null && st.recepteur.ContentDescription.Value(r) > 0) { src = st.gameObject; fromStation = true; break; }
            if (src == null) return BotOutcome.Fail("no own crate or crafting station holds " + item);
            Labor worker = null;
            if (string.Equals(s.Args[0], "any", StringComparison.OrdinalIgnoreCase)) { foreach (Labor l in BotCraft.OwnBuilders()) if (l.recepteur != null && l.recepteur.IsEmpty() && !BotIsOperator(l)) { worker = l; break; } }
            else { List<Labor> m = BotWorkersMatching(s.Args[0]); if (m.Count > 0) worker = m[0]; }
            if (worker == null) return BotOutcome.Fail("no matching own worker" + (s.Args[0] == "any" ? " with an empty bag (station operators excluded)" : ""));
            string bagNote = worker.recepteur != null && !worker.recepteur.IsEmpty() ? " (bag not empty: it may not fit)" : "";
            BotHold(w, worker);   // hold position first: after the order a free agent would carry the load off to a crate 3 s later
            if (!BotWorkers.PickUpFrom(worker, src)) return BotOutcome.Fail("pick-up order not sent (no UNetProjectCmd)");
            w.carrier = worker;
            string who = BotWorkers.DisplayName(worker), from = src.name + (fromStation ? " (a crafting station: the worker takes as much as fits, ingredients too)" : "") + bagNote;
            w.pending = delegate(float el)
            {
                if (worker == null || worker.gameObject == null) return BotOutcome.Fail("the worker is gone");
                if (BotCarries(worker, item)) return BotOutcome.Ok(who + " took " + item + " from " + from + ": " + BotWorkers.WorkerLine(worker));
                return BotOutcome.Wait(who + " on the way to " + from);
            };
            return BotOutcome.Wait(who + " sent to take " + item + " from " + from);
        }

        // Action wheel 'Drop load into' (DirectOrderProject.AddGoDropInto -> CmdGoStoreInto) with the nearest own blueprint as the target: the worker
        // stores its load there; a full ward blueprint turns into the real ward on the host (Blueprint.OnReceivedNewItem -> MorphToObject).
        BotOutcome BotDeliver(BotStep s, BotGameWorld w)
        {
            string itemWord = s.Args[1].IndexOf('/') > 0 ? s.Args[1].Substring(s.Args[1].IndexOf('/') + 1) : s.Args[1];
            Labor worker = null;
            if (string.Equals(s.Args[0], "any", StringComparison.OrdinalIgnoreCase))
            {   // the worker of the last pickup first, else an own worker that carries what this blueprint is named after
                if (w.carrier != null && w.carrier.gameObject != null && BotCarries(w.carrier, itemWord)) worker = w.carrier;
                else foreach (Labor l in BotWorkers.OwnWorkers(false)) if (BotCarries(l, itemWord)) { worker = l; break; }
            }
            else { List<Labor> m = BotWorkersMatching(s.Args[0]); if (m.Count > 0) worker = m[0]; }
            if (worker == null) return BotOutcome.Fail("no matching own worker carries " + itemWord + " (use pickup first)");
            Blueprint bp = null; float best = float.MaxValue;
            foreach (Blueprint b in BotBlueprint.OwnBlueprints(false))
            {
                if (b.IsAnti || !BotKeyIs(b.AssetKey, s.Args[1])) continue;
                float d = (b.transform.position - worker.transform.position).sqrMagnitude; if (d < best) { best = d; bp = b; }
            }
            if (bp == null) return BotOutcome.Fail("no own blueprint " + s.Args[1]);
            uint bpNet = BotWorkers.NetIdOf(bp.gameObject); GameObject bpGo = bp.gameObject;
            if (bpNet == 0 || BotNetFind(bpNet) == null) return BotOutcome.Fail("the blueprint has no network object on this PC yet");
            int storedBefore = bp.recepteur == null ? 0 : bp.recepteur.StoredItems.Count;
            if (!BotWorkers.StoreInto(worker, bpGo)) return BotOutcome.Fail("store order not sent (no UNetProjectCmd)");
            string who = BotWorkers.DisplayName(worker);
            w.pending = delegate(float el)
            {   // done only when THIS blueprint is gone (built) or holds more items than before; an empty bag alone proves nothing
                GameObject cur = BotNetFind(bpNet);
                if (cur == null || !cur.activeInHierarchy) return BotOutcome.Ok("blueprint " + s.Args[1] + " got the load and is gone (built); " + BotWorkers.WorkerLine(worker));
                Blueprint cb = cur.GetComponent<Blueprint>();
                int stored = cb == null || cb.recepteur == null ? 0 : cb.recepteur.StoredItems.Count;
                if (stored > storedBefore) return BotOutcome.Ok("load stored into the blueprint (" + storedBefore + " -> " + stored + " item(s)): " + BotWorkers.BlueprintLine(cur));
                return BotOutcome.Wait(who + " carrying to the blueprint");
            };
            return BotOutcome.Wait(who + " sent to the blueprint " + s.Args[1]);
        }

        // 0.4.11 join: the same calls as the F8 buttons ('join the selected friend' / 'I have a host code'): peer = host, StartBridge(false).
        //   join            the host code of this run (the host bot writes bot\sync\<run>\host-code.txt), else the ONE friend in Castle Story
        //   join code       only the host code of this run
        //   join <steamid>  that host (F8 'host code' = the host's SteamID)
        //   join name=<t>   the friend in Castle Story whose Steam name contains <t>
        BotOutcome BotJoin(BotStep s, BotGameWorld w)
        {
            // a guest that left its match keeps its bridge (only a host stops it, SteamBridgePlugin.cs:345): stop it like the F8 'stop the bridge' button
            if (bridge != null && !hosting && !NetworkClient.active && !NetworkServer.active) { StopBridge(); PtLog("BOT join: stopped the old guest bridge first (left the match before)"); }
            if (bridge != null || hosting) return BotOutcome.Fail("the bridge is already running (hosting=" + hosting + ")");
            string arg = s.Args.Count == 1 ? s.Args[0] : null; ulong id = 0; string how = null;
            string codeFile = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(BotRoot, "sync"), w.Run), "host-code.txt");
            if (arg == null || arg.ToLowerInvariant() == "code")
            {
                try { if (File.Exists(codeFile)) ulong.TryParse(File.ReadAllText(codeFile).Trim(), NumberStyles.Integer, BotIc, out id); } catch (Exception) { }
                if (id != 0) how = "the host code of this run";
                else if (arg != null) return BotOutcome.Fail("no host code in " + codeFile + " (the host's bot writes it when its run starts)");
            }
            if (id == 0 && arg != null && arg.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
            {
                string want = arg.Substring(5); RefreshFriends();
                foreach (CSteamID f in friends) if (SteamFriends.GetFriendPersonaName(f).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) { id = f.m_SteamID; how = "friend " + SteamFriends.GetFriendPersonaName(f); break; }
                if (id == 0) return BotOutcome.Fail("no friend in Castle Story named like '" + want + "' (in game: " + BotFriendNames() + ")");
            }
            if (id == 0 && arg != null && ulong.TryParse(arg, NumberStyles.Integer, BotIc, out id)) how = "Steam id";
            if (id == 0)
            {
                RefreshFriends();
                if (friends.Count != 1) return BotOutcome.Fail(friends.Count + " friend(s) in Castle Story (" + BotFriendNames() + "): use 'join code', 'join name=<text>' or 'join <steamid>'");
                id = friends[0].m_SteamID; how = "the one friend in Castle Story";
            }
            if (!LooksLikeSteamId(id)) return BotOutcome.Fail("not a Steam id: " + id);
            if (id == SteamUser.GetSteamID().m_SteamID) return BotOutcome.Fail("that is this PC's own id");
            peer = new CSteamID(id); peerByCode = !IsFriend(peer); StartBridge(false);
            return BotOutcome.Ok("join started to " + SteamFriends.GetFriendPersonaName(peer) + " " + id + " (" + how + (peerByCode ? ", not a friend: the host needs AllowStrangers" : "") + "; bridge " + (bridge != null) + ")");
        }
        string BotFriendNames()
        {
            StringBuilder b = new StringBuilder();
            foreach (CSteamID f in friends) { if (b.Length > 0) b.Append(", "); b.Append(SteamFriends.GetFriendPersonaName(f)); }
            return b.Length == 0 ? "nobody" : b.ToString();
        }

        BotOutcome BotLoad(string name, BotGameWorld w)
        {
            BotMenu.Phase ph = BotMenu.Current();
            if (ph == BotMenu.Phase.InMatch || ph == BotMenu.Phase.Loading) return BotOutcome.Fail("in a match: 'leavematch' first, then 'hostlobby' (host) or 'join' (guest), then 'load'");
            if (BotMenu.HostLobbyUp())
            {
                BotOutcome sel = BotSaveLoad.HostSelectSave(name); if (sel.Status == BotStatus.Failed) return sel;
                string codename = GameParam.Map == null ? null : GameParam.Map.codename; bool started = false; float rearm = 0f;
                w.pending = delegate(float el) { return BotSaveLoad.HostPollStart(ref started, ref rearm, w.now, codename, el); };
                return BotOutcome.Wait(sel.Detail);
            }
            if (NetworkClient.active && !NetworkServer.active)
            {
                float next = 0f; bool inMatch = false;
                w.pending = delegate(float el)
                {
                    if (!inMatch) { BotOutcome o = BotSaveLoad.GuestPollReady(ref next, w.now); if (o.Status != BotStatus.Done) return o; inMatch = true; }
                    return BotSaveLoad.PollInMatch(null, null, el);
                };
                return BotOutcome.Wait("guest: waiting for the host's save and ready-up");
            }
            BotOutcome st = BotSaveLoad.StartSinglePlayerLoad(name); if (st.Status == BotStatus.Failed) return st;
            string folder = BotSaveLoad.Folder(name);
            w.pending = delegate(float el) { return BotSaveLoad.PollInMatch(null, folder, el); };
            return st;
        }

        BotOutcome BotHostLobby(BotStep s, BotGameWorld w)
        {
            string map = s.Args[0], mode = s.Args.Count == 2 ? s.Args[1] : "conquest";
            int phase = BotMenu.HostLobbyUp() ? 1 : 0; float upAt = -1f;
            if (phase == 0) { string e = BotMenu.HostCreateLobby(); if (e != null && e != "fallback-started") return BotOutcome.Fail(e); }
            w.pending = delegate(float el)
            {
                if (phase == 0) { if (!BotMenu.HostLobbyUp()) return BotOutcome.Wait("creating the lobby"); phase = 1; }
                if (phase == 1)
                {
                    if (upAt < 0f) upAt = el;
                    if (el - upAt < 3f) return BotOutcome.Wait("lobby menu settling (it selects its own default map first)");
                    string e = BotMenu.HostSelectMap(map, mode, null); if (e != null) return BotOutcome.Fail(e + " (map '" + map + "', mode '" + mode + "')");
                    phase = 2; return BotOutcome.Wait("map selected");
                }
                return BotOutcome.Ok("lobby up: " + BotMenu.DescribeLobby());
            };
            return w.pending(0f);
        }

        // BotMenu.StartSandboxSp with the mode as a parameter (sandbox, conquest, ...): SelectAndLoad + GameParam.StartGame (SpNewGameStarterMenu 'Play')
        static string BotStartSinglePlayer(string mapKey, string modeCodename)
        {
            if (SceneManager.GetActiveScene().name != BotMenu.MenuScene || Brix.Network.UNetManager.instance == null || GameParam.instance == null) return "not in the main menu";
            if (NetworkServer.active || NetworkClient.active || Brix.Engine.Neo.multiplayer) return "network still active / multiplayer";
            Brix.Assets.Asset_Map map = BotMenu.FindMap(mapKey, Brix.Assets.AssetPlayableContext.Singleplayer);
            Brix.Assets.Asset_Gamemode mode = BotMenu.FindMode(modeCodename, Brix.Assets.AssetPlayableContext.Singleplayer);
            Brix.Assets.Asset_GamemodePreset preset = BotMenu.FindPreset(mode, null, Brix.Assets.AssetPlayableContext.Singleplayer);
            if (map == null) return "map '" + mapKey + "' not found for single player";
            if (mode == null || preset == null) return "mode '" + modeCodename + "' (or its default preset) not found for single player";
            if (!map.IsCurrentVersion()) return "map needs the update step";
            BotMenu.SelectAndLoad(map, mode, preset, Brix.Assets.AssetPlayableContext.Singleplayer, false);
            GameParam.StartGame(BaseMapLibraryMenu.EmptySelected);
            return null;
        }

        static string BotResolveTarget(List<string> a, int at, out Vector3 pos, out GameObject go)
        {
            pos = Vector3.zero; go = null;
            if (at >= a.Count) return "missing target";
            string k = a[at].ToLowerInvariant();
            switch (k)
            {
                case "anchor": pos = VpAnchor(); return pos == Vector3.zero ? "no own workers for the anchor" : null;
                case "altar": { CraftingStation st = BotCraft.FindOwnStation("Altar"); if (st == null) return "no own Altar"; go = st.gameObject; pos = go.transform.position; return null; }
                case "at": { float x, y, z; BotRules.TryFloat(a[at + 1], out x); BotRules.TryFloat(a[at + 2], out y); BotRules.TryFloat(a[at + 3], out z); pos = new Vector3(x, y, z); return null; }
                case "worker": { List<Labor> m = BotWorkersMatching(a[at + 1]); if (m.Count == 0) return "no own worker matches " + a[at + 1]; go = m[0].gameObject; pos = go.transform.position; return null; }
                case "carrier":   // own workers first, then anyone's (the guest's view of the host's carrier)
                    foreach (Labor l in BotWorkers.OwnWorkers(false)) if (BotCarries(l, a[at + 1])) { go = l.gameObject; pos = go.transform.position; return null; }
                    foreach (Labor l in BotWorkersMatching("anyone")) if (BotCarries(l, a[at + 1])) { go = l.gameObject; pos = go.transform.position; return null; }
                    return "nobody carries " + a[at + 1];
                case "blueprint": foreach (Blueprint b in BotBlueprint.OwnBlueprints(false)) if (BotKeyIs(b.AssetKey, a[at + 1])) { go = b.gameObject; pos = go.transform.position; return null; } return "no own blueprint " + a[at + 1];
                case "structure":
                {
                    int i = 0; if (at + 2 < a.Count) BotRules.TryInt(a[at + 2], out i);
                    List<GameObject> l = BotOwnStructures(a[at + 1]); if (i < 0 || i >= l.Count) return "no own structure " + a[at + 1] + " #" + i;
                    go = l[i]; pos = go.transform.position; return null;
                }
            }
            return "unknown target " + a[at];
        }

        BotOutcome BotCameraAction(BotStep s, BotGameWorld w)
        {
            CameraScript cs = BotCamera.Get(); if (cs == null) return BotOutcome.Fail("camera not ready (CameraScript starts at LevelReady)");
            if (s.Args.Count == 1 && s.Args[0].ToLowerInvariant() == "restore")
            {
                if (w.cam == null) return BotOutcome.Ok("nothing to restore");
                BotCamera.Restore(cs, w.cam); w.cam = null; return BotOutcome.Ok("camera restored: " + BotCamera.Describe(cs));
            }
            Vector3 p; GameObject go; string err = BotResolveTarget(s.Args, 0, out p, out go); if (err != null) return BotOutcome.Fail(err);
            BotSaveCam(w, cs);
            if (go != null) BotCamera.Focus(cs, go.transform, 12f); else BotCamera.LookAt(cs, p, 12f);
            return BotOutcome.Ok("camera on " + CsV(p) + ": " + BotCamera.Describe(cs));
        }

        BotOutcome BotShot(BotStep s, BotGameWorld w)
        {
            if (BotWindowMinimized()) return BotOutcome.Fail("the game window is minimized: nothing is rendered (keep it open when shots are wanted)");
            string name = s.Args[0]; int box = 60; Vector3 tp = Vector3.zero; GameObject tgo = null; bool hasTarget = false;
            for (int i = 1; i < s.Args.Count; i++)
            {
                string k = s.Args[i].ToLowerInvariant();
                if (k == "box") { BotRules.TryInt(s.Args[i + 1], out box); i++; continue; }
                if (k == "focus")
                {
                    int used; BotScript.ParseTarget(s.Args, i + 1, out used);
                    string err = BotResolveTarget(s.Args.GetRange(i + 1, used), 0, out tp, out tgo); if (err != null) return BotOutcome.Fail("focus: " + err);
                    hasTarget = true; i += used; continue;
                }
            }
            CameraScript cs = BotCamera.Get();
            if (hasTarget && cs != null) { BotSaveCam(w, cs); if (tgo != null) BotCamera.Focus(cs, tgo.transform, 12f); else BotCamera.LookAt(cs, tp, 12f); }
            if (botEvidence == null) botEvidence = gameObject.AddComponent<BotEvidence>();
            string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "bot-shots");
            try { Directory.CreateDirectory(dir); } catch (Exception e) { return BotOutcome.Fail("cannot create " + dir + ": " + e.Message); }
            string path = System.IO.Path.Combine(dir, w.Run + "-" + w.DeclaredRole + "-" + name + ".png");
            BotShotResult result = null; bool requested = false; float settle = hasTarget ? 1.5f : 0.2f; int half = Math.Max(4, box / 2);
            botShotHideGui = true;   // the bot's own status line stays out of the picture
            w.pending = delegate(float el)
            {
                if (!requested)
                {
                    if (el < settle) return BotOutcome.Wait("camera settling");
                    Vector3? target = null; Bounds? bounds = null;
                    if (hasTarget) { target = tgo != null ? tgo.transform.position : tp; Bounds bb; if (tgo != null && BotEvidence.WorldBounds(tgo, out bb)) bounds = bb; }
                    if (!botEvidence.Capture(path, target, bounds, half, half, delegate(BotShotResult r) { result = r; })) return BotOutcome.Wait("another capture is running");
                    requested = true; return BotOutcome.Wait("capturing at the end of the frame");
                }
                if (result == null) return BotOutcome.Wait("waiting for the end of the frame");
                botShotHideGui = false;
                if (!result.Ok) return BotOutcome.Fail("capture failed: " + result.Error);
                string stats = "";
                if (result.RegionRgb != null)
                {
                    BotColorStats inner = new BotColorStats(), ring = new BotColorStats();
                    for (int y = 0; y < result.RegionH; y++)
                        for (int x = 0; x < result.RegionW; x++)
                        {
                            int rgb = result.RegionRgb[y * result.RegionW + x], ax = result.RegionX + x, ay = result.RegionY + y;
                            bool inBox = ax >= result.BoxX0 && ax < result.BoxX1 && ay >= result.BoxY0 && ay < result.BoxY1;
                            (inBox ? inner : ring).Add((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
                        }
                    w.shots[name] = inner;
                    stats = " box(" + inner.Summary() + ") ring(" + ring.Summary() + ") verdict=" + BotColor.Verdict(inner, ring, 2.0, 2.0);
                }
                else if (hasTarget) stats = " target not on screen (" + result.TargetX.ToString("F0", BotIc) + "," + result.TargetY.ToString("F0", BotIc) + " depth " + result.TargetDepth.ToString("F1", BotIc) + ")";
                return BotOutcome.Ok("shot " + path + " " + result.Width + "x" + result.Height + " encode " + result.EncodeMs.ToString("F0", BotIc) + " ms" + stats);
            };
            return BotOutcome.Wait("shot requested");
        }

        BotOutcome BotDump(string what, BotGameWorld w)
        {
            List<string> lines = new List<string>(); bool all = what == "all"; Faction own = BotOwnFaction();
            try
            {
                if (all || what == "state") lines.Add("state phase=" + BotMenu.Current() + " net=" + BotNetRole() + " scene=" + SceneManager.GetActiveScene().name + " guests=" + BotGuestCount() + " sandboxed=" + BotInSandbox + " mod=" + BridgeVersion.Text + " lobby: " + BotMenu.DescribeLobby());
                if (all || what == "workers") foreach (Labor l in BotWorkers.OwnWorkers(false)) lines.Add("worker " + BotWorkers.WorkerLine(l));
                if (all || what == "blueprints") foreach (Blueprint b in BotBlueprint.OwnBlueprints(false)) lines.Add("blueprint " + BotBlueprint.Describe(b) + " | " + BotBlueprint.Hologram(b));
                if (all || what == "projects") foreach (BuildGoalProvider p in BotBlueprint.OwnBuildProjects()) lines.Add("task " + BotBlueprint.ProjectInfo(p) + (w.project == p ? " (bot task)" : ""));
                if (all || what == "structures") foreach (WardController x in UnityEngine.Object.FindObjectsOfType<WardController>()) lines.Add("ward " + BotBlueprint.WardInfo(x));
                if ((all || what == "stock") && own != null)
                    foreach (string rn in new string[] { "OrangeCrystal", "Iron", "RawIron", "Stick", "SentinelWardAdj", "WoodBlock", "StoneBlock" })
                    { Ressource r = BotResource(rn); if (r != null) lines.Add("stock " + rn + " crates=" + BotCraft.CountInCrates(own, r) + " carried=" + BotCraft.CountCarried(own, r)); }
                if (all || what == "altar") { CraftingStation al = BotCraft.FindOwnStation("Altar"); lines.Add(al == null ? "altar: none" : "altar " + BotCraft.Describe(al)); if (al != null) lines.Add(OrangeBot.PreviewReport(al)); }
                if (all || what == "orange") { lines.Add(OrangeBot.StartupCheck()); lines.Add(OrangeBot.HeldReport()); lines.Add(OrangeBot.HeldPoolReport()); lines.Add(OrangeBot.HologramReport()); lines.Add(OrangeBot.PlacedWardReport()); lines.Add(OrangeBot.CrateModelReport()); }
                if (all || what == "camera") { CameraScript cs = BotCamera.Get(); lines.Add(cs == null ? "camera not ready" : "camera " + BotCamera.Describe(cs)); }
                if (all || what == "lobby") lines.Add("lobby " + BotMenu.DescribeLobby() + " | " + BotMenu.DescribeTeams());   // 0.4.18
                if (all || what == "doors") lines.AddRange(BotDumpDoors());                                                         // 0.4.18
            }
            catch (Exception e) { lines.Add("dump stopped: " + e.GetType().Name + " " + e.Message); }
            foreach (string l in lines) w.Write("INFO  dump " + BotRules.Cut(l, 1500));
            return BotOutcome.Ok(lines.Count + " dump line(s) above");
        }

        // ---------------------------------------------------------------- state checks
        static BotEval BotCmp(BotCondition c, double actual, string what)
        {
            string d = what + "=" + actual.ToString(BotIc) + " (want " + c.Op + " " + c.Number.ToString(BotIc) + ")";
            return BotRules.Compare(actual, c.Op, c.Number) ? BotEval.Yes(d) : BotEval.No(d);
        }

        BotEval BotCheckCondition(BotCondition c, BotGameWorld w)
        {
            List<string> a = c.Args; Faction own = BotOwnFaction();
            switch (c.Kind)
            {
                case "state": { string st = BotState(); return st == a[0].ToLowerInvariant() ? BotEval.Yes("state " + st) : BotEval.No("state " + st + " (phase " + BotMenu.Current() + ")"); }
                case "scene": { string sc = SceneManager.GetActiveScene().name; return string.Equals(sc, a[0], StringComparison.OrdinalIgnoreCase) ? BotEval.Yes("scene " + sc) : BotEval.No("scene " + sc); }
                case "role":
                {
                    string want = a[0].ToLowerInvariant(), r = BotNetRole(); bool single = NetworkServer.active && !Brix.Engine.Neo.multiplayer;
                    string actual = r == "host" && single ? "single" : r;
                    return want == actual || (want == "host" && r == "host") ? BotEval.Yes("role " + actual) : BotEval.No("role " + actual);
                }
                case "guests": return BotCmp(c, BotGuestCount(), "guests");
                case "workers": return BotCmp(c, BotWorkers.OwnWorkers(false).Count, "own workers");
                case "crates": return BotCmp(c, BotOwnCrates().Count, "own crates");
                case "stumps": return BotCmp(c, BotStumpCountCached(w.now), "stumps within 20 cells of the workers");
                case "doors": return BotCmp(c, BotDoors(a[0]).Count, a[0].ToLowerInvariant() + " doors");   // 0.4.18
                case "items":
                {
                    Ressource r = BotResource(a[0]); if (r == null) return BotEval.Unable("unknown resource " + a[0]);
                    if (own == null) return BotEval.No("no local faction yet");
                    BotEval e = BotCmp(c, BotCraft.CountInCrates(own, r), a[0] + " in own crates"); e.Detail += ", carried " + BotCraft.CountCarried(own, r);
                    return e;
                }
                case "carry":
                {
                    List<Labor> m = BotWorkersMatching(a[0]);
                    foreach (Labor l in m) if (BotCarries(l, a[1])) return BotEval.Yes(BotWorkers.WorkerLine(l));
                    return BotEval.No("nobody of " + m.Count + " matching worker(s) carries " + a[1] + (m.Count > 0 ? "; first: " + BotWorkers.DisplayName(m[0]) + " " + BotCarryText(m[0]) : ""));
                }
                case "held":
                {
                    List<Labor> m = BotWorkersMatching(a[0]); string what = "none", first = null;
                    foreach (Labor l in m) { if (BotHolds(l, a[1], out what)) return BotEval.Yes(BotWorkers.DisplayName(l) + " holds " + what + " | " + OrangeBot.HeldReport()); if (first == null) first = BotWorkers.DisplayName(l) + " holds " + what; }
                    return BotEval.No("no matching worker holds " + a[1] + (first == null ? "" : "; first: " + first));
                }
                case "blueprint":
                {
                    Blueprint b = BotFirstBlueprint(a[0], c.Anti); bool exists = b != null;
                    string d = (c.Anti ? "anti " : "") + "blueprint " + a[0] + ": " + (b == null ? "none" : BotBlueprint.Describe(b));
                    return (a[1] == "exists") == exists ? BotEval.Yes(d) : BotEval.No(d);
                }
                case "blueprints":
                {
                    int k = 0; foreach (Blueprint b in BotBlueprint.OwnBlueprints(false)) if (b.IsAnti == c.Anti && BotKeyIs(b.AssetKey, a[0])) k++;
                    return BotCmp(c, k, (c.Anti ? "anti " : "") + "blueprints " + a[0]);
                }
                case "structure": return BotCmp(c, BotOwnStructures(a[0]).Count, "own structures " + a[0]);
                case "queue":
                {
                    string tag = BotStationTag(a[0]); CraftingStation st = BotCraft.FindOwnStation(tag);
                    if (st == null) return BotEval.No("no own built " + tag);
                    BotEval e = BotCmp(c, BotCraft.QueueIds(st).Count, tag + " queue"); e.Detail += " | " + BotCraft.Describe(st);
                    return e;
                }
                case "hologram":
                {
                    Blueprint b = BotFirstBlueprint(a[0], c.Anti);
                    if (b == null) return BotEval.No("no own " + (c.Anti ? "anti " : "") + "blueprint " + a[0] + " yet");
                    string mat; Color col = BotBlueprint.HologramColor(b, out mat);
                    string actual = mat == null ? "none" : BotHue(col), logical = "?", want = a[1].ToLowerInvariant();
                    try { BlueprintMaterialCache.BlueprintDisplay d = BlueprintMaterialCache.ForMode(b.State); if (d != null) logical = BotHue(d.color); } catch (Exception) { }
                    bool modCopy = mat != null && mat.EndsWith("_Orange", StringComparison.Ordinal);
                    // the game draws the hologram in its state colour only while its task is selected on this PC; otherwise white: then the state colour decides
                    bool ok = actual == want || ((actual == "white" || actual == "gray") && logical == want);
                    string detail = "actual=" + actual + " (" + (mat ?? "no material") + " " + col + ") stateColour=" + logical + " state=" + b.State + (modCopy ? " MOD-ORANGE-COPY" : "");
                    return ok ? BotEval.Yes(detail) : BotEval.No(detail);
                }
                case "color":
                {
                    BotColorStats st; if (!w.shots.TryGetValue(a[0], out st)) return BotEval.Unable("no shot '" + a[0] + "' with a focus box yet (take 'shot " + a[0] + " focus ...' first)");
                    BotEval e = BotCmp(c, Math.Round(st.Percent(a[1].ToLowerInvariant()), 2), a[1] + " % in the box of " + a[0]); e.Detail += " | " + st.Summary();
                    return e;
                }
            }
            return BotEval.Unable("condition not implemented: " + c.Kind);
        }

        // end of a run: camera, sound and the builders on hold go back to how they were
        void BotRunCleanup(BotGameWorld w)
        {
            try { if (w.cam != null && w.camScene == SceneManager.GetActiveScene().name) { CameraScript cs = BotCamera.Get(); if (cs != null) BotCamera.Restore(cs, w.cam); } w.cam = null; } catch (Exception) { }
            try { if (w.muteSaved >= 0) { Brix.Audio.SoundEngine.mute = w.muteSaved == 1; w.muteSaved = -1; } } catch (Exception) { }
            try
            {
                foreach (KeyValuePair<Labor, AutonomyStatus.Ids> kv in w.held)
                    if (kv.Key != null && kv.Key.gameObject != null && kv.Value != AutonomyStatus.Ids.lockedToOperable) BotWorkers.SetAutonomy(kv.Key, kv.Value);
                w.held.Clear();
            }
            catch (Exception) { }
            try
            {
                if (w.editorSaved >= 0 && Brix.Engine.Architecte.editorMode != (w.editorSaved == 1))
                { Brix.Engine.Architecte.editorMode = w.editorSaved == 1; Brix.Engine.GameSignals.Invoke(Brix.Engine.GameSignals.OnEditorModeChanged, w.editorSaved == 1); }
                if (w.debugMenuSaved >= 0) Brix.Engine.Architecte.allowDebugMenu = w.debugMenuSaved == 1;
                w.editorSaved = -1; w.debugMenuSaved = -1;
            }
            catch (Exception) { }
        }
    }
}
