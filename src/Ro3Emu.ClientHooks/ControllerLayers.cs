using System;
using System.Collections.Generic;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Skill layers for <see cref="Controller"/>: holding the LayerShift2 control (default L2) swaps the face buttons, L1
    /// and the D-pad to the "Layer2.*" actions of controller.ini, holding LayerShift3 (default R2) to "Layer3.*".
    /// Controls without a layer entry keep their base binding. No Unity types, so the tests compile it.
    /// <para>ini syntax: "Layer2.ButtonSouth = NormalSkill_06" (slot name = control name with a capital first letter,
    /// D-pad as DpadUp/DpadDown/DpadLeft/DpadRight; value = a game action); "LayerShift2 = &lt;Gamepad&gt;/leftTrigger".</para>
    /// <para>Game side (inferred, the stock client has no layer concept): the actions are plain Input System actions
    /// (re/clientB/lua-src/UI/Main/Controllers/UI_Sys_Main_PropsItemWidgetCtrl.lua:101-111 registers ShortCut_1..6; the
    /// skill bar binders carry NormalSkill_01..06, ExpandSkill_01..02, InteractiveSkill_01..03, GamePlaySkill_01..03), so a
    /// layer is just a different gamepad-path set added to them while the shift is held (Controller.Sync).</para>
    /// </summary>
    internal static class ControllerLayers
    {
        /// <summary>Layers 1 (base) to 3.</summary>
        internal const int Count = 3;
        private const string Pad = "<Gamepad>/";

        /// <summary>Slot names and the control each stands for.</summary>
        internal static readonly string[] Slots =
        {
            "ButtonSouth", "ButtonEast", "ButtonWest", "ButtonNorth", "LeftShoulder", "RightShoulder",
            "LeftStickPress", "RightStickPress", "DpadUp", "DpadDown", "DpadLeft", "DpadRight",
        };

        /// <summary>"ButtonSouth" -> "&lt;Gamepad&gt;/buttonSouth", "DpadLeft" -> "&lt;Gamepad&gt;/dpad/left"; null when unknown.</summary>
        internal static string SlotPath(string slot)
        {
            foreach (var s in Slots)
            {
                if (!string.Equals(s, slot, StringComparison.OrdinalIgnoreCase)) continue;
                return s.StartsWith("Dpad", StringComparison.Ordinal) ? Pad + "dpad/" + char.ToLowerInvariant(s[4]) + s.Substring(5)
                    : Pad + char.ToLowerInvariant(s[0]) + s.Substring(1);
            }
            return null;
        }

        /// <summary>The ini key of a control's slot in a layer ("Layer2.ButtonSouth"), or null for a control that has no slot.</summary>
        internal static string Key(int layer, string path)
        {
            foreach (var s in Slots)
                if (SlotPath(s) == path) return "Layer" + layer + "." + s;
            return null;
        }


        /// <summary>"Layer2.ButtonSouth" style ini key.</summary>
        internal static bool IsLayerKey(string key) =>
            key != null && key.Length > 7 && key.StartsWith("Layer", StringComparison.OrdinalIgnoreCase) && key[5] >= '2' && key[5] <= '3'
            && key[6] == '.' && SlotPath(key.Substring(7)) != null;

        /// <summary>The default action of a layer key, or null (slot keeps its base binding).</summary>
        internal static string DefaultFor(string key)
        {
            var table = new Table();
            Parse(DefaultBlock.Split('\n'), table);
            return IsLayerKey(key) && table.Of(key[5] - '0').TryGetValue(SlotPath(key.Substring(7)), out var action) ? action : null;
        }

        /// <summary>Layer n (2 or 3) -> control path -> action.</summary>
        internal sealed class Table
        {
            private readonly Dictionary<string, string>[] _by = { new Dictionary<string, string>(), new Dictionary<string, string>() };
            internal Dictionary<string, string> Of(int layer) => _by[layer - 2];
            internal void Clear() { _by[0].Clear(); _by[1].Clear(); }
        }

        /// <summary>Reads the "LayerN.Slot = Action" lines. Unknown slots and empty values are skipped; a later line wins.</summary>
        internal static void Parse(string[] lines, Table table)
        {
            table.Clear();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (value.Length == 0 || value[0] == '<' || key.Length < 8 || !key.StartsWith("Layer", StringComparison.OrdinalIgnoreCase)
                    || key[6] != '.' || key[5] < '2' || key[5] > '3') continue;
                string path = SlotPath(key.Substring(7));
                if (path != null) table.Of(key[5] - '0')[path] = value;
            }
        }

        /// <summary>The gamepad paths an action has while a layer is active: its base paths minus the controls that layer
        /// reassigns, plus the controls that layer assigns to it. Layer 1 is the base binding.</summary>
        internal static List<string> Resolve(Dictionary<string, List<string>> binds, Table table, int layer, string action)
        {
            var result = new List<string>();
            if (binds.TryGetValue(action, out var paths))
                foreach (var p in paths)
                    if (layer < 2 || !table.Of(layer).ContainsKey(p)) result.Add(p);
            if (layer >= 2)
                foreach (var pair in table.Of(layer))
                    if (string.Equals(pair.Value, action, StringComparison.OrdinalIgnoreCase) && !result.Contains(pair.Key)) result.Add(pair.Key);
            return result;
        }

        /// <summary>The first control an action has in a layer, or null.</summary>
        internal static string First(Dictionary<string, List<string>> binds, Table table, int layer, string action)
        {
            var list = Resolve(binds, table, layer, action);
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>The first control the action has in any layer (base first), for labels that are not layer-aware.</summary>
        internal static string Any(Dictionary<string, List<string>> binds, Table table, string action)
        {
            for (int layer = 1; layer <= Count; layer++)
            {
                var p = First(binds, table, layer, action);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>Which layer is active. Holding both shifts keeps the one pressed first.</summary>
        internal sealed class State
        {
            public int Layer { get; private set; } = 1;

            public int Update(bool shift2, bool shift3)
            {
                if (Layer == 2 && shift2) return 2;
                if (Layer == 3 && shift3) return 3;
                return Layer = shift2 ? 2 : shift3 ? 3 : 1;
            }
        }

        /// <summary>New defaults for an older controller.ini (no layer lines): the trigger skill lines are dropped (the
        /// triggers are layer shifts now; NormalSkill_04 / 05 moved to Layer2) and the layer block appended. Null when the
        /// file needs nothing.</summary>
        internal static string[] Migrate(string[] lines)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("LayerShift2", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Layer2.", StringComparison.OrdinalIgnoreCase)) return null;
            }
            var result = new List<string>();
            foreach (var raw in lines)
            {
                var compact = raw.Replace(" ", "").TrimEnd('\r');
                if (compact.Equals("NormalSkill_04=<Gamepad>/leftTrigger", StringComparison.OrdinalIgnoreCase)
                    || compact.Equals("NormalSkill_05=<Gamepad>/rightTrigger", StringComparison.OrdinalIgnoreCase)) continue;
                if (compact.Equals("SwitchTarget=<Gamepad>/rightShoulder", StringComparison.OrdinalIgnoreCase))
                {
                    // Same era: R1 was the stock tab-target; it is our TargetCycle now, and R3 tap locks (Targeting).
                    result.Add("TargetCycle = <Gamepad>/rightShoulder");
                    result.Add("TargetLock = <Gamepad>/rightStickPress");
                    continue;
                }
                result.Add(raw);
            }
            result.AddRange(DefaultBlock.Split('\n'));
            return result.ToArray();
        }

        internal const string DefaultBlock =
@"
# Skill layers: hold LayerShift2 (L2) or LayerShift3 (R2) and the face buttons, L1 and the D-pad switch to these actions
# (Layer2.* / Layer3.*; slots ButtonSouth ButtonEast ButtonWest ButtonNorth LeftShoulder RightShoulder LeftStickPress
# RightStickPress DpadUp DpadDown DpadLeft DpadRight; a slot with no line keeps its base binding). The triggers used to be
# NormalSkill_04 / 05: those are Layer2.ButtonWest / ButtonNorth now. L2 + R1 cycles party members (AllySelect), R1 alone enemies.
LayerShift2 = <Gamepad>/leftTrigger
LayerShift3 = <Gamepad>/rightTrigger
Layer2.ButtonWest = NormalSkill_04
Layer2.ButtonNorth = NormalSkill_05
Layer2.ButtonEast = NormalSkill_06
Layer2.ButtonSouth = ExpandSkill_01
Layer2.LeftShoulder = ExpandSkill_02
Layer2.RightShoulder = AllySelect
Layer2.DpadLeft = ShortCut_3
Layer2.DpadRight = ShortCut_4
Layer3.ButtonWest = InteractiveSkill_02
Layer3.ButtonNorth = InteractiveSkill_03
Layer3.ButtonEast = GamePlaySkill_01
Layer3.ButtonSouth = GamePlaySkill_02
Layer3.LeftShoulder = GamePlaySkill_03
Layer3.DpadLeft = ShortCut_5
Layer3.DpadRight = ShortCut_6
";
    }
}
