using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.UI.Builder.Menu;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

namespace CastleStorySteam
{
    public sealed partial class SteamBridgePlugin
    {
        // Native visual components only. Never instantiate Containee, Rewired or prefab listeners.
        bool groupsMenuOpen, menuShown, gmDirty;
        float nextMenuProbe, nextMenuError;
        LegacyKeybindingsCategoriesMenu catMenu;
        LegacyBaseMenu.Operation lastMenuOp;
        Container bindingsList;
        ContainerBindingsPopulator gmPopulator;
        Rect menuTabRect, menuPanelRect, menuButtonRect, gmBuiltPanel, gmBuiltButton;
        GameObject gmRoot, gmPanel;
        Transform gmRows;
        Text gmNotice; Image gmNoticeBackground;
        GameObject gmPanelBar;
        Button gmTab;
        Text gmFooter, gmToggleText, gmLimitText, gmNativeTitle;
        string gmSavedTitle;
        readonly List<GroupKeyCell> gmCells = new List<GroupKeyCell>();
        readonly List<GroupBlock> gmBlocks = new List<GroupBlock>();
        readonly List<GroupTabState> gmNativeTabs = new List<GroupTabState>();
        readonly List<GroupGraphicState> gmHiddenGraphics = new List<GroupGraphicState>();
        readonly Vector3[] gmCorners = new Vector3[4];
        float gmScale = 1f;
        Containee gmTabTemplate, gmRowTemplate;
        Text gmNameTitle, gmNameSubtitle, gmPrimaryValue, gmAlternateValue; Button gmPrimaryTemplate, gmAlternateTemplate;
        Image gmNameTemplate;
        float gmRowHeight, gmRowPitch, gmKeyWidth, gmAltWidth;
        GameObject gmCategoryRoot;
        RectTransform gmCategoryContent;
        int gmScreenWidth, gmScreenHeight;
        readonly List<CategoryPlacement> gmCategoryPlacements = new List<CategoryPlacement>();
        readonly List<Button> gmCategoryListeners = new List<Button>();
        LayoutGroup gmCategoryLayout; bool gmCategoryLayoutEnabled;
        sealed class CategoryPlacement
        {
            internal RectTransform Rect; internal Transform Parent; internal int Index;
            internal Vector2 AnchorMin, AnchorMax, Pivot, Position, Size; internal Vector3 Scale;
            internal Rect ScreenButton;
        }

        sealed class GroupKeyCell
        {
            internal string Id; internal Text Value; internal Button Button; internal ColorBlock Colors;
        }
        sealed class GroupBlock
        {
            internal CanvasGroup Group; internal bool Created, Interactable, Raycasts; internal float Alpha;
        }
        sealed class GroupTabState
        {
            internal Button Button; internal ColorBlock Colors; internal LegacyBaseMenu.Operation Operation;
        }
        sealed class GroupGraphicState { internal Graphic Graphic; internal bool Enabled; }

