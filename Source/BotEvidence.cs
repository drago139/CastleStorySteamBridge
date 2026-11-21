// 0.4.10 IN-GAME TEST BOT evidence: end-of-frame screenshot with a probe box around a target (+ background ring) and the game camera helpers.
// Taken from the read-only research draft bot-research\evidence-BotEvidence.cs (2026-11-06; APIs checked in the real DLL decompiles) and compiled
// with the plugin. Local only (host and guest alike); used by BotActions.cs while the bot is armed. The pixel classes live in BotRules.cs (BotColor).
using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using Brix.Input;
using UnityEngine;

namespace CastleStorySteam {
    // ======================= 1. screenshot + pixel probe (end of frame = what the player sees) =======================
    public sealed class BotShotResult
    {
        public bool Ok; public string Error, Path; public int Width, Height, Frame; public double EncodeMs;
        public bool TargetOnScreen; public float TargetX, TargetY, TargetDepth;
        public int BoxX0, BoxY0, BoxX1, BoxY1;            // screen px, origin bottom-left (same as Camera.WorldToScreenPoint and ReadPixels)
        public int RegionX, RegionY, RegionW, RegionH;    // box + background ring actually read back
        public int[] RegionRgb;                           // 0xRRGGBB, bottom row first (Texture2D.GetPixels32 order)
    }

    // Add once (plugin.gameObject.AddComponent<BotEvidence>()), call from the main thread, one capture at a time.
    public sealed class BotEvidence : MonoBehaviour
    {
        public bool Busy { get; private set; }

        // path: absolute .png / .jpg or null (no file). target: world point to probe or null. bounds: optional world bounds of the target (WorldBounds below)
        // to size the box; boxHalf: half box in px when there are no bounds; ring: background margin in px around the box.
        public bool Capture(string path, Vector3? target, Bounds? bounds, int boxHalf, int ring, Action<BotShotResult> done)
        {
            if (Busy) return false;
            Busy = true;
            StartCoroutine(Run(path, target, bounds, boxHalf, ring, done));
            return true;
        }

        IEnumerator Run(string path, Vector3? target, Bounds? bounds, int boxHalf, int ring, Action<BotShotResult> done)
        {
            yield return new WaitForEndOfFrame();   // all cameras, the uGUI overlay and IMGUI are in the back buffer now; ReadPixels on the back buffer is only valid here
            BotShotResult r = new BotShotResult(); Texture2D full = null, part = null;
            try
            {
                int w = Screen.width, h = Screen.height;
                r.Width = w; r.Height = h; r.Frame = Time.frameCount;
                Camera cam = Camera.main;
                if (target.HasValue && cam != null)
                {
                    Vector3 s = cam.WorldToScreenPoint(target.Value);
                    r.TargetX = s.x; r.TargetY = s.y; r.TargetDepth = s.z;
                    r.TargetOnScreen = s.z > 0f && s.x >= 0f && s.y >= 0f && s.x < w && s.y < h;
                    int x0 = (int)s.x - boxHalf, y0 = (int)s.y - boxHalf, x1 = (int)s.x + boxHalf, y1 = (int)s.y + boxHalf;
                    if (bounds.HasValue) ProjectBounds(cam, bounds.Value, ref x0, ref y0, ref x1, ref y1);
                    r.BoxX0 = Clamp(x0, 0, w); r.BoxY0 = Clamp(y0, 0, h); r.BoxX1 = Clamp(x1, 0, w); r.BoxY1 = Clamp(y1, 0, h);
                    if (r.TargetOnScreen && r.BoxX1 - r.BoxX0 >= 2 && r.BoxY1 - r.BoxY0 >= 2)
                    {
                        r.RegionX = Clamp(r.BoxX0 - ring, 0, w); r.RegionY = Clamp(r.BoxY0 - ring, 0, h);
                        r.RegionW = Clamp(r.BoxX1 + ring, 0, w) - r.RegionX; r.RegionH = Clamp(r.BoxY1 + ring, 0, h) - r.RegionY;
                        part = new Texture2D(r.RegionW, r.RegionH, TextureFormat.RGB24, false);
                        part.ReadPixels(new Rect(r.RegionX, r.RegionY, r.RegionW, r.RegionH), 0, 0, false);   // small read: no 33 MB Color32[] of a 4K frame
                        part.Apply(false);
                        Color32[] px = part.GetPixels32();
                        r.RegionRgb = new int[px.Length];
                        for (int i = 0; i < px.Length; i++) r.RegionRgb[i] = (px[i].r << 16) | (px[i].g << 8) | px[i].b;
                    }
                }
                if (path != null)
                {
                    full = new Texture2D(w, h, TextureFormat.RGB24, false);
                    full.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    full.Apply(false);
                    Stopwatch sw = Stopwatch.StartNew();
                    byte[] data = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? full.EncodeToJPG(90) : full.EncodeToPNG();   // main thread only; the hitch
                    r.EncodeMs = sw.Elapsed.TotalMilliseconds;
                    string dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string file = path;
                    ThreadPool.QueueUserWorkItem(delegate(object unused) { try { File.WriteAllBytes(file, data); } catch (Exception) { } });
                    r.Path = path;
                }
                r.Ok = true;
            }
            catch (Exception e) { r.Error = e.GetType().Name + ": " + e.Message; }
            finally { if (full != null) Destroy(full); if (part != null) Destroy(part); Busy = false; }
            if (done != null) { try { done(r); } catch (Exception) { } }
        }

