using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Button glyphs in place of keyboard key labels. Every key label in the stock UI is an InputBinder
    /// (Happyengine.UI.Framework InputBinderBase: m_inputKey = enum_InputKey action, m_tip = the TMP label, m_view = the
    /// label's container, shown while the action has a key). In controller mode each label becomes the PS5 glyph of the
    /// action's first gamepad binding (controller.ini); actions without one show no label, so no keyboard key is shown.
    /// Off again, the labels come back as they were.
    /// </summary>
    public static class Glyphs
    {
        private const string IconName = "Ro3emuGlyph";
        private static readonly FieldInfo KeyField = typeof(InputBinderBase).GetField("m_inputKey", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo TipField = typeof(InputBinderBase).GetField("m_tip", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo ViewField = typeof(InputBinderBase).GetField("m_view", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();
        private static bool _shown;

        /// <summary>The glyph sprite for a gamepad control path ("&lt;Gamepad&gt;/dpad/up" -> glyphs/dpad-up.png; stick
        /// directions use the stick glyph), or null.</summary>
        public static Sprite For(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("<Gamepad>/")) return null;
            string control = path.Substring("<Gamepad>/".Length);
            if (control.StartsWith("leftStick/")) control = "leftStick";
            else if (control.StartsWith("rightStick/")) control = "rightStick";
            control = control.Replace('/', '-');
            if (Sprites.TryGetValue(control, out var sprite)) return sprite;
            using (var stream = typeof(Glyphs).Assembly.GetManifestResourceStream("glyphs/" + control + ".png"))
            {
                if (stream != null)
                {
                    var bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "ro3emu_glyph_" + control };
                    ImageConversion.LoadImage(tex, bytes);
                    sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
            }
            return Sprites[control] = sprite;
        }

        /// <summary>From Controller.Tick about once a second. ponytail: FindObjectsOfType scan, a registry from
        /// InputBinder.OnEnable if it ever shows in a profile.</summary>
        internal static void Refresh(bool on)
        {
            if (!on && !_shown) return;
            _shown = on;
            foreach (var binder in UnityEngine.Object.FindObjectsOfType<InputBinder>())
            {
                var tip = TipField.GetValue(binder) as TMPro.TMP_Text;
                if (tip == null) continue;
                var icon = tip.transform.parent != null ? tip.transform.parent.Find(IconName + tip.GetInstanceID()) : null;
                Sprite sprite = on ? For(Controller.FirstBinding(KeyField.GetValue(binder) as string)) : null;
                if (sprite == null)
                {
                    if (icon != null) UnityEngine.Object.Destroy(icon.gameObject);
                    tip.enabled = true;
                    // Controller mode: an action without a gamepad binding shows no keyboard key.
                    var view = ViewField.GetValue(binder) as GameObject;
                    if (on && view != null && view.activeSelf) view.SetActive(false);
                    continue;
                }
                var view2 = ViewField.GetValue(binder) as GameObject;
                if (view2 != null && !view2.activeSelf) view2.SetActive(true);
                tip.enabled = false;
                if (icon == null) icon = MakeIcon(tip);
                float size = Math.Max(((RectTransform)tip.transform).rect.height, tip.fontSize * 1.25f) * Controller.Setting("GlyphScale", 2.4f);
                ((RectTransform)icon).sizeDelta = new Vector2(size, size);
                var image = icon.GetComponent<Image>();
                if (image.sprite != sprite) image.sprite = sprite;
            }
        }

        /// <summary>A square Image where the label was; Refresh sizes it (label line height x GlyphScale).</summary>
        private static Transform MakeIcon(TMPro.TMP_Text tip)
        {
            var go = new GameObject(IconName + tip.GetInstanceID(), typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            var src = (RectTransform)tip.transform;
            rect.SetParent(src.parent, false);
            rect.SetSiblingIndex(src.GetSiblingIndex() + 1);
            rect.anchorMin = src.anchorMin;
            rect.anchorMax = src.anchorMax;
            rect.pivot = src.pivot;
            rect.anchoredPosition = src.anchoredPosition;
            var image = go.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            return rect;
        }
    }
}
