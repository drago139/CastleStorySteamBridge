using System;
using System.Diagnostics;
using System.IO;

// Desktop helpers must not initialize the game's Steam API or replace its files during a match.
internal static class GameGuard {
 static DateTime lastBusy=DateTime.MinValue;
 internal static bool Running(){
  bool busy=false;
  Process[] processes;
  try { processes=Process.GetProcesses(); } catch { lastBusy=DateTime.UtcNow; return true; }
  foreach(Process process in processes){
   try {
    string name=process.ProcessName.Replace(" ","").Replace("_","");
    if(string.Equals(name,"CastleStory",StringComparison.OrdinalIgnoreCase)) busy=true;
   } catch(InvalidOperationException) { } catch { busy=true; }
   finally { process.Dispose(); }
  }
  if(busy)lastBusy=DateTime.UtcNow;
  return busy;
 }
 internal static bool Ready(){return !Running()&&(DateTime.UtcNow-lastBusy).TotalSeconds>=15;}
 internal static void RequireClosed(){if(Running())throw new IOException("Castle Story працює. Закрий гру перед встановленням або публікацією оновлення.");}
}
