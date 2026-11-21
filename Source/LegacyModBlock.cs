using Brix.Assets;
using HarmonyLib;

namespace CastleStorySteam
{
    // 0.4.23 (user: 'виріжи лишнє: сніг, хелловін, події такі'): the seasonal code (SeasonalEvents, SeasonalCalendar, WinterEvents, WinterWeather, VisualOptions) is deleted.
    // Older installers left an ACTIVE Lua mod Info/Lua/Mods/halloween2017 (its clientside.lua puts a pumpkin moon on the skybox on level ready). The game loads every
    // Info/Lua/Mods folder at every match (GameLoader.StartLua -> Asset_Mod.Disk_LoadMod). The old SeasonalEvents prefix skipped it outside Halloween; with the seasonal code
    // gone, this prefix skips it always, so the moon stays off on PCs that still have the folder. Files on disk are not touched. Local visuals only (its serverside.lua is empty).
    internal static class LegacyModBlock
    {
        static Harmony harmony;

        internal static void Install()
        {
            if (harmony != null) return;
            Harmony h = new Harmony("local.castlestory.steambridge.legacymodblock");
            h.Patch(AccessTools.Method(typeof(Asset_Mod), "Disk_LoadMod"), new HarmonyMethod(typeof(LegacyModBlock), "Skip"));
            harmony = h;
        }

        internal static void Shutdown()
        {
            if (harmony != null) { harmony.UnpatchSelf(); harmony = null; }
        }

        // same body as the 0.4.20 SeasonalEvents.AllowOriginalMoon prefix (out Script = null, original skipped), without the date check
        static bool Skip(Asset_Mod __instance, object[] __args)
        {
            if (__instance == null || __instance.shortCodename != "halloween2017") return true;
            __args[0] = null;
            return false;
        }
    }
}
