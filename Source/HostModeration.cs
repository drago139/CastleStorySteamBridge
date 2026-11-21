using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.40: host moderation. Kick removes a guest at once (bridge link and game connection); a timed ban also refuses its
    // hello for the chosen time, so a duplicated or stuck guest cannot occupy a slot again. Kept in memory only.
    public sealed partial class SteamBridgePlugin
    {
        readonly Dictionary<ulong, float> bannedUntil = new Dictionary<ulong, float>();
        readonly Dictionary<ulong, string> bannedNames = new Dictionary<ulong, string>();
        CSteamID kickTarget; float kickBanMinutes; bool kickPending;
        CSteamID unbanTarget; bool unbanPending, unbanAllPending;

        bool IsBanned(CSteamID id)
        {
            float until;
            if (!bannedUntil.TryGetValue(id.m_SteamID, out until)) return false;
            if (Time.realtimeSinceStartup < until) return true;
            bannedUntil.Remove(id.m_SteamID); bannedNames.Remove(id.m_SteamID);
            return false;
        }

        void RequestKick(CSteamID id, float banMinutes) { kickTarget = id; kickBanMinutes = banMinutes; kickPending = true; }

        // Main thread, start of UpdateGuests.
        void ApplyKickRequests()
        {
            if (unbanAllPending)
            {
                unbanAllPending = false;
                int n = bannedUntil.Count;
                bannedUntil.Clear(); bannedNames.Clear();
                status = n > 0 ? "Усіх розбанено (" + n + "). Вони можуть заходити знову: хай натиснуть «Приєднатися» ще раз." : "Заблокованих немає.";
                Logger.LogInfo("Host moderation: unban all (" + n + ")");
            }
            if (unbanPending)
            {
                unbanPending = false;
                string uname; bannedNames.TryGetValue(unbanTarget.m_SteamID, out uname);
                bannedUntil.Remove(unbanTarget.m_SteamID); bannedNames.Remove(unbanTarget.m_SteamID);
                status = "Розбанено: " + (uname ?? unbanTarget.m_SteamID.ToString()) + ". Гравець може зайти знову: хай натисне «Приєднатися» ще раз.";
                Logger.LogInfo("Host moderation: unban " + (uname ?? unbanTarget.m_SteamID.ToString()));
            }
            if (!kickPending) return;
            kickPending = false;
            CSteamID id = kickTarget;
            string name = PingName(id);
            if (kickBanMinutes > 0f)
            {
                bannedUntil[id.m_SteamID] = Time.realtimeSinceStartup + kickBanMinutes * 60f;
                bannedNames[id.m_SteamID] = name;
            }
            if (guests.ContainsKey(id)) RemoveGuest(id, true);    // sends goodbye, drops the bridge; the guard then drops its game connection
            status = kickBanMinutes > 0f ? "Гравця " + name + " вигнано й заблоковано на " + (int)kickBanMinutes + " хв. Скасувати: кнопка «Розбан» нижче в списку заблокованих." : "Гравця " + name + " вигнано.";
            Logger.LogInfo("Host moderation: " + status);
        }

        void DrawBans()
        {
            if (bannedUntil.Count == 0) return;
            GUILayout.Label("Заблоковані гравці. «Розбан» одразу пускає гравця назад (бан діє до кінця часу або до перезапуску гри):");
            List<ulong> ids = new List<ulong>(bannedUntil.Keys);
            foreach (ulong id in ids)
            {
                float left;
                if (!bannedUntil.TryGetValue(id, out left)) continue;
                int minutes = Mathf.Max(0, Mathf.CeilToInt((left - Time.realtimeSinceStartup) / 60f));
                string name; bannedNames.TryGetValue(id, out name);
                GUILayout.BeginHorizontal();
                GUILayout.Label((name ?? id.ToString()) + " — ще " + minutes + " хв");
                if (GUILayout.Button("Розбан", GUILayout.Width(80))) { unbanTarget = new CSteamID(id); unbanPending = true; }
                GUILayout.EndHorizontal();
            }
            if (ids.Count > 1 && GUILayout.Button("Розбанити всіх")) unbanAllPending = true;
        }
    }
}