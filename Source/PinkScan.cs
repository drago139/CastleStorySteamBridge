using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace CastleStorySteam
{
    // 0.3.92 PINK-SCAN (log only). A magenta blob after a catapult explosion was reported on the host. Unity draws magenta when a renderer's shader is missing,
    // failed to compile or is unsupported. Every 30 s, in the match scene only, the renderers are checked in slices of 300 per frame (one FindObjectsOfType per
    // scan) and each new offender is written once to perf-trace.txt with its scene path, position, material and shader, so the pink object can be named. It
    // changes nothing in the game. Capped at 60 lines per session.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> pinkCfg;
        Renderer[] pinkList; int pinkIdx; float pinkNextScan; int pinkLogged, pinkNoMat, pinkScans;
        readonly HashSet<int> pinkSeen = new HashSet<int>();
        const int PinkLogLimit = 60, PinkNoMatLimit = 15;

        void PinkScanTick()
        {
            try
            {
                if (pinkCfg == null) pinkCfg = Config.Bind("Diag", "PinkScan", true, "Log renderers whose shader is missing, broken or unsupported (they are drawn magenta). Looks at 300 renderers per frame, one pass every 30 s, only in a match. Changes nothing in the game.");
                if (!pinkCfg.Value || !ptOn || pinkLogged >= PinkLogLimit || !HpInGame()) { pinkList = null; return; }
                float now = Time.realtimeSinceStartup;
                if (pinkList == null)
                {
                    if (now < pinkNextScan) return;
                    pinkList = (Renderer[])UnityEngine.Object.FindObjectsOfType(typeof(Renderer)); pinkIdx = 0; pinkNextScan = now + 30f; pinkScans++;
                    if (pinkScans == 1) PtLog("PINK-SCAN on: " + pinkList.Length + " renderers in the first pass");
                    return;
                }
                int end = Math.Min(pinkIdx + 300, pinkList.Length);
                for (; pinkIdx < end; pinkIdx++) PinkCheck(pinkList[pinkIdx]);
                if (pinkIdx >= pinkList.Length) pinkList = null;
            }
            catch (Exception) { pinkList = null; }
        }

        void PinkCheck(Renderer r)
        {
            if (r == null || !r.enabled) return;
            GameObject go = r.gameObject;
            if (go == null || !go.activeInHierarchy) return;
            Material m = r.sharedMaterial; string why = null; string shaderName = "-";
            if (m == null) why = "no-material";
            else
            {
                Shader s = m.shader;
                if (s == null) why = "no-shader";
                else
                {
                    shaderName = s.name;
                    if (shaderName == "Hidden/InternalErrorShader") why = "error-shader";
                    else if (!s.isSupported) why = "unsupported-shader";
                }
            }
            if (why == null) return;
            if (!pinkSeen.Add(r.GetInstanceID())) return;
            if (why == "no-material" && ++pinkNoMat > PinkNoMatLimit) return;
            pinkLogged++;
            StringBuilder path = new StringBuilder(); Transform t = go.transform; int depth = 0;
            while (t != null && depth < 5) { if (path.Length > 0) path.Insert(0, "/"); path.Insert(0, t.name); t = t.parent; depth++; }
            Vector3 p = go.transform.position;
            PtLog("PINK-SCAN " + why + " obj=" + path + " renderer=" + r.GetType().Name + " pos=(" + p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1") + ") material=" + (m != null ? m.name : "-") + " shader=" + shaderName + " layer=" + go.layer);
        }
    }
}
