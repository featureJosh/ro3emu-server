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
# Game actions (the Key Settings list): MoveForward MoveBackward MoveLeft MoveRight OpenBag OpenMap OpenChat OpenShop
# OpenTrading OpenActivity OpenMustDo OpenFriend OpenRole OpenGuild Mount MountCareer OpenTask OpenSkill OpenEquip
# OpenAutoFight OpenKneelDown OpenEquipGrowth OpenTalent OpenCard OpenPet OpenTotem OpenSoulEcho OpenMonsterHunt
# OpenWardrobe OpenCollection OpenLifeJob OpenEmoji OpenBattleReport Direction_Up Direction_Down FixTip ActionPageLeft
# ActionPageRight NormalSkill_01..06 ExpandSkill_1..6 GamePlaySkill_01..03 Interactive_1..3 ShortCut_1..6
# SwitchTarget FastPickUp FastPickUpAll Esc.
# Controller actions: CursorMode (toggle the menu cursor), LeftClick / RightClick (in cursor mode),
# CameraZoom (hold, then right stick up/down).
# Tuning: CameraSpeedX / CameraSpeedY (negative inverts), CursorSpeed (pixels per second), Deadzone (0-1),
# GlyphScale (button glyph size relative to the key label it replaces).

MoveForward = <Gamepad>/leftStick/up
MoveBackward = <Gamepad>/leftStick/down
MoveLeft = <Gamepad>/leftStick/left
MoveRight = <Gamepad>/leftStick/right
Interactive_1 = <Gamepad>/buttonSouth
FastPickUp = <Gamepad>/buttonEast
NormalSkill_01 = <Gamepad>/buttonWest
NormalSkill_02 = <Gamepad>/buttonNorth
NormalSkill_03 = <Gamepad>/leftShoulder
NormalSkill_04 = <Gamepad>/leftTrigger
NormalSkill_05 = <Gamepad>/rightTrigger
SwitchTarget = <Gamepad>/rightShoulder
Direction_Up = <Gamepad>/dpad/up
Direction_Down = <Gamepad>/dpad/down
ShortCut_1 = <Gamepad>/dpad/left
ShortCut_2 = <Gamepad>/dpad/right
Esc = <Gamepad>/start
OpenMap = <Gamepad>/leftStickPress
CameraZoom = <Gamepad>/rightStickPress
CursorMode = <Gamepad>/select
LeftClick = <Gamepad>/buttonSouth
RightClick = <Gamepad>/buttonEast

CameraSpeedX = 700
CameraSpeedY = 400
CursorSpeed = 1200
Deadzone = 0.2
GlyphScale = 2.4
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
