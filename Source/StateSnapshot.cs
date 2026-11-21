using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Brix.Engine;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.84 STATE SNAPSHOT (test mode only): tells real state divergence from visual lag.
    // Virtual player command "snapshot" (sent to host and guest at the same moment after a quiet period) writes BepInEx\state-snapshot-<utc>.txt:
    //   OBJ netId|name|x,y,z|component=hash ...  for every network object; the hash is FNV-1a of what the component itself serializes for the
    //                                           network (OnSerialize, forceAll) = exactly the synchronized state as this machine holds it.
    //                                           UNetTransform is left out of the hash (interpolated position; positions are compared with a tolerance).
    //   VOX x,y,z|blockId|levels|damaged        for every voxel this machine changed since the level loaded (Architecte.Changement hook, log-only).
    // Compare-Snapshots.ps1 pairs host and guest files: missing objects, differing component hashes, position gaps, voxel differences.
    public sealed partial class SteamBridgePlugin
    {
        static readonly HashSet<string> ssChanged = new HashSet<string>(); static readonly Dictionary<string, object> ssChangedPos = new Dictionary<string, object>();
        static bool ssHooked;

        void InstallStateSnapshot()
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(Architecte), "Changement");
                if (m == null) { Logger.LogWarning("STATE-SNAPSHOT: Architecte.Changement not found"); return; }
                discoveryHarmony.Patch(m, null, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("SsAfterChange", BindingFlags.Static | BindingFlags.NonPublic)));
                ssHooked = true;
            }
            catch (Exception e) { Logger.LogWarning("STATE-SNAPSHOT unavailable: " + e.Message); }
        }

        static void SsAfterChange(object __0)
        {
            try
            {
                if (!TestOnStatic || __0 == null || ssChangedPos.Count > 200000) return;
                string k = __0.ToString(); if (!ssChangedPos.ContainsKey(k)) ssChangedPos[k] = __0;
            }
            catch (Exception) { }
        }
        static bool TestOnStatic { get { SteamBridgePlugin p = discoveryInstance; return p != null && p.TestOn; } }

        // Components whose UNET hook does not store the received value (updateId / state stay at their spawn value on clients): compare the fields that carry the game state.
        static string SsSpecial(NetworkBehaviour nb)
        {
            string n = nb.GetType().Name;
            if (nb is Brix.Game.Network.GenericParentTracker) { object pair = Traverse.Create(nb).Field("currentTracker").GetValue(); return "tracker " + SsDescribe(pair); }
            if (n != "CharacterState" && n != "CatapultData" && n != "UNetAnimator") return null;
            Traverse tr = Traverse.Create(nb);
            if (n == "CharacterState") { object op = tr.Field("operable").GetValue(); return tr.Field("autonomyID").GetValue() + "|" + tr.Field("thoughtId").GetValue() + "|" + tr.Field("ammoCount").GetValue() + "|" + (op is GameObject && (GameObject)op != null ? ((GameObject)op).name : "-"); }
            if (n == "CatapultData") return "flags " + tr.Field("flags").GetValue();
            return "anim " + (NetworkServer.active ? tr.Field("state").GetValue() : tr.Field("previous_state").GetValue());
        }

        // a struct/object as field=value pairs; GameObjects as their netId (0 = this machine does not have the object)
        static string SsDescribe(object o)
        {
            if (o == null) return "null"; StringBuilder sb = new StringBuilder();
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            { object v = f.GetValue(o); GameObject go = v as GameObject; string s = go != null ? "net" + (go.GetComponent<NetworkIdentity>() != null ? go.GetComponent<NetworkIdentity>().netId.Value.ToString() : "?") : (v is UnityEngine.Object && (UnityEngine.Object)v == null ? "null" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); sb.Append(f.Name).Append('=').Append(s).Append(';'); }
            return sb.ToString();
        }

        static uint Fnv(byte[] b, int n) { uint h = 2166136261; for (int i = 0; i < n; i++) { h ^= b[i]; h *= 16777619; } return h; }

        // virtual player "snapshot"
        bool VpSnapshot()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            long utc = QdUtcMs(); string role = NetworkServer.active ? "host" : NetworkClient.active ? "guest" : "none";
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "state-snapshot-" + utc + ".txt");
            int objs = 0, comps = 0, failed = 0, vox = 0;
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("SNAPSHOT utc=" + utc + " role=" + role + " level=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " voxHook=" + ssHooked);
                Dictionary<NetworkInstanceId, NetworkIdentity> objects = NetworkServer.active ? NetworkServer.objects : ClientScene.objects;
                List<NetworkIdentity> list = new List<NetworkIdentity>();
                if (objects != null) foreach (KeyValuePair<NetworkInstanceId, NetworkIdentity> kv in objects) if (kv.Value != null) list.Add(kv.Value);
                list.Sort(delegate(NetworkIdentity a, NetworkIdentity b) { return a.netId.Value.CompareTo(b.netId.Value); });
                StringBuilder sb = new StringBuilder();
                foreach (NetworkIdentity ni in list)
                {
                    objs++; sb.Length = 0;
                    Vector3 p = ni.transform.position;
                    sb.Append("OBJ ").Append(ni.netId.Value).Append('|').Append(ni.gameObject.name.Replace("(Clone)", "").Replace('|', '/').Trim()).Append('|')
                      .Append(p.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(p.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(p.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                    { Brix.Game.Network.UNetTransform ut = ni.GetComponent<Brix.Game.Network.UNetTransform>(); if (ut != null) { Rigidbody rb = ni.GetComponent<Rigidbody>(); sb.Append("ut=").Append(ut.UpdateOnlyOnSleep ? "sleep" : "cont").Append(rb == null ? "" : (rb.isKinematic ? ",kin" : rb.IsSleeping() ? ",rest" : ",moving")).Append(' '); } }
                    if (NetworkServer.active) { int obs = 0; bool remote = false; try { if (ni.observers != null) { obs = ni.observers.Count; foreach (NetworkConnection c in ni.observers) if (c != null && c.GetType().Name.IndexOf("Local", StringComparison.Ordinal) < 0) remote = true; } } catch (Exception) { } sb.Append("obs=").Append(obs).Append(remote ? "+remote" : "").Append(ni.serverOnly ? "+serverOnly" : ""); }
                    foreach (NetworkBehaviour nb in ni.GetComponents<NetworkBehaviour>())
                    {
                        if (nb == null || nb is Brix.Game.Network.UNetTransform) continue;
                        try
                        {
                            string special = SsSpecial(nb);
                            if (special != null) { sb.Append(' ').Append(nb.GetType().Name).Append('=').Append(Fnv(Encoding.ASCII.GetBytes(special), special.Length).ToString("x8")); comps++; continue; }
                            NetworkWriter nw = new NetworkWriter(); nb.OnSerialize(nw, true); byte[] buf = nw.AsArray(); sb.Append(' ').Append(nb.GetType().Name).Append('=').Append(Fnv(buf, nw.Position).ToString("x8")); comps++;
                        }
                        catch (Exception) { sb.Append(' ').Append(nb.GetType().Name).Append("=ERR"); failed++; }
                    }
                    w.WriteLine(sb.ToString());
                }
                List<string> keys = new List<string>(ssChangedPos.Keys); keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys)
                {
                    try
                    {
                        IVoxel v = Voxel.GetVoxel((XYZ)ssChangedPos[k]);
                        w.WriteLine("VOX " + k.Replace(" ", "") + "|" + (v == null || v.IsNull || v.BlockInfo == null ? "null" : v.BlockInfo.ID.ToString()) + "|" + (v == null || v.IsNull ? 0 : v.Niveaux) + "|" + (v != null && !v.IsNull && v.IsDamaged));
                        vox++;
                    }
                    catch (Exception) { w.WriteLine("VOX " + k.Replace(" ", "") + "|ERR"); }
                }
                w.WriteLine("END objects=" + objs + " components=" + comps + " failed=" + failed + " voxels=" + vox + " ms=" + sw.ElapsedMilliseconds);
            }
            VpLog("#" + vpDone + " snapshot " + role + " objects=" + objs + " voxels=" + vox + " ms=" + sw.ElapsedMilliseconds + " -> " + Path.GetFileName(path));
            return true;
        }
    }
}
