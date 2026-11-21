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
        ConfigEntry<bool> longHostTimeout; static ConfigEntry<string> radminModHosts;
        static string radminJoinTarget; static bool radminJoinVanilla;   // 0.4.26: set by JoinViaRadmin (RadminPath.cs), read by BeforeConnectTimeout

        void InstallTimeoutPolicy()
        {
            try
            {
                longHostTimeout = Config.Bind("Network", "LongTimeoutWhenHosting", true, "Hosted lobbies use a 10-minute UNet disconnect timeout (needed because bridged links can stall for seconds). Set false only if you host for players without the mod over LAN or port forwarding.");
                MethodInfo host = typeof(UNetManager).GetMethod("StartHost", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                MethodInfo connect = typeof(UNetManager).GetMethod("ConnectToServer", new Type[] { typeof(string), typeof(int) });
                radminModHosts = Config.Bind("Network", "RadminModHosts", "", "Radmin VPN (26.x.x.x) hosts that run this mod, comma separated. Joining them uses the long timeout the mod hosts with; every other 26.x host (a vanilla game in the public Castle Story gaming network) is joined with the game's own timeout, otherwise UNet refuses with CRCMismatch.");
                if (host == null || connect == null) { Logger.LogWarning("Timeout policy hooks not found; the game's own timeout stays."); return; }
                discoveryHarmony.Patch(host, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeStartHostTimeout", BindingFlags.Static | BindingFlags.NonPublic)));
                discoveryHarmony.Patch(connect, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("BeforeConnectTimeout", BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo("Per-connection disconnect timeout ready (bridge/host: long, direct connections: the game's value).");
                radminSilenceCfg = Config.Bind("Network", "RadminHostSilenceSeconds", 60, new ConfigDescription("Guest on a direct connection with the long timeout (Radmin VPN or an address typed in F8, no Steam bridge): after this many seconds without any UNet packet from the host one RADMIN-STALL line is logged and F8 shows a warning. Nothing is disconnected (the UNet timeout stays 600 s). 0 = off.", new AcceptableValueRange<int>(0, 600)));
            }
            catch (Exception e) { Logger.LogWarning("Timeout policy unavailable: " + e.Message); }
        }

        // 0.4.27 review (F-01 of the 2026-11-09 review): a direct guest that joined with the long timeout has no bridge, so nothing watches the host (Update returns at
        // 'bridge == null' before WatchGameSession; the P2P hold needs the bridge) and UNet reports a dead host only after 600 s. WARN ONLY: the transport's incoming
        // packet counter (it counts UNet's own pings too, so an idle lobby is not silent) is read once a second; when it stands still for RadminHostSilenceSeconds,
        // one RADMIN-STALL line is logged and F8 shows it. The player decides; nothing is disconnected.
        ConfigEntry<int> radminSilenceCfg; float rwNext, rwLastChange; int rwLastCount = -1; bool rwWarned;
        void RadminWatchTick()
        {
            if (bridge != null || hosting || radminSilenceCfg == null || radminSilenceCfg.Value <= 0) { rwLastCount = -1; rwWarned = false; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < rwNext) return;
            rwNext = now + 1f;
            try
            {
                UNetManager m = UNetManager.instance;
                NetworkClient client = m == null ? null : m.LocalNetworkClient;
                NetworkConnection c = client == null ? null : client.connection;
                if (c == null || c.hostId < 0 || NetworkServer.active || !client.isConnected || m.connectionConfig == null || m.connectionConfig.DisconnectTimeout < LongTimeoutMs || LoopbackAddress.IsLoopback(c.address))
                { rwLastCount = -1; rwWarned = false; return; }
                byte error; int count = NetworkTransport.GetIncomingPacketCount(c.hostId, c.connectionId, out error);
                if (error != 0) return;   // 0.4.27 verify (A-05-v): a failed read is not a packet; it used to reset the counter, and the next good read then looked like "the host answers again" in the middle of a stall
                if (count != rwLastCount)
                {
                    // 0.4.27 verify (A-05-v): the warning opened F8 with "the host is silent ... leave by hand"; that text stayed there after the host came back
                    if (rwWarned) { Logger.LogInfo("RADMIN-STALL over: the host " + c.address + " answers again after " + (now - rwLastChange).ToString("F0") + " s"); status = "Хост знову відповідає (мовчав " + (int)(now - rwLastChange) + " с)."; }
                    rwLastCount = count; rwLastChange = now; rwWarned = false; return;
                }
                if (rwWarned || now - rwLastChange < radminSilenceCfg.Value) return;
                rwWarned = true;
                Logger.LogWarning("RADMIN-STALL: no UNet packet from the host " + c.address + " for " + (now - rwLastChange).ToString("F0") + " s (last game message " + (UnityEngine.Time.time - c.lastMessageTime).ToString("F0") + " s ago); the 600 s timeout keeps the connection, nothing is disconnected");
                status = "Хост мовчить " + (int)(now - rwLastChange) + " с (пряме з'єднання, тайм-аут 10 хв). Якщо не повернеться - вийди в головне меню вручну."; visible = true;
            }
            catch (Exception) { }
        }

        static void BeforeStartHostTimeout()
        {
            SteamBridgePlugin p = discoveryInstance;
            ApplyTimeout(p != null && p.longHostTimeout != null && p.longHostTimeout.Value, "hosting");
        }

        static void BeforeConnectTimeout(string __0)
        {
            bool viaBridge = LoopbackAddress.IsLoopback(__0);
            // B-035: a Radmin VPN host (26.x.x.x) runs the mod and hosts with the long timeout, so the guest must match or UNet answers CRCMismatch.
            // 0.4.26: an address typed into the F8 Radmin join box is a mod host by default (long timeout, no cfg edit); the typed choice is
            // consumed by this one connect (review 2026-11-09: it must not leak into a later join of the same address). See RadminTimeoutRules.
            bool viaRadmin = RadminTimeoutRules.UseLongOnce(__0, ref radminJoinTarget, ref radminJoinVanilla, radminModHosts != null ? radminModHosts.Value : null);
            string why = viaBridge ? "joining through the bridge"
                : !viaRadmin ? "direct connection to " + __0
                : __0.StartsWith("26.", StringComparison.Ordinal) ? "joining through Radmin VPN (long timeout to match a mod host) " + __0
                : "direct join to a mod host typed in the F8 box (long timeout) " + __0;
            ApplyTimeout(viaBridge || viaRadmin, why);
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
