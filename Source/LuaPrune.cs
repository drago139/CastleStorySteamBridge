using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.85 B-051 Lua weak-reference leak (found by heap walks T-139/T-140): the game's MoonSharp Script adds a WeakReference to
    // Script._childClosures for EVERY closure and to _childTables for every table ever created and never removes dead ones (closures are
    // never deregistered; DeregisterTable scans the whole list linearly). In a match that is ~430k new WeakReferences per hour: +20-40 MB of
    // live heap, a million GC handles that every collection has to process (longer GC pauses), and ever slower table deregistration.
    // Fix: once a minute, on the main thread (where the game runs Lua), drop the entries whose target is already dead. Live entries keep
    // their order, so Script.Kill and DeregisterTable behave exactly as before. Scripts are found by patching the Script constructors.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> lpCfg; static readonly List<WeakReference> lpScripts = new List<WeakReference>();
        static FieldInfo lpClosures, lpTables; static float lpNext; static long lpRemovedTotal;

        void InstallLuaPrune()
        {
            try
            {
                lpCfg = Config.Bind("Game", "LuaWeakRefPrune", true, "Once a minute remove dead entries from the Lua engine's ever-growing weak-reference lists (memory leak and long-match slowdown, B-051).");
                Type script = AccessTools.TypeByName("MoonSharp.Interpreter.Script");
                if (script == null) { Logger.LogWarning("LUA-PRUNE: MoonSharp Script not found"); return; }
                lpClosures = AccessTools.Field(script, "_childClosures"); lpTables = AccessTools.Field(script, "_childTables");
                if (lpClosures == null || lpTables == null || lpClosures.FieldType != typeof(List<WeakReference>) || lpTables.FieldType != typeof(List<WeakReference>)) { Logger.LogWarning("LUA-PRUNE: weak-reference lists not found (different MoonSharp build)"); return; }
                HarmonyMethod post = new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("LpAfterCtor", BindingFlags.Static | BindingFlags.NonPublic));
                int n = 0; foreach (ConstructorInfo c in script.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) { discoveryHarmony.Patch(c, null, post); n++; }
                Logger.LogInfo("LUA-PRUNE ready (" + n + " constructors, " + (lpCfg.Value ? "on" : "off") + ")");
            }
            catch (Exception e) { Logger.LogWarning("LUA-PRUNE unavailable: " + e.Message); }
        }

        static void LpAfterCtor(object __instance)
        {
            // 0.4.27 review: Script() chains to Script(CoreModules) and both constructors are patched, so every script was registered twice and LuaPruneTick walked its weak lists twice a minute (LUA-PRUNE scripts=4 vs LUALIVE scripts=2, same lists). Register an instance once.
            try { if (__instance != null) lock (lpScripts) { for (int i = 0; i < lpScripts.Count; i++) if (object.ReferenceEquals(lpScripts[i].Target, __instance)) return; lpScripts.Add(new WeakReference(__instance)); } } catch (Exception) { }
        }

        void LuaPruneTick()
        {
            if (lpCfg == null || !lpCfg.Value || lpClosures == null) return;
            float now = Time.realtimeSinceStartup; if (now < lpNext) return; lpNext = now + 60f;
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                int scripts = 0, cBefore = 0, cAfter = 0, tBefore = 0, tAfter = 0;
                lock (lpScripts)
                {
                    for (int i = lpScripts.Count - 1; i >= 0; i--)
                    {
                        object s = lpScripts[i].Target; if (s == null) { lpScripts.RemoveAt(i); continue; }
                        scripts++;
                        List<WeakReference> c = lpClosures.GetValue(s) as List<WeakReference>, t = lpTables.GetValue(s) as List<WeakReference>;
                        if (c != null) { cBefore += c.Count; LpPrune(c); cAfter += c.Count; }
                        if (t != null) { tBefore += t.Count; LpPrune(t); tAfter += t.Count; }
                    }
                }
                long removed = (cBefore - cAfter) + (tBefore - tAfter); lpRemovedTotal += removed;
                if (removed > 0 || sw.ElapsedMilliseconds > 5) PtLog("LUA-PRUNE scripts=" + scripts + " closures " + cBefore + "->" + cAfter + " tables " + tBefore + "->" + tAfter + " removed total " + lpRemovedTotal + " ms=" + sw.ElapsedMilliseconds);
            }
            catch (Exception e) { PtLog("LUA-PRUNE failed: " + e.Message); }
        }

        // 0.3.98 LUALIVE (log only): what do the live Lua objects look like? Every Diag/LuaLiveCensusSeconds (default 300, 0 = off) the live closures are grouped by entry point (same @id as LUACALLS, with the first
        // upvalue name as a hint) and the live tables by shape (array length, metatable). At most ~20000 entries per list are sampled (even stride) and scaled up, so the pause stays a few ms.
        static ConfigEntry<int> llCfg; static float llNext; static PropertyInfo llLoc, llLen, llMeta, llPairs, llPairKey, llDvType, llDvString; static MethodInfo llUpName, llUpCount;

        // "key1,key2,key3,... (n)" of one table: the first six keys sorted (string keys by name, others by type) tell what kind of record it is (vector, unit data, event, ...)
        static string LlSignature(object table)
        {
            try
            {
                System.Collections.IEnumerable pairs = llPairs.GetValue(table, null) as System.Collections.IEnumerable; if (pairs == null) return null;
                List<string> keys = new List<string>(); int n = 0;
                foreach (object p in pairs)
                {
                    n++; if (keys.Count >= 6) { if (n > 40) break; continue; }
                    if (llPairKey == null) llPairKey = p.GetType().GetProperty("Key");
                    object k = llPairKey.GetValue(p, null); if (k == null) { keys.Add("?"); continue; }
                    if (llDvType == null) { llDvType = k.GetType().GetProperty("Type"); llDvString = k.GetType().GetProperty("String"); }
                    string ty = llDvType.GetValue(k, null).ToString(); keys.Add(ty == "String" ? (string)llDvString.GetValue(k, null) : "#" + ty);
                }
                keys.Sort(); return string.Join(",", keys.ToArray()) + (n > 40 ? " (40+)" : " (" + n + ")");
            }
            catch (Exception) { return null; }
        }

        void LuaLiveCensusTick()
        {
            try
            {
                if (llCfg == null) llCfg = Config.Bind("Diag", "LuaLiveCensusSeconds", 180, "Every N seconds write LUALIVE to perf-trace.txt: live Lua closures by entry point and live tables by shape (finds what fills the managed heap in a long match). 0 = off. Log only.");
                if (llCfg.Value <= 0 || lpClosures == null || !ptOn || !HpInGame()) return;
                float now = Time.realtimeSinceStartup; if (now < llNext) return; llNext = now + llCfg.Value;
                if (llLoc == null)
                {
                    Type closure = AccessTools.TypeByName("MoonSharp.Interpreter.Closure"), table = AccessTools.TypeByName("MoonSharp.Interpreter.Table");
                    if (closure == null || table == null) return;
                    llLoc = closure.GetProperty("EntryPointByteCodeLocation"); llUpName = closure.GetMethod("GetUpvalueName"); llUpCount = closure.GetMethod("GetUpvaluesCount");
                    llLen = table.GetProperty("Length"); llMeta = table.GetProperty("MetaTable"); llPairs = table.GetProperty("Pairs");
                    if (llLoc == null) return;
                }
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                Dictionary<int, int> byEntry = new Dictionary<int, int>(); Dictionary<int, object> sample = new Dictionary<int, object>(); Dictionary<string, int> shapes = new Dictionary<string, int>();
                long cTotal = 0, tTotal = 0; int cSeen = 0, tSeen = 0; int scripts = 0, sigSeen = 0; Dictionary<string, int> sigs = new Dictionary<string, int>(); HashSet<int> seenScripts = new HashSet<int>(); StringBuilder scriptLines = new StringBuilder();
                lock (lpScripts)
                {
                    foreach (WeakReference sref in lpScripts)
                    {
                        object s = sref.Target; if (s == null) continue; if (!seenScripts.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s))) continue; scripts++;
                        List<WeakReference> c = lpClosures.GetValue(s) as List<WeakReference>, t = lpTables.GetValue(s) as List<WeakReference>;
                        if (c != null)
                        {
                            cTotal += c.Count; int step = Mathf.Max(1, c.Count / 20000);
                            for (int i = 0; i < c.Count; i += step)
                            {
                                object o = c[i] == null ? null : c[i].Target; if (o == null) continue; cSeen++;
                                int loc = (int)llLoc.GetValue(o, null); int k; byEntry.TryGetValue(loc, out k); byEntry[loc] = k + step; if (!sample.ContainsKey(loc)) sample[loc] = o;
                            }
                        }
                        if (t != null && llLen != null)
                        {
                            tTotal += t.Count; int step = Mathf.Max(1, t.Count / 20000);
                            for (int i = 0; i < t.Count; i += step)
                            {
                                object o = t[i] == null ? null : t[i].Target; if (o == null) continue; tSeen++;
                                int len = (int)llLen.GetValue(o, null); bool meta = llMeta != null && llMeta.GetValue(o, null) != null;
                                string key = (len == 0 ? "arr0" : len <= 4 ? "arr1-4" : len <= 32 ? "arr5-32" : len <= 1000 ? "arr33-1000" : "arr1000+") + (meta ? "+meta" : "");
                                int k; shapes.TryGetValue(key, out k); shapes[key] = k + step;
                                if (llPairs != null && sigSeen < 3000 && (tSeen & 7) == 0) { string sig = LlSignature(o); if (sig != null) { sigSeen++; int q; sigs.TryGetValue(sig, out q); sigs[sig] = q + step * 8; } }
                            }
                        }
                        scriptLines.Append(scripts > 1 ? ", " : "").Append("#").Append(scripts).Append(" ").Append(c == null ? 0 : c.Count).Append("c/").Append(t == null ? 0 : t.Count).Append("t");
                    }
                }
                List<KeyValuePair<int, int>> ents = new List<KeyValuePair<int, int>>(byEntry); ents.Sort((a, b) => b.Value.CompareTo(a.Value));
                StringBuilder sb = new StringBuilder("LUALIVE scripts=").Append(scripts).Append(" closures(weak list)=").Append(cTotal).Append(" alive~").Append(cSeen).Append(" sampled, tables(weak list)=").Append(tTotal).Append(" alive~").Append(tSeen).Append(" sampled; closures by entry: ");
                for (int i = 0; i < ents.Count && i < 8; i++)
                {
                    string hint = ""; try { object o = sample[ents[i].Key]; if (llUpCount != null && llUpName != null && (int)llUpCount.Invoke(o, null) > 0) hint = "/" + llUpName.Invoke(o, new object[] { 0 }); } catch (Exception) { }
                    if (i > 0) sb.Append(", "); sb.Append('@').Append(ents[i].Key).Append(hint).Append(" ~").Append(ents[i].Value);
                }
                List<KeyValuePair<string, int>> sh = new List<KeyValuePair<string, int>>(shapes); sh.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append("; tables by shape: "); for (int i = 0; i < sh.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(sh[i].Key).Append(" ~").Append(sh[i].Value); }
                List<KeyValuePair<string, int>> sg = new List<KeyValuePair<string, int>>(sigs); sg.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append("; per script (weak list sizes): ").Append(scriptLines);
                sb.Append("; table kinds by keys (").Append(sigSeen).Append(" sampled): "); for (int i = 0; i < sg.Count && i < 10; i++) { if (i > 0) sb.Append(" | "); sb.Append(sg[i].Key).Append(" ~").Append(sg[i].Value); }
                sb.Append("; managed heap ").Append(GC.GetTotalMemory(false) / 1048576).Append(" MB, gc0 ").Append(GC.CollectionCount(0)).Append(", gc2 ").Append(GC.CollectionCount(2));
                sb.Append("; ms=").Append(sw.ElapsedMilliseconds);
                PtLog(sb.ToString());
            }
            catch (Exception e) { PtLog("LUALIVE failed: " + e.Message); }
        }

        // removes dead entries in place, keeping the order of the live ones; gives memory back when the list shrank a lot
        static void LpPrune(List<WeakReference> list)
        {
            int w = 0, n = list.Count;
            for (int r = 0; r < n; r++) { WeakReference x = list[r]; if (x != null && x.IsAlive) { if (w != r) list[w] = x; w++; } }
            if (w < n) list.RemoveRange(w, n - w);
            if (list.Capacity > 4096 && list.Count < list.Capacity / 4) list.TrimExcess();
        }
    }
}
