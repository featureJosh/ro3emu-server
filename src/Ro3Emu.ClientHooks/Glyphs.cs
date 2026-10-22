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
            if (Sprites.TryGetValue(control, out var sprite)) return ControllerSkin.Get("glyph_" + control, sprite);
            using (var stream = typeof(Glyphs).Assembly.GetManifestResourceStream("glyphs/" + control + ".png"))
            {
                if (stream != null)
                {
                    var bytes = new byte[stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) throw new System.IO.EndOfStreamException("Incomplete embedded glyph: " + control);
                        offset += read;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "ro3emu_glyph_" + control };
                    ImageConversion.LoadImage(tex, bytes);
                    sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
            }
            Sprites[control] = sprite;
            return ControllerSkin.Get("glyph_" + control, sprite);
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
                Sprite sprite = on ? For(Controller.AnyBinding(KeyField.GetValue(binder) as string)) : null;
                if (sprite == null)
                {
                    Unreplace(tip);
                    // Controller mode: an action without a gamepad binding shows no keyboard key.
                    var view = ViewField.GetValue(binder) as GameObject;
                    if (on && view != null && view.activeSelf) view.SetActive(false);
                    continue;
                }
                var view2 = ViewField.GetValue(binder) as GameObject;
                if (view2 != null && !view2.activeSelf) view2.SetActive(true);
                // Some labels (skill-bar corners) are tiny, so there is also a floor: 28 px at 1080p.
                Replace(tip, sprite, 28f * Controller.Setting("GlyphScale", 2f) / 2f);
            }
            if (!on) StockPrompts.Restore();
        }

        /// <summary>Shows the glyph in place of a key label (hidden, with the small key badge behind it): label line height
        /// x GlyphScale, at least <paramref name="floor"/> px at 1080p (exactly that with <paramref name="exact"/>).</summary>
        internal static RectTransform Replace(TMPro.TMP_Text tip, Sprite sprite, float floor, bool exact = false)
        {
            var icon = tip.transform.parent != null ? tip.transform.parent.Find(IconName + tip.GetInstanceID()) : null;
            tip.enabled = false;
            var badge = tip.transform.parent != null ? tip.transform.parent.GetComponent<Image>() : null;
            if (badge != null) badge.enabled = false; // the small key badge behind the label (KeyPosition_Img)
            if (icon == null) icon = MakeIcon(tip);
            ((RectTransform)icon).localPosition = tip.transform.localPosition;
            var canvas = tip.canvas;
            floor *= canvas != null ? Screen.height / 1080f / canvas.scaleFactor : 1f; // in the label's canvas units
            float size = exact ? floor : Math.Max(Math.Max(((RectTransform)tip.transform).rect.height, tip.fontSize * 1.25f) * Controller.Setting("GlyphScale", 2f), floor);
            ((RectTransform)icon).sizeDelta = new Vector2(size, size);
            var image = icon.GetComponent<Image>();
            if (image.sprite != sprite) image.sprite = sprite;
            return (RectTransform)icon;
        }

        /// <summary>The key label again, without our glyph.</summary>
        internal static void Unreplace(TMPro.TMP_Text tip)
        {
            var icon = tip.transform.parent != null ? tip.transform.parent.Find(IconName + tip.GetInstanceID()) : null;
            if (icon == null) return;
            UnityEngine.Object.Destroy(icon.gameObject);
            tip.enabled = true;
            var badge = tip.transform.parent.GetComponent<Image>();
            if (badge != null) badge.enabled = true;
        }

        /// <summary>A square Image where the label was; Refresh sizes it (label line height x GlyphScale).</summary>
        private static Transform MakeIcon(TMPro.TMP_Text tip)
        {
            var go = new GameObject(IconName + tip.GetInstanceID(), typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            var src = (RectTransform)tip.transform;
            rect.SetParent(src.parent, false);
            rect.SetSiblingIndex(src.GetSiblingIndex() + 1);
            // Key labels often sit in a HorizontalLayoutGroup + ContentSizeFitter badge (skill bar: 16x16 KeyPosition_Img)
            // that would size the icon back down every layout pass.
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            var image = go.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            return rect;
        }
    }
}
