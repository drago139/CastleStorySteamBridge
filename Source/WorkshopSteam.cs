using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
internal sealed class WorkshopSteam : IDisposable {
 const string Dll="CSteamworks";
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetDllDirectory(string path);
 [DllImport(Dll,EntryPoint="InitSafe",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool InitSafe();
 [DllImport(Dll,EntryPoint="Shutdown",CallingConvention=CallingConvention.Cdecl)] static extern void Shutdown();
 [DllImport(Dll,EntryPoint="RunCallbacks",CallingConvention=CallingConvention.Cdecl)] internal static extern void RunCallbacks();
 [DllImport(Dll,EntryPoint="ISteamUtils_GetAppID",CallingConvention=CallingConvention.Cdecl)] internal static extern uint AppID();
 [DllImport(Dll,EntryPoint="ISteamUser_GetSteamID",CallingConvention=CallingConvention.Cdecl)] internal static extern ulong UserID();
 [DllImport(Dll,EntryPoint="ISteamUGC_SubscribeItem",CallingConvention=CallingConvention.Cdecl)] internal static extern ulong Subscribe(ulong id);
 [DllImport(Dll,EntryPoint="ISteamUGC_CreateQueryUserUGCRequest",CallingConvention=CallingConvention.Cdecl)] static extern ulong UserQuery(uint owner,int list,int type,int sort,uint creator,uint consumer,uint page);
 [DllImport(Dll,EntryPoint="ISteamUGC_SetReturnMetadata",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool ReturnMetadata(ulong query,[MarshalAs(UnmanagedType.I1)] bool metadata);
 [DllImport(Dll,EntryPoint="ISteamUGC_SendQueryUGCRequest",CallingConvention=CallingConvention.Cdecl)] internal static extern ulong SendQuery(ulong query);
 [DllImport(Dll,EntryPoint="ISteamUGC_ReleaseQueryUGCRequest",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool ReleaseQuery(ulong query);
 [DllImport(Dll,EntryPoint="ISteamUGC_GetQueryUGCResult",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool QueryResult(ulong query,uint index,IntPtr details);
 [DllImport(Dll,EntryPoint="ISteamUGC_GetQueryUGCMetadata",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool QueryMetadata(ulong query,uint index,IntPtr metadata,uint size);
 [DllImport(Dll,EntryPoint="ISteamUGC_DownloadItem",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool Download(ulong id,[MarshalAs(UnmanagedType.I1)] bool priority);
 [DllImport(Dll,EntryPoint="ISteamUGC_GetItemState",CallingConvention=CallingConvention.Cdecl)] internal static extern uint State(ulong id);
 [DllImport(Dll,EntryPoint="ISteamUGC_GetItemInstallInfo",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool InstallInfo(ulong id,out ulong size,IntPtr folder,uint capacity,out uint stamp);
 [DllImport(Dll,EntryPoint="ISteamUGC_GetItemDownloadInfo",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool Progress(ulong id,out ulong received,out ulong total);
 [DllImport(Dll,EntryPoint="ISteamUtils_IsAPICallCompleted",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool Completed(ulong call,[MarshalAs(UnmanagedType.I1)] out bool failed);
 [DllImport(Dll,EntryPoint="ISteamUtils_GetAPICallResult",CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool Result(ulong call,IntPtr data,int size,int callback,[MarshalAs(UnmanagedType.I1)] out bool failed);
 [DllImport(Dll,EntryPoint="RegisterCallback",CallingConvention=CallingConvention.Cdecl)] static extern void Register(IntPtr callback,int id);
 [DllImport(Dll,EntryPoint="UnregisterCallback",CallingConvention=CallingConvention.Cdecl)] static extern void Unregister(IntPtr callback);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RunCB(IntPtr self,IntPtr data);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RunCR(IntPtr self,IntPtr data,[MarshalAs(UnmanagedType.I1)] bool failed,ulong call);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SizeCB(IntPtr self);
 RunCB run;RunCR runResult;SizeCB sizeCallback;IntPtr callbackBase,vtable;bool initialized;
 internal event Action<ulong,int> DownloadFinished;
 internal WorkshopSteam(string game){ GameGuard.RequireClosed();
  string plugins=Path.Combine(Path.GetFullPath(game),"Castle Story_Data","Plugins");
  if(!File.Exists(Path.Combine(plugins,"CSteamworks.dll")))throw new IOException("Оберіть папку Castle Story: бібліотеку Steam не знайдено.");
  Environment.SetEnvironmentVariable("SteamAppId","227860");Environment.SetEnvironmentVariable("SteamGameId","227860");
  if(!SetDllDirectory(plugins)||!InitSafe())throw new IOException("Відкрий Steam з акаунтом, який має Castle Story, та спробуй знову.");
  initialized=true;if(AppID()!=227860){Dispose();throw new IOException("Steam підключив іншу гру.");}
  run=delegate(IntPtr self,IntPtr data){try{if((uint)Marshal.ReadInt32(data)==227860&&DownloadFinished!=null)DownloadFinished((ulong)Marshal.ReadInt64(data,8),Marshal.ReadInt32(data,16));}catch{}};
  runResult=delegate(IntPtr self,IntPtr data,bool failed,ulong call){if(!failed)run(self,data);};sizeCallback=delegate{return 24;};
  vtable=Marshal.AllocHGlobal(3*IntPtr.Size);Marshal.WriteIntPtr(vtable,0,Marshal.GetFunctionPointerForDelegate(runResult));Marshal.WriteIntPtr(vtable,IntPtr.Size,Marshal.GetFunctionPointerForDelegate(run));Marshal.WriteIntPtr(vtable,2*IntPtr.Size,Marshal.GetFunctionPointerForDelegate(sizeCallback));
  callbackBase=Marshal.AllocHGlobal(16);for(int i=0;i<16;i++)Marshal.WriteByte(callbackBase,i,0);Marshal.WriteIntPtr(callbackBase,vtable);Marshal.WriteInt32(callbackBase,12,3406);Register(callbackBase,3406);
 }
 internal static byte[] Poll(ulong call,int callback,int size,int resultOffset=0){if(call==0)throw new IOException("Steam не прийняв запит.");bool failed;if(!Completed(call,out failed))return null;if(failed)throw new IOException("З'єднання Steam перервано.");IntPtr data=Marshal.AllocHGlobal(size);try{if(!Result(call,data,size,callback,out failed)||failed)throw new IOException("Steam не повернув результат.");byte[] bytes=new byte[size];Marshal.Copy(data,bytes,0,size);if(BitConverter.ToInt32(bytes,resultOffset)!=1)throw new IOException("Steam відхилив запит: код "+BitConverter.ToInt32(bytes,resultOffset));return bytes;}finally{Marshal.FreeHGlobal(data);}}
 internal static ulong ChannelQuery(uint owner){ulong query=UserQuery(owner,0,0,3,227860,227860,1);if(query==0||query==ulong.MaxValue||!ReturnMetadata(query,true))throw new IOException("Steam не прийняв пошук каналу оновлень.");return query;}
 internal static ulong FindChannel(ulong query,uint count){if(count>50)throw new IOException("Некоректна відповідь Workshop.");IntPtr details=Marshal.AllocHGlobal(16384),metadata=Marshal.AllocHGlobal(4096);try{for(uint i=0;i<count;i++){if(!QueryResult(query,i,details)||Marshal.ReadInt32(details,8)!=1||Marshal.ReadInt32(details,16)!=227860||Marshal.ReadInt32(details,20)!=227860||!QueryMetadata(query,i,metadata,4096))continue;string json=Utf8(metadata);if(System.Text.RegularExpressions.Regex.IsMatch(json,"\"SteamBridgeReleaseChannel\"\\s*:\\s*1\\s*[,}]"))return (ulong)Marshal.ReadInt64(details);}return 0;}finally{Marshal.FreeHGlobal(details);Marshal.FreeHGlobal(metadata);}}
 internal static string Folder(ulong id){uint state=State(id);if((state&4)==0||(state&(8|16|32))!=0)return null;IntPtr buffer=Marshal.AllocHGlobal(4096);try{ulong bytes;uint stamp;if(!InstallInfo(id,out bytes,buffer,4096,out stamp))return null;return Utf8(buffer);}finally{Marshal.FreeHGlobal(buffer);}}
 static string Utf8(IntPtr value){int n=0;while(n<4096&&Marshal.ReadByte(value,n)!=0)n++;byte[] bytes=new byte[n];Marshal.Copy(value,bytes,0,n);return Encoding.UTF8.GetString(bytes);}
 public void Dispose(){if(callbackBase!=IntPtr.Zero){Unregister(callbackBase);Marshal.FreeHGlobal(callbackBase);callbackBase=IntPtr.Zero;}if(vtable!=IntPtr.Zero){Marshal.FreeHGlobal(vtable);vtable=IntPtr.Zero;}if(initialized){Shutdown();initialized=false;}GC.KeepAlive(run);GC.KeepAlive(runResult);GC.KeepAlive(sizeCallback);}
}
