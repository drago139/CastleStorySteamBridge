using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Game.AI;
using Brix.Game.Storage;
using CastleStorySteam;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 0.4.3 ORANGE-WARD PACK on top of 0.4.1 (own recipe). Every part has its own config switch and logs (prefix "ORANGE-WARD RECIPE ") when it acts; a missing delegate = the part is OFF.
// Models and the key also need the 0.4.1 recipe ([Fixes] OrangeWardCraftable): without it there is no orange item to draw or to queue.
//  F-1  small icon on a square canvas (OrangeWardItem.EnsureIcon asks SquareIcon): the Altar queue strip and the item counters draw Get32() into a square cell.
//  F-2  key for the 6th Craft-bar button (Rewired has no 6th queue action): a Lua badge (GameMenu.lua patch in OrangeWardLuaText) + this poller.
//  F-4  models of the finished item in crates and on the Altar: RecepteurDisplay switches child models by the stored adjectives and has none for Stick -> tinted twins.
//  F-3a the Altar preview (RecipeDisplay: shown while a recipe is the first queue item / being crafted; a blue hologram) -> an orange hologram twin of it.
// NOT included: the model in the worker's hands (Props -> Accessories.HeldSentinelWard stays red): networked tool objects, see the hand-over notes.
internal static class OrangeWardExtras
{
    internal static Func<bool> SquareIcon, Models, HotkeyOn, HologramOrange;   // [Fixes] OrangeWardSquareIcon / OrangeWardModels / OrangeWardHotkey / OrangeWardOrangeHologram (0.4.7)
    internal static Func<KeyCode> Key;                          // [Controls] OrangeWardKey
    static readonly Color Orange = new Color(1f, 0.42f, 0.02f, 1f);   // = OrangeWardItem.Orange (gem and light of the placed ward)
    static KeyCode keyCode; static string keyLabel = ""; static bool keyActive;
    static readonly List<WeakReference> scripts = new List<WeakReference>();
    static readonly Dictionary<Material, Material> gemCopies = new Dictionary<Material, Material>();
    static readonly Dictionary<int, GameObject> previews = new Dictionary<int, GameObject>();
    static readonly HashSet<int> previewFailed = new HashSet<int>();
    static int modelLogs, keyLogs, previewLogs, gateLogs;

    static void Log(string text) { try { CastleStorySteam.SteamBridgePlugin.PtLog("ORANGE-WARD RECIPE " + text); } catch (Exception) { } }

    internal static bool On(Func<bool> f) { try { return f != null && f(); } catch (Exception) { return false; } }

    // the 0.4.1 recipe switch: null = on (as in OrangeWardRecipe.Prepare)
    static bool RecipeOn() { try { return OrangeWardRecipe.Enabled == null || OrangeWardRecipe.Enabled(); } catch (Exception) { return false; } }

    static bool ModelsOn() { return On(Models) && RecipeOn(); }
    // 0.4.7: orange holograms (placement ghost, Altar preview) only on request; the default is the game's blue hologram of every other item
    internal static bool OrangeHolograms() { return OrangeWardPatchRules.OrangeHologram(On(HologramOrange), RecipeOn()); }

