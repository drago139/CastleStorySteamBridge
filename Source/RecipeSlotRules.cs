using System;
using System.Collections.Generic;
using System.Text;

namespace CastleStorySteam
{
    // 0.4.x StableRecipeSlots: pure string logic (no game types, unit-tested by RecipeSlotTests.cs).
    // Problem: ItemFilter used to cut a blocked class kit out of the station recipe list in Meta_Operable.lua. The game binds the keys Y U I O H
    // (project_QueueSlot1..5, GameMenu.lua) to operableRecipes[N] = the N-th SLOT, so after the cut every later slot moved one place and the key
    // queued another item. Now the list stays untouched and the slot button of a blocked kit is hidden in GameMenu.lua; the click and the key both
    // go through h.Visible() (h.OnAction = ||h.Visible() and ...AddToQueue), so a hidden slot queues nothing. Slot numbers and bindings stay as they are.
    internal static class RecipeSlotRules
    {
        internal const string Marker = "SB-STABLE-SLOTS";
        // anchor = the Visible line of the recipe slot buttons in GameMenu.lua (appears once)
        internal const string Anchor = "h.Visible = ||Data.Operable:HasSelected() and Data.Operable:HasRecipeAtSlotIndex(Data.Operable:GetSelectedType(), i)";

        // "unit:HalberdierKit" -> "HalberdierKit"; anything else, or an unsafe name, -> null
        internal static string KitName(string id)
        {
            if (id == null || !id.StartsWith("unit:", StringComparison.Ordinal)) return null;
            string n = id.Substring(5);
            if (n.Length == 0 || n.Length > 40) return null;
            for (int i = 0; i < n.Length; i++) { char c = n[i]; if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return null; }
            return n;
        }

        // Returns the same instance when nothing changes (no blocked kit, marker already there, anchor missing or not unique).
        // blockedCount = number of kits written into the condition.
        internal static string PatchGameMenu(string code, ICollection<string> blockedIds, out int blockedCount)
        {
            blockedCount = 0;
            if (code == null || blockedIds == null || blockedIds.Count == 0 || code.IndexOf(Marker, StringComparison.Ordinal) >= 0) return code;
            List<string> names = new List<string>();
            foreach (string id in blockedIds) { string n = KitName(id); if (n != null && !names.Contains(n)) names.Add(n); }
            if (names.Count == 0) return code;
            names.Sort(StringComparer.Ordinal);
            int i = code.IndexOf(Anchor, StringComparison.Ordinal);
            if (i < 0 || code.IndexOf(Anchor, i + Anchor.Length, StringComparison.Ordinal) >= 0) return code;
            StringBuilder cond = new StringBuilder(" and not (");
            for (int k = 0; k < names.Count; k++) { if (k > 0) cond.Append(" or "); cond.Append("GetRecipeAtSlotIndex(i) == Recipe.").Append(names[k]); }
            cond.Append(") --[[").Append(Marker).Append("]]");
            blockedCount = names.Count;
            return code.Substring(0, i) + Anchor + cond.ToString() + code.Substring(i + Anchor.Length);
        }
    }
}