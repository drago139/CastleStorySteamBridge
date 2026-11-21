using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Brix.Game.Components.CatapultSpace;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.76 log-only diagnostics.
    //  SYSMEM   every 15 s: system free physical RAM, commit charge (used/limit); LOWMEM line (once per 60 s) when free RAM < 1500 MB, so a later
    //           "game vanished" can be matched with memory pressure. EXIT line from AppDomain.ProcessExit / Application.quitting (a hard kill by the
    //           OS or Task Manager cannot be logged: then the LAST SYSMEM/LOWMEM line before the log stops is the evidence).
    //  CAT-STATE  for every catapult in the world, one line whenever its aim state changes: role, target, afficherCible/Trajets/GUI/Apercu flags.
    //           A catapult whose target is set (cible != 0) but whose marker is not shown (afficherCible false) is flagged MARKER-MISSING.
    public sealed partial class SteamBridgePlugin
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        sealed class MemStatusEx { public uint dwLength = (uint)Marshal.SizeOf(typeof(MemStatusEx)); public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern bool GlobalMemoryStatusEx([In, Out] MemStatusEx buffer);

        static float sdNextMem, sdNextLow, sdNextCat, sdNextScan; static bool sdHooked;
        static CatapultControl[] sdCats = new CatapultControl[0];
        static readonly Dictionary<int, string> sdCatState = new Dictionary<int, string>();

        static void SysDiagInstall()
        {
            if (sdHooked) return; sdHooked = true;
            try { AppDomain.CurrentDomain.ProcessExit += delegate { PtLog("EXIT process exit event (clean shutdown)"); PtFlushNow(); }; } catch (Exception) { }
        }

        static void PtFlushNow() { try { PtFlush(); System.Threading.Thread.Sleep(150); } catch (Exception) { } }

        void SysDiagTick()
        {
            float t = Time.realtimeSinceStartup;
            try
            {
                if (!sdHooked) SysDiagInstall();
                // 0.4.27 review: everything below ends in PtLog only. With PerfTrace off (lean build) and no armed bot it was pure cost: a scene-wide FindObjectsOfType<CatapultControl>() every 20 s
                // (12-30 ms per scan of that kind in a real match: CENSUS lines 2026-11-08) plus a state string per catapult every second, all thrown away.
                if (!ptOn && !botTapOn) return;
                if (t >= sdNextMem)
                {
                    sdNextMem = t + 15f;
                    MemStatusEx m = new MemStatusEx();
                    if (GlobalMemoryStatusEx(m))
                    {
                        long freeMb = (long)(m.ullAvailPhys / 1048576UL), totMb = (long)(m.ullTotalPhys / 1048576UL), commitUsed = (long)((m.ullTotalPageFile - m.ullAvailPageFile) / 1048576UL), commitLim = (long)(m.ullTotalPageFile / 1048576UL);
                        PtLog("SYSMEM free " + freeMb + " / " + totMb + " MB, load " + m.dwMemoryLoad + "%, commit " + commitUsed + " / " + commitLim + " MB");
                        if (freeMb < 1500 && t >= sdNextLow) { sdNextLow = t + 60f; PtLog("LOWMEM free RAM " + freeMb + " MB < 1500 MB: the OS may close programs (this game = " + (GC.GetTotalMemory(false) / 1048576) + " MB managed)"); PtFlush(); }
                    }
                }
                if (t >= sdNextScan) { sdNextScan = t + 20f; sdCats = UnityEngine.Object.FindObjectsOfType<CatapultControl>(); }
                if (t >= sdNextCat && sdCats.Length > 0)
                {
                    sdNextCat = t + 1f;
                    for (int i = 0; i < sdCats.Length; i++)
                    {
                        CatapultControl c = sdCats[i]; if (c == null) continue;
                        CatapultBody b = c.body; if (b == null) continue;
                        bool hasTarget = c.cible != Vector3.zero;
                        string st = "target=" + (hasTarget ? c.cible.x.ToString("F0") + "," + c.cible.y.ToString("F0") + "," + c.cible.z.ToString("F0") : "none") + " cible=" + b.afficherCible + " trajets=" + b.afficherTrajets + " gui=" + b.afficherGUI + " apercu=" + b.afficherApercu + " fire=" + c.feu + " animee=" + c.animee + " rot=" + b.rotation.ToString("F2") + "/" + b.rotationVoulue.ToString("F2") + " crinque=" + b.crinque.ToString("F1") + "/" + b.crinqueVoulue.ToString("F1") + " force=" + b.forceVoulue.ToString("F1") + CsDrift(c);
                        string old; int id = c.GetInstanceID();
                        if (sdCatState.TryGetValue(id, out old) && old == st) continue;
                        sdCatState[id] = st;
                        string role = UnityEngine.Networking.NetworkServer.active ? "host" : (UnityEngine.Networking.NetworkClient.active ? "guest" : "single");
                        PtLog("CAT-STATE " + c.name + " role=" + role + " " + st + (hasTarget && !b.afficherCible && b.afficherGUI ? " MARKER-MISSING(selected, target set, marker off)" : ""));
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
