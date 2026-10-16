using System;
using System.IO;
namespace CastleStorySteam {
 internal static class WorkshopMapImport {
  internal static string Import(string source,string playerMaps,ulong id) {
   source=Path.GetFullPath(source);playerMaps=Path.GetFullPath(playerMaps);
   if(id==0||!File.Exists(Path.Combine(source,"meta.json")))throw new InvalidDataException("Map metadata missing");
   Directory.CreateDirectory(playerMaps);
   string target=Path.Combine(playerMaps,"SteamBridge_"+id);
   if(Directory.Exists(target)) {
    if(File.Exists(Path.Combine(target,".steambridge-workshop-origin"))&&File.ReadAllText(Path.Combine(target,".steambridge-workshop-origin")).Trim()==id.ToString()&&File.Exists(Path.Combine(target,"meta.json")))return target;
    throw new IOException("Player map folder already exists; kept unchanged");
   }
   string stage=Path.Combine(Path.GetDirectoryName(playerMaps),".SteamBridgeImport-"+id+"-"+Guid.NewGuid().ToString("N"));
   CopyFolder(source,stage);
   File.WriteAllText(Path.Combine(stage,".steambridge-workshop-origin"),id.ToString());
   Directory.Move(stage,target);return target;
  }
  static void CopyFolder(string source,string target) {
   if((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0)throw new IOException("Map folder is a link");
   Directory.CreateDirectory(target);
   foreach(string file in Directory.GetFiles(source)) {
    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Map file is a link");
    File.Copy(file,Path.Combine(target,Path.GetFileName(file)),false);
   }
   foreach(string child in Directory.GetDirectories(source))CopyFolder(child,Path.Combine(target,Path.GetFileName(child)));
  }
 }
}
