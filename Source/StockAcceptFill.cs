using System;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

// 0.3.97: in the game's debug / editor mode ("Режим налагоджування", Architecte.editorMode) a stockpile that gets "Приймати лише <ресурс>" from its wheel menu
// is filled with that resource at once (Recepteur.WipeAndFill, the same call the game's own creative sidebar uses); a stockpile with nothing chosen stays as it is (empty),
// so units have something to carry. Host only (the game spawns the items on the server); outside debug mode nothing changes.
internal static class StockAcceptFill
{
    static Action<string> note;

    internal static void Install(Harmony harmony, Action<string> logger)
    {
        note = logger;
        harmony.Patch(AccessTools.Method(typeof(UNetProjectCmd), "CmdStockpileAcceptOnly"), null, new HarmonyMethod(AccessTools.Method(typeof(StockAcceptFill), "After")), null, null, null);
    }
    internal static void Clear() { note = null; }

    static void After(GameObject recepteurGO, string ressourceString)
    {
        try
        {
            if (!NetworkServer.active || !Architecte.editorMode || recepteurGO == null) return;
            Recepteur rec = recepteurGO.GetComponent<Recepteur>(); Ressource res = Adjectif.StringToAdjectif(ressourceString) as Ressource;
            if (rec == null || res == null || rec.ExclusiveRessource == null || rec.ExclusiveRessource.GetType() != res.GetType()) return;
            int filled = 0;
            rec.WipeAndFill(res); filled++;
            StorageLocator loc = recepteurGO.GetComponentInParent<StorageLocator>();
            if (loc != null && loc.Storage != null)
                foreach (Recepteur other in loc.Storage.GetRecepteurs())
                    if (other != null && other != rec && other.ExclusiveRessource != null && other.ExclusiveRessource.GetType() == res.GetType()) { other.WipeAndFill(res); filled++; }
            if (note != null) note("STOCK-ACCEPT-FILL " + ressourceString + ": " + filled + " crate(s) filled (debug mode)");
        }
        catch (Exception e) { if (note != null) note("STOCK-ACCEPT-FILL failed: " + e.GetType().Name + " " + e.Message); }
    }
}
