using System;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Game.AI;
using HarmonyLib;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.102 GameAnalytics probe moved to GaGuard.cs (GaInstall / GaPre / GaPost, [Fixes] GameAnalyticsMode + DisableGameAnalyticsSubmit). This file keeps the shared Locomotion4 scan and the Boehm readers.
    public sealed partial class SteamBridgePlugin
    {
        // ---- shared Locomotion4 scan: FlailWatch (diagnostic), FloatLog (diagnostic) and BuriedRescue (host fix) each ran FindObjectsOfType<Locomotion4>() on their own timer (2 s / 3 s / 2 s);
        // a scene-wide scan costs 100+ ms with ~120k MonoBehaviours (side-session A2: a 2.04 s comb of 150-300 ms hitches appeared with 0.3.97). Users of the list already skip destroyed units (l == null).
        static Locomotion4[] loco4Cache; static float loco4At = -100f;
        static Locomotion4[] Loco4All(float maxAge)
        {
            float t = Time.realtimeSinceStartup;
            if (loco4Cache == null || t - loco4At > maxAge || t < loco4At) { loco4Cache = UnityEngine.Object.FindObjectsOfType<Locomotion4>(); loco4At = t; }
            return loco4Cache;
        }

        // ---- Boehm globals of the game's own mono.dll, READ ONLY, only when the file is exactly the build analysed in the offline lab (sha256 below; side-session A7: GC_free_space_divisor at RVA 0x262950
        // (8 bytes, default 3, headroom between rule-triggered collections = live / (divisor - 1)), GC_dont_gc at RVA 0x266bf4 (4 bytes)). Logged at start and in every GCPROBE line to show whether the game changes them.
        const string GpMonoSha256 = "5661FA36BDC08AD91562F609536A3235BDBA113901920F1700C51FB8245295EA";
        static long gpDivAddr, gpDontAddr;
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)] static extern uint GetModuleFileNameA(IntPtr module, System.Text.StringBuilder name, uint size);

        static void GpBoehmInit(IntPtr mono, ref string note)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(520);
                if (GetModuleFileNameA(mono, sb, 520) == 0) { note += "; boehm globals: mono.dll path unknown"; return; }
                byte[] hash;
                using (System.IO.FileStream fs = System.IO.File.OpenRead(sb.ToString())) hash = System.Security.Cryptography.SHA256.Create().ComputeHash(fs);
                string hex = BitConverter.ToString(hash).Replace("-", "");
                if (hex != GpMonoSha256) { note += "; boehm globals not read: mono.dll sha256 " + hex.Substring(0, 8) + " is not the analysed build"; return; }
                gpDivAddr = mono.ToInt64() + 0x262950L; gpDontAddr = mono.ToInt64() + 0x266bf4L; foOk = true; foBase = mono;
                note += "; boehm free_space_divisor=" + System.Runtime.InteropServices.Marshal.ReadInt64(new IntPtr(gpDivAddr)) + " dont_gc=" + System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(gpDontAddr)) + " (mono.dll sha256 ok)";
            }
            catch (Exception e) { gpDivAddr = 0; gpDontAddr = 0; note += "; boehm globals failed: " + e.GetType().Name; }
        }

        static string GpBoehmText()
        {
            if (gpDivAddr == 0) return "";
            try { return "; boehm divisor " + System.Runtime.InteropServices.Marshal.ReadInt64(new IntPtr(gpDivAddr)) + " dontGc " + System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(gpDontAddr)); }
            catch (Exception) { gpDivAddr = 0; return ""; }
        }
    }
}
