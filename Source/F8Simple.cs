using System;
using BepInEx.Configuration;
using Brix.Game.Network;
using Brix.Network;
using Network;
using Steamworks;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.4.x: the simple F8 window for a new player. [Interface] SimpleF8 (default true) picks it; false keeps the old single long list in
    // SteamBridgePlugin.DrawContent. Only existing panels are redrawn in a new order with plain-language hints on three tabs:
    // "Гра" (what to press now), "Налаштування" (look and controls), "Діагностика" (ping, reports, reserve path, test tools).
    // No packet, session, config value or game behaviour changes; every button calls the same method as before.
    public sealed partial class SteamBridgePlugin
    {
        ConfigEntry<bool> simpleF8;
        bool SimpleF8 { get { return simpleF8 == null || simpleF8.Value; } }
        int f8Tab;
        bool f8CodeOpen;
        static readonly string[] F8Tabs = { "Гра", "Налаштування", "Діагностика" };
        GUIStyle f8StateStyle, f8SteamStyle;
        ConfigEntry<int> f8HintLeft;
        float f8HintStart = -1f;
        GUIStyle f8HintStyle;

        // Rich text is on for the state line only; a friend's name must not be able to inject tags.
        static string F8Safe(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace('<', '‹').Replace('>', '›');
        }

        string F8StateLine()
        {
            if (!SteamManager.Initialized) return "<color=#ff5a4d>●</color> Steam не запущено";
            if (hosting) return "<color=#4cd964>●</color> Ти хост. Гравців підключено: " + guests.Count + " із 3";
            if (bridge != null)
                return (sessionSeen ? "<color=#4cd964>●</color> Ти в грі друга " : "<color=#ffd83a>●</color> Підключаюся до ")
                    + F8Safe(SteamFriends.GetFriendPersonaName(peer));
            return "<color=#b0b0b0>●</color> Не підключено до гри друга";
        }

        static string SteamStatusUa(string name)
        {
            switch (name)
            {
                case "Online": return "у мережі";
                case "Away": return "відійшов (ти все одно в мережі)";
                case "Busy": return "зайнятий (ти все одно в мережі)";
                case "Snooze": return "сплю (ти все одно в мережі)";
                case "LookingToTrade": case "LookingToPlay": return "у мережі";
                case "Offline": return "ОФЛАЙН";
                case "Invisible": return "НЕВИДИМИЙ";
                default: return name;
            }
        }

        // Always visible above the tabs. Green = friends can reach this PC; red = Steam status Offline / Invisible: a join silently fails then,
        // so the player is told why. Same switch as the old warning: [Diag] SteamStatusWarning (false hides this line too).
        void DrawSteamStatusLine()
        {
            if (!SteamManager.Initialized || !SteamStatusWarnOn) return;
            RefreshOwnSteamStatus();
            if (f8SteamStyle == null)
            {
                f8SteamStyle = new GUIStyle(GUI.skin.label);
                f8SteamStyle.richText = true; f8SteamStyle.wordWrap = true;
            }
            if (statusHidden)
                GUILayout.Label("<color=#ff5a4d>\u25CF</color> <b>Steam: " + SteamStatusUa(statusName) + ".</b> Ти не в мережі для друзів, тому <b>приєднатися до друга не вийде</b> (і він тебе не побачить). Постав у Steam статус «У мережі».", f8SteamStyle);
            else
                GUILayout.Label("<color=#4cd964>\u25CF</color> Steam: " + SteamStatusUa(statusName), f8SteamStyle);
        }
        // Called from DrawContent (inside the scroll view, with the wrapped overlay styles in place).
        void DrawSimpleBody()
        {
            if (f8StateStyle == null)
            {
                f8StateStyle = new GUIStyle(GUI.skin.label);
                f8StateStyle.richText = true; f8StateStyle.wordWrap = true;
                f8StateStyle.fontStyle = FontStyle.Bold; f8StateStyle.fontSize = 14;
            }
            GUILayout.Label(F8StateLine(), f8StateStyle);
            GUILayout.Label(status);
            DrawSteamStatusLine();
            // Things that need an answer stay above the tabs so they are never missed.
            DrawDiagnostics(false);
            if (offerFrom.m_SteamID != 0 || xferActive) DrawUpdate();
            int newTab = GUILayout.Toolbar(f8Tab, F8Tabs);
            if (newTab != f8Tab) { f8Tab = newTab; overlayContentScroll = Vector2.zero; }
            if (f8Tab == 1) DrawF8Settings();
            else if (f8Tab == 2) DrawF8Diagnostics();
            else DrawF8Game();
            if (GUILayout.Button("Закрити вікно (відкрити знову: F8)")) { HideBridgeWindow(); ReleaseBridgeFocus(); }
        }

        // First game starts only: a one-line hint at the bottom tells a new player that F8 opens the mod window (it starts hidden).
        // Ends for good once F8 was opened once, or after [Interface] F8HintSessions starts, 40 s each.
        void DrawF8Hint()
        {
            if (f8HintLeft == null || f8HintLeft.Value <= 0) return;
            if (f8HintStart < 0f)
            {
                f8HintStart = Time.realtimeSinceStartup;
                try { f8HintLeft.Value = f8HintLeft.Value - 1; } catch (Exception) { }
            }
            if (Time.realtimeSinceStartup - f8HintStart > 40f) return;
            if (f8HintStyle == null)
            {
                f8HintStyle = new GUIStyle(GUI.skin.label);
                f8HintStyle.alignment = TextAnchor.MiddleCenter; f8HintStyle.fontSize = 14; f8HintStyle.wordWrap = true;
                f8HintStyle.normal.textColor = Color.white;
            }
            Rect r = new Rect(Screen.width * 0.5f - 280f, Screen.height - 64f, 560f, 40f);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.7f); GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(r, "Steam Bridge: натисни F8, щоб відкрити вікно мода (приєднання до друга, налаштування)", f8HintStyle);
        }

        void F8HintDone()
        {
            if (f8HintLeft != null && f8HintLeft.Value > 0) { try { f8HintLeft.Value = 0; } catch (Exception) { } }
        }
        void DrawF8Game()
        {
            if (!SteamManager.Initialized) { GUILayout.Label("Запусти Steam і оригінальну гру зі своєї бібліотеки."); return; }
            if (hosting) DrawF8Hosting();
            else if (bridge == null) DrawF8Idle();
            else DrawF8Client();
        }

        void DrawF8Idle()
        {
            GUILayout.Label("Звичайний спосіб: хост створює мережеву гру в самій Castle Story, а друзі бачать її в списку серверів гри. "
                + "Це вікно потрібне, лише якщо список порожній або зайти не виходить.");
            DrawReconnect();
            GUILayout.Label("Приєднатися до друга (він уже створив гру). Обери його зі списку:");
            if (GUILayout.Button("Оновити список друзів")) RefreshFriends();
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(110));
            foreach (CSteamID f in friends)
                if (GUILayout.Button((f == peer ? "✓ " : "") + SteamFriends.GetFriendPersonaName(f))) peer = f;
            GUILayout.EndScrollView();
            if (friends.Count == 0) GUILayout.Label("Друзів у Castle Story не видно. Усі мають запустити гру через Steam, потім натисни «Оновити список друзів».");
            if (GUILayout.Button("Приєднатися до вибраного друга")) { peerByCode = false; StartBridge(false); }
            GUILayout.Label("Або створи гру сам:");
            if (GUILayout.Button("Я хост: дозволити до 3 друзів Steam")) StartBridge(true);
            if (GUILayout.Button((f8CodeOpen ? "▾ " : "▸ ") + "У мене є код хоста (гравця немає серед друзів Steam)")) f8CodeOpen = !f8CodeOpen;
            if (!f8CodeOpen) return;
            GUI.SetNextControlName("CSBJoinCode");
            joinCode = GUILayout.TextField(joinCode ?? "", 20);
            if (Event.current.type == EventType.Repaint) joinCodeRect = GUILayoutUtility.GetLastRect();
            if (GUILayout.Button("Приєднатися за кодом"))
            {
                ulong typed;
                if (!ulong.TryParse((joinCode ?? "").Trim(), out typed) || !LooksLikeSteamId(typed)) status = "Код має бути числом SteamID (17 цифр, починається з 7656119…).";
                else if (typed == SteamUser.GetSteamID().m_SteamID) status = "Це твій власний код.";
                else { peer = new CSteamID(typed); peerByCode = !IsFriend(peer); StartBridge(false); }
            }
        }

        void DrawF8Hosting()
        {
            ulong myCode = SteamUser.GetSteamID().m_SteamID;
            GUILayout.Label("Друзі Steam заходять самі через список серверів гри. Гравцю, якого немає серед твоїх друзів, надішли код:");
            GUILayout.Label((AllowStrangersOn ? "Код хоста: " : "Сторонні вимкнено ([Network] AllowStrangers=false), код працює лише для друзів: ") + myCode);
            if (GUILayout.Button("Скопіювати код хоста")) { GUIUtility.systemCopyBuffer = myCode.ToString(); status = "Код скопійовано. Надішли його гравцеві."; }
            DrawHostPauseButton();
            GUILayout.Label("Гравці в грі:");
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(130));
            foreach (System.Collections.Generic.KeyValuePair<CSteamID, BridgeCore> g in guests)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(PingName(g.Key) + ": " + (g.Value.Ready ? "з'єднаний" : "очікує"));
                if (GUILayout.Button("Кік", GUILayout.Width(46))) RequestKick(g.Key, 0f);
                if (GUILayout.Button("Бан 10 хв", GUILayout.Width(78))) RequestKick(g.Key, 10f);
                if (GUILayout.Button("Бан 60 хв", GUILayout.Width(78))) RequestKick(g.Key, 60f);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (guests.Count == 0) GUILayout.Label("Поки нікого. Друг має знайти твою гру в списку серверів.");
            DrawBans();
            if (GUILayout.Button("Зупинити хост-міст (від'єднає всіх гравців)")) { autoHostSuppressed = true; StopBridge(); status = "Хост-міст зупинено."; }
        }

        void DrawF8Client()
        {
            GUILayout.Label("Ти гравець у грі друга " + SteamFriends.GetFriendPersonaName(peer) + ". Грай як зазвичай. "
                + "Якщо гра підвисає або рветься зв'язок, відкрий вкладку «Діагностика».");
            if (starvationNote != null) GUILayout.Label(starvationNote);
            if (GUILayout.Button("Вийти з гри друга (зупинити міст)"))
            {
                bool wasClient = !host; StopBridge(); if (wasClient) LeaveGameSession();
                status = "Міст зупинено. Повернись у головне меню перед новою спробою.";
            }
        }

        void DrawF8Settings()
        {
            GUILayout.Label("Налаштування мода. Зберігаються самі й діють одразу.");
            DrawBorderless();
            DrawGroupsSettings();
            if (GUILayout.Button("Предмети будівництва (вимкнути непотрібні, з картинками)...")) ItemFilter.WindowOpen = !ItemFilter.WindowOpen;
        }

        void DrawF8Diagnostics()
        {
            GUILayout.Label("Для розбору проблем. Звичайному гравцю тут нічого міняти не треба.");
            DrawPing();
            if (!(offerFrom.m_SteamID != 0 || xferActive)) DrawUpdate();
            if (hosting)
            {
                GUILayout.Label("Хост + до 3 гравців. Підключених мостів: " + guests.Count + "; режим " + (mixedSend ? SendMixed : SendReliable));
                foreach (System.Collections.Generic.KeyValuePair<CSteamID, BridgeCore> g in guests)
                {
                    GUILayout.Label(PingName(g.Key) + ": пакети " + g.Value.Sent + "/" + g.Value.Received
                        + (g.Value.Received > 0 ? "; останні дані " + ReceiveAge(g.Value).ToString("F1") + " с тому" : ""));
                    P2PSessionState_t gs;
                    if (SteamNetworking.GetP2PSessionState(g.Key, out gs)) GUILayout.Label("Черга " + gs.m_nBytesQueuedForSend + "; помилка " + gs.m_eP2PSessionError);
                }
            }
            else if (bridge != null)
            {
                GUILayout.Label("Пакети гри: відправлено " + bridge.Sent + ", отримано " + bridge.Received + "; режим " + (mixedSend ? SendMixed : SendReliable));
                P2PSessionState_t s;
                if (SteamNetworking.GetP2PSessionState(peer, out s))
                    GUILayout.Label("Steam: " + (s.m_bUsingRelay != 0 ? "relay" : "P2P") + "; черга " + s.m_nBytesQueuedForSend + " байт; помилка " + s.m_eP2PSessionError);
                if (lastData > 0) GUILayout.Label("Останні дані: " + (Time.realtimeSinceStartup - lastData).ToString("F1") + " с тому");
            }
            if (DiagnosticsEnabled && (hosting || bridge != null))
            {
                GUILayout.Label("Діагностика мережі (потрібна версія 0.3.7 в обох):");
                if (GUILayout.Button("Надіслати мою діагностику другові")) ShareDiagnostics();
                if (GUILayout.Button("Попросити діагностику в друга (він підтвердить)")) RequestDiagnostics();
                if (lastDiagFile.Length > 0) GUILayout.Label("Остання отримана: " + lastDiagFile);
            }
            DrawJoinLoad();
            DrawDiagBundle();
            DrawFixes();
            if (NetworkCompat.Status.Length > 0) GUILayout.Label(NetworkCompat.Status);
            if (ModInventory.F8Line.Length > 0) GUILayout.Label(ModInventory.F8Line);   // 0.4.14 MOD-INV (a cached string)
            DrawTestPanel();
            if (SteamManager.Initialized)
            {
                RadminState rs = CurrentRadminState();
                bool radminUp = rs != null && rs.Service == "Running";   // a new player does not need to see a stopped service
                if (hosting) { if (radminUp) DrawRadminHost(); }
                else if (bridge == null)
                {
                    if (radminUp) DrawRadminJoin();
                    if (sendMode != null && GUILayout.Button("Режим надсилання: " + SendModeName() + " (натисни, щоб змінити)"))
                        sendMode.Value = SendModeName() == SendMixed ? SendReliable : SendMixed;
                }
            }
        }
    }
}
