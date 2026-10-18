using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Small layout changes to the client's UI that the signed Lua and bundles cannot carry, applied from the per-frame
    /// pump while a view is open.
    /// <list type="bullet">
    /// <item>Mode selection ("Select Mode", first world entry and Settings → PC Mode): the Mobile column (in development
    /// in this build) becomes "Controller" (see Controller).</item>
    /// <item>Login: no password row (single-player; HidePassword).</item>
    /// </list>
    /// </summary>
    public static class UiTweaks
    {
        private static readonly string[] ModeWidgets = { "UI_Sys_SetModeWidget_PC(Clone)", "UI_Sys_SetModeWidget(Clone)" };
        private const string ModeList = "Root/Content_RTransform/Pop_Center/Root/Pan_Content/Pan_BgMask/Content_RTransform";
        private const string MobileColumn = "PCMobile_RTransform";

        private static readonly HashSet<int> Done = new HashSet<int>();
        private static int _frame, _lastFrame = -1;

        /// <summary>Called every frame; looks for the views every 10th frame (GameObject.Find only sees active objects).</summary>
        public static void Tick()
        {
            // The pump (ServerImAdapter's per-frame call) can run more than once a frame; everything here is per frame.
            if (Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;
            Diagnostics.AutoDump();
            Diagnostics.PollCommandFile();
            TextFit.Tick();
            Controller.Tick();
            bool tenth = ++_frame % 10 == 0;
            // While still in the login flow every frame, not every 10th: the password row must be gone before the panel's first draw.
            try { LoginTick(tenth); }
            catch (Exception e) { Debug.LogError("[Ro3Emu.UiTweaks] " + e); }
            if (!tenth) return;
            try
            {
                foreach (var name in ModeWidgets)
                {
                    var widget = GameObject.Find(name);
                    if (widget != null && !Done.Contains(widget.GetInstanceID()) && RelabelMobileMode(widget.transform)) Done.Add(widget.GetInstanceID());
                }
                foreach (var name in StallWindows)
                {
                    var window = GameObject.Find(name);
                    if (window != null && Done.Add(window.GetInstanceID())) WidenStallName(window.transform);
                }
                if (Done.Count > 64) Done.Clear();
                QuitButton.Tick();
                LuaPatches.Tick();
                PassChallenges.Tick();
                PassButton.Tick();
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.UiTweaks] " + e);
            }
        }

        private const string LoginPanel = "UI_Sys_LoginPanel(Clone)";
        private const string NoPassword = "offline";
        private static GameObject _login;
        private static TMPro.TMP_InputField _passwordField;
        private static Transform _passwordRow;
        private static bool _inWorld;

        /// <summary>
        /// Per frame. A cached panel is cheap. Without one, a Find (active objects only) runs every frame while the client
        /// is still in the login flow, so the panel is caught the frame it appears; once the in-world HUD has been seen
        /// it drops to every 10th frame (the panel comes back after a logout and is found again, which ends _inWorld).
        /// On every 10th frame the field is re-checked and the GM box kept up.
        /// </summary>
        private static void LoginTick(bool tenth)
        {
            if (_login == null)
            {
                if (_inWorld && !tenth) return;
                _login = GameObject.Find(LoginPanel);
                if (_login == null)
                {
                    if (tenth && !_inWorld && GameObject.Find(ControllerHud.SkillWidget) != null) _inWorld = true;
                    return;
                }
                _inWorld = false;
                _passwordField = null; _passwordRow = null;
                HidePassword(_login.transform, true); // this very frame, before the first draw
            }
            else if (!_login.activeInHierarchy) { _login = null; return; }
            else HidePassword(_login.transform, tenth);
            if (tenth) LoginGmBox.Tick(_login.transform);
        }

        /// <summary>
        /// Single-player: the server opens an account with any password (ServerOptions.RequirePasswords false in our
        /// appsettings), but the client refuses an empty one ("Enter password"). The password row is hidden and its field
        /// filled, so login is account + Start Game, also with a controller. The field and row are cached once found; the
        /// row is kept inactive every frame, the field re-checked (<paramref name="refill"/>) every 10th: the client can reset it.
        /// ponytail: always hidden; a LAN server with RequirePasswords on needs this off (a client setting) when that comes.
        /// </summary>
        private static void HidePassword(Transform login, bool refill)
        {
            if (_passwordField == null)
            {
                foreach (var field in login.GetComponentsInChildren<TMPro.TMP_InputField>(true))
                {
                    if (field.name != "Password_TInputField") continue;
                    _passwordField = field;
                    var row = field.transform.parent;
                    _passwordRow = row != null && row.name == "Password_Img" ? row : null;
                    refill = true;
                    break;
                }
                if (_passwordField == null) return;
            }
            if (refill && string.IsNullOrEmpty(_passwordField.text)) _passwordField.text = NoPassword;
            if (_passwordRow != null && _passwordRow.gameObject.activeSelf)
            {
                _passwordRow.gameObject.SetActive(false);
                Debug.Log("[Ro3Emu.UiTweaks] login: password row hidden (single-player)");
            }
        }

        private static readonly string[] StallWindows = { "UI_Sys_StallWindow(Clone)", "UI_Sys_StallWindow_PC(Clone)" };

        /// <summary>
        /// The stall title input (UI_Sys_StallWindow m_kTINPUTFIELD_TInput) has characterLimit 10 in the prefab, sized for
        /// Chinese; it gets LuaPatches.StallNameMaxLength, the same limit the server applies.
        /// </summary>
        private static void WidenStallName(Transform window)
        {
            foreach (var input in window.GetComponentsInChildren<TMPro.TMP_InputField>(true))
                if (input.characterLimit > 0 && input.characterLimit < LuaPatches.StallNameMaxLength)
                {
                    Debug.Log("[Ro3Emu.UiTweaks] stall input " + input.name + ": limit " + input.characterLimit + " -> " + LuaPatches.StallNameMaxLength);
                    input.characterLimit = LuaPatches.StallNameMaxLength;
                }
        }

        /// <summary>The Mobile column becomes "Controller" (Controller.InstallModeSelect gives it its meaning). The card
        /// widget is created inside the column by SelectMode, so this retries until its texts exist.</summary>
        private static bool RelabelMobileMode(Transform widget)
        {
            var content = widget.Find(ModeList);
            if (content == null) return false;
            Transform mobile = null;
            foreach (var t in content.GetComponentsInChildren<Transform>(true))
                if (t.name == MobileColumn) { mobile = t; break; }
            if (mobile == null) return false;
            // Names logged in game (2026-10-14): PCModel_TTxt title, PCModelContent_TTxt blurb, PCMode01-03_TTxt the three
            // feature captions, Txt_Null "In Development - Coming Soon" on the overlay that blocks the column.
            Transform title = null, overlay = null;
            int set = 0;
            foreach (var text in mobile.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (Labels.TryGetValue(text.name, out var label))
                {
                    if (text.name == "PCModelContent_TTxt") text.enableAutoSizing = true;
                    text.text = label;
                    set++;
                }
                if (text.name == "PCModel_TTxt") title = text.transform;
                if (text.name == "Txt_Null") overlay = text.transform.parent;
            }
            // Walk up to the dark lock panel (Unlock_RImg/Pic_UnlockBg/Pan_Null_Dark/NullEmpty_GraphicSwitchG/...).
            for (var p = overlay; p != null && p != mobile; p = p.parent) if (p.name == "Pan_Null_Dark") { overlay = p; break; }
            if (overlay != null)
            {
                Debug.Log("[Ro3Emu.UiTweaks] mode select overlay: " + Describe(overlay, mobile));
                // The overlay node goes, unless it also holds the card itself (then only its text does).
                if (overlay != mobile && (title == null || !title.IsChildOf(overlay))) overlay.gameObject.SetActive(false);
                else foreach (var t in overlay.GetComponentsInChildren<TMPro.TMP_Text>(true)) if (t.name == "Txt_Null") t.gameObject.SetActive(false);
            }
            // Whatever dims or locks the column.
            foreach (var group in mobile.GetComponentsInChildren<CanvasGroup>(true))
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }
            foreach (var selectable in mobile.GetComponentsInChildren<UnityEngine.UI.Selectable>(true)) selectable.interactable = true;
            if (set > 0)
            {
                Debug.Log("[Ro3Emu.UiTweaks] mode select column: " + Describe(mobile, mobile));
                Controller.MakeColumnSelectable(mobile.gameObject);
            }
            if (set > 0) Debug.Log("[Ro3Emu.UiTweaks] mode select: Mobile column relabelled Controller (" + set + " text(s))");
            return set > 0;
        }

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "PCModel_TTxt", "Controller" },
            { "PCModelContent_TTxt", "For players who use a gamepad. Modern PC controls; remap in ro3emu/controller.ini" },
            { "PCMode01_TTxt", "Stick Movement" },
            { "PCMode02_TTxt", "Stick Camera" },
            { "PCMode03_TTxt", "Target Lock Mode" },
        };

        /// <summary>Path from the column and the components of the node and its children, for the log.</summary>
        private static string Describe(Transform node, Transform root)
        {
            string path = node.name;
            for (var p = node.parent; p != null && p != root.parent; p = p.parent) path = p.name + "/" + path;
            var parts = new List<string>();
            foreach (var t in node.GetComponentsInChildren<Transform>(true))
            {
                var names = new List<string>();
                foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) names.Add(c.GetType().Name);
                parts.Add(t.name + "[" + string.Join(",", names) + "]");
            }
            return path + " :: " + string.Join(" ", parts);
        }

    }
}
