using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CastleStorySteam;
class WorkshopPublisher {
 const string Native="CSteamworks";
 [DllImport(Native,EntryPoint="ISteamUGC_CreateItem",CallingConvention=CallingConvention.Cdecl)] static extern ulong Create(uint app,int type);
 [DllImport(Native,EntryPoint="ISteamUGC_StartItemUpdate",CallingConvention=CallingConvention.Cdecl)] static extern ulong Begin(uint app,ulong item);
 [DllImport(Native,EntryPoint="ISteamUGC_SetItemTitle",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool Title(ulong item,IntPtr value);
 [DllImport(Native,EntryPoint="ISteamUGC_SetItemDescription",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool Description(ulong item,IntPtr value);
 [DllImport(Native,EntryPoint="ISteamUGC_SetItemMetadata",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool Metadata(ulong item,IntPtr value);
 [DllImport(Native,EntryPoint="ISteamUGC_SetItemContent",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool Content(ulong item,IntPtr value);
 [DllImport(Native,EntryPoint="ISteamUGC_SetItemPreview",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool Preview(ulong item,IntPtr value);
 [DllImport(Native,EntryPoint="ISteamUGC_SetItemVisibility",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool Visibility(ulong item,int value);
 [DllImport(Native,EntryPoint="ISteamUGC_SubmitItemUpdate",CallingConvention=CallingConvention.Cdecl)] static extern ulong Submit(ulong item,IntPtr notes);
 delegate bool Setter(ulong item,IntPtr value);
 static void Set(Setter setter,ulong item,string value){byte[] bytes=System.Text.Encoding.UTF8.GetBytes(value+"\0");IntPtr text=Marshal.AllocHGlobal(bytes.Length);try{Marshal.Copy(bytes,0,text,bytes.Length);if(!setter(item,text))throw new IOException("Steam не прийняв дані елемента.");}finally{Marshal.FreeHGlobal(text);}}
 static byte[] Wait(ulong call,int callback,int size){DateTime until=DateTime.UtcNow.AddMinutes(5);while(DateTime.UtcNow<until){WorkshopSteam.RunCallbacks();byte[] result=WorkshopSteam.Poll(call,callback,size);if(result!=null)return result;Thread.Sleep(100);}throw new IOException("Steam timeout. Inspect upload logs before retrying.");}
 static int Main(string[] args){try{
  if(args.Length<2)throw new ArgumentException("WorkshopPublisher --prepare PACKAGE_ROOT | --publish GAME PACKAGE_ROOT [public|friends]");
  string root=Path.GetFullPath(args[args[0]=="--prepare"?1:2]),app=Path.Combine(root,"DesktopApp","CastleStory-Steam-App.exe"),content=Path.Combine(root,"WorkshopContent");
  if(args[0]=="--prepare"){Directory.CreateDirectory(content);byte[] blob=DesktopRelease.Build(BridgeVersion.Text,File.ReadAllBytes(app),File.ReadAllBytes(app+".sig"));File.WriteAllBytes(Path.Combine(content,"program.csupdate"),blob);byte[] bridge=File.ReadAllBytes(Path.Combine(root,"Components","bridge.dll")),bridgeSig=File.ReadAllBytes(Path.Combine(root,"Components","bridge.dll.sig"));if(!UpdateVerifier.Verify(BridgeVersion.Text,bridge,bridgeSig))throw new IOException("Unsigned game update");File.WriteAllBytes(Path.Combine(content,"bridge.csupdate"),UpdatePackage.Build(BridgeVersion.Text,bridge,bridgeSig));string modeRoot=Path.Combine(root,"SeasonalMode","PackageRoot");if(Directory.Exists(modeRoot))CopyDirectory(modeRoot,content);string version;byte[] exe,sig;DesktopRelease.Parse(blob,out version,out exe,out sig);Console.WriteLine("Prepared signed Workshop package: "+version+"; bytes="+blob.Length);return 0;}
  if(args[0]!="--publish"&&args[0]!="--description")throw new ArgumentException("Unknown mode");string pkg=Path.Combine(content,"program.csupdate");string v;byte[] appBytes,signature;DesktopRelease.Parse(File.ReadAllBytes(pkg),out v,out appBytes,out signature);if(v!=BridgeVersion.Text)throw new IOException("Rebuild package first");
  string description=File.ReadAllText(Path.Combine(root,"WORKSHOP-DESCRIPTION.txt"));if(!File.Exists(Path.Combine(root,"workshop-preview.png")))throw new IOException("Preview missing");
  GameGuard.RequireClosed(); using(WorkshopSteam steam=new WorkshopSteam(args[1])){
   if((uint)WorkshopSteam.UserID()!=WorkshopChannel.OwnerAccount)throw new IOException("Use the release author's Steam account");
   string saved=Path.Combine(root,"workshop-item-id.txt");ulong id;
   if(File.Exists(saved)){id=ulong.Parse(File.ReadAllText(saved).Trim());Console.WriteLine("Updating existing item "+id);}else{byte[] created=Wait(Create(227860,0),3403,24);id=BitConverter.ToUInt64(created,8);if(id==0)throw new IOException("Steam returned empty item ID");File.WriteAllText(saved,id.ToString());Console.WriteLine("Created item "+id);if(created[16]!=0){Console.WriteLine("Workshop agreement requires manual acceptance: https://steamcommunity.com/sharedfiles/filedetails/?id="+id);return 2;}}
   ulong handle=Begin(227860,id);if(handle==0||handle==ulong.MaxValue)throw new IOException("Steam rejected update handle");
   if(args[0]=="--description"){
    Set(Description,handle,description);byte[] edited=Wait(Submit(handle,IntPtr.Zero),3404,8);
    if(BitConverter.ToInt32(edited,0)!=1)throw new IOException("Steam description result: "+BitConverter.ToInt32(edited,0));
    if(edited[4]!=0)throw new IOException("Workshop agreement requires manual acceptance");
    Console.WriteLine("Workshop description updated: https://steamcommunity.com/sharedfiles/filedetails/?id="+id);return 0;
   }
   Set(Title,handle,"Castle Story Steam Bridge — multiplayer, ping & bug fixes");Set(Description,handle,description);Set(Metadata,handle,"{\"ItemType\":6,\"SteamBridgeReleaseChannel\":1,\"Version\":\""+v+"\"}");Set(Content,handle,content);Set(Preview,handle,Path.Combine(root,"workshop-preview.png"));int visibility=args.Length>3&&args[3]=="public"?0:1;if(!Visibility(handle,visibility))throw new IOException("Steam rejected visibility");
   byte[] submitted=Wait(Submit(handle,IntPtr.Zero),3404,8);if(submitted[4]!=0){Console.WriteLine("Workshop agreement requires manual acceptance: https://steamcommunity.com/sharedfiles/filedetails/?id="+id);return 2;}
   Console.WriteLine("Workshop upload succeeded: https://steamcommunity.com/sharedfiles/filedetails/?id="+id+"; visibility="+(visibility==0?"public":"friends"));return 0;
  }
 }catch(Exception error){Console.WriteLine(error.Message);return 1;}}
 static void CopyDirectory(string source,string destination){foreach(string directory in Directory.GetDirectories(source,"*",SearchOption.AllDirectories))Directory.CreateDirectory(Path.Combine(destination,directory.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)));foreach(string file in Directory.GetFiles(source,"*",SearchOption.AllDirectories)){string target=Path.Combine(destination,file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,true);}}
}
