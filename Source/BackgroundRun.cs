using System;
using BepInEx.Configuration;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.58: keep the game running when its window loses focus (alt-tab). With Unity's default the game pauses or crawls in the background,
    // so a host or guest who alt-tabs during loading or play stalls the shared session: orders and object state arrive late and units end up out of step.
    // On by default for everyone; switch off with [Game] RunInBackground = false. Re-applied every few seconds in case the game resets it.
    public sealed partial class SteamBridgePlugin
    {
        ConfigEntry<bool> runInBackgroundEntry; float nextBackgroundCheck;

        void InstallRunInBackground()
        {
            try
            {
                runInBackgroundEntry = Config.Bind("Game", "RunInBackground", true, "Keep the game running when its window is not focused (alt-tab). Prevents stalls and desync in multiplayer while a player is in another window.");
                ApplyRunInBackground();
            }
            catch (Exception e) { Logger.LogWarning("RunInBackground setup failed: " + e.Message); }
        }

        void ApplyRunInBackground()
        {
            if (runInBackgroundEntry == null) return;
            if (runInBackgroundEntry.Value && !Application.runInBackground)
            {
                Application.runInBackground = true;
                Logger.LogInfo("Application.runInBackground set to true (alt-tab no longer pauses the game).");
            }
        }

        void RunInBackgroundTick()
        {
            if (runInBackgroundEntry == null) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextBackgroundCheck) return;
            nextBackgroundCheck = now + 5f;
            ApplyRunInBackground();
        }
    }
}