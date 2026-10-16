using System;
using System.IO;
using System.Reflection;
using CastleStorySteam;
internal static class AutoModUpdate {
 // 0: nothing installed/newer already present, 1: applied, -1: wait for game closure.
 internal static int Apply(string game,bool gameOpen){if(!Setup.IsGame(game))return 0;string dll=Path.Combine(game,"BepInEx","plugins","CastleStorySteamBridge.dll");if(!File.Exists(dll))return 0;Version installed=AssemblyName.GetAssemblyName(dll).Version,current=new Version(BridgeVersion.Text+".0");if(installed>=current)return 0;if(gameOpen||!GameGuard.Ready())return -1;Setup.Install(game,false,false,false);return 1;}
}
