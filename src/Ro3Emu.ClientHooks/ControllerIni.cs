using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ro3Emu.ClientHooks
{
    /// <summary>&lt;client&gt;/ro3emu/controller.ini for <see cref="Controller"/>: the defaults written on first use and the parser.
    /// No Unity types, so the tests compile it.</summary>
    internal static class ControllerIni
    {
        internal const string Defaults =
@"# RO3 controller bindings: one ""Action = control"" per line; actions and controls may repeat. Saved changes apply
# within a second. Controls: <Gamepad>/ + buttonSouth (A/Cross), buttonEast (B/Circle), buttonWest (X/Square),
# buttonNorth (Y/Triangle), leftShoulder, rightShoulder, leftTrigger, rightTrigger, start, select, leftStickPress,
# rightStickPress, dpad/up|down|left|right, leftStick/up|down|left|right, rightStick/up|down|left|right.
# Game actions (the Input System names, not the Lua enum_InputKey keys; dev command ""actions"" lists them):
# MoveForward MoveBackward MoveLeft MoveRight SwitchTarget OpenMap OpenChat OpenShop OpenTrading OpenActivity
# OpenMustDo OpenFriend OpenGuild OpenAutoFight Mount MountCareer NormalSkill_01..06 ExpandSkill_01..02
# InteractiveSkill_01..03 OpenKneelDown ShortCut_1..6 OpenTalent OpenCard OpenPet OpenTotem OpenSoulEcho
# OpenMonsterHunt OpenWardrobe OpenCollection OpenLifeJob OpenEmoji OpenTask OpenRole OpenBag OpenSkill
# OpenEquipGrowth ClosePanel (Esc) Jump OpenBattleReport FastPickUp FastPickUpAll FixTip Direction_Up Direction_Down
# ActionPageLeft ActionPageRight GamePlaySkill_01..03.
# Controller actions: MenuWheel (tap: radial menu), CursorMode (on the same button: hold 0.5 s; toggles the mouse
# cursor), LeftClick / RightClick (in cursor mode),
# QuestNav (auto-path to the tracked quest, like clicking it in the quest tracker; default: the DualSense touchpad
# press, <Gamepad>/touchpadButton, the one control the defaults leave free; also in the radial menu as Go to Quest),
# CameraZoom (hold, then right stick up/down; on the right stick press it shares the control with TargetLock, a tap),
# TargetCycle (next hostile target, near and ahead of the camera first; to get the stock cycle back, remove it and bind
# SwitchTarget), TargetLock (tap: hard lock on the target until it dies or leaves TargetLockRange), AllySelect (press:
# select the nearest party member, again for the next; a heal or buff pressed next lands on them). In menus: MenuConfirm, MenuBack, MenuTabLeft, MenuTabRight
# (D-pad / left stick move the focus, right stick scrolls). In the skill window: MenuAssign (focused skill, then press a
# skill button to bind it to that slot) and MenuUnassign (then press a skill button to unbind that slot). MenuAssign /
# MenuUnassign also work on bag items: assign the item to, or clear, a D-pad potion slot.
# Tuning: CameraSpeedX / CameraSpeedY (negative inverts), CursorSpeed (pixels per second), Deadzone (0-1),
# SoftLock (1/0: pick a monster when attacking or pushing the stick with no target), SoftRange / CycleRange /
# TargetLockRange / AllyRange (metres; 15 / 25 / 30 / 30), AllyHold (seconds a hard lock yields after AllySelect; 1.5),
# GlyphScale (button glyph size relative to the key label it replaces), UiScale (size of our controller UI).

MoveForward = <Gamepad>/leftStick/up
MoveBackward = <Gamepad>/leftStick/down
MoveLeft = <Gamepad>/leftStick/left
MoveRight = <Gamepad>/leftStick/right
InteractiveSkill_01 = <Gamepad>/buttonSouth
FastPickUp = <Gamepad>/buttonEast
NormalSkill_01 = <Gamepad>/buttonWest
NormalSkill_02 = <Gamepad>/buttonNorth
NormalSkill_03 = <Gamepad>/leftShoulder
NormalSkill_04 = <Gamepad>/leftTrigger
NormalSkill_05 = <Gamepad>/rightTrigger
TargetCycle = <Gamepad>/rightShoulder
TargetLock = <Gamepad>/rightStickPress
AllySelect = <Gamepad>/leftShoulder
Direction_Up = <Gamepad>/dpad/up
Direction_Down = <Gamepad>/dpad/down
ShortCut_1 = <Gamepad>/dpad/left
ShortCut_2 = <Gamepad>/dpad/right
ClosePanel = <Gamepad>/start
OpenMap = <Gamepad>/leftStickPress
CameraZoom = <Gamepad>/rightStickPress
MenuWheel = <Gamepad>/select
QuestNav = <Gamepad>/touchpadButton
CursorMode = <Gamepad>/select
LeftClick = <Gamepad>/buttonSouth
RightClick = <Gamepad>/buttonEast
MenuConfirm = <Gamepad>/buttonSouth
MenuBack = <Gamepad>/buttonEast
MenuTabLeft = <Gamepad>/leftShoulder
MenuTabRight = <Gamepad>/rightShoulder
MenuAssign = <Gamepad>/buttonWest
MenuUnassign = <Gamepad>/buttonNorth

CameraSpeedX = 700
CameraSpeedY = 400
CursorSpeed = 1200
Deadzone = 0.2
GlyphScale = 2
UiScale = 1
";

        /// <summary>"Action = &lt;Gamepad&gt;/control" lines go to binds, "Name = number" lines to tune.</summary>
        public static void Parse(string[] lines, Dictionary<string, List<string>> binds, Dictionary<string, float> tune)
        {
            binds.Clear();
            tune.Clear();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (value.StartsWith("<"))
                {
                    if (!binds.TryGetValue(key, out var list)) binds[key] = list = new List<string>();
                    if (!list.Contains(value)) list.Add(value);
                }
                else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                    tune[key] = number;
            }
        }
    }
}
