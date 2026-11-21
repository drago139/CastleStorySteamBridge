#if HEAPWALK
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.84 HEAP WALK (test mode only, virtual player command "heapwalk"): what the live managed heap consists of and WHO holds it.
    // Walks every object reachable from (a) every static field of every loaded assembly and (b) every loaded MonoBehaviour / ScriptableObject,
    // following reference fields, arrays and reference fields inside structs (Dictionary/HashSet entries). Each object is counted once, for
    // the first root that reached it. Sizes are estimates (header 16 B + 8 B per field, arrays by element size, strings 2 B per char).
    // Writes BepInEx\heapwalk-<utc>.txt: totals, top 60 types by bytes, top 60 roots by bytes. Two walks an hour apart show what grows.
    // Bounded: stops at 25 million objects or 120 s. Freezes the game while it runs (seconds to a minute); never run it in a real match.
    public sealed partial class SteamBridgePlugin
    {
        sealed class RefEq : IEqualityComparer<object> { public new bool Equals(object a, object b) { return ReferenceEquals(a, b); } public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); } }
        sealed class HwType { internal Func<object, object>[] Refs; internal string[] RefNames; internal FieldInfo[] StructFields; internal int Fields; internal long Size; }
        static readonly Dictionary<Type, HwType> hwTypes = new Dictionary<Type, HwType>();

        static HwType HwInfo(Type t)
        {
            HwType h; if (hwTypes.TryGetValue(t, out h)) return h;
            h = new HwType(); List<Func<object, object>> refs = new List<Func<object, object>>(); List<string> names = new List<string>(); List<FieldInfo> structs = new List<FieldInfo>(); int n = 0;
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    n++; Type ft = f.FieldType;
                    if (ft.IsPointer || ft == typeof(IntPtr) || ft == typeof(UIntPtr)) continue;
                    if (!ft.IsValueType) { Func<object, object> g = HwGetter(f); if (g != null) { refs.Add(g); names.Add(x.Name + "." + f.Name); } }
                    else if (!ft.IsPrimitive && !ft.IsEnum && HwHasRefs(ft, 0)) structs.Add(f);
                }
            h.Refs = refs.ToArray(); h.RefNames = names.ToArray(); h.StructFields = structs.ToArray(); h.Fields = n; h.Size = 16 + 8L * n; hwTypes[t] = h; return h;
        }

        static readonly Dictionary<Type, bool> hwHasRefs = new Dictionary<Type, bool>();
        static bool HwHasRefs(Type t, int depth)
        {
            bool r; if (hwHasRefs.TryGetValue(t, out r)) return r; r = false;
            if (depth < 3) foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                { Type ft = f.FieldType; if (ft.IsPointer) continue; if (!ft.IsValueType || (!ft.IsPrimitive && !ft.IsEnum && HwHasRefs(ft, depth + 1))) { r = true; break; } }
            hwHasRefs[t] = r; return r;
        }

        static Func<object, object> HwGetter(FieldInfo f)
        {
            try
            {
                DynamicMethod dm = new DynamicMethod("hw_" + f.Name, typeof(object), new Type[] { typeof(object) }, typeof(SteamBridgePlugin).Module, true);
                ILGenerator il = dm.GetILGenerator(); il.Emit(OpCodes.Ldarg_0);
                if (f.DeclaringType.IsValueType) il.Emit(OpCodes.Unbox, f.DeclaringType); else il.Emit(OpCodes.Castclass, f.DeclaringType);
                il.Emit(OpCodes.Ldfld, f); il.Emit(OpCodes.Ret);
                return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
            }
            catch (Exception) { return delegate(object o) { return f.GetValue(o); }; }
        }

        static long HwElemSize(Type e) { if (!e.IsValueType) return 8; if (e == typeof(byte) || e == typeof(bool) || e == typeof(sbyte)) return 1; if (e == typeof(short) || e == typeof(ushort) || e == typeof(char)) return 2; if (e == typeof(int) || e == typeof(uint) || e == typeof(float)) return 4; if (e.IsPrimitive) return 8; try { return Math.Max(1, System.Runtime.InteropServices.Marshal.SizeOf(e)); } catch (Exception) { return 16; } }

        bool VpHeapWalk()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            long before = GC.GetTotalMemory(false);
            HashSet<object> seen = new HashSet<object>(new RefEq());
            Dictionary<Type, long[]> byType = new Dictionary<Type, long[]>(); Dictionary<string, long[]> byRoot = new Dictionary<string, long[]>();
            Stack<object> stack = new Stack<object>(); long objects = 0, bytes = 0; bool capped = false;
            hwParent = new Dictionary<object, HwHold>(new RefEq()); List<object> big = new List<object>();
            Action<string, object> walk = null;
            walk = delegate(string root, object start)
            {
                if (start == null || !seen.Add(start)) return;
                hwRoot = root; if (HwTrack(start)) hwParent[start] = new HwHold("root " + root, null);
                stack.Push(start); long[] rb; if (!byRoot.TryGetValue(root, out rb)) { rb = new long[2]; byRoot[root] = rb; }
                while (stack.Count > 0)
                {
                    if (objects >= 25000000 || sw.ElapsedMilliseconds > 120000) { capped = true; stack.Clear(); return; }
                    object o = stack.Pop(); Type t = o.GetType(); long size;
                    if (t == typeof(string)) size = 20 + 2L * ((string)o).Length;
                    else if (t.IsArray)
                    {
                        Array a = (Array)o; Type e = t.GetElementType(); size = 16 + a.LongLength * HwElemSize(e);
                        if (size >= 262144) big.Add(o);
                        if (!e.IsValueType) { foreach (object x in a) if (x != null && seen.Add(x)) { stack.Push(x); HwNote(x, "element", o); } }
                        else if (!e.IsPrimitive && !e.IsEnum && HwHasRefs(e, 0) && a.Rank == 1)
                        { HwType st = HwInfo(e); for (long i = 0; i < a.LongLength; i++) { object box = a.GetValue(i); HwPushStruct(box, st, seen, stack, o); } }
                    }
                    else
                    {
                        HwType ht = HwInfo(t); size = ht.Size;
                        for (int i = 0; i < ht.Refs.Length; i++) { object v; try { v = ht.Refs[i](o); } catch (Exception) { continue; } if (v != null && seen.Add(v)) { stack.Push(v); HwNote(v, ht.RefNames[i], o); } }
                        for (int i = 0; i < ht.StructFields.Length; i++) { object box; try { box = ht.StructFields[i].GetValue(o); } catch (Exception) { continue; } HwPushStruct(box, HwInfo(ht.StructFields[i].FieldType), seen, stack, o); }
                    }
                    objects++; bytes += size; rb[0]++; rb[1] += size;
                    long[] tb; if (!byType.TryGetValue(t, out tb)) { tb = new long[2]; byType[t] = tb; } tb[0]++; tb[1] += size;
                }
            };
            int roots = 0;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; } catch (Exception) { continue; }
                string an = asm.GetName().Name;
                foreach (Type t in types)
                {
                    if (t == null || t.ContainsGenericParameters) continue;
                    FieldInfo[] fs; try { fs = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); } catch (Exception) { continue; }
                    foreach (FieldInfo f in fs)
                    {
                        if (f.IsLiteral || f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType.IsPointer) continue;
                        object v; try { v = f.GetValue(null); } catch (Exception) { continue; }
                        if (v == null) continue; roots++; walk(an + ":" + t.Name + "." + f.Name, v); if (capped) break;
                    }
                    if (capped) break;
                }
                if (capped) break;
            }
            if (!capped)
                foreach (UnityEngine.Object uo in Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)))
                { if (uo == null) continue; roots++; walk("scene:" + uo.GetType().Name, uo); if (capped) break; }
            if (!capped)
                foreach (UnityEngine.Object uo in Resources.FindObjectsOfTypeAll(typeof(ScriptableObject)))
                { if (uo == null) continue; roots++; walk("asset:" + uo.GetType().Name, uo); if (capped) break; }
            long ms = sw.ElapsedMilliseconds; long utc = QdUtcMs();
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "heapwalk-" + utc + ".txt");
            List<KeyValuePair<Type, long[]>> tl = new List<KeyValuePair<Type, long[]>>(byType); tl.Sort(delegate(KeyValuePair<Type, long[]> a, KeyValuePair<Type, long[]> b) { return b.Value[1].CompareTo(a.Value[1]); });
            List<KeyValuePair<string, long[]>> rl = new List<KeyValuePair<string, long[]>>(byRoot); rl.Sort(delegate(KeyValuePair<string, long[]> a, KeyValuePair<string, long[]> b) { return b.Value[1].CompareTo(a.Value[1]); });
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("HEAPWALK utc=" + utc + " objects=" + objects + " estBytes=" + bytes + " roots=" + roots + " managedHeap=" + before + " ms=" + ms + " capped=" + capped);
                foreach (KeyValuePair<Type, long[]> kv in tl) if (kv.Value[1] >= 65536) w.WriteLine("TYPE " + kv.Key.FullName + "|" + kv.Value[0] + "|" + kv.Value[1]);
                foreach (KeyValuePair<string, long[]> kv in rl) if (kv.Value[1] >= 65536) w.WriteLine("ROOT " + kv.Key + "|" + kv.Value[0] + "|" + kv.Value[1]);
                // 0.3.85: who holds every array of 256 KB or more (up to 6 steps up the chain of collections/fields)
                foreach (object o in big)
                {
                    Array a = (Array)o; StringBuilder chain = new StringBuilder(); object cur = o;
                    for (int d = 0; d < 6 && cur != null; d++) { HwHold hh; if (!hwParent.TryGetValue(cur, out hh)) break; chain.Append(" <- ").Append(hh.Field); if (hh.Owner != null) chain.Append(" (").Append(HwShort(hh.Owner.GetType())).Append(')'); cur = hh.Owner; }
                    w.WriteLine("BIG " + HwShort(o.GetType()) + "|" + a.LongLength + "|" + (16 + a.LongLength * HwElemSize(o.GetType().GetElementType())) + "|" + chain);
                }
            }
            seen.Clear(); hwTypes.Clear(); hwParent = null;
            PtLog("HEAPWALK objects=" + objects + " estMB=" + (bytes >> 20) + " roots=" + roots + " ms=" + ms + " capped=" + capped + " -> " + Path.GetFileName(path));
            VpLog("#" + vpDone + " heapwalk objects=" + objects + " estMB=" + (bytes >> 20) + " ms=" + ms + " capped=" + capped);
            return true;
        }

        static void HwPushStruct(object box, HwType st, HashSet<object> seen, Stack<object> stack, object owner)
        {
            if (box == null) return;
            for (int j = 0; j < st.Refs.Length; j++) { object v; try { v = st.Refs[j](box); } catch (Exception) { continue; } if (v != null && seen.Add(v)) { stack.Push(v); HwNote(v, st.RefNames[j], owner); } }
            for (int j = 0; j < st.StructFields.Length; j++) { object inner; try { inner = st.StructFields[j].GetValue(box); } catch (Exception) { continue; } HwPushStruct(inner, HwInfo(st.StructFields[j].FieldType), seen, stack, owner); }
        }

        // 0.3.85 holders: the first parent is remembered only for arrays of 4096+ elements and collections, and for the objects holding those
        // collections, so a BIG line can name the field chain that owns a growing array. Objects outside that set keep no parent (cheap).
        sealed class HwHold { internal string Field; internal object Owner; internal HwHold(string f, object o) { Field = f; Owner = o; } }
        static Dictionary<object, HwHold> hwParent; static string hwRoot;
        static void HwNote(object v, string field, object owner)
        {
            if (hwParent == null || hwParent.ContainsKey(v)) return;
            if (!HwTrack(v)) return;
            hwParent[v] = new HwHold(field, owner);
            // remember one more level up for the owner (who holds the collection), if not known yet
            if (owner != null && !hwParent.ContainsKey(owner)) hwParent[owner] = new HwHold("(held under root " + hwRoot + ")", null);
        }
        static bool HwTrack(object v) { Type t = v.GetType(); if (t.IsArray) return ((Array)v).Length >= 4096; if (!t.IsGenericType) return false; string n = t.Name; return n.StartsWith("List`") || n.StartsWith("Dictionary`") || n.StartsWith("HashSet`") || n.StartsWith("LinkedList`") || n.StartsWith("Queue`") || n.StartsWith("Stack`"); }
        static string HwShort(Type t) { string n = t.FullName ?? t.Name; if (!t.IsArray) { int i = n.IndexOf('['); if (i > 0) n = n.Substring(0, i); } return n.Length > 140 ? n.Substring(0, 140) : n; }
    }
}

#endif
