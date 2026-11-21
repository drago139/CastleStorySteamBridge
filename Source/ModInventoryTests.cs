using System;
using System.Collections.Generic;
using CastleStorySteam;

// MOD-INV (0.4.14): ModInventoryRules and the committed ModBaseline without the game: the data scope and exclusions, the baseline manifest (unpack, parse, classify, missing,
// build check), MODS-HASH, the lobby verdict, the Lua anchor book, Workshop kinds, path privacy. The glue (Chainloader, Harmony, files, Steam lobby) needs the game.
static class ModInventoryTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    static int Main()
    {
        try
        {
            // scope
            Check(ModInventoryRules.InScope("Castle Story_Data/Managed/Assembly-CSharp-firstpass.dll") && ModInventoryRules.InScope("Info/Lua/LUI/Menus/GameMenu.lua") && ModInventoryRules.InScope(@"Info\Lua\Mods\halloween2017\clientside.lua"), "managed DLLs and Lua are in scope (both slash kinds)");
            Check(ModInventoryRules.InScope("Info/Languages/English/language.csv") && ModInventoryRules.InScope("Info/Checksum/gamemodes.json") && ModInventoryRules.InScope("Info/Livre/a.txt") && ModInventoryRules.InScope("Info/names.json"), "languages, checksum, livre and the files directly in Info");
            Check(!ModInventoryRules.InScope("Info/Maps/Test Catapults/meta.json") && !ModInventoryRules.InScope("Castle Story_Data/sharedassets6.assets") && !ModInventoryRules.InScope("BepInEx/plugins/x.dll") && !ModInventoryRules.InScope("Info"), "maps, big assets, BepInEx and a bare folder are out");
            Check(!ModInventoryRules.InScope("Info/Saves/a/b.json") && !ModInventoryRules.InScope("Info/UGC/Mods/x/clientside.lua") && !ModInventoryRules.InScope("Info/Downloads/Maps/x/meta.json") && !ModInventoryRules.InScope("Info/UserSettings/Current.json"), "user data is out");
            Check(!ModInventoryRules.InScope("Info/Lua/userconfigs.lua") && !ModInventoryRules.InScope("Info/stats.json") && !ModInventoryRules.InScope("Info/game_analytics_events.json") && !ModInventoryRules.InScope("Info/Lua/_removed-by-claude-20261101/x.lua"), "files the game rewrites and our removed folder are out");
            Check(!ModInventoryRules.InScope("Info/Lua/LUI/Menus/GameMenu.lua.bak-0399") && !ModInventoryRules.InScope("Info/Lua/LUI/Meta/Meta_Structure.lua.bak-orange") && !ModInventoryRules.InScope("Info/Lua/LUI/Menus/Game/SidebarCommandMenu.lua.bak"), "our .bak backups are out");
            Check(!ModInventoryRules.InScope("Info/Lua/Gamemodes/world_editor/Menu/WorldEditorMenu.lua.orig") && !ModInventoryRules.InScope("Info/Lua/userconfigs.lua.bak-before-AB") && !ModInventoryRules.InScope("Info/Lua/x.lua.bytecode"), ".orig, .bak-before and .bytecode are out");
            Check(ModInventoryRules.InScope("Info/Lua/x.older.lua") && ModInventoryRules.InScope("Info/Lua/bakery.lua") && ModInventoryRules.InScope("Info/Lua/Mods/halloween2017.bak/clientside.lua"), "markers match whole suffixes only; a backup FOLDER under Mods is kept (the game loads it)");
            Check(ModInventoryRules.CountsForModsHash("Info/Lua/a.lua") && !ModInventoryRules.CountsForModsHash("Info/Languages/English/language.csv") && !ModInventoryRules.CountsForModsHash("Info/LodManagerSettings.json"), "languages and the LOD profile stay out of MODS-HASH");

            // manifest: pack round trip, parse, classify, missing, build check
            string text = "#version v1.8.8\n#firstpass cf5b5406\nInfo/Lua/a.lua|AAAAAAAA\nInfo/Lua/B.lua|BBBBBBBB\nCastle Story_Data/Managed/x.dll|CCCCCCCC\n+Info/Lua/a.lua|11111111\n+Info/Lua/a.lua|22222222\n+Info/Lua/new.lua|33333333\n";
            Check(ModInventoryRules.Unpack(ModInventoryRules.Pack(text)) == text, "pack / unpack round trip");
            ModManifest m = ModInventoryRules.Parse(text);
            Check(m.Version == "v1.8.8" && m.Firstpass == "CF5B5406" && m.Base.Count == 3 && m.Ours.Count == 2 && m.Ours["info/lua/a.lua"].Count == 2, "parse: header, base entries, our variants");
            Check(ModInventoryRules.Classify(m, "info/lua/A.LUA", "aaaaaaaa") == DataState.Same && ModInventoryRules.Classify(m, "Info/Lua/a.lua", "22222222") == DataState.Ours, "same and ours (case-insensitive)");
            Check(ModInventoryRules.Classify(m, "Info/Lua/a.lua", "DEADBEEF") == DataState.Changed && ModInventoryRules.Classify(m, "Info/Lua/zzz.lua", "DEADBEEF") == DataState.Added && ModInventoryRules.Classify(m, "Info/Lua/new.lua", "33333333") == DataState.Ours, "changed, added, an added file of ours");
            List<string> missing = ModInventoryRules.Missing(m, new HashSet<string>(new string[] { "info/lua/a.lua", "castle story_data/managed/x.dll" }));
            Check(missing.Count == 1 && missing[0] == "Info/Lua/B.lua", "missing keeps the written case");
            Check(ModInventoryRules.SameBuild(m, "v1.8.8 ", "CF5B5406") && !ModInventoryRules.SameBuild(m, "v1.8.9", "CF5B5406") && !ModInventoryRules.SameBuild(m, "v1.8.8", "00000000") && !ModInventoryRules.SameBuild(new ModManifest(), "v1.8.8", "CF5B5406"), "build check: version and firstpass must both match");

            // the committed baseline (Make-ModBaseline.ps1 from OriginalBuild)
            ModManifest real = ModInventoryRules.Parse(ModInventoryRules.Unpack(ModBaseline.Packed));
            Check(real.Base.Count == ModBaseline.BaseEntries && real.Base.Count > 700, "baseline: " + real.Base.Count + " entries as generated");
            Check(real.Version == ModBaseline.Version && real.Firstpass == ModBaseline.Firstpass && real.Version == "v1.8.8" && real.Firstpass == "CF5B5406", "baseline header = Castle Story v1.8.8, firstpass CF5B5406");
            int bad = 0, outOfScope = 0, ourVariants = 0;
            foreach (KeyValuePair<string, string> pair in real.Base) { if (pair.Value.Length != 8) bad++; if (!ModInventoryRules.InScope(pair.Key)) outOfScope++; }
            foreach (KeyValuePair<string, List<string>> pair in real.Ours) ourVariants += pair.Value.Count;
            Check(bad == 0 && outOfScope == 0, "every baseline entry has an 8-char hash and is in scope (" + bad + " bad, " + outOfScope + " out of scope)");
            Check(ourVariants == ModBaseline.OurEntries, "our known variants: " + ourVariants);
            Check(ModInventoryRules.Classify(real, "Castle Story_Data/Managed/Assembly-CSharp-firstpass.dll", "CF5B5406") == DataState.Same, "the pristine firstpass is 'same'");
            Check(ModInventoryRules.Classify(real, "Info/Lua/LUI/Meta/Meta_Structure.lua", "F54CD36A") == DataState.Ours && ModInventoryRules.Classify(real, "Info/Lua/LUI/Meta/Meta_Structure.lua", "A0FC6597") == DataState.Same, "our GameLua copy is 'ours', vanilla is 'same'");
            Check(ModInventoryRules.Classify(real, "Info/Languages/English/language.csv", "ED8594DD") == DataState.Ours && ModInventoryRules.Classify(real, "Info/Lua/Gamemodes/capturethepumpkin/clientside.lua", "00C19E29") == DataState.Ours, "our seasonal language and game mode files are 'ours'");
            Check(ModInventoryRules.Classify(real, "Info/Lua/LUI/Meta/Meta_Structure.lua", "12345678") == DataState.Changed, "a foreign edit of a base file is 'changed'");

            // hashes
            Check(ModInventoryRules.Hash8("abc") == "90015098" && ModInventoryRules.Hash8(new byte[0]) == "D41D8CD9", "MD5 first 8 hex (known vectors)");
            string h1 = ModInventoryRules.ModsHash(new string[] { "b@1#2", "a@1#1" }, new string[] { "changed x#1", "added y#2" });
            string h2 = ModInventoryRules.ModsHash(new string[] { "a@1#1", "b@1#2" }, new string[] { "added y#2", "changed x#1" });
            string h3 = ModInventoryRules.ModsHash(new string[] { "a@1#1", "b@1#3" }, new string[] { "added y#2", "changed x#1" });
            Check(h1 == h2 && h1 != h3 && h1.Length == 8, "MODS-HASH ignores the order and changes with any entry");
            Check(ModInventoryRules.ModsHash(new string[] { "x" }, new string[0]) != ModInventoryRules.ModsHash(new string[0], new string[] { "x" }), "a plugin entry and a data entry with the same text differ");

            // lobby
            Check(ModInventoryRules.LobbyVerdict("", "AB") == "n/a" && ModInventoryRules.LobbyVerdict(null, "AB") == "n/a" && ModInventoryRules.LobbyVerdict("AB", "") == "pending", "lobby: older host / own hash not ready");
            Check(ModInventoryRules.LobbyVerdict("ab12cd34", "AB12CD34") == "match" && ModInventoryRules.LobbyVerdict("AB12CD34", "AB12CD35") == "mismatch", "lobby: match / mismatch");

            // anchors
            Check(ModInventoryRules.AnchorCount("return _t\nx\nreturn _t", "return _t") == 2 && ModInventoryRules.AnchorCount("abc", "zz") == 0 && ModInventoryRules.AnchorCount(null, "a") == 0, "anchor count");
            Check(ModInventoryRules.AnchorState(1, true) == "found" && ModInventoryRules.AnchorState(0, false) == "NOT FOUND" && ModInventoryRules.AnchorState(2, true) == "NOT UNIQUE (2)" && ModInventoryRules.AnchorState(3, false) == "found", "anchor state");
            AnchorBook book = new AnchorBook();
            Check(book.Note("a", "found") && !book.Note("a", "found") && book.Note("a", "NOT FOUND") && book.Note("b", "found"), "anchor book: first sight and changes only");
            Check(book.Count == 2 && book.Bad == 1 && book.BadKeys().Count == 1 && book.BadKeys()[0] == "a NOT FOUND", "anchor book counts");

            // workshop kinds
            Check(ModInventoryRules.WorkshopKind(new HashSet<string>(new string[] { "gameobjects.json", "meta.json" })) == "map" && ModInventoryRules.WorkshopKind(new HashSet<string>(new string[] { "graph.json" })) == "blueprint", "workshop: map, blueprint");
            Check(ModInventoryRules.WorkshopKind(new HashSet<string>(new string[] { "clientside.lua", "map_validator.lua" })) == "gamemode" && ModInventoryRules.WorkshopKind(new HashSet<string>(new string[] { "clientside.lua", "meta.json" })) == "mod", "workshop: a game mode before a mod");
            Check(ModInventoryRules.WorkshopKind(new HashSet<string>(new string[] { "language.csv" })) == "language" && ModInventoryRules.WorkshopKind(new HashSet<string>(new string[] { "x.json" })) == "preset" && ModInventoryRules.WorkshopKind(new HashSet<string>()) == "empty", "workshop: language, preset, empty");
            Check(ModInventoryRules.WorkshopHashed("mod") && ModInventoryRules.WorkshopHashed("gamemode") && ModInventoryRules.WorkshopHashed("language") && !ModInventoryRules.WorkshopHashed("map"), "workshop: only mods, modes and languages are hashed");

            // privacy
            Check(ModInventoryRules.RelativeOrName(@"D:\SteamLibrary\steamapps\common\Castle Story\BepInEx\plugins\x.dll", @"D:\SteamLibrary\steamapps\common\Castle Story") == "BepInEx/plugins/x.dll", "a game path becomes relative");
            Check(ModInventoryRules.RelativeOrName(@"C:\Users\someone\Desktop\x.dll", @"D:\SteamLibrary\steamapps\common\Castle Story") == "x.dll" && ModInventoryRules.RelativeOrName("", "r") == "", "outside the game folder only the file name");
            Check(ModInventoryRules.Cut(new string('x', 400)).Length == ModInventoryRules.LineMax && ModInventoryRules.Cut("short") == "short", "lines are cut for the friend report");

            Console.WriteLine("PASS: " + checks + " mod inventory checks (scope and exclusions, baseline " + real.Base.Count + " entries + " + ourVariants + " of ours, classify, MODS-HASH, lobby verdict, anchors, workshop kinds, privacy).");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; }
    }
}
