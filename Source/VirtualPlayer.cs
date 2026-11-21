using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.CatapultSpace;
using Brix.Game.Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.78 VIRTUAL PLAYER (test only, needs [Test] Enabled = true). Runs a script from BepInEx\config\test-actions.txt (consumed once) and issues the SAME
    // game calls a player's mouse/keyboard would end in (see VirtualPlayerRules for the commands). It needs no mouse, keyboard, focus or foreground window, so
    // two game instances (main PC + Sandboxie box) can each be driven by their own file while the real mouse stays free. It does not touch the real input
    // devices, installs no driver and does not simulate pixels/UI: it exercises the game's own commands (host authority is unchanged).
    // Results: BepInEx\test-actions.result.txt and VPLAYER lines in perf-trace.txt (with timestamps, so host and guest logs can be compared).
    public sealed partial class SteamBridgePlugin
    {
        static readonly List<VirtualAction> vpQueue = new List<VirtualAction>(); static float vpNextAt, vpPollAt; static int vpDone; static bool vpUnarmedLogged;

        static string VpResultPath { get { return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "test-actions.result.txt"); } }
        static void VpLog(string text)
        {
            if (vpCapture != null) { vpCapture.Append(text).Append('\n'); PtLog("VPLAYER " + text); return; }   // 0.4.10: the in-game bot runs this command (Bot.cs)
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Time.realtimeSinceStartup.ToString("F2") + " " + text;
            PtLog("VPLAYER " + text);
            try { File.AppendAllText(VpResultPath, line + "\r\n", new UTF8Encoding(true)); } catch (Exception) { }
        }

        // Two locks: [Test] Enabled = true AND the marker file BepInEx\config\virtual-player.enable. Without both the channel does nothing (default: off).
        static bool VpArmed() { try { return File.Exists(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "virtual-player.enable")); } catch (Exception) { return false; } }
        static void VpKeepCopy(string file)
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "test-actions-history"); Directory.CreateDirectory(dir);
                string name = "test-actions-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".txt"; File.Copy(file, System.IO.Path.Combine(dir, name), true);
                string[] old = Directory.GetFiles(dir, "test-actions-*.txt"); Array.Sort(old); for (int i = 0; i < old.Length - 50; i++) { try { File.Delete(old[i]); } catch (Exception) { } }
            }
            catch (Exception) { }
        }

        static List<CatapultControl> VpOwnCatapults()
        {
            List<CatapultControl> res = new List<CatapultControl>(); Faction local = User.LocalUser == null ? null : User.LocalUser.faction;
            foreach (CatapultControl c in UnityEngine.Object.FindObjectsOfType<CatapultControl>())
            {
                if (c == null) continue; AffiliationComponent a = c.GetComponent<AffiliationComponent>();
                if (local != null && a != null && a.faction == local) res.Add(c);
            }
            res.Sort(delegate(CatapultControl x, CatapultControl y) { return string.CompareOrdinal(x.name, y.name); });
            return res;
        }

        void VirtualPlayerTick()
        {
            if (!TestOn) return;
            float now = Time.realtimeSinceStartup;
            if (vpQueue.Count == 0)
            {
                if (now < vpPollAt) return; vpPollAt = now + 1f;
                if (BotRunActive) return;   // 0.4.10: never two script runners at once; test-actions.txt waits until the bot run ends
                string file = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "test-actions.txt");
                if (!File.Exists(file)) return;
                // 0.4.10: the second lock the comments always promised (VpArmed was defined but never called): [Test] Enabled alone no longer runs a script
                if (!VpArmed()) { if (!vpUnarmedLogged) { vpUnarmedLogged = true; PtLog("VPLAYER test-actions.txt ignored: marker BepInEx\\config\\virtual-player.enable missing"); } return; }
                try
                {
                    string[] lines = File.ReadAllLines(file); VpKeepCopy(file); File.Delete(file);
                    int errors; List<VirtualAction> script = VirtualPlayerRules.ParseScript(lines, out errors); vpQueue.AddRange(script); vpDone = 0;
                    VpLog("script loaded: " + script.Count + " action(s), " + errors + " with errors, role=" + (NetworkServer.active ? "host" : NetworkClient.active ? "guest" : "single"));
                }
                catch (Exception e) { VpLog("script read failed: " + e.Message); }
                return;
            }
            if (now < vpNextAt) return;
            VirtualAction a = vpQueue[0]; vpQueue.RemoveAt(0); vpDone++; vpNextAt = now + 0.25f;
            try { if (!VpRunInstance(a)) VpRun(a); } catch (Exception e) { VpLog("#" + vpDone + " " + a.Name + " RESULT=failed reason=" + e.GetType().Name + ": " + e.Message); }
            if (vpQueue.Count == 0) VpLog("script finished (" + vpDone + " action(s))");
        }

        // Commands that need the plugin instance (bridge, friends) or the lobby UI statics. Return true when handled.
        bool VpRunInstance(VirtualAction a)
        {
            if (a.Error != null) return false;
            if (a.Name == "barrels") { VpLog("#" + vpDone + " " + OrangeWardItem.Describe()); return true; }
            if (a.Name == "doors") { VpListDoors(); return true; }
            if (a.Name == "breakdoor") { VpBreakDoor(a.Cat); return true; }
            if (a.Name == "fillstock") { int piles; int crates = StockFill(a.Key, out piles); VpLog("#" + vpDone + " fillstock " + (a.Key ?? "all types") + ": stockpiles=" + piles + " crates filled=" + crates); return true; }
            if (a.Name == "joinlan") return VpJoinLan();
            if (a.Name == "snapshot") return VpSnapshot();
#if HEAPWALK
            if (a.Name == "heapwalk") return VpHeapWalk();
#else
            if (a.Name == "heapwalk") { VpLog("heapwalk: not in this build (0.3.86: build with /define:HEAPWALK, plugin size limit)"); return false; }
#endif
            if (a.Name == "join")
            {
                RefreshFriends();
                if (friends.Count != 1) { VpLog("#" + vpDone + " join RESULT=failed reason=" + friends.Count + " friend(s) in Castle Story (need exactly 1)"); return true; }
                if (bridge != null || hosting) { VpLog("#" + vpDone + " join RESULT=failed reason=bridge already running (hosting=" + hosting + ")"); return true; }
                peer = friends[0]; peerByCode = false; StartBridge(false);
                VpLog("#" + vpDone + " join started to " + SteamFriends.GetFriendPersonaName(peer) + " (bridge " + (bridge != null) + ")"); return true;
            }
            if (a.Name == "ready" || a.Name == "unready")
            {
                Brix.UI.Builder.Menu.LobbyMenu.SetIsReady(a.Name == "ready");
                VpLog("#" + vpDone + " " + a.Name + " sent (can set ready: " + Brix.UI.Builder.Menu.LobbyMenu.CanSetReady + ")"); return true;
            }
            if (a.Name == "net") { ApplyTestControl(a.Key, a.Seconds, Time.realtimeSinceStartup); VpLog("#" + vpDone + " net " + a.Key + "=" + a.Seconds); return true; }
            if (a.Name == "walkrel") { Vector3 an = VpAnchor(); a.X += an.x; a.Z += an.z; a.Name = "walk"; }
            if (a.Name == "walk")
            {
                Brix.Game.User u = Brix.Game.User.LocalUser; Brix.Game.Faction local = u == null ? null : u.faction;
                List<GameObject> mine = new List<GameObject>(); float y = 0f;
                foreach (Brix.Game.AI.Labor l in UnityEngine.Object.FindObjectsOfType<Brix.Game.AI.Labor>()) if (l != null && local != null && l.faction == local) { mine.Add(l.gameObject); y = l.transform.position.y; }
                Brix.Game.Network.UNetContextualCmd cc = u == null ? null : u.GetComponent<Brix.Game.Network.UNetContextualCmd>();
                if (mine.Count == 0 || cc == null) { VpLog("#" + vpDone + " walk RESULT=failed reason=no own workers / command component"); return true; }
                cc.CallCmdBatchedContextualActionVector(mine.ToArray(), new Vector3(a.X, y, a.Z), Brix.Input.ContextualOrders.ActionType.Primary);
                VpLog("#" + vpDone + " walk sent " + mine.Count + " worker(s) -> " + a.X + "," + y.ToString("F0") + "," + a.Z); return true;
            }
            if (a.Name == "start") { Brix.UI.Builder.Menu.LobbyMenu.StartGame(); VpLog("#" + vpDone + " start sent"); return true; }
            if (a.Name == "lobby")
            {
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                VpLog("#" + vpDone + " lobby scene=" + scene + " canReady=" + Brix.UI.Builder.Menu.LobbyMenu.CanSetReady + " ready=" + Brix.UI.Builder.Menu.LobbyMenu.GetIsReady() + " server=" + NetworkServer.active + " client=" + NetworkClient.active + " bridge=" + (bridge != null && bridge.Ready) + " hosting=" + hosting + " guests=" + guests.Count + " friends=" + friends.Count);
                return true;
            }
            return false;
        }

        static void VpRun(VirtualAction a)
        {
            if (a.Error != null) { VpLog("#" + vpDone + " " + a.Name + " RESULT=rejected reason=" + a.Error); return; }
            User u = User.LocalUser; UNetCatapult cmd = u == null ? null : u.GetComponent<UNetCatapult>();
            if (a.Name == "wait") { vpNextAt = Time.realtimeSinceStartup + a.Seconds; VpLog("#" + vpDone + " wait " + a.Seconds); return; }
            if (a.Name == "status") { VpLog("#" + vpDone + " status localFaction=" + (u == null || u.faction == null ? "none" : u.faction.ID.ToString()) + " ownCatapults=" + VpOwnCatapults().Count + " server=" + NetworkServer.active + " client=" + NetworkClient.active); return; }
            if (a.Name == "digrel") { Vector3 an = VpAnchor(); a.X = Mathf.Round(an.x + a.X); a.Y = Mathf.Round(an.y + a.Y); a.Z = Mathf.Round(an.z + a.Z); a.Name = "dig"; }
            List<CatapultControl> cats = VpOwnCatapults();
            if (a.Name == "aimrel" && a.Cat < cats.Count) { Vector3 cp = cats[a.Cat].transform.position; a.X += cp.x; a.Y = cp.y; a.Z += cp.z; a.Name = "aim"; }
            if (a.Name == "list")
            {
                for (int i = 0; i < cats.Count; i++) { Vector3 p = cats[i].transform.position; VpLog("#" + vpDone + " list own[" + i + "]=" + cats[i].name + " pos=" + CsV(p) + " cible=" + CsV(cats[i].cible)); }
                if (cats.Count == 0) VpLog("#" + vpDone + " list: no own catapults"); return;
            }
            if (a.Name == "dig")
            {
                Faction local = u == null ? null : u.faction; Labor worker = null;
                foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>()) if (l != null && local != null && l.faction == local) { worker = l; break; }
                UNetProjectCmd pc = u == null ? null : u.GetComponent<UNetProjectCmd>();
                if (worker == null || pc == null) { VpLog("#" + vpDone + " dig: no own worker / command component"); return; }
                int index = XYZ.FromVector3(new Vector3(a.X, a.Y, a.Z)).ToScalaire();
                pc.CallCmdAddBreakVoxelGoal(worker.gameObject, index); VpLog("#" + vpDone + " dig sent worker=" + worker.name + " at " + CsV(new Vector3(a.X, a.Y, a.Z))); return;
            }
            if (a.Cat >= cats.Count) { VpLog("#" + vpDone + " " + a.Name + ": own catapult index " + a.Cat + " does not exist (have " + cats.Count + ")"); return; }
            CatapultControl cc = cats[a.Cat]; Catapult cat = cc.GetComponent<Catapult>(); Vector3 pt = new Vector3(a.X, a.Y, a.Z);
            if (a.Name == "hover") { if (cat != null) cat.Viser(pt, Vector3.up); VpLog("#" + vpDone + " hover " + cc.name + " -> " + CsV(pt) + " (local cible now " + CsV(cc.cible) + ")"); }
            else if (a.Name == "leave") { if (cat != null) cat.ArreterDeViser(); VpLog("#" + vpDone + " leave " + cc.name + " (local cible " + CsV(cc.cible) + ")"); }
            else if (cmd == null) { VpLog("#" + vpDone + " " + a.Name + ": no UNetCatapult on the local user"); }
            else if (a.Name == "aim") { cmd.CallCmdSetTarget(cc.gameObject, pt, Vector3.up); VpLog("#" + vpDone + " aim sent " + cc.name + " -> " + CsV(pt)); }
            else if (a.Name == "clear") { cmd.CallCmdClearTarget(cc.gameObject); VpLog("#" + vpDone + " clear sent " + cc.name); }
            else if (a.Name == "fire") { cmd.CallCmdRequestFireOnce(cc.gameObject); VpLog("#" + vpDone + " fire-once sent " + cc.name); }
        }

        // 0.3.84 relative commands: anchor = centroid of the own workers when first used (so scripts work whichever base a player gets)
        static bool vpAnchorSet; static Vector3 vpAnchorPos;
        static Vector3 VpAnchor()
        {
            if (vpAnchorSet) return vpAnchorPos;
            User u = User.LocalUser; Faction local = u == null ? null : u.faction; Vector3 sum = Vector3.zero; int n = 0;
            foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>()) if (l != null && local != null && l.faction == local) { sum += l.transform.position; n++; }
            if (n == 0) return Vector3.zero;
            vpAnchorPos = sum / n; vpAnchorSet = true; VpLog("anchor = " + CsV(vpAnchorPos) + " (" + n + " own workers)"); return vpAnchorPos;
        }
    }
}
