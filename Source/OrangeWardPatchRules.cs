using System;
using System.Collections.Generic;

namespace CastleStorySteam
{
    // 0.4.3 ORANGE-WARD PACK (F-1 small icon, F-2 hotkey of slot 6, F-3/F-4 models): the decisions that need no Unity, so OrangeWardPatchTests can drive them.
    internal static class OrangeWardPatchRules
    {
        // ---------------------------------------------------------------- F-1: the small icon (Image32)
        // The Altar queue strip and the item counters over the Altar and the crates draw recipe.icon.Get32() / the adjective icon into a SQUARE cell without keeping the aspect
        // (FurnaceOverheadDisplayDriver -> OrderQueue.AddOrderAt: item prefab Image.preserveAspect = 0, LayoutElement min 32x32). The stock SpriteCompiler icons have a padded square
        // canvas (art about 0.43 x 0.63 of it); our picture lives in a 57x83 portrait canvas (art 0.41 x 0.62 after the 0.65 shrink) and was stretched 83/57 = 1.46 times sideways there.
        // The big icon (Image64, Lua widgets, keeps the aspect) stays as it is; the small one gets a canvas with the aspect of the stock Image32.

        // width of the canvas for the given height. The cell is square (prefab data), so the canvas is square; the aspect (width / height) of the stock Image32 is followed only when it is
        // nearly square (0.8 .. 1.25). A portrait stock sprite, an unreadable one or an absurd value gives 1 = a square: the cell, not the stock sprite, decides.
        internal static int CanvasWidth(int height, float stockAspect)
        {
            float a = stockAspect;
            if (float.IsNaN(a) || float.IsInfinity(a) || a < 0.8f || a > 1.25f) a = 1f;
            int w = (int)Math.Round(height * a);
            return w < 1 ? 1 : w;
        }

        // The pixel of the source picture shown at pixel (x, y) of the destination canvas when the art is scaled by k around the centre of the canvas ((ox, oy) = the sub-pixel
        // offset of the 2x2 supersampling). The arithmetic of the 0.3.105 ShrinkInCanvas; identical to it when both canvases have the same size (the Image64 path).
        internal static void SourcePixel(int x, int y, float ox, float oy, int dstW, int dstH, int srcW, int srcH, float k, out float sx, out float sy)
        {
            float scx = (srcW - 1) * 0.5f, scy = (srcH - 1) * 0.5f, dcx = (dstW - 1) * 0.5f, dcy = (dstH - 1) * 0.5f;
            sx = scx + (x + ox - dcx) / k;
            sy = scy + (y + oy - dcy) / k;
        }

        internal static bool InsideSource(float sx, float sy, int srcW, int srcH)
        {
            return !(sx < 0f || sy < 0f || sx > srcW - 1 || sy > srcH - 1);
        }

        // ---------------------------------------------------------------- F-2: the key of the 6th Craft-bar button
        // Rewired cannot get a 6th queue action (only project_QueueSlot1..5 exist), so the key is polled by the plugin. Letters bound in the Rewired keyboard maps that are active while
        // the Altar's craft bar is open (PersistentScene.unity: map 1 camera + commands, 2 chat, 3 bricktron, 4 projects): A B C D E G H I K L O P Q S T U W X Y Z
        // (Y U I O H = the five queue slots, K = loop, L = clear queue, P = pause, W A S D / Q E = camera, T B G C X = commands, T = chat, Z = stop, H = stance).
        // No default map binds F J M N R V there. J is the key of project_TaskActionSlot6, which the game registers next to QueueSlot1..5 but guards with a selected harvest/tunnel task (that
        // cannot coexist with a selected Altar); J also continues the row Y U I O H. F is the only letter that has no delegate in any input mode.
        internal const string ReservedLetters = "ABCDEGHIKLOPQSTUWXYZ";
        internal const string FreeLetters = "FJMNRV";

        internal enum KeyVerdict { Off, Ok, Reserved, Unsupported }

        // unityKeyCode = (int)UnityEngine.KeyCode: 0 = None, 97..122 = A..Z
        internal static KeyVerdict JudgeKey(int unityKeyCode, out string label)
        {
            label = "";
            if (unityKeyCode == 0) return KeyVerdict.Off;
            if (unityKeyCode < 97 || unityKeyCode > 122) return KeyVerdict.Unsupported;
            char c = (char)(unityKeyCode - 32);
            if (ReservedLetters.IndexOf(c) >= 0) return KeyVerdict.Reserved;
            label = c.ToString();
            return KeyVerdict.Ok;
        }

        // ---------------------------------------------------------------- GameMenu.lua: the badge of slot 6 and the action the key calls
        // The game builds one button per recipe slot in a loop (GameMenu.lua, "for i = 1, Data.Operable:GetHighestRecipeSlotCount()"); slots 1..5 show the label of their Rewired action.
        internal const string GameMenuAnchor = "h.HotkeyLabel_Text = ||GetRecipeSlotHotkey(i)";

