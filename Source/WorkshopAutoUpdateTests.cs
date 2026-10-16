using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using CastleStorySteam;

class WorkshopAutoUpdateTests {
 static string Hash(string path){using(SHA256 sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));}
 static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
 static int Main(string[] args){
  if(args.Length==1&&args[0]=="--hold"){Thread.Sleep(30000);return 0;}
  try{
   if(WorkshopAutoUpdate.AnotherGame())throw new Exception("Real game is running; do not test now");
   string root=Path.Combine(Path.GetTempPath(),"SteamBridgeWorkshopTest_"+Guid.NewGuid().ToString("N")),game=Path.Combine(root,@"steamapps\common\Castle Story"),source=Path.Combine(root,@"steamapps\workshop\content\227860\3818506669");
   Directory.CreateDirectory(Path.Combine(game,@"BepInEx\plugins"));Directory.CreateDirectory(source);
   Check(WorkshopAutoUpdate.SourceFolder(game)==source,"Steam library path resolution failed");
   string target=Path.Combine(game,@"BepInEx\plugins\CastleStorySteamBridge.dll"),sigPath=target+".sig",packet=Path.Combine(source,"bridge.csupdate");
   File.Copy(args[0],target);File.Copy(args[0]+".sig",sigPath);string oldHash=Hash(target),sigHash=Hash(sigPath);
   string keep=Path.Combine(game,"keep-save-and-settings.txt");File.WriteAllText(keep,"preserve");
   Check(WorkshopAutoUpdate.Apply(game,source).StartsWith("No downloaded"),"Missing release must retain mod");
   byte[] valid=File.ReadAllBytes(args[1]);File.WriteAllBytes(packet,new byte[3]);bool rejected=false;try{WorkshopAutoUpdate.Apply(game,source);}catch(IOException){rejected=true;}Check(rejected&&Hash(target)==oldHash,"Incomplete packet changed mod");
   byte[] tampered=(byte[])valid.Clone();tampered[tampered.Length/2]^=1;File.WriteAllBytes(packet,tampered);rejected=false;try{WorkshopAutoUpdate.Apply(game,source);}catch(IOException){rejected=true;}Check(rejected&&Hash(target)==oldHash,"Tampered packet changed mod");
   File.WriteAllBytes(packet,valid);
   using(FileStream lockedSignature=new FileStream(sigPath,FileMode.Open,FileAccess.Read,FileShare.Read)){
    bool rolledBack=false;try{WorkshopAutoUpdate.Apply(game,source);}catch(IOException){rolledBack=true;}
    Check(rolledBack&&Hash(target)==oldHash&&Hash(sigPath)==sigHash,"Signature replacement failure did not restore old DLL and signature");
   }
   string sentinel=Path.Combine(root,"Castle Story.exe");File.Copy(Assembly.GetExecutingAssembly().Location,sentinel);
   using(Process process=Process.Start(new ProcessStartInfo(sentinel,"--hold"){UseShellExecute=false,CreateNoWindow=true})){
    try{Thread.Sleep(200);Check(WorkshopAutoUpdate.Apply(game,source).StartsWith("Deferred")&&Hash(target)==oldHash,"Concurrent game was not deferred");}finally{process.Kill();process.WaitForExit();}
   }
   string result=WorkshopAutoUpdate.Apply(game,source);Check(result.StartsWith("Applied verified"),"Valid update was not installed");
   string version;byte[] dll,sig;Check(UpdatePackage.Parse(valid,out version,out dll,out sig),"Fixture packet invalid");
   Check(WorkshopAutoUpdate.DllVersion(File.ReadAllBytes(target)).ToString(3)==version&&UpdateVerifier.Verify(version,File.ReadAllBytes(target),File.ReadAllBytes(sigPath)),"Installed version/signature failed");
   Check(File.ReadAllText(keep)=="preserve","Save/settings fixture changed");Check(WorkshopAutoUpdate.Apply(game,source).Contains("up to date"),"Same version reinstalled");
   bool backed=false;foreach(string file in Directory.GetFiles(Path.Combine(game,@"BepInEx\SteamBridgeWorkshop\backups"),"CastleStorySteamBridge.dll",SearchOption.AllDirectories))if(Hash(file)==oldHash&&Hash(file+".sig")==sigHash)backed=true;Check(backed,"Previous DLL/signature backup missing");
   Assembly.Load(File.ReadAllBytes(target));Check(WorkshopAutoUpdate.Apply(game,source).StartsWith("Deferred"),"Already-loaded plugin was not deferred");
   Console.WriteLine("PASS: signed Workshop update, Steam library path, missing/incomplete/tampered rejection, DLL+signature rollback on real file lock, concurrent-game and loaded-plugin deferral, unchanged save/settings, backup, same-version no-op. Fixture: "+root);return 0;
  }catch(Exception error){Console.WriteLine(error);return 1;}
 }
}
