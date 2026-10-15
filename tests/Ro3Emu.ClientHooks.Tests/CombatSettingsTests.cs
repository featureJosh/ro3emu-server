using Obf_R;
using Ro3Emu.ClientHooks;

namespace Ro3Emu.ClientHooks.Tests;

public class CombatSettingsTests
{
    private sealed class Client
    {
        public object Flat;
        public object Chase = 1L;
        public object Loaded = 1L;
        public readonly Obf_ff Global = new(), Process = new(), Helper = new(), Setting = new();
        public int BattleReads;

        public Client()
        {
            var types = new Obf_ff();
            types.Obf_luA("ContinuousAttackAfterTargetSelected", 2L);
            types.Obf_luA("ChaseLogic", 1L);
            Global.Obf_luA("enum_SettingItemType", types);
            var config = new Obf_ff();
            config.Obf_luA("getBattleSetting", new Obf_Ef(args =>
            {
                Assert.Equal("eSelectTargetNormalAttack", args[0]);
                BattleReads++;
                return new[] { Loaded };
            }));
            Global.Obf_luA("G_ClientCfg", config);
            Helper.Obf_luA("GetValue", new Obf_Ef(args =>
            {
                Assert.Equal(1L, args[0]);
                return new[] { Chase };
            }));
            Setting.Obf_luA("GetValue", new Obf_Ef(_ => new object[]
            {
                LuaPatches.ToLong(Chase) == 1 ? Flat ?? 0L : 0L,
            }));
            var definition = new Obf_ff();
            definition.Obf_luA("ValueSetting", Setting);
            Process.Obf_luA(2L, definition);
        }

        public void Install() => CombatSettings.Install(Global, Process, Helper);
        public long Read() => Setting.Obf_RuA<CombatSettings.ReadSetting>("GetValue")();
    }

    [Fact]
    public void LoginPreferenceEnablesClickWithoutFlatAsset()
    {
        var c = new Client();
        c.Install();
        Assert.Equal(1, c.Read());
        Assert.Null(c.Flat); // Reading does not overwrite the player's setting or sync it to the server.
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void ExplicitFlatPreferenceWins(int flat, int loaded)
    {
        var c = new Client { Flat = (long)flat, Loaded = (long)loaded };
        c.Install();
        Assert.Equal(flat, c.Read());
        Assert.Equal(0, c.BattleReads);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ChaseGateStillDisablesAttack(int chase)
    {
        var c = new Client { Chase = (long)chase, Flat = 1L };
        c.Install();
        Assert.Equal(0, c.Read());
        Assert.Equal(0, c.BattleReads);
    }

    [Fact]
    public void ReadsUpdatedPlatformPreferenceAndPlayerEdits()
    {
        var c = new Client();
        c.Install();
        Assert.Equal(1, c.Read());
        c.Loaded = 2L;
        Assert.Equal(2, c.Read());
        c.Flat = 1L;
        Assert.Equal(1, c.Read());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3)]
    public void MissingOrInvalidLoadedPreferenceDoesNotEnableAttack(object loaded)
    {
        var c = new Client { Loaded = loaded };
        c.Install();
        Assert.Equal(0, c.Read());
    }

    [Fact]
    public void IncompatibleClientKeepsOriginalGetter()
    {
        var c = new Client();
        var original = c.Setting.Obf_RuA<Obf_Ef>("GetValue");
        c.Global.Obf_luA<string, object>("G_ClientCfg", null);
        c.Install();
        Assert.Same(original, c.Setting.Obf_RuA<Obf_Ef>("GetValue"));
    }
}
