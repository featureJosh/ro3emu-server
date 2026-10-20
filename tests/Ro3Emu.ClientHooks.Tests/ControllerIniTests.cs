namespace Ro3Emu.ClientHooks.Tests;

public class ControllerIniTests
{
    // Input System action names (dev command "actions"; not the Lua enum_InputKey keys) plus the hook's own controller actions.
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "MoveForward", "MoveBackward", "MoveLeft", "MoveRight", "OpenMap", "InteractiveSkill_01", "FastPickUp", "SwitchTarget",
        "NormalSkill_01", "NormalSkill_02", "NormalSkill_03", "NormalSkill_04", "NormalSkill_05", "Direction_Up",
        "Direction_Down", "ShortCut_1", "ShortCut_2", "ClosePanel", "CursorMode", "LeftClick", "RightClick", "CameraZoom", "MenuConfirm", "MenuBack", "MenuTabLeft", "MenuTabRight", "MenuAssign", "MenuUnassign", "MenuRightClick", "MenuWheel", "QuestNav", "TargetCycle", "TargetLock", "LayerShift2", "LayerShift3",
    };

    [Fact]
    public void DefaultsBindKnownActionsToGamepadControls()
    {
        var binds = new Dictionary<string, List<string>>();
        var tune = new Dictionary<string, float>();
        ControllerIni.Parse(ControllerIni.Defaults.Split('\n'), binds, tune);
        Assert.Equal(32, binds.Count);
        foreach (var (action, paths) in binds)
        {
            Assert.Contains(action, Known);
            Assert.All(paths, p => Assert.StartsWith("<Gamepad>/", p));
        }
        Assert.Equal(["<Gamepad>/buttonSouth"], binds["InteractiveSkill_01"]);
        Assert.Equal(["<Gamepad>/touchpadButton"], binds["QuestNav"]);
        Assert.Equal(["<Gamepad>/rightShoulder"], binds["TargetCycle"]);
        Assert.Equal(["<Gamepad>/rightStickPress"], binds["TargetLock"]);
        Assert.False(binds.ContainsKey("SwitchTarget")); // TargetCycle replaces the stock action on R1
        Assert.Equal(["<Gamepad>/buttonWest"], binds["MenuAssign"]);
        Assert.Equal(["<Gamepad>/buttonNorth"], binds["MenuUnassign"]);
        Assert.Equal(0.2f, tune["Deadzone"]);
        Assert.Equal(6, tune.Count);
    }

    [Fact]
    public void RepeatsAddCommentsAndJunkAreSkipped()
    {
        var binds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); // as Controller
        var tune = new Dictionary<string, float>();
        ControllerIni.Parse(["# x = <Gamepad>/a", "Esc = <Gamepad>/start", "esc = <Gamepad>/select", "Esc = <Gamepad>/start",
            "nonsense", "= <Gamepad>/b", "CursorSpeed = 9.5", "Bad = fast"], binds, tune);
        Assert.Equal(["<Gamepad>/start", "<Gamepad>/select"], binds["Esc"]);
        Assert.Single(binds);
        Assert.Equal(9.5f, tune["CursorSpeed"]);
        Assert.Single(tune);
    }
}
