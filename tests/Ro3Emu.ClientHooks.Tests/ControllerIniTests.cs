namespace Ro3Emu.ClientHooks.Tests;

public class ControllerIniTests
{
    // enum_InputKey (Config/Define/Lua_GameDefine.lua) plus the hook's own controller actions.
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "MoveForward", "MoveBackward", "MoveLeft", "MoveRight", "OpenMap", "Interactive_1", "FastPickUp", "SwitchTarget",
        "NormalSkill_01", "NormalSkill_02", "NormalSkill_03", "NormalSkill_04", "NormalSkill_05", "Direction_Up",
        "Direction_Down", "ShortCut_1", "ShortCut_2", "Esc", "CursorMode", "LeftClick", "RightClick", "CameraZoom",
    };

    [Fact]
    public void DefaultsBindKnownActionsToGamepadControls()
    {
        var binds = new Dictionary<string, List<string>>();
        var tune = new Dictionary<string, float>();
        ControllerIni.Parse(ControllerIni.Defaults.Split('\n'), binds, tune);
        Assert.Equal(22, binds.Count);
        foreach (var (action, paths) in binds)
        {
            Assert.Contains(action, Known);
            Assert.All(paths, p => Assert.StartsWith("<Gamepad>/", p));
        }
        Assert.Equal(["<Gamepad>/buttonSouth"], binds["Interactive_1"]);
        Assert.Equal(0.2f, tune["Deadzone"]);
        Assert.Equal(5, tune.Count);
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
