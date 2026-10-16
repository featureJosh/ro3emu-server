using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Binds skills to the skill bar with the pad. On PC the player drags a skill icon from the skill panel onto a skill-bar
    /// slot; a pad cannot drag, so the same drag is replayed through the game's own components, and the game's own code does
    /// the rest (checks, the SkillLoad request, the server reply). Nothing here builds a request.
    /// <list type="bullet">
    /// <item>The drag source is a UIDragButton (Happyengine.UI.Framework) whose Lua wrapper Lua_UIDragButton binds the public
    /// Actions onDragBegin / onDragEnd (Lua_UIDragButton.lua:99-115). Begin publishes the item to the asset "DragItemData"
    /// (:274-296); End calls the asset "DragTriggerFunc" with it (:334-352), or, when no trigger was entered and the button
    /// has a drag-outside function, that function (:352-366).</item>
    /// <item>A drop target is the slot's UIDragTrigger, bound by Lua_UIDragTrigger.Add (Lua_UIDragTrigger.lua:22-88) to
    /// onPointerEnter / onPointerExit. Entering (OnPointerEnter, :407-440) sets "DragTriggerFunc" to the slot's progress
    /// function when its check accepts the item. The stock HUD slot (UI_Sys_Main_JoyStickSkillItemWidget_PCCtrl.lua:231-257,
    /// _checkDragItem :1182-1209, _progressDragItem :1210-1278) then sends SkillLoad (LC_SkillDevelopServer.lua:115-215)
    /// as Load into an empty slot or Replace over an occupied one.</item>
    /// <item>Bind: begin on the focused skill cell, enter the trigger of the slot whose button was pressed, end, exit. The
    /// trigger sets m_bIsPointerEnter before it checks (:407), so a refused drop (same skill already there) still counts as
    /// inside a trigger and never reaches the drag-outside Unload. Cancel is a plain end: a list cell has no drag-outside.</item>
    /// <item>Unbind: the stock way is dragging a bound slot out of the bar. The slot's UIDragButton (its drag data is set in
    /// _refreshView, :425-460) gets begin and end without entering a trigger, so _progressDragOutside runs
    /// (:1279-1301: SkillLoad Unload; the last skill is refused by LC_SkillDevelopServer.lua:131-147).</item>
    /// </list>
    /// The slots are the stock HUD's (ControllerHud reads the same widgets and shows the result when the server answers).
    /// A slot is found from its InputBinder (m_inputKey is the action, bound to a pad control in controller.ini), and its
    /// trigger and button are the only ones under the same widget.
    /// </summary>
    internal static class SkillBinding
    {
        private enum Mode { None, Bind, Unbind }

        private sealed class Target
        {
            public string Action, Control;
            public UIDragTrigger Trigger;
            public UIDragButton Button;
        }

        private static readonly FieldInfo CanDragField = typeof(UIDragButton).GetField("canDrag", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly List<Target> Targets = new List<Target>();
        private static Mode _mode;
        private static UIDragButton _source;
        private static int _startFrame;
        private static string _notice;
        private static float _noticeUntil;

        /// <summary>A bind or unbind is waiting for a skill button (UiNav hands it all input).</summary>
        public static bool Active => _mode != Mode.None;

        /// <summary>The skill panel (UI_Sys_SkillPanel and its siblings) is the only place this runs: other windows have
        /// UIDragButtons too (bag cells), with their own drag-outside actions.</summary>
        public static bool InSkillPanel(Transform panel) => panel != null && panel.name.StartsWith("UI_Sys_Skill", StringComparison.Ordinal);

        /// <summary>The focus can be bound: a drag button that can drag.</summary>
        public static bool CanBind(Component focus) => CanDrag(SourceOf(focus));

        /// <summary>The focus itself (UIDragButton is a Button), or the drag button inside the focused cell.</summary>
        private static UIDragButton SourceOf(Component focus) => focus as UIDragButton ?? (focus != null ? focus.GetComponentInChildren<UIDragButton>() : null);

        /// <summary>Enter bind mode for the focused skill cell (unbind false) or unbind mode (focus ignored).</summary>
        public static void Start(Component focus, bool unbind)
        {
            try
            {
                if (_mode != Mode.None) return;
                FindTargets();
                if (Targets.Count == 0) { Notice("No skill bar found"); return; }
                if (!unbind)
                {
                    var button = SourceOf(focus);
                    if (button == null || !CanDrag(button) || button.onDragBegin == null || button.onDragEnd == null) { Notice("Pick a skill that can be bound"); return; }
                    button.onDragBegin();
                    if (!IsListSkill(out string why))
                    {
                        button.onDragEnd(); // clears the drag data again; a list cell has no drag-outside
                        Notice("Pick a skill that can be bound");
                        Debug.Log("[Ro3Emu.SkillBinding] refused: " + why);
                        return;
                    }
                    _source = button;
                }
                _mode = unbind ? Mode.Unbind : Mode.Bind;
                _startFrame = Time.frameCount;
                Debug.Log("[Ro3Emu.SkillBinding] " + _mode + " mode, HUD widget " + (HudActive ? "active" : "inactive") + ", slots " + string.Join(" ", Targets.ConvertAll(t => t.Action + "=" + t.Control)));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.SkillBinding] start failed: " + e);
                Cancel();
            }
        }

        /// <summary>From UiNav while Active, every frame. A skill button applies; Back cancels.</summary>
        public static void Tick(Gamepad pad, bool back)
        {
            if (Time.frameCount == _startFrame) return; // the entry button may itself be a skill button
            if (back || _mode == Mode.Bind && _source == null) { Cancel(); return; }
            foreach (var t in Targets)
            {
                if (t.Trigger == null || t.Button == null) continue; // pooled widgets are rebuilt; the scan at entry is stale
                if (!Controller.Edge(pad.TryGetChildControl(t.Control.Substring(t.Control.IndexOf('/') + 1)) as ButtonControl, true)) continue;
                Apply(t);
                return;
            }
        }

        /// <summary>The mode ends without a request (○, panel closed, controller off).</summary>
        public static void Cancel()
        {
            if (_mode == Mode.None) return;
            try { if (_mode == Mode.Bind && _source != null) _source.onDragEnd?.Invoke(); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SkillBinding] cancel failed: " + e); }
            Debug.Log("[Ro3Emu.SkillBinding] cancelled");
            Reset();
        }

        /// <summary>The mode's prompt (glyphs of the skill buttons, then the text), and ○ Cancel.</summary>
        public static void AddPrompts(List<(string, string)> prompts)
        {
            if (_mode == Mode.None) { if (Time.unscaledTime < _noticeUntil) prompts.Add(("", _notice)); return; }
            string text = _mode == Mode.Bind ? "Press a skill button to bind" : "Press a skill button to unbind";
            int first = prompts.Count;
            foreach (var t in Targets) prompts.Add((t.Control, ""));
            if (prompts.Count > first) prompts[prompts.Count - 1] = (prompts[prompts.Count - 1].Item1, text);
            else prompts.Add(("", text));
            prompts.Add((UiNav.Control("MenuBack", "buttonEast"), "Cancel"));
        }

        // ---- the drop ----

        private static void Apply(Target t)
        {
            try
            {
                if (_mode == Mode.Bind)
                {
                    bool accepted = false;
                    try
                    {
                        t.Trigger.onPointerEnter?.Invoke(); // DragTriggerFunc = the slot's _progressDragItem, if _checkDragItem accepts
                        accepted = DragTriggerFunc() != null;
                        _source.onDragEnd?.Invoke();        // calls it with DragItemData: SkillLoad Load / Replace
                    }
                    finally { t.Trigger.onPointerExit?.Invoke(); }
                    Debug.Log("[Ro3Emu.SkillBinding] bind on " + t.Action + " (" + t.Control + "): " + (accepted ? "accepted, SkillLoad sent" : "refused by the slot"));
                    if (!accepted) Notice("Already in that slot");
                }
                else
                {
                    if (!CanDrag(t.Button) || t.Button.onDragBegin == null || t.Button.onDragEnd == null) { Notice("This slot cannot be changed"); Reset(); return; }
                    t.Button.onDragBegin();
                    bool had = DragData() != null; // the slot's data is only set while a skill is in it
                    t.Button.onDragEnd();          // no trigger entered: the drag-outside function, SkillLoad Unload
                    Debug.Log("[Ro3Emu.SkillBinding] unbind " + t.Action + (had ? "" : ": the slot is empty"));
                    if (!had) Notice("That slot is empty");
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SkillBinding] apply failed: " + e); }
            Reset();
        }

        private static void Reset()
        {
            _mode = Mode.None;
            _source = null;
            Targets.Clear();
        }

        private static void Notice(string text)
        {
            _notice = text;
            _noticeUntil = Time.unscaledTime + 2.5f;
            Debug.Log("[Ro3Emu.SkillBinding] " + text);
        }

        private static bool CanDrag(UIDragButton b) => b != null && b.isActiveAndEnabled && b.interactable && (CanDragField == null || CanDragField.GetValue(b) is bool on && on);

        // ---- slots ----

        /// <summary>The HUD's skill slots whose action has a pad control: per InputBinder, the first ancestor widget that
        /// holds a UIDragTrigger, which must be the slot's only one (and have a single UIDragButton).</summary>
        private static void FindTargets()
        {
            Targets.Clear();
            var widget = GameObject.Find(ControllerHud.SkillWidget);
            if (widget == null) // inactive while a full-screen window is open
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                    if (t.gameObject.scene.name != null && t.name == ControllerHud.SkillWidget) { widget = t.gameObject; break; }
            if (widget == null) return;
            string back = UiNav.Control("MenuBack", "buttonEast"), confirm = UiNav.Control("MenuConfirm", "buttonSouth");
            var seen = new HashSet<string>();
            foreach (var binder in widget.GetComponentsInChildren<InputBinder>(true))
            {
                string action = ControllerHud.KeyField.GetValue(binder) as string;
                string control = Controller.FirstBinding(action);
                if (string.IsNullOrEmpty(action) || control == null || control == back || control == confirm || !seen.Add(action)) continue;
                for (var node = binder.transform; node != null && node != widget.transform; node = node.parent)
                {
                    var triggers = node.GetComponentsInChildren<UIDragTrigger>(true);
                    if (triggers.Length == 0) continue;
                    var buttons = node.GetComponentsInChildren<UIDragButton>(true);
                    if (triggers.Length == 1 && buttons.Length == 1) Targets.Add(new Target { Action = action, Control = control, Trigger = triggers[0], Button = buttons[0] });
                    else Debug.Log("[Ro3Emu.SkillBinding] " + action + ": " + triggers.Length + " triggers, " + buttons.Length + " drag buttons under " + node.name + "; skipped");
                    break;
                }
            }
        }

        // ---- the game's drag data (Lua) ----

        private static bool HudActive => GameObject.Find(ControllerHud.SkillWidget) != null;

        private static object First(object[] r) => r != null && r.Length > 0 ? r[0] : null;

        /// <summary>LC_AssetManager asset "DragItemData": { iItemType, iSourceType, kData = { iSkillId, iLevel, iSlotIndex } }, or null.</summary>
        private static Obf_R.Obf_ff DragData() => Asset("DragItemData") as Obf_R.Obf_ff;

        /// <summary>LC_AssetManager asset "DragTriggerFunc": set by the trigger the drag entered when its check accepted the item.</summary>
        private static object DragTriggerFunc() => Asset("DragTriggerFunc");

        private static object Asset(string name)
        {
            var location = LuaPatches.Global?.Obf_RuA<Obf_R.Obf_ff>("ServerLocation");
            var assets = First(location?.Obf_RuA<Obf_R.Obf_Ef>("GetServer")?.Obf_HuA(location, "LC_AssetManager")) as Obf_R.Obf_ff;
            return First(assets?.Obf_RuA<Obf_R.Obf_Ef>("GetAssetData")?.Obf_HuA(assets, name));
        }

        /// <summary>The drag data after begin is a skill from the skill list (iSlotIndex 0, as SetItemWidget_PC sets it,
        /// UI_Sys_Skill_SetItemWidget_PCCtrl.lua:253-359). A skill-bar cell (iSlotIndex above 0) has a drag-outside Unload, so
        /// it is refused: cancelling it would unbind.</summary>
        private static bool IsListSkill(out string why)
        {
            var data = DragData();
            if (data == null) { why = "no DragItemData after begin"; return false; }
            long type = LuaPatches.ToLong(data.Obf_RuA<object>("iItemType"));
            long skillType = LuaPatches.ToLong(LuaPatches.Global.Obf_LuA<object>("enum_DragItemType.Skill"));
            var skill = data.Obf_RuA<Obf_R.Obf_ff>("kData");
            long slot = skill == null ? long.MinValue : LuaPatches.ToLong(skill.Obf_RuA<object>("iSlotIndex"));
            why = "item type " + type + " (skill " + skillType + "), skill " + skill?.Obf_RuA<object>("iSkillId") + " level " + skill?.Obf_RuA<object>("iLevel") + " slot " + slot;
            Debug.Log("[Ro3Emu.SkillBinding] drag data: " + why);
            return type == skillType && slot == 0;
        }
    }
}
