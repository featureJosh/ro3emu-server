using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Screens that draw their own key prompts keep them, with our glyphs in place of the keyboard keys, and our prompt bar
    /// stays off there (UiNav). Per-screen rules are data:
    /// <list type="bullet">
    /// <item>NPC dialog: "SPACE [mouse] Continue" (Pan_Guide: Pic_SpaceBg/Txt_Sapce, Pic_Sign, Pic_Mouse, Txt_Text), "F" on the
    /// highlighted choice (KeyPosition_TTxt in a KeyPosition_Img badge), "ESC" by Exit (Pic_Esc/Txt_Esc). Plain labels, not
    /// InputBinders. ✕ picks the focused choice, or with none continues as Space does (the game's Jump action); Exit and
    /// the full-screen click-to-continue button are not focus targets.</item>
    /// </list>
    /// </summary>
    internal static class StockPrompts
    {
        internal sealed class Rule
        {
            public string Panel, Confirm;
            public Dictionary<string, string> Labels; // stock key label -> our action (its glyph)
            public string[] Hide, NoFocus;
        }

        private static readonly Rule[] Rules =
        {
            new Rule
            {
                Panel = "UI_Sys_NpcDialogPanel(Clone)", Confirm = "Jump",
                Labels = new Dictionary<string, string> { ["Txt_Sapce"] = "MenuConfirm", ["KeyPosition_TTxt"] = "MenuConfirm", ["Txt_Esc"] = "MenuBack" },
                Hide = new[] { "Pic_Sign", "Pic_Mouse" },
                NoFocus = new[] { "Skip_Img_Btn", "Pan_CloseTips", "NarrationNext_Btn" },
            },
        };

        private static readonly Dictionary<string, string> Fallback = new Dictionary<string, string>
            { ["MenuConfirm"] = "buttonSouth", ["MenuBack"] = "buttonEast" };

        private static readonly List<TMP_Text> Skinned = new List<TMP_Text>();
        private static readonly List<GameObject> Hidden = new List<GameObject>();

        public static Rule For(Transform panel)
        {
            if (panel != null) foreach (var r in Rules) if (r.Panel == panel.name) return r;
            return null;
        }

        /// <summary>From UiNav every frame while the rule's panel has the pad: our glyphs on its key labels.</summary>
        public static void Skin(Transform panel, Rule rule)
        {
            foreach (var t in panel.GetComponentsInChildren<Transform>(false))
            {
                if (System.Array.IndexOf(rule.Hide, t.name) >= 0) { t.gameObject.SetActive(false); if (!Hidden.Contains(t.gameObject)) Hidden.Add(t.gameObject); continue; }
                if (!rule.Labels.TryGetValue(t.name, out var action) || !(t.GetComponent<TMP_Text>() is TMP_Text tip)) continue;
                var sprite = Glyphs.For(UiNav.Control(action, Fallback[action]));
                if (sprite == null) continue;
                if (!Skinned.Contains(tip)) Skinned.Add(tip);
                var icon = Glyphs.Replace(tip, sprite, 30f, true);
                // At the label's right end, next to the text it belongs to ("SPACE" was a wide badge before "Continue").
                icon.localPosition += new Vector3((((RectTransform)tip.transform).rect.width - icon.sizeDelta.x) / 2f, 0f, 0f);
            }
        }

        /// <summary>Controller mode off: the keyboard labels come back.</summary>
        public static void Restore()
        {
            foreach (var tip in Skinned) if (tip != null) Glyphs.Unreplace(tip);
            Skinned.Clear();
            foreach (var go in Hidden) if (go != null) go.SetActive(true);
            Hidden.Clear();
        }
    }
}
