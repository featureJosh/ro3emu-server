using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Menu input the stock panels expect from a mouse and <see cref="UiNav"/> alone cannot send.
    /// <list type="bullet">
    /// <item>MenuRightClick (default R3): a right click on the focus. The Lua wrappers SetOnRightClick (Lua_UIDragButton.lua:529-557,
    /// Lua_UIClickEventNew.lua:348-367) listen to the C# components' onPointerRightClick, which the component raises from
    /// OnPointerClick with PointerEventData.button == Right (inferred from the Obf_Gf.cs:45763 / 46000 member names; the
    /// component itself is in Happyengine.UI.Framework, not decompiled here).</item>
    /// <item>Double click: two ✕ presses on the same focus within MenuDoubleClickWindow seconds (default 0.3) send the second
    /// as clickCount 2, like a mouse; MenuDoubleClick (default unbound) sends click 1 then click 2 in one press. The siege
    /// permission list adds a player only on right or double click (UI_Sys_Siege_PermissionManage_PlayerWidgetCtrl.lua:47, :56
    /// -> _OnAddPCClick).</item>
    /// <item>Right stick, left/right: swipes the model zone in the focused window (the equip window's m_kSWIPEZONE_Player,
    /// UI_Sys_EquipInfoWindowCtrl.lua:847-853 SetOnSwipeXMul -> _onModelSwipe: 8 facings, SetLockToward) by simulating a drag
    /// on the UISwipeZone. Up/down still scrolls.</item>
    /// <item>Idle guide: LC_GuideManager._hasIdleGuidePlayerInput reads legacy Input.GetMouseButton(0) / Input.anyKey
    /// (LC_GuideManager.lua:3888-3902), which pad input may not set; pad activity resets m_fIdleGuideTimer and ends a running idle
    /// guide, as that function's true result does (_onUpdateIdleGuide, LC_GuideManager.lua:3826-3850).</item>
    /// </list>
    /// </summary>
    internal static class MenuInput
    {
        private static readonly MenuInputLogic.ClickCounter Counter = new MenuInputLogic.ClickCounter();

        /// <summary>clickCount for a ✕ press on the focus (2 on a quick repeat).</summary>
        internal static int ConfirmCount(Component focus) =>
            focus == null ? 1 : Counter.Press(focus.GetInstanceID(), Time.unscaledTime, Controller.Setting("MenuDoubleClickWindow", 0.3f));

        /// <summary>MenuRightClick / MenuDoubleClick on the focus; true when one was sent.</summary>
        internal static bool Secondary(Gamepad pad, Component focus)
        {
            if (focus == null) return false;
            if (UiNav.Pressed(pad, "MenuRightClick", "rightStickPress"))
            {
                Counter.Reset();
                UiNav.Click(focus, PointerEventData.InputButton.Right, 1);
                return true;
            }
            string bound = Controller.FirstBinding("MenuDoubleClick");
            if (bound != null && UiNav.Pressed(pad, "MenuDoubleClick", "none"))
            {
                Counter.Reset();
                UiNav.Click(focus, PointerEventData.InputButton.Left, 1);
                UiNav.Click(focus, PointerEventData.InputButton.Left, 2);
                return true;
            }
            return false;
        }

        // ---- model rotate ----

        private static UISwipeZone _zone;
        private static PointerEventData _drag;
        private static float _offset;

        /// <summary>The visible swipe zone in the window, or null.</summary>
        internal static UISwipeZone ZoneIn(Transform panel)
        {
            if (panel == null) return null;
            if (_zone != null && _zone.isActiveAndEnabled && _zone.transform.IsChildOf(panel)) return _zone;
            _zone = null;
            foreach (var z in panel.GetComponentsInChildren<UISwipeZone>(false))
            {
                var r = UiNav.RectOf(z);
                if (z.isActiveAndEnabled && r.width > 8 && r.height > 8) { _zone = z; break; }
            }
            return _zone;
        }

        /// <summary>Right stick x swipes the zone; true while the stick is swiping (so the caller leaves scrolling to y).</summary>
        internal static bool Rotate(Gamepad pad, Transform panel)
        {
            var stick = pad.rightStick.ReadValue();
            float dead = Controller.Setting("Deadzone", 0.2f);
            float dx = Math.Abs(stick.x) > Math.Abs(stick.y) ? MenuInputLogic.SwipeDelta(stick.x, dead, Controller.Setting("MenuRotateSpeed", 600f), Time.unscaledDeltaTime) : 0f;
            var zone = dx != 0f ? ZoneIn(panel) : null;
            if (zone == null || EventSystem.current == null) { EndSwipe(); return false; }
            var go = zone.gameObject;
            if (_drag == null || _drag.pointerDrag != go || Math.Abs(_offset) > 120f)
            {
                EndSwipe();
                var start = UiNav.RectOf(zone).center;
                _drag = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = start, pressPosition = start, pointerPress = go, pointerDrag = go };
                _offset = 0f;
                ExecuteEvents.Execute(go, _drag, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(go, _drag, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(go, _drag, ExecuteEvents.beginDragHandler);
            }
            _drag.position += new Vector2(dx, 0f);
            _drag.delta = new Vector2(dx, 0f);
            _offset += dx;
            ExecuteEvents.Execute(go, _drag, ExecuteEvents.dragHandler);
            return true;
        }

        internal static void EndSwipe()
        {
            if (_drag == null) return;
            var go = _drag.pointerDrag;
            if (go != null)
            {
                ExecuteEvents.Execute(go, _drag, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(go, _drag, ExecuteEvents.pointerUpHandler);
            }
            _drag = null;
        }

        // ---- idle guide ----

        private static float _idleAt = float.NegativeInfinity;

        /// <summary>From UiNav.Tick every frame (pad null when controller mode is off).</summary>
        internal static void PadActivity(Gamepad pad)
        {
            if (pad == null || Time.unscaledTime - _idleAt < 0.5f || !Active(pad)) return;
            _idleAt = Time.unscaledTime;
            try
            {
                var guide = LuaPatches.Global?.Obf_RuA<Obf_R.Obf_ff>("LC_GuideManager");
                if (guide == null) return;
                guide.Obf_luA<string, double>("m_fIdleGuideTimer", 0.0);
                if (guide.Obf_RuA<object>("m_bHasRunningIdleGuide") is bool running && running)
                    guide.Obf_RuA<Obf_R.Obf_Ef>("_tryFinishIdleGuide")?.Obf_HuA(guide);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.MenuInput] idle guide mark failed: " + e.Message); }
        }

        private static bool Active(Gamepad pad)
        {
            float dead = Controller.Setting("Deadzone", 0.2f);
            return pad.leftStick.ReadValue().magnitude > dead || pad.rightStick.ReadValue().magnitude > dead
                || pad.leftTrigger.ReadValue() > 0.3f || pad.rightTrigger.ReadValue() > 0.3f
                || pad.buttonSouth.isPressed || pad.buttonEast.isPressed || pad.buttonWest.isPressed || pad.buttonNorth.isPressed
                || pad.leftShoulder.isPressed || pad.rightShoulder.isPressed || pad.startButton.isPressed || pad.selectButton.isPressed
                || pad.leftStickButton.isPressed || pad.rightStickButton.isPressed || pad.dpad.ReadValue() != Vector2.zero;
        }
    }
}
