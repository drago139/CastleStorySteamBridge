using System;
using System.Reflection;
using Brix.UI.Builder.Menu;
using HarmonyLib;

// 0.4.24 GROUPS-MENU PROBE GATE (frame cost). The unit-groups key menu (GroupsMenu.cs, GroupsMenuTick) asked FindObjectOfType<LegacyKeybindingsCategoriesMenu>() every 0.4 s, in every scene and
// even when no settings menu was open. That scan walks the loaded objects: it is the top (timing-only, unmeasured) suspect for the ~150 per minute frames of 33-67 ms in the diagnostics
// (150 per minute = exactly one per 0.4 s). The menu object announces itself (OnEnable / OnDisable), so the poll can wait: while no such menu is enabled, the plugin probes every 30 s (0.4.27 review: was 5 s; a safety
// net if a hook call was missed) and at once when the menu is enabled. If a hook cannot be installed, Hooked stays false and the old 0.4 s poll is kept. Switch: [Fixes] GroupsMenuProbeGate.
internal static class GroupsMenuGate
{
    internal static bool Hooked;
    static int enabledMenus;
    static bool wake;

    internal static bool MenuMaybeEnabled { get { return enabledMenus > 0; } }
    // true once after a menu was enabled: the caller probes at once
    internal static bool TakeWake() { if (!wake) return false; wake = false; return true; }
    internal static void Reset() { enabledMenus = 0; wake = false; }

    internal static void Install(Harmony harmony)
    {
        MethodInfo on = AccessTools.Method(typeof(LegacyKeybindingsCategoriesMenu), "OnEnable");
        MethodInfo off = AccessTools.Method(typeof(LegacyKeybindingsCategoriesMenu), "OnDisable");
        if (on == null || off == null) return;
        harmony.Patch(on, null, new HarmonyMethod(AccessTools.Method(typeof(GroupsMenuGate), "AfterEnable")), null, null, null);
        harmony.Patch(off, null, new HarmonyMethod(AccessTools.Method(typeof(GroupsMenuGate), "AfterDisable")), null, null, null);
        Hooked = true;
    }

    static void AfterEnable() { enabledMenus++; wake = true; }
    static void AfterDisable() { if (enabledMenus > 0) enabledMenus--; }
}
