using System;
using Brix.Game.AI;
using HarmonyLib;

// 0.3.97: zone tools (Cleanup / harvest circles, quarry and landscape rectangles) are capped by Project.MaxSize (default 16) and the beacon's own MaxRadius, so the circle cannot be
// dragged wider. This raises both caps to Fixes/ZoneMaxSize (default 200, 0 = leave the game's value) for every project when it starts and for every beacon when its circle handle is built.
// The host must have it too: the host clamps the radius it accepts (CmdAccepterRayon -> Beacon.radius).
internal static class ZoneSizeFix
{
    static Func<int> target;
    internal static void Install(Harmony harmony, Func<int> size)
    {
        target = size;
        harmony.Patch(AccessTools.Method(typeof(Project), "Start"), null, new HarmonyMethod(AccessTools.Method(typeof(ZoneSizeFix), "AfterProjectStart")), null, null, null);
        Type beacon = AccessTools.TypeByName("Brix.Legacy.Beacon");
        if (beacon != null) harmony.Patch(AccessTools.Method(beacon, "InstantiateCircleDrag"), new HarmonyMethod(AccessTools.Method(typeof(ZoneSizeFix), "BeforeCircle")), null, null, null, null);
    }
    internal static void Clear() { target = null; }

    static void AfterProjectStart(Project __instance)
    {
        try { int n = target == null ? 0 : target(); if (n > 0 && __instance != null && __instance.MaxSize < n) __instance.MaxSize = n; } catch (Exception) { }
    }

    static void BeforeCircle(object __instance)
    {
        try
        {
            int n = target == null ? 0 : target(); if (n <= 0 || __instance == null) return;
            Type t = __instance.GetType();
            System.Reflection.FieldInfo max = AccessTools.Field(t, "MaxRadius");
            if (max != null && (int)max.GetValue(__instance) < n) max.SetValue(__instance, n);
            // 0.4.27 review: Beacon.project is a property (GetComponentInParent<Project>), not a field. The field lookup always failed (real logs: "AccessTools.Field: Could not find field for type
            // Brix.Legacy.Beacon and name project" at every circle), so the project's cap was never raised here and a circle built before Project.Start (spawn callback) kept the game's own limit.
            System.Reflection.PropertyInfo pp = AccessTools.Property(t, "project");
            object project = pp != null ? pp.GetValue(__instance, null) : null;
            Project p = project as Project; if (p != null && p.MaxSize < n) p.MaxSize = n;
        }
        catch (Exception) { }
    }
}
