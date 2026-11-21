using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using CastleStorySteam;

internal static class WorkshopAutoUpdate {
 internal static string Join(params string[] parts){string path=parts[0];for(int i=1;i<parts.Length;i++)path=Path.Combine(path,parts[i]);return path;}
 internal static string SourceFolder(string game){return Path.GetFullPath(Join(game,"..","..","workshop","content","227860","3818506669"));}
  // 0.4.23: InstallHalloweenMode removed (user: seasonal content cut). It copied Info/Lua/Gamemodes/capturethepumpkin, Info/Lua/Mods/halloween2017 and Info/Languages/{English,French,Ukrainian}/language.csv from the Workshop folder on every start.
 internal static bool AlreadyLoaded(){foreach(Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()){string name=assembly.GetName().Name;if(name=="CastleStorySteamBridge"||name=="CastleStorySteamBridge-rebuilt")return true;}return false;}
 internal static bool AnotherGame(){int current;using(Process self=Process.GetCurrentProcess())current=self.Id;bool other=false;foreach(Process process in Process.GetProcessesByName("Castle Story")){try{if(process.Id!=current)other=true;}finally{process.Dispose();}}return other;}
 internal static Version DllVersion(byte[] bytes){using(MemoryStream stream=new MemoryStream(bytes))using(AssemblyDefinition assembly=AssemblyDefinition.ReadAssembly(stream)){if(assembly.Name.Name!="CastleStorySteamBridge"&&assembly.Name.Name!="CastleStorySteamBridge-rebuilt")throw new IOException("Not a Steam Bridge DLL");return assembly.Name.Version;}}
 internal static string Apply(string game,string sourceFolder){
  string target=Join(game,"BepInEx","plugins","CastleStorySteamBridge.dll"),packet=Path.Combine(sourceFolder,"bridge.csupdate");
  if(!File.Exists(target)||!File.Exists(packet))return "No downloaded release; current mod retained";
  if(AlreadyLoaded()||AnotherGame())return "Deferred: plugin already loaded or another game instance is running";
  string work=Join(game,"BepInEx","SteamBridgeWorkshop");Directory.CreateDirectory(work);
  using(FileStream mutex=new FileStream(Path.Combine(work,"update.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
   byte[] blob;using(FileStream input=new FileStream(packet,FileMode.Open,FileAccess.Read,FileShare.Read)){if(input.Length<10||input.Length>UpdatePackage.MaxBytes)throw new IOException("Invalid Workshop package size");blob=new byte[(int)input.Length];int offset=0;while(offset<blob.Length){int n=input.Read(blob,offset,blob.Length-offset);if(n==0)throw new IOException("Incomplete Workshop package");offset+=n;}}
   string version;byte[] dll,sig;if(!UpdatePackage.Parse(blob,out version,out dll,out sig)||!UpdateVerifier.Verify(version,dll,sig))throw new IOException("Workshop signature rejected; current mod retained");
   Version candidate=DllVersion(dll),installed=DllVersion(File.ReadAllBytes(target));if(candidate.ToString(3)!=version)throw new IOException("Signed version does not match DLL metadata");if(candidate<=installed)return "Current mod "+installed.ToString(3)+" is up to date";
   // Check again immediately before the transaction. Never replace a loaded plugin.
   if(AlreadyLoaded()||AnotherGame())return "Deferred: game started during verification";
   string backup=Join(work,"backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
   string old=Path.Combine(backup,"CastleStorySteamBridge.dll"),oldSig=old+".sig",sigPath=target+".sig",stage=target+"."+Guid.NewGuid().ToString("N")+".new",stageSig=stage+".sig";
   File.Copy(target,old);bool hadSig=File.Exists(sigPath);if(hadSig)File.Copy(sigPath,oldSig);bool replaced=false,sigReplaced=false;
   try{
    File.WriteAllBytes(stage,dll);File.WriteAllBytes(stageSig,sig);
    File.Replace(stage,target,null);replaced=true;
    if(hadSig)File.Replace(stageSig,sigPath,null);else File.Move(stageSig,sigPath);sigReplaced=true;
    // Receipt errors must not undo an otherwise verified, complete installation.
    string result="Applied verified Workshop update "+installed.ToString(3)+" -> "+version+" before plugin loading";
    try{File.WriteAllText(Path.Combine(work,"last-update.txt"),result+Environment.NewLine+"Backup: "+backup+Environment.NewLine+DateTime.UtcNow.ToString("O"));}catch{}
    return result;
   }catch{
    if(replaced){File.Copy(old,stage,true);File.Replace(stage,target,null);}
    if(sigReplaced){if(hadSig){File.Copy(oldSig,stageSig,true);File.Replace(stageSig,sigPath,null);}else File.Delete(sigPath);}
    throw;
   }finally{TryDelete(stage);TryDelete(stageSig);}
  }
 }
 static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch{}}
}
