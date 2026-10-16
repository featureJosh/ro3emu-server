using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Controller navigation for any stock panel, without per-panel code. The menu is the top-most open panel: the active
    /// UI_Sys_* / UI_Com_* view root (under GameMain/NodeRoot/UINode) with the highest canvas sorting order that is not part
    /// of the HUD and has something to press. Its Selectables (the game's buttons, toggles, sliders, dropdowns and input
    /// fields all derive from Selectable; InputBinderBase.m_button is one) are the focus targets.
    /// <list type="bullet">
    /// <item>D-pad / left stick: spatial focus move with hold-to-repeat; on a slider left/right changes the value.</item>
    /// <item>MenuConfirm (✕): pointer down/up/click on the focus, as a mouse click would.</item>
    /// <item>MenuBack (○): the game's own Esc (a tapped Escape key), which closes the top panel.</item>
    /// <item>MenuTabLeft / MenuTabRight (L1/R1): previous / next toggle of the panel's top-most toggle group (tab bars).</item>
    /// <item>Right stick: scrolls the scroll view around the focus (or the panel's largest); the focus is kept in view.</item>
    /// </list>
    /// While a menu is open the game's gamepad bindings are dropped except ClosePanel (Esc) (Controller.Sync), so ✕ does not also talk
    /// to an NPC behind the panel.
    /// </summary>
    public static class UiNav
    {
        // The HUD's own panels: always open, never a menu.
        private static readonly HashSet<string> Hud = new HashSet<string>
        {
            "UI_Sys_MainPanel(Clone)", "UI_Sys_SceneUIPanel(Clone)", "UI_Com_MainTipsPanel(Clone)", "UI_Sys_GMPanel(Clone)",
            "UI_Com_CastingBarPanel(Clone)", "UI_Com_CursorAttach(Clone)",
        };

        private static readonly List<Component> Items = new List<Component>();
        private static readonly Dictionary<string, string> LastFocus = new Dictionary<string, string>();
        private static Transform _panel;
        private static Component _focus, _beforeChange, _restore;
        private static int _frame;
        private static float _repeatAt;
        private static Vector2 _held;
        private static bool _escDown;
        private static Image[] _ring;

        /// <summary>The menu that has the pad ("" when none; the guide panel's name while a guide waits).</summary>
        public static string MenuName => _panel != null ? _panel.name : "";

        /// <summary>A menu is open and has the controller.</summary>
        public static bool Active => _panel != null;

        /// <summary>From Controller.Tick every frame while controller mode is on (pad may be null).</summary>
        internal static void Tick(Gamepad pad, bool enabled)
        {
            if (_escDown) { ReleaseEscape(); }
            if (!enabled || pad == null) { SkillBinding.Cancel(); Close(); return; }
            if (Diagnostics.Verbose && pad.buttonSouth.isPressed) Debug.Log("[Ro3Emu.UiNav] dbg south: panel=" + (_panel?.name ?? "-") + " guide=" + (_guide != null) + " osk=" + Osk.Open + " focus=" + (_focus?.name ?? "-") + " edge=" + Controller.Edge(pad.buttonSouth, true));
            if (++_frame % 10 == 0) Scan(); // ponytail: FindObjectsOfType<Canvas> every 10th frame; panel open/close events if it costs
            if (_panel == null) { SkillBinding.Cancel(); Close(); return; }
            if (SkillBinding.Active && !SkillBinding.InSkillPanel(FocusPanel)) SkillBinding.Cancel(); // the skill window is gone or covered
            if (Osk.Open)
            {
                HideRing();
                PromptList.Clear();
                Osk.Tick(pad, PromptList);
                Overlay.SetPrompts(PromptList);
                return;
            }
            if (_guide != null)
            {
                var hole = ScreenRect(_guide);
                if (Pressed(pad, "MenuConfirm", "buttonSouth")) ClickThrough(hole.center);
                DrawRing(hole);
                PromptList.Clear();
                PromptList.Add((Control("MenuConfirm", "buttonSouth"), "Continue"));
                Overlay.SetPrompts(PromptList);
                return;
            }
            if (_focus == null || !Usable(_focus)) _focus = DefaultFocus();
            Input(pad);
            if (_focus != null) DrawRing(ScreenRect(_focus)); else HideRing();
            Prompts();
        }

        private static void Close()
        {
            if (_panel == null && (_ring == null || !_ring[0].gameObject.activeSelf)) return;
            _panel = null;
            _focus = null;
            if (_ring != null) foreach (var r in _ring) r.gameObject.SetActive(false);
            Overlay.SetPrompts(null);
        }

        // ---- which panel, which items ----

        private static void Scan()
        {
            // Tutorial guide: a full-screen mask (GuideMaskRaycastFilter) with a hole over Rect_Img; only a click inside the
            // hole reaches the target, which a pad cannot do. It has no Selectables, so it is handled on its own.
            _guide = FindGuideTarget();
            if (_guide != null)
            {
                if (_panel == null || _panel.name != GuidePanel) Debug.Log("[Ro3Emu.UiNav] guide: target " + _guide.name);
                _panel = ViewRoot(_guide);
                _focus = null;
                Items.Clear();
                return;
            }
            Transform top = null;
            int topOrder = int.MinValue;
            Roots.Clear();
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (!canvas.isActiveAndEnabled || !canvas.isRootCanvas) continue; // one root canvas per opened view
                var root = ViewRoot(canvas.transform);
                if (root == null || Hud.Contains(root.name) || Roots.Contains(root)) continue;
                if (!HasItems(root)) continue;
                Roots.Add(root);
                int order = canvas.sortingLayerID * 100000 + canvas.sortingOrder;
                if (order <= topOrder) continue;
                top = root;
                topOrder = order;
            }
            if (top != _panel)
            {
                if (_panel != null && _focus != null) LastFocus[_panel.name] = _focus.name;
                // A popup over a window (item tips over the Backpack) and back: the focus returns to where it was.
                _restore = _beforeChange;
                _beforeChange = _focus;
                _panel = top;
                _focus = null;
                if (top != null) Debug.Log("[Ro3Emu.UiNav] menu: " + top.name);
            }
            Items.Clear();
            if (_panel == null) return;
            Collect(_panel, Items);
            // Side-by-side windows (Equip + Backpack) share one focus space; a modal popup keeps it to itself.
            if (!Modal(_panel)) foreach (var root in Roots) if (root != _panel) Collect(root, Items);
            if (_restore != null && Items.Contains(_restore)) _focus = _restore;
            _restore = null;
        }

        private static readonly List<Transform> Roots = new List<Transform>();
        private static readonly List<MonoBehaviour> Behaviours = new List<MonoBehaviour>();

        /// <summary>The panel's focus targets: Selectables, and other click handlers on a raycast graphic (item cells are
        /// UIDragButton, many links are ClickEvent; neither is a Selectable).
        /// ponytail: full GetComponentsInChildren every 6th frame; cache per panel if it shows in a profile.</summary>
        private static void Collect(Transform root, List<Component> into)
        {
            var skip = StockPrompts.For(root)?.NoFocus;
            foreach (var s in Selectable.allSelectablesArray)
                if (s.transform.IsChildOf(root) && Usable(s) && (skip == null || Array.IndexOf(skip, s.name) < 0)) into.Add(s);
            root.GetComponentsInChildren(false, Behaviours);
            foreach (var mb in Behaviours)
            {
                if (!(mb is IPointerClickHandler) || mb is Selectable || mb.GetComponent<Selectable>() != null) continue;
                if (mb.GetComponent<TMPro.TMP_Text>() != null) continue; // text with links (item tips title), not a control
                if (Small(mb) && Usable(mb) && (skip == null || Array.IndexOf(skip, mb.name) < 0)) into.Add(mb);
            }
        }

        /// <summary>A panel with a click-blocking graphic over (nearly) the whole screen is a modal popup.</summary>
        private static bool Modal(Transform root)
        {
            foreach (var g in root.GetComponentsInChildren<Graphic>(false))
            {
                if (!g.raycastTarget || !g.isActiveAndEnabled) continue;
                var r = ScreenRect(g);
                if (r.width >= Screen.width * 0.9f && r.height >= Screen.height * 0.9f) return true;
            }
            return false;
        }

        private const string GuidePanel = "UI_Sys_GuidePanel(Clone)";
        private static RectTransform _guide;

        /// <summary>The active guide's highlight (Rect_Img under UI_Sys_GuidePanel), or null.</summary>
        private static RectTransform FindGuideTarget()
        {
            var panel = GameObject.Find(GuidePanel);
            if (panel == null) return null;
            foreach (var t in panel.GetComponentsInChildren<RectTransform>(false))
                if (t.name == "Rect_Img" && t.rect.width > 4) return t;
            return null;
        }

        /// <summary>A mouse click at the screen point on the first thing under it that is not part of the guide panel.</summary>
        private static void ClickThrough(Vector2 point)
        {
            if (EventSystem.current == null) return;
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = point, clickCount = 1 };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            foreach (var hit in hits)
            {
                if (ViewRoot(hit.gameObject.transform)?.name == GuidePanel) continue;
                var target = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
                if (target == null) continue;
                data.pointerPress = target;
                data.pressPosition = point;
                data.pointerCurrentRaycast = data.pointerPressRaycast = hit;
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
                Debug.Log("[Ro3Emu.UiNav] guide click " + target.name);
                return;
            }
            Debug.Log("[Ro3Emu.UiNav] guide click: nothing clickable under " + point);
        }

        /// <summary>The outermost UI_Sys_* / UI_Com_* object above t (the opened view's root), or null.</summary>
        private static Transform ViewRoot(Transform t)
        {
            Transform root = null;
            for (var p = t; p != null; p = p.parent)
                if (p.name.StartsWith("UI_Sys_", StringComparison.Ordinal) || p.name.StartsWith("UI_Com_", StringComparison.Ordinal)) root = p;
            return root;
        }

        /// <summary>Has something to press (stops at the first one).</summary>
        private static bool HasItems(Transform root)
        {
            foreach (var s in Selectable.allSelectablesArray)
                if (s.transform.IsChildOf(root) && Usable(s)) return true;
            root.GetComponentsInChildren(false, Behaviours);
            foreach (var mb in Behaviours)
                if (mb is IPointerClickHandler && !(mb is Selectable) && mb.GetComponent<TMPro.TMP_Text>() == null && Usable(mb) && Small(mb)) return true;
            return false;
        }

        private static bool Small(Component c)
        {
            var r = ScreenRect(c);
            return r.width <= Screen.width * 0.4f && r.height <= Screen.height * 0.4f; // not backgrounds or drag areas
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        private static bool Usable(Component c)
        {
            if (c is Selectable s) return UsableSelectable(s);
            if (!(c is Behaviour b) || b == null || !b.isActiveAndEnabled) return false;
            var graphic = c.GetComponent<Graphic>();
            if (graphic == null || !graphic.raycastTarget || graphic.canvasRenderer.GetInheritedAlpha() < 0.05f) return false;
            for (var p = c.transform; p != null; p = p.parent)
            {
                var group = p.GetComponent<CanvasGroup>();
                if (group != null && (!group.blocksRaycasts || !group.interactable)) return false;
                if (group != null && group.ignoreParentGroups) break;
            }
            var rect = ScreenRect(c);
            return rect.width > 4 && rect.height > 4 && rect.xMax > 0 && rect.yMax > 0 && rect.xMin < Screen.width && rect.yMin < Screen.height;
        }

        private static bool UsableSelectable(Selectable s)
        {
            if (s == null || !s.isActiveAndEnabled || !s.IsInteractable()) return false;
            var g = s.targetGraphic;
            if (g != null && g.canvasRenderer.GetInheritedAlpha() < 0.05f) return false;
            var r = ScreenRect(s);
            return r.width > 4 && r.height > 4 && r.xMax > 0 && r.yMax > 0 && r.xMin < Screen.width && r.yMin < Screen.height;
        }

        private static Rect ScreenRect(Component c)
        {
            var rt = c.transform as RectTransform;
            if (rt == null) return default;
            rt.GetWorldCorners(Corners);
            var canvas = c.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, Corners[0]), b = RectTransformUtility.WorldToScreenPoint(cam, Corners[2]);
            return Rect.MinMaxRect(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Max(a.x, b.x), Math.Max(a.y, b.y));
        }

        /// <summary>The panel the focus is in (several windows can be open side by side), else the top panel.</summary>
        private static Transform FocusPanel => (_focus != null ? ViewRoot(_focus.transform) : null) ?? _panel;

        /// <summary>Developer "select &lt;name&gt;": focus the open menu's item with that object name.</summary>
        internal static void Select(string name)
        {
            foreach (var s in Items)
                if (s.name == name) { _focus = s; Debug.Log("[Ro3Emu.UiNav] select " + name); return; }
            Debug.Log("[Ro3Emu.UiNav] select " + name + ": not in the open menu");
        }

        /// <summary>The panel's remembered focus, else a confirm-like button, else the top-left item (reading order).</summary>
        private static Component DefaultFocus()
        {
            if (Items.Count == 0) return null;
            // A newly opened top panel (item tips over the Backpack) gets the focus before side-by-side windows.
            var top = Items.FindAll(c => c.transform.IsChildOf(_panel));
            var pool = top.Count > 0 ? top : Items;
            var buttons = pool.FindAll(c => c is Selectable); // real buttons before other clickables (cells, links)
            if (buttons.Count > 0 && !LastFocus.ContainsKey(_panel.name)) pool = buttons;
            if (LastFocus.TryGetValue(_panel.name, out var name))
                foreach (var s in pool) if (s.name == name) return s;
            foreach (var s in pool)
            {
                string n = s.name.ToLowerInvariant();
                if (n.Contains("confirm") || n.Contains("sure") || n.Contains("define") || n.Contains("_ok") || n.StartsWith("enter_") || n.StartsWith("login_")) return s;
            }
            Component best = null;
            float bestScore = float.MaxValue;
            foreach (var s in pool)
            {
                var r = ScreenRect(s);
                float score = (Screen.height - r.center.y) * 4f + r.center.x; // rows first, then left to right
                if (score < bestScore) { best = s; bestScore = score; }
            }
            return best;
        }

        // ---- input ----

        private static void Input(Gamepad pad)
        {
            if (SkillBinding.Active) { SkillBinding.Tick(pad, Pressed(pad, "MenuBack", "buttonEast")); return; } // a skill button binds, ○ cancels
            if (SkillBinding.InSkillPanel(FocusPanel))
            {
                if (Pressed(pad, "MenuAssign", "buttonWest")) { SkillBinding.Start(_focus, false); return; }
                if (Pressed(pad, "MenuUnassign", "buttonNorth")) { SkillBinding.Start(null, true); return; }
            }
            var dir = Direction(pad);
            if (dir != Vector2.zero)
            {
                bool fresh = dir != _held;
                if (fresh || Time.unscaledTime >= _repeatAt)
                {
                    _repeatAt = Time.unscaledTime + (fresh ? 0.38f : 0.11f);
                    if (_focus is Slider slider && dir.y == 0f) Nudge(slider, dir.x);
                    else Move(dir);
                }
            }
            _held = dir;
            if (Pressed(pad, "MenuConfirm", "buttonSouth"))
            {
                string confirm = StockPrompts.For(_panel)?.Confirm;
                if (_focus == null && confirm != null) Wheel.Run(confirm); // nothing to pick: the screen's own key (NPC dialog: Space continues)
                else Click(_focus);
            }
            if (Pressed(pad, "MenuBack", "buttonEast")) TapEscape();
            if (Pressed(pad, "MenuTabLeft", "leftShoulder")) Tab(-1);
            if (Pressed(pad, "MenuTabRight", "rightShoulder")) Tab(1);
            float scroll = pad.rightStick.ReadValue().y;
            if (Math.Abs(scroll) > 0.2f) Scroll(scroll);
        }

        private static Vector2 Direction(Gamepad pad)
        {
            var d = pad.dpad.ReadValue();
            if (d == Vector2.zero)
            {
                var s = pad.leftStick.ReadValue();
                if (s.magnitude > 0.55f) d = s;
            }
            if (d == Vector2.zero) return d;
            return Math.Abs(d.x) > Math.Abs(d.y) ? new Vector2(Math.Sign(d.x), 0) : new Vector2(0, Math.Sign(d.y));
        }

        private static bool Pressed(Gamepad pad, string action, string fallback)
        {
            string path = Controller.FirstBinding(action) ?? "<Gamepad>/" + fallback;
            return Controller.Edge(pad.TryGetChildControl(path.Substring(path.IndexOf('/') + 1)) as ButtonControl, true);
        }

        internal static string Control(string action, string fallback) => Controller.FirstBinding(action) ?? "<Gamepad>/" + fallback;

        /// <summary>The nearest item in the direction: distance along it plus twice the sideways offset.</summary>
        private static void Move(Vector2 dir)
        {
            if (_focus == null) { _focus = DefaultFocus(); return; }
            var from = ScreenRect(_focus).center;
            Component best = null;
            float bestScore = float.MaxValue;
            foreach (var s in Items)
            {
                if (s == _focus) continue;
                var d = ScreenRect(s).center - from;
                float along = Vector2.Dot(d, dir);
                if (along <= 1f) continue;
                float side = Math.Abs(dir.x != 0 ? d.y : d.x);
                // Inside the 45° cone first (so "right" crosses to the next window rather than to a slot just below);
                // outside it only when nothing is inside.
                float score = along + side * 2f + (side > along ? 100000f : 0f);
                if (score < bestScore) { best = s; bestScore = score; }
            }
            if (best == null) return;
            _focus = best;
            KeepInView(best);
            if (Diagnostics.Verbose) Debug.Log("[Ro3Emu.UiNav] focus " + best.name);
        }

        private static void Nudge(Slider slider, float sign)
        {
            float step = slider.wholeNumbers ? 1f : (slider.maxValue - slider.minValue) / 20f;
            slider.value = Mathf.Clamp(slider.value + sign * step, slider.minValue, slider.maxValue);
        }

        private static void Click(Component s)
        {
            if (s == null || EventSystem.current == null) return;
            if (s is TMPro.TMP_InputField field) { Osk.Show(field); return; } // typing needs the on-screen keyboard
            var data = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = ScreenRect(s).center,
                clickCount = 1,
            };
            var go = s.gameObject;
            data.pointerPress = go;
            data.pressPosition = data.position;
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerClickHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerExitHandler);
            Debug.Log("[Ro3Emu.UiNav] click " + go.name);
        }

        /// <summary>The game's Esc action is a keyboard action: tap Escape for one frame.</summary>
        private static void TapEscape()
        {
            if (Keyboard.current == null) return;
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(Key.Escape));
            _escDown = true;
        }

        private static void ReleaseEscape()
        {
            _escDown = false;
            if (Keyboard.current != null) InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState());
        }

        private static readonly Dictionary<int, int> TabIndex = new Dictionary<int, int>();

        /// <summary>The focused window's top-most tab bar: the items under one ToggleGroup object (real Toggles, or the
        /// game's ClickEvent tabs, e.g. the Backpack's Type1/Type2_ClickEvent), two or more, in reading order.</summary>
        private static List<Component> TabBar(out int key)
        {
            key = 0;
            List<Component> bar = null;
            float barY = float.MinValue;
            foreach (var group in FocusPanel.GetComponentsInChildren<ToggleGroup>(false))
            {
                var tabs = new List<Component>();
                foreach (var c in Items) if (c.transform.IsChildOf(group.transform) || c is Toggle t && t.group == group) tabs.Add(c);
                if (tabs.Count < 2) continue;
                float y = ScreenRect(group).center.y;
                if (y <= barY) continue;
                bar = tabs; barY = y; key = group.GetInstanceID();
            }
            if (bar == null) return null;
            bool vertical = Math.Abs(ScreenRect(bar[0]).center.x - ScreenRect(bar[bar.Count - 1]).center.x) < 4f;
            bar.Sort((a, b) => vertical ? ScreenRect(b).center.y.CompareTo(ScreenRect(a).center.y) : ScreenRect(a).center.x.CompareTo(ScreenRect(b).center.x));
            return bar;
        }

        /// <summary>L1/R1: previous / next tab. The current tab is the Toggle that is on, else the one last chosen here.</summary>
        private static void Tab(int step)
        {
            var bar = TabBar(out int key);
            if (bar == null) return;
            // Our own index: the game's tab toggles do not always flip isOn when clicked (Backpack Type1/Type2), so isOn
            // only seeds it the first time this bar is used.
            if (!TabIndex.TryGetValue(key, out int on)) on = Math.Max(0, bar.FindIndex(c => c is Toggle t && t.isOn));
            int next = ((on + step) % bar.Count + bar.Count) % bar.Count;
            TabIndex[key] = next;
            Click(bar[next]);
            _focus = bar[next]; // the old focus is usually gone with the old tab's content
        }

        private static ScrollRect ScrollFor(Component s)
        {
            var sr = s != null ? s.GetComponentInParent<ScrollRect>() : null;
            var panel = FocusPanel;
            if (sr != null && sr.transform.IsChildOf(panel)) return sr;
            ScrollRect biggest = null;
            float area = 0;
            foreach (var r in panel.GetComponentsInChildren<ScrollRect>(false))
            {
                var rect = ScreenRect(r);
                if (rect.width * rect.height > area) { biggest = r; area = rect.width * rect.height; }
            }
            return biggest;
        }

        private static void Scroll(float y)
        {
            var sr = ScrollFor(_focus);
            if (sr == null || sr.content == null || sr.viewport == null && !(sr.transform is RectTransform)) return;
            var view = sr.viewport != null ? sr.viewport : (RectTransform)sr.transform;
            if (sr.vertical)
            {
                float range = sr.content.rect.height - view.rect.height;
                if (range > 1f) sr.verticalNormalizedPosition = Mathf.Clamp01(sr.verticalNormalizedPosition + y * Time.unscaledDeltaTime * 900f / range);
            }
            else if (sr.horizontal)
            {
                float range = sr.content.rect.width - view.rect.width;
                if (range > 1f) sr.horizontalNormalizedPosition = Mathf.Clamp01(sr.horizontalNormalizedPosition - y * Time.unscaledDeltaTime * 900f / range);
            }
        }

        /// <summary>Scrolls the focus's scroll view so the focus is inside the viewport.</summary>
        private static void KeepInView(Component s)
        {
            var sr = s.GetComponentInParent<ScrollRect>();
            if (sr == null || sr.content == null || !s.transform.IsChildOf(sr.content)) return;
            var view = sr.viewport != null ? sr.viewport : (RectTransform)sr.transform;
            var b = RectTransformUtility.CalculateRelativeRectTransformBounds(view, s.transform);
            var v = view.rect;
            var shift = Vector2.zero;
            if (sr.vertical)
            {
                if (b.max.y > v.yMax) shift.y = -(b.max.y - v.yMax);
                else if (b.min.y < v.yMin) shift.y = v.yMin - b.min.y;
            }
            if (sr.horizontal)
            {
                if (b.max.x > v.xMax) shift.x = -(b.max.x - v.xMax);
                else if (b.min.x < v.xMin) shift.x = v.xMin - b.min.x;
            }
            if (shift == Vector2.zero) return;
            sr.StopMovement();
            sr.content.anchoredPosition += shift;
        }

        // ---- visuals ----

        private static void HideRing()
        {
            if (_ring != null) foreach (var r in _ring) r.gameObject.SetActive(false);
        }

        private static void DrawRing(Rect rect)
        {
            if (_ring == null)
            {
                _ring = new Image[4];
                for (int i = 0; i < 4; i++) _ring[i] = Overlay.Image("Ro3emuFocus" + i, null, new Color(1f, 0.78f, 0.3f, 0.95f));
            }
            float s = Overlay.Scale, pad = 4 * s, w = Math.Max(2f, 3 * s) * (1f + 0.25f * Mathf.Sin(Time.unscaledTime * 6f));
            rect = Rect.MinMaxRect(rect.xMin - pad, rect.yMin - pad, rect.xMax + pad, rect.yMax + pad);
            Place(_ring[0], rect.xMin, rect.yMax - w, rect.width, w);
            Place(_ring[1], rect.xMin, rect.yMin, rect.width, w);
            Place(_ring[2], rect.xMin, rect.yMin, w, rect.height);
            Place(_ring[3], rect.xMax - w, rect.yMin, w, rect.height);
        }

        private static void Place(Image image, float x, float y, float w, float h)
        {
            image.gameObject.SetActive(true);
            var rt = image.rectTransform;
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static readonly List<(string, string)> PromptList = new List<(string, string)>();
        private static bool _hasTabs;

        private static void Prompts()
        {
            if (SkillBinding.Active)
            {
                PromptList.Clear();
                SkillBinding.AddPrompts(PromptList);
                Overlay.SetPrompts(PromptList);
                return;
            }
            var stock = StockPrompts.For(_panel);
            if (stock != null)
            {
                // The screen draws its own prompts: they get our glyphs and our bar stays off.
                if (_frame % 5 == 0) StockPrompts.Skin(_panel, stock);
                Overlay.SetPrompts(null);
                return;
            }
            PromptList.Clear();
            PromptList.Add((Control("MenuConfirm", "buttonSouth"), _focus is Slider ? "Adjust" : _focus is TMPro.TMP_InputField ? "Type" : "Select"));
            PromptList.Add((Control("MenuBack", "buttonEast"), "Back"));
            if (_frame % 10 == 0) _hasTabs = TabBar(out _) != null; // ponytail: rechecked with the scan, not every frame
            if (_hasTabs)
            {
                PromptList.Add((Control("MenuTabLeft", "leftShoulder"), ""));
                PromptList.Add((Control("MenuTabRight", "rightShoulder"), "Tabs"));
            }
            if (ScrollFor(_focus) != null) PromptList.Add(("<Gamepad>/rightStick/up", "Scroll"));
            if (SkillBinding.InSkillPanel(FocusPanel))
            {
                if (SkillBinding.CanBind(_focus)) PromptList.Add((Control("MenuAssign", "buttonWest"), "Bind"));
                PromptList.Add((Control("MenuUnassign", "buttonNorth"), "Unbind"));
                SkillBinding.AddPrompts(PromptList); // a refusal ("Pick a skill that can be bound"), for a moment
            }
            Overlay.SetPrompts(PromptList);
        }
    }
}
