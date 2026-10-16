using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Utils;
using HarmonyLib;
namespace CastleStorySteam {
 internal static class LoggerAssemblyFix {
  static Harmony harmony;
  static readonly FieldInfo names=AccessTools.Field(typeof(CoreConfigurator),"_usualAssemblyNames");
  internal static void Install() {
   if(names==null)throw new MissingFieldException("CoreConfigurator._usualAssemblyNames");
   harmony=new Harmony("local.castlestory.steambridge.loggerassemblies");
   harmony.Patch(AccessTools.Method(typeof(CoreConfigurator),"ForceAllBrixLoggersToInitiate"),new HarmonyMethod(typeof(LoggerAssemblyFix),"Before"));
  }
  static void Before() {
   List<string> original=names.GetValue(null) as List<string>;
   if(original==null)return;
   List<string> existing=new List<string>();Assembly[] loaded=AppDomain.CurrentDomain.GetAssemblies();
   foreach(string reference in original) {
    string expected=new AssemblyName(reference).Name;
    foreach(Assembly assembly in loaded) {
     string actual=assembly.GetName().Name;
     if(actual==expected||(expected=="Brix.Common"&&actual=="brix-common")) {
      existing.Add(assembly.FullName);break;
     }
    }
   }
   // The optional logger scan must not try to load a non-existent Brix.Common.
   // Retain initialization of the real, already-loaded game assemblies.
   names.SetValue(null,existing);
  }
  internal static void Shutdown() {if(harmony!=null)harmony.UnpatchSelf();harmony=null;}
 }
}
