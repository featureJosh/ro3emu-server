using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Radial menu (Diablo-style) for everything the keyboard reaches with hotkeys: tap MenuWheel (Create / View) in the
    /// world, point with a stick, ✕ (or MenuWheel again) opens, ○ closes. An entry runs the game's own input action, as
    /// its hotkey would: InputSystemManager's private started / performed / canceled handlers (Obf_jn / Obf_Jn / Obf_kn)
    /// are called with the action name, which reaches the same Lua callbacks (LC_InputBinder, Lua_UIPanelManager's
    /// Esc) as a key press.
    /// </summary>
    public static class Wheel
    {
        private static readonly (string label, string action)[] Entries =
        {
            ("Bag", "OpenBag"), ("Character", "OpenRole"), ("Skills", "OpenSkill"), ("Quests", "OpenTask"),
            ("Map", "OpenMap"), ("Friends", "OpenFriend"), ("Guild", "OpenGuild"), ("Shop", "OpenShop"),
            ("Auto Battle", "OpenAutoFight"), ("Settings", "ClosePanel"), ("Controller", "@controller-settings"),
        };

        private static readonly MethodInfo Started = Handler("Obf_jn"), Performed = Handler("Obf_Jn"), Canceled = Handler("Obf_kn");

        private static MethodInfo Handler(string name) =>
            typeof(InputSystemManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(InputAction.CallbackContext) }, null);

        private static RectTransform _root;
        private static readonly List<(Image bg, TextMeshProUGUI label)> Slices = new List<(Image, TextMeshProUGUI)>();
        private static TextMeshProUGUI _center;
        private static int _selected = -1;

        public static bool Open { get; private set; }

        /// <summary>Runs a game input action as a key press would (started, performed, canceled).</summary>
        public static bool Run(string action)
        {
            if (action == "@controller-settings") { ControllerSettings.Show(); return true; }
            var manager = HappyEngine.Base.SingletonMonoBehaviour<InputSystemManager>.Instance;
            if (manager == null || Started == null || Performed == null || Canceled == null)
            {
                Debug.LogWarning("[Ro3Emu.Wheel] input action handlers not found; " + action + " not run");
                return false;
            }
            var args = new object[] { action, default(InputAction.CallbackContext) };
            Started.Invoke(manager, args);
            Performed.Invoke(manager, args);
            Canceled.Invoke(manager, args);
            Debug.Log("[Ro3Emu.Wheel] run " + action);
            return true;
        }

        public static void Show()
        {
            Build();
            Open = true;
            _selected = -1;
            _root.gameObject.SetActive(true);
            foreach (var s in Slices) if (s.bg.GetComponent<Punch>() is Punch p) p.enabled = false; // a closing punch must not hide it again
            Refresh();
        }

        public static void Hide()
        {
            Open = false;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>From Controller.Tick while open; returns the prompts.</summary>
        internal static void Tick(Gamepad pad, List<(string, string)> prompts)
        {
            RefreshArt();
            var stick = pad.rightStick.ReadValue();
            if (stick.magnitude < 0.5f) stick = pad.leftStick.ReadValue();
            if (stick.magnitude >= 0.5f)
            {
                // Slice 0 is at the top, then clockwise.
                float angle = Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg;
                if (angle < 0) angle += 360f;
                int n = Entries.Length;
                int index = (int)Math.Round(angle / (360f / n)) % n;
                if (index != _selected) { _selected = index; Refresh(); Punch.Play(Slices[index].bg.transform, 1.25f, null, 1.12f); }
            }
            var dpad = pad.dpad.ReadValue();
            if (dpad != Vector2.zero && Controller.Edge(dpad.x > 0 ? pad.dpad.right : dpad.x < 0 ? pad.dpad.left : dpad.y > 0 ? pad.dpad.up : pad.dpad.down, true))
            {
                int step = dpad.x > 0 || dpad.y < 0 ? 1 : -1;
                _selected = ((_selected < 0 ? 0 : _selected) + step + Entries.Length) % Entries.Length;
                Refresh();
                Punch.Play(Slices[_selected].bg.transform, 1.25f, null, 1.12f);
            }
            bool confirm = Controller.Edge(pad.TryGetChildControl(Path(UiNav.Control("MenuConfirm", "buttonSouth"))) as ButtonControl, true)
                || Controller.Edge(pad.TryGetChildControl(Path(UiNav.Control("MenuWheel", "select"))) as ButtonControl, true);
            if (confirm)
            {
                if (_selected < 0) { Hide(); return; }
                Open = false; // the wheel stays on screen for the pick's punch, then hides itself
                Punch.Play(Slices[_selected].bg.transform, 1.4f, null, 1.12f, _root.gameObject);
                Run(Entries[_selected].action);
                return;
            }
            if (Controller.Edge(pad.TryGetChildControl(Path(UiNav.Control("MenuBack", "buttonEast"))) as ButtonControl, true)) { Hide(); return; }
            prompts.Add(("<Gamepad>/rightStick", "Choose"));
            prompts.Add((UiNav.Control("MenuConfirm", "buttonSouth"), "Open"));
            prompts.Add((UiNav.Control("MenuBack", "buttonEast"), "Close"));
        }

        private static string Path(string control) => control.Substring(control.IndexOf('/') + 1);

        // ---- view ----

        private const float Radius = 270f, SliceW = 156f, SliceH = 54f;

        private static void Build()
        {
            if (_root != null) { _root.localScale = Vector3.one * Overlay.Scale; return; }
            _root = Overlay.Rect("Ro3emuWheel");
            _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0.5f, 0.5f);
            _root.localScale = Vector3.one * Overlay.Scale;
            var disc = Overlay.Image("Disc", _root, new Color(0.04f, 0.03f, 0.03f, 0.82f));
            disc.sprite = Shapes.Circle;
            ControllerSkin.Bind(disc, "wheel_disc", Shapes.Circle);
            Place(disc.rectTransform, -Radius - 110f, -Radius - 110f, 2 * Radius + 220f, 2 * Radius + 220f);
            var ring = Overlay.Image("Ring", _root, new Color(0.86f, 0.7f, 0.4f, 0.8f));
            ring.sprite = Shapes.Ring;
            ControllerSkin.Bind(ring, "wheel_ring", Shapes.Ring);
            Place(ring.rectTransform, -Radius - 110f, -Radius - 110f, 2 * Radius + 220f, 2 * Radius + 220f);
            _center = Overlay.Text("Center", _root, 34);
            _center.alignment = TextAlignmentOptions.Center;
            Place(_center.rectTransform, -150f, -30f, 300f, 60f);
            for (int i = 0; i < Entries.Length; i++)
            {
                float a = i * 2f * Mathf.PI / Entries.Length;
                float cx = Mathf.Sin(a) * Radius, cy = Mathf.Cos(a) * Radius;
                var bg = Overlay.Image("Slice" + i, _root, Color.white);
                bg.sprite = Shapes.RoundedRect;
                bg.type = Image.Type.Simple;
                Place(bg.rectTransform, cx - SliceW / 2f, cy - SliceH / 2f, SliceW, SliceH);
                var label = Overlay.Text("Label", bg.transform, 24);
                label.alignment = TextAlignmentOptions.Center;
                Place(label.rectTransform, 0, 0, SliceW, SliceH);
                label.text = Entries[i].label;
                Slices.Add((bg, label));
            }
        }

        private static void Refresh()
        {
            RefreshArt();
            for (int i = 0; i < Slices.Count; i++)
            {
                bool on = i == _selected;
                Slices[i].label.color = on ? new Color(0.1f, 0.07f, 0.03f) : new Color(0.96f, 0.92f, 0.82f);
                Slices[i].bg.rectTransform.localScale = Vector3.one * (on ? 1.12f : 1f);
            }
            _center.text = _selected >= 0 ? Entries[_selected].label : "Menu";
        }

        private static void RefreshArt()
        {
            for (int i = 0; i < Slices.Count; i++)
            {
                bool selected = i == _selected;
                var sprite = ControllerSkin.Get(selected ? "wheel_slice_selected" : "wheel_slice", Shapes.RoundedRect);
                if (Slices[i].bg.sprite != sprite) Slices[i].bg.sprite = sprite;
                Slices[i].bg.color = sprite != Shapes.RoundedRect ? Color.white
                    : selected ? new Color(0.86f, 0.7f, 0.4f, 0.95f) : new Color(1f, 1f, 1f, 0.1f);
            }
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }
    }
}
