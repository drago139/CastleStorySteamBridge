using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.83 MODMEM (B-010 / T-123: the managed heap grows by GBs in long idle matches; is it the mod or the game?). LOG ONLY.
    // Every ModMemMinutes (default 60, first one 5 min after start) it walks, by reflection, every static field of every type in this
    // assembly and the plugin instance's fields (two levels into the mod's own objects, e.g. PingPeer.Out inside pings) and logs the
    // element count of each collection (ICollection / StringBuilder length), the biggest ones and the ones that grew most since the
    // previous line, plus the game's network object counts and the managed heap. A mod collection that grows hour after hour = a mod leak;
    // flat mod numbers while the heap grows = the game. Costs a few ms once an hour; nothing is changed.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<int> mmCfg; static float mmNext = 300f; static Dictionary<string, long> mmPrev, gmPrev; static int mmRuns; static Func<Type, bool> mmDeep = MmIsMod; static bool mmGame; static Assembly[] gmAsms;

        void InstallModMem()
        {
            try { InstallModMemTest(); mmCfg = Config.Bind("Diag", "ModMemMinutes", 60, "Every N minutes log MODMEM lines in perf-trace.txt: sizes of all mod collections and game network object counts (finds memory leaks). 0 = off."); }
            catch (Exception e) { Logger.LogWarning("MODMEM unavailable: " + e.Message); }
        }

        void ModMemTick()
        {
            if (mmCfg == null || mmCfg.Value <= 0) return;
            float now = Time.realtimeSinceStartup; if (now < mmNext) return;
            mmNext = now + mmCfg.Value * 60f;
            try { LiveMemRun(); } catch (Exception e) { PtLog("LIVEMEM failed: " + e.Message); }
            try { mmDeep = MmIsMod; mmGame = false; ModMemRun(); } catch (Exception e) { PtLog("MODMEM failed: " + e.Message); }
            if (TestOn) try { mmDeep = GmIsGame; mmGame = true; GameMemRun(); } catch (Exception e) { PtLog("GAMEMEM failed: " + e.Message); }
            mmDeep = MmIsMod; mmGame = false;
            try { SceneMemRun(); } catch (Exception e) { PtLog("SCENEMEM failed: " + e.Message); }
        }

        void ModMemRun()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Dictionary<string, long> cur = new Dictionary<string, long>();
            BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Assembly asm = typeof(SteamBridgePlugin).Assembly; Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (Type t in types)
            {
                if (t == null || t.ContainsGenericParameters) continue;
                FieldInfo[] fs; try { fs = t.GetFields(st); } catch (Exception) { continue; }
                foreach (FieldInfo f in fs)
                {
                    if (f.IsLiteral) continue;
                    object v; try { v = f.GetValue(null); } catch (Exception) { continue; }
                    MmVisit(cur, t.Name + "." + f.Name, v, 0);
                }
            }
            BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (FieldInfo f in typeof(SteamBridgePlugin).GetFields(inst))
            {
                object v; try { v = f.GetValue(this); } catch (Exception) { continue; }
                MmVisit(cur, "plugin." + f.Name, v, 0);
            }
            long total = 0; foreach (KeyValuePair<string, long> kv in cur) total += kv.Value;
            List<KeyValuePair<string, long>> big = new List<KeyValuePair<string, long>>(cur); big.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            List<KeyValuePair<string, long>> grew = new List<KeyValuePair<string, long>>();
            if (mmPrev != null) foreach (KeyValuePair<string, long> kv in cur) { long p; mmPrev.TryGetValue(kv.Key, out p); if (kv.Value - p > 0) grew.Add(new KeyValuePair<string, long>(kv.Key, kv.Value - p)); }
            grew.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            int server = -1, client = -1;
            try { if (NetworkServer.active && NetworkServer.objects != null) server = NetworkServer.objects.Count; } catch (Exception) { }
            try { if (ClientScene.objects != null) client = ClientScene.objects.Count; } catch (Exception) { }
            mmRuns++;
            StringBuilder sb = new StringBuilder("MODMEM #").Append(mmRuns).Append(" managed=").Append(GC.GetTotalMemory(false) >> 20).Append("MB netObjects server=").Append(server).Append(" client=").Append(client)
                .Append(" unitTransforms=").Append(posRegistry.Count).Append(" collections=").Append(cur.Count).Append(" items=").Append(total).Append(" scanMs=").Append(sw.ElapsedMilliseconds).Append(" biggest:");
            for (int i = 0; i < big.Count && i < 15; i++) sb.Append(' ').Append(big[i].Key).Append('=').Append(big[i].Value);
            PtLog(sb.ToString());
            if (mmPrev != null)
            {
                StringBuilder g = new StringBuilder("MODMEM-GREW since previous:");
                for (int i = 0; i < grew.Count && i < 15; i++) g.Append(' ').Append(grew[i].Key).Append("=+").Append(grew[i].Value);
                if (grew.Count == 0) g.Append(" nothing");
                PtLog(g.ToString());
            }
            mmPrev = cur;
        }


        // TEST MODE ONLY ([Test] Enabled and the two [Diag] switches below). Both change timing (one forced full GC, one long scan), never game state.
        // LIVEMEM: GC.GetTotalMemory before and after a forced full collection. Flat "after" over hours = no leak (the GC is only lazy);
        //          a rising "after" = memory that is really still referenced.
        // SCENEMEM: every live MonoBehaviour of the game's assemblies, grouped by type: number of instances and the summed size of their
        //          collections / arrays / strings (one more level into the game's own objects they hold). Shows which kind of object grows.
        static ConfigEntry<bool> mmForceGcCfg, mmSceneCfg; static Dictionary<string, long> smPrevItems, smPrevCount; static long mmPrevLive = -1;

        void InstallModMemTest()
        {
            mmForceGcCfg = Config.Bind("Diag", "ModMemForcedGc", false, "TEST MODE ONLY: before each MODMEM run force a full GC and log the live heap (LIVEMEM). Costs one ~1 s pause per run.");
            mmSceneCfg = Config.Bind("Diag", "ModMemScene", false, "TEST MODE ONLY: count the game's MonoBehaviours per type with the size of their collections (SCENEMEM). Costs a pause per run.");
        }

        void LiveMemRun()
        {
            if (!TestOn || mmForceGcCfg == null || !mmForceGcCfg.Value) return;
            long before = GC.GetTotalMemory(false); System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            long live = GC.GetTotalMemory(true); long ms = sw.ElapsedMilliseconds;
            PtLog("LIVEMEM before=" + (before >> 20) + "MB afterFullGC=" + (live >> 20) + "MB freed=" + ((before - live) >> 20) + "MB gcMs=" + ms + (mmPrevLive >= 0 ? " liveChange=" + ((live - mmPrevLive) >> 20) + "MB" : ""));
            mmPrevLive = live;
        }

        void SceneMemRun()
        {
            if (!TestOn || mmSceneCfg == null || !mmSceneCfg.Value) return;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            if (gmAsms == null) { mmDeep = GmIsGame; GameMemRun(); }
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(typeof(MonoBehaviour));
            Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
            Dictionary<string, long> items = new Dictionary<string, long>(), count = new Dictionary<string, long>();
            int objects = 0;
            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour mb = all[i] as MonoBehaviour; if (mb == null) continue;
                Type t = mb.GetType(); if (!GmIsGame(t)) continue;
                objects++; string tn = t.Name; long c; count.TryGetValue(tn, out c); count[tn] = c + 1;
                long sum = SmSize(mb, t, fieldCache, 0);
                long s0; items.TryGetValue(tn, out s0); items[tn] = s0 + sum;
            }
            List<KeyValuePair<string, long>> big = new List<KeyValuePair<string, long>>(items); big.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            StringBuilder sb = new StringBuilder("SCENEMEM monoBehaviours=").Append(all.Length).Append(" game=").Append(objects).Append(" types=").Append(count.Count).Append(" scanMs=").Append(sw.ElapsedMilliseconds).Append(" biggest(type=instances/items):");
            for (int i = 0; i < big.Count && i < 25; i++) sb.Append(' ').Append(big[i].Key).Append('=').Append(count[big[i].Key]).Append('/').Append(big[i].Value);
            PtLog(sb.ToString());
            if (smPrevItems != null)
            {
                List<KeyValuePair<string, long>> grew = new List<KeyValuePair<string, long>>();
                foreach (KeyValuePair<string, long> kv in items) { long p; smPrevItems.TryGetValue(kv.Key, out p); if (kv.Value - p > 0) grew.Add(new KeyValuePair<string, long>(kv.Key, kv.Value - p)); }
                grew.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
                StringBuilder g = new StringBuilder("SCENEMEM-GREW items(instances change):");
                for (int i = 0; i < grew.Count && i < 25; i++) { long pc; smPrevCount.TryGetValue(grew[i].Key, out pc); long nc; count.TryGetValue(grew[i].Key, out nc); g.Append(' ').Append(grew[i].Key).Append("=+").Append(grew[i].Value).Append('(').Append(nc - pc >= 0 ? "+" : "").Append(nc - pc).Append(')'); }
                List<KeyValuePair<string, long>> more = new List<KeyValuePair<string, long>>();
                foreach (KeyValuePair<string, long> kv in count) { long p; smPrevCount.TryGetValue(kv.Key, out p); if (kv.Value - p > 0) more.Add(new KeyValuePair<string, long>(kv.Key, kv.Value - p)); }
                more.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
                g.Append(" | more instances:"); for (int i = 0; i < more.Count && i < 15; i++) g.Append(' ').Append(more[i].Key).Append("=+").Append(more[i].Value);
                PtLog(g.ToString());
            }
            smPrevItems = items; smPrevCount = count;
        }

        // size of one object's data: collection Count, array Length, string Length; one level deeper into the game's own (non-Unity) objects
        static long SmSize(object o, Type t, Dictionary<Type, FieldInfo[]> cache, int depth)
        {
            FieldInfo[] fs;
            if (!cache.TryGetValue(t, out fs))
            {
                List<FieldInfo> l = new List<FieldInfo>();
                for (Type x = t; x != null && GmIsGame(x); x = x.BaseType)
                    foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (!f.FieldType.IsPrimitive && !f.FieldType.IsEnum && !f.FieldType.IsValueType) l.Add(f);
                fs = l.ToArray(); cache[t] = fs;
            }
            long sum = 0;
            for (int i = 0; i < fs.Length; i++)
            {
                object v; try { v = fs[i].GetValue(o); } catch (Exception) { continue; }
                if (v == null) continue;
                string s = v as string; if (s != null) { sum += s.Length; continue; }
                ICollection c = v as ICollection; if (c != null) { sum += c.Count; continue; }
                if (depth == 0 && !(v is UnityEngine.Object) && !(v is Delegate) && GmIsGame(v.GetType())) sum += SmSize(v, v.GetType(), cache, 1);
            }
            return sum;
        }

        static bool MmIsMod(Type t) { return t != null && t.Namespace == "CastleStorySteam" && !t.IsEnum && !t.IsPrimitive; }

        // GAMEMEM: the same walk over the GAME's code (Assembly-CSharp-firstpass, Assembly-CSharp, UnityEngine.Networking): every static field,
        // two levels into the game's own objects (singletons, managers). Logs the biggest collections, the ones that grew since the previous run,
        // and "(dead)" = destroyed Unity objects a collection still holds. Log only.
        static bool GmIsGame(Type t)
        {
            if (t == null || t.IsEnum || t.IsPrimitive || gmAsms == null) return false;
            Assembly a = t.Assembly; for (int i = 0; i < gmAsms.Length; i++) if (gmAsms[i] == a) return true;
            return false;
        }

        void GameMemRun()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            if (gmAsms == null)
            {
                List<Assembly> l = new List<Assembly>();
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { string n = a.GetName().Name; if (n == "Assembly-CSharp-firstpass" || n == "Assembly-CSharp" || n == "UnityEngine.Networking") l.Add(a); }
                gmAsms = l.ToArray();
            }
            Dictionary<string, long> cur = new Dictionary<string, long>();
            BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            int fields = 0;
            foreach (Assembly asm in gmAsms)
            {
                Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (Type t in types)
                {
                    if (t == null || t.ContainsGenericParameters) continue;
                    FieldInfo[] fs; try { fs = t.GetFields(st); } catch (Exception) { continue; }
                    foreach (FieldInfo f in fs)
                    {
                        if (f.IsLiteral || f.FieldType.IsPrimitive || f.FieldType.IsEnum) continue;
                        object v; try { v = f.GetValue(null); } catch (Exception) { continue; }
                        fields++; MmVisit(cur, t.Name + "." + f.Name, v, 0);
                    }
                }
            }
            long total = 0; foreach (KeyValuePair<string, long> kv in cur) total += kv.Value;
            List<KeyValuePair<string, long>> big = new List<KeyValuePair<string, long>>(cur); big.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            StringBuilder sb = new StringBuilder("GAMEMEM #").Append(mmRuns).Append(" staticFields=").Append(fields).Append(" collections=").Append(cur.Count).Append(" items=").Append(total).Append(" scanMs=").Append(sw.ElapsedMilliseconds).Append(" biggest:");
            for (int i = 0; i < big.Count && i < 25; i++) sb.Append(' ').Append(big[i].Key).Append('=').Append(big[i].Value);
            PtLog(sb.ToString());
            if (gmPrev != null)
            {
                List<KeyValuePair<string, long>> grew = new List<KeyValuePair<string, long>>();
                foreach (KeyValuePair<string, long> kv in cur) { long p; gmPrev.TryGetValue(kv.Key, out p); if (kv.Value - p > 0) grew.Add(new KeyValuePair<string, long>(kv.Key, kv.Value - p)); }
                grew.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
                StringBuilder g = new StringBuilder("GAMEMEM-GREW since previous:");
                for (int i = 0; i < grew.Count && i < 25; i++) g.Append(' ').Append(grew[i].Key).Append("=+").Append(grew[i].Value);
                if (grew.Count == 0) g.Append(" nothing");
                PtLog(g.ToString());
            }
            gmPrev = cur;
        }

        static void MmVisit(Dictionary<string, long> cur, string name, object v, int depth)
        {
            if (v == null || depth > 2) return;
            if (v is string || v is Delegate || (v is UnityEngine.Object && !mmDeep(v.GetType()))) return;
            if (v is StringBuilder) { cur[name] = ((StringBuilder)v).Length; return; }
            if (v is Array) { Array a = (Array)v; if (a.Length > 4096) cur[name] = a.Length; return; }   // fixed buffers: only big ones are worth a line
            ICollection c = v as ICollection;
            if (c != null)
            {
                cur[name] = c.Count;
                if (mmGame && depth == 0 && c.Count > 0 && c.Count <= 200000)
                {
                    // destroyed Unity objects still held by a collection = the classic Unity leak: count them
                    int dead = 0; IEnumerable all = v is IDictionary ? (IEnumerable)((IDictionary)v).Values : c;
                    foreach (object o in all) { UnityEngine.Object uo = o as UnityEngine.Object; if (uo != null && uo == null) dead++; }
                    if (v is IDictionary) foreach (object o in ((IDictionary)v).Keys) { UnityEngine.Object uo = o as UnityEngine.Object; if (uo != null && uo == null) dead++; }
                    if (dead > 0) cur[name + "(dead)"] = dead;
                }
                // the mod's own objects inside a collection (e.g. PingPeer in pings): sum their collections, first 200 elements
                if (depth < 2)
                {
                    IEnumerable items = v is IDictionary ? (IEnumerable)((IDictionary)v).Values : c;
                    int n = 0;
                    foreach (object o in items) { if (++n > 200) break; if (o != null && mmDeep(o.GetType())) MmFields(cur, name + "[]", o, depth + 1); }
                }
                return;
            }
            if (mmDeep(v.GetType()) && !v.GetType().IsValueType) MmFields(cur, name, v, depth + 1);
        }

        static void MmFields(Dictionary<string, long> cur, string name, object o, int depth)
        {
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType.IsPrimitive || f.FieldType.IsEnum) continue;
                object v; try { v = f.GetValue(o); } catch (Exception) { continue; }
                string key = name + "." + f.Name;
                if (v is ICollection || v is StringBuilder) { long before; cur.TryGetValue(key, out before); Dictionary<string, long> tmp = new Dictionary<string, long>(); MmVisit(tmp, key, v, depth); long add; if (tmp.TryGetValue(key, out add)) cur[key] = before + add; foreach (KeyValuePair<string, long> kv in tmp) if (kv.Key != key) { long b2; cur.TryGetValue(kv.Key, out b2); cur[kv.Key] = b2 + kv.Value; } }
                else if (depth < 2 && v != null && mmDeep(v.GetType()) && !v.GetType().IsValueType) MmFields(cur, key, v, depth + 1);
            }
        }
    }
}
