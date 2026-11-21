using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace CastleStorySteam
{
    // 0.3.97 MEMSTATS (log only). The working set grows by GBs during large excavations while the managed heap and Unity's own counters stay small, so something outside them holds the
    // memory. Every Diag/MemStatsSeconds (default 90, 0 = off) in a match: how many Mesh / Texture / RenderTexture / Material / AudioClip objects exist and what Unity says they use
    // (Profiler.GetRuntimeMemorySize), the five biggest mesh groups by name (digits stripped), the mono heap and the cost of this very scan. Cheap enough once per 90 s; changes nothing.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<int> memStatsCfg; float memStatsNext; static long msVoxTotal, msVoxLast; static bool msPatched;
        // counts every voxel change the game processes (Architecte.Changement): digging, building, explosions. Compared with the growth of the working set it shows how much memory terrain destruction costs.
        static void MsVoxelHook() { msVoxTotal++; }

        // 0.3.98: texture memory is the biggest native block (~3 GB estimated). QualitySettings.masterTextureLimit N halves every texture N times per side (1 = a quarter of the memory, 2 = a sixteenth).
        // Local setting, changes nothing for other players. 0 (default) leaves the game's own value alone. Re-applied every 5 s because the game's quality screen may reset it.
        static ConfigEntry<int> texLimitCfg; float texLimitNext; static bool texLimitApplied;
        void TextureLimitTick()
        {
            try
            {
                if (texLimitCfg == null) texLimitCfg = Config.Bind("Graphics", "TextureSizeReduction", 0, "Reduce every texture by this many steps (0 = off, 1 = half width and height = a quarter of the texture memory, 2 = a sixteenth). Saves RAM/VRAM, pictures get blurrier. Only on this PC.");
                if (texLimitCfg.Value <= 0 && !texLimitApplied) return;
                float now = Time.realtimeSinceStartup; if (now < texLimitNext) return; texLimitNext = now + 5f;
                int want = Mathf.Clamp(texLimitCfg.Value, 0, 3); texLimitApplied = want > 0;
                if (QualitySettings.masterTextureLimit != want) { QualitySettings.masterTextureLimit = want; if (ptOn) PtLog("TEXTURE-LIMIT masterTextureLimit set to " + want); }
            }
            catch (Exception) { }
        }

        void MemStatsTick()
        {
            try
            {
                if (memStatsCfg == null) memStatsCfg = Config.Bind("Diag", "MemStatsSeconds", 90, "Every N seconds in a match write MEMSTATS to perf-trace.txt: counts and sizes of meshes, textures, render textures, materials and audio clips, biggest mesh groups, mono heap. 0 = off. Log only.");
                if (!ptOn || memStatsCfg.Value <= 0 || !HpInGame()) return;
                if (!msPatched)
                {
                    msPatched = true;
                    try { new Harmony("local.castlestory.steambridge.memstats").Patch(AccessTools.Method(typeof(Brix.Engine.Architecte), "Changement", new Type[] { typeof(Brix.Engine.XYZ) }), null, new HarmonyMethod(AccessTools.Method(typeof(SteamBridgePlugin), "MsVoxelHook"))); }
                    catch (Exception pe) { PtLog("MEMSTATS voxel counter unavailable: " + pe.Message); }
                }
                float now = Time.realtimeSinceStartup;
                if (now < memStatsNext) return;
                memStatsNext = now + memStatsCfg.Value;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                StringBuilder sb = new StringBuilder("MEMSTATS ");
                Mesh[] meshes = Resources.FindObjectsOfTypeAll<Mesh>(); long mb = 0, verts = 0, readable = 0;
                Dictionary<string, long> bytesByName = new Dictionary<string, long>(), countByName = new Dictionary<string, long>();
                for (int i = 0; i < meshes.Length; i++)
                {
                    Mesh m = meshes[i]; if (m == null) continue;
                    long b = Profiler.GetRuntimeMemorySize(m); mb += b; verts += m.vertexCount; if (m.isReadable) readable++;
                    string n = m.name ?? ""; StringBuilder k = new StringBuilder(24);
                    for (int c = 0; c < n.Length && k.Length < 24; c++) if (!char.IsDigit(n[c])) k.Append(n[c]);
                    string key = k.ToString(); long cur;
                    bytesByName.TryGetValue(key, out cur); bytesByName[key] = cur + b; countByName.TryGetValue(key, out cur); countByName[key] = cur + 1;
                }
                sb.Append("meshes ").Append(meshes.Length).Append(" (").Append(mb / 1048576).Append(" MB, ").Append(verts / 1000).Append("k verts, readable ").Append(readable).Append(") top:");
                List<KeyValuePair<string, long>> top = new List<KeyValuePair<string, long>>(bytesByName);
                top.Sort(delegate(KeyValuePair<string, long> x, KeyValuePair<string, long> y) { return y.Value.CompareTo(x.Value); });
                for (int i = 0; i < top.Count && i < 5; i++) sb.Append(" [").Append(top[i].Key).Append(' ').Append(top[i].Value / 1048576).Append(" MB x").Append(countByName[top[i].Key]).Append(']');
                Texture2D[] tex = Resources.FindObjectsOfTypeAll<Texture2D>(); long tb = 0, texReadable = 0, texNoMips = 0, texCompressed = 0, texBig = 0; string texTop = ""; long texTopB = 0;
                for (int i = 0; i < tex.Length; i++)
                {
                    Texture2D t = tex[i]; if (t == null) continue;
                    // bytes per pixel by format (compressed DXT1 0.5, DXT5 1, RGB24 3, Alpha8 1, everything else 4) and the 4/3 mip chain only when the texture has one
                    float bpp = 4f; TextureFormat f = t.format; bool comp = false;
                    if (f == TextureFormat.DXT1 || f == TextureFormat.DXT1Crunched) { bpp = 0.5f; comp = true; } else if (f == TextureFormat.DXT5 || f == TextureFormat.DXT5Crunched) { bpp = 1f; comp = true; } else if (f == TextureFormat.RGB24) bpp = 3f; else if (f == TextureFormat.Alpha8) bpp = 1f;
                    long b = (long)(t.width * (long)t.height * bpp); if (t.mipmapCount > 1) b = b * 4L / 3L;
                    tb += b; if (t.mipmapCount <= 1) texNoMips++; if (comp) texCompressed++; if (t.width >= 2048 || t.height >= 2048) texBig++;
                    if (b > texTopB) { texTopB = b; texTop = t.name + " " + t.width + "x" + t.height + " " + f + " mips" + t.mipmapCount; }
                }
                string texInfo = "; texdetail no mips " + texNoMips + ", DXT " + texCompressed + ", >=2048px " + texBig + ", biggest " + texTop + " (" + (texTopB / 1048576) + " MB); masterTextureLimit " + QualitySettings.masterTextureLimit;
                RenderTexture[] rts = Resources.FindObjectsOfTypeAll<RenderTexture>(); long rb = 0; StringBuilder rtTop = new StringBuilder(); long bestRt = 0;
                for (int i = 0; i < rts.Length; i++) if (rts[i] != null) { long b = (long)rts[i].width * rts[i].height * 4L * (rts[i].depth > 0 ? 2L : 1L); rb += b; if (b > bestRt) { bestRt = b; rtTop.Length = 0; rtTop.Append(rts[i].name).Append(' ').Append(rts[i].width).Append('x').Append(rts[i].height).Append(' ').Append(b / 1048576).Append(" MB"); } }
                Material[] mats = Resources.FindObjectsOfTypeAll<Material>();
                // 0.3.102: when the material count is high, name the biggest groups (instances created by Renderer.material end with " (Instance)") so the creator of a runaway count can be found
                string matTop = "";
                if (mats.Length >= 1500)
                {
                    Dictionary<string, int> matNames = new Dictionary<string, int>(); int matInst = 0;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        Material mt = mats[i]; if (mt == null) continue;
                        string mname = mt.name ?? ""; bool isInst = mname.EndsWith(" (Instance)", StringComparison.Ordinal); if (isInst) { matInst++; mname = mname.Substring(0, mname.Length - 11); }
                        if (mname.Length > 28) mname = mname.Substring(0, 28);
                        string mkey = isInst ? "I:" + mname : mname; int mcnt; matNames.TryGetValue(mkey, out mcnt); matNames[mkey] = mcnt + 1;
                    }
                    List<KeyValuePair<string, int>> mtop = new List<KeyValuePair<string, int>>(matNames);
                    mtop.Sort(delegate(KeyValuePair<string, int> x, KeyValuePair<string, int> y) { return y.Value.CompareTo(x.Value); });
                    StringBuilder mtb = new StringBuilder(" (instances " + matInst + ", names " + matNames.Count + ", top:");
                    for (int i = 0; i < mtop.Count && i < 6; i++) mtb.Append(" [").Append(mtop[i].Key).Append(" x").Append(mtop[i].Value).Append(']');
                    matTop = mtb.Append(')').ToString();
                }
                AudioClip[] clips = Resources.FindObjectsOfTypeAll<AudioClip>(); long ab = 0; for (int i = 0; i < clips.Length; i++) if (clips[i] != null) ab += Profiler.GetRuntimeMemorySize(clips[i]);
                sb.Append("; textures ").Append(tex.Length).Append(" (").Append(tb / 1048576).Append(" MB est.)").Append(texInfo).Append("; rendertex ").Append(rts.Length).Append(" (").Append(rb / 1048576).Append(" MB est., biggest ").Append(rtTop).Append("); screen ").Append(Screen.width).Append("x").Append(Screen.height).Append("; materials ").Append(mats.Length).Append(matTop)
                  .Append("; audio ").Append(clips.Length).Append(" (").Append(ab / 1048576).Append(" MB); voxel changes +").Append(msVoxTotal - msVoxLast).Append(" (total ").Append(msVoxTotal).Append("); mono ").Append(GC.GetTotalMemory(false) / 1048576).Append(" MB; scan ").Append(sw.ElapsedMilliseconds).Append(" ms");
                msVoxLast = msVoxTotal;
                PtLog(sb.ToString());
            }
            catch (Exception e) { memStatsNext = Time.realtimeSinceStartup + 600f; PtLog("MEMSTATS failed: " + e.GetType().Name + " " + e.Message); }
        }
    }
}
