using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CastleStorySteam
{
    // CLICKLOG (bug pack; log only; switch [Diag] ClickLog, default true; the user asked for click recording in the full diagnostics). Every mouse-button press and release in a match is written to
    // perf-trace.txt: button (L/R/M = the raw hardware button; the game itself acts on Rewired cam_action1/cam_action2), DOWN/UP/DOWN+UP, screen position (Unity origin = bottom-left) and window size,
    // Shift/Ctrl/Alt held, the first 3 UI elements under the pointer (EventSystem raycast, parent/leaf), the currently selected UI object, the nearest physics object under the cursor (Camera.main ray with the
    // default layer mask: name, parent, layer, trigger flag, distance, hit point - NOT what the game's own picker sees) and the game's picking state (Picking.actif, UILocked, active override picker, blueprint
    // being placed, input mode, and for an override picker what its own voxel raycast hit: rh=). Made for "left click does not fire the catapult" (B-067) and "orange ward is clickable in the menu but never
    // appears" (B-068).
    // Correlate with: CAT-CMD send / recv (same frame; its absence after an L DOWN means the click never committed a target), CAT-STATE (polled 1x/s, written only on change, so up to ~1 s later) and
    // brix.log / output_log.txt exceptions of the same second. ORANGE-WARD lines are start-up lines only, they have no click-path line.
    // Only in the scene ScenePrincipale. "CLICKLOG on" is written once per match. Volume: a token bucket (burst of 120 lines, then 1 per second; "CLICKLOG skipped N" before the next line after a storm;
    // a dropped DOWN can leave its UP unpaired) and at most 4000 lines per match (about 1.2 MB), then one "CLICKLOG limit" line. Object names are filtered (BugPackRules.SafeName: plain characters only,
    // 40 characters, a typed text becomes "?<length>"). No keys, no text. Changes nothing in the game.
    // Hook: ONE call, ClickLogTick(); in SteamBridgePlugin.Update (see README). The switch is bound on the first call.
    public sealed partial class SteamBridgePlugin
    {
        static ConfigEntry<bool> clickLogCfg;
        int clickLogged; bool clickIn, clickCapped;
        readonly TokenBucket clickBudget = new TokenBucket(BugPackRules.ClickBurst, BugPackRules.ClickPerSecond);
        readonly List<RaycastResult> clickUi = new List<RaycastResult>();
        readonly StringBuilder clickSb = new StringBuilder(512);
        PointerEventData clickPed; EventSystem clickPedFor;

        void ClickLogTick()
        {
            try
            {
                if (clickLogCfg == null) clickLogCfg = Config.Bind("Diag", "ClickLog", true, "Log every mouse click in a match to perf-trace.txt (button, position, UI element and world object under the pointer, the game's picking state). Rate limited, at most 4000 lines per match. Changes nothing in the game.");
                if (!clickLogCfg.Value || !ptOn) return;
                if (!HpInGame()) { clickIn = false; return; }
                if (!clickIn) { clickIn = true; clickCapped = false; clickLogged = 0; clickBudget.Reset(); PtLog("CLICKLOG on: mouse presses/releases of this match are logged (rate limited, max " + BugPackRules.ClickMatchLimit + " lines)"); }
                if (clickLogged >= BugPackRules.ClickMatchLimit) { if (!clickCapped) { clickCapped = true; PtLog("CLICKLOG limit " + BugPackRules.ClickMatchLimit + " reached: no more CLICK lines in this match"); } return; }
                for (int b = 0; b < 3; b++)
                {
                    bool down = Input.GetMouseButtonDown(b), up = Input.GetMouseButtonUp(b);
                    if (!down && !up) continue;
                    if (!clickBudget.Allow(Time.unscaledTime)) continue;
                    int skipped = clickBudget.TakeDropped();
                    if (skipped > 0) { clickLogged++; PtLog("CLICKLOG skipped " + skipped + " clicks (rate limit)"); }
                    ClickLogWrite(b, down ? (up ? "DOWN+UP" : "DOWN") : "UP");
                }
            }
            catch (Exception) { }
        }

        void ClickLogWrite(int button, string kind)
        {
            clickLogged++;
            Vector3 mp = Input.mousePosition;
            StringBuilder sb = clickSb; sb.Length = 0;
            sb.Append("CLICK #").Append(clickLogged).Append(' ').Append(kind).Append(" btn=").Append(button == 0 ? "L" : button == 1 ? "R" : "M");
            sb.Append(" pos=").Append((int)mp.x).Append(',').Append((int)mp.y).Append(" scr=").Append(Screen.width).Append('x').Append(Screen.height).Append(" mods=");
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) sb.Append('S');
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) sb.Append('C');
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) sb.Append('A');
            try
            {
                EventSystem es = EventSystem.current;
                if (es == null) sb.Append(" ui=noEventSystem");
                else
                {
                    clickUi.Clear();
                    if (clickPed == null || clickPedFor != es) { clickPed = new PointerEventData(es); clickPedFor = es; }
                    clickPed.position = mp;
                    es.RaycastAll(clickPed, clickUi);
                    sb.Append(" ui=");
                    if (clickUi.Count == 0) sb.Append("none");
                    for (int i = 0; i < clickUi.Count && i < 3; i++)
                    {
                        GameObject g = clickUi[i].gameObject; sb.Append(i > 0 ? "|" : "");
                        if (g == null) sb.Append("null");
                        else { Transform pt = g.transform.parent; sb.Append(pt != null ? BugPackRules.SafeName(pt.name) + "/" : "").Append(BugPackRules.SafeName(g.name)); }
                    }
                    sb.Append(" selected=").Append(es.currentSelectedGameObject != null ? BugPackRules.SafeName(es.currentSelectedGameObject.name) : "none");
                }
            }
            catch (Exception e) { sb.Append(" uiErr=").Append(e.GetType().Name); }
            try
            {
                Camera cam = Camera.main;
                if (cam == null) sb.Append(" world=noCamera");
                else
                {
                    RaycastHit hit;
                    sb.Append(" world=");
                    if (!Physics.Raycast(cam.ScreenPointToRay(mp), out hit, 2000f) || hit.collider == null) sb.Append("none");
                    else
                    {
                        GameObject go = hit.collider.gameObject; Transform parent = go.transform.parent; Vector3 at = hit.point;
                        sb.Append(BugPackRules.SafeName(go.name)).Append(parent != null ? " <" + BugPackRules.SafeName(parent.name) : "").Append("[L").Append(go.layer).Append(hit.collider.isTrigger ? ",trigger" : "").Append("] d=").Append(hit.distance.ToString("F0"))
                          .Append(" at=").Append(at.x.ToString("F0")).Append(',').Append(at.y.ToString("F0")).Append(',').Append(at.z.ToString("F0"));
                    }
                }
            }
            catch (Exception e) { sb.Append(" worldErr=").Append(e.GetType().Name); }
            try
            {
                Brix.Input.Picking pk = Brix.Input.Picking.Instance;
                sb.Append(" act=").Append(Brix.Input.Picking.actif ? 1 : 0).Append(" mode=").Append(Brix.Input.InputModeController.GetCurrentMode());
                if (pk == null) sb.Append(" picking=none");
                else
                {
                    sb.Append(" uiLock=").Append(pk.UILocked ? 1 : 0);
                    Brix.Input.Picker op = pk.OverridePickerActive() ? pk.OverridePicker : null;
                    sb.Append(" pick=").Append(op != null ? op.AssetKey.ToString() : "default");
                    if (op != null && op.HeldKey != null) sb.Append(" held=").Append(op.HeldKey.ToString());
                    if (op != null && op.RaycastHit != null)
                    {
                        GameObject hg = op.RaycastHit.HasHit ? op.RaycastHit.HitGameObject : null;
                        sb.Append(" rh=").Append(op.RaycastHit.HasHit ? 1 : 0).Append(op.RaycastHit.OnDefaultPlane ? "/plane" : "").Append(hg != null ? "/" + BugPackRules.SafeName(hg.name) + "[L" + hg.layer + "]" : "");
                    }
                }
            }
            catch (Exception e) { sb.Append(" pickErr=").Append(e.GetType().Name); }
            PtLog(sb.ToString());
        }
    }
}
