using Obf_R;
using Ro3Emu.ClientHooks;

namespace Ro3Emu.ClientHooks.Tests;

public class CombatSettingsTests
{
    private const long Attack = 403001;

    /// <summary>The mode widget class, lc_settinghelper and the setting value they share.</summary>
    private sealed class Client
    {
        public readonly Obf_ff Global = new(), Widget = new(), Helper = new();
        public object Value;
        public readonly List<object> Writes = new();
        public int OriginalCalls;

        /// <param name="pick">What the original writes on a first pick: 1 classic, 2 action.</param>
        public Client(long pick)
        {
            var types = new Obf_ff();
            types.Obf_luA("ContinuousAttackAfterTargetSelected", Attack);
            Global.Obf_luA("enum_SettingItemType", types);
            Helper.Obf_luA("SetValue", new Obf_Ef(args =>
            {
                Assert.Equal(Attack, args[0]);
                Value = args[1];
                Writes.Add(args[1]);
                return [];
            }));
            Widget.Obf_luA("_saveGameModeSettingReal", new Obf_Ef(args =>
            {
                OriginalCalls++;
                var self = (Obf_ff)args[0];
                if (self.Obf_RuA<object>("m_bPcPlatformRecord") is false)
                    Helper.Obf_RuA<Obf_Ef>("SetValue").Obf_HuA(Attack, pick);
                return [];
            }));
        }

        public void Install() => CombatSettings.Install(Global, Widget, Helper);

        public void Save(bool recorded)
        {
            var self = new Obf_ff();
            self.Obf_luA("m_bPcPlatformRecord", recorded);
            Widget.Obf_RuA<CombatSettings.SaveMode>("_saveGameModeSettingReal")(self);
        }
    }

    [Theory]
    [InlineData(2L)] // action mode: the client's own default is off
    [InlineData(1L)] // classic mode
    public void FirstModePickEndsWithClickToAttackOn(long pick)
    {
        var c = new Client(pick);
        c.Install();
        c.Save(recorded: false);
        Assert.Equal(1, c.OriginalCalls);
        Assert.Equal(1L, c.Value);
        Assert.Equal([pick, 1L], c.Writes);
    }

    [Fact]
    public void LaterModeChangesKeepThePlayersSetting()
    {
        var c = new Client(2L) { Value = 2L };
        c.Install();
        c.Save(recorded: true);
        Assert.Equal(1, c.OriginalCalls);
        Assert.Empty(c.Writes);
        Assert.Equal(2L, c.Value); // the LOG-tab choice stands
    }

    [Fact]
    public void IncompatibleClientKeepsOriginalMethod()
    {
        var c = new Client(2L);
        var original = c.Widget.Obf_RuA<Obf_Ef>("_saveGameModeSettingReal");
        c.Helper.Obf_luA<string, object>("SetValue", null);
        c.Install();
        Assert.Same(original, c.Widget.Obf_RuA<Obf_Ef>("_saveGameModeSettingReal"));
    }
}
