using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Our own screen-space canvas above the game's UI, for everything the controller UI draws (focus ring, prompt bar,
    /// later the HUD). Works in screen pixels; <see cref="Scale"/> turns 1080p design sizes into pixels for any
    /// resolution, times the player's UiScale (controller.ini). The font is the game's own TMP font.
    /// </summary>
    public static class Overlay
    {
        private static Canvas _canvas;
        private static TMP_FontAsset _font;

        /// <summary>1080p design units to screen pixels: screen height / 1080 x UiScale.</summary>
        public static float Scale => Screen.height / 1080f * Controller.Setting("UiScale", 1f);

        public static RectTransform Root
        {
            get
            {
                if (_canvas == null)
                {
                    var go = new GameObject("Ro3emuOverlay", typeof(Canvas));
                    Object.DontDestroyOnLoad(go);
                    _canvas = go.GetComponent<Canvas>();
                    _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    _canvas.sortingOrder = 32000;
                }
                return (RectTransform)_canvas.transform;
            }
        }

        /// <summary>The game's TMP font (the first label found), so our text matches theirs and has its glyphs.</summary>
        public static TMP_FontAsset Font
        {
            get
            {
                if (_font != null) return _font;
                _font = TMP_Settings.defaultFontAsset;
                if (_font == null)
                    foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>())
                        if (t.font != null) { _font = t.font; break; }
                return _font;
            }
        }

        /// <summary>A child rect of the overlay anchored at the bottom-left corner (positions are screen pixels).</summary>
        public static RectTransform Rect(string name, Transform parent = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent ?? Root, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            return rect;
        }

        public static Image Image(string name, Transform parent, Color color)
        {
            var image = Rect(name, parent).gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        public static TextMeshProUGUI Text(string name, Transform parent, float size)
        {
            var text = Rect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) text.font = Font;
            text.fontSize = size;
            text.raycastTarget = false;
            text.enableWordWrapping = false;
            text.color = new Color(0.96f, 0.92f, 0.82f);
            text.outlineWidth = 0.2f;
            text.outlineColor = new Color32(0, 0, 0, 200);
            return text;
        }

        // ---- prompt bar: bottom-right "[glyph] Label" pairs ----

        private static RectTransform _bar;
        private static readonly List<(Image glyph, TextMeshProUGUI label)> Prompts = new List<(Image, TextMeshProUGUI)>();
        private static string _shown;

        /// <summary>Shows the prompts (control path, label) at the bottom right, or hides the bar for none.</summary>
        public static void SetPrompts(IList<(string control, string label)> prompts)
        {
            string key = prompts == null ? "" : string.Join("|", prompts);
            float scale = Scale;
            if (key == _shown && _bar != null && Mathf.Approximately(_bar.localScale.x, scale)) return;
            _shown = key;
            if (_bar == null)
            {
                _bar = Rect("Ro3emuPrompts");
                _bar.anchorMin = _bar.anchorMax = _bar.pivot = new Vector2(1f, 0f);
                var bg = _bar.gameObject.AddComponent<Image>();
                bg.color = new Color(0.05f, 0.04f, 0.03f, 0.72f);
                bg.raycastTarget = false;
                var layout = _bar.gameObject.AddComponent<HorizontalLayoutGroup>();
                layout.spacing = 18;
                layout.padding = new RectOffset(16, 20, 6, 6);
                layout.childAlignment = TextAnchor.MiddleRight;
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandWidth = layout.childForceExpandHeight = false;
                _bar.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                _bar.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            _bar.localScale = new Vector3(scale, scale, 1f);
            _bar.anchoredPosition = new Vector2(-24 * scale, 110 * scale); // above the stock HUD's bottom-right menu buttons
            int n = prompts?.Count ?? 0;
            _bar.gameObject.SetActive(n > 0);
            while (Prompts.Count < n)
            {
                var glyph = Image("Glyph", _bar, Color.white);
                glyph.preserveAspect = true;
                var le = glyph.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = le.preferredHeight = 44;
                var label = Text("Label", _bar, 26);
                label.alignment = TextAlignmentOptions.MidlineLeft;
                Prompts.Add((glyph, label));
            }
            for (int i = 0; i < Prompts.Count; i++)
            {
                bool on = i < n;
                Prompts[i].glyph.gameObject.SetActive(on);
                Prompts[i].label.gameObject.SetActive(on);
                if (!on) continue;
                Prompts[i].glyph.sprite = Glyphs.For(prompts[i].control);
                Prompts[i].label.text = prompts[i].label;
            }
        }
    }
}