        void GroupsMenuTick()
        {
            float now = Time.realtimeSinceStartup;
            try
            {
                if (groupsCapturing == "ma" || groupsCapturing == "md")
                {
                    GroupMod? held = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ? GroupMod.Ctrl : (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? GroupMod.Shift : (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ? (GroupMod?)GroupMod.Alt : null));
                    if (held != null) { SetCapturedGroupModifier(groupsCapturing, held.Value); groupsCapturing = null; SaveGroupBindings(); gmDirty = true; }
                }
                if (groupsCapturing != null && groupsCapturing != "ma" && groupsCapturing != "md") { KeyCode mk = PressedExtraMouse(); if (mk != KeyCode.None) CaptureMouseKey(groupsCapturing, mk); }
                if (gmRoot != null && (catMenu == null || !catMenu.gameObject.activeInHierarchy)) DestroyGroupsUI();
                if (groupsMenuOpen && (bindingsList == null || !bindingsList.gameObject.activeInHierarchy || LegacyKeybindingsCategoriesMenu.SelectedOperation != lastMenuOp)) CloseGroupsMenu();
                if (gmDirty && groupsMenuOpen) RefreshGroupsUI();
                if (GroupsMenuGate.Hooked && GroupsMenuGate.TakeWake()) nextMenuProbe = 0f;   // 0.4.24: the key-bindings menu was just enabled: probe at once
                if (now < nextMenuProbe) return;
                // 0.4.24: with the menu hooks in place and no such menu enabled or in use, the object scan (a frame hitch each time) runs only as a safety net instead of every 0.4 s
                bool relaxed = GroupsMenuGate.Hooked && !GroupsMenuGate.MenuMaybeEnabled && catMenu == null && gmRoot == null && !groupsMenuOpen;
                nextMenuProbe = now + (relaxed ? 30f : 0.4f);   // 0.4.27 review: was 5 s = 12 scans a minute in every scene on every PC, each one a 33-67 ms frame (T-195: one hitch per scan), although OnEnable already wakes the probe at once (GroupsMenuGate); 30 s keeps the safety net
                LegacyKeybindingsCategoriesMenu next = UnityEngine.Object.FindObjectOfType<LegacyKeybindingsCategoriesMenu>();
                if (catMenu != next) DestroyGroupsUI();
                catMenu = next;
                menuShown = catMenu != null && catMenu.gameObject.activeInHierarchy;
                if (!menuShown) return;
                if (gmRoot != null && (gmScreenWidth != Screen.width || gmScreenHeight != Screen.height)) { DestroyGroupsUI(); return; }
                menuTabRect = new Rect(); gmTabTemplate = null;
                foreach (Container.Item item in catMenu.TabsContainer.items)
                {
                    if (item == null || item.instance == null || item.instance.button == null || !item.instance.gameObject.activeInHierarchy || !item.containerPopulatorOperation.ToString().StartsWith("Keybindings_", StringComparison.Ordinal)) continue;
                    Rect b = CategoryRect(item.instance.button.transform as RectTransform);
                    if (b.width < 1f || b.height < 1f) continue;
                    if (gmTabTemplate == null) gmTabTemplate = item.instance;
                    menuTabRect = menuTabRect.width < 1f ? b : Rect.MinMaxRect(Math.Min(menuTabRect.xMin, b.xMin), Math.Min(menuTabRect.yMin, b.yMin), Math.Max(menuTabRect.xMax, b.xMax), Math.Max(menuTabRect.yMax, b.yMax));
                }
                Container previous = bindingsList;
                bindingsList = null; gmPopulator = null;
                foreach (ContainerBindingsPopulator list in UnityEngine.Object.FindObjectsOfType<ContainerBindingsPopulator>())
                {
                    if (!list.gameObject.activeInHierarchy || list.TargetOperation != LegacyKeybindingsCategoriesMenu.SelectedOperation) continue;
                    gmPopulator = list; bindingsList = list.selectionsContainer; break;
                }
                if (previous != bindingsList) DestroyGroupsUI();
                if (bindingsList == null || gmTabTemplate == null || bindingsList.items.Count == 0) return;
                gmRowTemplate = bindingsList.items[0].instance;
                if (gmRowTemplate == null) return;
                gmPrimaryTemplate = gmRowTemplate.transform.Find("Panel: Value/Primary").GetComponent<Button>();
                gmAlternateTemplate = gmRowTemplate.transform.Find("Panel: Value/Alternate").GetComponent<Button>();
                gmNameTitle = gmRowTemplate.transform.Find("Name/Text: Title").GetComponent<Text>();
                gmNameSubtitle = gmRowTemplate.transform.Find("Name/Text: Subtitle").GetComponent<Text>();
                gmPrimaryValue = gmPrimaryTemplate.transform.Find("Keycode").GetComponent<Text>();
                gmAlternateValue = gmAlternateTemplate.transform.Find("Keycode").GetComponent<Text>();
                Rect row = ScreenRectOf(gmRowTemplate.transform as RectTransform);
                Rect viewport = ScreenRectOf(gmPopulator.scrollRect.transform as RectTransform);
                menuPanelRect = new Rect(row.x, viewport.y, row.width, Math.Min(viewport.height, Screen.height - viewport.y));
                Rect tab = CategoryRect(gmTabTemplate.button.transform as RectTransform);
                menuButtonRect = new Rect(menuTabRect.x, menuTabRect.yMax + 2f, menuTabRect.width, tab.height);
                LegacyKeybindingsActionsMenu actions = UnityEngine.Object.FindObjectOfType<LegacyKeybindingsActionsMenu>();
                if (actions != null && actions.Container != null)
                    foreach (Container.Item item in actions.Container.items)
                    {
                        if (item == null || item.instance == null || item.instance.button == null || !item.instance.gameObject.activeInHierarchy) continue;
                        if (!menuButtonRect.Overlaps(ScreenRectOf(item.instance.button.transform as RectTransform))) continue;
                        // Preserve Apply/Back clearance at both supported resolutions.
                        float gap = menuPanelRect.x - menuTabRect.xMax - 16f;
                        if (gap > 40f) menuButtonRect = new Rect(menuTabRect.xMax + 8f, Math.Min(menuButtonRect.y, Screen.height - tab.height - 8f), Math.Min(menuTabRect.width, gap), tab.height);
                        break;
                    }
                if (gmRoot != null && (gmBuiltPanel != menuPanelRect || gmBuiltButton != menuButtonRect)) { DestroyGroupsUI(); return; }
                if (gmRoot == null) BuildGroupsUI();
            }
            catch (Exception e) { DestroyGroupsUI(); menuShown = false; GroupsMenuError("probe", e); }
        }

        Rect ScreenRectOf(RectTransform rt)
        {
            if (rt == null) return new Rect();
            rt.GetWorldCorners(gmCorners);
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, gmCorners[0]), b = RectTransformUtility.WorldToScreenPoint(cam, gmCorners[2]);
            return new Rect(Math.Min(a.x, b.x), Screen.height - Math.Max(a.y, b.y), Math.Abs(b.x - a.x), Math.Abs(b.y - a.y));
        }

