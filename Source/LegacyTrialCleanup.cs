using System;
using System.IO;
internal static class LegacyTrialCleanup {
 internal static void Archive(string game) {
  string plugins=Path.Combine(Path.Combine(game,"BepInEx"),"plugins");
  string backup=Path.Combine(Path.Combine(Path.Combine(game,"BepInEx"),"SteamBridgeWorkshop"),"retired-trials-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
  foreach(string name in new[]{"CastleStoryGameplayFixes.dll","CastleStoryDoorProbe.dll","CastleStoryFreezeProbe.dll"}) {
   string source=Path.Combine(plugins,name);
   if(!File.Exists(source))continue;
   Directory.CreateDirectory(backup);File.Move(source,Path.Combine(backup,name));
  }
 }
}