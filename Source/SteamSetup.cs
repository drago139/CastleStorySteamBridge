using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

static class Setup
{
    internal static readonly Encoding Utf8 = new UTF8Encoding(false);
    internal static byte[] Resource(string name)
    {
        string expected;
        if (!ComponentHashes.Values.TryGetValue(name, out expected)) throw new IOException("Невідомий компонент: " + name);
        byte[] data;
        using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (source == null) throw new IOException("У встановлювачі відсутній компонент: " + name + ". Файли гри не змінено.");
            using (MemoryStream output = new MemoryStream())
            { source.CopyTo(output); data = output.ToArray(); }
        }
        using (SHA256 sha = SHA256.Create())
            if (BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "") != expected)
                throw new IOException("Контрольна сума компонента не збігається: " + name + ". Файли гри не змінено.");
        return data;
    }
    internal static string ChangeLanguage(string json)
    { return ChangeLanguage(json,"Українська"); }
    internal static string ChangeLanguage(string json,string selectedLanguage)
    {
        if(String.IsNullOrEmpty(selectedLanguage)||selectedLanguage.IndexOf('\0')>=0)throw new ArgumentException("Некоректна мова.");
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> settings;
        try { settings = serializer.DeserializeObject(json.TrimStart('\uFEFF')) as Dictionary<string, object>; }
        catch (ArgumentException e) { throw new IOException("Файл налаштувань містить некоректний JSON. Файли гри не змінено.", e); }
        if (settings == null) throw new IOException("Налаштування мають бути об'єктом JSON. Файли гри не змінено.");
        object value;
        Dictionary<string, object> localization;
        if (!settings.TryGetValue("localization", out value))
        { localization = new Dictionary<string, object>(); settings.Add("localization", localization); }
        else
        { localization = value as Dictionary<string, object>; if (localization == null) throw new IOException("Некоректний розділ localization. Файли гри не змінено."); }
        localization["language"] = selectedLanguage;
        return serializer.Serialize(settings);
    }
    internal static bool IsGame(string path)
    {
        return !String.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "Castle Story.exe"))
            && Directory.Exists(Path.Combine(path, "Castle Story_Data", "Managed"))
            && Directory.Exists(Path.Combine(path, "Info", "UserSettings"));
    }
    internal static string FindGame()
    {
        List<string> libraries = new List<string>();
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            if (key != null) libraries.Add(Convert.ToString(key.GetValue("SteamPath", "")));
        }
        libraries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        List<string> roots = new List<string>(libraries);
        foreach (string steam in roots)
        {
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"(?:path|[0-9]+)\"\\s*\"([^\"]+)\""))
            {
                string candidate = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Path.IsPathRooted(candidate)) libraries.Add(candidate);
            }
        }
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            libraries.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            libraries.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
        }
        foreach (string library in libraries)
        {
            if (String.IsNullOrEmpty(library)) continue;
            string folder = Path.Combine(library, "steamapps", "common", "Castle Story");
            if (IsGame(folder)) return folder;
            string manifest = Path.Combine(library, "steamapps", "appmanifest_227860.acf");
            if (!File.Exists(manifest)) continue;
            Match item = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
            if (item.Success)
            {
                folder = Path.Combine(library, "steamapps", "common", item.Groups[1].Value);
                if (IsGame(folder)) return folder;
            }
        }
        return "";
    }
    sealed class Transaction
    {
        readonly string root, backup;
        readonly Dictionary<string, byte[]> previous = new Dictionary<string, byte[]>();
        internal Transaction(string path)
        {
            root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            backup = Path.Combine(path, "CodexSetupBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        }
        internal string Backup { get { return backup; } }
        internal void Put(string relative, byte[] data)
        {
            string target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Некоректний шлях компонента.");
            if (!previous.ContainsKey(target))
            {
                byte[] old = File.Exists(target) ? File.ReadAllBytes(target) : null;
                if (old != null)
                {
                    string saved = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)); File.WriteAllBytes(saved, old);
                }
                previous.Add(target, old);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllBytes(target, data);
        }
        internal void Rollback()
        {
            foreach (KeyValuePair<string, byte[]> file in previous)
            {
                if (file.Value == null) { if (File.Exists(file.Key)) File.Delete(file.Key); }
                else File.WriteAllBytes(file.Key, file.Value);
            }
        }
    }
    // Keeps the logs of the previous run before the next start overwrites them; the bridge later sends a
    // network-only summary of them to a friend. Best effort: it must never block the installation.
    internal static void RescueLogs(string path)
    {
        try
        {
            string dir = Path.Combine(path, "BepInEx", "SteamBridgeDiagnostics", "previous");
            foreach (string rel in new string[] { Path.Combine("Castle Story_Data", "output_log.txt"), Path.Combine("BepInEx", "LogOutput.log") })
            {
                string source = Path.Combine(path, rel);
                if (!File.Exists(source)) continue;
                FileInfo info = new FileInfo(source);
                if (info.Length < 2000) continue;
                Directory.CreateDirectory(dir);
                string name = info.LastWriteTime.ToString("yyyyMMdd-HHmmss") + "-" + Path.GetFileNameWithoutExtension(rel) + ".txt";
                string target = Path.Combine(dir, name);
                if (File.Exists(target) || File.Exists(target + ".sent")) continue;
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write))
                    input.CopyTo(output);
            }
        }
        catch (Exception) { }
    }
    internal static string Install(string path, bool ukrainian, bool timeout, bool playerPrefs)
    {return Install(path,ukrainian?"Українська":null,timeout,playerPrefs);}
    internal static string Install(string path,string selectedLanguage,bool timeout,bool playerPrefs)
    {
        GameGuard.RequireClosed(); if (!IsGame(path)) throw new IOException("Оберіть папку оригіналу, де є Castle Story.exe.");
        // Validate every supplied component before writing any game files.
        foreach (string component in ComponentHashes.Values.Keys) Resource(component);
        
        Dictionary<string, byte[]> languageSettings = new Dictionary<string, byte[]>();
        if (!String.IsNullOrEmpty(selectedLanguage))
        {
            foreach (string name in new string[] { "Current.json", "Default.json" })
            {
                string rel = Path.Combine("Info", "UserSettings", name);
                languageSettings.Add(rel, Utf8.GetBytes(ChangeLanguage(File.ReadAllText(Path.Combine(path, rel), Encoding.UTF8),selectedLanguage)));
            }
        }
        bool existingRuntime = File.Exists(Path.Combine(path, "BepInEx", "core", "BepInEx.dll"));
        if (existingRuntime)
        {
            Version version = AssemblyName.GetAssemblyName(Path.Combine(path, "BepInEx", "core", "BepInEx.dll")).Version;
            string cecil = Path.Combine(path, "BepInEx", "core", "Mono.Cecil.dll");
            if (version.Major != 5 || !File.Exists(cecil) || AssemblyName.GetAssemblyName(cecil).Version != new Version(0, 10, 4, 0))
                throw new IOException("Знайдено несумісну версію BepInEx. Її не змінено. Мову гри можна змінити окремою кнопкою.");
            string preloader = Path.Combine(path, "BepInEx", "core", "BepInEx.Preloader.dll");
            if (!File.Exists(preloader)) throw new IOException("BepInEx встановлений не повністю. Файли не змінено.");
            string doorstop = Path.Combine(path, "doorstop_config.ini");
            if (!File.Exists(Path.Combine(path, "winhttp.dll")) || !File.Exists(doorstop)
                || !Regex.IsMatch(File.ReadAllText(doorstop), @"(?im)^\s*enabled\s*=\s*true\s*$")
                || !Regex.IsMatch(File.ReadAllText(doorstop), @"(?im)^\s*target_assembly\s*=\s*BepInEx[\\/]core[\\/]BepInEx\.Preloader\.dll\s*$"))
                throw new IOException("Завантажувач BepInEx відсутній або вимкнений. Файли не змінено; потрібна окрема перевірка.");
        }
        RescueLogs(path);
        // 0.3.39: the 10-minute timeout patcher changes the game's UNet connection config, so players with it cannot join players
        // without it. It is opt-in: ticked = patcher + keep-marker (every friend must do the same); unticked = no patcher, old one disabled.
        {
            string patcherFile = Path.Combine(path, "BepInEx", "patchers", "CastleStoryNetworkTimeout.dll");
            string keepMarker = Path.Combine(path, "BepInEx", "config", "long-disconnect-timeout.keep");
            if (false)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(keepMarker));
                File.WriteAllText(keepMarker, "Delete this file (and restart the game) to use the standard timeout that works with players who do not have the mod.");
            }
            else
            {
                if (File.Exists(keepMarker)) File.Delete(keepMarker);
                if (File.Exists(patcherFile))
                {
                    string disabled = patcherFile + ".disabled-vanilla-compat";
                    if (File.Exists(disabled)) File.Delete(disabled);
                    File.Move(patcherFile, disabled);
                }
            }
        }
        Transaction tx = new Transaction(path);
        try
        {
            // Steam Bridge always needs the loader, even when the optional timeout is unchecked.
            if (true)
            {
                using (MemoryStream bytes = new MemoryStream(Resource("runtime.zip")))
                using (ZipArchive zip = new ZipArchive(bytes, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string name = entry.FullName.Replace('\\', '/');
                        bool patch = name == "BepInEx/patchers/CastleStoryWorkshopUpdate.dll";   // 0.3.43: the timeout is set per connection by the plugin; the global patcher is never installed
                        bool runtime = name.StartsWith("BepInEx/core/", StringComparison.Ordinal)
                            || name == "winhttp.dll" || name == "doorstop_config.ini";
                        bool halloweenMode = name.StartsWith("Info/Lua/Gamemodes/capturethepumpkin/", StringComparison.Ordinal)
                            || name == "Info/Languages/English/language.csv"
                            || name == "Info/Languages/French/language.csv"
                            || name == "Info/Languages/Ukrainian/language.csv";
                        if (entry.Name.Length == 0 || !(patch || halloweenMode || (!existingRuntime && runtime))) continue;
                        using (Stream source = entry.Open())
                        using (MemoryStream output = new MemoryStream())
                        { source.CopyTo(output); tx.Put(name.Replace('/', Path.DirectorySeparatorChar), output.ToArray()); }
                    }
                }
            }
            using (MemoryStream bytes = new MemoryStream(Resource("capturethepumpkin.zip")))
            using (ZipArchive zip = new ZipArchive(bytes, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    bool mode = name.StartsWith("Info/Lua/Gamemodes/capturethepumpkin/", StringComparison.Ordinal);
                    bool language = name == "Info/Languages/English/language.csv"
                        || name == "Info/Languages/French/language.csv"
                        || name == "Info/Languages/Ukrainian/language.csv";
                    if (entry.Name.Length == 0 || (!mode && !language)) continue;
                    using (Stream source = entry.Open())
                    using (MemoryStream output = new MemoryStream())
                    { source.CopyTo(output); tx.Put(name.Replace('/', Path.DirectorySeparatorChar), output.ToArray()); }
                }
            }
            using (MemoryStream bytes = new MemoryStream(Resource("halloween2017.zip")))
            using (ZipArchive zip = new ZipArchive(bytes, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    bool halloweenAssets = name.StartsWith("Info/Lua/Mods/halloween2017/", StringComparison.Ordinal);
                    if (entry.Name.Length == 0 || !halloweenAssets) continue;
                    using (Stream source = entry.Open())
                    using (MemoryStream output = new MemoryStream())
                    { source.CopyTo(output); tx.Put(name.Replace('/', Path.DirectorySeparatorChar), output.ToArray()); }
                }
            }
            if (selectedLanguage=="Українська")
            {
                tx.Put(Path.Combine("Info", "Languages", "Ukrainian", "language.csv"), Resource("uk.csv"));
                tx.Put(Path.Combine("Info", "Languages", "Ukrainian", "meta.json"), Resource("uk.json"));
            }
            if (!String.IsNullOrEmpty(selectedLanguage))
            {
                foreach (KeyValuePair<string, byte[]> setting in languageSettings) tx.Put(setting.Key, setting.Value);
                if (playerPrefs)
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Sauropod Studio\Castle Story"))
                    {
                        byte[] old = key.GetValue("I2 Language_h3293684300") as byte[];
                        Directory.CreateDirectory(tx.Backup);
                        if (old != null) File.WriteAllBytes(Path.Combine(tx.Backup, "I2-Language-before.bin"), old);
                        key.SetValue("I2 Language_h3293684300", Utf8.GetBytes(selectedLanguage+"\0"), RegistryValueKind.Binary);
                    }
                }
            }
            tx.Put(Path.Combine("BepInEx", "plugins", "CastleStorySteamBridge.dll"), Resource("bridge.dll"));
            // Signature that lets this copy hand the update to friends who run an older bridge.
            tx.Put(Path.Combine("BepInEx", "plugins", "CastleStorySteamBridge.dll.sig"), Resource("bridge.dll.sig"));
            LegacyTrialCleanup.Archive(path);
            return tx.Backup;
        }
        catch { tx.Rollback(); throw; }
    }
    internal static string ApplyLanguage(string path,string selectedLanguage,bool playerPrefs)
    {
        GameGuard.RequireClosed();if(!IsGame(path))throw new IOException("Оберіть папку оригіналу, де є Castle Story.exe.");
        Dictionary<string,byte[]> changed=new Dictionary<string,byte[]>();
        foreach(string name in new[]{"Current.json","Default.json"}) {
            string rel=Path.Combine("Info","UserSettings",name);
            changed[rel]=Utf8.GetBytes(ChangeLanguage(File.ReadAllText(Path.Combine(path,rel),Encoding.UTF8),selectedLanguage));
        }
        if(selectedLanguage=="Українська") {
            changed[Path.Combine("Info","Languages","Ukrainian","language.csv")]=Resource("uk.csv");
            changed[Path.Combine("Info","Languages","Ukrainian","meta.json")]=Resource("uk.json");
        }
        Transaction tx=new Transaction(path);
        try {
            foreach(KeyValuePair<string,byte[]> item in changed)tx.Put(item.Key,item.Value);
            if(playerPrefs)using(RegistryKey key=Registry.CurrentUser.CreateSubKey(@"Software\Sauropod Studio\Castle Story")) {
                byte[] old=key.GetValue("I2 Language_h3293684300") as byte[];
                Directory.CreateDirectory(tx.Backup);if(old!=null)File.WriteAllBytes(Path.Combine(tx.Backup,"I2-Language-before.bin"),old);
                key.SetValue("I2 Language_h3293684300",Utf8.GetBytes(selectedLanguage+"\0"),RegistryValueKind.Binary);
            }
            return tx.Backup;
        }catch{tx.Rollback();throw;}
    }
    internal static void SelfTest()
    {
        NetworkSelfTest.Run();
        UpdateSelfTest.Run();
        DesktopUpdateSelfTest.Run();
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        foreach (string input in new string[] {
            "{\"network\":{\"Port\":\"3000\",\"Name\":\"Friend\"},\"localization\":{\"other\":false,\"language\":\"english\"}}",
            "{\"network\":{\"Port\":\"3000\",\"Name\":\"Friend\"},\"localization\":{}}",
            "{\"network\":{\"Port\":\"3000\",\"Name\":\"Friend\"}}"
        })
        {
            Dictionary<string, object> parsed = serializer.DeserializeObject(ChangeLanguage(input)) as Dictionary<string, object>;
            Dictionary<string, object> network = (Dictionary<string, object>)parsed["network"];
            Dictionary<string, object> language = (Dictionary<string, object>)parsed["localization"];
            if ((string)network["Name"] != "Friend" || (string)network["Port"] != "3000" || (string)language["language"] != "Українська") throw new Exception("Language format/preservation test failed");
            if (input.Contains("other") && (!language.ContainsKey("other") || (bool)language["other"])) throw new Exception("Localization field preservation failed");
        }
        string root = Path.Combine(Path.GetTempPath(), "CastleStorySetupTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Castle Story_Data", "Managed"));
        Directory.CreateDirectory(Path.Combine(root, "Info", "UserSettings"));
        File.WriteAllText(Path.Combine(root, "Castle Story.exe"), "fixture");
        // A log left by a previous run: the installer must save it, and the summary must keep only network lines.
        StringBuilder oldLog = new StringBuilder();
        for (int i = 0; i < 80; i++) oldLog.AppendLine("filler line " + i + " with C:\\Users\\Someone\\file.txt");
        oldLog.AppendLine("[Warning:Castle Story Steam Bridge (experimental)] Main game loop pause: 4.50 s; bridge work in previous frame 0.1 ms");
        oldLog.AppendLine("[Warning:Castle Story Steam Bridge (experimental)] Game data gap: peer=76561190000000001; age=4.0 s; sent=10; received=20");
        oldLog.AppendLine("NullReferenceException");
        File.WriteAllText(Path.Combine(root, "Castle Story_Data", "output_log.txt"), oldLog.ToString());
        string settings = "{\"network\":{\"Port\":3000},\"localization\":{\"language\":\"english\"},\"other\":42}";
        foreach (string name in new string[] { "Current.json", "Default.json" })
            File.WriteAllText(Path.Combine(root, "Info", "UserSettings", name), settings);
        string backup = Install(root, true, true, false);
        string current = File.ReadAllText(Path.Combine(root, "Info", "UserSettings", "Current.json"));
        if (current != ChangeLanguage(settings)) throw new Exception("Settings preservation test failed");
        if (File.ReadAllText(Path.Combine(backup, "Info", "UserSettings", "Current.json")) != settings) throw new Exception("Backup test failed");
        if (!File.Exists(Path.Combine(root, "BepInEx", "plugins", "CastleStorySteamBridge.dll"))) throw new Exception("Missing patch");
        if (!File.Exists(Path.Combine(root, "BepInEx", "plugins", "CastleStorySteamBridge.dll.sig"))) throw new Exception("Missing update signature");
        string[] rescued = Directory.GetFiles(Path.Combine(root, "BepInEx", "SteamBridgeDiagnostics", "previous"), "*-output_log.txt");
        if (rescued.Length != 1) throw new Exception("Previous log was not saved");
        string summary = CastleStorySteam.LogSummary.Build(rescued[0], "selftest", 30000);
        if (!summary.Contains("Main game loop pause 1") || !summary.Contains("Game data gap 1") || !summary.Contains("NullReferenceException 1") || summary.Contains("76561190000000001") || summary.Contains("Someone") || summary.Length > 30000) throw new Exception("Log summary test failed");
        byte[] core = File.ReadAllBytes(Path.Combine(root, "BepInEx", "core", "BepInEx.dll"));
        Install(root, true, true, false);
        if (Convert.ToBase64String(core) != Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root, "BepInEx", "core", "BepInEx.dll")))) throw new Exception("Existing runtime was replaced");
        File.WriteAllText(Path.Combine(root, "Info", "UserSettings", "Current.json"), "{invalid");
        bool refused = false;
        try { Install(root, true, true, false); } catch (IOException) { refused = true; }
        if (!refused || File.ReadAllText(Path.Combine(root, "Info", "UserSettings", "Current.json")) != "{invalid") throw new Exception("Preflight safety test failed");
        string minimal = root + "-steam-only";
        Directory.CreateDirectory(Path.Combine(minimal, "Castle Story_Data", "Managed"));
        Directory.CreateDirectory(Path.Combine(minimal, "Info", "UserSettings"));
        File.WriteAllText(Path.Combine(minimal, "Castle Story.exe"), "fixture");
        Install(minimal, false, false, false);
        if (!File.Exists(Path.Combine(minimal, "BepInEx", "plugins", "CastleStorySteamBridge.dll"))
            || !File.Exists(Path.Combine(minimal, "BepInEx", "core", "BepInEx.dll"))
            || File.Exists(Path.Combine(minimal, "BepInEx", "patchers", "CastleStoryNetworkTimeout.dll")))
            throw new Exception("Steam-only loader installation failed");
        File.WriteAllText(Path.Combine(root, "TEST-PASSED.txt"), "PASS: update signature/version binding, update package round trip and chunk safety, diagnostics/update side channel isolated from game data; reusable receive buffer over real UDP (1400, 1, 65507, 7 bytes), no stale trailing bytes, handshake/goodbye, invalid lengths; reordered/missing language fields, network/localization preservation, complete install, backups, repeat install, existing runtime preservation, invalid JSON refusal. Live Steam performance unverified.");
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SelfTest-result.txt"), File.ReadAllText(Path.Combine(root, "TEST-PASSED.txt")) + "\r\nFixture: " + root);
    }
}

static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
        { try { Setup.SelfTest(); return 0; } catch (Exception e) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SelfTest-result.txt"), e.ToString()); return 1; } }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        bool preview=args.Length > 0 && args[0] == "--ui-test"; Application.Run(new InstallerForm(preview ? "" : (args.Length > 0 ? args[0] : ""),preview)); return 0;
    }
}