        // withKey = false: exactly the 0.4.1 text (slot 6 has no badge). withKey = true: slot 6 shows the label SB_OrangeHotkeyLabel() gives and SB_OrangeSlotAction = ||h.OnAction() is
        // stored for the plugin (the game's own h.Visible() check inside OnAction means "the Altar is selected"). Both globals are tested for nil, so a Script without them stays valid.
        internal static string GameMenuReplacement(bool withKey, string marker, string nl)
        {
            if (!withKey)
                return "h.HotkeyLabel_Text = ||(i <= 5 and GetRecipeSlotHotkey(i) or \"\")" + nl + "if i > 5 then h.hasHotkey = false end -- " + marker;
            return "h.HotkeyLabel_Text = ||(i <= 5 and GetRecipeSlotHotkey(i) or (i == 6 and SB_OrangeHotkeyLabel and SB_OrangeHotkeyLabel() or \"\"))" + nl
                + "if i > 5 then h.hasHotkey = false end" + nl
                + "if i == 6 and SB_OrangeHotkeyLabel and SB_OrangeHotkeyLabel() ~= \"\" then h.hasHotkey = true; SB_OrangeSlotAction = ||h.OnAction() end -- " + marker;
        }

        // the same instance comes back when the anchor is missing or not unique (the caller compares references)
        internal static string PatchGameMenu(string code, bool withKey, string marker, string nl)
        {
            if (code == null) return null;
            int i = code.IndexOf(GameMenuAnchor, StringComparison.Ordinal);
            if (i < 0 || code.IndexOf(GameMenuAnchor, i + GameMenuAnchor.Length, StringComparison.Ordinal) >= 0) return code;
            return code.Substring(0, i) + GameMenuReplacement(withKey, marker, nl) + code.Substring(i + GameMenuAnchor.Length);
        }

        // ---------------------------------------------------------------- F-3 / F-4: models of the finished item (crate, Altar) and of the item being crafted (Altar preview)
        // The renderer whose material carries the red of a ward is the child named "Gem" (SentinelWard_Gem), in the real ward and in every display model of it.
        internal const string RedWardAdjective = "SentinelWardAdj";
        internal const string GemName = "Gem";

        internal static bool IsGemName(string rendererName) { return rendererName == GemName; }

        // Which display elements get an orange twin. firstAdjective[i] = the adjective code name of element i when it has exactly ONE condition (null otherwise).
        // Nothing when a Stick element exists already (a second Start of a pooled object must not add the twins again).
        internal static List<int> ElementsToClone(string[] firstAdjective, bool hasStickElement)
        {
            List<int> result = new List<int>();
            if (hasStickElement || firstAdjective == null) return result;
            for (int i = 0; i < firstAdjective.Length; i++)
                if (firstAdjective[i] == RedWardAdjective) result.Add(i);
            return result;
        }

        // ---------------------------------------------------------------- 0.4.6: the model a worker CARRIES
        // Props.props[adjective] -> Accessories.HeldSentinelWard: a pooled, NETWORKED tool object. The host picks the key (Interaction.UpdateCarrying runs on the server only) and
        // NetworkServer.Spawn(go, key.AssetId) shows it to the guests, so the orange ward needs its own template, registered under the SAME name on every PC (the UNet asset id of a
        // template is MD5(factory + name)). These three names are part of the network format: never rename them.
        internal const string HeldFactoryName = "Accessories";
        internal const string HeldRedName = "HeldSentinelWard";
        internal const string HeldOrangeName = "HeldOrangeWard";

        // 0.4.7: orange holograms (the placement ghost and the Altar preview) are OFF by default: the game draws every other hologram blue. Needs the recipe switch as well.
        internal const bool OrangeHologramDefault = false;
        internal static bool OrangeHologram(bool switchOn, bool recipeOn) { return switchOn && recipeOn; }

        // The orange key is returned only when everything it needs exists on this PC: the switch, the tinted clone and its pool, and no earlier failure. Anything else = the red model (0.4.5).
        internal static bool UseHeldOrange(bool switchOn, bool cloneReady, bool poolReady, bool failed)
        {
            return switchOn && cloneReady && poolReady && !failed;
        }

        // 0.4.16 team filter: the orange ward does not shoot a barrel launched by a catapult of its own side. The numbers are Affiliation.Relation (None 0, Allied 1, Neutral 2, Enemy 4, Undefined 8);
        // the same faction counts as Allied in the game's own GetRelation. Unknown origin (no catapult, a catapult without a faction) or any other relation = shoot, as in 0.4.15.
        internal const int RelationAllied = 1;
        internal const bool SkipAlliedDefault = true;
        internal static bool SkipAlliedBarrel(bool switchOn, bool originKnown, int relation)
        {
            return switchOn && originKnown && relation == RelationAllied;
        }

        // The first missing piece, for the log ("" when the orange model is used).
        internal static string HeldWhyRed(bool switchOn, bool cloneReady, bool poolReady, bool failed)
        {
            if (failed) return "the clone failed earlier";
            if (!switchOn) return "switched off ([Fixes] OrangeWardHeldModel or OrangeWardCraftable is false)";
            if (!cloneReady) return "the clone does not exist yet";
            if (!poolReady) return "the pool does not exist yet";
            return "";
        }
    }
}
