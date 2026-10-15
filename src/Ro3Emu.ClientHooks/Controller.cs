using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Gamepad play. Every game key is a named Unity Input System action (InputSystemManager's "RO3Current" map, plus the
    /// fixed-key map "RO3_Fixed"; names are enum_InputKey: MoveForward, NormalSkill_01, SwitchTarget, Esc...), so gamepad
    /// controls are added as extra bindings on those actions and fire the same Lua callbacks as the keyboard. The map is
    /// rebuilt on mode switches and a key rebind erases the action's bindings, so <see cref="Sync"/> re-adds them.
    /// <list type="bullet">
    /// <item>Bindings: &lt;client&gt;/ro3emu/controller.ini (written with defaults on first use, reloaded when saved).</item>
    /// <item>Right stick: camera, through MyInput's own drag events (Obf_Sj start, Obf_sj delta, Obf_tj end); held
    /// CameraZoom + right stick: zoom (Obf_Wj, Obf_wj), like the mouse wheel.</item>
    /// <item>CursorMode toggles a virtual mouse for menus: left stick moves the OS cursor, LeftClick/RightClick click,
    /// right stick scrolls. The client reads the legacy mouse (Input.mousePosition), so it is driven through user32
    /// (works under Wine/Proton).</item>
    /// <item>On only while "Controller" is the chosen mode (<see cref="Enabled"/>): the mode select's hidden Mobile
    /// column is that option (UiTweaks relabels it, <see cref="InstallModeSelect"/> saves it as Modern PC + this flag).</item>
    /// </list>
    /// </summary>
    public static class Controller
    {
        private const string PrefKey = "ro3emu.controller";
        private const string Pad = "<Gamepad>/";

        private static readonly Dictionary<string, List<string>> Binds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> Tune = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<InputAction> Actions = new List<InputAction>();
        private static string _path;
        private static DateTime _loaded;
        private static int _frame;
        private static bool? _enabled;
        private static bool _cursor, _dragging, _zooming, _synced;
        private static float _zoomLast, _scroll;
        private static Vector2 _cursorRest;

        /// <summary>The "Controller" mode is chosen (saved in PlayerPrefs).</summary>
        public static bool Enabled
        {
            get => _enabled ??= PlayerPrefs.GetInt(PrefKey, 0) == 1;
            set
            {
                if (Enabled == value) return;
                _enabled = value;
                PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
                PlayerPrefs.Save();
                _cursor = false;
                Debug.Log("[Ro3Emu.Controller] controller mode " + (value ? "on" : "off"));
            }
        }

        /// <summary>From UiTweaks.Tick, every frame.</summary>
        public static void Tick()
        {
            try
            {
                bool on = Enabled;
                if (++_frame % 30 == 0)
                {
                    if (on) Load();
                    if (on || _synced) Sync(on);
                }
                var pad = Gamepad.current;
                if (!on || pad == null || !Application.isFocused) { EndCamera(); return; }
                if (Pressed(pad, "CursorMode"))
                {
                    _cursor = !_cursor;
                    EndCamera();
                    Sync(true);
                    Debug.Log("[Ro3Emu.Controller] cursor mode " + (_cursor ? "on" : "off"));
                }
                if (_cursor) Cursor(pad); else Camera(pad);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.Controller] " + e.GetType().Name + ": " + e.Message);
                _frame = 0;
            }
        }

        private static void Load()
        {
            if (_path == null)
            {
                _path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "ro3emu", "controller.ini");
                if (!File.Exists(_path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, ControllerIni.Defaults);
                }
            }
            var stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _loaded) return;
            _loaded = stamp;
            ControllerIni.Parse(File.ReadAllLines(_path), Binds, Tune);
            Debug.Log("[Ro3Emu.Controller] " + _path + ": " + Binds.Count + " action(s) bound, " + Tune.Count + " setting(s)");
        }

        /// <summary>Makes every game action carry exactly its configured gamepad bindings (none when off). In cursor mode
        /// the left stick and the click buttons belong to the cursor, so game bindings on them are left out.</summary>
        private static void Sync(bool on)
        {
            Actions.Clear();
            InputSystem.ListEnabledActions(Actions);
            int changed = 0;
            foreach (var action in Actions)
            {
                var map = action.actionMap?.name;
                if (map != "RO3Current" && map != "RO3_Fixed") continue;
                var want = new List<string>();
                if (on && Binds.TryGetValue(action.name, out var paths))
                    foreach (var p in paths) if (!(_cursor && CursorOwns(p))) want.Add(p);
                var have = new List<string>();
                foreach (var b in action.bindings) if (b.path != null && b.path.StartsWith(Pad)) have.Add(b.path);
                if (have.Count == want.Count && have.TrueForAll(want.Contains)) continue;
                for (int i = action.bindings.Count - 1; i >= 0; i--)
                    if (action.bindings[i].path != null && action.bindings[i].path.StartsWith(Pad)) action.ChangeBinding(i).Erase();
                foreach (var p in want) action.AddBinding(p);
                changed++;
            }
            _synced = on;
            if (changed > 0) Debug.Log("[Ro3Emu.Controller] gamepad bindings updated on " + changed + " action(s)" + (_cursor ? " (cursor mode)" : ""));
        }

        private static bool CursorOwns(string path) =>
            path.StartsWith(Pad + "leftStick/") || Uses("LeftClick", path) || Uses("RightClick", path);

        private static bool Uses(string name, string path) => Binds.TryGetValue(name, out var list) && list.Contains(path);

        private static ButtonControl Button(Gamepad pad, string name, out bool any)
        {
            any = false;
            if (!Binds.TryGetValue(name, out var list)) return null;
            foreach (var p in list)
                if (pad.TryGetChildControl(p.Substring(p.IndexOf('/') + 1)) is ButtonControl b) { any = true; if (b.isPressed || b.wasReleasedThisFrame) return b; }
            return null;
        }

        private static bool Pressed(Gamepad pad, string name) => Button(pad, name, out _)?.wasPressedThisFrame == true;
        private static bool Released(Gamepad pad, string name) => Button(pad, name, out _)?.wasReleasedThisFrame == true;
        private static bool Held(Gamepad pad, string name) => Button(pad, name, out _)?.isPressed == true;

        private static float Get(string name, float fallback) => Tune.TryGetValue(name, out float v) ? v : fallback;

        private static Vector2 Stick(Vector2 v)
        {
            float dead = Get("Deadzone", 0.2f);
            return v.magnitude < dead ? Vector2.zero : v * ((v.magnitude - dead) / (1f - dead) / v.magnitude);
        }

        private static void Camera(Gamepad pad)
        {
            var stick = Stick(pad.rightStick.ReadValue());
            if (Held(pad, "CameraZoom"))
            {
                EndDrag();
                if (stick.y != 0f)
                {
                    _zoomLast = stick.y * Time.deltaTime * 4f;
                    MyInput.Obf_Wj?.Invoke(_zoomLast, true);
                    _zooming = true;
                }
                return;
            }
            EndZoom();
            if (stick == Vector2.zero) { EndDrag(); return; }
            if (!_dragging) { MyInput.Obf_Sj?.Invoke(); _dragging = true; }
            MyInput.Obf_sj?.Invoke(new Vector2(stick.x * Get("CameraSpeedX", 700f), stick.y * Get("CameraSpeedY", 400f)) * Time.deltaTime);
        }

        private static void EndCamera() { EndDrag(); EndZoom(); }

        private static void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            MyInput.Obf_tj?.Invoke();
        }

        private static void EndZoom()
        {
            if (!_zooming) return;
            _zooming = false;
            MyInput.Obf_wj?.Invoke(_zoomLast);
        }

        // ---- virtual mouse (user32; Wine implements these) ----

        private struct Point { public int X, Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);

        private const uint LeftDown = 0x02, LeftUp = 0x04, RightDown = 0x08, RightUp = 0x10, Wheel = 0x800;

        private static void Cursor(Gamepad pad)
        {
            var stick = Stick(pad.leftStick.ReadValue());
            if (stick != Vector2.zero && GetCursorPos(out var at))
            {
                // Sub-pixel motion accumulates so slow stick tilts still move the cursor; screen y grows downwards.
                _cursorRest += new Vector2(stick.x, -stick.y) * (Get("CursorSpeed", 1200f) * Time.deltaTime);
                int dx = (int)_cursorRest.x, dy = (int)_cursorRest.y;
                _cursorRest -= new Vector2(dx, dy);
                if (dx != 0 || dy != 0) SetCursorPos(at.X + dx, at.Y + dy);
            }
            if (Pressed(pad, "LeftClick")) mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            if (Released(pad, "LeftClick")) mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            if (Pressed(pad, "RightClick")) mouse_event(RightDown, 0, 0, 0, UIntPtr.Zero);
            if (Released(pad, "RightClick")) mouse_event(RightUp, 0, 0, 0, UIntPtr.Zero);
            // Right stick scrolls lists: one wheel notch (120) per ~1/8 s at full tilt.
            _scroll += Stick(pad.rightStick.ReadValue()).y * Time.deltaTime * 8f;
            if (Math.Abs(_scroll) >= 1f)
            {
                int notches = (int)_scroll;
                _scroll -= notches;
                mouse_event(Wheel, 0, 0, notches * 120, UIntPtr.Zero);
            }
        }

        // ---- mode select: the Mobile column is "Controller" ----

        /// <summary>A Lua method called as self:Method().</summary>
        public delegate void SelfMethod(Obf_R.Obf_ff self);
        /// <summary>UI_Sys_SetModeWidgetCtrl:Setup(pcPlatformRecord, mode, initMode, panelLayer, panelName).</summary>
        public delegate void SetupMethod(Obf_R.Obf_ff self, object record, object mode, object initMode, object layer, object panel);

        // xLua keeps delegate references weakly; retain the wrappers for the lifetime of the process.
        private static readonly SelfMethod SaveWrapper = SaveGameModeSetting;
        private static readonly SetupMethod SetupWrapper = Setup;
        private static Obf_R.Obf_Ef _save, _setup, _select;
        private static object _mobile, _fashion;

        /// <summary>
        /// UI_Sys_SetModeWidgetCtrl: picking the Mobile column (which UiTweaks relabels "Controller") saves Modern PC
        /// (enum_GameMode.Fashion) with <see cref="Enabled"/> on; any other pick turns it off. On open, a saved Modern
        /// mode shows the Controller column selected while the flag is on. Unhooked, Mobile would switch the client to the
        /// in-development mobile UI after a restart (_isNeedRestart). Evidence: UI/Setup/Controllers/UI_Sys_SetModeWidgetCtrl.lua.
        /// </summary>
        internal static void InstallModeSelect(Obf_R.Obf_ff global, Obf_R.Obf_ff modeWidget)
        {
            var modes = global?.Obf_RuA<Obf_R.Obf_ff>("enum_GameMode");
            _mobile = modes?.Obf_RuA<object>("Mobile");
            _fashion = modes?.Obf_RuA<object>("Fashion");
            _save = modeWidget?.Obf_RuA<Obf_R.Obf_Ef>("_saveGameModeSetting");
            _setup = modeWidget?.Obf_RuA<Obf_R.Obf_Ef>("Setup");
            _select = modeWidget?.Obf_RuA<Obf_R.Obf_Ef>("SelectMode");
            if (_mobile == null || _fashion == null || _save == null || _setup == null || _select == null)
            {
                Debug.LogWarning("[Ro3Emu.Controller] mode-select functions not found; no Controller mode option");
                return;
            }
            var manager = global.Obf_RuA<Obf_R.Obf_ff>("LC_OperationModeManager");
            _setModeType = manager?.Obf_RuA<Obf_R.Obf_Ef>("SetModeType");
            _traceback = global.Obf_LuA<Obf_R.Obf_Ef>("debug.traceback");
            if (_setModeType != null) manager.Obf_luA<string, SetModeMethod>("SetModeType", SetModeWrapper);
            else Debug.LogWarning("[Ro3Emu.Controller] LC_OperationModeManager.SetModeType not found; no mobile-mode guard");
            modeWidget.Obf_luA<string, SelfMethod>("_saveGameModeSetting", SaveWrapper);
            modeWidget.Obf_luA<string, SetupMethod>("Setup", SetupWrapper);
            Debug.Log("[Ro3Emu.Controller] mode select: Mobile column is Controller");
        }

        /// <summary>LC_OperationModeManager:SetModeType(mode).</summary>
        public delegate void SetModeMethod(Obf_R.Obf_ff self, object mode);
        private static readonly SetModeMethod SetModeWrapper = SetModeType;
        private static Obf_R.Obf_Ef _setModeType, _traceback;

        /// <summary>
        /// Every operation-mode change goes through here (the client's own LC_OperationModeManager instance, so the class
        /// stays untouched). While Controller mode is on, Mobile becomes Modern PC: after a Controller pick the client
        /// still ended up in the mobile UI (player report 2026-10-14, Group_MobileRoot active) although gameMode 2 was saved;
        /// each call is logged with a Lua traceback to find where Mobile comes from.
        /// </summary>
        private static void SetModeType(Obf_R.Obf_ff self, object mode)
        {
            string trace = (_traceback?.Obf_HuA("SetModeType", 2) is object[] r && r.Length > 0 ? r[0] as string : null) ?? "";
            bool swap = Enabled && Same(mode, _mobile);
            Debug.Log("[Ro3Emu.Controller] SetModeType(" + (mode == null ? "nil" : mode + ":" + mode.GetType().Name) + ")"
                + (swap ? " -> Fashion (controller mode)" : "") + "\n" + trace);
            _setModeType.Obf_HuA(self, swap ? Convert.ToInt64(_fashion) : mode);
        }

        private static bool Same(object a, object b) => a is IConvertible && b is IConvertible && Convert.ToInt64(a) == Convert.ToInt64(b);

        private static void SaveGameModeSetting(Obf_R.Obf_ff self)
        {
            bool pad = Same(self?.Obf_RuA<object>("m_iSelectMode"), _mobile);
            // Typed as long: pushed as object, Lua got a userdata, not the number 2, so SetModeType's "== Fashion" failed and
            // the client fell through to the mobile UI (player report 2026-10-14; the saved setting still encoded 2).
            if (pad) self.Obf_luA<string, long>("m_iSelectMode", Convert.ToInt64(_fashion));
            Enabled = pad;
            _save.Obf_HuA(self);
        }

        /// <summary>The Mobile column is drawn locked and its card never gets the click, so the column itself becomes a
        /// button that does what the card would: SelectMode(Mobile) on the open mode widget.</summary>
        internal static void MakeColumnSelectable(GameObject column)
        {
            if (column.GetComponent<UnityEngine.UI.Button>() != null) return;
            if (column.GetComponent<UnityEngine.UI.Graphic>() == null)
                column.AddComponent<UnityEngine.UI.Image>().color = new Color(0f, 0f, 0f, 0f); // invisible click target
            column.AddComponent<UnityEngine.UI.Button>().onClick.AddListener(() =>
            {
                if (_widget != null && _select != null) _select.Obf_HuA(_widget, Convert.ToInt64(_mobile));
                Debug.Log("[Ro3Emu.Controller] Controller column clicked" + (_widget == null ? " (no mode widget yet)" : ""));
            });
        }

        private static Obf_R.Obf_ff _widget;

        private static void Setup(Obf_R.Obf_ff self, object record, object mode, object initMode, object layer, object panel)
        {
            _widget = self;
            _setup.Obf_HuA(self, record, mode, initMode, layer, panel);
            if (Enabled && (mode == null || Same(mode, _fashion))) _select.Obf_HuA(self, Convert.ToInt64(_mobile));
        }
    }
}
