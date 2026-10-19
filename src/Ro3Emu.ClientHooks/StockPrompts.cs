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

        // Everything below is per panel instance: found once when the panel opens, then reused. The scan covers that panel's own
        // subtree only (never the scene), and repeats every RescanFrames so labels the game builds later (choice rows are pooled
        // and rebuilt) are picked up; a destroyed entry forces an earlier rescan.
        private const int RescanFrames = 45;
        private static Transform _scanned;
        private static int _scanFrame;
        private static readonly List<KeyValuePair<TMP_Text, string>> Labels = new List<KeyValuePair<TMP_Text, string>>();
        private static readonly List<GameObject> ToHide = new List<GameObject>();
        private static readonly List<Transform> Walk = new List<Transform>();
        private static readonly HashSet<TMP_Text> Skinned = new HashSet<TMP_Text>();
        private static readonly HashSet<GameObject> Hidden = new HashSet<GameObject>();

        public static Rule For(Transform panel)
        {
            if (panel != null) foreach (var r in Rules) if (r.Panel == panel.name) return r;
            return null;
        }

        /// <summary>From UiNav every few frames while the rule's panel has the pad: our glyphs on its key labels.</summary>
        public static void Skin(Transform panel, Rule rule)
        {
            if (panel == null || rule == null) return;
            bool stale = false;
            foreach (var l in Labels) if (l.Key == null) { stale = true; break; }
            if (panel != _scanned || stale || ++_scanFrame >= RescanFrames) Scan(panel, rule);
            foreach (var go in ToHide)
                if (go != null && go.activeSelf) { go.SetActive(false); Hidden.Add(go); }
            foreach (var l in Labels)
            {
                var tip = l.Key;
                if (tip == null || !tip.gameObject.activeInHierarchy) continue;
                if (!Fallback.TryGetValue(l.Value, out var fallback)) continue;
                var sprite = Glyphs.For(UiNav.Control(l.Value, fallback));
                if (sprite == null) continue;
                Skinned.Add(tip);
                var icon = Glyphs.Replace(tip, sprite, 30f, true);
                // At the label's right end, next to the text it belongs to ("SPACE" was a wide badge before "Continue").
                icon.localPosition += new Vector3((((RectTransform)tip.transform).rect.width - icon.sizeDelta.x) / 2f, 0f, 0f);
            }
        }

        private static void Scan(Transform panel, Rule rule)
        {
            _scanned = panel;
            _scanFrame = 0;
            Labels.Clear();
            ToHide.Clear();
            panel.GetComponentsInChildren(true, Walk); // inactive too: a row's label can wake later
            foreach (var t in Walk)
            {
                if (t == null) continue;
                if (rule.Hide != null && System.Array.IndexOf(rule.Hide, t.name) >= 0) ToHide.Add(t.gameObject);
                else if (rule.Labels != null && rule.Labels.TryGetValue(t.name, out var action) && t.GetComponent<TMP_Text>() is TMP_Text tip)
                    Labels.Add(new KeyValuePair<TMP_Text, string>(tip, action));
            }
            Walk.Clear();
        }

        /// <summary>Controller mode off: the keyboard labels come back.</summary>
        public static void Restore()
        {
            foreach (var tip in Skinned) if (tip != null) Glyphs.Unreplace(tip);
            Skinned.Clear();
            foreach (var go in Hidden) if (go != null) go.SetActive(true);
            Hidden.Clear();
            Labels.Clear();
            ToHide.Clear();
            _scanned = null;
        }
    }
}
