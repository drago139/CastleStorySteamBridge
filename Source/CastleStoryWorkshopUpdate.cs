using System;
using System.Collections.Generic;
using Mono.Cecil;

public static class CastleStoryWorkshopUpdate {
 public static IEnumerable<string> TargetDLLs {get{return new string[0];}}
 public static void Patch(AssemblyDefinition assembly) { }
 public static void Initialize(){
  try{
   string game=BepInEx.Paths.GameRootPath;
   // 0.4.23: InstallHalloweenMode removed (user: seasonal content cut); the patcher no longer copies capturethepumpkin / halloween2017 / language.csv from the Workshop folder
   Console.WriteLine("[SteamBridgeWorkshopUpdate] "+WorkshopAutoUpdate.Apply(game,WorkshopAutoUpdate.SourceFolder(game)));
   if (!WorkshopAutoUpdate.AnotherGame() && !WorkshopAutoUpdate.AlreadyLoaded()) {
    string installed=System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(game,"BepInEx"),"plugins"),"CastleStorySteamBridge.dll");
    if(System.IO.File.Exists(installed) && WorkshopAutoUpdate.DllVersion(System.IO.File.ReadAllBytes(installed)) >= new Version(0,3,20,0)) LegacyTrialCleanup.Archive(game);
   }
  }catch(Exception error){Console.WriteLine("[SteamBridgeWorkshopUpdate] Update skipped; current mod retained: "+error.Message);}
 }
}
