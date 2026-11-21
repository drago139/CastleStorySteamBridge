using System;
using HarmonyLib;

// 0.3.97: the game shows the English pop-up "Cannot use box selection in Edit mode" whenever a selection rectangle is dragged in debug / edit mode
// (EditModeCursorPicker.ToggleDragFeedback). It is only the notice, box selection stays disabled by the game. Config Fixes/HideEditModeBoxSelectionNotice (default true).
internal static class HideEditModeNotice
{
    static Func<bool> enabled;
    internal static void Install(Harmony harmony, Func<bool> isEnabled)
    {
        enabled = isEnabled;
        Type picker = AccessTools.TypeByName("Brix.Input.EditModeCursorPicker"); if (picker == null) throw new MissingMemberException("EditModeCursorPicker");
        harmony.Patch(AccessTools.Method(picker, "ToggleDragFeedback"), new HarmonyMethod(AccessTools.Method(typeof(HideEditModeNotice), "Before")), null, null, null, null);
    }
    internal static void Clear() { enabled = null; }
    static bool Before() { return enabled == null || !enabled(); }   // false = skip the original, so no notice
}
