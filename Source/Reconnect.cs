using System;
using BepInEx;
using BepInEx.Configuration;
using Brix.Game.Network;
using Brix.Network;
using Network;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.57: "reconnect to the game that I dropped out of". The last host a guest joined successfully is kept in the config ([Network] LastHost),
    // F8 shows a button to join it again, and if the game died while that session was open (crash, killed process) the F8 window opens by itself
    // on the next start with the button ready. A clean end of the session (leaving, host closing, normal quit) clears only the "died" mark.
    public sealed partial class SteamBridgePlugin
    {
        ConfigEntry<string> lastHostEntry; ConfigEntry<bool> lastHostOpenEntry; ulong lastHostId; string lastHostName = "";

        void InstallReconnect()
        {
            try
            {
                lastHostEntry = Config.Bind("Network", "LastHost", "", "Last host this player joined (id;name). Used by the F8 reconnect button; safe to clear.");
                lastHostOpenEntry = Config.Bind("Network", "LastHostOpen", false, "True while a joined session is open; if it is still true at start, the game closed unexpectedly.");
                string v = lastHostEntry.Value ?? ""; int sep = v.IndexOf(';');
                ulong id; if (UInt64.TryParse(sep > 0 ? v.Substring(0, sep) : v, out id) && LooksLikeSteamId(id)) { lastHostId = id; lastHostName = sep > 0 ? v.Substring(sep + 1) : ""; }
                if (lastHostId != 0 && lastHostOpenEntry.Value)
                {
                    lastHostOpenEntry.Value = false; visible = true;
                    status = "Схоже, гра закрилась під час сесії. Натисни «Перепідключитися» щоб повернутись до " + LastHostLabel() + ".";
                    Logger.LogInfo("Previous session did not end cleanly; offering reconnect to " + lastHostId);
                }
            }
            catch (Exception e) { Logger.LogWarning("Reconnect setup failed: " + e.Message); }
        }

        string LastHostLabel()
        {
            string n = lastHostName;
            try { if (String.IsNullOrEmpty(n) && lastHostId != 0 && SteamManager.Initialized) n = SteamFriends.GetFriendPersonaName(new CSteamID(lastHostId)); } catch (Exception) { }
            return String.IsNullOrEmpty(n) || n == "[unknown]" ? lastHostId.ToString() : n;
        }

        // The game really connected through the bridge: this host is the one to come back to.
        void RememberHost()
        {
            try
            {
                if (host || peer.m_SteamID == 0 || lastHostEntry == null) return;
                lastHostId = peer.m_SteamID;
                string n = SteamManager.Initialized ? SteamFriends.GetFriendPersonaName(peer) : "";
                lastHostName = String.IsNullOrEmpty(n) || n == "[unknown]" ? "" : n.Replace(';', ' ');
                lastHostEntry.Value = lastHostId + ";" + lastHostName;
                if (lastHostOpenEntry != null) lastHostOpenEntry.Value = true;
            }
            catch (Exception e) { Logger.LogWarning("Could not remember host: " + e.Message); }
        }

        void ClearReconnectOpen()
        {
            try { if (lastHostOpenEntry != null && lastHostOpenEntry.Value) lastHostOpenEntry.Value = false; } catch (Exception) { }
        }

        void DrawReconnect()
        {
            if (lastHostId == 0 || !SteamManager.Initialized || lastHostId == SteamUser.GetSteamID().m_SteamID) return;
            if (GUILayout.Button("↻ Перепідключитися до " + LastHostLabel())) Reconnect();
        }

        // After the bridge is gone the game's own lobby window would stay open ("ghost lobby") with a dead host; leave it like a normal disconnect.
        void LeaveGameSession()
        {
            try
            {
                if (UNetManager.instance != null && (NetworkClient.active || UNetManager.instance.LocalLobbyPlayer != null)) UNetManager.instance.Disconnect();
            }
            catch (Exception e) { Logger.LogWarning("Could not leave the game lobby: " + e.Message); }
        }

        void Reconnect()
        {
            if (hosting || bridge != null) { status = "Міст уже працює."; return; }
            if (NetworkClient.active || (UNetManager.instance != null && UNetManager.instance.LocalLobbyPlayer != null))
            { status = "Спочатку вийди в головне меню гри, потім натисни ще раз."; return; }
            peer = new CSteamID(lastHostId); peerByCode = !IsFriend(peer);
            Logger.LogInfo("Reconnecting to the last host " + lastHostId + (peerByCode ? " (not a friend)" : " (friend)"));
            StartBridge(false);
        }
    }
}