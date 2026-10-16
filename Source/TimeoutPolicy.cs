using System;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Network;
using HarmonyLib;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.43: the UNet DisconnectTimeout is chosen per connection instead of patched globally. A hosted lobby and a client that
    // joins through the bridge use a long timeout (a host hitch or a Steam relay hiccup of a few seconds no longer drops players);
    // a direct connection to someone without the mod keeps the game's own value, so the configs match and the join works.
    // The shared ConnectionConfig is read when the topology is built, i.e. when StartHost / ConnectToServer run.
    public sealed partial class SteamBridgePlugin
    {
        const uint LongTimeoutMs = 600000;
        static uint vanillaTimeout; static bool vanillaKnown;
        ConfigEntry<bool> longHostTimeout;

        void InstallTimeoutPolicy()
        {
            try
            {
                longHostTimeout = Config.Bind("Network", "LongTimeoutWhenHosting", true, "Hosted lobbies use a 10-minute UNet disconnect timeout (needed because bridged links can stall for seconds). Set false only if you host for players without the mod over LAN or port forwarding.");
                MethodInfo host = typeof(UNetManager).GetMethod("StartHost", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                MethodInfo connect = typeof(UNetManager).GetMethod("ConnectToServer", new Type[] { typeof(string), typeof(int) });
                if (host == null || connect == null) { Logger.LogWarning("Timeout policy hooks not found; the game's own timeout stays."); return; }
                discoveryHarmony.Patch(host, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeStartHostTimeout", BindingFlags.Static | BindingFlags.NonPublic)));
                discoveryHarmony.Patch(connect, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeConnectTimeout", BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo("Per-connection disconnect timeout ready (bridge/host: long, direct connections: the game's value).");
            }
            catch (Exception e) { Logger.LogWarning("Timeout policy unavailable: " + e.Message); }
        }

        static void BeforeStartHostTimeout()
        {
            SteamBridgePlugin p = discoveryInstance;
            ApplyTimeout(p != null && p.longHostTimeout != null && p.longHostTimeout.Value, "hosting");
        }

        static void BeforeConnectTimeout(string __0)
        {
            bool viaBridge = __0 != null && __0.StartsWith("127.", StringComparison.Ordinal);
            ApplyTimeout(viaBridge, viaBridge ? "joining through the bridge" : "direct connection to " + __0);
        }

        static void ApplyTimeout(bool longTimeout, string why)
        {
            try
            {
                UNetManager manager = UNetManager.instance; if (manager == null) return;
                ConnectionConfig config = manager.connectionConfig; if (config == null) return;
                if (!vanillaKnown) { vanillaTimeout = config.DisconnectTimeout; vanillaKnown = true; if (vanillaTimeout >= LongTimeoutMs) vanillaTimeout = 2000; }
                uint value = longTimeout ? LongTimeoutMs : vanillaTimeout;
                if (config.DisconnectTimeout != value) config.DisconnectTimeout = value;
                if (discoveryInstance != null) discoveryInstance.Logger.LogInfo("Disconnect timeout " + value + " ms (" + why + ")");
            }
            catch (Exception e) { if (discoveryInstance != null) discoveryInstance.Logger.LogWarning("Timeout policy failed: " + e.Message); }
        }
    }
}