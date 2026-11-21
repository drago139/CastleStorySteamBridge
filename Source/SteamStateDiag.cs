using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.Network;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.4.2: log-only diagnostics for "I cannot join my friend" (2026-11-04: the guest's Steam status was Offline/Invisible, so his hellos
    // never reached the host and nothing was logged on either side). Changes no behaviour; nothing here sends data anywhere.
    //  - STEAMSTATE: own Steam status, warned on screen (F8 window) and in the log while it is Offline or Invisible.
    //  - STEAMJOIN: the client's 35 s "friend did not answer" timeout is now written to the log with the Steam P2P session state.
    //  - HOSTDROP: the host logs a hello / session request it refuses (not a friend, banned, full, not hosting), at most once per 30 s per sender.
    public sealed partial class SteamBridgePlugin
    {
        const int StatusOffline = 0, StatusInvisible = 7;
        ConfigEntry<bool> steamStatusWarnCfg;
        // [Diag] SteamStatusWarning (default true): the F8 / status-line warning only; the log lines are always written.
        bool SteamStatusWarnOn { get { try { if (steamStatusWarnCfg == null) steamStatusWarnCfg = Config.Bind("Diag", "SteamStatusWarning", true, "Warn in the F8 window and the join status when this PC's Steam status is Offline or Invisible (Steam then does not deliver the bridge packets to friends, so a join silently fails). Log lines STEAMSTATE / STEAMJOIN / HOSTDROP are written regardless. Changes nothing else."); return steamStatusWarnCfg.Value; } catch (Exception) { return true; } } }
        float statusCheckedAt; bool statusHidden; string statusName = "?";
        readonly Dictionary<ulong, float> hostDropLoggedAt = new Dictionary<ulong, float>();

        static string SteamStatusName(int state)
        {
            switch (state)
            {
                case 0: return "Offline";
                case 1: return "Online";
                case 2: return "Busy";
                case 3: return "Away";
                case 4: return "Snooze";
                case 5: return "LookingToTrade";
                case 6: return "LookingToPlay";
                case 7: return "Invisible";
                default: return "state " + state;
            }
        }

        // Cached for 2 s: the window asks every frame.
        void RefreshOwnSteamStatus()
        {
            if (Time.realtimeSinceStartup < statusCheckedAt + 2f) return;
            statusCheckedAt = Time.realtimeSinceStartup;
            try
            {
                if (!SteamManager.Initialized) { statusHidden = false; statusName = "Steam not initialised"; return; }
                int s = (int)SteamFriends.GetPersonaState();
                statusName = SteamStatusName(s);
                statusHidden = s == StatusOffline || s == StatusInvisible;
            }
            catch (Exception) { statusHidden = false; statusName = "?"; }
        }

        string OwnSteamStatusText() { RefreshOwnSteamStatus(); return statusName; }

        // Ukrainian text for the status line; empty when the status is fine.
        string OfflineStatusHint()
        {
            RefreshOwnSteamStatus();
            return statusHidden && SteamStatusWarnOn ? "Твій статус у Steam: " + statusName + ". Через це друг не отримує пакети з мосту. Постав «У мережі» (Online) і спробуй знову." : "";
        }

        void DrawSteamStatusWarning()
        {
            string hint = OfflineStatusHint();
            if (hint.Length == 0) return;
            Color old = GUI.color; GUI.color = new Color(1f, 0.55f, 0.35f);
            GUILayout.Label(hint);
            GUI.color = old;
        }

        // Called when a client bridge starts (the join begins).
        void SteamJoinStartDiag()
        {
            try
            {
                RefreshOwnSteamStatus();
                Logger.LogInfo("STEAMSTATE: own Steam status=" + statusName + " when joining " + peer + SteamPeerDiag(peer));
                PtLog("STEAMSTATE join start: own Steam status=" + statusName + SteamPeerDiag(peer));
                if (statusHidden)
                {
                    Logger.LogWarning("STEAMSTATE: own Steam status is " + statusName + ": Steam may not deliver P2P packets to friends, the host will not see this join. Set the status to Online.");
                    if (SteamStatusWarnOn) { status = OfflineStatusHint(); visible = true; }
                }
            }
            catch (Exception e) { Logger.LogWarning("STEAMSTATE diagnostics failed: " + e.Message); }
        }

        // Called when the client gave up after 35 s without any reply.
        void SteamJoinNoAnswerDiag(float waitedSeconds)
        {
            try
            {
                RefreshOwnSteamStatus();
                Logger.LogWarning("STEAMJOIN: no answer from host " + peer + " in " + waitedSeconds.ToString("F0") + " s (Hello sent every 2 s, none acknowledged); own Steam status=" + statusName + SteamPeerDiag(peer)
                    + (statusHidden ? "; own status hides this PC from friends: set it to Online" : "; check that the host is in an open lobby, its game is not frozen and its Steam status is Online"));
                PtLog("STEAMJOIN no answer from host in " + waitedSeconds.ToString("F0") + " s; own Steam status=" + statusName + SteamPeerDiag(peer));
                if (statusHidden && SteamStatusWarnOn) status = OfflineStatusHint();
            }
            catch (Exception e) { Logger.LogWarning("STEAMJOIN diagnostics failed: " + e.Message); }
        }

        static string SteamPeerDiag(CSteamID id)
        {
            try
            {
                P2PSessionState_t s;
                if (!SteamManager.Initialized || id.m_SteamID == 0) return "";
                if (!SteamNetworking.GetP2PSessionState(id, out s)) return "; P2P session: none yet";
                return "; P2P session: active=" + s.m_bConnectionActive + " connecting=" + s.m_bConnecting + " error=" + s.m_eP2PSessionError + " relay=" + s.m_bUsingRelay
                    + " queuedBytes=" + s.m_nBytesQueuedForSend + " queuedPackets=" + s.m_nPacketsQueuedForSend;
            }
            catch (Exception) { return ""; }
        }

        // Host side: a hello or a session request was refused. Rate-limited so a hello every 2 s does not flood the log.
        void HostDropLog(CSteamID sender, string where)
        {
            try
            {
                float last;
                if (hostDropLoggedAt.TryGetValue(sender.m_SteamID, out last) && Time.realtimeSinceStartup - last < 30f) return;
                hostDropLoggedAt[sender.m_SteamID] = Time.realtimeSinceStartup;
                string reason = !hosting ? "this PC is not hosting a bridge"
                    : !IsFriend(sender) && !AllowStrangersOn ? "sender is not a Steam friend (AllowStrangers=false)"
                    : IsBanned(sender) ? "sender is banned by host moderation"
                    : closeGuests.ContainsKey(sender) ? "sender was just removed (2 s wait)"
                    : guests.Count >= 3 ? "bridge is full (3 guests)"
                    : "refused";
                PtLog("HOSTDROP " + where + " refused: " + reason + "; own Steam status=" + OwnSteamStatusText());
                Logger.LogInfo("HOSTDROP: " + where + " from " + sender + " (" + SteamFriends.GetFriendPersonaName(sender) + "): " + reason + "; own Steam status=" + OwnSteamStatusText());
            }
            catch (Exception) { }
        }
    }
}
