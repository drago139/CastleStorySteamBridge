using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Brix.Game.AI;
using Brix.UI.GameSelector;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CastleStorySteam
{
    // 0.3.61: control groups of fighters, like in an RTS: Ctrl+1..4 saves the selected units (at most 6) as group 1..4, Shift+Ctrl+N adds the selection to group N
    // (up to 6), pressing 1..4 selects the group again. The selection is local to each player (no network traffic), units that died or left are dropped.
    // Config: [Groups] Enabled, MaxPerGroup (1..15). If the game already uses these keys for something else, switch the feature off with Enabled = false.
    public sealed partial class SteamBridgePlugin
    {
        const int GroupCount = 6;
        ConfigEntry<bool> groupsEnabled; ConfigEntry<int> groupsMax;
        readonly List<GameObject>[] groups = new List<GameObject>[GroupCount];
        List<GameObject> groupScratch = new List<GameObject>();
        readonly List<List<GameObject>[]> groupHistory = new List<List<GameObject>[]>(); string groupsCapturing; bool groupsProbed; string groupsLine = ""; string groupsToast = ""; float groupsToastUntil, nextGroupsRefresh; bool groupsScene; float groupsLastTap; int groupsLastKey = -1;
        // Bindings (changed in Options -> Controls -> "Групи бійців", or in the config): the main and the alternative key of each group, the two modifiers, the undo key.
        internal enum GroupMod { Ctrl, Shift, Alt }
        KeyCode[] groupKeys = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6 };
        KeyCode[] groupPad = { KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5, KeyCode.Keypad6 };
        GroupMod groupModAssign = GroupMod.Ctrl, groupModAdd = GroupMod.Shift; KeyCode groupUndoKey = KeyCode.Z;
        ConfigEntry<string>[] groupKeyCfg = new ConfigEntry<string>[GroupCount], groupPadCfg = new ConfigEntry<string>[GroupCount]; ConfigEntry<string> groupModAssignCfg, groupModAddCfg, groupUndoCfg;
        string groupsBindingError = "", groupsCaptureError = "";

        static KeyCode ParseKey(string s, KeyCode fallback) { try { KeyCode key = (KeyCode)Enum.Parse(typeof(KeyCode), s, true); return Enum.IsDefined(typeof(KeyCode), key) ? key : fallback; } catch (Exception) { return fallback; } }
        static GroupMod ParseMod(string s, GroupMod fallback) { try { return (GroupMod)Enum.Parse(typeof(GroupMod), s, true); } catch (Exception) { return fallback; } }
        static bool ModDown(GroupMod m)
        {
            switch (m)
            {
                case GroupMod.Ctrl: return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                case GroupMod.Shift: return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                default: return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            }
        }
        static string ModLabel(GroupMod m) { return m == GroupMod.Ctrl ? "Ctrl" : (m == GroupMod.Shift ? "Shift" : "Alt"); }
        internal static string KeyLabel(KeyCode k) { if (k == KeyCode.None) return "НЕМАЄ"; if (k == KeyCode.Mouse2) return "МИША: СЕРЕДНЯ"; if (k == KeyCode.Mouse3) return "МИША: НАЗАД"; if (k == KeyCode.Mouse4) return "МИША: ВПЕРЕД"; if (k >= KeyCode.Mouse5 && k <= KeyCode.Mouse6) return "МИША " + ((int)k - (int)KeyCode.Mouse0 + 1); string n = k.ToString(); if (n.StartsWith("Alpha")) n = n.Substring(5); return n.ToUpper(); }
        void SaveGroupBindings()
        {
            ValidateGroupBindings(); groupsCaptureError = "";
            try
            {
                for (int i = 0; i < GroupCount; i++) { groupKeyCfg[i].Value = groupKeys[i].ToString(); groupPadCfg[i].Value = groupPad[i].ToString(); }
                groupModAssignCfg.Value = groupModAssign.ToString(); groupModAddCfg.Value = groupModAdd.ToString(); groupUndoCfg.Value = groupUndoKey.ToString();
            }
            catch (Exception) { }
        }
        void ResetGroupBindings()
        {
            for (int i = 0; i < GroupCount; i++) { groupKeys[i] = (KeyCode)((int)KeyCode.Alpha1 + i); groupPad[i] = (KeyCode)((int)KeyCode.Keypad1 + i); }
            groupModAssign = GroupMod.Ctrl; groupModAdd = GroupMod.Shift; groupUndoKey = KeyCode.Z; SaveGroupBindings();
        }

        void InstallUnitGroups()
        {
            try
            {
                groupsEnabled = Config.Bind("Groups", "Enabled", true, "Control groups of fighters: Ctrl+Z undoes the last group change. Ctrl+1..6 saves the selected units (with nothing selected it clears the group), 1..6 selects them again (Shift+Ctrl+N adds). Local to each player.");
                groupsMax = Config.Bind("Groups", "MaxPerGroup", 15, "How many units one group can hold (1..15).");
                for (int i = 0; i < GroupCount; i++)
                {
                    groups[i] = new List<GameObject>();
                    groupKeyCfg[i] = Config.Bind("Groups", "Key" + (i + 1), ((KeyCode)((int)KeyCode.Alpha1 + i)).ToString(), "Main key that selects group " + (i + 1) + " (a Unity KeyCode name).");
                    groupPadCfg[i] = Config.Bind("Groups", "AltKey" + (i + 1), ((KeyCode)((int)KeyCode.Keypad1 + i)).ToString(), "Alternative key that selects group " + (i + 1) + " (None = no alternative).");
                    groupKeys[i] = ParseKey(groupKeyCfg[i].Value, (KeyCode)((int)KeyCode.Alpha1 + i)); groupPad[i] = ParseKey(groupPadCfg[i].Value, KeyCode.None);
                }
                groupModAssignCfg = Config.Bind("Groups", "SaveModifier", "Ctrl", "Hold this (Ctrl, Shift or Alt) with a group key to save the selected units into the group.");
                groupModAddCfg = Config.Bind("Groups", "AddModifier", "Shift", "Hold this together with the save modifier to ADD to the group instead of replacing it.");
                groupUndoCfg = Config.Bind("Groups", "UndoKey", "Z", "Key that undoes the last group change (with the save modifier held).");
                groupModAssign = ParseMod(groupModAssignCfg.Value, GroupMod.Ctrl); groupModAdd = ParseMod(groupModAddCfg.Value, GroupMod.Shift); groupUndoKey = ParseKey(groupUndoCfg.Value, KeyCode.Z);
                if (groupModAdd == groupModAssign) { groupModAdd = groupModAssign == GroupMod.Shift ? GroupMod.Alt : GroupMod.Shift; }
                ValidateGroupBindings();
                if (groupsBindingError.Length > 0) Logger.LogWarning("Unit groups shortcuts disabled: " + groupsBindingError + " Fix the bindings in Controls; config preserved.");
            }
            catch (Exception) { }
        }

        string GroupBindingConflict(string id, KeyCode key)
        {
            if (key == KeyCode.None) return id[0] == 'p' ? "" : "A primary/undo key is required.";
            if (!Enum.IsDefined(typeof(KeyCode), key) || key == KeyCode.Escape || key == KeyCode.F8 || key == KeyCode.LeftControl || key == KeyCode.RightControl || key == KeyCode.LeftShift || key == KeyCode.RightShift || key == KeyCode.LeftAlt || key == KeyCode.RightAlt || ((int)key >= (int)KeyCode.Mouse0 && !(key >= KeyCode.Mouse2 && key <= KeyCode.Mouse6)))
                return "Choose a keyboard key or a mouse button 3..7 (middle / side buttons) other than a modifier, Escape, F8 or the left / right mouse button.";
            if (id != "uz" && key == groupUndoKey) return KeyLabel(key) + " is already the undo key.";
            for (int i = 0; i < GroupCount; i++)
                if ((id != "g" + i && key == groupKeys[i]) || (id != "p" + i && key == groupPad[i])) return KeyLabel(key) + " is already used by group " + (i + 1) + ".";
            return "";
        }

        void ValidateGroupBindings()
        {
            groupsBindingError = GroupBindingConflict("uz", groupUndoKey);
            for (int i = 0; i < GroupCount && groupsBindingError.Length == 0; i++)
            {
                groupsBindingError = GroupBindingConflict("g" + i, groupKeys[i]);
                if (groupsBindingError.Length == 0) groupsBindingError = GroupBindingConflict("p" + i, groupPad[i]);
            }
        }

        void UnitGroupsTick()
        {
            if (groupsEnabled == null || !groupsEnabled.Value) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextGroupsRefresh)
            {
                nextGroupsRefresh = now + 0.4f;
                try { groupsScene = SceneManager.GetActiveScene().name == "ScenePrincipale" && UIGameSelector.instance != null && GroupsMatchReady(); } catch (Exception) { groupsScene = false; }
                if (groupsScene) { RefreshGroupsLine(); if (!groupsProbed) { groupsProbed = true; ProbeKeyConflicts(); } }
            }
            if (!GroupGameplayInput()) return;
            bool ctrl = ModDown(groupModAssign), shift = ModDown(groupModAdd);
            if (ctrl && !shift && groupUndoKey != KeyCode.None && Input.GetKeyDown(groupUndoKey)) { UndoGroups(); return; }
            for (int i = 0; i < GroupCount; i++)
            {
                if (!(groupKeys[i] != KeyCode.None && Input.GetKeyDown(groupKeys[i])) && !(groupPad[i] != KeyCode.None && Input.GetKeyDown(groupPad[i]))) continue;
                try { if (ctrl) AssignGroup(i, shift); else if (!shift) RecallGroup(i, now); }
                catch (Exception e) { groupsToast = "Групи: помилка " + e.Message; groupsToastUntil = now + 3f; Logger.LogWarning("Unit groups: " + e.Message); }
            }
        }

        bool GroupGameplayInput()
        {
            try
            {
                // Do not cache this gate: a menu can open between geometry probes.
                if (SceneManager.GetActiveScene().name != "ScenePrincipale" || UIGameSelector.instance == null || !Application.isFocused || Brix.Engine.Architecte.editorMode) return false;
                // The owning overlay/menu and native text focus are authoritative; IMGUI ids can outlive a hidden field.
                if (groupsBindingError.Length > 0 || visible || menuShown || groupsMenuOpen || groupsCapturing != null) return false;
                Brix.Input.InputMode mode = Brix.Input.InputModeController.GetCurrentMode();
                if (mode == Brix.Input.InputMode.none || (mode & (Brix.Input.InputMode.pause | Brix.Input.InputMode.menu | Brix.Input.InputMode.worldEditor)) != 0) return false;
                if (Brix.Input.ControlContext.ContextIsActive("Chat") || TypingInTextField()) return false;
                // Menu/pause handlers can run later than this plugin in the same frame.
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F8)) return false;
                Rewired.Player player = Rewired.ReInput.players.GetPlayer("local");
                if (player == null || player.GetButtonDown("cam_pause_game")) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        // Does the game itself already use 1..4 (or Ctrl+1..4)? Written to the log once, and shown on screen for a few seconds.
        void ProbeKeyConflicts()
        {
            try
            {
                Rewired.Player player = Rewired.ReInput.players.GetPlayer("local"); if (player == null) return;
                StringBuilder found = new StringBuilder();
                foreach (Rewired.ControllerMap map in player.controllers.maps.GetMaps(Rewired.ControllerType.Keyboard, 0))
                    foreach (Rewired.ActionElementMap em in map.AllMaps)
                    {
                        KeyCode kc = em.keyCode; bool hit = false;
                        for (int i = 0; i < GroupCount; i++) if (kc == groupKeys[i] || kc == groupPad[i]) hit = true;
                        if (!hit) continue; { Rewired.InputAction a0 = Rewired.ReInput.mapping.GetAction(em.actionId); if (a0 != null && a0.name != null && a0.name.StartsWith("we_")) continue; } // 0.3.97: world-editor menus own 1..6 only in editor mode (groups are off there), not a clash
                        Rewired.InputAction action = Rewired.ReInput.mapping.GetAction(em.actionId);
                        found.Append(kc).Append(em.modifierKey1 != Rewired.ModifierKey.None ? "+" + em.modifierKey1 : "").Append(" -> ").Append(action != null ? action.name : "?").Append("; ");
                    }
                if (found.Length > 0) { Logger.LogWarning("Unit groups: the game already binds these keys: " + found + "- if they clash, set [Groups] Enabled=false"); groupsToast = "Групи: клавіші 1-6 вже щось роблять у грі (див. журнал)"; groupsToastUntil = Time.realtimeSinceStartup + 6f; }
                else Logger.LogInfo("Unit groups: keys 1-6 are free in the game's keyboard map.");
            }
            catch (Exception e) { Logger.LogInfo("Unit groups: key check skipped: " + e.Message); }
        }

        string NativeGroupKeyWarning(KeyCode key)
        {
            if (key == KeyCode.None) return "";
            try
            {
                Rewired.Player player = Rewired.ReInput.players.GetPlayer("local");
                if (player == null) return "Warning: game key map unavailable; conflict check skipped.";
                StringBuilder result = new StringBuilder();
                List<string> seen = new List<string>();
                foreach (Rewired.ControllerMap map in player.controllers.maps.GetMaps(Rewired.ControllerType.Keyboard, 0))
                    foreach (Rewired.ActionElementMap em in map.AllMaps)
                        if (em.keyCode == key)
                        {
                            Rewired.InputAction action = Rewired.ReInput.mapping.GetAction(em.actionId);
                            string actionName = action != null ? action.name : "unknown action";
                            if (seen.Contains(actionName)) continue; seen.Add(actionName);
                            if (result.Length > 0) result.Append("; ");
                            result.Append(actionName);
                        }
                return result.Length > 0 ? "Warning: " + KeyLabel(key) + " is already bound by the game: " + result : "";
            }
            catch (Exception e) { return "Warning: game key check unavailable: " + e.Message; }
        }

        static bool TypingInTextField()
        {
            try
            {
                UnityEngine.EventSystems.EventSystem es = UnityEngine.EventSystems.EventSystem.current;
                return es != null && es.currentSelectedGameObject != null && es.currentSelectedGameObject.GetComponentInParent<UnityEngine.UI.InputField>() != null;
            }
            catch (Exception) { return true; }
        }

        void PushGroupHistory()
        {
            List<GameObject>[] snap = new List<GameObject>[GroupCount];
            for (int i = 0; i < GroupCount; i++) snap[i] = new List<GameObject>(groups[i]);
            groupHistory.Add(snap); if (groupHistory.Count > 20) groupHistory.RemoveAt(0);
        }

        // Ctrl+Z: go back to how the groups were before the last save / add / clear.
        void UndoGroups()
        {
            if (groupHistory.Count == 0) { groupsToast = "Групи: нічого скасовувати"; groupsToastUntil = Time.realtimeSinceStartup + 2f; return; }
            List<GameObject>[] snap = groupHistory[groupHistory.Count - 1]; groupHistory.RemoveAt(groupHistory.Count - 1);
            for (int i = 0; i < GroupCount; i++) { groups[i].Clear(); groups[i].AddRange(snap[i]); }
            RefreshGroupsLine(); groupsToast = "Групи: скасовано останню зміну (залишилось кроків: " + groupHistory.Count + ")"; groupsToastUntil = Time.realtimeSinceStartup + 2.5f;
        }

        // F8 -> "Додатково": switch, group size and quick actions (saved in the config).
        void DrawGroupsSettings()
        {
            if (groupsEnabled == null) return;
            bool on = GUILayout.Toggle(groupsEnabled.Value, "Групи бійців: Ctrl+1..6 зберегти виділених, 1..6 вибрати, Shift+Ctrl+N додати, Ctrl+Z скасувати");
            if (on != groupsEnabled.Value) groupsEnabled.Value = on;
            if (!on) return;
            if (groupsBindingError.Length > 0) GUILayout.Label("Shortcuts disabled: " + groupsBindingError + " Open Options -> Controls to fix.");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Бійців у групі: " + GroupLimit() + " (1-15)");
            if (GUILayout.Button("-", GUILayout.Width(28))) groupsMax.Value = Math.Max(1, GroupLimit() - 1);
            if (GUILayout.Button("+", GUILayout.Width(28))) groupsMax.Value = Math.Min(15, GroupLimit() + 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Очистити всі групи")) { PushGroupHistory(); for (int i = 0; i < GroupCount; i++) groups[i].Clear(); RefreshGroupsLine(); groupsToast = "Усі групи очищено (Ctrl+Z поверне)"; groupsToastUntil = Time.realtimeSinceStartup + 3f; }
            if (GUILayout.Button("Скасувати останню зміну")) UndoGroups();
            GUILayout.EndHorizontal();
        }

        int GroupLimit() { int m = groupsMax != null ? groupsMax.Value : 15; return m < 1 ? 1 : (m > 15 ? 15 : m); }

        static bool UnitAlive(GameObject go)
        {
            try { return go != null && go.GetComponent<Labor>() != null && UIGameSelector.IsSelectable(go); } catch (Exception) { return false; }
        }

        void AssignGroup(int index, bool add)
        {
            PushGroupHistory(); groupScratch.Clear(); UIGameSelector.GetSelectedOfType(ref groupScratch, SelectionType.Unit);
            List<GameObject> g = groups[index]; if (!add) g.Clear(); else g.RemoveAll(delegate(GameObject o) { return !UnitAlive(o); });
            int limit = GroupLimit(), taken = 0, skipped = 0;
            foreach (GameObject go in groupScratch)
            {
                if (go == null || go.GetComponent<Labor>() == null || g.Contains(go)) continue;
                if (g.Count >= limit) { skipped++; continue; }
                g.Add(go); taken++;
            }
            groupsToast = groupScratch.Count == 0 ? (add ? "Група " + (index + 1) + ": нікого не виділено, нічого не додано" : "Група " + (index + 1) + " очищена") : "Група " + (index + 1) + (add ? ": додано " : ": збережено ") + taken + " (всього " + g.Count + ")" + (skipped > 0 ? "; ще " + skipped + " не влізло (максимум " + limit + ")" : "");
            groupsToastUntil = Time.realtimeSinceStartup + 3f; RefreshGroupsLine();
        }

        void RecallGroup(int index, float now)
        {
            List<GameObject> g = groups[index]; g.RemoveAll(delegate(GameObject o) { return !UnitAlive(o); });
            if (g.Count == 0) { groupsToast = "Група " + (index + 1) + " порожня (Ctrl+" + (index + 1) + " зберігає виділених)"; groupsToastUntil = now + 2.5f; return; }
            UIGameSelector.Select(new List<GameObject>(g));
            groupsToast = "Група " + (index + 1) + ": " + g.Count; groupsToastUntil = now + 1.5f; groupsLastKey = index; groupsLastTap = now; RefreshGroupsLine();
        }

        // 0.3.97: not on the loading screen (the loader invokes TerrainReady while the "Входимо в світ" step is still shown, so wait for LevelReady and SoundEngineReady, the last loader steps) and not in the map editor (Architecte.editorMode).
        static bool GroupsMatchReady()
        {
            try { return !Brix.Engine.Architecte.editorMode && Brix.Engine.GameSignals.Received(Brix.Engine.GameSignals.LevelReady) && Brix.Engine.GameSignals.Received(Brix.Engine.GameSignals.SoundEngineReady); } catch (Exception) { return true; }
        }

        void RefreshGroupsLine()
        {
            StringBuilder sb = new StringBuilder("Групи: ");
            for (int i = 0; i < GroupCount; i++)
            {
                List<GameObject> g = groups[i]; if (g == null) continue; g.RemoveAll(delegate(GameObject o) { return !UnitAlive(o); });
                sb.Append('[').Append(i + 1).Append("] ").Append(g.Count == 0 ? "-" : g.Count.ToString()).Append("   ");
                int sel = 0; try { for (int k = 0; k < g.Count; k++) if (UIGameSelector.IsSelected(g[k])) sel++; } catch (Exception) { }
                groupsSelCount[i] = sel;
                groupCellText[i] = (i + 1) + (g.Count > 0 ? (g.Count >= 10 ? ":" : ": ") + g.Count : ""); groupTagText[i] = (i + 1).ToString();
            }
            groupsLine = sb.ToString();
        }

        GUIStyle groupsCellStyle, groupsTagStyle; readonly int[] groupsSelCount = new int[GroupCount]; readonly string[] groupCellText = new string[GroupCount], groupTagText = new string[GroupCount]; Camera groupsCam;
        // 0.3.97: the game's own look (black panels, gold text), bottom-right corner just above the native "Ближній бій / Дальній бій" panel (about 50 px from the right edge,
        // panel top about 72 px above the bottom): a small caption and one black square per group (number and units), measured from the screen corner.
        void DrawUnitGroups()
        {
            if (groupsEnabled == null || !groupsEnabled.Value || !groupsScene) return;
            if (Event.current.type != EventType.Repaint) return;   // drawing only on Repaint: OnGUI also runs for Layout and input events
            try { Brix.Input.InputMode mode = Brix.Input.InputModeController.GetCurrentMode(); if ((mode & (Brix.Input.InputMode.pause | Brix.Input.InputMode.menu)) != 0) return; } catch (Exception) { }
            if (groupsTagStyle == null) { groupsTagStyle = new GUIStyle(GUI.skin.label); groupsTagStyle.alignment = TextAnchor.MiddleCenter; groupsTagStyle.fontStyle = FontStyle.Bold; groupsTagStyle.fontSize = 12; }
            if (groupsCellStyle == null) { groupsCellStyle = new GUIStyle(GUI.skin.label); groupsCellStyle.alignment = TextAnchor.MiddleCenter; groupsCellStyle.fontStyle = FontStyle.Bold; groupsCellStyle.fontSize = 13; }
            const float cell = 40f, cap = 16f, high = 26f; float w = cell * GroupCount;
            Rect top = new Rect(Screen.width - 50f - w, Screen.height - 76f - cap - high, w - 2f, cap);
            Color gold = new Color(1f, 0.85f, 0.3f, 1f), old = GUI.color;
            GUI.color = new Color(0.09f, 0.09f, 0.09f, 1f); GUI.DrawTexture(top, Texture2D.whiteTexture);
            GUI.color = gold; GUI.Label(top, "ГРУПИ", groupsCellStyle);
            for (int i = 0; i < GroupCount; i++)
            {
                Rect c = new Rect(top.x + i * cell, top.yMax + 1f, cell - 2f, high);
                int n = groups[i] == null ? 0 : groups[i].Count;
                int sel = groupsSelCount[i]; bool whole = n > 0 && sel >= n, part = sel > 0 && !whole;
                // selected group: gold square with dark text; part of it selected: gold frame; otherwise the plain black square
                GUI.color = whole ? new Color(1f, 0.85f, 0.3f, 0.95f) : new Color(0.09f, 0.09f, 0.09f, 1f); GUI.DrawTexture(c, Texture2D.whiteTexture);
                if (part) { GUI.color = gold; GUI.DrawTexture(new Rect(c.x, c.y, c.width, 2f), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(c.x, c.yMax - 2f, c.width, 2f), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(c.x, c.y, 2f, c.height), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(c.xMax - 2f, c.y, 2f, c.height), Texture2D.whiteTexture); }
                GUI.color = whole ? new Color(0.08f, 0.06f, 0f, 1f) : (n > 0 ? gold : new Color(0.6f, 0.55f, 0.35f, 1f)); GUI.Label(c, groupCellText[i] ?? "", groupsCellStyle);
            }
            // a small group number over every fighter that belongs to a group; a fighter that is selected right now gets a gold square behind it
            if (groupsCam == null) groupsCam = Camera.main;
            Camera cam = groupsCam;
            if (cam != null)
            {
                for (int i = 0; i < GroupCount; i++)
                {
                    List<GameObject> g = groups[i]; if (g == null) continue;
                    for (int k = 0; k < g.Count && k < 15; k++)
                    {
                        GameObject u = g[k]; if (u == null) continue;
                        Vector3 sp = cam.WorldToScreenPoint(u.transform.position + Vector3.up * 2.4f);
                        if (sp.z <= 0f) continue;
                        Rect tag = new Rect(sp.x - 9f, Screen.height - sp.y - 9f, 18f, 18f);
                        bool picked = false; try { picked = UIGameSelector.IsSelected(u); } catch (Exception) { }
                        GUI.color = picked ? new Color(1f, 0.85f, 0.3f, 0.95f) : new Color(0f, 0f, 0f, 0.75f); GUI.DrawTexture(tag, Texture2D.whiteTexture);
                        GUI.color = picked ? new Color(0.08f, 0.06f, 0f, 1f) : gold; GUI.Label(tag, groupTagText[i] ?? "", groupsTagStyle);
                    }
                }
            }
            GUI.color = old;
            if (Time.realtimeSinceStartup < groupsToastUntil && groupsToast.Length > 0)
            {
                Rect t = new Rect(Screen.width * 0.5f - 220f, Screen.height - 50f, 440f, 22f);
                GUI.color = new Color(0f, 0f, 0f, 0.7f); GUI.DrawTexture(t, Texture2D.whiteTexture); GUI.color = Color.white; GUI.Label(t, groupsToast); GUI.color = old;
            }
        }
    }
}
