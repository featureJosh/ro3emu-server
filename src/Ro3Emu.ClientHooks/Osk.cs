using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// On-screen keyboard for TMP input fields (login account and password, chat, names). UiNav opens it when ✕ is pressed
    /// on a focused input field. D-pad / left stick pick a key, MenuConfirm (✕) types it, □ deletes, △ adds a space,
    /// L2 toggles shift, OPTIONS accepts, ○ cancels. Accepting writes the field's text and fires its end-edit / submit
    /// events, as typing and leaving the field would.
    /// </summary>
    public static class Osk
    {
        private static readonly string[] Lower = { "1234567890", "qwertyuiop", "asdfghjkl@", "zxcvbnm.-_" };
        private static readonly string[] Upper = { "!#$%&*()+=", "QWERTYUIOP", "ASDFGHJKL@", "ZXCVBNM,?/" };
        // The bottom row: special keys, each as wide as a share of the row.
        private static readonly string[] Bottom = { "Shift", "Space", "Del", "Done" };

        private static TMP_InputField _field;
        private static string _text;
        private static bool _shift;
        private static int _row, _col;
        private static float _repeatAt;
        private static Vector2 _held;
        private static RectTransform _panel;
        private static TextMeshProUGUI _preview, _title;
        private static readonly List<List<(Image bg, TextMeshProUGUI label)>> Keys = new List<List<(Image, TextMeshProUGUI)>>();

        public static bool Open => _field != null;

        internal static void Show(TMP_InputField field)
        {
            _field = field;
            _text = field.text ?? "";
            _row = 1; _col = 0;
            _held = new Vector2(1, 1); // ignore the stick/d-pad until released
            Build();
            _panel.gameObject.SetActive(true);
            var placeholder = field.placeholder as TMP_Text;
            _title.text = placeholder != null && !string.IsNullOrEmpty(placeholder.text) ? placeholder.text : field.name.Replace("_TInputField", "").Replace("_", " ");
            Refresh();
            Debug.Log("[Ro3Emu.Osk] open for " + field.name);
        }

        private static void Close(bool accept)
        {
            var field = _field;
            _field = null;
            if (_panel != null) _panel.gameObject.SetActive(false);
            if (field == null || !accept) return;
            field.text = _text;
            field.onEndEdit?.Invoke(_text);
            field.onSubmit?.Invoke(_text);
            Debug.Log("[Ro3Emu.Osk] accepted " + field.name + " (" + _text.Length + " chars)");
        }

        /// <summary>From UiNav.Tick while open; returns the prompts to show.</summary>
        internal static void Tick(Gamepad pad, List<(string, string)> prompts)
        {
            if (_field == null || !_field.isActiveAndEnabled) { Close(false); return; }
            var dir = Direction(pad);
            if (dir != Vector2.zero && (dir != _held || Time.unscaledTime >= _repeatAt))
            {
                _repeatAt = Time.unscaledTime + (dir != _held ? 0.3f : 0.09f);
                Move(dir);
                if (Diagnostics.Verbose) Debug.Log("[Ro3Emu.Osk] move " + dir + " -> " + _row + "," + _col);
            }
            _held = dir;
            if (Tap(pad, "buttonSouth")) Press(_row, _col);
            if (Tap(pad, "buttonWest")) Backspace();
            if (Tap(pad, "buttonNorth")) Type(" ");
            if (Tap(pad, "leftTrigger")) { _shift = !_shift; Refresh(); }
            if (Tap(pad, "start")) { Close(true); return; }
            if (Tap(pad, "buttonEast")) { Close(false); return; }
            Refresh();
            prompts.Add(("<Gamepad>/buttonSouth", "Type"));
            prompts.Add(("<Gamepad>/buttonWest", "Delete"));
            prompts.Add(("<Gamepad>/buttonNorth", "Space"));
            prompts.Add(("<Gamepad>/leftTrigger", "Shift"));
            prompts.Add(("<Gamepad>/start", "Done"));
            prompts.Add(("<Gamepad>/buttonEast", "Cancel"));
        }

        private static bool Tap(Gamepad pad, string control) =>
            Controller.Edge(pad.TryGetChildControl(control) as UnityEngine.InputSystem.Controls.ButtonControl, true);

        private static Vector2 Direction(Gamepad pad)
        {
            var d = pad.dpad.ReadValue();
            if (d == Vector2.zero) { var s = pad.leftStick.ReadValue(); if (s.magnitude > 0.55f) d = s; }
            if (d == Vector2.zero) return d;
            return Math.Abs(d.x) > Math.Abs(d.y) ? new Vector2(Math.Sign(d.x), 0) : new Vector2(0, Math.Sign(d.y));
        }

        private static int RowLength(int row) => row < Lower.Length ? Lower[row].Length : Bottom.Length;

        private static void Move(Vector2 dir)
        {
            int rows = Lower.Length + 1;
            if (dir.y != 0)
            {
                // Keep the horizontal position when moving between the 10-key rows and the 4-key bottom row.
                float x = (_col + 0.5f) / RowLength(_row);
                _row = (_row - (int)dir.y + rows) % rows;
                _col = Mathf.Clamp((int)(x * RowLength(_row)), 0, RowLength(_row) - 1);
            }
            else _col = (_col + (int)dir.x + RowLength(_row)) % RowLength(_row);
        }

        private static void Press(int row, int col)
        {
            if (Diagnostics.Verbose) Debug.Log("[Ro3Emu.Osk] press " + row + "," + col);
            if (row < Lower.Length) { Type((_shift ? Upper : Lower)[row][col].ToString()); return; }
            switch (Bottom[col])
            {
                case "Shift": _shift = !_shift; break;
                case "Space": Type(" "); break;
                case "Del": Backspace(); break;
                case "Done": Close(true); break;
            }
        }

        private static void Type(string s)
        {
            int limit = _field.characterLimit;
            if (limit > 0 && _text.Length >= limit) return;
            _text += s;
        }

        private static void Backspace()
        {
            if (_text.Length > 0) _text = _text.Substring(0, _text.Length - 1);
        }

        // ---- view ----

        private const float Key = 64f, Gap = 8f;

        private static void Build()
        {
            if (_panel != null) { _panel.localScale = Vector3.one * Overlay.Scale; return; }
            float width = 10 * Key + 9 * Gap + 2 * 24, height = 5 * Key + 4 * Gap + 150;
            _panel = Overlay.Rect("Ro3emuOsk");
            _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0.5f, 0f);
            _panel.sizeDelta = new Vector2(width, height);
            _panel.anchoredPosition = new Vector2(0, 110 * Overlay.Scale);
            _panel.localScale = Vector3.one * Overlay.Scale;
            var bg = _panel.gameObject.AddComponent<Image>();
            bg.color = new Color(0.06f, 0.05f, 0.04f, 0.92f);
            bg.raycastTarget = false;

            _title = Overlay.Text("Title", _panel, 24);
            Place(_title.rectTransform, 24, height - 44, width - 48, 32);
            var field = Overlay.Image("Field", _panel, new Color(1f, 1f, 1f, 0.08f));
            Place(field.rectTransform, 24, height - 104, width - 48, 52);
            _preview = Overlay.Text("Preview", _panel, 32);
            Place(_preview.rectTransform, 36, height - 104, width - 72, 52);
            _preview.alignment = TextAlignmentOptions.MidlineLeft;

            for (int r = 0; r <= Lower.Length; r++)
            {
                var row = new List<(Image, TextMeshProUGUI)>();
                int n = RowLength(r);
                float keyW = r < Lower.Length ? Key : (10 * Key + 9 * Gap - (n - 1) * Gap) / n;
                for (int c = 0; c < n; c++)
                {
                    var key = Overlay.Image("Key", _panel, Color.white);
                    float x = 24 + c * (keyW + Gap), y = 24 + (Lower.Length - r) * (Key + Gap);
                    Place(key.rectTransform, x, y, keyW, Key);
                    var label = Overlay.Text("Label", key.transform, 28);
                    label.alignment = TextAlignmentOptions.Center;
                    Place(label.rectTransform, 0, 0, keyW, Key);
                    row.Add((key, label));
                }
                Keys.Add(row);
            }
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private static void Refresh()
        {
            bool password = _field.contentType == TMP_InputField.ContentType.Password || _field.inputType == TMP_InputField.InputType.Password;
            _preview.text = (password ? new string('•', _text.Length) : _text) + "<alpha=#AA>|";
            for (int r = 0; r < Keys.Count; r++)
                for (int c = 0; c < Keys[r].Count; c++)
                {
                    bool focus = r == _row && c == _col;
                    Keys[r][c].bg.color = focus ? new Color(1f, 0.78f, 0.3f, 0.95f) : new Color(1f, 1f, 1f, r < Lower.Length ? 0.12f : 0.2f);
                    Keys[r][c].label.color = focus ? new Color(0.1f, 0.07f, 0.03f) : new Color(0.96f, 0.92f, 0.82f);
                    Keys[r][c].label.text = r < Lower.Length ? (_shift ? Upper : Lower)[r][c].ToString()
                        : Bottom[c] == "Shift" && _shift ? "SHIFT" : Bottom[c];
                }
        }
    }
}