    // ---------------------------------------------------------------- config (called from CastleStoryGameplayFixes right before OrangeWardItem.Install)
    internal static void BindConfig(ConfigFile config)
    {
        try
        {
            ConfigEntry<bool> square = config.Bind("Fixes", "OrangeWardSquareIcon", true, "0.4.3: the small orange-ward icon (queue strip over the Altar, item counter over the Altar, the crates and the workers) is drawn from a square picture, so it is no longer stretched sideways. false = the 0.4.1 picture. Only a picture: players may differ.");
            ConfigEntry<bool> models = config.Bind("Fixes", "OrangeWardModels", true, "0.4.3: orange models for the orange ward in crates, on the Altar and in the Altar preview (copies of the red models with an orange gem / an orange hologram). false = nothing is drawn in crates and the Altar preview stays as in 0.4.1. Needs Fixes/OrangeWardCraftable = true. Only visuals: players may differ.");
            ConfigEntry<bool> hotkey = config.Bind("Fixes", "OrangeWardHotkey", true, "0.4.3: a key for the 6th button of the Altar's craft bar (the orange ward). false = click only (0.4.1). Needs Fixes/OrangeWardCraftable = true. Plugin on this PC only.");
            ConfigEntry<KeyCode> key = config.Bind("Controls", "OrangeWardKey", KeyCode.J, "Key that queues the orange ward while the Altar is selected (shown on the 6th button). Letters only; None = off. Letters the game itself uses in the craft bar, the camera and the commands (A B C D E G H I K L O P Q S T U W X Y Z) are refused; free ones: F J M N R V.");
            ConfigEntry<bool> holo = config.Bind("Fixes", "OrangeWardOrangeHologram", OrangeWardPatchRules.OrangeHologramDefault, "0.4.7: false (default) = the placement ghost and the Altar preview of the orange ward are the game's own BLUE holograms like every other item; true = orange holograms (0.3.99 ghost, 0.4.3 Altar preview). The placed ward, the item in the worker's hands and the crate / Altar item models stay orange either way. Only visuals: players may differ.");
            HologramOrange = delegate { return holo.Value; };
            SquareIcon = delegate { return square.Value; };
            Models = delegate { return models.Value; };
            HotkeyOn = delegate { return hotkey.Value; };
            Key = delegate { return key.Value; };
        }
        catch (Exception e) { Log("pack 0.4.3: config bind failed (" + e.GetType().Name + " " + e.Message + "): all parts stay OFF, the 0.4.1 behaviour applies"); }
    }

