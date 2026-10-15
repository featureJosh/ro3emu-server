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
    /// </list>
    /// </summary>
    public static class UiTweaks
    {
        private static readonly string[] ModeWidgets = { "UI_Sys_SetModeWidget_PC(Clone)", "UI_Sys_SetModeWidget(Clone)" };
        private const string ModeList = "Root/Content_RTransform/Pop_Center/Root/Pan_Content/Pan_BgMask/Content_RTransform";
        private const string MobileColumn = "PCMobile_RTransform";

        private static readonly HashSet<int> Done = new HashSet<int>();
        private static int _frame;

        /// <summary>Called every frame; looks for the views every 10th frame (GameObject.Find only sees active objects).</summary>
        public static void Tick()
        {
            Diagnostics.AutoDump();
            TextFit.Tick();
            Controller.Tick();
            if (++_frame % 10 != 0) return;
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
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.UiTweaks] " + e);
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
            var texts = mobile.GetComponentsInChildren<TMPro.TMP_Text>(true);
            int set = 0;
            foreach (var text in texts)
            {
                Debug.Log("[Ro3Emu.UiTweaks] mode select Mobile text " + text.name + ": " + text.text);
                if (text.name.Contains("Content")) { text.enableAutoSizing = true; text.text = ControllerBlurb; set++; }
                else if (text.name.Contains("Model") || text.name.Contains("Mode")) { text.text = "Controller"; set++; }
            }
            if (set > 0) Debug.Log("[Ro3Emu.UiTweaks] mode select: Mobile column relabelled Controller (" + set + " text(s))");
            return set > 0;
        }

        private const string ControllerBlurb = "Modern PC controls on a gamepad. Left stick moves, right stick turns the camera, "
            + "View/Select toggles a cursor for menus. Remap in ro3emu/controller.ini.";
    }
}
