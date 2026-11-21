using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Semantique;
using Brix.Legacy;
using Brix.Transactions;
using Brix.Utils;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // Local observers only. Sampled path is a lower bound, not a ballistic range.
    internal static class ResourceScatterLog
    {
        sealed class Flight
        {
            internal GameObject Go;
            internal Rigidbody Body;
            internal Vector3 Origin, Last;
            internal float Start, Path, Max, Speed;
            internal int Quiet, Serial;
            internal bool Rest;
            internal uint Net;
        }
        static readonly Dictionary<int, Flight> flights = new Dictionary<int, Flight>();
        static readonly Dictionary<int, uint> observed = new Dictionary<int, uint>();
        static readonly List<int> remove = new List<int>();
        static readonly Collider[] blastHits = new Collider[256];
        static ConfigEntry<bool> enabled;
        static Action<string> output;
        static float next, window;
        static int lines, dropped, serial;
        static GameObject thrustObject;
        static bool coverageGap;
        const int Capacity = 1024, LinesPerMinute = 2400;
        static bool On { get { return enabled != null && enabled.Value; } }
        static bool Accept
        {
            get
            {
                if (!On) return false;
                if (Time.realtimeSinceStartup < window && lines >= LinesPerMinute) { dropped++; coverageGap = true; return false; }
                return true;
            }
        }
        static string F(float v) { return v.ToString("F3", CultureInfo.InvariantCulture); }
        static string V(Vector3 p) { return F(p.x) + "," + F(p.y) + "," + F(p.z); }
        static string Role { get { return NetworkServer.active ? "host" : "guest"; } }
        static string Id(GameObject go)
        {
            NetworkIdentity n = go == null ? null : go.GetComponent<NetworkIdentity>();
            // Asset names only; never unit/player names or account identifiers.
            return " net=" + (n == null ? "0" : n.netId.Value.ToString()) + " local=" + (go == null ? 0 : go.GetInstanceID()) + " asset=" + (n == null ? "none" : n.assetId.ToString());
        }
        static void Log(string s)
        {
            if (!On || output == null) return;
            float now = Time.realtimeSinceStartup;
            if (now >= window)
            {
                if (dropped > 0) output("SCATTER " + Role + " LIMIT dropped=" + dropped);
                dropped = 0; lines = 0; window = now + 60f;
            }
            if (++lines > LinesPerMinute) { dropped++; coverageGap = true; return; }
            output("SCATTER " + Role + " " + s);
        }
        internal static void Install(Harmony h, ConfigFile cfg, Action<string> log)
        {
            output = log;
            enabled = cfg.Bind("Diag", "ResourceScatter", true, "Local resource motion logs only. No physics or network changes; SCATTER in perf-trace.txt.");
            Hook(h, typeof(Transactor), "SpawnUNet", null, "Spawned");
            Hook(h, typeof(DamagedTree), "RpcFallingLogsPostprocess", "TreeLog", null);
            Hook(h, typeof(Recepteur), "ScatterObjects", "ScatterBefore", "ScatterAfter");
            Hook(h, typeof(Recepteur), "ObjectUntracked", "Untracked", null);
            Hook(h, typeof(VolumeRegistry), "ClosestAvailableDropPosition", null, "DropPosition");
            Hook(h, typeof(KinematicProjectile), "InitBeforeSpawn", null, "Fired");
            Hook(h, typeof(KinematicProjectile), "Physicalize", null, "Physicalized");
            Hook(h, typeof(PhysicalizedProjectile), "Thrust", "Thrust", "ThrustDone");
            Hook(h, typeof(Projectile), "GetInaccurateVelocity", null, "ActualVelocity");
            Hook(h, typeof(Garnotte), "Nouvelle", "Harvest", null);
            Hook(h, typeof(Garnotte), "OnDisable", "Disabled", null);
            Hook(h, typeof(SphereCollection), "Shuffle", null, "Shuffled");
            Hook(h, typeof(ExplosionGameplay), "ApplyObjectExplosionForce", "Explosion", null);
            Log("READY logOnly=true sampleSeconds=0.5 capacity=" + Capacity + " linesPerMinute=" + LinesPerMinute);
        }
        static void Hook(Harmony h, Type type, string method, string pre, string post)
        {
            try
            {
                MethodInfo m = AccessTools.Method(type, method);
                if (m == null) throw new MissingMethodException(type.FullName, method);
                h.Patch(m, pre == null ? null : new HarmonyMethod(typeof(ResourceScatterLog), pre), post == null ? null : new HarmonyMethod(typeof(ResourceScatterLog), post));
                Log("HOOK " + type.Name + "." + method);
            }
            catch (Exception e) { Log("HOOK_ERROR " + type.Name + "." + method + " " + e.GetType().Name); }
        }
        static bool Carried(GameObject go)
        {
            Transform p = go.transform.parent;
            return p != null && p.GetComponentInParent<NetworkIdentity>() != null;
        }
        static Flight Begin(GameObject go, string why, Vector3 origin, bool replace)
        {
            if (!Accept || go == null) return null;
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) return null;
            int key = go.GetInstanceID(); Flight f;
            if (flights.TryGetValue(key, out f))
            {
                if (!replace) return f;
                End(f, "superseded"); flights.Remove(key);
            }
            if (flights.Count >= Capacity)
            {
                int oldest = 0; float age = float.MaxValue;
                foreach (KeyValuePair<int, Flight> entry in flights)
                    if (entry.Value.Rest && entry.Value.Start < age) { oldest = entry.Key; age = entry.Value.Start; }
                if (age != float.MaxValue) { End(flights[oldest], "capacity-evicted-rest"); flights.Remove(oldest); }
            }
            if (flights.Count >= Capacity) { dropped++; return null; }
            NetworkIdentity ni = go.GetComponent<NetworkIdentity>();
            f = new Flight { Go = go, Body = rb, Origin = origin, Last = go.transform.position, Start = Time.realtimeSinceStartup, Serial = ++serial, Net = ni == null ? 0 : ni.netId.Value };
            flights[key] = f;
            if (observed.Count < 16384) observed[key] = f.Net;
            Log("BEGIN event=" + f.Serial + Id(go) + " source=" + why + " origin=" + V(origin) + " pos=" + V(f.Last) + " velocity=" + V(rb.velocity) + " mass=" + F(rb.mass) + " drag=" + F(rb.drag) + " gravity=" + rb.useGravity + " kp=" + (go.GetComponent<KinematicProjectile>() != null));
            return f;
        }
        static void End(Flight f, string reason)
        {
            if (Accept) Log("END event=" + f.Serial + " capturedNet=" + f.Net + Id(f.Go) + " reason=" + reason + " pos=" + V(f.Go == null ? f.Last : f.Go.transform.position) + " sampledPath=" + F(f.Path) + " maxDisplacement=" + F(f.Max));
        }
        static void Disabled(Garnotte __instance)
        {
            try
            {
                if (__instance == null) return;
                int key = __instance.gameObject.GetInstanceID(); Flight f;
                observed.Remove(key);
                if (flights.TryGetValue(key, out f)) { End(f, "disabled-or-pooled"); flights.Remove(key); }
            }
            catch (Exception) { dropped++; }
        }
        static void Spawned(GameObject __result)
        {
            try
            {
                if (Accept && __result != null && (__result.GetComponent<Garnotte>() != null || __result.GetComponent<KinematicProjectile>() != null))
                    Begin(__result, "SpawnUNet", __result.transform.position, true);
            }
            catch (Exception) { dropped++; }
        }
        static void TreeLog(GameObject Log, Vector3 velocity)
        {
            try { if (!Accept || Log == null) return; Begin(Log, "tree-log", Log.transform.position, true); ResourceScatterLog.Log("VELOCITY" + Id(Log) + " source=tree-log value=" + V(velocity)); }
            catch (Exception) { dropped++; }
        }
        static void Fired(KinematicProjectile __instance)
        {
            try { if (!Accept || __instance == null) return; Begin(__instance.gameObject, "kinematic-fire", __instance.NetworkFiredPosition, true); Log("VELOCITY" + Id(__instance.gameObject) + " source=kinematic-fire value=" + V(__instance.NetworkFiredVelocity)); }
            catch (Exception) { dropped++; }
        }
        static void Physicalized(KinematicProjectile __instance)
        {
            try { if (!Accept || __instance == null) return; Begin(__instance.gameObject, "physicalize-observed", __instance.transform.position, false); Log("PHYSICALIZE" + Id(__instance.gameObject) + " pos=" + V(__instance.transform.position)); }
            catch (Exception) { dropped++; }
        }
        static void Thrust(PhysicalizedProjectile __instance, Vector3 initialVelocity, float inaccuracy)
        {
            thrustObject = null;
            try { if (!Accept || __instance == null) return; thrustObject = __instance.gameObject; Begin(__instance.gameObject, "physical-thrust", __instance.transform.position, true); Log("IMPULSE_INPUT" + Id(__instance.gameObject) + " mode=VelocityChange requested=" + V(initialVelocity) + " inaccuracy=" + F(inaccuracy)); }
            catch (Exception) { dropped++; }
        }
        static void ThrustDone() { thrustObject = null; }
        static void ActualVelocity(Vector3 __result)
        {
            try { if (Accept && thrustObject != null) Log("IMPULSE_ACTUAL" + Id(thrustObject) + " mode=VelocityChange deltaVelocity=" + V(__result)); }
            catch (Exception) { dropped++; }
            finally { thrustObject = null; }
        }
        static void Harvest(Vector3 position, int qte)
        {
            try { if (Accept) Log("HARVEST_REQUEST origin=" + V(position) + " count=" + qte); }
            catch (Exception) { dropped++; }
        }
        static void Shuffled(SphereCollection __instance, Vector3 center)
        {
            try
            {
                if (!Accept) return;
                float max = 0f, radius = 0f;
                foreach (SphereCollection.Sphere s in __instance.Spheres) { max = Mathf.Max(max, Vector3.Distance(s.center, center)); radius = Mathf.Max(radius, s.radius); }
                Log("SHUFFLE count=" + __instance.Spheres.Count + " center=" + V(center) + " maxPlacementDistance=" + F(max) + " largestColliderRadius=" + F(radius));
            }
            catch (Exception) { dropped++; }
        }
        static void Untracked(Recepteur __instance, GameObject itemGO)
        {
            try { if (!Accept || itemGO == null) return; Log("UNTRACK" + Id(itemGO) + " container" + Id(__instance.gameObject) + " containerPos=" + V(__instance.transform.position) + " itemPos=" + V(itemGO.transform.position)); }
            catch (Exception) { dropped++; }
        }
        static void DropPosition(Transform reference, int objectCount, bool forceDrop, Vector3 __result)
        {
            try { if (Accept && reference != null) Log("DROP_CENTER" + Id(reference.gameObject) + " count=" + objectCount + " force=" + forceDrop + " origin=" + V(reference.position) + " center=" + V(__result) + " zeroSentinel=" + (__result == Vector3.zero)); }
            catch (Exception) { dropped++; }
        }
        // Only repeat-enumerate concrete collections. Never consume a lazy game iterator.
        static void ScatterBefore(IEnumerable<GameObject> objects, Vector3 center, out List<GameObject> __state)
        {
            __state = null;
            try
            {
                if (!Accept) return;
                ICollection<GameObject> items = objects as ICollection<GameObject>;
                if (items == null) { Log("SCATTER_COLLECTION unsupported-iterator"); return; }
                __state = new List<GameObject>(Math.Min(items.Count, Capacity));
                foreach (GameObject go in items)
                {
                    if (__state.Count >= Capacity) { dropped++; break; }
                    if (go == null) continue;
                    __state.Add(go); Begin(go, "storage-or-recipe-scatter", center, true);
                    Log("PLACE_BEFORE" + Id(go) + " center=" + V(center) + " pos=" + V(go.transform.position));
                }
            }
            catch (Exception) { dropped++; }
        }
        static void ScatterAfter(Vector3 center, List<GameObject> __state)
        {
            try
            {
                if (!Accept || __state == null) return;
                foreach (GameObject go in __state)
                {
                    if (go == null) continue;
                    Flight f; if (flights.TryGetValue(go.GetInstanceID(), out f)) f.Last = go.transform.position;
                    Log("PLACE_AFTER" + Id(go) + " center=" + V(center) + " pos=" + V(go.transform.position) + " placementDistance=" + F(Vector3.Distance(center, go.transform.position)));
                }
            }
            catch (Exception) { dropped++; }
        }
        static void Explosion(ExplosionGameplay __instance, Vector3 explosionCenter)
        {
            try
            {
                if (!Accept || __instance == null || __instance.objectImpulseCurve == null) return;
                Vector3 forceCenter = __instance.voxel.position.ToVector3();
                int n = Physics.OverlapSphereNonAlloc(forceCenter, __instance.objectImpulseCurve.maxRange, blastHits, 24576);
                if (n == blastHits.Length) Log("EXPLOSION observer-buffer-full");
                for (int i = 0; i < n; i++)
                {
                    Collider c = blastHits[i]; if (c == null) continue;
                    Rigidbody rb = c.GetComponent<Rigidbody>(); if (rb == null || rb.isKinematic) continue;
                    float force = __instance.objectImpulseCurve.Evaluate(Vector3.Distance(c.transform.position, explosionCenter));
                    Begin(c.gameObject, "explosion-observed", c.transform.position, false);
                    Log("IMPULSE_INPUT" + Id(c.gameObject) + " source=explosion mode=Impulse force=" + F(force) + " radius=" + F(force * 3f) + " up=2 center=" + V(forceCenter) + " predictedCall=true");
                }
            }
            catch (Exception) { dropped++; }
            finally { Array.Clear(blastHits, 0, blastHits.Length); }
        }
        internal static void Sync(GameObject go, Vector3 target, string stage, uint missingId = 0)
        {
            try
            {
                if (!Accept) return;
                if (go == null) { Log("SYNC_" + stage + " net=" + missingId + " missing=true target=" + V(target)); return; }
                Log("SYNC_" + stage + Id(go) + " pos=" + V(go.transform.position) + " target=" + V(target) + " gap=" + F(Vector3.Distance(go.transform.position, target)) + " carried=" + Carried(go));
                Flight f;
                if (stage == "APPLIED" && flights.TryGetValue(go.GetInstanceID(), out f))
                {
                    End(f, "authoritative-correction"); flights.Remove(go.GetInstanceID());
                    Begin(go, "after-sync", target, false);
                }
            }
            catch (Exception) { dropped++; }
        }
        internal static void Tick()
        {
            if (!On) { flights.Clear(); observed.Clear(); return; }
            float now = Time.realtimeSinceStartup; if (now < next) return; next = now + 0.5f;
            if (!Accept) return;
            try
            {
                if (!NetworkServer.active && !NetworkClient.active) { flights.Clear(); observed.Clear(); return; }
                if (coverageGap)
                {
                    int incomplete = flights.Count; flights.Clear(); observed.Clear(); coverageGap = false;
                    Log("COVERAGE_GAP discardedIncompleteTrajectories=" + incomplete + " reason=rate-limit");
                }
                // Bound lifetime cache. An explicit reset means the next origins are observations again.
                if (observed.Count >= 16384) { observed.Clear(); Log("COVERAGE lifetime-cache-reset"); }
                // Fallback covers digging and fog spawns. It is explicitly not an exact spawn time.
                var all = NetworkServer.active ? NetworkServer.objects : ClientScene.objects;
                foreach (NetworkIdentity ni in all.Values)
                {
                    if (ni == null || !ni.gameObject.activeInHierarchy || flights.ContainsKey(ni.gameObject.GetInstanceID())) continue;
                    uint seenNet;
                    if (observed.TryGetValue(ni.gameObject.GetInstanceID(), out seenNet) && seenNet == ni.netId.Value) continue;
                    if (ni.GetComponent<Garnotte>() == null && ni.GetComponent<KinematicProjectile>() == null) continue;
                    if (!Carried(ni.gameObject)) Begin(ni.gameObject, "first-observed-not-spawn", ni.transform.position, false);
                }
                remove.Clear();
                foreach (KeyValuePair<int, Flight> kv in flights)
                {
                    Flight f = kv.Value; GameObject go = f.Go;
                    if (go == null || f.Body == null || !go.activeInHierarchy) { End(f, "gone-or-pooled"); remove.Add(kv.Key); continue; }
                    NetworkIdentity identity = go.GetComponent<NetworkIdentity>();
                    uint currentNet = identity == null ? 0 : identity.netId.Value;
                    if (f.Net != 0 && currentNet != f.Net) { End(f, "network-lifetime-changed"); remove.Add(kv.Key); continue; }
                    f.Net = currentNet;
                    if (Carried(go)) { End(f, "carried-or-stored"); observed.Remove(kv.Key); remove.Add(kv.Key); continue; }
                    Vector3 p = go.transform.position;
                    float step = Vector3.Distance(p, f.Last); f.Path += step; f.Last = p;
                    f.Max = Mathf.Max(f.Max, Vector3.Distance(p, f.Origin)); f.Speed = Mathf.Max(f.Speed, f.Body.velocity.magnitude);
                    KinematicProjectile kp = go.GetComponent<KinematicProjectile>();
                    bool quiet = step < 0.02f && (kp == null || !kp.enabled || kp.NetworkFiredPosition == Vector3.zero || !f.Body.isKinematic) && (f.Body.isKinematic || f.Body.IsSleeping() || (f.Body.velocity.sqrMagnitude < 0.01f && f.Body.angularVelocity.sqrMagnitude < 0.01f));
                    f.Quiet = quiet ? f.Quiet + 1 : 0;
                    if (!quiet && f.Rest) { f.Rest = false; Log("MOVED_AGAIN event=" + f.Serial + Id(go) + " pos=" + V(p)); }
                    if (f.Quiet >= 3 && !f.Rest)
                    {
                        f.Rest = true;
                        Log("REST_LOCAL event=" + f.Serial + Id(go) + " origin=" + V(f.Origin) + " pos=" + V(p) + " displacement=" + F(Vector3.Distance(p, f.Origin)) + " sampledPath=" + F(f.Path) + " maxDisplacement=" + F(f.Max) + " peakSampledSpeed=" + F(f.Speed) + " seconds=" + F(now - f.Start) + " kinematic=" + f.Body.isKinematic);
                    }
                }
                foreach (int key in remove) flights.Remove(key);
                // Flush limit accounting even when no further events arrive.
                if (dropped > 0 && now >= window) Log("COVERAGE active=" + flights.Count);
            }
            catch (Exception e) { Log("OBSERVER_ERROR " + e.GetType().Name); }
        }
    }
}
