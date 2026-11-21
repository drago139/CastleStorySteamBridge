using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.62: borderless window from the mod itself (the launcher check box exists only in one patched launcher). Removes the title bar and frame of the game
    // window and stretches it over the monitor it is on; a second press restores the previous style and rectangle. Config: [Window] Borderless (applied at start,
    // default off). F8 -> "Додатково" has the button. Windows only; does nothing in exclusive full screen (the game is then already without a frame).
    public sealed partial class SteamBridgePlugin
    {
        const int GWL_STYLE = -16; const uint WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
        const uint SWP_FRAMECHANGED = 0x0020, SWP_NOZORDER = 0x0004, SWP_SHOWWINDOW = 0x0040; const uint MONITOR_DEFAULTTONEAREST = 2;
        [StructLayout(LayoutKind.Sequential)] struct WinRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int cbSize; public WinRect Monitor, Work; public uint Flags; }
        [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowRect(IntPtr hWnd, out WinRect rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        delegate bool EnumWindowCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);

        ConfigEntry<bool> borderlessEntry; bool borderlessOn, borderlessApplied; int borderlessSavedStyle; WinRect borderlessSavedRect; float borderlessAt; string borderlessStatus = "";
        IntPtr borderlessSavedWindow; int borderlessFsTries;

        void InstallBorderless()
        {
            try { borderlessEntry = Config.Bind("Window", "Borderless", false, "Borderless window: no title bar or frame, stretched over the monitor (applied a few seconds after the game starts)."); borderlessAt = borderlessEntry.Value ? Time.realtimeSinceStartup + 6f : 0f; }
            catch (Exception) { }
        }

        void BorderlessTick()
        {
            if (borderlessAt > 0f && Time.realtimeSinceStartup >= borderlessAt) { borderlessAt = 0f; if (!borderlessOn) SetBorderless(true); }
        }

        IntPtr GameWindow()
        {
            // Unity's Mono 2.0 can return zero for Process.MainWindowHandle.
            // Select only the visible Unity window belonging to this process.
            try
            {
                uint ownPid = (uint)Process.GetCurrentProcess().Id; IntPtr found = IntPtr.Zero;
                EnumWindows(delegate(IntPtr window, IntPtr data)
                {
                    uint pid; GetWindowThreadProcessId(window, out pid);
                    if (pid != ownPid || !IsWindowVisible(window)) return true;
                    System.Text.StringBuilder name = new System.Text.StringBuilder(64); GetClassName(window, name, name.Capacity);
                    if (name.ToString() != "UnityWndClass") return true;
                    found = window; return false;
                }, IntPtr.Zero);
                return found;
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        static int ReadWindowStyle(IntPtr window)
        {
            SetLastError(0); int style = GetWindowLong(window, GWL_STYLE); int error = Marshal.GetLastWin32Error();
            if (style == 0 && error != 0) throw new System.ComponentModel.Win32Exception(error, "GetWindowLong failed");
            return style;
        }

        static void WriteWindowStyle(IntPtr window, int style)
        {
            SetLastError(0); int previous = SetWindowLong(window, GWL_STYLE, style); int error = Marshal.GetLastWin32Error();
            if (previous == 0 && error != 0) throw new System.ComponentModel.Win32Exception(error, "SetWindowLong failed");
        }

        static void PositionWindow(IntPtr window, WinRect rect)
        {
            if (!SetWindowPos(window, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SWP_FRAMECHANGED | SWP_NOZORDER | SWP_SHOWWINDOW))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed");
        }

        bool SetBorderless(bool on)
        {
            IntPtr h = IntPtr.Zero; int previousStyle = 0; WinRect previousRect = new WinRect(); bool changedStyle = false;
            try
            {
                h = GameWindow(); if (h == IntPtr.Zero) throw new InvalidOperationException("Game window not found.");
                if (borderlessApplied && h != borderlessSavedWindow) throw new InvalidOperationException("Game window changed; saved window state cannot be applied to it.");
                if (on && borderlessOn) return true;
                if (!on && !borderlessApplied) return true;
                previousStyle = ReadWindowStyle(h);
                if (!GetWindowRect(h, out previousRect)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed");
                if (previousRect.Right <= previousRect.Left || previousRect.Bottom <= previousRect.Top) throw new InvalidOperationException("Invalid game window rectangle.");
                if (on)
                {
                    if (Screen.fullScreen)
                    {
                        // 0.3.66: exclusive full screen minimises the game when another window (Discord, a stream) takes focus. Leave it first, then stretch the window over the monitor.
                        if (borderlessFsTries >= 6) { borderlessStatus = "Не вдалося вийти з ексклюзивного повного екрана."; Logger.LogWarning("Borderless: could not leave exclusive full screen"); borderlessFsTries = 0; return false; }
                        borderlessFsTries++;
                        Screen.fullScreen = false;
                        borderlessAt = Time.realtimeSinceStartup + 1.5f;
                        borderlessStatus = "Вихід з ексклюзивного повного екрана…";
                        Logger.LogInfo("Borderless: leaving exclusive full screen (try " + borderlessFsTries + ")");
                        return false;
                    }
                    borderlessFsTries = 0;
                    MonitorInfo mi = new MonitorInfo(); mi.cbSize = Marshal.SizeOf(typeof(MonitorInfo));
                    IntPtr monitor = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
                    if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetMonitorInfo failed");
                    if (!borderlessApplied) { borderlessSavedStyle = previousStyle; borderlessSavedRect = previousRect; borderlessSavedWindow = h; }
                    int style = (int)(((uint)borderlessSavedStyle & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_POPUP | WS_VISIBLE);
                    WriteWindowStyle(h, style); changedStyle = true;
                    PositionWindow(h, mi.Monitor);
                    borderlessApplied = true;
                    borderlessOn = true; borderlessStatus = "Вікно без рамок увімкнено (" + (mi.Monitor.Right - mi.Monitor.Left) + "x" + (mi.Monitor.Bottom - mi.Monitor.Top) + ").";
                    Logger.LogInfo("Borderless window on: " + (mi.Monitor.Right - mi.Monitor.Left) + "x" + (mi.Monitor.Bottom - mi.Monitor.Top));
                }
                else if (borderlessApplied)
                {
                    WriteWindowStyle(h, borderlessSavedStyle); changedStyle = true;
                    PositionWindow(h, borderlessSavedRect);
                    borderlessOn = false; borderlessApplied = false; borderlessStatus = "Рамку вікна повернуто.";
                    borderlessSavedWindow = IntPtr.Zero;
                    Logger.LogInfo("Borderless window off");
                }
                return true;
            }
            catch (Exception e)
            {
                string rollback = "";
                if (changedStyle)
                {
                    try { WriteWindowStyle(h, previousStyle); PositionWindow(h, previousRect); }
                    catch (Exception restore) { borderlessApplied = true; rollback = "; rollback failed: " + restore.Message; }
                }
                borderlessStatus = "Не вдалося: " + e.Message + rollback; Logger.LogWarning("Borderless window failed: " + e.Message + rollback);
                return false;
            }
        }

        void DrawBorderless()
        {
            if (GUILayout.Button(borderlessApplied ? "Вікно без рамок: повернути рамку" : "Вікно без рамок (прибрати рамку й розтягнути на монітор)"))
            {
                borderlessAt = 0f;
                if (SetBorderless(!borderlessApplied) && borderlessEntry != null) borderlessEntry.Value = borderlessOn;
            }
            if (!string.IsNullOrEmpty(borderlessStatus)) GUILayout.Label(borderlessStatus);
        }
    }
}
