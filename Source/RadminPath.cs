using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
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
        ConfigEntry<string> radminHostEntry; string radminHostText = ""; string radminStatus = ""; bool radminHostVanilla;
        sealed class RadminState { internal string Service, Ip; internal long CheckedAt; }
        volatile RadminState radminState;
        int radminProbeBusy;
        [StructLayout(LayoutKind.Sequential)]
        struct RadminServiceStatus { public uint Type, State, Controls, Win32Exit, ServiceExit, CheckPoint, WaitHint; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr service, out RadminServiceStatus status);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);

        static string ReadRadminService()
        {
            IntPtr manager = IntPtr.Zero, service = IntPtr.Zero;
            try
            {
                manager = OpenSCManager(null, null, 1); // CONNECT only; never start/configure a service.
                if (manager == IntPtr.Zero) return "Unknown";
                service = OpenService(manager, "RvControlSvc", 4); // QUERY_STATUS only.
                if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() == 1060 ? "Not installed" : "Unknown";
                RadminServiceStatus state;
                if (!QueryServiceStatus(service, out state)) return "Unknown";
                return state.State == 4 ? "Running" : (state.State == 1 ? "Stopped" : "Not running (state " + state.State + ")");
            }
            catch (Exception) { return "Unknown"; }
            finally { if (service != IntPtr.Zero) CloseServiceHandle(service); if (manager != IntPtr.Zero) CloseServiceHandle(manager); }
        }

        RadminState CurrentRadminState()
        {
            RadminState state = radminState;
            long age = state == null ? Int64.MaxValue : DateTime.UtcNow.Ticks - state.CheckedAt;
            if ((age < 0 || age > TimeSpan.TicksPerSecond * 10) && Interlocked.CompareExchange(ref radminProbeBusy, 1, 0) == 0)
            {
                try
                {
                    if (!ThreadPool.QueueUserWorkItem(delegate(object unused)
                    {
                        try
                        {
                            string service = ReadRadminService();
                            string ip = service == "Running" ? FindRadminIp() : null;
                            radminState = new RadminState { Service = service, Ip = ip, CheckedAt = DateTime.UtcNow.Ticks };
                        }
                        finally { Interlocked.Exchange(ref radminProbeBusy, 0); }
                    })) Interlocked.Exchange(ref radminProbeBusy, 0);
                }
                catch (Exception) { Interlocked.Exchange(ref radminProbeBusy, 0); }
            }
            return age >= 0 && age <= TimeSpan.TicksPerSecond * 15 ? state : null;
        }

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

        static string FindRadminIp()
        {
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
                        if (named && ip.StartsWith("26.", StringComparison.Ordinal)) return ip;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        void DrawRadminHost()
        {
            RadminState state = CurrentRadminState();
            GUILayout.Label("Radmin VPN service: " + (state == null ? "Checking" : state.Service));
            if (state == null || state.Service != "Running") { GUILayout.Label("Резервний шлях недоступний: службу Radmin VPN не підтверджено як Running."); return; }
            string ip = state.Ip;
            if (ip == null) { GUILayout.Label("Radmin VPN: активну адресу 26.x не знайдено."); return; }
            string addr = ip + ":" + hostingPort;
            GUILayout.Label("Резервний шлях (Radmin VPN): адреса хоста " + addr + ". Потрібна спільна мережа Radmin; доступність не перевірена (без мосту й каналу позицій).");
            if (GUILayout.Button("Скопіювати адресу Radmin")) { GUIUtility.systemCopyBuffer = addr; status = "Адресу Radmin скопійовано: " + addr; }
        }

        void DrawRadminJoin()
        {
            RadminState state = CurrentRadminState();
            GUILayout.Label("Radmin VPN service: " + (state == null ? "Checking" : state.Service));
            if (state == null || state.Service != "Running") { GUILayout.Label("Резервний шлях недоступний, доки служба не Running."); return; }
            GUILayout.Label("Резервний шлях через Radmin VPN (якщо Steam нестабільний). Адреса хоста в Radmin, наприклад 26.1.2.3:3000:");
            GUILayout.Label("Хост з модом: довгий тайм-аут, як у хоста, у cfg нічого вписувати не треба. Якщо хост без мода або з LongTimeoutWhenHosting=false, познач птичку. CRCMismatch = тайм-аути не збіглися.");
            radminHostText = GUILayout.TextField(radminHostText ?? "", 40);
            radminHostVanilla = GUILayout.Toggle(radminHostVanilla, "Хост без мода (тайм-аут гри)");
            if (GUILayout.Button("Приєднатися через Radmin VPN")) JoinViaRadmin();
            if (!string.IsNullOrEmpty(radminStatus)) GUILayout.Label(radminStatus);
        }

        void JoinViaRadmin()
        {
            try
            {
                RadminState state = CurrentRadminState();
                if (state == null || state.Service != "Running") { radminStatus = "Radmin VPN service is not confirmed Running."; return; }
                string text = (radminHostText ?? "").Trim(); int port = 3000; string host = text; int c = text.LastIndexOf(':');
                if (c > 0) { int p; if (int.TryParse(text.Substring(c + 1), out p) && p > 0 && p < 65536) { port = p; host = text.Substring(0, c); } }
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) { radminStatus = "Введи адресу IPv4 хоста з Radmin (наприклад 26.1.2.3:3000)."; return; }
                host = ip.ToString();   // canonical dotted quad: the connect hook sees RakNet's print of the address and compares it exactly ("26.01.2.3" would never match)
                if (hosting || bridge != null) { radminStatus = "Міст працює. Зупини його (кнопка нижче) і спробуй знову."; return; }
                if (NetworkClient.active || UNetManager.instance == null || UNetManager.instance.LocalLobbyPlayer != null) { radminStatus = "Спочатку вийди в головне меню гри."; return; }
                if (!BotMenu.MenuLoaded<NetworkWaitingModalMenu>()) { radminStatus = "Меню мультиплеєра гри ще не завантажене: відкрий головне меню і спробуй знову."; return; }   // otherwise the signal below connects nothing (same guard as BotMenu.JoinDirect)
                if (radminHostEntry != null) radminHostEntry.Value = text;
                radminJoinTarget = host; radminJoinVanilla = radminHostVanilla;   // 0.4.26: BeforeConnectTimeout consumes these on the connect this button starts (long timeout unless ticked)
                Logger.LogInfo("Joining through the Radmin VPN reserve route: " + host + ":" + port + (radminHostVanilla ? " (host without the mod: the game's own timeout)" : " (mod host: long timeout)"));
                NetworkServersMenu.SelectedMatchData = new MatchList.MatchData { IsLanGame = true, Address = new MatchAddress(host, (ushort)port), MatchName = "Radmin VPN" };
                NetworkWaitingModalMenu.OpenAsClientSignal.Invoke();
                radminStatus = "Підключаюсь через Radmin VPN: " + host + ":" + port + " ...";
            }
            catch (Exception e) { radminStatus = "Не вдалося: " + e.Message; Logger.LogWarning("Radmin join failed: " + e.Message); }
        }
    }
}
