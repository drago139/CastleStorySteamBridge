using System;
using System.Collections.Generic;
using CastleStorySteam;

// RecipeSlotRules (StableRecipeSlots) plus a small model of the station slot bar: the keys Y U I O H press slot N; 6th slot has no key.
static class RecipeSlotTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static string Menu(string nl)
    {
        return "for i = 1, Data.Operable:GetHighestRecipeSlotCount() do" + nl + "local h = ButtonHandle.New()" + nl + RecipeSlotRules.Anchor + nl + "h.hasHotkey = true" + nl + "end" + nl;
    }
    // model: station recipes in fixed slot order; blocked ones are hidden but keep their slot; key N queues slot N if visible
    static string Press(string[] slots, HashSet<string> blocked, int key) { if (key < 1 || key > slots.Length) return null; string r = slots[key - 1]; return blocked.Contains(r) ? null : r; }
    static void Main()
    {
        int n;
        string p = RecipeSlotRules.PatchGameMenu(Menu("\r\n"), new List<string> { "unit:HalberdierKit" }, out n);
        Check(n == 1 && p.Contains("and not (GetRecipeAtSlotIndex(i) == Recipe.HalberdierKit)"), "one kit patched");
        Check(p.Contains(RecipeSlotRules.Marker) && p.Contains(RecipeSlotRules.Anchor + " and not ("), "marker and condition follow the anchor");
        Check(p.Contains("--[[" + RecipeSlotRules.Marker + "]]\r\nh.hasHotkey = true"), "block comment keeps the next line");
        string p2 = RecipeSlotRules.PatchGameMenu(Menu("\n"), new List<string> { "unit:KnightKit", "unit:ArcherKit", "unit:KnightKit" }, out n);
        Check(n == 2 && p2.Contains("Recipe.ArcherKit or GetRecipeAtSlotIndex(i) == Recipe.KnightKit"), "two kits, sorted, no duplicate");
        string same = Menu("\n");
        Check(object.ReferenceEquals(RecipeSlotRules.PatchGameMenu(same, new List<string>(), out n), same) && n == 0, "empty list = same instance");
        Check(object.ReferenceEquals(RecipeSlotRules.PatchGameMenu(same, new List<string> { "Stone_Brick", "unit:", "unit:a b", "unit:x);os.exit()" }, out n), same) && n == 0, "non-kit and unsafe ids are ignored");
        Check(object.ReferenceEquals(RecipeSlotRules.PatchGameMenu(p, new List<string> { "unit:KnightKit" }, out n), p) && n == 0, "already patched = same instance");
        Check(object.ReferenceEquals(RecipeSlotRules.PatchGameMenu("no anchor here", new List<string> { "unit:KnightKit" }, out n), "no anchor here") || n == 0, "no anchor = unchanged");
        string dup = Menu("\n") + Menu("\n");
        Check(RecipeSlotRules.PatchGameMenu(dup, new List<string> { "unit:KnightKit" }, out n) == dup && n == 0, "duplicate anchor = unchanged");
        Check(RecipeSlotRules.PatchGameMenu(null, new List<string> { "unit:KnightKit" }, out n) == null, "null code");
        Check(RecipeSlotRules.KitName("unit:ArbalistKit") == "ArbalistKit" && RecipeSlotRules.KitName("ArbalistKit") == null && RecipeSlotRules.KitName(null) == null, "KitName");

        // slot model: Workbench = HalberdierKit, ArcherKit, LanternWard; block HalberdierKit
        string[] bench = { "HalberdierKit", "ArcherKit", "LanternWard" };
        HashSet<string> blocked = new HashSet<string> { "HalberdierKit" };
        Check(Press(bench, blocked, 1) == null, "blocked slot 1: key Y does nothing");
        Check(Press(bench, blocked, 2) == "ArcherKit" && Press(bench, blocked, 3) == "LanternWard", "slots 2 and 3 keep their keys");
        // the old behaviour (cut out) for comparison: key 1 would queue ArcherKit
        string[] cut = { "ArcherKit", "LanternWard" };
        Check(Press(cut, new HashSet<string>(), 1) == "ArcherKit", "model of the old shift (documents the bug)");
        // altar: 5 old wards + orange as slot 6, no key for slot 6
        string[] altar = { "w1", "w2", "w3", "w4", "w5", "OrangeWard" };
        Check(Press(altar, new HashSet<string>(), 5) == "w5" && Press(altar, new HashSet<string>(), 1) == "w1", "altar keys 1-5 unchanged");
        Console.WriteLine("RecipeSlotTests OK (" + checks + " checks)");
    }
}