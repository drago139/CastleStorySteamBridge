using System;
using System.Collections.Generic;
using System.Text;

namespace CastleStorySteam
{
    // 0.3.58: the bookkeeping of CrateGuard without Unity, so CrateMixTests can drive it.
    // The only mixed crate ever logged (0.3.16 client: RawStone x40 + RawTronStone x1, added by a replicated re-parent) cannot tell
    // a lasting host/client divergence from a short replication-order glitch; a re-check a few seconds later can.
    internal static class CrateMixRules
    {
        internal const float RecheckSeconds = 5f;

        // "RawStone (12)", "RawStone(Clone)" and "RawStone" are the same kind of item.
        internal static string KindOf(string objectName)
        {
            if (String.IsNullOrEmpty(objectName)) return "?";
            string n = objectName;
            int cut = n.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) n = n.Substring(0, cut);
            if (n.EndsWith("(Clone)", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 7);
            n = n.Trim();
            return n.Length == 0 ? "?" : n;
        }

        // Kinds in a stable (sorted) order, e.g. "RawStone x40, RawTronStone x1"; kinds = how many different ones.
        internal static string Summary(IEnumerable<string> objectNames, out int kinds)
        {
            SortedDictionary<string, int> count = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string name in objectNames)
            {
                string k = KindOf(name);
                int c; count.TryGetValue(k, out c); count[k] = c + 1;
            }
            kinds = count.Count;
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, int> k in count) { if (sb.Length > 0) sb.Append(", "); sb.Append(k.Key).Append(" x").Append(k.Value); }
            return sb.ToString();
        }

        // The game's own notion of "one resource type": a crate narrows its capacity by intersecting adjective types
        // (Recepteur.AddTo -> CurrentCapacity.IntersectWith), so two objects with the same adjective types are the same kind
        // even with different names. RawStone and RawTronStone share Stones/Encombrement/Granulaire/Libre/StoredGeneric
        // (ResourcesFactory), so a crate holding both is legal, not mixed.
        internal static string GameKind(IEnumerable<string> adjectiveTypeNames)
        {
            List<string> types = new List<string>();
            if (adjectiveTypeNames != null)
                foreach (string t in adjectiveTypeNames) if (!String.IsNullOrEmpty(t) && !types.Contains(t)) types.Add(t);
            if (types.Count == 0) return "?";
            types.Sort(StringComparer.Ordinal);
            return String.Join("+", types.ToArray());
        }

        internal static int CountDistinct(IEnumerable<string> keys)
        {
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string k in keys) seen[k ?? "?"] = true;
            return seen.Count;
        }

        internal static string Verdict(int kindsBefore, int kindsNow, bool crateGone)
        {
            if (crateGone) return "crate gone";
            if (kindsNow >= 2) return "still mixed (lasting divergence or a real game rule break)";
            return kindsBefore >= 2 ? "resolved (short replication-order glitch)" : "not mixed";
        }
    }

    // One pending re-check per crate; a crate already waiting is not re-armed, so a flood of tracked items costs one entry.
    internal sealed class CrateRecheckQueue<T>
    {
        internal sealed class Entry { internal int Id; internal T Crate; internal float Due; internal string Before; internal int Report; }
        readonly List<Entry> pending = new List<Entry>();
        internal int Count { get { return pending.Count; } }

        internal bool Add(int id, T crate, float now, string before, int report)
        {
            foreach (Entry e in pending) if (e.Id == id) return false;
            Entry n = new Entry(); n.Id = id; n.Crate = crate; n.Due = now + CrateMixRules.RecheckSeconds; n.Before = before; n.Report = report;
            pending.Add(n);
            return true;
        }

        internal List<Entry> TakeDue(float now)
        {
            List<Entry> due = new List<Entry>();
            for (int i = pending.Count - 1; i >= 0; i--)
                if (pending[i].Due <= now) { due.Insert(0, pending[i]); pending.RemoveAt(i); }
            return due;
        }

        internal void Clear() { pending.Clear(); }
    }
}
