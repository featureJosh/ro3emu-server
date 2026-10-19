using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>Controller-accessible binding and scale page, opened from the radial menu. Saves to controller.ini.</summary>
    internal static class ControllerSettings
    {
        private static readonly (string name, float step, float min, float max)[] Numbers =
        {
            ("UiScale", 0.05f, 0.6f, 1.6f), ("GlyphScale", 0.1f, 0.8f, 3f),
            ("CameraSpeedX", 50f, -1500f, 1500f), ("CameraSpeedY", 50f, -1500f, 1500f),
            ("CursorSpeed", 100f, 300f, 2400f), ("Deadzone", 0.05f, 0f, 0.75f),
        };

        private static readonly string[] Buttons =
        {
            "buttonSouth", "buttonEast", "buttonWest", "buttonNorth", "leftShoulder", "rightShoulder",
            "leftTrigger", "rightTrigger", "leftStickPress", "rightStickPress", "start", "select",
            "dpad/up", "dpad/down", "dpad/left", "dpad/right",
        };

        private const int VisibleRows = 10;
        // Layer slot rows (Layer2.* then Layer3.*) sit between the action rows and the numbers; D-pad left/right or X cycles the action.
        private static readonly int LayerRows = (ControllerLayers.Count - 1) * ControllerLayers.Slots.Length;
        private static readonly string[] LayerPrefixes = { "NormalSkill_", "ExpandSkill_", "InteractiveSkill_", "GamePlaySkill_", "ShortCut_" };
        private static readonly List<string> LayerChoices = new List<string>();
        private static readonly List<string> Actions = new List<string>();
        private static readonly List<TextMeshProUGUI> Rows = new List<TextMeshProUGUI>();
        private static RectTransform _root;
        private static TextMeshProUGUI _title, _help;
        private static int _selected, _top, _captureFrame;
        private static bool _capture;
        private static string _pending, _conflict;

        internal static bool Open { get; private set; }

        internal static void Show()
        {
            Actions.Clear();
            Actions.AddRange(Controller.ConfigurableActions());
            LayerChoices.Clear();
            LayerChoices.Add(""); // no entry: the slot keeps its base binding
            foreach (var a in Actions)
                foreach (var prefix in LayerPrefixes)
                    if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { LayerChoices.Add(a); break; }
            Build();
            _selected = _top = 0;
            _capture = false;
            _pending = _conflict = null;
            Open = true;
            _root.gameObject.SetActive(true);
            Refresh();
        }

        private static void Hide()
        {
            Open = false;
            _capture = false;
            _pending = _conflict = null;
            if (_root != null) _root.gameObject.SetActive(false);
            Overlay.SetPrompts(null);
        }

        internal static void Tick(Gamepad pad)
        {
            if (!Open) return;
            _root.localScale = Vector3.one * Overlay.Scale;
            if (_capture)
            {
                if (Time.frameCount == _captureFrame) return;
                foreach (var name in Buttons)
                {
                    if (!(pad.TryGetChildControl(name) is ButtonControl button) || !Controller.Edge(button, true)) continue;
                    if (name == "start") { _capture = false; Refresh(); return; }
                    _capture = false;
                    _pending = "<Gamepad>/" + name;
                    _conflict = Controller.BindingConflict(_pending, Actions[_selected]);
                    if (_conflict == null) SavePending();
                    Refresh();
                    return;
                }
                return;
            }
            if (_pending != null)
            {
                if (Tap(pad.dpad.right)) { SavePending(); Refresh(); }
                else if (Tap(pad.dpad.left) || Tap(pad.buttonEast)) { _pending = _conflict = null; Refresh(); }
                return;
            }
            if (Tap(pad.buttonEast)) { Hide(); return; }
            if (Tap(pad.dpad.up)) Move(-1);
            if (Tap(pad.dpad.down)) Move(1);
            if (Tap(pad.buttonNorth)) { Controller.ResetSetting(_selected < Actions.Count ? Actions[_selected] : _selected < Actions.Count + LayerRows ? LayerKey(_selected - Actions.Count) : Numbers[_selected - Actions.Count - LayerRows].name); Refresh(); return; }
            if (_selected < Actions.Count)
            {
                if (Tap(pad.buttonSouth))
                {
                    foreach (var name in Buttons)
                        Controller.Edge(pad.TryGetChildControl(name) as ButtonControl, true);
                    _capture = true;
                    _captureFrame = Time.frameCount;
                    Refresh();
                }
            }
            else if (_selected < Actions.Count + LayerRows)
            {
                if (Tap(pad.dpad.left)) CycleLayer(_selected - Actions.Count, -1);
                if (Tap(pad.dpad.right) || Tap(pad.buttonSouth)) CycleLayer(_selected - Actions.Count, 1);
            }
            else
            {
                int index = _selected - Actions.Count - LayerRows;
                if (Tap(pad.dpad.left)) Adjust(index, -1);
                if (Tap(pad.dpad.right) || Tap(pad.buttonSouth)) Adjust(index, 1);
            }
        }

        private static bool Tap(ButtonControl button) => Controller.Edge(button, true);

        private static void Move(int step)
        {
            _selected = Mathf.Clamp(_selected + step, 0, Actions.Count + LayerRows + Numbers.Length - 1);
            if (_selected < _top) _top = _selected;
            if (_selected >= _top + VisibleRows) _top = _selected - VisibleRows + 1;
            Refresh();
        }

        private static void SavePending()
        {
            Controller.SaveSetting(Actions[_selected], _pending);
            _pending = _conflict = null;
        }


        private static string LayerKey(int row) =>
            "Layer" + (2 + row / ControllerLayers.Slots.Length) + "." + ControllerLayers.Slots[row % ControllerLayers.Slots.Length];

        private static void CycleLayer(int row, int step)
        {
            string key = LayerKey(row);
            string current = Controller.LayerAction(key[5] - '0', ControllerLayers.SlotPath(key.Substring(7)));
            int at = Math.Max(0, LayerChoices.FindIndex(c => string.Equals(c, current ?? "", StringComparison.OrdinalIgnoreCase)));
            string next = LayerChoices[(at + step + LayerChoices.Count) % LayerChoices.Count];
            Controller.SaveSetting(key, next.Length == 0 ? null : next);
            Refresh();
        }

        private static void Adjust(int index, int direction)
        {
            var number = Numbers[index];
            float old = Controller.Setting(number.name, number.min);
            float value = Mathf.Clamp(old + direction * number.step, number.min, number.max);
            Controller.SaveSetting(number.name, value.ToString("0.##", CultureInfo.InvariantCulture));
            Refresh();
        }

        private static void Build()
        {
            if (_root != null) return;
            _root = Overlay.Rect("Ro3emuControllerSettings");
            _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0.5f, 0.5f);
            _root.sizeDelta = new Vector2(840f, 750f);
            _root.localScale = Vector3.one * Overlay.Scale;
            var bg = _root.gameObject.AddComponent<Image>();
            bg.color = new Color(0.045f, 0.035f, 0.03f, 0.95f);
            bg.raycastTarget = false;
            ControllerSkin.Bind(bg, "settings_panel", null);
            _title = Overlay.Text("Title", _root, 34);
            _title.alignment = TextAlignmentOptions.Center;
            Place(_title.rectTransform, 20, 680, 800, 48);
            _help = Overlay.Text("Help", _root, 21);
            _help.alignment = TextAlignmentOptions.Center;
            Place(_help.rectTransform, 20, 20, 800, 60);
            for (int i = 0; i < VisibleRows; i++)
            {
                var row = Overlay.Text("Setting" + i, _root, 26);
                row.alignment = TextAlignmentOptions.MidlineLeft;
                Place(row.rectTransform, 42, 608 - i * 52, 750, 46);
                Rows.Add(row);
            }
        }

        private static void Refresh()
        {
            _title.text = "Controller  •  " + (_selected + 1) + "/" + (Actions.Count + LayerRows + Numbers.Length);
            for (int i = 0; i < Rows.Count; i++)
            {
                int index = _top + i;
                var row = Rows[i];
                row.gameObject.SetActive(index < Actions.Count + LayerRows + Numbers.Length);
                if (!row.gameObject.activeSelf) continue;
                string name, value;
                if (index < Actions.Count)
                {
                    name = Actions[index];
                    value = Controller.FirstBinding(name)?.Replace("<Gamepad>/", "") ?? "Unbound";
                }
                else if (index < Actions.Count + LayerRows)
                {
                    name = LayerKey(index - Actions.Count);
                    value = Controller.LayerAction(name[5] - '0', ControllerLayers.SlotPath(name.Substring(7))) ?? "(base)";
                }
                else
                {
                    name = Numbers[index - Actions.Count - LayerRows].name;
                    value = Controller.Setting(name, 0f).ToString("0.##", CultureInfo.InvariantCulture);
                }
                row.text = (index == _selected ? "▸  " : "    ") + name + "     " + value;
                row.color = index == _selected ? new Color(1f, 0.78f, 0.3f) : new Color(0.96f, 0.92f, 0.82f);
            }
            _help.text = _capture ? "Press a button to bind it  •  OPTIONS cancels"
                : _pending != null ? "Already used by " + _conflict + "  •  D-pad right: keep both  •  left / ○: cancel"
                : _selected < Actions.Count ? "D-pad: choose  •  ✕: rebind  •  △: reset  •  ○: close"
                : _selected < Actions.Count + LayerRows ? "Layer slot (held L2 / R2)  •  D-pad left/right: pick action  •  △: reset  •  ○: close"
                : "D-pad left/right: adjust  •  △: reset  •  ○: close";
        }

        private static void Place(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(w, h);
        }
    }
}
