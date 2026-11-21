using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStorySteam
{
    // 0.3.83 host pause for online games. The game has its own pause (ClockConfig.Paused: time scale 0, orders blocked, pause icon) but refuses it
    // in multiplayer unless GeneralRules.AllowPauseInMultiplayer. Only the HOST may pause: the host pauses its own game (the authoritative world
    // stops) and tells every 0.3.83+ guest over the bridge (extra packet kind 15, repeated every 2 s while paused), and each guest applies the same
    // native pause locally. The permission is lifted only for the moment of the switch, so Esc/menus still cannot pause a multiplayer game.
    // Safety (0.3.84): a paused guest stays paused while the host is silent (no independent simulation) and continues only on the host's signal or when
    // the session ends (bridge closed, client stopped, game left); the host repeats its state every 2 s; the host unpauses on leaving.
    // Older guests ignore the packet; for them the host world simply stands still until the host continues.
    public sealed partial class SteamBridgePlugin
    {
        const byte KindPause = BridgeCore.KindPause;
        static ConfigEntry<bool> hpCfg; static ConfigEntry<KeyCode> hpKey;
        static bool hpPaused, hpByHost; static float hpLastRecv, hpNextSend; static uint hpSeq;
        static PropertyInfo hpAllowProp;

        void InstallHostPause()
        {
            try
            {
                hpCfg = Config.Bind("Game", "HostPause", true, "Online: the host can pause the game for everybody (Pause key or F8). Guests cannot pause.");
                hpKey = Config.Bind("Game", "HostPauseKey", KeyCode.Pause, "Key the host presses to pause / continue the online game.");
                hpLiftCfg = Config.Bind("Game", "HostPauseEndsWithBridge", false, "Host: when the host bridge stops while the host pause is on and the match goes on (F8 'stop host bridge', or a bridge error), continue the game at once. false = as before: the game stays paused until you start hosting again in F8 and then press the pause key.");
                hpAllowProp = typeof(Brix.Game.Rules.GeneralRules).GetProperty("AllowPauseInMultiplayer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                hpNativeCfg = Config.Bind("Game", "HostPauseNativeKey", true, "Online: the game's own pause key works like in single player (host: pause for everybody; guest: refused) and shows the game's pause screen.");
                hpToggleMode = typeof(Brix.Input.InputModeController).GetMethod("ToggleMode", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo native = typeof(Brix.Input.InputModeController).GetMethod("TogglePause", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (native != null && hpToggleMode != null) discoveryHarmony.Patch(native, new HarmonyMethod(typeof(SteamBridgePlugin).GetMethod("HpBeforeNativeToggle", BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo("HOST-PAUSE ready (" + (hpCfg.Value ? "key " + hpKey.Value : "off") + ", native pause " + (hpAllowProp != null ? "found" : "MISSING") + ", native key " + (native != null && hpToggleMode != null ? (hpNativeCfg.Value ? "on" : "off") : "MISSING") + ")");
            }
            catch (Exception e) { Logger.LogWarning("HOST-PAUSE unavailable: " + e.Message); }
        }

        static int hpInGameFrame = -1; static bool hpInGameValue;
        static bool HpInGame()
        {
            try { int f = Time.frameCount; if (f == hpInGameFrame) return hpInGameValue; hpInGameValue = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "ScenePrincipale"; hpInGameFrame = f; return hpInGameValue; } catch (Exception) { return false; }
        }

        // Switch the game's own pause, lifting the multiplayer restriction only for this call.
        static bool HpApply(bool on, string why)
        {
            try
            {
                if (hpAllowProp == null) return false;
                MethodInfo set = hpAllowProp.GetSetMethod(true); bool prev = (bool)hpAllowProp.GetValue(null, null);
                set.Invoke(null, new object[] { true });
                try { ClockConfig.Paused = on; HpShowPauseMode(on); } finally { set.Invoke(null, new object[] { prev }); }
                if (!on) { try { if (Time.timeScale == 0f) Time.timeScale = 1f; } catch (Exception) { } }   // never leave a frozen game behind
                hpPaused = on; if (!on) hpByHost = false;
                PtLog("HOST-PAUSE " + (on ? "ON" : "OFF") + " (" + why + ") timeScale=" + Time.timeScale);
                return true;
            }
            catch (Exception e) { PtLog("HOST-PAUSE failed: " + e.Message); return false; }
        }

        // 0.3.86: the game's own pause key (InputModeController.TogglePause) does nothing in multiplayer. Online it now toggles the host pause on the
        // host and is refused on a guest; single player is untouched. The game's pause screen (input mode "pause": pause icon + text, orders
        // blocked) is shown on every PC while the host pause is on.
        static ConfigEntry<bool> hpNativeCfg; static MethodInfo hpToggleMode; static bool hpModeShown; static float hpNextModeFix;

        static bool HpBeforeNativeToggle()
        {
            try
            {
                if (hpCfg == null || !hpCfg.Value || hpNativeCfg == null || !hpNativeCfg.Value || !Brix.Engine.Neo.IsMultiplayer || discoveryInstance == null) return true;
                if (discoveryInstance.hosting) discoveryInstance.HpToggle();
                else discoveryInstance.status = "Паузу ставить лише хост.";
                return false;
            }
            catch (Exception) { return true; }
        }

        // enters / leaves the game's pause input mode (called with the multiplayer pause permission lifted, so its Enter/ExitPause agree)
        static void HpShowPauseMode(bool on)
        {
            try
            {
                if (hpToggleMode == null || hpNativeCfg == null || !hpNativeCfg.Value) return;
                bool shown = Brix.Input.InputModeController.IsPaused();
                if (on && !shown && !Brix.Input.InputModeController.IsInMenu()) hpToggleMode.Invoke(null, new object[] { Brix.Input.InputMode.pause });
                else if (!on && shown) hpToggleMode.Invoke(null, new object[] { Brix.Input.InputMode.pause });
                hpModeShown = on && Brix.Input.InputModeController.IsPaused();
            }
            catch (Exception e) { PtLog("HOST-PAUSE pause screen failed: " + e.Message); }
        }

        // pause screen left while the host pause is on (Esc etc.): the host continues the game for everybody, like Esc in single player; a guest
        // gets the pause screen back (its world stays paused anyway: the game refuses to unpause in multiplayer)
        void HpWatchPauseMode(float now)
        {
            if (!hpPaused || hpNativeCfg == null || !hpNativeCfg.Value || hpToggleMode == null || now < hpNextModeFix) return;
            if (Brix.Input.InputModeController.IsPaused() || Brix.Input.InputModeController.IsInMenu()) { hpModeShown = Brix.Input.InputModeController.IsPaused() || hpModeShown; return; }
            hpNextModeFix = now + 1f;
            if (hosting) { if (hpModeShown) { PtLog("HOST-PAUSE pause screen closed by the host -> continue"); HpToggle(); } }
            else if (hpByHost) { HpShowPauseModeAllowed(); }
        }

        static void HpShowPauseModeAllowed()
        {
            try
            {
                if (hpAllowProp == null) return; MethodInfo set = hpAllowProp.GetSetMethod(true); bool prev = (bool)hpAllowProp.GetValue(null, null);
                set.Invoke(null, new object[] { true });
                try { HpShowPauseMode(true); } finally { set.Invoke(null, new object[] { prev }); }
            }
            catch (Exception) { }
        }

        void HpBroadcast(bool on)
        {
            // 0.3.86 payload: [0]=on [1..4]=seq [5..8]=GameClock.Age (kept for 0.3.84/0.3.85 guests) [9..12]=match time on the host's screen (-1 = none)
            // [13..16]=pause sync generation [17..20]=host realtime clock [21..24]=host session id (seq restarts with a new session)
            hpSeq++; byte[] p = new byte[25]; PrWriteAge(p); p[0] = (byte)(on ? 1 : 0); p[1] = (byte)hpSeq; p[2] = (byte)(hpSeq >> 8); p[3] = (byte)(hpSeq >> 16); p[4] = (byte)(hpSeq >> 24);
            Buffer.BlockCopy(BitConverter.GetBytes(TsHostShown()), 0, p, 9, 4); Buffer.BlockCopy(BitConverter.GetBytes(psGen), 0, p, 13, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(Time.realtimeSinceStartup), 0, p, 17, 4); Buffer.BlockCopy(BitConverter.GetBytes(HpSession()), 0, p, 21, 4);
            byte[] pk = BridgeCore.Packet(KindPause, p, p.Length);
            foreach (var g in guests) { try { if (g.Value.Ready) SendTo(g.Key, pk); } catch (Exception) { } }
        }

        // Host: toggle (key or F8 button)
        void HpToggle()
        {
            if (hpCfg == null || !hpCfg.Value) return;
            if (!hosting || !NetworkServer.active || !HpInGame()) { status = "Паузу ставить лише хост під час гри."; return; }
            bool on = !hpPaused;
            // 0.3.86: continue waits (at most 5 s) until every 0.3.86+ guest confirmed the state check of this pause
            if (!on && PsHoldContinue()) return;
            HpSet(on);
        }

        void HpSet(bool on)
        {
            if (HpApply(on, "host " + (on ? "paused" : "continued")))
            {
                HpBroadcast(on); if (!on) { hpNextSend = Time.realtimeSinceStartup + 0.5f; hpOffResends = 3; }
                status = on ? "Пауза для всіх гравців. Продовжити: P, " + hpKey.Value + " або F8." : "Гру продовжено.";
            }
        }
        static int hpOffResends; static bool hpWanted, hpSilent, hpFormerNoted; static float hpNextSilentLog; static ConfigEntry<bool> hpLiftCfg;
        static uint hpRecvSession, hpRecvSeq, hpSessionId; static int hpStaleDropped;
        static uint HpSession() { if (hpSessionId == 0) hpSessionId = (uint)new System.Random().Next(1, int.MaxValue); return hpSessionId; }

        void HostPauseTick()
        {
            if (hpCfg == null || !hpCfg.Value) return;
            float now = Time.realtimeSinceStartup;
            try
            {
                if (hosting)
                {
                    hpFormerNoted = false;
                    if (Input.GetKeyDown(hpKey.Value)) HpToggle();
                    if (hpPaused && (!NetworkServer.active || !HpInGame())) { HpApply(false, "host left the game"); HpBroadcast(false); }
                    if (now >= hpNextSend && NetworkServer.active && HpInGame())
                    {
                        // 0.3.84: the current state goes out every 2 s whether paused or not, so a guest that missed a switch converges
                        if (hpOffResends > 0) { hpOffResends--; hpNextSend = now + 0.5f; HpBroadcast(false); }
                        else { hpNextSend = now + 2f; HpBroadcast(hpPaused); }
                    }
                }
                else
                {
                    // 0.3.84: a paused guest NEVER continues on its own while the session lives (no independent simulation); it continues only when
                    // the host says so, or when the session is really over (bridge closed, client stopped, game left).
                    if (hpPaused && hpByHost && (bridge == null || !NetworkClient.active || !HpInGame())) { HpApply(false, "guest: session ended"); hpWanted = false; }
                    else if (hpWanted && !hpPaused && bridge != null && NetworkClient.active && HpInGame()) { if (HpApply(true, "guest: host pause applied after loading")) hpByHost = true; }
                    // 0.4.27 review: a host whose bridge stopped while its pause was on (F8 "stop host bridge", a bridge error) is no longer "hosting": the branch above never
                    // continued it, the Pause key / F8 button / native key all refuse a non-host, and the game itself refuses to unpause in multiplayer -> frozen until re-hosting.
                    // 0.4.27 verify (A-06-v): continuing a paused game by itself is visible to the player (the host may be away from the PC, and a bridge error can stop the
                    // bridge too), so it happens only with [Game] HostPauseEndsWithBridge = true. Default false = as before: the game stays paused; 'Я хост' in F8 and then
                    // the pause key continue it (the banner says so now). A former host that has LEFT the match is always cleared, like a host above ("host left the game").
                    else if (hpPaused && !hpByHost)
                    {
                        bool left = !NetworkServer.active || !HpInGame();
                        if (left || (hpLiftCfg != null && hpLiftCfg.Value))
                        {
                            bool ok = HpApply(false, left ? "former host left the game" : "host bridge stopped while paused"); hpPaused = false; hpFormerNoted = false;
                            Logger.LogWarning("HOST-PAUSE: " + (left ? "the former host left the game with the pause on; the pause is cleared" : "the host bridge stopped while the pause was on; the game continues") + (ok ? "" : " (the game's pause switch failed)"));
                        }
                        else if (!hpFormerNoted) { hpFormerNoted = true; Logger.LogWarning("HOST-PAUSE: the host bridge stopped while the pause was on; the game stays paused. To continue: F8, 'Я хост', then the pause key ([Game] HostPauseEndsWithBridge = true continues at once)"); }
                    }
                    // 0.4.27 review: a "paused" order received while loading must not outlive its session (it paused the NEXT match after loading; a host with HostPause off never lifts it)
                    if (bridge == null && hpWanted) hpWanted = false;
                    hpSilent = hpPaused && hpByHost && now - hpLastRecv > 6f;
                    if (hpSilent && now >= hpNextSilentLog) { hpNextSilentLog = now + 10f; PtLog("HOST-PAUSE guest still paused, no signal from the host for " + (now - hpLastRecv).ToString("F0") + " s"); }
                }
                HpWatchPauseMode(now);
            }
            catch (Exception) { }
        }

        void OnPausePacket(CSteamID from, byte[] payload)
        {
            try
            {
                if (hosting || payload == null || payload.Length < 1 || from != peer || hpCfg == null || !hpCfg.Value) return;
                if (payload.Length >= 25)
                {
                    // 0.3.86: a packet older than one already applied (same host session) is dropped, so a late "paused"/"continued" never undoes a newer one
                    uint seq = BitConverter.ToUInt32(payload, 1), session = BitConverter.ToUInt32(payload, 21);
                    if (session == hpRecvSession && seq <= hpRecvSeq) { hpStaleDropped++; PtLog("HOST-PAUSE stale state packet dropped (seq " + seq + " <= " + hpRecvSeq + ", dropped " + hpStaleDropped + ")"); return; }
                    hpRecvSession = session; hpRecvSeq = seq;
                    TsOnHostTime(BitConverter.ToSingle(payload, 9), BitConverter.ToSingle(payload, 17), session, payload[0] == 1);
                }
                bool on = payload[0] == 1; hpLastRecv = Time.realtimeSinceStartup; hpWanted = on; if (on) PrApplyHostAge(payload);
                if (on && !hpPaused) { if (HpInGame() && NetworkClient.active) { if (HpApply(true, "host paused")) hpByHost = true; } }
                else if (!on && hpPaused && hpByHost) HpApply(false, "host continued");
            }
            catch (Exception) { }
        }

        void DrawHostPauseButton()
        {
            if (hpCfg == null || !hpCfg.Value || !hosting || !NetworkServer.active || !HpInGame()) return;
            if (GUILayout.Button(hpPaused ? "▶ Продовжити гру для всіх (" + hpKey.Value + ")" : "⏸ Пауза для всіх гравців (" + hpKey.Value + ")")) HpToggle();
        }

        void DrawPauseBanner()
        {
            if (!hpPaused) return;
            string key = hpKey != null ? hpKey.Value.ToString() : "Pause";
            // 0.4.27 verify (A-06-v): for a former host (bridge stopped while paused) "P, Pause або F8" was untrue: all three refuse a non-host until it hosts again
            bool former = !hpByHost && !hosting;
            string text = hpByHost ? (hpSilent ? "ПАУЗА — немає зв'язку з хостом, чекаю" : "ПАУЗА — поставив хост (продовжити може лише хост)") : !former ? "ПАУЗА для всіх — продовжити: P, " + key + " або F8" : "ПАУЗА — хост-міст зупинено. Продовжити: F8, «Я хост», потім P або " + key;
            float w = former ? Mathf.Max(400f, GUI.skin.box.CalcSize(new GUIContent(text)).x + 16f) : 400f;   // the former-host text is longer than the 400 px box of the other three
            GUI.Box(new Rect(Screen.width / 2f - w / 2f, 60f, w, 30f), text);
        }
    }
}