        // union of the enabled renderers under a GameObject (particles and line renderers skipped)
        public static bool WorldBounds(GameObject go, out Bounds b)
        {
            b = new Bounds(); bool any = false;
            if (go == null) return false;
            Renderer[] rs = go.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer rr = rs[i];
                if (rr == null || !rr.enabled || rr is ParticleSystemRenderer || rr is LineRenderer) continue;
                if (!any) { b = rr.bounds; any = true; } else b.Encapsulate(rr.bounds);
            }
            return any;
        }

        static void ProjectBounds(Camera cam, Bounds b, ref int x0, ref int y0, ref int x1, ref int y1)
        {
            Vector3 c = b.center, e = b.extents;
            int ax0 = int.MaxValue, ay0 = int.MaxValue, ax1 = int.MinValue, ay1 = int.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x), c.y + ((i & 2) == 0 ? -e.y : e.y), c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 s = cam.WorldToScreenPoint(p);
                if (s.z <= 0f) return;   // a corner behind the camera: keep the fixed box
                ax0 = Math.Min(ax0, (int)s.x); ay0 = Math.Min(ay0, (int)s.y); ax1 = Math.Max(ax1, (int)s.x + 1); ay1 = Math.Max(ay1, (int)s.y + 1);
            }
            x0 = ax0; y0 = ay0; x1 = ax1; y1 = ay1;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // ======================= 3. camera: point, focus, hide gizmo, restore (local only, host and guest) =======================
    public sealed class BotCamState { public Vector3 Target; public float Distance, Pitch, Yaw; public bool AutoAdjust, Hug, Cinematique, GizmoHidden; }

    public static class BotCamera
    {
        public static CameraScript Get()
        {
            CameraScript cs = CameraScript.Instance;
            if (cs == null && Camera.main != null) cs = Camera.main.GetComponent<CameraScript>();
            return cs != null && cs.isActiveAndEnabled ? cs : null;   // CameraScript is disabled until GameSignals.LevelReady (CameraScript.Start)
        }

        public static BotCamState Save(CameraScript cs)
        {
            BotCamState s = new BotCamState();
            s.Target = CameraScript.Target; s.Distance = cs.Distance; s.Pitch = cs.AngleOfView; s.Yaw = cs.CamOrientation;
            s.AutoAdjust = cs.AutoAdjustWhenIdle; s.Hug = cs.HugTerrain; s.Cinematique = cs.Cinematique;
            return s;
        }

        // the minimap-click path (MiniMap.MoveAt -> CameraScript.LookAt): keeps pitch/yaw, puts 'world' on the pivot = screen centre
        public static void LookAt(CameraScript cs, Vector3 world, float distance)
        {
            cs.AutoAdjustWhenIdle = false; cs.HugTerrain = false;   // stop ApplyAutoClimbToTarget from sliding Target.y to the terrain under it
            if (distance > 0f) cs.Distance = distance;               // ClampDistance() keeps it inside ZoomIn/ZoomOut limits next Update
            cs.LookAt(world);
        }

        // the 'cam_focus_target' key path for a far unit (private FocusOnTarget/ChangeTarget): pivot on the unit from FindBestAngle
        public static void Focus(CameraScript cs, Transform t, float distance)
        {
            cs.AutoAdjustWhenIdle = false; cs.HugTerrain = false;
            float d = distance > 0f ? distance : cs.Distance;
            Quaternion r = cs.FindBestAngle(t);
            cs.Positionner(t.position - r * Vector3.forward * d, r, d);   // Target = t.position; pitch/yaw rounded to whole degrees (private LookAt(Quaternion))
        }

        // the camera target marker and its 2 s line sit exactly on the screen centre = on the focused target
        public static void HideGizmo(CameraScript cs, BotCamState s) { cs.DeactivateGizmoRenderers(); if (s != null) s.GizmoHidden = true; }

        // exact restore (no rounding): the next CameraScript.Update rebuilds the transform from these four values
        public static void Restore(CameraScript cs, BotCamState s)
        {
            if (cs == null || s == null) return;
            if (s.GizmoHidden) { if (s.Cinematique) cs.Cinematique = true; else cs.ActivateGizmoRenderers(); }
            CameraScript.Target = s.Target; cs.Distance = s.Distance; cs.AngleOfView = s.Pitch; cs.CamOrientation = s.Yaw;
            cs.AutoAdjustWhenIdle = s.AutoAdjust; cs.HugTerrain = s.Hug;
        }

        public static string Describe(CameraScript cs)
        {
            CultureInfo ic = CultureInfo.InvariantCulture; Vector3 t = CameraScript.Target;
            return "target=(" + t.x.ToString("F1", ic) + "," + t.y.ToString("F1", ic) + "," + t.z.ToString("F1", ic) + ") dist=" + cs.Distance.ToString("F1", ic)
                + " pitch=" + cs.AngleOfView.ToString("F0", ic) + " yaw=" + cs.CamOrientation.ToString("F0", ic) + " freeCam=" + cs.FreeCam + " fixCam=" + cs.FixCam.ToString("F0", ic)
                + " pivot=" + cs.PivotAroundTarget + " fps=" + cs.FPSmode + " cin=" + cs.Cinematique + " hugDelay=" + cs.AutoHugDelay.ToString("F1", ic);
        }
    }
}
