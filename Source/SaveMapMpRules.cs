using System;

namespace CastleStorySteam
{
    // 0.4.20 SAVE-MP, pure part (no Unity; tested by SaveMapMpTests.cs). The game part is SaveMapMp.cs.
    // User 2026-11-07: "дороби пісочницю щоб можна було зберігати карти, додай кнопку якщо нема". In a multiplayer match the game hides Pause and Quicksave
    // (GameMenu.lua: "if not IsMultiplayer() ..."), so the host had no way to save. This adds one "Зберегти карту" button in the settings section (before Screenshot),
    // shown only to the host; every press writes a NEW save folder with a unique name (never overwrites: Disk_WriteAsNew deletes a same-name folder).
    internal static class SaveMapMpRules
    {
        internal const string Prefix = "MP_";
        internal const int MaxTries = 50;              // MP_<time>, MP_<time>_2 .. MP_<time>_50
        internal const float BusySeconds = 3f;         // a second press within 3 s is ignored (double click)
        internal const string Marker = "---SB-SAVE-MP";
        internal const string Label = "Зберегти карту";

        // why the save is not allowed now; null = allowed
        internal static string Why(bool enabled, bool multiplayer, bool server, bool inMatch, bool levelReady, bool haveMap)
        {
            if (!enabled) return "вимкнено в налаштуваннях ([Fixes] SaveMapInMultiplayer)";
            if (!multiplayer) return "не мультиплеєр (у грі на одного є звичайне збереження)";
            if (!server) return "зберігати може лише хост";
            if (!inMatch) return "не в матчі";
            if (!levelReady) return "карта ще завантажується";
            if (!haveMap) return "немає даних карти (GameParam.Map)";
            return null;
        }

        internal static bool Allowed(bool enabled, bool multiplayer, bool server, bool inMatch, bool levelReady, bool haveMap)
        {
            return Why(enabled, multiplayer, server, inMatch, levelReady, haveMap) == null;
        }

        // MP_2026-11-07_23-15-04 ; attempt 2.. adds _2.. ; only digits, '-', '_' (safe as a folder name on every PC)
        internal static string Name(DateTime now, int attempt)
        {
            string n = Prefix + now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
            return attempt <= 1 ? n : n + "_" + attempt;
        }

        // first name whose folder does not exist; null when MaxTries are all taken
        internal static string PickName(DateTime now, Func<string, bool> exists)
        {
            for (int i = 1; i <= MaxTries; i++)
            {
                string n = Name(now, i);
                if (!exists(n)) return n;
            }
            return null;
        }

        internal static bool Busy(float now, float lastPress)
        {
            return lastPress > 0f && now >= lastPress && now - lastPress < BusySeconds;
        }

        // GameMenu.lua: the button block goes right before the "---screenshot" line of the settings section (that line must be unique).
        // Returns the patched code; count = how many anchor lines were found (1 = patched; 0 / 2+ = code unchanged). Already patched = unchanged, count 1.
        internal static string Patch(string code, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(code)) return code;
            int at = -1, from = 0;
            while (true)
            {
                int i = code.IndexOf("\n---screenshot", from, StringComparison.Ordinal);
                if (i < 0) break;
                int end = i + "\n---screenshot".Length;
                if (end == code.Length || code[end] == '\r' || code[end] == '\n') { count++; if (at < 0) at = i + 1; }
                from = end;
            }
            if (count != 1) return code;
            if (code.IndexOf(Marker, StringComparison.Ordinal) >= 0) return code;
            string nl = code.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            return code.Substring(0, at) + Block(nl) + code.Substring(at);
        }

        // Visible asks C# every time the menu is drawn (host / match / switch); OnAction asks again before saving (guests get a "host only" note).
        internal static string Block(string nl)
        {
            string[] l =
            {
                Marker + " (Castle Story Steam Bridge 0.4.20): host-only save in multiplayer",
                "if SB_SaveMapAllowed and SB_SaveMap then",
                "\tdo",
                "\tlocal h = ButtonHandle.New()",
                "\th.Visible = ||SB_SaveMapAllowed()",
                "\th.Icon = ||IconKeys.UI_Quicksave:Get64()",
                "\th.IconColor = ||CastleYellow",
                "\th.IconSize = ||32",
                "\th.Label = ||\"" + Label + "\"",
                "\th.hasFgLabel = true",
                "\th.OnAction = ||SB_SaveMap()",
                "",
                "\t_m.mg.settings:AddChild(h)",
                "\t_m.mh.settings.onLoad:AddListener(function(m) m.AddMenuHandleToggle(h, _m.sh.settings) end)",
                "\tend",
                "end",
                ""
            };
            return string.Join(nl, l) + nl;
        }

        internal static string LogLine(bool ok, string name, string detail)
        {
            return "SAVE-MP " + (ok ? "saved " : "FAILED ") + (name ?? "-") + ": " + (detail ?? "");
        }
    }
}
