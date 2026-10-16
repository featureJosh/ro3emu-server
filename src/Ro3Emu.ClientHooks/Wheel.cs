using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Radial menu (Diablo-style) for everything the keyboard reaches with hotkeys: tap MenuWheel (Create / View) in the
    /// world, point with a stick, ✕ (or MenuWheel again) opens, ○ closes. An entry runs the game's own input action, as
    /// its hotkey would: InputSystemManager's private started / performed / canceled handlers (Obf_jn / Obf_Jn / Obf_kn)
    /// are called with the action name, which reaches the same Lua callbacks (LC_InputBinder, Lua_UIPanelManager's
    /// Esc) as a key press. Entries without a hotkey open through the game's own function menu path
    /// (LC_FunctionHelper.OpenFunctionPanelDefault, as UI_Sys_MainFunctionMenuWidgetBaseCtrl._onClickFunction, re/clientB/lua-src/
    /// UI/Main/Controllers/UI_Sys_MainFunctionMenuWidgetBaseCtrl.lua:1083) or the main UI widget's own call (mail, party).
    /// There are more entries than fit one ring, so the wheel pages (MenuTabLeft / MenuTabRight, L1 / R1); an entry whose
    /// function is locked is dimmed, using the game's own check (LC_FunctionHelper.CheckFunctionIsOpen), and picking it pops
    /// the game's own "unlocks at level N" tip.
    /// </summary>
    public static class Wheel
    {
        private enum Kind { Action, Function, Mail, Party, Controller, QuestNav }

        /// <summary>key: an input action name (Kind.Action), or an enum_FunctionType name (Kind.Function).</summary>
        private sealed class Entry
        {
            public readonly string Label, Key;
            public readonly Kind Kind;
            public bool Enabled = true;
            public Entry(string label, Kind kind, string key = null) { Label = label; Kind = kind; Key = key; }
        }

        private static Entry Act(string label, string action) => new Entry(label, Kind.Action, action);
        private static Entry Fn(string label, string function) => new Entry(label, Kind.Function, function);

        // Input actions: re/clientB/lua-src/Config/Define/Lua_GameDefine.lua:9277 (enum_InputKey), their function (for the lock
        // check) from enum_InputKeyFunc (same file :9490). OpenEquip is not wired in LC_InputBinder.lua:30-90, so equipment
        // goes through Equip Growth / Equip Craft. Functions: enum_FunctionType (same file :6572) with a panel mapping in
        // LC_FunctionHelper.FunctionOpenPanelInfo (re/clientB/lua-src/Logic/Function/LC_FunctionHelper.lua:359-1023).
        private const int PageSize = 10;
        private static readonly Entry[] Entries =
        {
            Act("Bag", "OpenBag"), Act("Character", "OpenRole"), Act("Skills", "OpenSkill"), Act("Quests", "OpenTask"),
            new Entry("Go to Quest", Kind.QuestNav),
            Act("Map", "OpenMap"), new Entry("Party", Kind.Party), Act("Friends", "OpenFriend"), Act("Guild", "OpenGuild"),
            Act("Shop", "OpenShop"),

            Act("Auto Battle", "OpenAutoFight"), Act("Equip Growth", "OpenEquipGrowth"), Fn("Equip Craft", "EquipMake"), Act("Talents", "OpenTalent"),
            Act("Cards", "OpenCard"), Act("Pets", "OpenPet"), Fn("Mounts", "Mount"), Act("Totems", "OpenTotem"),
            Act("Soul Echo", "OpenSoulEcho"), Act("Wardrobe", "OpenWardrobe"), Act("Collection", "OpenCollection"),

            Act("Life Jobs", "OpenLifeJob"), Act("Monster Hunt", "OpenMonsterHunt"), Act("Activities", "OpenActivity"),
            Act("Daily Tasks", "OpenMustDo"), Act("Trading", "OpenTrading"), Fn("Stall", "Stall"),
            new Entry("Mail", Kind.Mail), Fn("Achievements", "Achievements"), Fn("Ranking", "RankNew"),
            Fn("Season", "SeasonActivity"),

            Fn("Dungeons", "Dungeon"), Fn("Challenge", "Challenge"), Fn("Competition", "Competition"),
            Fn("Climb Tower", "ClimbTower"), Fn("Become Stronger", "BecomeStronger"), Fn("Profile", "Personal"),
            Fn("School", "School"),
            Act("Settings", "ClosePanel"), new Entry("Controller", Kind.Controller),
        };

        private static int Pages => (Entries.Length + PageSize - 1) / PageSize;
        private static int _page;
        private static int PageCount => Math.Min(PageSize, Entries.Length - _page * PageSize);
        private static Entry Current(int slice) => Entries[_page * PageSize + slice];

        private static readonly MethodInfo Started = Handler("Obf_jn"), Performed = Handler("Obf_Jn"), Canceled = Handler("Obf_kn");

        private static MethodInfo Handler(string name) =>
            typeof(InputSystemManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(InputAction.CallbackContext) }, null);

        private static RectTransform _root;
        private static readonly List<(Image bg, TextMeshProUGUI label)> Slices = new List<(Image, TextMeshProUGUI)>();
        private static TextMeshProUGUI _center;
        private static int _selected = -1;

        public static bool Open { get; private set; }

        /// <summary>Runs a game input action as a key press would (started, performed, canceled).</summary>
        public static bool Run(string action)
        {
            var manager = HappyEngine.Base.SingletonMonoBehaviour<InputSystemManager>.Instance;
            if (manager == null || Started == null || Performed == null || Canceled == null)
            {
                Debug.LogWarning("[Ro3Emu.Wheel] input action handlers not found; " + action + " not run");
                return false;
            }
            var args = new object[] { action, default(InputAction.CallbackContext) };
            Started.Invoke(manager, args);
            Performed.Invoke(manager, args);
            Canceled.Invoke(manager, args);
            Debug.Log("[Ro3Emu.Wheel] run " + action);
            return true;
        }

        public static void Show()
        {
            Build();
            Open = true;
            _selected = -1;
            RefreshEnabled();
            _root.gameObject.SetActive(true);
            foreach (var s in Slices) if (s.bg.GetComponent<Punch>() is Punch p) p.enabled = false; // a closing punch must not hide it again
            Refresh();
        }

        public static void Hide()
        {
            Open = false;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>From Controller.Tick while open; returns the prompts.</summary>
        internal static void Tick(Gamepad pad, List<(string, string)> prompts)
        {
            RefreshArt();
            var stick = pad.rightStick.ReadValue();
            if (stick.magnitude < 0.5f) stick = pad.leftStick.ReadValue();
            if (stick.magnitude >= 0.5f)
            {
                // Slice 0 is at the top, then clockwise.
                float angle = Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg;
                if (angle < 0) angle += 360f;
                int n = PageCount;
                int index = (int)Math.Round(angle / (360f / n)) % n;
                if (index != _selected) { _selected = index; Refresh(); Punch.Play(Slices[index].bg.transform, 1.25f, null, 1.12f); }
            }
            int turn = (Controller.Edge(pad.TryGetChildControl(Path(UiNav.Control("MenuTabRight", "rightShoulder"))) as ButtonControl, true) ? 1 : 0)
                - (Controller.Edge(pad.TryGetChildControl(Path(UiNav.Control("MenuTabLeft", "leftShoulder"))) as ButtonControl, true) ? 1 : 0);
            if (turn != 0 && Pages > 1)
            {
                _page = (_page + turn + Pages) % Pages;
                _selected = -1;
                Refresh();
            }
            var dpad = pad.dpad.ReadValue();
            if (dpad != Vector2.zero && Controller.Edge(dpad.x > 0 ? pad.dpad.right : dpad.x < 0 ? pad.dpad.left : dpad.y > 0 ? pad.dpad.up : pad.dpad.down, true))
            {
                int step = dpad.x > 0 || dpad.y < 0 ? 1 : -1;
                _selected = ((_selected < 0 ? 0 : _selected) + step + PageCount) % PageCount;
                Refresh();
                Punch.Play(Slices[_selected].bg.transform, 1.25f, null, 1.12f);
            }
            bool confirm = Controller.Edge(pad.TryGetChildControl(Path(UiNav.Control("MenuConfirm", "buttonSouth"))) as ButtonControl, true)
                || Controller.Edge(pad.TryGetChildControl(Path(UiNav.Control("MenuWheel", "select"))) as ButtonControl, true);
            if (confirm)
            {
                if (_selected < 0) { Hide(); return; }
                var entry = Current(_selected);
                if (!entry.Enabled)
                {
                    CheckOpen(entry, true); // the game's own "locked / unlocks at level N" tip; the wheel stays open
                    return;
                }
                Open = false; // the wheel stays on screen for the pick's punch, then hides itself
                Punch.Play(Slices[_selected].bg.transform, 1.4f, null, 1.12f, _root.gameObject);
                Pick(entry);
                return;
            }
            if (Controller.Edge(pad.TryGetChildControl(Path(UiNav.Control("MenuBack", "buttonEast"))) as ButtonControl, true)) { Hide(); return; }
            prompts.Add(("<Gamepad>/rightStick", "Choose"));
            prompts.Add((UiNav.Control("MenuConfirm", "buttonSouth"), "Open"));
            prompts.Add((UiNav.Control("MenuBack", "buttonEast"), "Close"));
            if (Pages > 1)
            {
                prompts.Add((UiNav.Control("MenuTabLeft", "leftShoulder"), ""));
                prompts.Add((UiNav.Control("MenuTabRight", "rightShoulder"), "Page"));
            }
        }

        // ---- opening ----

        private static void Pick(Entry entry)
        {
            switch (entry.Kind)
            {
                case Kind.Action: Run(entry.Key); break;
                case Kind.Controller: ControllerSettings.Show(); break;
                case Kind.QuestNav: QuestNav.Go(); break;
                case Kind.Function:
                    // The function menu's own click: UI_Sys_MainFunctionMenuWidgetBaseCtrl.lua:1083 -> OpenFunctionPanelDefault(id).
                    CallLua("LC_FunctionHelper.OpenFunctionPanelDefault", FunctionId(entry));
                    break;
                case Kind.Mail:
                    // UI_Sys_Main_EmailWidgetCtrl.lua:111-147: OpenPanelDefault(UINames.UI_Sys_MailPanel) once MailNew is open.
                    OpenPanel("UI_Sys_MailPanel");
                    break;
                case Kind.Party:
                    // UI_Sys_Main_TeamUpWidgetCtrl.lua:864 / Logic/Team/LC_TeamHelper.lua:1227: opens the team panel by team type.
                    // Without a party the stock team widget has nothing to open, so fall back to the party finder the chat
                    // opens (UI_Sys_Chat_FunctionContentWidgetCtrl.lua:3647, _openTeamPlatform).
                    {
                        var global = LuaPatches.Global;
                        var type = CallLua("LC_TeamHelper.GetTeamType");
                        var five = global?.Obf_LuA<object>("PBTeamType.TeamTypeFive");
                        var forty = global?.Obf_LuA<object>("PBTeamType.TeamTypeForty");
                        object have = type != null && type.Length > 0 ? type[0] : null;
                        if (have != null && (Equals(have, five) || Equals(have, forty))) CallLua("LC_TeamHelper.OpenPanelDependsTeamType");
                        else OpenPanel("UI_Sys_TeamPlatformPanel");
                    }
                    break;
            }
            Debug.Log("[Ro3Emu.Wheel] pick " + entry.Label);
        }

        /// <summary>Lua_UIManagerNewIns:OpenPanelDefault(UINames.&lt;name&gt;).</summary>
        private static void OpenPanel(string name)
        {
            var global = LuaPatches.Global;
            var ui = global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
            var open = ui?.Obf_RuA<Obf_R.Obf_Ef>("OpenPanelDefault");
            var panel = global?.Obf_LuA<object>("UINames." + name);
            if (open == null || panel == null) { Debug.LogWarning("[Ro3Emu.Wheel] panel " + name + " not found"); return; }
            open.Obf_HuA(ui, panel);
        }

        /// <summary>The function behind an entry: enum_FunctionType name, or enum_InputKeyFunc[action] for a hotkey (null: always open).</summary>
        private static object FunctionId(Entry entry)
        {
            var global = LuaPatches.Global;
            if (global == null) return null;
            switch (entry.Kind)
            {
                case Kind.Function: return global.Obf_LuA<object>("enum_FunctionType." + entry.Key);
                case Kind.Action: return global.Obf_LuA<object>("enum_InputKeyFunc." + entry.Key);
                case Kind.Mail: return global.Obf_LuA<object>("enum_FunctionType.MailNew");
                default: return null;
            }
        }

        /// <summary>LC_FunctionHelper.CheckFunctionIsOpen(id, showTip) (LC_FunctionHelper.lua:1032-1172): false is silent, true pops the game's lock tip.</summary>
        private static bool CheckOpen(Entry entry, bool showTip)
        {
            try
            {
                var global = LuaPatches.Global;
                if (global == null) return true;
                // Task and Map are FunctionDefaultOpen: OpenFunctionPanelDefault skips the check for them (LC_FunctionHelper.lua:1023-1031).
                if (entry.Key == "OpenTask" || entry.Key == "OpenMap") return true;
                var id = FunctionId(entry);
                if (id == null) return entry.Kind != Kind.Function && entry.Kind != Kind.Mail; // an unmapped function stays disabled
                var r = CallLua("LC_FunctionHelper.CheckFunctionIsOpen", id, showTip);
                return r == null || r.Length == 0 || !(r[0] is bool b) || b;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.Wheel] lock check failed for " + entry.Label + ": " + e.Message);
                return true;
            }
        }

        private static void RefreshEnabled()
        {
            foreach (var e in Entries) e.Enabled = CheckOpen(e, false);
        }

        private static object[] CallLua(string path, params object[] args)
        {
            var fn = LuaPatches.Global?.Obf_LuA<Obf_R.Obf_Ef>(path);
            if (fn == null) { Debug.LogWarning("[Ro3Emu.Wheel] Lua function " + path + " not found"); return null; }
            return fn.Obf_HuA(args);
        }

        private static string Path(string control) => control.Substring(control.IndexOf('/') + 1);

        // ---- view ----

        private const float Radius = 270f, SliceW = 156f, SliceH = 54f;

        private static void Build()
        {
            if (_root != null) { _root.localScale = Vector3.one * Overlay.Scale; return; }
            _root = Overlay.Rect("Ro3emuWheel");
            _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0.5f, 0.5f);
            _root.localScale = Vector3.one * Overlay.Scale;
            var disc = Overlay.Image("Disc", _root, new Color(0.04f, 0.03f, 0.03f, 0.82f));
            disc.sprite = Shapes.Circle;
            ControllerSkin.Bind(disc, "wheel_disc", Shapes.Circle);
            Place(disc.rectTransform, -Radius - 110f, -Radius - 110f, 2 * Radius + 220f, 2 * Radius + 220f);
            var ring = Overlay.Image("Ring", _root, new Color(0.86f, 0.7f, 0.4f, 0.8f));
            ring.sprite = Shapes.Ring;
            ControllerSkin.Bind(ring, "wheel_ring", Shapes.Ring);
            Place(ring.rectTransform, -Radius - 110f, -Radius - 110f, 2 * Radius + 220f, 2 * Radius + 220f);
            _center = Overlay.Text("Center", _root, 34);
            _center.alignment = TextAlignmentOptions.Center;
            Place(_center.rectTransform, -150f, -30f, 300f, 60f);
            for (int i = 0; i < PageSize; i++)
            {
                var bg = Overlay.Image("Slice" + i, _root, Color.white);
                bg.sprite = Shapes.RoundedRect;
                bg.type = Image.Type.Simple;
                bg.rectTransform.sizeDelta = new Vector2(SliceW, SliceH);
                var label = Overlay.Text("Label", bg.transform, 24);
                label.alignment = TextAlignmentOptions.Center;
                Place(label.rectTransform, 0, 0, SliceW, SliceH);
                Slices.Add((bg, label));
            }
        }

        private static void Refresh()
        {
            RefreshArt();
            int n = PageCount;
            for (int i = 0; i < Slices.Count; i++)
            {
                Slices[i].bg.gameObject.SetActive(i < n);
                if (i >= n) continue;
                // Slice 0 is at the top, then clockwise.
                float a = i * 2f * Mathf.PI / n;
                // Overlay.Image pivots bottom-left, so the slice is centred by half its size.
                Slices[i].bg.rectTransform.anchoredPosition = new Vector2(Mathf.Sin(a) * Radius - SliceW / 2f, Mathf.Cos(a) * Radius - SliceH / 2f);
                var entry = Current(i);
                bool on = i == _selected;
                Slices[i].label.text = entry.Label;
                Slices[i].label.color = !entry.Enabled ? new Color(0.55f, 0.52f, 0.48f)
                    : on ? new Color(0.1f, 0.07f, 0.03f) : new Color(0.96f, 0.92f, 0.82f);
                Slices[i].bg.rectTransform.localScale = Vector3.one * (on ? 1.12f : 1f);
            }
            _center.text = (_selected >= 0 ? Current(_selected).Label : "Menu") + (Pages > 1 ? "\n<size=60%>" + (_page + 1) + " / " + Pages + "</size>" : "");
        }

        private static void RefreshArt()
        {
            for (int i = 0; i < Slices.Count; i++)
            {
                if (i >= PageCount) continue;
                bool selected = i == _selected, enabled = Current(i).Enabled;
                var sprite = ControllerSkin.Get(selected ? "wheel_slice_selected" : "wheel_slice", Shapes.RoundedRect);
                if (Slices[i].bg.sprite != sprite) Slices[i].bg.sprite = sprite;
                Slices[i].bg.color = sprite != Shapes.RoundedRect ? new Color(1f, 1f, 1f, enabled ? 1f : 0.45f)
                    : selected && enabled ? new Color(0.86f, 0.7f, 0.4f, 0.95f) : new Color(1f, 1f, 1f, selected ? 0.18f : enabled ? 0.1f : 0.04f);
            }
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }
    }
}
