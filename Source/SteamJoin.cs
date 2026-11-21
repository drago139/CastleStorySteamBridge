using System;
using Steamworks;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.67: "Join Game" / invite from the Steam Friends list. Steam sends GameLobbyJoinRequested_t to the running game; the game's own SteamManager joins that lobby and
    // then calls UNetManager.ConnectToServerWithMatchData with a match that carries ONLY the lobby id. These traces show where that path ends (STEAMJOIN lines).
    public sealed partial class SteamBridgePlugin
    {
        Callback<GameLobbyJoinRequested_t> steamJoinLobbyCb; Callback<GameRichPresenceJoinRequested_t> steamJoinRichCb;

        void InstallSteamJoinTrace()
        {
            try
            {
                steamJoinLobbyCb = Callback<GameLobbyJoinRequested_t>.Create(delegate(GameLobbyJoinRequested_t p)
                {
                    Logger.LogInfo("STEAMJOIN: Steam sent GameLobbyJoinRequested lobby=" + p.m_steamIDLobby.m_SteamID + " friend=" + p.m_steamIDFriend.m_SteamID + "; game already in a match (NetworkClient.active)=" + NetworkClient.active + "; " + SteamJoinLobbyDiag(p.m_steamIDLobby));
                });
                steamJoinRichCb = Callback<GameRichPresenceJoinRequested_t>.Create(delegate(GameRichPresenceJoinRequested_t p)
                {
                    Logger.LogInfo("STEAMJOIN: Steam sent GameRichPresenceJoinRequested friend=" + p.m_steamIDFriend.m_SteamID + " connect='" + p.m_rgchConnect + "'");
                });
                Logger.LogInfo("STEAMJOIN: join-request trace ready");
            }
            catch (Exception e) { Logger.LogWarning("STEAMJOIN trace unavailable: " + e.Message); }
        }

        internal static string SteamJoinLobbyDiag(CSteamID lobby)
        {
            try
            {
                if (lobby.m_SteamID == 0) return "lobby=0";
                CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);
                return "owner=" + owner.m_SteamID + " members=" + SteamMatchmaking.GetNumLobbyMembers(lobby)
                    + " csb_host='" + SteamMatchmaking.GetLobbyData(lobby, "csb_host") + "' csb_ver='" + SteamMatchmaking.GetLobbyData(lobby, "csb_ver")
                    + "' csb_seen='" + SteamMatchmaking.GetLobbyData(lobby, "csb_seen") + "' name='" + SteamMatchmaking.GetLobbyData(lobby, "name")
                    + "' version='" + SteamMatchmaking.GetLobbyData(lobby, "version") + "' hostInternalIP='" + SteamMatchmaking.GetLobbyData(lobby, "hostInternalIP") + "'";
            }
            catch (Exception e) { return "lobby diag failed: " + e.Message; }
        }
    }
}