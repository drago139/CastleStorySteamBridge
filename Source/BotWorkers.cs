// 0.4.10 IN-GAME TEST BOT game layer: taken from the read-only research draft bot-research\workers-BotWorkers-draft.cs (side-session-notes of local_a8d26072, 2026-11-06),
// compiled with the plugin (signatures checked by csc against the real game DLLs). Called only by the bot (Bot.cs / BotActions.cs) while it is armed.
// workers-BotWorkers-draft.cs  (research draft, area 'workers'; NOT compiled: research rule = read + decompile only)
// C# 5 / Unity 5.6 Mono. Every member used below was checked in decompiles of the REAL DLLs (bot-research\workers-*.cs,
// ilspycmd 9.1 on Castle Story_Data\Managed\Assembly-CSharp-firstpass.dll and Brix.Core.dll). The recovered FP sources match them
// (only syntax differences: lambda vs delegate, RecoveredNetworkCode vs GeneratedNetworkCode).
// Must run on the Unity main thread (plugin Update / coroutine). CallCmd* works on host and guest (needs NetworkClient.active).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Brix.Engine;                    // GameSignals
using Brix.External.Factories;        // Factory, Factory.AssetKey, FactoryImprint (Brix.Core.dll), Accessories (firstpass)
using Brix.Game;                      // User, Faction, Affiliation
using Brix.Game.AI;                   // Labor, Project, BuildProject, AutonomyStatus, Toolbag, DirectOrderProject, Occupation, Thought, TaskResult
using Brix.Game.Bricktron;            // Nom, GestionnaireDeNom
using Brix.Game.Components;           // Blueprint
using Brix.Game.Network;              // UNetProjectCmd, UNetContextualCmd, UNetGroupe
using Brix.Game.Semantique;           // Recepteur, IDescriptor
using Brix.Input;                     // CameraScript
using Brix.Input.ContextualOrders;    // ActionType
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    internal static class BotWorkers
    {
        // ------------------------------------------------------------------ command components on the local player's object
        static UNetProjectCmd ProjectCmd() { User u = User.LocalUser; return u == null ? null : u.GetComponent<UNetProjectCmd>(); }
        static UNetContextualCmd ContextCmd() { User u = User.LocalUser; return u == null ? null : u.GetComponent<UNetContextualCmd>(); }
        static UNetGroupe GroupeCmd() { User u = User.LocalUser; return u == null ? null : u.GetComponent<UNetGroupe>(); }

        // ------------------------------------------------------------------ identity
        // netId is assigned by the host and replicated: the same number on host and guest -> stable index/handle for both bots.
        internal static uint NetIdOf(GameObject go)
        {
            NetworkIdentity ni = go == null ? null : go.GetComponent<NetworkIdentity>();
            return ni == null ? 0u : ni.netId.Value;
        }

        // Display name = Nom.nom (SyncVar _nom); Nom.SetNom also sets gameObject.name (BrixComponentPoco.Name), on host and guest.
        internal static string DisplayName(Labor l)
        {
            if (l == null) return "";
            Nom n = l.GetComponent<Nom>();
            string s = n == null ? null : n.nom;
            return string.IsNullOrEmpty(s) ? l.name : s;
        }

        // Own workers (faction of the local user), sorted by netId. buildersOnly: Occupation Builder (class from tools; Knights/Archers excluded).
        internal static List<Labor> OwnWorkers(bool buildersOnly)
        {
            List<Labor> res = new List<Labor>();
            User u = User.LocalUser; Faction local = u == null ? null : u.faction;
            if (local == null) return res;
            foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>())
            {
                if (l == null || l.faction != local) continue;
                if (buildersOnly && (l.Occupation == null || l.Occupation.CurrentOccupation != Occupation.Type.Builder)) continue;
                res.Add(l);
            }
            res.Sort(delegate(Labor a, Labor b) { return NetIdOf(a.gameObject).CompareTo(NetIdOf(b.gameObject)); });
            return res;
        }

        internal static Labor WorkerByIndex(int index)
        {
            List<Labor> all = OwnWorkers(true);
            return index >= 0 && index < all.Count ? all[index] : null;
        }

        internal static Labor WorkerByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            GameObject go = GestionnaireDeNom.Get(name);          // static name registry, filled on host and guest by Nom.SetNom
            Labor l = go == null ? null : go.GetComponent<Labor>();
            if (l != null && Affiliation.BelongsToMe(l.gameObject)) return l;
            foreach (Labor x in OwnWorkers(false)) if (DisplayName(x) == name) return x;
            return null;
        }

        internal static Labor WorkerByNetId(uint netId)
        {
            foreach (Labor x in OwnWorkers(false)) if (NetIdOf(x.gameObject) == netId) return x;
            return null;
        }

        // ------------------------------------------------------------------ projects
        internal static Project ProjectOfBlueprint(GameObject blueprintGO)
        {
            Blueprint b = blueprintGO == null ? null : blueprintGO.GetComponent<Blueprint>();
            return b == null ? null : b.Project;                  // BlueprintProjectParentTracker SyncVar -> works on guest
        }

        internal static List<BuildProject> OwnBuildProjects()
        {
            List<BuildProject> res = new List<BuildProject>();
            User u = User.LocalUser; Faction local = u == null ? null : u.faction;
            if (local == null) return res;
            foreach (BuildProject p in UnityEngine.Object.FindObjectsOfType<BuildProject>())
                if (p != null && p.faction == local) res.Add(p);
            res.Sort(delegate(BuildProject a, BuildProject b) { return NetIdOf(a.gameObject).CompareTo(NetIdOf(b.gameObject)); });
            return res;
        }

        // ------------------------------------------------------------------ ACTIONS (each ends in the same Cmd a player's input ends in)
        // Task panel "add selection to crew": Project.AssignSelection -> UIGameActor.AssignCharacterToProject -> DirectOrderProject.ChangeProject
        // -> UNetProjectCmd.CallCmdChangeProject. Host: faction check, raise crew cap if full (max 8), Hire, Autonomy = LockedToOperable.
        internal static bool AssignToProject(Labor l, Project p)
        {
            if (l == null || p == null || ProjectCmd() == null) return false;
            DirectOrderProject.ChangeProject(l, p, false);        // no local side effects except optional sound
            return true;
        }

        // Task panel "remove from crew": AssignSelectionToProject(idle) -> CmdChangeProject(labor, idle): host AssignToIdle + Autonomy = FreeAgent.
        internal static bool Unassign(Labor l)
        {
            UNetProjectCmd pc = ProjectCmd();
            if (l == null || pc == null) return false;
            IdleProject idle = User.LocalUser.faction == null ? null : IdleProject.GetMatchingProject(User.LocalUser.faction);
            pc.CallCmdChangeProject(l.gameObject, idle == null ? null : idle.gameObject);   // null is handled the same way on the host
            return true;
        }

        // Autonomy (action wheel Toggle_Hold_Position, Lua SetAutonomyFreeAgent/Instinct/HoldPosition). For a Builder the host also sends HaltWhatever.
        internal static bool SetAutonomy(Labor l, AutonomyStatus.Ids id)
        {
            UNetProjectCmd pc = ProjectCmd();
            if (l == null || pc == null) return false;
            pc.CallCmdSetAutonomy(l.gameObject, (int)id);
            return true;
        }

        // Action wheel "Take from" / "Pick up" (Instructions Action_PickUp): DirectOrderProject.PickUp = labor.AssignToIdle() (LOCAL) + CallCmdPickup.
        // Host: Order.Transfer.GoPickUpFrom -> GoPickUp(container, -1): walk to it, take as much as fits.
        internal static bool PickUpFrom(Labor l, GameObject containerOrItem)
        {
            if (l == null || containerOrItem == null || ProjectCmd() == null) return false;
            DirectOrderProject.PickUp(l, containerOrItem);
            return true;
        }

        // Action wheel "Drop load into" (Event_PlaceLuggage): DirectOrderProject.AddGoDropInto = AssignToIdle (LOCAL) + sound + CallCmdGoStoreInto.
        // Host: Order.Transfer.GoStoreInto -> StoreNode.GoStoreIn: walk, Transaction.StoreEverything(labor -> target). Target may be a crate or a blueprint.
        internal static bool StoreInto(Labor l, GameObject container)
        {
            if (l == null || container == null || ProjectCmd() == null) return false;
            DirectOrderProject.AddGoDropInto(l, container);
            return true;
        }

        // Action wheel "Drop luggage" (Action_DropLuggage): DirectOrderProject.AddGoDropAt -> CallCmdGoDropAt -> GoDropAt(Location.LeadingTo(p)).
        internal static bool DropAt(Labor l, Vector3 p)
        {
            if (l == null || ProjectCmd() == null) return false;
            DirectOrderProject.AddGoDropAt(l, p);
            return true;
        }

        // Right click (PickerOrderGiver.DirectOrder) on an object / on the ground. Resolved ON THE HOST by ContextualActor:
        //   Recepteur (crate, blueprint, any container): Primary = SmartTransfer (pick up first, else store), Secondary = store first;
        //   loose resource = GoPickUpOne; Tool = GoPickUp tool; ground Primary = RunToward (formation), Secondary = drop (bag full) or dig.
        // The host calls subject.AssignToIdle() first: the worker LEAVES its build project.
        internal static bool RightClickObject(List<Labor> who, GameObject target, ActionType type)
        {
            UNetContextualCmd cc = ContextCmd();
            if (cc == null || who == null || who.Count == 0 || target == null) return false;
            GameObject[] arr = new GameObject[who.Count];
            for (int i = 0; i < who.Count; i++) arr[i] = who[i].gameObject;
            cc.CallCmdBatchedContextualActionGameObject(arr, target, type);
            return true;
        }

        internal static bool RightClickGround(List<Labor> who, Vector3 point, ActionType type)
        {
            UNetContextualCmd cc = ContextCmd();
            if (cc == null || who == null || who.Count == 0) return false;
            GameObject[] arr = new GameObject[who.Count];
            for (int i = 0; i < who.Count; i++) arr[i] = who[i].gameObject;
            cc.CallCmdBatchedContextualActionVector(arr, point, type);
            return true;
        }

        // "Stop": DirectOrderProject.Stop = AssignToIdle (LOCAL) + CallCmdStop -> host AbortWorkSignal(ReceivedNewInstruction).
        internal static bool Stop(Labor l)
        {
            if (l == null || ContextCmd() == null) return false;
            DirectOrderProject.Stop(l);
            return true;
        }

        // Task panel OK/Apply on a build project (blueprints area, but required before anyone builds): UNetGroupe.CallCmdCommit.
        internal static bool Commit(Project p)
        {
            UNetGroupe g = GroupeCmd();
            if (g == null || p == null) return false;
            g.CallCmdCommit(p.gameObject);
            return true;
        }

        // ------------------------------------------------------------------ READS (host and guest unless marked)
        // Bag = the worker's own Recepteur. Each item has a RecepteurParentTracker SyncVar; its hook runs Recepteur.OnObjectTracked/ObjectUntracked
        // on every peer, so StoredItems is filled on the guest too.
        internal static string BagText(Labor l)
        {
            Recepteur r = l == null ? null : l.recepteur;
            if (r == null) return "no-recepteur";
            if (r.IsEmpty()) return "empty";
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (IDescriptor d in r.StoredItems)
            {
                if (d == null) continue;
                string k = KeyText(d);
                int c; counts.TryGetValue(k, out c); counts[k] = c + 1;
            }
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in counts) { if (sb.Length > 0) sb.Append(','); sb.Append(kv.Key).Append('x').Append(kv.Value); }
            return sb.ToString() + " [" + r.ContentString() + "]";      // ContentString: "Empty" | "Fill" (bag stuff) | resource type name
        }

        static string KeyText(IDescriptor d)
        {
            try { Factory.AssetKey k = d.AssetKey; if (k != null) return k.ToString(); } catch (Exception) { }
            GameObject go = d.GameObject; return go == null ? "?" : go.name;
        }

        // Carried MODEL: host Interaction.UpdateCarrying (server only) -> Toolbag.EquipMagicalTool(Props.GetProp) -> pooled Tool,
        // NetworkServer.Spawn(go, key.AssetId), tracked into a grip slot by ToolParentTracker (SyncVar) -> present in the toolbag on the guest too.
        internal static GameObject HeldProp(Labor l, out Factory.AssetKey key)
        {
            key = null;
            Toolbag tb = l == null ? null : l.GetComponent<Toolbag>();
            if (tb == null) return null;
            List<GameObject> tools = new List<GameObject>();
            tb.Tools(ref tools, false, true);
            foreach (GameObject t in tools)
            {
                if (t == null) continue;
                global::Tool tool = t.GetComponent<global::Tool>();
                FactoryImprint fi = t.GetComponent<FactoryImprint>();
                Factory.AssetKey k = fi == null ? null : fi.AssetKey;
                if (tool == null || !tool.Magical || k == null || k == Accessories.Scarf) continue;   // Magical is a SyncVar
                if (k == Accessories.Bag || (k.Name != null && k.Name.StartsWith("Held", StringComparison.Ordinal))) { key = k; return t; }
            }
            return null;
        }

        // asset id = NetworkHash128.Parse(MD5(factory + name)) (Factory.AssetKey.AssetId, Brix.Core) = what NetworkServer.Spawn used.
        internal static bool IsAsset(GameObject go, Factory.AssetKey key)
        {
            NetworkIdentity ni = go == null ? null : go.GetComponent<NetworkIdentity>();
            return ni != null && key != null && ni.assetId.ToString() == key.AssetId.ToString();
        }

        // Thought bubble: CharacterState.thoughtId SyncVar. On a guest Thought.FromId can fall back to ##thought_unknown
        // (brix.log WARN "Could not retrieve thoughId N from id") when that Thought static was never created there; the raw id still compares.
        static readonly FieldInfo ThoughtIdField = typeof(CharacterState).GetField("thoughtId", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static int RawThoughtId(Labor l)
        {
            CharacterState cs = l == null ? null : l.GetComponent<CharacterState>();
            if (cs == null || ThoughtIdField == null) return 0;
            return (int)ThoughtIdField.GetValue(cs);
        }
        internal static bool ThoughtIs(Labor l, string label) { return RawThoughtId(l) == label.GetHashCode(); }   // Thought.id = label[0].GetHashCode()

        internal static string WorkerLine(Labor l)
        {
            if (l == null) return "worker=null";
            StringBuilder sb = new StringBuilder();
            Vector3 pos = l.transform.position;
            Project p = l.Project;                                  // CharacterParentTracker SyncVar
            AutonomyStatus a = l.Autonomy;                          // CharacterState.autonomyID SyncVar
            Thought th = l.Thought;                                 // CharacterState.thoughtId SyncVar
            Factory.AssetKey heldKey; GameObject held = HeldProp(l, out heldKey);
            sb.Append("net=").Append(NetIdOf(l.gameObject)).Append(" name=").Append(DisplayName(l));
            sb.Append(" pos=").Append(pos.x.ToString("F1")).Append(',').Append(pos.y.ToString("F1")).Append(',').Append(pos.z.ToString("F1"));
            sb.Append(" project=").Append(p == null ? "none" : (p is IdleProject ? "idle" : p.name + "#" + NetIdOf(p.gameObject)));
            sb.Append(" autonomy=").Append(a == null ? "?" : a.Id.ToString());
            sb.Append(" thoughtId=").Append(RawThoughtId(l)).Append(" thought=").Append(th == null ? "?" : th.GetLabel());
            sb.Append(" bag=").Append(BagText(l));
            sb.Append(" held=").Append(heldKey == null ? "none" : heldKey.ToString()).Append(held == null ? "" : " heldNet=" + NetIdOf(held));
            if (NetworkServer.active)
            {   // AI state exists only on the host (MultiBehavior.ToTree returns Node.Forever on clients)
                Brix.Game.AI.Task t = l.CurrentTask;
                GameObject giver = l.GetCurrentTaskGiver();
                sb.Append(" task=").Append(t == null ? "none" : t.Description);
                sb.Append(" giver=").Append(giver == null ? "none" : giver.name + "#" + NetIdOf(giver));
                sb.Append(" activity=").Append(l.Activity.ToString());
                sb.Append(" carryType=").Append(l.interaction == null ? "?" : l.interaction.CarriedType.ToString());
            }
            return sb.ToString();
        }

        // Build done = the blueprint object is gone (Blueprint.OnReceivedNewItem on the host -> MorphToObject -> SafeNetworkDestroy).
        // Keep the blueprint's netId from before; pooled objects get reused, so test by id, not by reference.
        internal static bool NetObjectGone(uint netId)
        {
            NetworkInstanceId id = new NetworkInstanceId(netId);
            GameObject go = NetworkServer.active ? NetworkServer.FindLocalObject(id) : ClientScene.FindLocalObject(id);
            return go == null || !go.activeInHierarchy;
        }

        internal static string BlueprintLine(GameObject bp)
        {
            Blueprint b = bp == null ? null : bp.GetComponent<Blueprint>();
            if (b == null) return "blueprint=none";
            Recepteur r = b.recepteur;
            Project p = b.Project;
            return "blueprint net=" + NetIdOf(bp) + " commited=" + b.commited + " state=" + b.State
                + " stored=" + (r == null ? -1 : r.StoredItems.Count) + " full=" + (r != null && r.IsFull())
                + " project=" + (p == null ? "none" : p.name + "#" + NetIdOf(p.gameObject));
        }

        // Camera on a worker (Brix.Input.CameraScript.Instance, set in its Awake/Start; MiniMap uses the same LookAt).
        internal static void LookAt(Labor l)
        {
            CameraScript cam = CameraScript.Instance;
            if (cam != null && l != null) cam.LookAt(l.transform.position);
        }

        // HOST ONLY: every finished task of the given faction. Caveat: CleanUp (this signal) runs in an inner Finally, before the outer
        // Catch sets result for failures, so a failed task may report Succes here; use the state reads for verdicts.
        static bool taskHooked;
        internal static void HookTaskEnd(Faction faction, Action<string> log)
        {
            if (taskHooked || !NetworkServer.active) return;
            taskHooked = true;
            GameSignals.Connect<Brix.Game.AI.Task, TaskResult>(Brix.Game.AI.Task.AnyTaskFinalized, delegate(Brix.Game.AI.Task t, TaskResult r)
            {
                try
                {
                    Labor w = t == null ? null : t.Worker as Labor;
                    if (w == null || (faction != null && w.faction != faction)) return;
                    log("BOT WORKER TASK-END net=" + NetIdOf(w.gameObject) + " name=" + w.name + " desc=" + t.Description
                        + " giver=" + (t.Giver == null ? "none" : t.Giver.name) + " result=" + r);
                }
                catch (Exception) { }
            });
        }
    }
}
