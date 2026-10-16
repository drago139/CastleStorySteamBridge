using BepInEx.Configuration;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.53: switches for the purely visual seasonal additions, saved in the config ([Visuals]) and shown in the F8 window.
    // They change only what is drawn on this PC; nothing in the game rules or the save is touched.
    internal static class VisualOptions
    {
        internal static bool Costumes = true, Moon = true, WinterSnow = true, WinterHats = true;
        static ConfigEntry<bool> eCostumes, eMoon, eSnow, eHats;

        internal static void Bind(ConfigFile c)
        {
            eCostumes = c.Bind("Visuals", "HalloweenCostumes", true, "Halloween costumes (hats) on the gnomes.");
            eMoon = c.Bind("Visuals", "HalloweenMoon", true, "Pumpkin moon in the sky (Capture the Pumpkin mode).");
            eSnow = c.Bind("Visuals", "WinterSnow", true, "Winter: snow on the ground, crystals and home trees, falling snow.");
            eHats = c.Bind("Visuals", "WinterHats", true, "Winter: red hats on the gnomes.");
            Costumes = eCostumes.Value; Moon = eMoon.Value; WinterSnow = eSnow.Value; WinterHats = eHats.Value;
        }

        static bool Row(string label, bool value, ConfigEntry<bool> entry)
        {
            bool now = GUILayout.Toggle(value, label);
            if (now != value && entry != null) entry.Value = now;
            return now;
        }

        internal static void Draw()
        {
            GUILayout.Label("Візуальні ефекти (вимкни, якщо заважають; ігрові правила не міняються):");
            Costumes = Row("Геловін: костюми на гномах", Costumes, eCostumes);
            Moon = Row("Геловін: гарбузовий місяць", Moon, eMoon);
            WinterSnow = Row("Зима: сніг на землі, кристалах, деревах і снігопад", WinterSnow, eSnow);
            WinterHats = Row("Зима: червоні шапки на гномах", WinterHats, eHats);
        }
    }
}
