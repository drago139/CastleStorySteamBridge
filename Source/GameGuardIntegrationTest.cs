using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
class GameGuardIntegrationTest {
 static int Main(string[] args){
  if(args.Length==1&&args[0]=="--sentinel"){Thread.Sleep(30000);return 0;}
  Process sentinel=null;
  try {
   if(GameGuard.Running())throw new Exception("Close the real game before this test");
   string root=Path.Combine(Path.GetTempPath(),"SteamBridgeGuardTest_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   string exe=Path.Combine(root,"Castle Story.exe");File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location,exe);
   sentinel=Process.Start(new ProcessStartInfo(exe,"--sentinel"){UseShellExecute=false,CreateNoWindow=true});
   Thread.Sleep(300);
   if(!GameGuard.Running())throw new Exception("Running game process was not detected");
   bool installBlocked=false,steamBlocked=false;
   try{Setup.Install(root,false,false,false);}catch(IOException e){installBlocked=e.Message.Contains("Castle Story працює");}
   try{using(WorkshopSteam steam=new WorkshopSteam(root)){}}catch(IOException e){steamBlocked=e.Message.Contains("Castle Story працює");}
   if(!installBlocked||!steamBlocked||Directory.Exists(Path.Combine(root,"BepInEx")))throw new Exception("Game-open protection failed");
   sentinel.Kill();sentinel.WaitForExit();sentinel.Dispose();sentinel=null;
   if(GameGuard.Ready())throw new Exception("Post-game grace period was skipped");
   Thread.Sleep(15100);
   if(!GameGuard.Ready())throw new Exception("Grace period did not expire");
   Console.WriteLine("PASS: actual running Castle Story process blocks installer and Steam initialization before any write/native call; 15-second post-game grace period.");return 0;
  }catch(Exception e){Console.WriteLine(e);return 1;}finally{if(sentinel!=null){try{sentinel.Kill();sentinel.WaitForExit();}catch{}sentinel.Dispose();}}
 }
}
