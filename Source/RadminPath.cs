using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Brix.Game.Network;
using Brix.Network;
using Brix.UI.Builder.Menu;
using BepInEx.Configuration;
using Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.60: (1) link diagnostics: which route Steam uses (direct P2P or relay) and how many unit-position packets are lost on the way
    // (sequence-number gaps on channel 78), written into STATS and FREEZE lines; (2) a reserve route through Radmin VPN: the game's own direct (LAN)
    // connection to the host's Radmin address. It does not use the Steam bridge, so channel 78, mod moderation and the ping meter are not active on it.
    public sealed partial class SteamBridgePlugin
    {
        ConfigEntry<string> radminHostEntry; string radminHostText = ""; string radminCached; float radminCheckedAt = -100f; string radminStatus = "";

        void InstallRadmin()
        {
            try { radminHostEntry = Config.Bind("Network", "RadminHost", "", "Last host address (ip or ip:port) typed for the Radmin VPN reserve route."); radminHostText = radminHostEntry.Value ?? ""; }
            catch (Exception) { }
        }

        // The route Steam uses to each connected peer: "P2P", "relay" or a mix for a host with several guests.
        string LinkPathNow()
        {
            try
            {
                int direct = 0, relay = 0;
                if (bridge != null && peer.m_SteamID != 0) CountRoute(peer, ref direct, ref relay);
                else if (hosting) foreach (KeyValuePair<CSteamID, BridgeCore> g in guests) CountRoute(g.Key, ref direct, ref relay);
                if (direct + relay == 0) return "route=none";
                return "route=" + (relay == 0 ? "P2P" : (direct == 0 ? "relay" : "mixed " + relay + "/" + (direct + relay) + " relay"));
            }
            catch (Exception) { return "route=?"; }
        }

        static void CountRoute(CSteamID id, ref int direct, ref int relay)
        {
            P2PSessionState_t s;
            if (SteamManager.Initialized && SteamNetworking.GetP2PSessionState(id, out s) && s.m_bConnectionActive != 0) { if (s.m_bUsingRelay != 0) relay++; else direct++; }
        }

        // Packet loss of the unit-position channel since the previous call (used once a minute by STATS).
        string PosLossReport()
        {
            int got = posWinGot, lost = posWinLost; posWinGot = posWinLost = 0;
            if (got + lost == 0) return "posLoss=n/a";
            return "posLoss=" + lost + "/" + (got + lost) + " (" + (100f * lost / (got + lost)).ToString("F1") + "%)";
        }

        string FindRadminIp()
        {
            float now = Time.realtimeSinceStartup;
            if (now - radminCheckedAt < 10f) return radminCached;
            radminCheckedAt = now; radminCached = null;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    bool named = (ni.Name + " " + ni.Description).IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) >= 0;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (named || ip.StartsWith("26.")) { radminCached = ip; return ip; }
                    }
                }
            }
            catch (Exception) { }
            return radminCached;
        }

        void DrawRadminHost()
        {
            string ip = FindRadminIp();
            if (ip == null) { GUILayout.Label("Резервний шлях: Radmin VPN не знайдено (встанови Radmin VPN і зайди в спільну мережу)."); return; }
            string addr = ip + ":" + hostingPort;
            GUILayout.Label("Резервний шлях (Radmin VPN): адреса хоста " + addr + ". Гравці в тій самій мережі Radmin можуть зайти за нею (без мосту й без каналу позицій).");
            if (GUILayout.Button("Скопіювати адресу Radmin")) { GUIUtility.systemCopyBuffer = addr; status = "Адресу Radmin скопійовано: " + addr; }
        }

        void DrawRadminJoin()
        {
            GUILayout.Label("Резервний шлях через Radmin VPN (якщо Steam нестабільний). Адреса хоста в Radmin, наприклад 26.1.2.3:3000:");
            radminHostText = GUILayout.TextField(radminHostText ?? "", 40);
            if (GUILayout.Button("Приєднатися через Radmin VPN")) JoinViaRadmin();
            if (!string.IsNullOrEmpty(radminStatus)) GUILayout.Label(radminStatus);
        }

        void JoinViaRadmin()
        {
            try
            {
                string text = (radminHostText ?? "").Trim(); int port = 3000; string host = text; int c = text.LastIndexOf(':');
                if (c > 0) { int p; if (int.TryParse(text.Substring(c + 1), out p) && p > 0 && p < 65536) { port = p; host = text.Substring(0, c); } }
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) { radminStatus = "Введи адресу IPv4 хоста з Radmin (наприклад 26.1.2.3:3000)."; return; }
                if (hosting || bridge != null) { radminStatus = "Міст працює. Зупини його (кнопка нижче) і спробуй знову."; return; }
                if (NetworkClient.active || UNetManager.instance == null) { radminStatus = "Спочатку вийди в головне меню гри."; return; }
                if (radminHostEntry != null) radminHostEntry.Value = text;
                Logger.LogInfo("Joining through the Radmin VPN reserve route: " + host + ":" + port);
                NetworkServersMenu.SelectedMatchData = new MatchList.MatchData { IsLanGame = true, Address = new MatchAddress(host, (ushort)port), MatchName = "Radmin VPN" };
                NetworkWaitingModalMenu.OpenAsClientSignal.Invoke();
                radminStatus = "Підключаюсь через Radmin VPN: " + host + ":" + port + " ...";
            }
            catch (Exception e) { radminStatus = "Не вдалося: " + e.Message; Logger.LogWarning("Radmin join failed: " + e.Message); }
        }
    }
}