        RectTransform GroupRect(string name, Transform parent, Rect rect)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform; rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(rect.x / gmScale, -rect.y / gmScale);
            rt.sizeDelta = new Vector2(rect.width / gmScale, rect.height / gmScale);
            return rt;
        }

        static void CopyGroupImage(Image from, Image to)
        {
            to.sprite = from.sprite; to.overrideSprite = from.overrideSprite; to.type = from.type;
            to.color = from.color; to.material = from.material; to.preserveAspect = from.preserveAspect;
            to.fillCenter = from.fillCenter; to.fillMethod = from.fillMethod; to.fillAmount = from.fillAmount;
            to.fillClockwise = from.fillClockwise; to.fillOrigin = from.fillOrigin; to.raycastTarget = false;
        }

        Image GroupImage(Image template, Transform parent, Rect rect)
        {
            Image im = GroupRect(template.name, parent, rect).gameObject.AddComponent<Image>();
            CopyGroupImage(template, im); return im;
        }

        Text GroupText(Text template, Transform parent, Rect rect, string value)
        {
            Text t = GroupRect(template.name, parent, rect).gameObject.AddComponent<Text>();
            t.font = template.font; t.fontSize = template.fontSize; t.fontStyle = template.fontStyle;
            t.color = template.color; t.alignment = template.alignment; t.lineSpacing = template.lineSpacing;
            t.supportRichText = template.supportRichText; t.horizontalOverflow = template.horizontalOverflow;
            t.verticalOverflow = template.verticalOverflow; t.resizeTextForBestFit = template.resizeTextForBestFit;
            t.resizeTextMinSize = template.resizeTextMinSize; t.resizeTextMaxSize = template.resizeTextMaxSize;
            t.material = template.material; t.raycastTarget = false; t.text = value; return t;
        }

        // Bake current native geometry; only Image/Text are copied. No inherited events or scripts.
        Button GroupButton(Button template, Transform parent, Rect target, out Dictionary<Transform, Transform> map)
        {
            map = new Dictionary<Transform, Transform>();
            Rect original = ScreenRectOf(template.transform as RectTransform);
            RectTransform root = GroupRect("Bridge " + template.name, parent, target);
            map.Add(template.transform, root);
            foreach (Graphic graphic in template.GetComponentsInChildren<Graphic>(false))
            {
                Rect r = ScreenRectOf(graphic.rectTransform);
                Rect local = new Rect((r.x - original.x) * target.width / original.width, (r.y - original.y) * target.height / original.height, r.width * target.width / original.width, r.height * target.height / original.height);
                Transform dst;
                if (graphic.transform == template.transform) dst = root;
                else { dst = GroupRect(graphic.name, root, local); map[graphic.transform] = dst; }
                Image image = graphic as Image; Text text = graphic as Text;
                if (image != null) CopyGroupImage(image, dst.gameObject.AddComponent<Image>());
                if (text != null)
                {
                    Text copy = GroupText(text, root, local, text.text);
                    if (dst != root) { UnityEngine.Object.Destroy(dst.gameObject); }
                    map[graphic.transform] = copy.transform;
                }
            }
            Button button = root.gameObject.AddComponent<Button>();
            button.transition = template.transition; button.colors = template.colors; button.spriteState = template.spriteState;
            Transform targetGraphic;
            if (template.targetGraphic == null || !map.TryGetValue(template.targetGraphic.transform, out targetGraphic)) throw new InvalidOperationException("Native button has no supported target graphic");
            button.targetGraphic = targetGraphic.GetComponent<Graphic>(); button.targetGraphic.raycastTarget = true;
            Navigation nav = button.navigation; nav.mode = Navigation.Mode.None; button.navigation = nav;
            return button;
        }

        void BuildGroupsUI()
        {
            Canvas sourceCanvas = catMenu.GetComponentInParent<Canvas>().rootCanvas;
            gmScale = sourceCanvas.scaleFactor;
            gmRoot = new GameObject("Steam Bridge Controls", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            // Independent root canvas: native root transforms may animate/offset their children.
            Canvas canvas = gmRoot.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sourceCanvas.sortingOrder + 1; canvas.scaleFactor = gmScale; canvas.pixelPerfect = sourceCanvas.pixelPerfect; canvas.referencePixelsPerUnit = sourceCanvas.referencePixelsPerUnit;
            Dictionary<Transform, Transform> map;
            gmTab = GroupButton(gmTabTemplate.button, gmRoot.transform, menuButtonRect, out map);
            Text title = map[gmTabTemplate.title.transform].GetComponent<Text>(); title.text = "ГРУПИ БІЙЦІВ";
            Text subtitle = map[gmTabTemplate.subtitle.transform].GetComponent<Text>(); subtitle.text = "Вибір та збереження груп";
            // The native one-line tab's font/insets and full two-line hierarchy.
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            gmTab.onClick.AddListener(delegate { if (groupsMenuOpen) CloseGroupsMenu(); else OpenGroupsMenu(); });
            ColorBlock tabColors = gmTab.colors; tabColors.normalColor = catMenu.DefaultNormalColor; tabColors.highlightedColor = catMenu.DefaultHighlightColor; gmTab.colors = tabColors;
            gmBuiltPanel = menuPanelRect; gmBuiltButton = menuButtonRect;
            gmScreenWidth = Screen.width; gmScreenHeight = Screen.height;
            foreach (Container.Item item in catMenu.TabsContainer.items)
                if (item != null && item.instance != null && item.instance.button != null)
                { item.instance.button.onClick.AddListener(CloseGroupsMenu); gmCategoryListeners.Add(item.instance.button); }
            BuildCategoryScroll();
            Logger.LogInfo("Groups native UI: viewport=" + menuPanelRect + " tab=" + menuButtonRect + " font=" + title.font.name + " size=" + title.fontSize + " pixelPerfect=" + canvas.pixelPerfect + " scale=" + gmScale);
        }

        void BuildCategoryScroll()
        {
            float bottom = Screen.height - 8f;
            LegacyKeybindingsActionsMenu actions = UnityEngine.Object.FindObjectOfType<LegacyKeybindingsActionsMenu>();
            if (actions != null && actions.Container != null)
                foreach (Container.Item item in actions.Container.items)
                    if (item != null && (item.containerPopulatorOperation == LegacyBaseMenu.Operation.Apply || item.containerPopulatorOperation == LegacyBaseMenu.Operation.Revert || item.containerPopulatorOperation == LegacyBaseMenu.Operation.OptionsBackTitle || item.containerPopulatorOperation == LegacyBaseMenu.Operation.OptionsBackInGame) && item.instance != null && item.instance.button != null && item.instance.gameObject.activeInHierarchy)
                        bottom = Math.Min(bottom, ScreenRectOf(item.instance.button.transform as RectTransform).y - 8f);
            // The established spare-column Groups placement is outside the left list.
            // Include Groups only when its existing placement is in that list's column.
            bool groupInColumn = menuButtonRect.xMin < menuTabRect.xMax && menuButtonRect.xMax > menuTabRect.xMin;
            Rect bounds = groupInColumn ? Rect.MinMaxRect(Math.Min(menuTabRect.xMin, menuButtonRect.xMin), menuTabRect.yMin, Math.Max(menuTabRect.xMax, menuButtonRect.xMax), Math.Max(menuTabRect.yMax, menuButtonRect.yMax)) : menuTabRect;
            float preferredHeight = 0f; int count = 0;
            VerticalLayoutGroup nativeLayout = catMenu.TabsContainer.GetComponent<VerticalLayoutGroup>();
            float spacing = nativeLayout != null ? nativeLayout.spacing * gmScale : 2f;
            foreach (Container.Item item in catMenu.TabsContainer.items)
                if (IsCategory(item)) { preferredHeight += Math.Max(ScreenRectOf(item.instance.transform as RectTransform).height, LayoutUtility.GetPreferredHeight(item.instance.transform as RectTransform) * gmScale); count++; }
            preferredHeight += Math.Max(0, count - 1) * spacing;
            if (groupInColumn) preferredHeight += menuButtonRect.height + 2f;
            Logger.LogInfo("Category fit: current=" + bounds.height + " preferred=" + preferredHeight + " available=" + (bottom - bounds.yMin));
            if (bounds.y + Math.Max(bounds.height, preferredHeight) <= bottom + 0.5f) return;
            bounds.height = Math.Max(bounds.height, preferredHeight);
            float height = Math.Max(40f, bottom - bounds.yMin);
            RectTransform root = GroupRect("Category scroll (overflow only)", gmRoot.transform, new Rect(bounds.x, bounds.y, bounds.width, height));
            gmCategoryRoot = root.gameObject;
            Image hit = root.gameObject.AddComponent<Image>(); hit.color = Color.clear; hit.raycastTarget = true;
            root.gameObject.AddComponent<RectMask2D>();
            ScrollRect scrollRect = root.gameObject.AddComponent<ScrollRect>();
            gmCategoryContent = GroupRect("Category content", root, new Rect(0, 0, bounds.width, bounds.height));
            gmCategoryLayout = nativeLayout;
            if (gmCategoryLayout != null) { gmCategoryLayoutEnabled = gmCategoryLayout.enabled; gmCategoryLayout.enabled = false; }
            float y = 0f;
            foreach (Container.Item item in catMenu.TabsContainer.items)
            {
                if (!IsCategory(item)) continue;
                RectTransform rt = item.instance.transform as RectTransform;
                Rect oldRect = ScreenRectOf(rt);
                float naturalHeight = Math.Max(oldRect.height, LayoutUtility.GetPreferredHeight(rt) * gmScale);
                gmCategoryPlacements.Add(new CategoryPlacement { Rect = rt, Parent = rt.parent, Index = rt.GetSiblingIndex() + gmCategoryPlacements.Count, AnchorMin = rt.anchorMin, AnchorMax = rt.anchorMax, Pivot = rt.pivot, Position = rt.anchoredPosition, Size = rt.sizeDelta, Scale = rt.localScale, ScreenButton = oldRect });
                rt.SetParent(gmCategoryContent, true);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2((oldRect.x - bounds.x) / gmScale, -y / gmScale);
                rt.sizeDelta = new Vector2(oldRect.width / gmScale, naturalHeight / gmScale);
                y += naturalHeight + spacing;
            }
            if (groupInColumn)
            {
                RectTransform rt = gmTab.transform as RectTransform; rt.SetParent(gmCategoryContent, false);
                rt.anchoredPosition = new Vector2((menuButtonRect.x - bounds.x) / gmScale, -y / gmScale);
            }
            scrollRect.content = gmCategoryContent; scrollRect.viewport = root; scrollRect.horizontal = false; scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped; scrollRect.inertia = gmPopulator.scrollRect.inertia;
            scrollRect.decelerationRate = gmPopulator.scrollRect.decelerationRate; scrollRect.scrollSensitivity = gmPopulator.scrollRect.scrollSensitivity;
            // Borrow native artwork and transitions, never clone native scripts or listeners.
            Scrollbar source = gmPopulator.scrollBar;
            if (source == null) source = gmPopulator.scrollRect.verticalScrollbar;
            if (source == null) throw new InvalidOperationException("Native scrollbar unavailable");
            float width = ScreenRectOf(source.transform as RectTransform).width; if (width < 4f) width = 12f * gmScale;
            RectTransform barRoot = GroupRect("Category scrollbar", gmRoot.transform, new Rect(bounds.xMax + 3f, bounds.y, width, height));
            // Keep the scrollbar in the same cleanup root, outside the viewport mask.
            gmCategoryBar = barRoot.gameObject;
            Image bg = source.GetComponent<Image>(); Image track = barRoot.gameObject.AddComponent<Image>();
            if (bg != null) CopyGroupImage(bg, track); else track.color = Color.clear; track.raycastTarget = true;
            RectTransform slide = GroupRect("Sliding Area", barRoot, new Rect(0, 0, width, height));
            RectTransform handle = GroupRect("Handle", slide, new Rect(0, 0, width, height));
            handle.offsetMin = handle.offsetMax = Vector2.zero;
            Image nativeHandle = source.handleRect.GetComponent<Image>();
            Image handleImage = handle.gameObject.AddComponent<Image>(); CopyGroupImage(nativeHandle, handleImage); handleImage.raycastTarget = true;
            Scrollbar bar = barRoot.gameObject.AddComponent<Scrollbar>(); bar.handleRect = handle; bar.targetGraphic = handleImage;
            bar.direction = Scrollbar.Direction.BottomToTop; bar.transition = source.transition; bar.colors = source.colors; bar.spriteState = source.spriteState;
            scrollRect.verticalScrollbar = bar; scrollRect.verticalNormalizedPosition = 1f;
            Logger.LogInfo("Category overflow: bounds=" + bounds + " viewportHeight=" + height + " native order/positions preserved");
        }
        GameObject gmCategoryBar;

        bool IsCategory(Container.Item item)
        {
            return item != null && item.instance != null && item.instance.button != null && item.instance.gameObject.activeInHierarchy && item.containerPopulatorOperation.ToString().StartsWith("Keybindings_", StringComparison.Ordinal);
        }
        Rect CategoryRect(RectTransform rt)
        {
            foreach (CategoryPlacement state in gmCategoryPlacements) if (state.Rect == rt) return state.ScreenButton;
            return ScreenRectOf(rt);
        }

        void RestoreCategoryScroll()
        {
            // Restore before destroying owned content; native objects must never be destroyed with it.
            foreach (CategoryPlacement state in gmCategoryPlacements)
            {
                RectTransform rt = state.Rect; if (rt == null || state.Parent == null) continue;
                rt.SetParent(state.Parent, false); rt.SetSiblingIndex(state.Index);
                rt.anchorMin = state.AnchorMin; rt.anchorMax = state.AnchorMax; rt.pivot = state.Pivot;
                rt.anchoredPosition = state.Position; rt.sizeDelta = state.Size; rt.localScale = state.Scale;
            }
            gmCategoryPlacements.Clear();
            if (gmCategoryLayout != null) gmCategoryLayout.enabled = gmCategoryLayoutEnabled;
            gmCategoryLayout = null;
            if (gmCategoryRoot != null) { gmCategoryRoot.SetActive(false); UnityEngine.Object.Destroy(gmCategoryRoot); }
            if (gmCategoryBar != null) { gmCategoryBar.SetActive(false); UnityEngine.Object.Destroy(gmCategoryBar); }
            gmCategoryRoot = gmCategoryBar = null; gmCategoryContent = null;
        }

        void BlockGroupsNative(GameObject go)
        {
            foreach (GroupBlock old in gmBlocks) if (old.Group != null && old.Group.gameObject == go) return;
            CanvasGroup cg = go.GetComponent<CanvasGroup>();
            GroupBlock state = new GroupBlock(); state.Created = cg == null;
            if (cg == null) cg = go.AddComponent<CanvasGroup>();
            state.Group = cg; state.Alpha = cg.alpha; state.Interactable = cg.interactable; state.Raycasts = cg.blocksRaycasts;
            gmBlocks.Add(state); cg.alpha = 0f; cg.interactable = false; cg.blocksRaycasts = false;
        }

        void BlockGroupsScrollbar(Scrollbar scrollbar)
        {
            if (scrollbar == null) return;
            BlockGroupsNative(scrollbar.gameObject);
            foreach (Graphic graphic in scrollbar.GetComponentsInChildren<Graphic>(true))
            {
                bool found = false;
                foreach (GroupGraphicState old in gmHiddenGraphics) if (old.Graphic == graphic) { found = true; break; }
                if (found) continue;
                gmHiddenGraphics.Add(new GroupGraphicState { Graphic = graphic, Enabled = graphic.enabled });
                graphic.enabled = false;
            }
        }

        void OpenGroupsMenu()
        {
            if (groupsMenuOpen || bindingsList == null || gmRoot == null) return;
            lastMenuOp = LegacyKeybindingsCategoriesMenu.SelectedOperation;
            try
            {
                BlockGroupsNative(bindingsList.gameObject);
                BlockGroupsScrollbar(gmPopulator.scrollBar);
                // Some native categories retain a reference to another category's scrollbar.
                // Resolve actual sibling scrollbars from the active list, once on opening.
                Transform scrollRoot = bindingsList.transform.parent;
                if (scrollRoot != null) scrollRoot = scrollRoot.parent;
                if (scrollRoot != null)
                    foreach (Scrollbar scrollbar in scrollRoot.GetComponentsInChildren<Scrollbar>(false)) BlockGroupsScrollbar(scrollbar);
                foreach (Container.Item item in catMenu.TabsContainer.items)
                {
                    if (item == null || item.instance == null || item.instance.button == null) continue;
                    Button b = item.instance.button;
                    gmNativeTabs.Add(new GroupTabState { Button = b, Colors = b.colors, Operation = item.containerPopulatorOperation });
                    ColorBlock colors = b.colors; colors.normalColor = catMenu.DefaultNormalColor; colors.highlightedColor = catMenu.DefaultHighlightColor; b.colors = colors;
                }
                // Locate once, outside OnGUI. The native category owns this title.
                foreach (Text t in gmPopulator.transform.parent.GetComponentsInChildren<Text>(false))
                {
                    Rect r = ScreenRectOf(t.rectTransform);
                    if (r.yMax <= menuPanelRect.y && r.y > 100f && r.width > menuPanelRect.width && t.fontSize > gmTabTemplate.title.fontSize) { gmNativeTitle = t; gmSavedTitle = t.text; t.text = "ГРУПИ БІЙЦІВ"; break; }
                }
                groupsMenuOpen = true;
                ColorBlock tabColors = gmTab.colors; tabColors.normalColor = catMenu.SelectedNormalColor; tabColors.highlightedColor = catMenu.SelectedHighlightColor; gmTab.colors = tabColors;
                BuildGroupsPanel(); RefreshGroupsUI();
            }
            catch (Exception e) { CloseGroupsMenu(); GroupsMenuError("open", e); }
        }

        void BuildGroupsPanel()
        {
            gmPanel = GroupRect("Groups panel", gmRoot.transform, menuPanelRect).gameObject;
            // Transparent hit surface blocks the native scroll area without painting a backdrop.
            Image hit = gmPanel.AddComponent<Image>(); hit.color = Color.clear; hit.raycastTarget = true;
            gmPanel.AddComponent<RectMask2D>();
            gmRows = gmPanel.transform;
            Rect row = ScreenRectOf(gmRowTemplate.transform as RectTransform);
            gmRowHeight = row.height; gmRowPitch = row.height + 1f;
            if (bindingsList.items.Count > 1 && bindingsList.items[1].instance != null)
                gmRowPitch = ScreenRectOf(bindingsList.items[1].instance.transform as RectTransform).y - row.y;
            gmKeyWidth = ScreenRectOf(gmPrimaryTemplate.transform as RectTransform).width;
            gmAltWidth = ScreenRectOf(gmAlternateTemplate.transform as RectTransform).width;
            gmNameTemplate = gmNameTitle.GetComponentInParent<Image>();
            float contentHeight = gmRowPitch * (GroupCount + 6) + 76f;
            if (contentHeight > menuPanelRect.height)
            {
                RectTransform content = GroupRect("Groups scroll content", gmPanel.transform, new Rect(0, 0, menuPanelRect.width, contentHeight));
                gmRows = content;
                ScrollRect scrollRect = gmPanel.AddComponent<ScrollRect>(); scrollRect.content = content; scrollRect.viewport = gmPanel.transform as RectTransform;
                scrollRect.horizontal = false; scrollRect.vertical = true; scrollRect.movementType = ScrollRect.MovementType.Clamped;
                scrollRect.scrollSensitivity = gmPopulator.scrollRect.scrollSensitivity; scrollRect.inertia = gmPopulator.scrollRect.inertia;
                Scrollbar native = gmPopulator.scrollBar != null ? gmPopulator.scrollBar : gmPopulator.scrollRect.verticalScrollbar;
                Scrollbar bar = CopyNativeScrollbar(native, new Rect(menuPanelRect.xMax + 3f, menuPanelRect.y, 16f, menuPanelRect.height), "Groups scrollbar");
                gmPanelBar = bar.gameObject; scrollRect.verticalScrollbar = bar; scrollRect.verticalNormalizedPosition = 1f;
            }

            for (int i = 0; i < GroupCount; i++)
            {
                GroupRowName(i, "ГРУПА " + (i + 1) + ": ВИБРАТИ");
                AddGroupKey(i, false, "g" + i); AddGroupKey(i, true, "p" + i);
            }
            GroupRowName(GroupCount, "ЗБЕРЕГТИ ВИДІЛЕНИХ", "Утримувати + клавішу групи"); AddGroupKey(GroupCount, false, "ma");
            GroupRowName(GroupCount + 1, "ДОДАТИ ДО ГРУПИ", "Утримувати разом зі збереженням"); AddGroupKey(GroupCount + 1, false, "md");
            GroupRowName(GroupCount + 2, "СКАСУВАТИ ЗМІНУ ГРУП", "Разом із клавішею збереження"); AddGroupKey(GroupCount + 2, false, "uz");
            gmToggleText = GroupRowAction(GroupCount + 3, "", delegate { if (groupsEnabled != null) groupsEnabled.Value = !groupsEnabled.Value; gmDirty = true; });
            GroupRowName(GroupCount + 4, "БІЙЦІВ У ГРУПІ");
            gmLimitText = GroupText(gmPrimaryValue, gmRows, new Rect(16f, (GroupCount + 4) * gmRowPitch + 28f, menuPanelRect.width - gmKeyWidth - gmAltWidth - 32f, 14f), "");
            GroupSmallAction(GroupCount + 4, false, "−", delegate { if (groupsMax != null) groupsMax.Value = Math.Max(1, GroupLimit() - 1); gmDirty = true; });
            GroupSmallAction(GroupCount + 4, true, "+", delegate { if (groupsMax != null) groupsMax.Value = Math.Min(15, GroupLimit() + 1); gmDirty = true; });   // 0.4.27 review: was Math.Min(6, ..): with the default limit of 15 the "+" button cut the limit down to 6 (the limit is 1..15 everywhere else: cfg text, F8 panel, GroupLimit)
            GroupRowAction(GroupCount + 5, "ПОВЕРНУТИ ТИПОВІ КЛАВІШІ", delegate { ResetGroupBindings(); groupsCapturing = null; gmDirty = true; });
            float footerY = gmRowPitch * (GroupCount + 6) + 12f;
            gmFooter = GroupText(gmTabTemplate.subtitle, gmRows, new Rect(16f, footerY, menuPanelRect.width - 32f, Math.Max(64f, menuPanelRect.height - footerY)), "");
            gmFooter.color = gmNativeTitle != null ? gmNativeTitle.color : gmTabTemplate.subtitle.color;
            gmFooter.horizontalOverflow = HorizontalWrapMode.Wrap; gmFooter.verticalOverflow = VerticalWrapMode.Truncate;
            gmNoticeBackground = GroupRect("Binding warning", gmPanel.transform, new Rect(0, menuPanelRect.height - 58f, menuPanelRect.width, 58f)).gameObject.AddComponent<Image>();
            gmNoticeBackground.color = new Color(0f, 0f, 0f, 0.88f); gmNoticeBackground.raycastTarget = false;
            gmNotice = GroupText(gmTabTemplate.subtitle, gmNoticeBackground.transform, new Rect(16, 4, menuPanelRect.width - 32f, 50), "");
            gmNotice.color = gmFooter.color; gmNotice.horizontalOverflow = HorizontalWrapMode.Wrap;
            gmNoticeBackground.gameObject.SetActive(false);
        }

        Scrollbar CopyNativeScrollbar(Scrollbar source, Rect rect, string name)
        {
            if (source == null) throw new InvalidOperationException("Native scrollbar unavailable");
            RectTransform root = GroupRect(name, gmRoot.transform, rect);
            Image bg = source.GetComponent<Image>(); Image image = root.gameObject.AddComponent<Image>();
            if (bg != null) CopyGroupImage(bg, image); else image.color = Color.clear; image.raycastTarget = true;
            RectTransform slide = GroupRect("Sliding Area", root, new Rect(0, 0, rect.width, rect.height));
            RectTransform handle = GroupRect("Handle", slide, new Rect(0, 0, rect.width, rect.height));
            handle.offsetMin = handle.offsetMax = Vector2.zero;
            Image imageHandle = handle.gameObject.AddComponent<Image>(); CopyGroupImage(source.handleRect.GetComponent<Image>(), imageHandle); imageHandle.raycastTarget = true;
            Scrollbar bar = root.gameObject.AddComponent<Scrollbar>(); bar.handleRect = handle; bar.targetGraphic = imageHandle;
            bar.direction = Scrollbar.Direction.BottomToTop; bar.transition = source.transition; bar.colors = source.colors; bar.spriteState = source.spriteState;
            return bar;
        }

        void GroupRowName(int row, string title) { GroupRowName(row, title, ""); }
        void GroupRowName(int row, string title, string subtitle)
        {
            float w = menuPanelRect.width - gmKeyWidth - gmAltWidth;
            Image name = GroupImage(gmNameTemplate, gmRows, new Rect(0, row * gmRowPitch, w, gmRowHeight));
            Rect sourceName = ScreenRectOf(gmNameTemplate.rectTransform), sourceTitle = ScreenRectOf(gmNameTitle.rectTransform);
            GroupText(gmNameTitle, name.transform, new Rect(sourceTitle.x - sourceName.x, sourceTitle.y - sourceName.y, w - 32f, sourceTitle.height), title);
            if (subtitle.Length > 0)
            {
                Rect sourceSub = ScreenRectOf(gmNameSubtitle.rectTransform);
                GroupText(gmNameSubtitle, name.transform, new Rect(sourceSub.x - sourceName.x, sourceSub.y - sourceName.y, w - 32f, sourceSub.height), subtitle);
            }
            // The modifier/undo rows retain the native alternate cell fill.
            if (row >= GroupCount)
            {
                Image empty = GroupImage(gmAlternateTemplate.GetComponent<Image>(), gmRows, new Rect(menuPanelRect.width - gmAltWidth, row * gmRowPitch, gmAltWidth, gmRowHeight));
                empty.color = gmAlternateTemplate.colors.normalColor;
            }
        }

        void AddGroupKey(int row, bool alternate, string id)
        {
            Button template = alternate ? gmAlternateTemplate : gmPrimaryTemplate;
            Dictionary<Transform, Transform> map;
            float w = alternate ? gmAltWidth : gmKeyWidth;
            Button b = GroupButton(template, gmRows, new Rect(menuPanelRect.width - gmAltWidth - (alternate ? 0f : gmKeyWidth), row * gmRowPitch, w, gmRowHeight), out map);
            Text nativeValue = alternate ? gmAlternateValue : gmPrimaryValue;
            Text value = map[nativeValue.transform].GetComponent<Text>();
            GroupKeyCell cell = new GroupKeyCell { Id = id, Value = value, Button = b, Colors = b.colors }; gmCells.Add(cell);
            b.onClick.AddListener(delegate { groupsCapturing = groupsCapturing == id ? null : id; groupsCaptureError = ""; gmDirty = true; });
        }

        Text GroupRowAction(int row, string title, UnityAction action)
        {
            Dictionary<Transform, Transform> map;
            Button b = GroupButton(gmTabTemplate.button, gmRows, new Rect(0f, row * gmRowPitch, menuPanelRect.width, gmRowHeight), out map);
            ColorBlock colors = b.colors; colors.normalColor = catMenu.DefaultNormalColor; colors.highlightedColor = catMenu.DefaultHighlightColor; b.colors = colors;
            foreach (Transform t in map.Values) { Graphic g = t.GetComponent<Graphic>(); if (g != null && g != b.targetGraphic) g.gameObject.SetActive(false); }
            Text label = GroupText(gmNameTitle, b.transform, new Rect(16f, 0f, menuPanelRect.width - 32f, gmRowHeight), title);
            label.alignment = TextAnchor.MiddleLeft; b.onClick.AddListener(action); return label;
        }

        void GroupSmallAction(int row, bool alternate, string label, UnityAction action)
        {
            Button template = alternate ? gmAlternateTemplate : gmPrimaryTemplate;
            Dictionary<Transform, Transform> map;
            float w = alternate ? gmAltWidth : gmKeyWidth;
            Button b = GroupButton(template, gmRows, new Rect(menuPanelRect.width - gmAltWidth - (alternate ? 0f : gmKeyWidth), row * gmRowPitch, w, gmRowHeight), out map);
            foreach (Transform t in map.Values) { Text text = t.GetComponent<Text>(); if (text != null) text.gameObject.SetActive(false); }
            Text tx = GroupText(gmPrimaryValue, b.transform, new Rect(16f, 0, w - 32f, gmRowHeight), label); tx.alignment = TextAnchor.MiddleCenter;
            b.onClick.AddListener(action);
        }

        void RefreshGroupsUI()
        {
            gmDirty = false;
            if (gmNoticeBackground != null) { gmNoticeBackground.gameObject.SetActive(groupsCaptureError.Length > 0); gmNotice.text = groupsCaptureError; }
            foreach (GroupKeyCell cell in gmCells)
            {
                string id = cell.Id; bool capture = groupsCapturing == id;
                KeyCode key = id == "uz" ? groupUndoKey : (id[0] == 'g' ? groupKeys[id[1] - '0'] : (id[0] == 'p' ? groupPad[id[1] - '0'] : KeyCode.None));
                cell.Value.text = capture ? "..." : (id == "ma" ? ModLabel(groupModAssign).ToUpper() : (id == "md" ? ModLabel(groupModAdd).ToUpper() : (key == KeyCode.None ? "<i>НЕМАЄ</i>" : KeyLabel(key))));
                ColorBlock colors = cell.Colors; if (capture) { colors.normalColor = colors.pressedColor; colors.highlightedColor = colors.pressedColor; } cell.Button.colors = colors;
            }
            if (gmToggleText != null) gmToggleText.text = groupsEnabled != null && groupsEnabled.Value ? "ГРУПИ: УВІМКНЕНО" : "ГРУПИ: ВИМКНЕНО";
            if (gmLimitText != null) gmLimitText.text = GroupLimit().ToString();
            if (gmFooter != null) gmFooter.text = groupsCaptureError.Length > 0 ? groupsCaptureError : (groupsBindingError.Length > 0 ? "Shortcuts disabled: " + groupsBindingError : (groupsCapturing != null ? "Натисни нову клавішу або кнопку миші (середня, назад, вперед). Модифікатори: Ctrl, Shift або Alt. Delete очищає альтернативну клавішу." : "Клікни по клавіші, щоб змінити. Групи локальні: у кожного гравця свої.\n" + ModLabel(groupModAssign) + " + клавіша групи — зберегти; без виділення — очистити."));
        }

        void CloseGroupsMenu()
        {
            groupsMenuOpen = false; groupsCapturing = null;
            if (gmPanelBar != null) { gmPanelBar.SetActive(false); UnityEngine.Object.Destroy(gmPanelBar); } gmPanelBar = null;
            if (gmPanel != null) { gmPanel.SetActive(false); UnityEngine.Object.Destroy(gmPanel); } gmPanel = null; gmCells.Clear();
            foreach (GroupGraphicState state in gmHiddenGraphics) if (state.Graphic != null) state.Graphic.enabled = state.Enabled;
            gmHiddenGraphics.Clear();
            foreach (GroupBlock state in gmBlocks)
            {
                try { if (state.Group != null) { state.Group.alpha = state.Alpha; state.Group.interactable = state.Interactable; state.Group.blocksRaycasts = state.Raycasts; if (state.Created) UnityEngine.Object.Destroy(state.Group); } } catch (Exception) { }
            }
            gmBlocks.Clear();
            foreach (GroupTabState state in gmNativeTabs)
            {
                if (state.Button == null) continue;
                ColorBlock colors = state.Colors;
                if (catMenu != null && lastMenuOp != LegacyKeybindingsCategoriesMenu.SelectedOperation)
                {
                    bool selected = state.Operation == LegacyKeybindingsCategoriesMenu.SelectedOperation;
                    colors.normalColor = selected ? catMenu.SelectedNormalColor : catMenu.DefaultNormalColor;
                    colors.highlightedColor = selected ? catMenu.SelectedHighlightColor : catMenu.DefaultHighlightColor;
                }
                state.Button.colors = colors;
            }
            gmNativeTabs.Clear();
            if (gmNativeTitle != null && lastMenuOp == LegacyKeybindingsCategoriesMenu.SelectedOperation) gmNativeTitle.text = gmSavedTitle;
            gmNativeTitle = null; gmSavedTitle = null;
            if (gmTab != null && catMenu != null) { ColorBlock colors = gmTab.colors; colors.normalColor = catMenu.DefaultNormalColor; colors.highlightedColor = catMenu.DefaultHighlightColor; gmTab.colors = colors; }
            gmFooter = gmToggleText = gmLimitText = null;
        }

        void DestroyGroupsUI()
        {
            CloseGroupsMenu();
            foreach (Button b in gmCategoryListeners) if (b != null) b.onClick.RemoveListener(CloseGroupsMenu);
            gmCategoryListeners.Clear(); RestoreCategoryScroll();
            if (gmRoot != null) { gmRoot.SetActive(false); UnityEngine.Object.Destroy(gmRoot); }
            gmRoot = null; gmTab = null;
            // No generated textures. Native sprites/fonts/materials are borrowed, never destroyed.
        }
        void ShutdownGroupsMenu() { DestroyGroupsUI(); }
        void OnDisable() { DestroyGroupsUI(); menuShown = false; }
        void OnLevelWasLoaded(int level) { DestroyGroupsUI(); menuShown = false; catMenu = null; bindingsList = null; nextMenuProbe = 0f; }
        void GroupsMenuError(string operation, Exception e)
        {
            if (Time.realtimeSinceStartup < nextMenuError) return;
            nextMenuError = Time.realtimeSinceStartup + 10f; Logger.LogWarning("Groups menu " + operation + " failed: " + e);
        }
        void DrawGroupsMenu()
        {
            // No component searches, reflection, style creation or string formatting in OnGUI.
            if (!groupsMenuOpen) return;
            try
            {
                HandleCapture();
            }
            catch (Exception e) { CloseGroupsMenu(); GroupsMenuError("capture", e); }
        }
        static KeyCode PressedExtraMouse()
        {
            for (KeyCode k = KeyCode.Mouse2; k <= KeyCode.Mouse6; k++) if (Input.GetKeyDown(k)) return k;
            return KeyCode.None;
        }
        // 0.3.97: the middle and side mouse buttons are valid group keys; Input.GetKeyDown sees them, the IMGUI KeyDown event does not
        void CaptureMouseKey(string id, KeyCode k)
        {
            gmDirty = true;
            string conflict = GroupBindingConflict(id, k);
            if (conflict.Length > 0) { groupsCaptureError = conflict; groupsCapturing = null; return; }
            if (id == "uz") groupUndoKey = k;
            else if (id[0] == 'g') groupKeys[id[1] - '0'] = k;
            else if (id[0] == 'p') groupPad[id[1] - '0'] = k;
            groupsCapturing = null; SaveGroupBindings();
            groupsCaptureError = k == KeyCode.Mouse2 ? "Середня кнопка миші в грі обертає камеру: при призначенні групи камера теж крутитиметься." : "";
        }

        void HandleCapture()
        {
            if (groupsCapturing == null) return;
            Event e = Event.current; if (e == null || e.type != EventType.KeyDown || e.keyCode == KeyCode.None) return;
            gmDirty = true; KeyCode k = e.keyCode; string id = groupsCapturing; e.Use();
            if (id == "ma" || id == "md")
            {
                GroupMod? m = (k == KeyCode.LeftControl || k == KeyCode.RightControl) ? GroupMod.Ctrl : ((k == KeyCode.LeftShift || k == KeyCode.RightShift) ? GroupMod.Shift : ((k == KeyCode.LeftAlt || k == KeyCode.RightAlt) ? GroupMod.Alt : (GroupMod?)null));
                if (m == null) return;                                         // wait for a real modifier
                SetCapturedGroupModifier(id, m.Value);
            }
            else
            {
                if (id[0] == 'p' && k == KeyCode.Delete) k = KeyCode.None;
                string conflict = GroupBindingConflict(id, k);
                if (conflict.Length > 0) { groupsCaptureError = conflict; groupsCapturing = null; return; }
                if (id == "uz") groupUndoKey = k;
                else if (id[0] == 'g') groupKeys[id[1] - '0'] = k;
                else if (id[0] == 'p') groupPad[id[1] - '0'] = k;
            }
            groupsCapturing = null; SaveGroupBindings();
            if (id != "ma" && id != "md")
            {
                groupsCaptureError = NativeGroupKeyWarning(k);
                if (groupsCaptureError.Length > 0) Logger.LogWarning(groupsCaptureError);
            }
        }
        void SetCapturedGroupModifier(string id, GroupMod modifier)
        {
            if (id == "ma") { if (modifier == groupModAdd) groupModAdd = groupModAssign; groupModAssign = modifier; }
            else { if (modifier == groupModAssign) groupModAssign = groupModAdd; groupModAdd = modifier; }
        }
    }
}
