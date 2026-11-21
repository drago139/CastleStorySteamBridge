using System;
using System.Net;
using System.Net.Sockets;
using BepInEx.Configuration;
using Brix.Game.Network;
using Brix.Network;
using Brix.UI.Builder.Menu;
using HarmonyLib;
using Network;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.83 DIAGNOSTICS-ONLY mode ([Mode] DiagnosticsOnly=true): the vanilla game plus measurements, for an honest "game vs mod" comparison.
    // Nothing that changes the game or its network is installed: no Steam bridge, no side channel, no smoothing, no timeout policy, no network
    // compatibility settings, no gameplay fixes, no seasonal content, no UI changes. Installed: unit position trace (test mode), hitch profiler,
    // perf trace (GC/hitch/catapult/fog log-only hooks), periodic stats, freeze watch, queue/command diagnostics (log-only), run-in-background
    // (needed to run two instances on one PC) and the virtual player, which joins the host directly by IP ("joinlan", [Test] DirectJoinAddress).
    public sealed partial class SteamBridgePlugin
    {
        static bool diagOnly; static ConfigEntry<string> directJoinCfg; bool doPortLogged;

        bool DiagOnlyRequested()
        {
            try { diagOnly = Config.Bind("Mode", "DiagnosticsOnly", false, "true = vanilla game + diagnostics only (for comparison tests). Disables the Steam bridge and every gameplay/network change of this mod. Players join directly by IP.").Value; }
            catch (Exception) { diagOnly = false; }
            return diagOnly;
        }

        void AwakeDiagnosticsOnly()
        {
            discoveryInstance = this;
            discoveryHarmony = new Harmony("local.castlestory.steambridge.diagonly");
            try { ModInventory.Install(Config, Logger); } catch (Exception e) { Logger.LogWarning("Mod inventory skipped: " + e.Message); }   // 0.4.14 MOD-INV (log only)
            directJoinCfg = Config.Bind("Test", "DirectJoinAddress", "127.0.0.1:7777", "Diagnostics-only mode: address the virtual player 'joinlan' connects to (host IP:port).");
            try { InstallPositionLink(); } catch (Exception e) { Logger.LogWarning("DIAG-ONLY trace: " + e.Message); }
            InstallHitchProfiler(); InstallPeriodicStats(); InstallFreezeWatch(); InstallRunInBackground(); InstallPerfTrace(); InstallQueueDiag(); InstallLinkProbe(); InstallStateSnapshot();
            Logger.LogWarning("DIAGNOSTICS-ONLY MODE: vanilla game + measurements. Steam bridge, side channel, smoothing, timeouts, fixes and content of this mod are OFF.");
            PtLog("DIAG-ONLY mode active");
        }

        void UpdateDiagnosticsOnly()
        {
            TestControlTick(); PositionLinkTick(); PeriodicStatsTick(); FreezeWatchTick(); RunInBackgroundTick(); SysDiagTick(); VirtualPlayerTick(); BotTick(); QueueDiagTick(); LinkProbeTick(); PerfFrameTick();
            if (!doPortLogged && NetworkServer.active && UNetManager.instance != null) { doPortLogged = true; PtLog("DIAG-ONLY host listening on port " + UNetManager.instance.networkPort); Logger.LogInfo("DIAG-ONLY host port " + UNetManager.instance.networkPort); }
        }

        void OnGuiDiagnosticsOnly()
        {
            GUI.Label(new Rect(8f, Screen.height - 22f, 600f, 20f), "Steam Bridge " + BridgeVersion.Text + ": DIAGNOSTICS-ONLY (vanilla network)");
        }

        // virtual player 'joinlan': the same direct connection the game makes for a LAN match
        bool VpJoinLan()
        {
            string text = directJoinCfg != null ? directJoinCfg.Value.Trim() : "127.0.0.1:7777"; int port = 7777; string host = text; int c = text.LastIndexOf(':');
            if (c > 0) { int p; if (int.TryParse(text.Substring(c + 1), out p) && p > 0 && p < 65536) { port = p; host = text.Substring(0, c); } }
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) { VpLog("#" + vpDone + " joinlan RESULT=failed reason=bad address " + text); return true; }
            if (NetworkClient.active || UNetManager.instance == null) { VpLog("#" + vpDone + " joinlan RESULT=failed reason=not in the main menu"); return true; }
            NetworkServersMenu.SelectedMatchData = new MatchList.MatchData { IsLanGame = true, Address = new MatchAddress(host, (ushort)port), MatchName = "Direct " + host };
            NetworkWaitingModalMenu.OpenAsClientSignal.Invoke();
            VpLog("#" + vpDone + " joinlan started to " + host + ":" + port); return true;
        }
    }
}
