using System;
using System.Collections.Generic;
using System.Globalization;
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
        private static bool _cursor, _dragging, _zooming, _synced, _menu;
        private static float _zoomLast, _scroll;
        private static Vector2 _cursorRest;

        private static bool _loggedForce;
        private static bool Saved => _enabled ??= PlayerPrefs.GetInt(PrefKey, 0) == 1;

        /// <summary>Cursor mode is on (the pad drives the mouse).</summary>
        public static bool CursorOn => _cursor;

        /// <summary>Dev switch: RO3EMU_FORCE_CONTROLLER=1 (tools/linux/dev/restart-client.sh sets it) turns controller mode on for
        /// the session without touching the saved setting, so automated tests reach the pad-driven menus whatever the player chose.</summary>
        private static readonly bool Forced = Environment.GetEnvironmentVariable("RO3EMU_FORCE_CONTROLLER") == "1";

        /// <summary>The "Controller" mode is chosen (saved in PlayerPrefs), or forced for the session.</summary>
        public static bool Enabled
        {
            get => Forced || Saved;
            set
            {
                if (Saved == value) return;
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
                if (Forced && !_loggedForce) { _loggedForce = true; Debug.Log("[Ro3Emu.Controller] controller mode forced by RO3EMU_FORCE_CONTROLLER (saved: " + (Saved ? 1 : 0) + ")"); }
                UpdateEdges();
                if (on) ControllerSkin.Tick();
                ControllerHud.Tick(on);
                if (++_frame % 30 == 0)
                {
                    if (on) Load();
                    if (on || _synced) Sync(on);
                    Glyphs.Refresh(on);
                }
                var pad = Gamepad.current;
                if (!on || pad == null || !(Application.isFocused || Diagnostics.IgnoreFocus)) { EndCamera(); UiNav.Tick(null, false); Interact.Tick(null, false); return; }
                if (ControllerSettings.Open)
                {
                    UiNav.Tick(pad, false);
                    ControllerSettings.Tick(pad);
                }
                else
                {
                    WheelAndCursorButton(pad);
                    if (Wheel.Open)
                    {
                        UiNav.Tick(pad, false);
                        WheelPrompts.Clear();
                        Wheel.Tick(pad, WheelPrompts);
                        Overlay.SetPrompts(Wheel.Open ? WheelPrompts : null);
                    }
                    else UiNav.Tick(pad, !_cursor);
                }
                bool menu = UiNav.Active || Wheel.Open || ControllerSettings.Open;
                if (menu != _menu) { _menu = menu; EndCamera(); Sync(true); }
                Interact.Tick(pad, !_menu && !_cursor);
                if (!_menu && !_cursor && Pressed(pad, "QuestNav")) QuestNav.Go();
                if (ControllerSettings.Open) EndCamera();
                else if (_cursor) Cursor(pad);
                else if (!_menu) Camera(pad);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.Controller] " + e.GetType().Name + ": " + e.Message);
                _frame = 0;
            }
        }

        private static readonly List<(string, string)> WheelPrompts = new List<(string, string)>();
        private static float _wheelDown = -1f;
        private static bool _held;

        /// <summary>
        /// MenuWheel (default Create / View) opens the radial menu on a tap, in the world (not over a menu). CursorMode on
        /// the same control is a 0.5 s hold; on its own control it is a plain press.
        /// </summary>
        private static void WheelAndCursorButton(Gamepad pad)
        {
            string wheel = FirstBinding("MenuWheel") ?? "<Gamepad>/select", cursor = FirstBinding("CursorMode");
            bool shared = cursor == wheel;
            if (!shared && Pressed(pad, "CursorMode")) ToggleCursor();
            if (Wheel.Open) { _wheelDown = -1f; return; }
            var button = pad.TryGetChildControl(wheel.Substring(wheel.IndexOf('/') + 1)) as ButtonControl;
            if (button == null) return;
            if (button.isPressed)
            {
                if (_wheelDown < 0f) { _wheelDown = Time.unscaledTime; _held = false; }
                else if (shared && !_held && Time.unscaledTime - _wheelDown > 0.5f) { _held = true; ToggleCursor(); }
            }
            else if (_wheelDown >= 0f)
            {
                _wheelDown = -1f;
                if (!_held && !_cursor && !UiNav.Active) { EndCamera(); Wheel.Show(); }
            }
        }

        private static void ToggleCursor()
        {
            _cursor = !_cursor;
            EndCamera();
            Sync(true);
            Debug.Log("[Ro3Emu.Controller] cursor mode " + (_cursor ? "on" : "off"));
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
                    foreach (var p in paths)
                        if (!(_cursor && CursorOwns(p)) && !(_menu && action.name != "ClosePanel")) want.Add(p); // menus: UiNav has the pad; ClosePanel is Esc
                var have = new List<string>();
                foreach (var b in action.bindings) if (b.path != null && b.path.StartsWith(Pad)) have.Add(b.path);
                if (have.Count == want.Count && have.TrueForAll(want.Contains)) continue;
                for (int i = action.bindings.Count - 1; i >= 0; i--)
                    if (action.bindings[i].path != null && action.bindings[i].path.StartsWith(Pad)) action.ChangeBinding(i).Erase();
                foreach (var p in want) action.AddBinding(p);
                changed++;
            }
            _synced = on;
            if (on && !_reportedMissing)
            {
                // Configured game actions with no enabled action of that name (typo, or a map we do not bind into).
                _reportedMissing = true;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in Actions) names.Add(a.name);
                var missing = new List<string>();
                foreach (var name in Binds.Keys) if (!names.Contains(name) && !Pseudo.Contains(name)) missing.Add(name);
                if (missing.Count > 0) Debug.LogWarning("[Ro3Emu.Controller] bound in controller.ini but not an enabled game action: " + string.Join(", ", missing));
            }
            if (changed > 0) Debug.Log("[Ro3Emu.Controller] gamepad bindings updated on " + changed + " action(s)" + (_cursor ? " (cursor mode)" : _menu ? " (menu)" : ""));
        }

        /// <summary>The first gamepad control bound to a game or controller action, or null.</summary>
        public static string FirstBinding(string action) =>
            !string.IsNullOrEmpty(action) && Binds.TryGetValue(action, out var list) && list.Count > 0 ? list[0] : null;

        internal static List<string> ConfigurableActions()
        {
            var names = new HashSet<string>(Binds.Keys, StringComparer.OrdinalIgnoreCase);
            var enabled = new List<InputAction>();
            InputSystem.ListEnabledActions(enabled);
            foreach (var action in enabled)
                if (action.actionMap?.name == "RO3Current" || action.actionMap?.name == "RO3_Fixed") names.Add(action.name);
            var sorted = new List<string>(names);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return sorted;
        }

        internal static string BindingConflict(string path, string except)
        {
            foreach (var pair in Binds)
                if (!string.Equals(pair.Key, except, StringComparison.OrdinalIgnoreCase) && pair.Value.Contains(path))
                    return pair.Key;
            return null;
        }

        internal static void SaveSetting(string key, string value, bool replaceAll = false)
        {
            Load();
            var lines = new List<string>(File.ReadAllLines(_path));
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#") || line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq < 0 || !string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                if (!replaced && value != null) { lines[i] = key + " = " + value; replaced = true; }
                else if (replaceAll || value == null || !value.StartsWith(Pad, StringComparison.Ordinal))
                { lines.RemoveAt(i); i--; }
            }
            if (!replaced && value != null) lines.Add(key + " = " + value);
            var temporary = _path + ".tmp";
            File.WriteAllLines(temporary, lines);
            try { File.Replace(temporary, _path, null); }
            catch (PlatformNotSupportedException) { File.Copy(temporary, _path, true); File.Delete(temporary); }
            _loaded = DateTime.MinValue;
            Load();
            Sync(Enabled);
        }

        internal static void ResetSetting(string key)
        {
            var defaults = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var numbers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            ControllerIni.Parse(ControllerIni.Defaults.Split('\n'), defaults, numbers);
            string value = defaults.TryGetValue(key, out var paths) && paths.Count > 0 ? paths[0]
                : numbers.TryGetValue(key, out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
            SaveSetting(key, value, true);
        }

        private static bool _reportedMissing;
        private static readonly HashSet<string> Pseudo = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CursorMode", "LeftClick", "RightClick", "CameraZoom", "MenuConfirm", "MenuBack", "MenuTabLeft", "MenuTabRight", "MenuAssign", "MenuUnassign", "MenuWheel", "Interact", "QuestNav" };

        /// <summary>Every enabled Input System action: map, name, bindings (dev command "actions").</summary>
        internal static string DescribeActions()
        {
            var list = new List<InputAction>();
            InputSystem.ListEnabledActions(list);
            var sb = new System.Text.StringBuilder();
            foreach (var a in list)
            {
                sb.Append("\n  ").Append(a.actionMap?.name ?? "-").Append('/').Append(a.name).Append(" [").Append(a.type).Append("]");
                foreach (var b in a.bindings) sb.Append(' ').Append(b.isComposite ? "{" + b.path + "}" : b.path);
            }
            return list.Count + " enabled action(s)" + sb;
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

        private static bool Pressed(Gamepad pad, string name) => Edge(Button(pad, name, out _), true);
        private static bool Released(Gamepad pad, string name) => Edge(Button(pad, name, out _), false);

        // Per control: (held last frame, held now). wasPressedThisFrame missed short presses here (the client's input
        // updates do not line up with Update; a scripted press was never seen), so edges are computed once per frame,
        // the same for every check in that frame. Every control seen once is sampled every frame (UpdateEdges): sampled
        // only when looked at, a press seen by one caller (UiNav in a menu) left a stale "held" for the next (Interact),
        // which then missed every other press.
        private static readonly Dictionary<ButtonControl, (bool before, bool now)> Edges = new Dictionary<ButtonControl, (bool, bool)>();
        private static readonly List<ButtonControl> EdgeControls = new List<ButtonControl>();
        private static int _edgeFrame;

        private static void UpdateEdges()
        {
            if (_edgeFrame == Time.frameCount) return;
            _edgeFrame = Time.frameCount;
            foreach (var b in EdgeControls) Edges[b] = (Edges[b].now, b.isPressed);
        }

        /// <summary>The button went down (down = true) or up (down = false) this frame.</summary>
        internal static bool Edge(ButtonControl b, bool down)
        {
            if (b == null) return false;
            UpdateEdges();
            if (!Edges.TryGetValue(b, out var h)) { EdgeControls.Add(b); Edges[b] = h = (b.isPressed, b.isPressed); } // first look: no edge
            return down ? h.now && !h.before : !h.now && h.before;
        }
        private static bool Held(Gamepad pad, string name) => Button(pad, name, out _)?.isPressed == true;

        /// <summary>A number setting from controller.ini.</summary>
        public static float Setting(string name, float fallback) => Get(name, fallback);

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

        private const uint LeftDown = 0x02, LeftUp = 0x04, RightDown = 0x08, RightUp = 0x10, MouseWheel = 0x800;

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
                mouse_event(MouseWheel, 0, 0, notches * 120, UIntPtr.Zero);
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
            var picked = self?.Obf_RuA<object>("m_iSelectMode");
            bool pad = Same(picked, ControllerMode) || Same(picked, _mobile);
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
                if (_widget != null && _select != null) _select.Obf_HuA(_widget, ControllerMode);
                Debug.Log("[Ro3Emu.Controller] Controller column clicked" + (_widget == null ? " (no mode widget yet)" : ""));
            });
        }

        private static Obf_R.Obf_ff _widget;

        /// <summary>Our own mode value for the Controller card; enum_GameMode has OldSchool 1, Fashion 2, Mobile 3. It never
        /// reaches the client's mode logic: the save turns it into Fashion.</summary>
        private const long ControllerMode = 4;

        private static void Setup(Obf_R.Obf_ff self, object record, object mode, object initMode, object layer, object panel)
        {
            _widget = self;
            _setup.Obf_HuA(self, record, mode, initMode, layer, panel);
            // The Mobile column's card was created as mode 3 (enum_GameMode.Mobile), and anything in the client that sees 3
            // can switch to the mobile UI. Re-key it as ControllerMode (4), a value the client never tests for.
            var list = self.Obf_RuA<Obf_R.Obf_ff>("m_kSelectWidgetList");
            var card = list?.Obf_ruA<long, Obf_R.Obf_ff>(Convert.ToInt64(_mobile));
            if (card != null)
            {
                list.Obf_luA<long, object>(Convert.ToInt64(_mobile), null);
                list.Obf_luA<long, Obf_R.Obf_ff>(ControllerMode, card);
                card.Obf_luA<string, long>("m_iMode", ControllerMode);
            }
            if (Saved && (mode == null || Same(mode, _fashion))) _select.Obf_HuA(self, ControllerMode);
        }
    }
}