    // ---------------------------------------------------------------- install
    internal static void Install(Harmony harmony)
    {
        InitKey();
        bool models = ModelsOn();
        if (models)
        {
            try
            {
                MethodInfo start = typeof(RecepteurDisplay).GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                MethodInfo hook = typeof(OrangeWardExtras).GetMethod("AfterDisplayStart", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (start == null || hook == null) Log("models: RecepteurDisplay.Start not found, crates and the Altar draw nothing for the orange ward (0.4.1)");
                else harmony.Patch(start, null, new HarmonyMethod(hook));
            }
            catch (Exception e) { Log("models: hook failed: " + e.GetType().Name + " " + e.Message); }
        }
        else if (On(Models)) Log("models: [Fixes] OrangeWardCraftable = false: there is no orange ward item, so no models");
        Log("pack 0.4.3: square icon " + (On(SquareIcon) ? "ON" : "off") + ", models " + (models ? "ON" : "off") + ", hotkey " + (keyActive ? "ON (key " + keyLabel + ")" : "off") + ", holograms (ghost + Altar preview) " + (OrangeHolograms() ? "ORANGE" : "blue (the game's own)"));
    }

    // ---------------------------------------------------------------- F-2: the key
    static void InitKey()
    {
        keyActive = false; OrangeWardLuaText.HotkeyKeyed = false;
        if (!On(HotkeyOn)) { Log("hotkey: switched off by the config ([Fixes] OrangeWardHotkey = false or no config): slot 6 is click-only"); return; }
        if (!RecipeOn()) { Log("hotkey: [Fixes] OrangeWardCraftable = false: there is no orange ward recipe, so no key"); return; }
        KeyCode kc = KeyCode.None;
        try { if (Key != null) kc = Key(); } catch (Exception) { }
        string label;
        OrangeWardPatchRules.KeyVerdict v = OrangeWardPatchRules.JudgeKey((int)kc, out label);
        if (v == OrangeWardPatchRules.KeyVerdict.Off) { Log("hotkey: [Controls] OrangeWardKey = None: slot 6 is click-only"); return; }
        if (v == OrangeWardPatchRules.KeyVerdict.Reserved) { Log("hotkey: key " + kc + " is used by the game (craft bar, camera or commands): refused, slot 6 is click-only. Free letters: " + OrangeWardPatchRules.FreeLetters); return; }
        if (v != OrangeWardPatchRules.KeyVerdict.Ok) { Log("hotkey: key " + kc + " is not supported (letters only): slot 6 is click-only"); return; }
        try
        {
            keyCode = kc; keyLabel = label;
            GameObject go = new GameObject("SteamBridge OrangeWardKey");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<OrangeWardKeyPoller>();
            keyActive = true; OrangeWardLuaText.HotkeyKeyed = true;
            Log("hotkey: key " + label + " queues the orange ward while the Altar is selected (badge on the 6th button)");
        }
        catch (Exception e) { keyActive = false; OrangeWardLuaText.HotkeyKeyed = false; Log("hotkey: poller could not start (" + e.GetType().Name + " " + e.Message + "): slot 6 is click-only"); }
    }

    // Every Lua Script gets the label callback (the GameMenu.lua patch asks for it) and is remembered (weakly) so the key can find the Script that ran GameMenu.lua.
    // The Script constructor chain reports one Script twice (Script() -> Script(CoreModules)), so a Script is added once.
    internal static void OnScript(Script script)
    {
        if (!keyActive || script == null) return;
        try
        {
            if (script.Globals == null) return;
            script.Globals.Set("SB_OrangeHotkeyLabel", DynValue.NewCallback(delegate(ScriptExecutionContext ctx, CallbackArguments args) { return DynValue.NewString(keyLabel); }, "SB_OrangeHotkeyLabel"));
        }
        catch (Exception) { return; }   // a Script the game already killed has no Globals
        lock (scripts)
        {
            for (int i = scripts.Count - 1; i >= 0; i--) if (object.ReferenceEquals(scripts[i].Target, script)) return;
            scripts.Add(new WeakReference(script));
            if (scripts.Count > 64)
            {
                for (int i = scripts.Count - 1; i >= 0; i--) if (scripts[i].Target == null) scripts.RemoveAt(i);
                if (scripts.Count > 64) scripts.RemoveRange(0, scripts.Count - 48);
            }
        }
    }

    internal static void PollKey()
    {
        if (!keyActive) return;
        try
        {
            if (!Input.GetKeyDown(keyCode)) return;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return;
            if (!GameplayInput())
            {
                if (gateLogs++ < 3) Log("hotkey: key " + keyLabel + " ignored (not in the game, a menu or pause is open, the chat or a text field has the keyboard)");
                return;
            }
            RunSlotAction();
        }
        catch (Exception e) { if (keyLogs++ < 8) Log("hotkey: key handling failed: " + e.GetType().Name + " " + e.Message); }
    }

    // The same gate the plugin's unit-group keys use (UnitGroups.GroupGameplayInput, without the plugin's own overlay flags): in the game scene, focused, no pause / menu / world editor mode,
    // chat not active, no UGUI InputField selected (or its parent), no IMGUI text field with the keyboard (F8 window).
    static bool GameplayInput()
    {
        try
        {
            if (!Application.isFocused || SceneManager.GetActiveScene().name != "ScenePrincipale" || Brix.Engine.Architecte.editorMode) return false;
            Brix.Input.InputMode mode = Brix.Input.InputModeController.GetCurrentMode();
            if (mode == Brix.Input.InputMode.none || (mode & (Brix.Input.InputMode.pause | Brix.Input.InputMode.menu | Brix.Input.InputMode.worldEditor)) != 0) return false;
            if (Brix.Input.ControlContext.ContextIsActive("Chat")) return false;
            if (GUIUtility.keyboardControl != 0) return false;
            EventSystem es = EventSystem.current;
            GameObject sel = es == null ? null : es.currentSelectedGameObject;
            if (sel != null && sel.GetComponentInParent<InputField>() != null) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    // SB_OrangeSlotAction = ||h.OnAction() was stored by the patched GameMenu.lua for slot 6; OnAction itself checks that the Altar is selected (h.Visible()).
    // The newest Script that has it wins; a Script the game has killed throws on Globals and is dropped.
    static void RunSlotAction()
    {
        Script script = null; DynValue fn = DynValue.Nil;
        lock (scripts)
        {
            for (int i = scripts.Count - 1; i >= 0; i--)
            {
                Script s = scripts[i].Target as Script;
                if (s == null) { scripts.RemoveAt(i); continue; }
                try
                {
                    DynValue v = s.Globals.Get("SB_OrangeSlotAction");
                    if (v.Type == DataType.Function || v.Type == DataType.ClrFunction) { script = s; fn = v; break; }
                }
                catch (Exception) { scripts.RemoveAt(i); }
            }
        }
        if (script == null) { if (keyLogs++ < 10) Log("hotkey: key " + keyLabel + " pressed but no slot action exists (GameMenu.lua has not run yet, or its anchor was missing: see the lua lines)"); return; }
        DynValue r = script.Call(fn);
        if (keyLogs++ < 10) Log("hotkey: key " + keyLabel + " pressed, slot 6 action called, result " + r.Type + " (nothing happens unless the Altar is selected)");
    }

    // ---------------------------------------------------------------- F-4: crate / Altar models (RecepteurDisplay)
    static void AfterDisplayStart(RecepteurDisplay __instance)
    {
        try
        {
            if (__instance == null || !ModelsOn()) return;
            List<RecepteurDisplay.DisplayElement> els = __instance.displayElements;
            if (els == null || els.Count == 0) return;
            // cheap pass first: most displays (hats, the other wards) have no red-ward element and must cost nothing
            bool anyRed = false, hasStick = false;
            for (int i = 0; i < els.Count; i++)
            {
                RecepteurDisplay.DisplayElement el = els[i];
                if (el == null || el.conditions == null) continue;
                for (int k = 0; k < el.conditions.Count; k++)
                {
                    RecepteurDisplay.DisplayElement.ConditionElement ce = el.conditions[k];
                    AdjectiveKey key = ce == null ? null : ce.relatedAdjective;
                    if (key == null) continue;
                    if (key.codeName == "Stick") hasStick = true;
                    else if (el.conditions.Count == 1 && key.codeName == OrangeWardPatchRules.RedWardAdjective) anyRed = true;
                }
            }
            if (!anyRed || hasStick) return;
            string[] first = new string[els.Count];
            for (int i = 0; i < els.Count; i++)
            {
                RecepteurDisplay.DisplayElement el = els[i];
                if (el == null || el.conditions == null || el.conditions.Count != 1 || el.conditions[0] == null) continue;
                AdjectiveKey key = el.conditions[0].relatedAdjective;
                if (key != null) first[i] = key.codeName;
            }
            List<int> todo = OrangeWardPatchRules.ElementsToClone(first, hasStick);
            if (todo.Count == 0) return;
            AdjectiveKey stick = Adjectif.stick.GetKey();
            List<RecepteurDisplay.DisplayElement> twins = new List<RecepteurDisplay.DisplayElement>();
            for (int n = 0; n < todo.Count; n++)
            {
                RecepteurDisplay.DisplayElement el = els[todo[n]];
                GameObject src = el.targetObject as GameObject;
                if (src == null) continue;
                GameObject clone = CloneTinted(src, false);
                if (clone == null) continue;
                RecepteurDisplay.DisplayElement twin = el.GetCopy();
                twin.targetObject = clone; twin.conditionLink = el.conditionLink;
                twin.conditions[0].relatedAdjective = stick;   // same condition kind and number as the red element, on Stick
                twins.Add(twin);
            }
            if (twins.Count == 0) return;
            els.AddRange(twins);
            if (modelLogs++ < 12) Log("models: " + __instance.gameObject.name + " (" + (__instance.transform.parent == null ? "root" : __instance.transform.parent.name) + ") got " + twins.Count + " orange twin(s) of its red ward element(s), " + els.Count + " display elements now");
        }
        catch (Exception e) { if (modelLogs++ < 12) Log("models: display hook failed: " + e.GetType().Name + " " + e.Message); }
    }

    // ---------------------------------------------------------------- F-3a: the Altar preview (RecipeDisplay.displays[product key])
    // red = the game's function for the red ward ((RecipeDisplay d) => d.Sentinel): a blue hologram (Blueprint material) shown while a recipe is the first queue item / being crafted.
    // The twin is made once per RecipeDisplay; on any problem (also remembered, so no copy is made twice) the original model is shown (0.4.1).
    internal static Func<RecipeDisplay, GameObject> PreviewFor(Func<RecipeDisplay, GameObject> red)
    {
        if (red == null || !ModelsOn() || !OrangeHolograms()) return red;
        return delegate(RecipeDisplay d)
        {
            GameObject src = red(d);
            if (src == null || d == null) return src;
            int id = 0;
            try
            {
                id = d.GetInstanceID();
                GameObject c;
                if (previews.TryGetValue(id, out c) && c != null) return c;
                if (previewFailed.Contains(id)) return src;
                c = CloneTinted(src, true);
                if (previews.Count > 200 || previewFailed.Count > 200) { previews.Clear(); previewFailed.Clear(); }
                if (c == null) { previewFailed.Add(id); if (previewLogs++ < 4) Log("preview: no orange twin for " + d.gameObject.name + " (nothing to recolour or the copy failed): the original model stays"); return src; }
                previews[id] = c;
                if (previewLogs++ < 4) Log("preview: " + d.gameObject.name + " got the orange preview model " + c.name);
                return c;
            }
            catch (Exception e) { previewFailed.Add(id); if (previewLogs++ < 4) Log("preview: failed (" + e.GetType().Name + " " + e.Message + "), the original model stays"); return src; }
        };
    }

    // ---------------------------------------------------------------- shared: a twin of a red display model
    // crate / Altar model (preview = false): a Base mesh with children Gem (material SentinelWard_Gem) and Plates; the only red part is the renderer named "Gem" (same rule as
    // OrangeWardItem.Customize). Preview (preview = true): the whole model wears the blue hologram material, so every blue hologram colour becomes orange (alpha kept), exactly like the
    // orange placement ghost (OrangeWardItem.OrangeCopy); a preview made of real materials falls back to the Gem rule.
    // The twin sits next to the original (same parent and local transform, checked), starts inactive and is never shared with a second element. Any failure destroys it and returns null.
    static GameObject CloneTinted(GameObject src, bool preview)
    {
        GameObject c = null;
        try
        {
            c = UnityEngine.Object.Instantiate(src, src.transform.parent, false) as GameObject;
            if (c == null) return null;
            c.SetActive(false);
            if (c.transform.parent != src.transform.parent) { UnityEngine.Object.Destroy(c); return null; }
            c.name = src.name + "_Orange";
            int n = preview ? TintPreview(c) : TintGems(c);
            if (n == 0) { UnityEngine.Object.Destroy(c); return null; }   // nothing to recolour: never show a red/blue model as the orange one
            return c;
        }
        catch (Exception e)
        {
            try { if (c != null) { c.SetActive(false); UnityEngine.Object.Destroy(c); } } catch (Exception) { }
            if (modelLogs++ < 12) Log("models: twin of " + (src == null ? "?" : src.name) + " failed: " + e.GetType().Name + " " + e.Message);
            return null;
        }
    }

    internal static int TintGems(GameObject root)   // 0.4.6: also used by OrangeWardHeld for the carried model
    {
        int n = 0;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || !OrangeWardPatchRules.IsGemName(r.name) || r.sharedMaterial == null) continue;
            r.sharedMaterial = OrangeGem(r.sharedMaterial); n++;
        }
        return n;
    }

    static int TintPreview(GameObject root)
    {
        int changed = 0;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            Material[] ms = r.sharedMaterials; bool any = false;
            for (int i = 0; i < ms.Length; i++)
            {
                Material o = OrangeWardItem.OrangeCopy(ms[i]);   // blue hologram colours -> orange with the same alpha; any other material comes back unchanged
                if (!object.ReferenceEquals(o, ms[i])) { ms[i] = o; any = true; }
            }
            if (any) { r.sharedMaterials = ms; changed++; }
        }
        return changed > 0 ? changed : TintGems(root);
    }

    static Material OrangeGem(Material m)
    {
        Material c;
        if (gemCopies.TryGetValue(m, out c) && c != null) return c;
        c = new Material(m); c.name = "OrangeWard_Gem";
        if (c.HasProperty("_Color")) c.SetColor("_Color", Orange);
        if (c.HasProperty("_EmissionColor")) c.SetColor("_EmissionColor", Orange);
        gemCopies[m] = c;
        return c;
    }
}

// one tiny component polls the key (the plugin class itself is not touched: it belongs to other packs)
public sealed class OrangeWardKeyPoller : MonoBehaviour
{
    void Update() { OrangeWardExtras.PollKey(); }
}
