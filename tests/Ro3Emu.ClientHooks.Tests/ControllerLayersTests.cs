namespace Ro3Emu.ClientHooks.Tests;

public class ControllerLayersTests
{
    private static (Dictionary<string, List<string>> binds, ControllerLayers.Table table) Defaults()
    {
        var binds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var tune = new Dictionary<string, float>();
        var lines = ControllerIni.Defaults.Split('\n');
        ControllerIni.Parse(lines, binds, tune);
        var table = new ControllerLayers.Table();
        ControllerLayers.Parse(lines, table);
        return (binds, table);
    }

    [Fact]
    public void StateFollowsShiftsAndKeepsTheFirstOneHeld()
    {
        var s = new ControllerLayers.State();
        Assert.Equal(1, s.Update(false, false));
        Assert.Equal(2, s.Update(true, false));
        Assert.Equal(2, s.Update(true, true));   // R2 pressed second: still layer 2
        Assert.Equal(3, s.Update(false, true));  // L2 released while R2 is held: layer 3
        Assert.Equal(3, s.Update(true, true));   // L2 pressed again: stays 3
        Assert.Equal(1, s.Update(false, false));
    }

    [Fact]
    public void BothShiftsFromRestPickLayer2()
    {
        var s = new ControllerLayers.State();
        Assert.Equal(2, s.Update(true, true));
    }

    [Fact]
    public void SlotNamesMapToControls()
    {
        Assert.Equal("<Gamepad>/buttonSouth", ControllerLayers.SlotPath("ButtonSouth"));
        Assert.Equal("<Gamepad>/buttonSouth", ControllerLayers.SlotPath("buttonsouth"));
        Assert.Equal("<Gamepad>/dpad/left", ControllerLayers.SlotPath("DpadLeft"));
        Assert.Equal("<Gamepad>/leftShoulder", ControllerLayers.SlotPath("LeftShoulder"));
        Assert.Null(ControllerLayers.SlotPath("Bogus"));
        Assert.Equal("Layer3.DpadRight", ControllerLayers.Key(3, "<Gamepad>/dpad/right"));
        Assert.Null(ControllerLayers.Key(2, "<Gamepad>/start"));
        Assert.True(ControllerLayers.IsLayerKey("Layer2.ButtonSouth"));
        Assert.False(ControllerLayers.IsLayerKey("Layer4.ButtonSouth"));
        Assert.False(ControllerLayers.IsLayerKey("Layer2.Nope"));
        Assert.False(ControllerLayers.IsLayerKey("NormalSkill_01"));
    }

    [Fact]
    public void ParseReadsLayerLinesAndIgnoresJunk()
    {
        var t = new ControllerLayers.Table();
        ControllerLayers.Parse(["Layer2.ButtonSouth = NormalSkill_06", "layer3.dpadleft = ShortCut_5", "# Layer2.ButtonEast = X",
            "Layer2.Nope = X", "Layer4.ButtonSouth = X", "Layer2.ButtonWest = ", "Layer2.ButtonNorth = <Gamepad>/a",
            "Layer2.ButtonSouth = ExpandSkill_01", "LayerShift2 = <Gamepad>/leftTrigger", "Layer2 = x"], t);
        Assert.Equal("ExpandSkill_01", t.Of(2)["<Gamepad>/buttonSouth"]); // a later line wins
        Assert.Equal("ShortCut_5", t.Of(3)["<Gamepad>/dpad/left"]);
        Assert.Single(t.Of(2));
        Assert.Single(t.Of(3));
    }

    [Fact]
    public void DefaultsPutEveryRemainingSkillActionOnALayer()
    {
        var (binds, table) = Defaults();
        Assert.Equal(["<Gamepad>/leftTrigger"], binds["LayerShift2"]);
        Assert.Equal(["<Gamepad>/rightTrigger"], binds["LayerShift3"]);
        foreach (var action in new[] { "NormalSkill_04", "NormalSkill_05", "NormalSkill_06", "ExpandSkill_01", "ExpandSkill_02",
            "InteractiveSkill_02", "InteractiveSkill_03", "GamePlaySkill_01", "GamePlaySkill_02", "GamePlaySkill_03",
            "ShortCut_3", "ShortCut_4", "ShortCut_5", "ShortCut_6" })
        {
            Assert.False(binds.ContainsKey(action), action + " has no base binding");
            Assert.NotNull(ControllerLayers.Any(binds, table, action));
        }
        Assert.Equal("<Gamepad>/buttonEast", ControllerLayers.First(binds, table, 2, "NormalSkill_06"));
        Assert.Equal("<Gamepad>/dpad/right", ControllerLayers.First(binds, table, 3, "ShortCut_6"));
        Assert.Null(ControllerLayers.First(binds, table, 1, "NormalSkill_06"));
        Assert.Null(ControllerLayers.First(binds, table, 3, "NormalSkill_06"));
    }

