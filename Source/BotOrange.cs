// 0.4.10 IN-GAME TEST BOT game layer: taken from the read-only research draft bot-research\orange-OrangeBotHelpers.cs (side-session-notes of local_a8d26072, 2026-11-06),
// compiled with the plugin (signatures checked by csc against the real game DLLs). Called only by the bot (Bot.cs / BotActions.cs) while it is armed.
// SCRATCH (research area 'orange', 2026-11-06). Helpers for the in-game test bot: orange ward end to end.
// C# 5. Meant to live INSIDE the bridge plugin assembly (uses the internal OrangeWard* classes of Standalone-0.4.10\Source).
// Every game signature below was confirmed by decompiling the REAL DLLs of the installed game (Assembly-CSharp-firstpass.dll,
// Brix.Core.dll, UnityEngine.Networking.dll); the file itself was NOT compiled (research rule: read + decompile only).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using Brix.Lifecycle.Pooling;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    internal static class OrangeBot
    {
        // asset ids = NetworkHash128.Parse(MD5(ASCII(factory + name))).ToString() (Factory.AssetKey.AssetId, StringUtils.CalculateMD5Hash)
        internal static readonly Factory.AssetKey BpKey = new Factory.AssetKey("Blueprints", "OrangeWard");             // 2dfc9e7ac52633e9f9ddd17e1721b1fc
        internal static readonly Factory.AssetKey WardKey = new Factory.AssetKey("ObjetsDynamiques", "OrangeWard");     // ae0a3f0fac002a6f76ffafa915a5fecf
        internal static readonly Factory.AssetKey HeldKey = new Factory.AssetKey("Accessories", "HeldOrangeWard");      // 2fb84edc6f73c4497db5906835b5c863
        internal static readonly Factory.AssetKey HeldRedKey = new Factory.AssetKey("Accessories", "HeldSentinelWard"); // d08d4f6a76c0b3e86d2d97c0a065f3c8
        internal const int RecipeId = 200;   // = OrangeWardRecipe.RecipeId (network/save format)

        static object PrivStatic(Type t, string name) { FieldInfo f = AccessTools.Field(t, name); return f == null ? null : f.GetValue(null); }
        static bool Bluish(Color c) { return c.b > 0.55f && c.b > c.r * 1.6f; }                       // same rule as OrangeWardItem.Bluish
        static bool OrangeColour(Color c) { return c.r > 0.9f && c.g > 0.3f && c.g < 0.55f && c.b < 0.15f; } // (1, 0.42, 0.02)

        static string GemOf(GameObject go)
        {
            if (go == null) return "none";
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.name != "Gem" || r.sharedMaterial == null) continue;
                Material m = r.sharedMaterial;   // never .material (makes an instance copy "... (Instance)")
                return m.name + (m.HasProperty("_Color") ? " " + m.GetColor("_Color").ToString() : "");
            }
            return "no Gem";
        }

        static GameObject Selected(object observer)
        {
            return null;
        }

        // ------------------------------------------------------------ 1. start-up state (any PC, any time after the factories are registered)
        internal static string StartupCheck()
        {
            StringBuilder sb = new StringBuilder("orange startup:");
            IObjectPoolSingleton ps = ObjectPoolSingleton.Instance;
            Factory.AssetKey[] keys = { BpKey, WardKey, HeldKey };
            foreach (Factory.AssetKey k in keys)
                sb.Append(' ').Append(k).Append(" template=").Append(Factory.Exist(k))
                  .Append(" pool=").Append(ps != null && ps.IndexedPools != null && ps.IndexedPools.ContainsKey(k))
                  .Append(" id=").Append(k.AssetId.ToString()).Append(';');
            GameObject wt = Factory.Exist(WardKey) ? Factory.Peek(WardKey) : null;
            Ressource pr = wt == null ? null : Apparence.PrincipaleOf(wt);
            sb.Append(" tag=").Append(wt != null && wt.GetComponent<OrangeWardTag>() != null)
              .Append(" principal=").Append(pr == null ? "null" : pr.GetType().Name);   // expect Stick
            RecipeInfo ri = null;
            try { ri = CookBook.GetById(RecipeId); } catch (KeyNotFoundException) { }
            sb.Append(" recipe200=").Append(ri == null ? "missing" : ri.Creates + " work " + ri.work);
            string lua = "not exposed";
            try { if (OrangeWardRecipe.Info != null) lua = ((int)Brix.Lua.LuaCrafting.ToLuaRecipe(OrangeWardRecipe.Info)).ToString(); } catch (Exception) { }
            sb.Append(" luaRecipe=").Append(lua);   // 37 after 'lua exposed'
            sb.Append(" identityOn=").Append(PrivStatic(typeof(OrangeWardRecipe), "identityOn"))
              .Append(" luaDone=").Append(PrivStatic(typeof(OrangeWardRecipe), "done"))
              .Append(" heldClone=").Append(PrivStatic(typeof(OrangeWardHeld), "cloneReady"))
              .Append(" heldPool=").Append(PrivStatic(typeof(OrangeWardHeld), "poolReady"))
              .Append(" heldFailed=").Append(PrivStatic(typeof(OrangeWardHeld), "failed"));
            bool craft = OrangeWardRecipe.Enabled == null || OrangeWardRecipe.Enabled();
            bool held = OrangeWardHeld.HeldOn != null && OrangeWardHeld.HeldOn();
            float range = OrangeWardItem.getRange == null ? -1f : OrangeWardItem.getRange();
            sb.Append(" cfg craftable=").Append(craft).Append(" heldModel=").Append(held)
              .Append(" models=").Append(OrangeWardExtras.On(OrangeWardExtras.Models))
              .Append(" orangeHolograms=").Append(OrangeWardExtras.OrangeHolograms())
              .Append(" range=").Append(range.ToString("F0"));
            return sb.ToString();
        }

        // ------------------------------------------------------------ 2. queue the recipe at the own Altar (host or guest; the same Cmd the slot-6 click / key J ends in)
        internal static CraftingStation OwnAltar()
        {
            User u = User.LocalUser;
            if (u == null) return null;
            foreach (CraftingStation cs in UnityEngine.Object.FindObjectsOfType<CraftingStation>())
                if (cs != null && cs.PieMenuTag == "Altar" && Affiliation.IsOwnedBy(cs.gameObject, u)) return cs;
            return null;
        }

        internal static int OrangeInQueue(CraftingStation altar)
        {
            int n = 0;
            try { foreach (RecipeInfo r in altar.QueuedRecipes) if (r != null && r.id == RecipeId) n++; }
            catch (KeyNotFoundException) { return -1; }   // this PC has no recipe 200 (no / old mod)
            return n;
        }

        internal static string QueueOrange()
        {
            CraftingStation altar = OwnAltar();
            UNetProjectCmd pc = User.LocalUser == null ? null : User.LocalUser.GetComponent<UNetProjectCmd>();
            if (altar == null || pc == null) return "queue: no own Altar or no UNetProjectCmd";
            int before = OrangeInQueue(altar);
            pc.CallCmdcraftingQueueRecipe(altar.gameObject, RecipeId, 1);   // qty is ignored by the host: one entry per call, max 8
            return "queue sent: " + altar.name + " orange entries before=" + before;
        }

        internal static string AssignWorker(CraftingStation altar)
        {
            User u = User.LocalUser;
            if (u == null || altar == null) return "assign: no user / Altar";
            Faction f = u.faction; Labor pick = null;
            foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>()) if (l != null && f != null && l.faction == f) { pick = l; break; }
            if (pick == null) return "assign: no own worker";
            u.GetComponent<UNetProjectCmd>().CallCmdCraftingAssignWorker(altar.gameObject, pick.gameObject);
            return "assign sent: " + pick.name + " -> " + altar.name;
        }

        // ------------------------------------------------------------ 3. what every worker holds (host AND guest: tools are UNet objects parented by the tracker)
        internal static string HeldReport()
        {
            StringBuilder sb = new StringBuilder();
            string orangeId = HeldKey.AssetId.ToString(), redId = HeldRedKey.AssetId.ToString();
            FieldInfo carried = AccessTools.Field(typeof(Interaction), "CarriedProp");   // set on the HOST only (Interaction.UpdateCarrying is server-only)
            List<GameObject> tools = new List<GameObject>();
            int orange = 0, red = 0;
            foreach (Labor l in UnityEngine.Object.FindObjectsOfType<Labor>())
            {
                Toolbag tb = l == null ? null : l.GetComponent<Toolbag>();
                if (tb == null) continue;
                tools.Clear();
                tb.Tools(ref tools, false, true);
                foreach (GameObject t in tools)
                {
                    if (t == null) continue;
                    NetworkIdentity ni = t.GetComponent<NetworkIdentity>();
                    string aid = ni == null ? "" : ni.assetId.ToString();
                    if (aid != orangeId && aid != redId) continue;
                    bool isOrange = aid == orangeId;
                    if (isOrange) orange++; else red++;
                    FactoryImprint fi = t.GetComponent<FactoryImprint>();
                    Interaction ia = l.GetComponent<Interaction>();
                    object cp = (NetworkServer.active && carried != null && ia != null) ? carried.GetValue(ia) : null;
                    sb.Append(" | ").Append(l.name).Append(": ").Append(t.name)
                      .Append(" key=").Append(fi == null ? "?" : fi.AssetKey.ToString())
                      .Append(" id=").Append(aid).Append(" gem=").Append(GemOf(t))
                      .Append(isOrange ? " ORANGE" : " RED");
                    if (cp != null) sb.Append(" hostCarriedProp=").Append(cp);
                }
            }
            return "held: orange=" + orange + " red=" + red + sb;
        }

        // spawn flood check (T-174 step 7): live orange tool objects vs the pool counter
        internal static string HeldPoolReport()
        {
            IObjectPoolSingleton ps = ObjectPoolSingleton.Instance;
            IGameObjectPool p = null;
            if (ps != null && ps.IndexedPools != null) ps.IndexedPools.TryGetValue(HeldKey, out p);
            int live = 0; string id = HeldKey.AssetId.ToString();
            foreach (global::Tool t in UnityEngine.Object.FindObjectsOfType<global::Tool>())
            {
                NetworkIdentity ni = t == null ? null : t.GetComponent<NetworkIdentity>();
                if (ni != null && ni.assetId.ToString() == id) live++;
            }
            return "held orange: active tool objects=" + live + " pool instancesRequested=" + (p == null ? -1 : p.InstancesRequested);
        }

        // ------------------------------------------------------------ 4. blueprint: cursor ghost (local) and placement (Cmd)
        internal static void ShowGhost() { Brix.Input.Picking.SetBlueprintInPicker(BpKey); }   // what the build-menu click does (Lua Picking.SetHeldBlueprint)

        internal static GameObject OwnBuildProject()
        {
            User u = User.LocalUser;
            if (u == null) return null;
            foreach (BuildGoalProvider p in UnityEngine.Object.FindObjectsOfType<BuildGoalProvider>())
                if (p != null && Affiliation.IsOwnedBy(p.gameObject, u)) return p.gameObject;
            return null;
        }

        internal static string PlaceOrangeBlueprint(Vector3 pos, Quaternion rot, GameObject buildProject)
        {
            UNetBlueprint ub = User.LocalUser == null ? null : User.LocalUser.GetComponent<UNetBlueprint>();
            if (ub == null) return "place: no UNetBlueprint";
            ub.CallCmdSpawnPlacedBlueprint(BpKey.Factory, BpKey.Name, pos, rot, false, buildProject, false);
            return "place sent at " + pos + " project=" + (buildProject == null ? "none" : buildProject.name);
        }

        // demolish order (T-175 step 6). buildProject must NOT be null: CmdPlaceAnti dereferences it without a check when instant == false.
        internal static string DemolishOrange(GameObject ward, GameObject buildProject)
        {
            UNetBlueprint ub = User.LocalUser == null ? null : User.LocalUser.GetComponent<UNetBlueprint>();
            FactoryImprint fi = ward == null ? null : ward.GetComponent<FactoryImprint>();
            if (ub == null || fi == null || buildProject == null) return "demolish: need UNetBlueprint, a placed ward and a build project";
            Factory.AssetKey k = Brix.Game.Factories.BlueprintAssetKeyResolver.Resolve(fi.AssetKey);   // ObjetsDynamiques.OrangeWard -> Blueprints.OrangeWard
            if (k == null) return "demolish: no blueprint key for " + fi.AssetKey;
            ub.CallCmdPlaceAnti(k.Factory, k.Name, ward.transform.position, ward.transform.rotation, false, buildProject);
            return "anti sent for " + ward.name + " as " + k;
        }

        // ------------------------------------------------------------ 5. hologram colours of every Blueprints.OrangeWard object (ghost = no 'placed' flag)
        internal static string HologramReport()
        {
            StringBuilder sb = new StringBuilder(); int n = 0;
            foreach (Blueprint bp in UnityEngine.Object.FindObjectsOfType<Blueprint>())
            {
                FactoryImprint fi = bp == null ? null : bp.GetComponent<FactoryImprint>();
                if (fi == null || fi.AssetKey != BpKey) continue;
                int blue = 0, modOrange = 0, orangeCol = 0, other = 0; float scale = 1f; string sample = "";
                foreach (StateRenderer sr in bp.GetComponentsInChildren<StateRenderer>(true))
                {
                    scale = sr.transform.localScale.x;   // 1.04 = anti (demolish) and picking_remove displays
                    foreach (MeshRenderer mr in sr.GetComponentsInChildren<MeshRenderer>(true))
                        foreach (Material m in mr.sharedMaterials)
                        {
                            if (m == null || !m.HasProperty("_Color")) continue;
                            Color c = m.GetColor("_Color");
                            if (sample.Length == 0) sample = m.name + " " + c;
                            if (m.name.EndsWith("_Orange", StringComparison.Ordinal)) modOrange++;   // OrangeWardItem.OrangeCopy names its copies "<name>_Orange"
                            else if (Bluish(c)) blue++;
                            else if (OrangeColour(c)) orangeCol++;
                            else other++;
                        }
                }
                n++;
                sb.Append(" | ").Append(bp.name).Append(" state=").Append(bp.State).Append(" anti=").Append(bp.IsAnti)
                  .Append(" blue=").Append(blue).Append(" modOrange=").Append(modOrange).Append(" orangeColour=").Append(orangeCol)
                  .Append(" other=").Append(other).Append(" scale=").Append(scale.ToString("F2")).Append(" sample=").Append(sample);
            }
            return "orange blueprints=" + n + sb;
        }

        // ------------------------------------------------------------ 6. Altar preview (RecipeDisplay; drawn only for a local user allied with the Altar)
        internal static string PreviewReport(CraftingStation altar)
        {
            if (altar == null) return "preview: no Altar";
            FieldInfo fRd = AccessTools.Field(altar.GetType(), "recipeDisplay");   // Altar is internal: reach it through the runtime type
            RecipeDisplay rd = fRd == null ? null : fRd.GetValue(altar) as RecipeDisplay;
            if (rd == null) rd = altar.GetComponentInChildren<RecipeDisplay>(true);
            if (rd == null) return "preview: no RecipeDisplay";
            Factory.AssetKey shown = AccessTools.Field(typeof(RecipeDisplay), "key").GetValue(rd) as Factory.AssetKey;
            GameObject sentinel = AccessTools.Field(typeof(RecipeDisplay), "Sentinel").GetValue(rd) as GameObject;
            Transform twin = (sentinel == null || sentinel.transform.parent == null) ? null : sentinel.transform.parent.Find(sentinel.name + "_Orange");
            string col = "";
            if (sentinel != null)
                foreach (Renderer r in sentinel.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.sharedMaterial != null && r.sharedMaterial.HasProperty("_Color")) { col = r.sharedMaterial.name + " " + r.sharedMaterial.GetColor("_Color"); break; }
            bool allied = User.LocalUser != null && Affiliation.IsAllied(User.LocalUser.faction, altar.gameObject);
            return "preview " + altar.name + ": key=" + (shown == null ? "none" : shown.ToString())
                + " sentinelActive=" + (sentinel != null && sentinel.activeSelf) + " sentinelMat=" + col
                + " orangeTwin=" + (twin == null ? "none" : (twin.gameObject.activeSelf ? "active" : "inactive")) + " allied=" + allied;
        }

        // ------------------------------------------------------------ 7. crate / Altar item models (RecepteurDisplay twins "<name>_Orange")
        internal static string CrateModelReport()
        {
            int twins = 0, shown = 0;
            foreach (RecepteurDisplay d in UnityEngine.Object.FindObjectsOfType<RecepteurDisplay>())
            {
                if (d == null || d.displayElements == null) continue;
                foreach (RecepteurDisplay.DisplayElement el in d.displayElements)
                {
                    GameObject go = el == null ? null : el.targetObject as GameObject;
                    if (go == null || !go.name.EndsWith("_Orange", StringComparison.Ordinal)) continue;
                    twins++; if (go.activeSelf) shown++;
                }
            }
            return "crate/Altar orange twins=" + twins + " shown=" + shown;
        }

        // ------------------------------------------------------------ 8. placed orange wards (active objects only; stored items are inactive)
        internal static string PlacedWardReport()
        {
            StringBuilder sb = new StringBuilder(); int n = 0;
            foreach (OrangeWardTag t in UnityEngine.Object.FindObjectsOfType<OrangeWardTag>())
            {
                if (t == null) continue; n++;
                NetworkIdentity ni = t.GetComponent<NetworkIdentity>();
                sb.Append(" | ").Append(t.name).Append(" gem=").Append(GemOf(t.gameObject)).Append(" id=").Append(ni == null ? "-" : ni.assetId.ToString());
            }
            return "orange wards (active)=" + n + sb;
        }

        // ------------------------------------------------------------ 9. evidence: UNet spawn failures of the orange assets on THIS PC (guest without the clones)
        internal static int SpawnFails;
        static bool watching;
        internal static void WatchSpawnFailures()
        {
            if (watching) return;
            watching = true;
            Application.logMessageReceived += delegate(string text, string stack, LogType type)
            {
                if (type != LogType.Error || text == null || !text.StartsWith("Failed to spawn server object", StringComparison.Ordinal)) return;
                if (text.Contains("2fb84edc6f73c4497db5906835b5c863") || text.Contains("ae0a3f0fac002a6f76ffafa915a5fecf") || text.Contains("2dfc9e7ac52633e9f9ddd17e1721b1fc")) SpawnFails++;
            };
        }

        // ------------------------------------------------------------ 10. evidence: new ORANGE-WARD lines of perf-trace.txt (flushed every 3 s). Never blocks the writer or its rollover.
        internal static List<string> NewOrangeLines(ref long offset)
        {
            List<string> res = new List<string>();
            string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "perf-trace.txt");
            try
            {
                byte[] buf;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (offset > len) offset = 0;   // rolled over (old text is now perf-trace.1.txt)
                    buf = new byte[(int)(len - offset)];
                    fs.Seek(offset, SeekOrigin.Begin);
                    int got = 0;
                    while (got < buf.Length) { int k = fs.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                    int last = Array.LastIndexOf(buf, (byte)10, Math.Max(0, got - 1));
                    if (got == 0 || last < 0) return res;
                    offset += last + 1;   // only whole lines
                    string text = Encoding.UTF8.GetString(buf, 0, last + 1);
                    foreach (string line in text.Split('\n'))
                        if (line.IndexOf(" ORANGE-WARD", StringComparison.Ordinal) >= 0) res.Add(line.TrimEnd('\r'));
                }
            }
            catch (Exception) { }
            return res;
        }
    }
}
