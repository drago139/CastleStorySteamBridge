using System;
using System.Collections.Generic;
using CastleStorySteam;

// 0.4.3 orange-ward pack: icon canvas math, the key rules, the GameMenu.lua badge/action patch and the display-element planning (no Unity needed).
static class OrangeWardPatchTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    const string Marker = "SB-ORANGE-6";
    const string Nl = "\r\n";

    // the recipe-button loop of GameMenu.lua (0.4.1 copy of the game's file), cut to the lines around the anchor
    static readonly string Sample = string.Join(Nl, new string[] {
        "for i = 1, Data.Operable:GetHighestRecipeSlotCount() do",
        "local h = ButtonHandle.New()",
        "h.Visible = ||Data.Operable:HasSelected() and Data.Operable:HasRecipeAtSlotIndex(Data.Operable:GetSelectedType(), i)",
        "h.IconSize = ||64",
        "h.OnAction = ||h.Visible() and Data.Operable:AddToQueue(GetRecipeAtSlotIndex(i))",
        "h.hasHotkey = true",
        "h.HotkeyLabel_Text = ||GetRecipeSlotHotkey(i)",
        "h.ev_refresh = Event.New()",
        "",
        "local Refresh = ||h.ev_refresh:Invoke()",
        "_m.bh.operableRecipes[i] = h",
        "end" }) + Nl;

    static int Count(string text, string part)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
        return n;
    }

    public static int Main()
    {
        try
        {
            // ---- F-1: canvas width. The cell is square; only a nearly square stock aspect is followed.
            Check(OrangeWardPatchRules.CanvasWidth(83, 1f) == 83, "square stock icon -> square canvas");
            Check(OrangeWardPatchRules.CanvasWidth(83, 57f / 83f) == 83, "a portrait stock sprite is ignored: the cell, not the stock sprite, decides");
            Check(OrangeWardPatchRules.CanvasWidth(83, float.NaN) == 83 && OrangeWardPatchRules.CanvasWidth(83, 0f) == 83 && OrangeWardPatchRules.CanvasWidth(83, -2f) == 83, "unreadable aspect -> square");
            Check(OrangeWardPatchRules.CanvasWidth(83, float.PositiveInfinity) == 83 && OrangeWardPatchRules.CanvasWidth(83, 5f) == 83 && OrangeWardPatchRules.CanvasWidth(83, 0.1f) == 83, "absurd aspect -> square");
            Check(OrangeWardPatchRules.CanvasWidth(83, 1.1f) == 91 && OrangeWardPatchRules.CanvasWidth(83, 0.9f) == 75, "nearly square follows the stock aspect");
            Check(OrangeWardPatchRules.CanvasWidth(1, 0.8f) == 1, "never below one pixel");

            // ---- F-1: the shrink arithmetic is the 0.3.105 one when the canvases have the same size (the big icon stays exactly as it was)
            int sw = 57, sh = 83; float k = 0.65f; float[] o = { -0.25f, 0.25f };
            float cx = (sw - 1) * 0.5f, cy = (sh - 1) * 0.5f;
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                    for (int i = 0; i < 2; i++)
                        for (int j = 0; j < 2; j++)
                        {
                            float lx = cx + (x + o[i] - cx) / k, ly = cy + (y + o[j] - cy) / k, nx, ny;
                            OrangeWardPatchRules.SourcePixel(x, y, o[i], o[j], sw, sh, sw, sh, k, out nx, out ny);
                            if (Math.Abs(nx - lx) > 1e-5f || Math.Abs(ny - ly) > 1e-5f) throw new Exception("big icon differs from the 0.3.105 arithmetic at " + x + "," + y);
                        }
            checks++;
            float ex, ey;
            OrangeWardPatchRules.SourcePixel(0, 0, 0f, 0f, sw, sh, sw, sh, k, out ex, out ey);
            Check(!OrangeWardPatchRules.InsideSource(ex, ey, sw, sh), "the corner of the shrunk canvas is outside the source (transparent)");
            OrangeWardPatchRules.SourcePixel(28, 41, 0f, 0f, sw, sh, sw, sh, k, out ex, out ey);
            Check(Math.Abs(ex - 28f) < 1e-4f && Math.Abs(ey - 41f) < 1e-4f && OrangeWardPatchRules.InsideSource(ex, ey, sw, sh), "the centre maps to the centre");

            // ---- F-1: the square canvas. The picture's art box is x 3..55, y 2..81 of 57x83 (alpha bounding box of the embedded PNG).
            int dw = OrangeWardPatchRules.CanvasWidth(83, 1f), dh = 83;
            float scx = (sw - 1) * 0.5f, scy = (sh - 1) * 0.5f, dcx = (dw - 1) * 0.5f, dcy = (dh - 1) * 0.5f;
            float x0 = dcx + (3 - scx) * k, x1 = dcx + (55 - scx) * k, y0 = dcy + (2 - scy) * k, y1 = dcy + (81 - scy) * k;
            float artW = x1 - x0, artH = y1 - y0;
            Check(dw == 83 && dh == 83, "square canvas 83x83");
            Check(Math.Abs(artW / dw - 0.407f) < 0.01f && Math.Abs(artH / dh - 0.619f) < 0.01f, "art is 0.41 x 0.62 of the canvas (the stock red art is about 0.43 x 0.63)");
            Check(Math.Abs((x0 + x1) * 0.5f - dcx) < 1.0f && Math.Abs((y0 + y1) * 0.5f - dcy) < 1.0f, "art is centred");
            Check(Math.Abs(artW / artH - 52f / 79f) < 0.01f, "in a square cell the art keeps its own shape (w/h 0.66)");
            float oldInCell = (52f * k / sw) / (79f * k / sh);   // the 57x83 canvas stretched into the square cell (the 0.4.1 picture)
            Check(oldInCell > 0.9f && oldInCell < 1.0f, "the 0.4.1 picture was drawn almost square (w/h 0.96 instead of 0.66): the stretched look in the queue");

            // ---- F-2: the key
            string label;
            Check(OrangeWardPatchRules.JudgeKey(106, out label) == OrangeWardPatchRules.KeyVerdict.Ok && label == "J", "J is usable and labelled J");
            foreach (char c in OrangeWardPatchRules.FreeLetters)
                Check(OrangeWardPatchRules.JudgeKey(c + 32, out label) == OrangeWardPatchRules.KeyVerdict.Ok && label == c.ToString(), "free letter " + c);
            foreach (char c in OrangeWardPatchRules.ReservedLetters)
                Check(OrangeWardPatchRules.JudgeKey(c + 32, out label) == OrangeWardPatchRules.KeyVerdict.Reserved && label == "", "reserved letter " + c);
            Check(OrangeWardPatchRules.ReservedLetters.Length + OrangeWardPatchRules.FreeLetters.Length == 26, "reserved + free = all 26 letters");
            foreach (char c in OrangeWardPatchRules.FreeLetters) Check(OrangeWardPatchRules.ReservedLetters.IndexOf(c) < 0, "free letter " + c + " is not reserved");
            foreach (char c in "YUIOH") Check(OrangeWardPatchRules.ReservedLetters.IndexOf(c) >= 0, "queue slot key " + c + " is reserved");
            Check(OrangeWardPatchRules.JudgeKey(0, out label) == OrangeWardPatchRules.KeyVerdict.Off && label == "", "None = off");
            foreach (int code in new int[] { 27, 48, 57, 96, 123, 282, 304, 323 })
                Check(OrangeWardPatchRules.JudgeKey(code, out label) == OrangeWardPatchRules.KeyVerdict.Unsupported, "unsupported key code " + code);

            // ---- GameMenu.lua: switch off = exactly the 0.4.1 text
            string off = OrangeWardPatchRules.PatchGameMenu(Sample, false, Marker, Nl);
            Check(!object.ReferenceEquals(off, Sample) && off.IndexOf(OrangeWardPatchRules.GameMenuAnchor, StringComparison.Ordinal) < 0, "anchor replaced (off)");
            Check(OrangeWardPatchRules.GameMenuReplacement(false, Marker, Nl) == "h.HotkeyLabel_Text = ||(i <= 5 and GetRecipeSlotHotkey(i) or \"\")\r\nif i > 5 then h.hasHotkey = false end -- SB-ORANGE-6", "the off text is the 0.4.1 text, byte for byte");
            Check(off.Replace(OrangeWardPatchRules.GameMenuReplacement(false, Marker, Nl), OrangeWardPatchRules.GameMenuAnchor) == Sample, "only the anchor line changed (off)");
            Check(Count(off, Marker) == 1, "marker once (off)");

            // ---- GameMenu.lua: with the key
            string on = OrangeWardPatchRules.PatchGameMenu(Sample, true, Marker, Nl);
            string rep = OrangeWardPatchRules.GameMenuReplacement(true, Marker, Nl);
            Check(!object.ReferenceEquals(on, Sample) && on.IndexOf(OrangeWardPatchRules.GameMenuAnchor, StringComparison.Ordinal) < 0, "anchor replaced (on)");
            Check(on.Replace(rep, OrangeWardPatchRules.GameMenuAnchor) == Sample, "only the anchor line changed (on)");
            Check(Count(on, Marker) == 1, "marker once (on)");
            Check(rep.Contains("SB_OrangeSlotAction = ||h.OnAction()") && rep.Contains("SB_OrangeHotkeyLabel"), "stores the slot action and asks for the label");
            Check(Count(rep, "SB_OrangeHotkeyLabel()") == Count(rep, "SB_OrangeHotkeyLabel and SB_OrangeHotkeyLabel()"), "every call of the label function is guarded against nil");
            Check(Count(rep, "if ") == 2 && Count(rep, " end") == 2, "if / end balanced in the inserted text");
            Check(rep.Contains("i == 6"), "only slot 6 gets the key");
            Check(rep.Split(new string[] { Nl }, StringSplitOptions.None).Length == 3, "three lines inserted");
            Check(object.ReferenceEquals(OrangeWardPatchRules.PatchGameMenu(on, true, Marker, Nl), on), "an already patched text comes back unchanged");
            Check(object.ReferenceEquals(OrangeWardPatchRules.PatchGameMenu("nothing here", true, Marker, Nl), "nothing here") || OrangeWardPatchRules.PatchGameMenu("nothing here", true, Marker, Nl) == "nothing here", "no anchor: unchanged");
            string twice = Sample + Sample;
            Check(object.ReferenceEquals(OrangeWardPatchRules.PatchGameMenu(twice, true, Marker, Nl), twice), "a second anchor makes it refuse (not unique)");
            Check(OrangeWardPatchRules.PatchGameMenu(null, true, Marker, Nl) == null, "null stays null");

            // ---- F-3/F-4: which display elements get an orange twin
            Check(OrangeWardPatchRules.IsGemName("Gem") && !OrangeWardPatchRules.IsGemName("gem") && !OrangeWardPatchRules.IsGemName("Plates") && !OrangeWardPatchRules.IsGemName(null), "only the renderer named Gem is recoloured");
            string[] elements = { "SentinelWardAdj", "SentinelWardAdj", null, "HealingWardAdj", "SentinelWardAdj", "Bow" };
            List<int> todo = OrangeWardPatchRules.ElementsToClone(elements, false);
            Check(todo.Count == 3 && todo[0] == 0 && todo[1] == 1 && todo[2] == 4, "the three red-ward elements get a twin (crate: >=1, >=2, ...; Altar: > 0)");
            Check(OrangeWardPatchRules.ElementsToClone(elements, true).Count == 0, "a second Start of a pooled object adds nothing");
            Check(OrangeWardPatchRules.ElementsToClone(null, false).Count == 0 && OrangeWardPatchRules.ElementsToClone(new string[0], false).Count == 0, "no elements, no twins");
            Check(OrangeWardPatchRules.ElementsToClone(new string[] { "HealingWardAdj", "Bow" }, false).Count == 0, "other wards and hats are never copied");

            // ---- 0.4.6: the model in the worker's hands
            Check(OrangeWardPatchRules.HeldFactoryName == "Accessories" && OrangeWardPatchRules.HeldRedName == "HeldSentinelWard" && OrangeWardPatchRules.HeldOrangeName == "HeldOrangeWard", "the three key names are the network format (asset id = MD5(factory + name)) and never change");
            Check(OrangeWardPatchRules.UseHeldOrange(true, true, true, false), "switch on, clone and pool ready, no failure: orange");
            Check(!OrangeWardPatchRules.UseHeldOrange(false, true, true, false) && !OrangeWardPatchRules.UseHeldOrange(true, false, true, false) && !OrangeWardPatchRules.UseHeldOrange(true, true, false, false) && !OrangeWardPatchRules.UseHeldOrange(true, true, true, true), "any missing piece or an earlier failure: red");
            Check(OrangeWardPatchRules.HeldWhyRed(true, true, true, false) == "" && OrangeWardPatchRules.HeldWhyRed(true, true, true, true).Contains("failed") && OrangeWardPatchRules.HeldWhyRed(false, true, true, false).Contains("switched off") && OrangeWardPatchRules.HeldWhyRed(true, false, true, false).Contains("clone") && OrangeWardPatchRules.HeldWhyRed(true, true, false, false).Contains("pool"), "the reason names the first missing piece");
            for (int m = 0; m < 16; m++)
            {
                bool a = (m & 1) != 0, b = (m & 2) != 0, c = (m & 4) != 0, d = (m & 8) != 0;
                Check(OrangeWardPatchRules.UseHeldOrange(a, b, c, d) == (OrangeWardPatchRules.HeldWhyRed(a, b, c, d) == ""), "decision and reason agree for case " + m);
            }

            // ---- 0.4.7: the placement ghost and the Altar preview are the game's blue holograms unless asked otherwise
            Check(OrangeWardPatchRules.OrangeHologramDefault == false, "orange holograms are OFF by default (every other hologram of the game is blue)");
            Check(!OrangeWardPatchRules.OrangeHologram(false, true) && !OrangeWardPatchRules.OrangeHologram(true, false) && !OrangeWardPatchRules.OrangeHologram(false, false) && OrangeWardPatchRules.OrangeHologram(true, true), "orange holograms need the hologram switch AND the recipe switch");

            // ---- 0.4.16: the ward skips barrels launched by an allied catapult (relation numbers = Affiliation.Relation)
            Check(OrangeWardPatchRules.SkipAlliedDefault && OrangeWardPatchRules.RelationAllied == 1, "the team filter is ON by default and Allied is relation 1 (None 0, Allied 1, Neutral 2, Enemy 4, Undefined 8)");
            Check(OrangeWardPatchRules.SkipAlliedBarrel(true, true, 1), "own side's catapult, filter on: skipped");
            Check(!OrangeWardPatchRules.SkipAlliedBarrel(true, true, 4) && !OrangeWardPatchRules.SkipAlliedBarrel(true, true, 2) && !OrangeWardPatchRules.SkipAlliedBarrel(true, true, 8) && !OrangeWardPatchRules.SkipAlliedBarrel(true, true, 0) && !OrangeWardPatchRules.SkipAlliedBarrel(true, true, 15), "enemy, neutral, undefined, none or 'all': shot");
            Check(!OrangeWardPatchRules.SkipAlliedBarrel(true, false, 1) && !OrangeWardPatchRules.SkipAlliedBarrel(true, false, 0), "unknown origin (no catapult, no faction): shot as before, even if a stale relation says allied");
            Check(!OrangeWardPatchRules.SkipAlliedBarrel(false, true, 1) && !OrangeWardPatchRules.SkipAlliedBarrel(false, false, 1), "filter off: every barrel in flight is shot (0.4.15 behaviour)");
            int skipped = 0; for (int m = 0; m < 2 * 2 * 16; m++) { if (OrangeWardPatchRules.SkipAlliedBarrel((m & 1) != 0, (m & 2) != 0, m >> 2)) skipped++; }
            Check(skipped == 1, "over every switch / origin / relation 0..15 exactly one combination skips (on, known, relation 1)");

            Console.WriteLine("PASS: " + checks + " orange ward pack checks (small icon canvas, 0.3.105 arithmetic kept, key rules, GameMenu.lua badge/action patch, display twins, carried model rules, hologram colour rule, 0.4.16 allied-barrel filter).");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
    }
}
