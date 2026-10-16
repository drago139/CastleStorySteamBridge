using System;
using System.IO;
using BepInEx.Logging;
using Brix.Engine;
using Brix.Game.Network;
using Brix.Network;
using Network;

namespace CastleStorySteam
{
    // 0.3.39: the optional 10-minute UNet DisconnectTimeout patcher rewrites ConnectionConfig for the whole game. UNet refuses a
    // connection when the two ends use different connection configs, so a player with the patcher could not join (or host for)
    // anyone without it (a stranger from the public list). The patcher is therefore opt-in: it stays only if the file
    // BepInEx\config\long-disconnect-timeout.keep exists (the installer writes it when "10 minutes" is ticked; every friend of
    // such a group must do the same). Otherwise it is renamed out of the way and the next start uses the standard config.
    internal static class NetworkCompat
    {
        internal static string Status = "";
        internal static void Apply(ManualLogSource log)
        {
            try
            {
                string root = BepInEx.Paths.GameRootPath;
                string patcher = Path.Combine(Path.Combine(Path.Combine(root, "BepInEx"), "patchers"), "CastleStoryNetworkTimeout.dll");
                string keep = Path.Combine(Path.Combine(Path.Combine(root, "BepInEx"), "config"), "long-disconnect-timeout.keep");
                if (!File.Exists(patcher)) { Status = "Мережа: довгий тайм-аут для лобі й мосту, власний для прямих з'єднань."; return; }
                if (File.Exists(keep)) { try { File.Delete(keep); } catch (Exception) { } }
                string disabled = patcher + ".disabled-vanilla-compat";
                if (File.Exists(disabled)) File.Delete(disabled);
                File.Move(patcher, disabled);
                Status = "Мережа: старий глобальний патч тайм-ауту вимкнено; тайм-аут тепер обирається для кожного з'єднання. Перезапусти гру.";
                log.LogWarning("Legacy 10-minute timeout patcher disabled for compatibility with players without the mod; restart the game to take effect.");
            }
            catch (Exception e) { Status = ""; log.LogWarning("Network compatibility check failed: " + e.Message); }
        }
    }
}

namespace CastleStorySteam
{
    // 0.3.39 diagnostics: why a server may be missing from the list. Logs (at most every 10 s, and only while the list is open)
    // how many Steam lobbies the game received and what each friend advertises. Changes nothing.
    public sealed partial class SteamBridgePlugin
    {
        float nextLobbyLog, nextPresenceLog; int lobbyLogs, presenceLogs;
        void InstallDiscoveryDiag()
        {
            try
            {
                System.Reflection.MethodInfo target = typeof(Brix.Game.Network.SteamManager).GetMethod("LobbyMatchListReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                System.Reflection.MethodInfo hook = typeof(SteamBridgePlugin).GetMethod("AfterLobbyListDiag", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (target != null && hook != null) discoveryHarmony.Patch(target, null, new HarmonyLib.HarmonyMethod(hook));
                else Logger.LogWarning("Lobby list diagnostics hook not found.");
            }
            catch (System.Exception e) { Logger.LogWarning("Lobby list diagnostics unavailable: " + e.Message); }
        }
        static void AfterLobbyListDiag(Steamworks.LobbyMatchList_t param)
        {
            SteamBridgePlugin p = discoveryInstance;
            if (p == null || UnityEngine.Time.realtimeSinceStartup < p.nextLobbyLog || p.lobbyLogs >= 30) return;
            p.nextLobbyLog = UnityEngine.Time.realtimeSinceStartup + 10f; p.lobbyLogs++;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < param.m_nLobbiesMatching && i < 12; i++)
                {
                    Steamworks.CSteamID id = Steamworks.SteamMatchmaking.GetLobbyByIndex(i);
                    sb.Append(" [").Append(Steamworks.SteamMatchmaking.GetLobbyData(id, "name")).Append(" v=").Append(Steamworks.SteamMatchmaking.GetLobbyData(id, "version"))
                      .Append(" players=").Append(Steamworks.SteamMatchmaking.GetLobbyData(id, "playercount")).Append(" ip=").Append(Steamworks.SteamMatchmaking.GetLobbyData(id, "hostInternalIP")).Append(" owner=").Append(OwnerName(id)).Append(" members=").Append(Steamworks.SteamMatchmaking.GetNumLobbyMembers(id)).Append(" keys=").Append(LobbyKeys(id)).Append("]");
                }
                p.Logger.LogInfo("Steam lobby list: " + param.m_nLobbiesMatching + " lobby(ies) received;" + sb + "; own game version=" + Neo.SemanticVersion);
            }
            catch (System.Exception e) { p.Logger.LogWarning("Lobby list diagnostics failed: " + e.Message); }
        }
        static string OwnerName(Steamworks.CSteamID lobby)
        {
            try { Steamworks.CSteamID o = Steamworks.SteamMatchmaking.GetLobbyOwner(lobby); return o.m_SteamID == 0 ? "?" : Steamworks.SteamFriends.GetFriendPersonaName(o) + "/" + o.m_SteamID + (o == Steamworks.SteamUser.GetSteamID() ? "(me)" : ""); } catch (System.Exception) { return "?"; }
        }
        static string LobbyKeys(Steamworks.CSteamID lobby)
        {
            try
            {
                int n = Steamworks.SteamMatchmaking.GetLobbyDataCount(lobby); System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < n && i < 16; i++) { string k, v; if (Steamworks.SteamMatchmaking.GetLobbyDataByIndex(lobby, i, out k, 64, out v, 128)) sb.Append(k).Append('=').Append(v.Length > 24 ? v.Substring(0, 24) : v).Append(','); }
                return n + ":" + sb;
            }
            catch (System.Exception) { return "?"; }
        }        // Called from UpdateDiscovery while the menus are shown.
        void LogFriendPresence(string summary)
        {
            if (UnityEngine.Time.realtimeSinceStartup < nextPresenceLog || presenceLogs >= 30) return;
            nextPresenceLog = UnityEngine.Time.realtimeSinceStartup + 10f; presenceLogs++;
            Logger.LogInfo("Friends in Castle Story: " + summary + "; advertised to the list: " + advertised.Count);
        }
    }
}