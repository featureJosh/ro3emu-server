using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Synthetic wardrobe/equipment fixture in the PopulationTests style; no game exports required.</summary>
public sealed class PopulationAppearanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ro3-population-look-" + Guid.NewGuid().ToString("N"));
    private readonly ServerOptions options;
    private readonly GameData data;
    private static readonly PopulationLookStyle Plain = new(100, 0, 0), Fashion = new(0, 100, 0), Distinctive = new(0, 0, 100, true);
    private static readonly int[] Premium = [4005, 4006, 4009, 5001, 6001, 8001, 1001, 7099];

    public PopulationAppearanceTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        options = new ServerOptions { DataDir = root };
        data = new GameData(Options.Create(options), NullLogger<GameData>.Instance);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private void WriteConfig(bool wardrobe = true)
    {
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """{"1000":{"_iUnitB":1000,"_iUnitG":1001}}""");
        File.WriteAllText(Path.Combine(root, "config", "ItemConfig.json"), """
        {
          "501":{"_iID":501,"_iType":5,"_iEquipId":501,"_iEquipPart":2,"_iLevelNeed":1,"_iQuality":2,"_kJobNeed":[1000]},
          "511":{"_iID":511,"_iType":5,"_iEquipId":501,"_iEquipPart":2,"_iLevelNeed":1,"_iQuality":2,"_kJobNeed":[1000]},
          "512":{"_iID":512,"_iType":5,"_iEquipId":501,"_iEquipPart":2,"_iLevelNeed":1,"_iQuality":2,"_kJobNeed":[1000]},
          "530":{"_iID":530,"_iType":5,"_iEquipId":530,"_iEquipPart":2,"_iLevelNeed":55,"_iQuality":4,"_kJobNeed":[1000]}
        }
        """);
        File.WriteAllText(Path.Combine(root, "config", "EquipConfig.json"), """{"501":{"_kBasicAttribute":[[103,7]]},"530":{"_kBasicAttribute":[[103,90]]}}""");
        if (!wardrobe) return;
        File.WriteAllText(Path.Combine(root, "config", "WardRobeConfig.json"), """
        {
          "7001":{"_iID":7001,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"h1"},
          "7002":{"_iID":7002,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"h2"},
          "7003":{"_iID":7003,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"h3"},
          "7004":{"_iID":7004,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"h4"},
          "7005":{"_iID":7005,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"h5"},
          "7006":{"_iID":7006,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"h6"},
          "7101":{"_iID":7101,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":2,"_kModelResourceG":"g1"},
          "7102":{"_iID":7102,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":2,"_kModelResourceG":"g2"},
          "7103":{"_iID":7103,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":2,"_kModelResourceG":"g3"},
          "7104":{"_iID":7104,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":2,"_kModelResourceG":"g4"},
          "7105":{"_iID":7105,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":2,"_kModelResourceG":"g5"},
          "7106":{"_iID":7106,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":2,"_kModelResourceG":"g6"},
          "7099":{"_iID":7099,"_iType":7,"_iDefaultLightUp":1,"_iDisable":1,"_kModelResourceB":"x","_kModelResourceG":"x"},
          "4001":{"_iID":4001,"_iType":4,"_iDefaultLightUp":1,"_kModelResourceB":"c1","_kModelResourceG":"c1"},
          "4002":{"_iID":4002,"_iType":4,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"c2"},
          "4003":{"_iID":4003,"_iType":4,"_iDefaultLightUp":1,"_iSexNeed":2,"_kModelResourceG":"c3"},
          "4004":{"_iID":4004,"_iType":4,"_iDefaultLightUp":1,"_kModelResourceB":"c4"},
          "4005":{"_iID":4005,"_iType":4,"_iDefaultLightUp":1,"_iLuxury":1,"_kModelResourceB":"lux","_kModelResourceG":"lux"},
          "4006":{"_iID":4006,"_iType":4,"_iDefaultLightUp":1,"_kModelResourceB":"fx","_kModelResourceG":"fx","_kEffectPathsB":["glow"]},
          "4007":{"_iID":4007,"_iType":4,"_iQuality":2,"_kModelResourceB":"u1","_kModelResourceG":"u1"},
          "4008":{"_iID":4008,"_iType":4,"_iQuality":4,"_kModelResourceB":"u2","_kModelResourceG":"u2"},
          "4009":{"_iID":4009,"_iType":4,"_iDefaultLightUp":1,"_iDisable":1,"_kModelResourceB":"d","_kModelResourceG":"d"},
          "2001":{"_iID":2001,"_iType":2,"_iDefaultLightUp":1,"_kModelResourceB":"f1","_kModelResourceG":"f1"},
          "2002":{"_iID":2002,"_iType":2,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"f2"},
          "3001":{"_iID":3001,"_iType":3,"_iDefaultLightUp":1,"_kModelResourceB":"m1","_kModelResourceG":"m1"},
          "9001":{"_iID":9001,"_iType":9,"_iDefaultLightUp":1,"_kModelResourceB":"s1","_kModelResourceG":"s1"},
          "5001":{"_iID":5001,"_iType":5,"_iDefaultLightUp":1,"_kModelResourceB":"wings","_kModelResourceG":"wings"},
          "6001":{"_iID":6001,"_iType":6,"_iDefaultLightUp":1,"_kModelResourceB":"mount","_kModelResourceG":"mount"},
          "8001":{"_iID":8001,"_iType":8,"_iDefaultLightUp":1,"_kModelResourceB":"aura","_kModelResourceG":"aura"},
          "1001":{"_iID":1001,"_iType":1,"_iQuality":6,"_iLuxury":1,"_kModelResourceB":"crown","_kModelResourceG":"crown"},
          "1002":{"_iID":1002,"_iType":1,"_iQuality":2,"_kModelResourceB":"cap","_kModelResourceG":"cap"},
          "1003":{"_iID":1003,"_iType":1,"_iQuality":5,"_kModelResourceB":"helm","_kModelResourceG":"helm"}
        }
        """);
    }

    private static PopulationUnit Bot(int gender = 1, int level = 1) => new() { Uid = 1, Name = "Look", Gender = gender, Level = level, Career = 1000 };

    [Fact]
    public void LooksRespectSexModelAndExcludePremiumEffectsAndSlotsWithoutStats()
    {
        WriteConfig(); var loadouts = new PopulationLoadouts(data);
        int[] maleOnly = [7001, 7002, 7003, 7004, 7005, 7006, 4002, 4004, 2002], femaleOnly = [7101, 7102, 7103, 7104, 7105, 7106, 4003];
        var worn = new HashSet<int>();
        foreach (var style in new[] { Fashion, Distinctive, Fashion with { UnlockableFashion = true } })
            foreach (int gender in new[] { 1, 2 })
                for (int seed = 0; seed < 150; seed++)
                {
                    var bot = Bot(gender, 60); loadouts.Apply(bot, 6, seed, style);
                    var legacy = Bot(gender, 60); loadouts.Apply(legacy, 6, seed);
                    Assert.Equal(legacy.GearAttributes, bot.GearAttributes); Assert.Equal(legacy.EquipmentLook, bot.EquipmentLook);
                    Assert.All(bot.Appearances.Keys, slot => Assert.Contains(slot, new[] { 1, 2, 3, 4, 7, 9 }));
                    Assert.All(bot.Appearances.Values, id => Assert.DoesNotContain(id, Premium));
                    Assert.All(bot.Appearances.Values, id => Assert.DoesNotContain(id, gender == 1 ? femaleOnly : maleOnly));
                    if (!style.UnlockableFashion) Assert.DoesNotContain(bot.Appearances.GetValueOrDefault(4), new[] { 4007, 4008 });
                    Assert.NotEqual(1003, bot.Appearances.GetValueOrDefault(1)); // above worn rarity 4
                    Assert.Equal(bot.Appearances.OrderBy(p => p.Key), bot.Entity().CurUseAppearances.OrderBy(p => p.Key));
                    worn.UnionWith(bot.Appearances.Values);
                }
        Assert.Superset(new HashSet<int> { 4001, 4002, 4003, 4008, 2001, 3001, 9001, 1002 }, worn);
        // Beginners stay plain-capped even when drawn distinctive: no headgear and no rarity above their gear.
        for (int seed = 0; seed < 50; seed++)
        {
            var novice = Bot(); loadouts.Apply(novice, 2, seed, Distinctive);
            Assert.False(novice.Appearances.ContainsKey(1)); Assert.NotEqual(4008, novice.Appearances.GetValueOrDefault(4));
        }
    }

    [Fact]
    public void PresetWeightsFavorFashionInCitiesAndPlainLooksInFields()
    {
        WriteConfig(); var loadouts = new PopulationLoadouts(data);
        var settings = new PopulationAppearanceOptions();
        int Styled(string profile) => Enumerable.Range(0, 400).Count(seed =>
        {
            var bot = Bot(1, 30); loadouts.Apply(bot, 3, seed, PopulationLookStyle.For(settings, profile)); return bot.Appearances.Count > 1;
        });
        int city = Styled("city"), field = Styled("field");
        Assert.True(city > field * 2, $"city {city}, field {field}");
        Assert.Equal(field, Styled("unknown-profile"));
        for (int seed = 0; seed < 50; seed++)
        {
            var bot = Bot(2, 30); loadouts.Apply(bot, 3, seed, Plain);
            Assert.Equal(new[] { 7 }, bot.Appearances.Keys); // plain: hair and real job gear only
        }
        var bound = Microsoft.Extensions.Configuration.ConfigurationBinder.Get<ServerOptions>(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Population:Appearance:Profiles:city:Fashion"] = "90",
                ["Population:Appearance:DistinctRetries"] = "2", ["Population:Appearance:Enabled"] = "false" }).Build())!;
        Assert.Equal(90, bound.Population.Appearance.Profiles["city"].Fashion); Assert.Equal(80, bound.Population.Appearance.Profiles["field"].Plain);
        Assert.Equal(2, bound.Population.Appearance.DistinctRetries); Assert.False(bound.Population.Appearance.Enabled);
    }

    [Fact]
    public void NearbyIdenticalLooksAreRerolledBoundedAndRebuildFromTheChosenSeed()
    {
        WriteConfig(); var loadouts = new PopulationLoadouts(data);
        var neighbour = Bot(); loadouts.Apply(neighbour, 2, 77, Fashion);
        var bot = Bot();
        long chosen = loadouts.ApplyDistinct(bot, 2, 77, Fashion, [neighbour], 4);
        Assert.NotEqual(77, chosen); Assert.Equal(chosen, bot.LookSeed);
        Assert.NotEqual(PopulationLoadouts.LookKey(neighbour), PopulationLoadouts.LookKey(bot));
        var rebuilt = Bot(); loadouts.Apply(rebuilt, 2, bot.LookSeed, Fashion);
        Assert.Equal(bot.Appearances, rebuilt.Appearances); Assert.Equal(bot.EquipmentLook, rebuilt.EquipmentLook);
        Assert.Equal(77, loadouts.ApplyDistinct(bot, 2, 77, Fashion, [neighbour], 0)); // retries off: identity look kept
        Assert.Equal(PopulationLoadouts.LookKey(neighbour), PopulationLoadouts.LookKey(bot));
        Assert.Equal(77, loadouts.ApplyDistinct(bot, 2, 77, Fashion, [], 4)); // no neighbours: no reroll
        // A crowd that occupies every reachable look keeps the identity look instead of searching unboundedly.
        var crowd = Enumerable.Range(0, 9).Select(k => { var b = Bot(); loadouts.Apply(b, 2, k == 0 ? 77 : unchecked(77 + k * (long)0x9E3779B97F4A7C15UL), Fashion); return b; }).ToArray();
        Assert.Equal(77, loadouts.ApplyDistinct(bot, 2, 77, Fashion, crowd, 8));
        Assert.Equal(PopulationLoadouts.LookKey(crowd[0]), PopulationLoadouts.LookKey(bot));
    }

    [Fact]
    public void LookIsStableForASeedAndHairSurvivesAreaChanges()
    {
        WriteConfig(); var loadouts = new PopulationLoadouts(data);
        for (int seed = 0; seed < 40; seed++)
        {
            var a = Bot(1, 60); loadouts.Apply(a, 6, seed, Fashion);
            var b = Bot(1, 60); loadouts.Apply(b, 6, seed, Fashion);
            Assert.Equal(a.Appearances, b.Appearances); Assert.Equal(a.EquipmentLook, b.EquipmentLook);
            var moved = Bot(1, 60); loadouts.Apply(moved, 2, seed, Plain);
            Assert.Equal(a.Appearances[7], moved.Appearances[7]);
        }
    }

    [Fact]
    public void MissingWardrobeOrEquipmentDataFallsBackToBaselineLook()
    {
        var loadouts = new PopulationLoadouts(data); // no config files at all
        var bare = Bot(1, 30); loadouts.Apply(bare, 6, 5, Distinctive);
        Assert.Empty(bare.Appearances); Assert.All(bare.EquipmentLook, id => Assert.Equal(0, id)); Assert.Equal(12 + 30 * 4, bare.Attack);
        Assert.Equal(5, loadouts.ApplyDistinct(bare, 6, 5, Distinctive, [Bot(1, 30)], 4));
    }

    [Fact]
    public async Task ServiceGivesNearbyBotsDistinctRebuildableLooksAndHonorsTheOffSwitch()
    {
        WriteConfig();
        Directory.CreateDirectory(Path.Combine(root, "nav"));
        File.WriteAllText(Path.Combine(root, "config", "MapConfig.json"), """{"1":{"_iMapResourceID":1}}""");
        File.WriteAllText(Path.Combine(root, "config", "MapResource.json"), """{"1":{"_kNavDataName":"test_dynamicmapdata.bytes"}}""");
        byte[] grid = new byte[64 + 256 * 256 * 4];
        BitConverter.GetBytes(256).CopyTo(grid, 20); BitConverter.GetBytes(256).CopyTo(grid, 24);
        BitConverter.GetBytes(256).CopyTo(grid, 28); BitConverter.GetBytes(64).CopyTo(grid, 48);
        for (int i = 64; i < grid.Length; i += 4) grid[i] = 0x80;
        File.WriteAllBytes(Path.Combine(root, "nav", "test_dynamicmapdata.bytes"), grid);
        options.Population = new() { Enabled = true, PerMap = 8, MaxTotal = 8, MaxSpawnsPerTick = 100,
            Appearance = new() { DistinctRetries = 8 },
            Maps = new() { [1] = new() { Profile = "city", AreaLevel = 5, MinLevel = 5, MaxLevel = 5 } } };
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        var world = new World(new NullSink(), loop, NullLogger<World>.Instance);
        var wrapped = Options.Create(options);
        var population = new PopulationService(world, new WorldData(wrapped, data, NullLogger<WorldData>.Instance), data, loop, wrapped);
        var player = new GameSession(null!, null!) { Role = new Ro3Emu.Server.Accounts.Role(1, "test", 1, "Player1", 1, 1000, 5, 0, 0, 0, 1, 128, 0, 128, 0),
            SceneEntry = 1, LineNumber = 1, Position = new Vec3 { X = 128, Z = 128 } };
        await world.JoinAsync(player);
        await population.UpdateAsync(1000);
        var bots = world.AllUnits().OfType<PopulationUnit>().ToArray();
        Assert.Equal(8, bots.Length);
        var loadouts = new PopulationLoadouts(data);
        var style = PopulationLookStyle.For(options.Population.Appearance, "city");
        foreach (var bot in bots)
        {
            var rebuilt = Bot(bot.Gender, bot.Level); loadouts.Apply(rebuilt, 2, bot.LookSeed, style);
            Assert.Equal(rebuilt.Appearances, bot.Appearances); Assert.Equal(rebuilt.EquipmentLook, bot.EquipmentLook);
            var near = bots.Where(o => o != bot && o.Gender == bot.Gender && (o.X - bot.X) * (o.X - bot.X) + (o.Z - bot.Z) * (o.Z - bot.Z) < 30 * 30);
            Assert.DoesNotContain(PopulationLoadouts.LookKey(bot), near.Select(PopulationLoadouts.LookKey));
        }
        await population.SetCountAsync(0);
        options.Population.Appearance.Enabled = false;
        await population.SetCountAsync(null);
        foreach (var bot in world.AllUnits().OfType<PopulationUnit>())
        {
            var legacy = Bot(bot.Gender, bot.Level); loadouts.Apply(legacy, 2, bot.LookSeed);
            Assert.Equal(legacy.Appearances, bot.Appearances); Assert.Equal(legacy.EquipmentLook, bot.EquipmentLook);
        }
    }

    private sealed class NullSink : IAoiSink
    {
        public Task EnterAsync(GameSession v, GameSession s) => Task.CompletedTask;
        public Task MoveAsync(GameSession v, GameSession s, WalkReq w) => Task.CompletedTask;
        public Task TurnAsync(GameSession v, GameSession s, float f) => Task.CompletedTask;
        public Task LeaveAsync(GameSession v, GameSession s) => Task.CompletedTask;
        public Task UnitsChangedAsync(GameSession v, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left) => Task.CompletedTask;
        public Task UnitMoveAsync(GameSession v, MovementChangeNtf movement) => Task.CompletedTask;
    }
}