    [Fact]
    public void NoLayerBindsTheSameControlTwice()
    {
        var (binds, table) = Defaults();
        var actions = new HashSet<string>(binds.Keys.Where(k => !k.StartsWith("Menu") && !k.StartsWith("Layer")
            && k is not ("CursorMode" or "LeftClick" or "RightClick" or "CameraZoom" or "QuestNav")), StringComparer.OrdinalIgnoreCase);
        foreach (var v in table.Of(2).Values.Concat(table.Of(3).Values)) actions.Add(v);
        for (int layer = 1; layer <= 3; layer++)
        {
            var seen = new Dictionary<string, string>();
            foreach (var a in actions)
                foreach (var p in ControllerLayers.Resolve(binds, table, layer, a))
                    Assert.True(seen.TryAdd(p, a), $"layer {layer}: {p} is both {seen.GetValueOrDefault(p)} and {a}");
        }
    }

    [Fact]
    public void ResolveReplacesOnlyTheControlsALayerAssigns()
    {
        var (binds, table) = Defaults();
        // base: X is InteractiveSkill_01 and the D-pad left/right are ShortCut_1/2; a held L2 takes those controls over.
        Assert.Equal(["<Gamepad>/buttonSouth"], ControllerLayers.Resolve(binds, table, 1, "InteractiveSkill_01"));
        Assert.Empty(ControllerLayers.Resolve(binds, table, 2, "InteractiveSkill_01"));
        Assert.Empty(ControllerLayers.Resolve(binds, table, 2, "ShortCut_1"));
        Assert.Equal(["<Gamepad>/dpad/left"], ControllerLayers.Resolve(binds, table, 1, "ShortCut_1"));
        // inherited: D-pad up/down and R1 have no layer entry and keep their base actions.
        Assert.Equal(["<Gamepad>/dpad/up"], ControllerLayers.Resolve(binds, table, 3, "Direction_Up"));
        Assert.Equal(["<Gamepad>/rightShoulder"], ControllerLayers.Resolve(binds, table, 2, "SwitchTarget"));
        Assert.Empty(ControllerLayers.Resolve(binds, table, 2, "NormalSkill_03"));
    }

    [Fact]
    public void ResolveKeepsAnActionOnItsOwnControlWhenALayerAssignsItThere()
    {
        var binds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["A"] = ["<Gamepad>/buttonSouth", "<Gamepad>/buttonEast"] };
        var table = new ControllerLayers.Table();
        ControllerLayers.Parse(["Layer2.ButtonSouth = a"], table);
        Assert.Equal(["<Gamepad>/buttonEast", "<Gamepad>/buttonSouth"], ControllerLayers.Resolve(binds, table, 2, "A"));
    }

    [Fact]
    public void DefaultForReadsTheDefaultBlock()
    {
        Assert.Equal("NormalSkill_06", ControllerLayers.DefaultFor("Layer2.ButtonEast"));
        Assert.Equal("ShortCut_5", ControllerLayers.DefaultFor("Layer3.DpadLeft"));
        Assert.Null(ControllerLayers.DefaultFor("Layer3.DpadUp"));
        Assert.Null(ControllerLayers.DefaultFor("NormalSkill_01"));
    }

    [Fact]
    public void MigrateMovesTriggerSkillsAndIsIdempotent()
    {
        string[] old = ["NormalSkill_03 = <Gamepad>/leftShoulder", "NormalSkill_04 = <Gamepad>/leftTrigger",
            "NormalSkill_05=<Gamepad>/rightTrigger", "NormalSkill_04 = <Gamepad>/select", "UiScale = 1"];
        var migrated = ControllerLayers.Migrate(old);
        Assert.NotNull(migrated);
        Assert.DoesNotContain(migrated, l => l.Contains("leftTrigger") && l.StartsWith("NormalSkill"));
        Assert.DoesNotContain(migrated, l => l.Contains("rightTrigger") && l.StartsWith("NormalSkill"));
        Assert.Contains("NormalSkill_04 = <Gamepad>/select", migrated); // a customised line stays
        var binds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        ControllerIni.Parse(migrated, binds, new Dictionary<string, float>());
        Assert.Equal(["<Gamepad>/leftTrigger"], binds["LayerShift2"]);
        Assert.Null(ControllerLayers.Migrate(migrated));
        Assert.Null(ControllerLayers.Migrate(ControllerIni.Defaults.Split('\n')));
    }

    [Fact]
    public void MigrateMovesStockTabTargetToTargetCycleAndAllyToLayer2()
    {
        var migrated = ControllerLayers.Migrate(["SwitchTarget = <Gamepad>/rightShoulder"]);
        var binds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        ControllerIni.Parse(migrated, binds, new Dictionary<string, float>());
        Assert.False(binds.ContainsKey("SwitchTarget"));
        Assert.Equal(["<Gamepad>/rightShoulder"], binds["TargetCycle"]);
        var table = new ControllerLayers.Table();
        ControllerLayers.Parse(migrated, table);
        Assert.Empty(ControllerLayers.Resolve(binds, table, 2, "TargetCycle")); // L2 + R1 is AllySelect, not the enemy cycle
        Assert.Equal(["<Gamepad>/rightShoulder"], ControllerLayers.Resolve(binds, table, 2, "AllySelect"));
    }
}
