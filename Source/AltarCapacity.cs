using System;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using HarmonyLib;
using UnityEngine;

// 0.4.24 ALTAR CAPACITY for the 80-crystal orange ward (user: the ward costs 80 of the golden resource).
// Why: CraftingInventory computes _resourceCap once, in its constructor, as the MAX over every Ressource entry of the Altar's Recepteur.BaseCapacity (stock data: 40), and
// RefreshAmounts fetches only required.CapAt(_resourceCap) - content. A recipe that needs more than that cap is fetched up to the cap and then the Altar waits for ever (HasIngredientsIn
// stays false). No stock recipe needs more than 40, so the case never happened in the stock game. This adds an exact OrangeCrystal entry (= the configured ward cost) to the base
// capacity of every Altar BEFORE the constructor runs, so the cap becomes the ward cost. Stock recipes need <= 40 and are not affected; nothing is ever lowered.
// Every peer runs it (like CarryCapacityFix). Switch: [Fixes] OrangeWardAltarCapacity (default true); only active while OrangeWardCraftable is on and the cost is above the Altar's own cap.
// Log tag: ALTAR-CAP (the Altar's base capacity before and after, once per Altar, max 6 lines).
internal static class AltarCapacity
{
    static Func<int> target;
    static Action<string> note;
    static int logs;

    internal static void Install(Harmony harmony, Func<int> wantedCrystals, Action<string> logger)
    {
        target = wantedCrystals; note = logger;
        harmony.Patch(AccessTools.Constructor(typeof(CraftingInventory), new Type[] { typeof(Recepteur) }), new HarmonyMethod(AccessTools.Method(typeof(AltarCapacity), "BeforeCtor")), null, null, null, null);
    }

    internal static void Clear() { target = null; note = null; logs = 0; }

    // The parameter name must match the constructor's (recepteur).
    static void BeforeCtor(Recepteur recepteur)
    {
        try
        {
            if (target == null || recepteur == null) return;
            CraftingStation cs = recepteur.GetComponent<CraftingStation>();
            if (cs == null || cs.GetType().Name != "Altar") return;
            int want = target();
            if (want <= 0) return;
            Description cap = recepteur.BaseCapacity;
            if (cap == null) return;
            string before = Dump(cap);
            Adjectif a = cap.Get(typeof(OrangeCrystal));
            if (a == null)
            {
                Adjectif add = Adjectif.orangeCrystal.Clone(); add.quantifiable.valeur = want; cap.Add(add);
            }
            else if (a.quantifiable != null && a.quantifiable.valeur < want) a.quantifiable.valeur = want;
            else return;
            if (logs++ < 6 && note != null) note("ALTAR-CAP " + recepteur.name + " base capacity " + before + " -> " + Dump(cap) + " (orange crystal " + want + ")");
            try { recepteur.ResetDescriptions(); }
            catch (Exception e) { if (note != null && logs < 12) { logs++; note("ALTAR-CAP ResetDescriptions after the change failed (" + e.GetType().Name + " " + e.Message + "); the next inventory change recomputes it"); } }
        }
        catch (Exception e) { if (note != null && logs++ < 12) note("ALTAR-CAP failed: " + e.GetType().Name + " " + e.Message); }
    }

    static string Dump(Description d)
    {
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[");
            bool first = true;
            foreach (System.Collections.Generic.KeyValuePair<Type, Adjectif> kv in d.DicoAdjectif)
            {
                if (!first) sb.Append(", "); first = false;
                sb.Append(kv.Key.Name);
                if (kv.Value != null && kv.Value.quantifiable != null) sb.Append(':').Append(kv.Value.quantifiable.valeur);
            }
            return sb.Append(']').ToString();
        }
        catch (Exception) { return "[?]"; }
    }
}
