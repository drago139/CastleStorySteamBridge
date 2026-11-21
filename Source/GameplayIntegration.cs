namespace CastleStorySteam {
 public sealed partial class SteamBridgePlugin {
  CastleStoryGameplayFixes gameplayFixes;
  bool legacyTrialsRetired;
  void RetireLegacyTrials() {
   if (legacyTrialsRetired) return;
   legacyTrialsRetired = true;
   // Older startup updaters replace only the bridge DLL. Retire previously installed
   // trial components after Chainloader has created every plugin, before a match.
   foreach (string id in new[] { "gnom.castlestory.freezeprobe", "gnom.castlestory.doorprobe", "gnom.castlestory.gameplayfixtrial" }) {
    BepInEx.PluginInfo info;
    if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(id, out info) && info.Instance != null) {
     info.Instance.enabled = false;
     new HarmonyLib.Harmony(id).UnpatchSelf();
     UnityEngine.Object.Destroy(info.Instance);
    }
   }
   try {
    if (System.Diagnostics.Process.GetProcessesByName("Castle Story").Length <= 1)
     LegacyTrialCleanup.Archive(BepInEx.Paths.GameRootPath);
   } catch (System.Exception e) { Logger.LogWarning("Old trial files retained in place: " + e.Message); }
  }
  void InitGameplayFixes() {
   if (gameplayFixes == null) gameplayFixes = new CastleStoryGameplayFixes(Config, Logger);
   // 0.4.23 (user: seasonal content cut): SeasonalEvents / Winter* / VisualOptions deleted; only the halloween2017 Lua-mod blocker stays (LegacyModBlock.cs)
   try { LegacyModBlock.Install(); Logger.LogInfo("Old Halloween 2017 Lua mod is skipped (pumpkin moon off)."); }
   catch(System.Exception e) { LegacyModBlock.Shutdown(); Logger.LogWarning("Legacy mod block unavailable: "+e.Message); }
   try { LoggerAssemblyFix.Install(); Logger.LogInfo("Optional logger initialization uses existing game assemblies."); }
   catch(System.Exception e) { LoggerAssemblyFix.Shutdown(); Logger.LogWarning("Logger assembly guard unavailable: "+e.Message); }
   try { SavePreviewFix.Install(); Logger.LogInfo("Save preview protection ready: compact save pictures; optional image errors do not stop save data."); }
   catch(System.Exception e) { SavePreviewFix.Shutdown(); Logger.LogWarning("Save preview protection unavailable: "+e.Message); }
  }
  void ShutdownGameplayFixes() { LoggerAssemblyFix.Shutdown(); SavePreviewFix.Shutdown(); LegacyModBlock.Shutdown(); if (gameplayFixes != null) { gameplayFixes.Dispose(); gameplayFixes = null; } }
 }
}
