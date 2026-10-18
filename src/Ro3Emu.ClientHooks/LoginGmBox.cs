using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// A "Login as GM" tick box on the login panel, in the slot of the hidden password row. The choice rides on the
    /// hidden password value ("offline+gm" ticked, "offline+nogm" unticked; the server strips it, ServerOptions
    /// .RequirePasswords false ignores the rest) and is remembered in PlayerPrefs. The server still applies its own GM
    /// policy: the box can only narrow it. Kept apart from UiTweaks.HidePassword, which fills the plain "offline".
    /// </summary>
    public static class LoginGmBox
    {
        private const string CloneName = "Ro3Emu_LoginGm_Tgl";
        private const string Pref = "ro3emu_login_gm";
        private const string Base = "offline";

        private static Toggle _toggle;

        public static void Tick(Transform login)
        {
            if (_toggle == null || !_toggle.gameObject.activeInHierarchy) _toggle = login.GetComponentsInChildren<Toggle>(true).FirstOrDefault(t => t.name == CloneName) ?? Build(login);
            if (_toggle == null) return;
            foreach (var field in login.GetComponentsInChildren<TMP_InputField>(true))
            {
                if (field.name != "Password_TInputField") continue;
                string want = Base + (_toggle.isOn ? "+gm" : "+nogm");
                // Only our own values: a typed password (a server with RequirePasswords on) is left alone.
                if (field.text != want && (string.IsNullOrEmpty(field.text) || field.text.StartsWith(Base, StringComparison.Ordinal))) field.text = want;
            }
        }

        private static Toggle Build(Transform login)
        {
            var row = login.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Password_Img");
            var rowRect = row != null ? row.GetComponent<RectTransform>() : null;
            if (rowRect == null) return null;
            var label = login.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.font != null);

            var go = new GameObject(CloneName, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(rowRect.parent, false);
            rect.anchorMin = rowRect.anchorMin; rect.anchorMax = rowRect.anchorMax; rect.pivot = rowRect.pivot;
            rect.anchoredPosition = rowRect.anchoredPosition;
            rect.sizeDelta = new Vector2(Mathf.Max(rowRect.sizeDelta.x, 220f), 36f);

            var box = Child(go.transform, "Box", new Vector2(0f, 0.5f), new Vector2(28f, 28f), Vector2.zero);
            var bg = box.gameObject.AddComponent<Image>(); bg.color = new Color(0f, 0f, 0f, 0.55f);
            var tick = Child(box, "Tick", new Vector2(0.5f, 0.5f), new Vector2(18f, 18f), Vector2.zero);
            var mark = tick.gameObject.AddComponent<Image>(); mark.color = new Color(1f, 0.85f, 0.3f, 1f);

            var text = Child(go.transform, "Label", new Vector2(0f, 0.5f), new Vector2(180f, 30f), new Vector2(38f, 0f));
            var tmp = text.gameObject.AddComponent<TextMeshProUGUI>();
            if (label != null) { tmp.font = label.font; tmp.fontSharedMaterial = label.fontSharedMaterial; }
            tmp.text = "Login as GM"; tmp.fontSize = 22; tmp.color = Color.white; tmp.alignment = TextAlignmentOptions.Left;
            tmp.raycastTarget = false;

            var toggle = go.AddComponent<Toggle>();
            toggle.targetGraphic = bg; toggle.graphic = mark;
            toggle.isOn = PlayerPrefs.GetInt(Pref, 0) == 1;
            toggle.onValueChanged.AddListener(on => { PlayerPrefs.SetInt(Pref, on ? 1 : 0); PlayerPrefs.Save(); });
            Debug.Log("[Ro3Emu.LoginGmBox] added, on=" + toggle.isOn);
            return toggle;
        }

        private static RectTransform Child(Transform parent, string name, Vector2 anchor, Vector2 size, Vector2 pos)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor; rect.pivot = new Vector2(anchor.x == 0f ? 0f : 0.5f, 0.5f);
            rect.sizeDelta = size; rect.anchoredPosition = pos;
            return rect;
        }
    }
}
