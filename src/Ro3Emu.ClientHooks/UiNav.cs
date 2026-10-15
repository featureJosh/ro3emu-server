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

        private static readonly List<Selectable> Items = new List<Selectable>();
        private static readonly Dictionary<string, string> LastFocus = new Dictionary<string, string>();
        private static Transform _panel;
        private static Selectable _focus;
        private static int _frame;
        private static float _repeatAt;
        private static Vector2 _held;
        private static bool _escDown;
        private static Image[] _ring;

        /// <summary>A menu is open and has the controller.</summary>
        public static bool Active => _panel != null;

        /// <summary>From Controller.Tick every frame while controller mode is on (pad may be null).</summary>
        internal static void Tick(Gamepad pad, bool enabled)
        {
            if (_escDown) { ReleaseEscape(); }
            if (!enabled || pad == null) { Close(); return; }
            if (++_frame % 6 == 0) Scan(); // ponytail: FindObjectsOfType<Canvas> every 6th frame; panel open/close events if it costs
            if (_panel == null) { Close(); return; }
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
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (!canvas.isActiveAndEnabled) continue;
                var root = ViewRoot(canvas.transform);
                if (root == null || Hud.Contains(root.name)) continue;
                int order = canvas.sortingLayerID * 100000 + canvas.sortingOrder;
                if (order <= topOrder) continue;
                if (!HasItems(root)) continue;
                top = root;
                topOrder = order;
            }
            if (top != _panel)
            {
                if (_panel != null && _focus != null) LastFocus[_panel.name] = _focus.name;
                _panel = top;
                _focus = null;
                if (top != null) Debug.Log("[Ro3Emu.UiNav] menu: " + top.name);
            }
            Items.Clear();
            if (_panel == null) return;
            foreach (var s in Selectable.allSelectablesArray)
                if (s.transform.IsChildOf(_panel) && Usable(s)) Items.Add(s);
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

        private static bool HasItems(Transform root)
        {
            foreach (var s in Selectable.allSelectablesArray)
                if (s.transform.IsChildOf(root) && Usable(s)) return true;
            return false;
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        private static bool Usable(Selectable s)
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

        /// <summary>The panel's remembered focus, else a confirm-like button, else the top-left item (reading order).</summary>
        private static Selectable DefaultFocus()
        {
            if (Items.Count == 0) return null;
            if (LastFocus.TryGetValue(_panel.name, out var name))
                foreach (var s in Items) if (s.name == name) return s;
            foreach (var s in Items)
            {
                string n = s.name.ToLowerInvariant();
                if (n.Contains("confirm") || n.Contains("sure") || n.Contains("define") || n.Contains("_ok")) return s;
            }
            Selectable best = null;
            float bestScore = float.MaxValue;
            foreach (var s in Items)
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
            if (Pressed(pad, "MenuConfirm", "buttonSouth")) Click(_focus);
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
            return pad.TryGetChildControl(path.Substring(path.IndexOf('/') + 1)) is ButtonControl b && b.wasPressedThisFrame;
        }

        internal static string Control(string action, string fallback) => Controller.FirstBinding(action) ?? "<Gamepad>/" + fallback;

        /// <summary>The nearest item in the direction: distance along it plus twice the sideways offset.</summary>
        private static void Move(Vector2 dir)
        {
            if (_focus == null) { _focus = DefaultFocus(); return; }
            var from = ScreenRect(_focus).center;
            Selectable best = null;
            float bestScore = float.MaxValue;
            foreach (var s in Items)
            {
                if (s == _focus) continue;
                var d = ScreenRect(s).center - from;
                float along = Vector2.Dot(d, dir);
                if (along <= 1f) continue;
                float side = Math.Abs(dir.x != 0 ? d.y : d.x);
                float score = along + side * 2f;
                if (score < bestScore) { best = s; bestScore = score; }
            }
            if (best == null) return;
            _focus = best;
            KeepInView(best);
        }

        private static void Nudge(Slider slider, float sign)
        {
            float step = slider.wholeNumbers ? 1f : (slider.maxValue - slider.minValue) / 20f;
            slider.value = Mathf.Clamp(slider.value + sign * step, slider.minValue, slider.maxValue);
        }

        private static void Click(Selectable s)
        {
            if (s == null || EventSystem.current == null) return;
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

        /// <summary>L1/R1: the panel's top-most toggle group with two or more toggles is its tab bar.</summary>
        private static void Tab(int step)
        {
            ToggleGroup bar = null;
            float barY = float.MinValue;
            var tabs = new List<Toggle>();
            foreach (var group in _panel.GetComponentsInChildren<ToggleGroup>(false))
            {
                var mine = new List<Toggle>();
                foreach (var s in Items) if (s is Toggle t && t.group == group) mine.Add(t);
                if (mine.Count < 2) continue;
                float y = ScreenRect(mine[0]).center.y;
                if (y > barY) { bar = group; barY = y; tabs = mine; }
            }
            if (bar == null) return;
            bool vertical = Math.Abs(ScreenRect(tabs[0]).center.x - ScreenRect(tabs[tabs.Count - 1]).center.x) < 4f;
            tabs.Sort((a, b) => vertical ? ScreenRect(b).center.y.CompareTo(ScreenRect(a).center.y) : ScreenRect(a).center.x.CompareTo(ScreenRect(b).center.x));
            int on = tabs.FindIndex(t => t.isOn);
            var next = tabs[((on < 0 ? 0 : on) + step + tabs.Count) % tabs.Count];
            Click(next);
        }

        private static ScrollRect ScrollFor(Selectable s)
        {
            var sr = s != null ? s.GetComponentInParent<ScrollRect>() : null;
            if (sr != null && sr.transform.IsChildOf(_panel)) return sr;
            ScrollRect biggest = null;
            float area = 0;
            foreach (var r in _panel.GetComponentsInChildren<ScrollRect>(false))
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
        private static void KeepInView(Selectable s)
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

        private static void Prompts()
        {
            PromptList.Clear();
            PromptList.Add((Control("MenuConfirm", "buttonSouth"), _focus is Slider ? "Adjust" : "Select"));
            PromptList.Add((Control("MenuBack", "buttonEast"), "Back"));
            foreach (var group in _panel.GetComponentsInChildren<ToggleGroup>(false))
            {
                int n = 0;
                foreach (var s in Items) if (s is Toggle t && t.group == group) n++;
                if (n < 2) continue;
                PromptList.Add((Control("MenuTabLeft", "leftShoulder"), ""));
                PromptList.Add((Control("MenuTabRight", "rightShoulder"), "Tabs"));
                break;
            }
            if (ScrollFor(_focus) != null) PromptList.Add(("<Gamepad>/rightStick/up", "Scroll"));
            Overlay.SetPrompts(PromptList);
        }
    }
}
