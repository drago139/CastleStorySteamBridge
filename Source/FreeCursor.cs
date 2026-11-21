using System;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.73 FreeCursor: in windowed mode the game confines the mouse to its window (ClipCursor), so two game windows side by side cannot be
    // worked with one mouse. With [Window] FreeCursor = true the plugin releases the confinement every frame while the game is windowed.
    // Off by default; it never touches fullscreen. Edge-of-screen camera scrolling then happens only at the window edge as before.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> freeCursor; static bool fcLogged;
        [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr rect);

        void InstallFreeCursor()
        {
            try { freeCursor = Config.Bind("Window", "FreeCursor", false, "Windowed mode: release the mouse from the game window so it can move to other windows (e.g. two game windows side by side). Off = original behaviour. Not used in fullscreen."); }
            catch (Exception) { }
        }

        void FreeCursorTick()
        {
            if (freeCursor == null || !freeCursor.Value) return;
            try
            {
                if (Screen.fullScreen) return;
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                ClipCursor(IntPtr.Zero);
                if (!fcLogged) { fcLogged = true; Logger.LogInfo("FreeCursor on: the mouse is released from the windowed game."); }
            }
            catch (Exception) { }
        }
    }
}
