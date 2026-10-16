using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Assign a bag item to a D-pad slot with the pad: the stock bag does it by dragging the item cell onto a slot of the item
    /// shortcut bar (no tooltip button exists: UI_Com_ItemTipsWidgetCtrl_Operation's button list has no shortcut entry).
    /// In the bag, the pad item focus + MenuAssign (default □, shared with the skill window) starts assign mode and MenuUnassign
    /// (default △) starts clear mode directly; a D-pad direction picks the slot (assign it / empty it), ○ cancels.
    /// <list type="bullet">
    /// <item>Slot n is PropsItem0n of the stock UI_Sys_Main_PropsWidget_PC (hidden, still running; the controller HUD's D-pad
    /// potions read the same widgets) and the direction is the ShortCut_n binding in controller.ini (default left = 1, right = 2).</item>
    /// <item>Assign runs the mouse drop through the game's own drag code, no item data of ours: the cell's UIDragButton.onDragBegin
    /// (Lua_UIDragButton.OnDragBegin, re/clientB/lua-src/UI/Component/Lua_UIDragButton.lua:332, publishes "DragItemData" and
    /// asks every drop trigger), the slot's UIDragTrigger.onPointerEnter (Lua_UIDragTrigger.OnPointerEnter, Lua_UIDragTrigger.lua:463,
    /// takes the slot as the drop target), then the cell's onDragEnd (Lua_UIDragButton.OnDragEnd, Lua_UIDragButton.lua:429, calls the
    /// target), which is UI_Sys_Main_PropsItemWidgetCtrl._onDragIn (UI/Main/Controllers/UI_Sys_Main_PropsItemWidgetCtrl.lua:623):
    /// LC_ShortcutBarHelper.CheckCanPutIn (Logic/ShortcutBar/LC_ShortcutBarHelper.lua:111, with its own "cannot put in" tip) and
    /// LC_ShortcutBarService.SaveSlot(slot, instanceId), which sends SHORTCUT_BAR_SAVE (LC_ShortcutBarService.lua:31).</item>
    /// <item>The cell's drag data is set when the cell shows its item (UI_Com_ItemNewBaseCtrl._refreshView, UI/Common/Controllers/
    /// UI_Com_ItemNewBaseCtrl.lua:405: SetDragItemData(GetDragItemData(...))), not on press, so the replay needs no pointer events.</item>
    /// <item>Clear is the call a drag off a slot makes: _onDragOutEnd (PropsItemWidgetCtrl.lua:645) = SaveSlot(slot, 0).</item>
    /// </list>
    /// </summary>
    internal static class ItemHotkey
    {
        private static UIDragButton _cell;
        private static bool _clear;
        private static readonly List<(string control, int slot)> Slots = new List<(string, int)>();

        /// <summary>Assign mode is on: UiNav gives this class the whole pad.</summary>
        internal static bool Active => _cell != null;

        /// <summary>A bag item cell (the bag windows are UI_Sys_Bag*; the equip window is not).</summary>
        internal static bool CanStart(Component focus)
        {
            if (!(focus is UIDragButton)) return false;
            for (var t = focus.transform; t != null; t = t.parent)
                if (t.name.StartsWith("UI_Sys_Bag", StringComparison.Ordinal)) return true;
            return false;
        }

        internal static void Cancel()
        {
            if (_cell == null) return;
            _cell = null;
            _clear = false;
        }

        /// <summary>From UiNav every frame with the pad focus. True while assign mode owns the pad, also on the frames it starts
        /// and ends, so the same button edge does not also drive the menu (○ would tap Esc, a direction would move the focus).</summary>
        internal static bool Tick(Gamepad pad, Component focus, List<(string, string)> prompts)
        {
            prompts.Clear();
            if (_cell == null)
            {
                if (!CanStart(focus)) return false;
                bool assign = UiNav.Pressed(pad, "MenuAssign", "buttonWest"), clear = UiNav.Pressed(pad, "MenuUnassign", "buttonNorth");
                if (!assign && !clear) return false;
                _cell = (UIDragButton)focus;
                _clear = !assign;
                Debug.Log("[Ro3Emu.ItemHotkey] " + (_clear ? "clear" : "assign") + " mode on " + focus.name);
                Collect();
                Fill(prompts);
                return true; // the start press must not also drive the menu
            }
            if (!_cell.isActiveAndEnabled) { Cancel(); return false; } // the bag closed or refreshed
            Collect();
            if (UiNav.Pressed(pad, "MenuBack", "buttonEast")) { Cancel(); return true; }
            foreach (var (control, slot) in Slots)
                if (Controller.Edge(pad.TryGetChildControl(control.Substring(control.IndexOf('/') + 1)) as ButtonControl, true))
                {
                    if (_clear) Clear(slot); else Assign(slot);
                    Cancel();
                    return true;
                }
            Fill(prompts);
            return true;
        }

        private static void Fill(List<(string, string)> prompts)
        {
            for (int i = 0; i < Slots.Count; i++)
                prompts.Add((Slots[i].control, i == Slots.Count - 1 ? (_clear ? "Press a D-pad direction to clear" : "Press a D-pad direction") : ""));
            prompts.Add((UiNav.Control("MenuBack", "buttonEast"), "Cancel"));
        }

        /// <summary>The D-pad directions that have a slot: ShortCut_n bound to a D-pad control and the stock PropsItem0n present.</summary>
        private static void Collect()
        {
            Slots.Clear();
            for (int n = 1; n <= 6; n++)
            {
                string control = Controller.FirstBinding("ShortCut_" + n);
                if (control != null && control.StartsWith("<Gamepad>/dpad/", StringComparison.Ordinal) && Trigger(n) != null) Slots.Add((control, n));
            }
        }

        private static UIDragTrigger Trigger(int slot)
        {
            var props = GameObject.Find(ControllerHud.PropsWidget);
            if (props == null) return null;
            foreach (var t in props.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("PropsItem0" + slot, StringComparison.Ordinal) && t.GetComponentInChildren<UIDragTrigger>(true) is UIDragTrigger trigger) return trigger;
            return null;
        }

        private static void Assign(int slot)
        {
            var trigger = Trigger(slot);
            if (trigger == null) return;
            Debug.Log("[Ro3Emu.ItemHotkey] assign " + _cell.name + " -> slot " + slot + " (drop target " + trigger.transform.parent?.name + "/" + trigger.name + ")");
            try
            {
                _cell.onDragBegin?.Invoke();
                trigger.onPointerEnter?.Invoke();
                _cell.onDragEnd?.Invoke();
            }
            finally { trigger.onPointerExit?.Invoke(); } // the pointer leaves the slot, as after a real drop
        }

        private static void Clear(int slot)
        {
            var save = LuaPatches.Global?.Obf_LuA<Obf_R.Obf_Ef>("package.loaded.lc_shortcutbarservice.SaveSlot");
            if (save == null) { Debug.Log("[Ro3Emu.ItemHotkey] clear: LC_ShortcutBarService not loaded"); return; }
            Debug.Log("[Ro3Emu.ItemHotkey] clear slot " + slot);
            save.Obf_HuA((long)slot, 0L); // long: an int boxed as object reaches Lua as userdata (see Controller)
        }
    }
}
