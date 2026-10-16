using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Density controls: gradual shrink/hysteresis, landmark clearance, companion reserve, live reload and readouts.
/// Same synthetic fixture style as PopulationTests (flat 256x256 grid); scene 3 adds NPC/portal/born landmarks.</summary>
public sealed class PopulationDensityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ro3-population-density-" + Guid.NewGuid().ToString("N"));
    private readonly GameLoop loop = new(NullLogger<GameLoop>.Instance);
    private readonly World world;
    private readonly PopulationService population;
    private readonly ServerOptions options;
    private readonly WorldData worldData;

    public PopulationDensityTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "nav"));
        Directory.CreateDirectory(Path.Combine(root, "sceneeditor"));
        File.WriteAllText(Path.Combine(root, "config", "MapConfig.json"),
            """{"1":{"_iMapResourceID":1},"2":{"_iMapResourceID":2},"3":{"_iMapResourceID":1,"_kResSceneEditorName":"PopClear"}}""");
        File.WriteAllText(Path.Combine(root, "config", "MapResource.json"), """{"1":{"_kNavDataName":"test_dynamicmapdata.bytes"}}""");
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """{"1000":{"_iUnitB":1000,"_iUnitG":1001},"1100":{"_iUnitB":1100,"_iUnitG":1101}}""");
        File.WriteAllText(Path.Combine(root, "config", "NPCConfig.json"), """{"9001":{"_iNPCType":2},"9002":{"_iNPCType":2}}""");
        File.WriteAllText(Path.Combine(root, "sceneeditor", "popclear.json"), """
            {"m_kBornPoints":[{"isDefault":true,"position":{"x":118,"y":0,"z":128},"direction":0}],
             "kNpcList":{"1":{"NpcTag":2,"IsServerNpc":true,"NpcId":9001,"x":128,"y":0,"z":128},
                         "2":{"NpcTag":26,"NpcId":9002,"x":140,"y":0,"z":128,"FootholdPos_x":3,"FootholdPos_z":0}}}
            """);
        byte[] grid = new byte[64 + 256 * 256 * 4];
        BitConverter.GetBytes(256).CopyTo(grid, 20);
        BitConverter.GetBytes(256).CopyTo(grid, 24);
        BitConverter.GetBytes(256).CopyTo(grid, 28);
        BitConverter.GetBytes(64).CopyTo(grid, 48);
        for (int i = 64; i < grid.Length; i += 4) grid[i] = 0x80;
        File.WriteAllBytes(Path.Combine(root, "nav", "test_dynamicmapdata.bytes"), grid);
        options = new ServerOptions { DataDir = root, Population = new() { Enabled = true, PerMap = 6, MaxTotal = 10, MaxSpawnsPerTick = 100,
            DespawnDelaySeconds = 1, MaxDespawnsPerTick = 2, Maps = new() { [1] = new() { AreaLevel = 5, MinLevel = 5, MaxLevel = 5 } } } };
        var wrapped = Options.Create(options);
        var data = new GameData(wrapped, NullLogger<GameData>.Instance);
        world = new World(new NullSink(), loop, NullLogger<World>.Instance);
        worldData = new WorldData(wrapped, data, NullLogger<WorldData>.Instance);
        population = new PopulationService(world, worldData, data, loop, wrapped);
    }

    private static GameSession Player(int id = 1, int line = 1, long scene = 1, long instance = 0) => new(null!, null!)
    {
        Role = new Role(id, "test", 1, "Player" + id, 1, 1000, 5, 0, 0, 0, scene, 128, 0, 128, 0),
        SceneEntry = scene, InstanceId = instance, LineNumber = line, Position = new Vec3 { X = 128, Z = 128 },
    };

    private int On(GameSession player) => world.AllUnits().OfType<PopulationUnit>().Count(b => b.RuntimeMap == player.RuntimeMap);

    [Fact]
    public async Task BudgetShrinkFromNewLineWaitsForDelayAndNeverExceedsTheCeiling()
    {
        options.Population.DespawnDelaySeconds = 10;
        var a = Player(); await world.JoinAsync(a); await population.UpdateAsync(1000);
        Assert.Equal(6, On(a));
        var b = Player(2, line: 2); await world.JoinAsync(b);
        await population.UpdateAsync(2000); // fair share is now 5/5: line 1 stays over budget, line 2 gets free capacity
        Assert.Equal(6, On(a)); Assert.Equal(4, On(b)); Assert.Equal(1, population.PendingDespawns);
        for (long now = 2250; now < 12000; now += 2250) { await population.UpdateAsync(now); Assert.Equal(6, On(a)); Assert.True(population.Count <= 10); }
        await population.UpdateAsync(12000);
        Assert.Equal(5, On(a)); Assert.Equal(5, On(b)); Assert.Equal(10, population.Count); Assert.Equal(0, population.PendingDespawns);
    }

    [Fact]
    public async Task LoweredTargetShrinksBoundedPerPassIgnoresBriefDipsAndHoldsRefill()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        Assert.Equal(6, population.Count);
        options.Population.PerMap = 1; await population.UpdateAsync(1500);
        options.Population.PerMap = 6; await population.UpdateAsync(1750); // back within target before the delay: no removal
        options.Population.PerMap = 1; await population.UpdateAsync(2000); await population.UpdateAsync(2750);
        Assert.Equal(6, population.Count); // the brief dip reset the over-target timer
        await population.UpdateAsync(3000); Assert.Equal(4, population.Count);
        await population.UpdateAsync(3250); Assert.Equal(2, population.Count);
        await population.UpdateAsync(3500); Assert.Equal(1, population.Count);
        await population.UpdateAsync(3750); Assert.Equal(1, population.Count);
        options.Population.PerMap = 6;
        await population.UpdateAsync(4000); Assert.Equal(1, population.Count); // refill held for one delay after a shrink
        await population.UpdateAsync(4600); Assert.Equal(6, population.Count);
    }

    [Fact]
    public async Task CommandsOffAndEmptyMapsStillRemoveImmediately()
    {
        options.Population.DespawnDelaySeconds = 600;
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        await population.SetCountAsync(2); Assert.Equal(2, population.Count);
        await population.SetMapCountAsync(1, 1); Assert.Equal(1, population.Count);
        await population.SetCountAsync(0); Assert.Equal(0, population.Count);
        await population.SetCountAsync(null); Assert.Equal(6, population.Count);
        await world.LeaveAsync(player); await population.UpdateAsync(2000); Assert.Equal(0, population.Count);
        options.Population.DespawnDelaySeconds = 0; // zero switches gradual shrink off
        await world.JoinAsync(Player()); await population.UpdateAsync(3000);
        options.Population.PerMap = 2; await population.UpdateAsync(3250); Assert.Equal(2, population.Count);
    }

    [Fact]
    public async Task GradualShrinkRemovesBotsOutOfPlayerSightFirst()
    {
        options.Population.MaxDespawnsPerTick = 100;
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var all = world.AllUnits().OfType<PopulationUnit>().ToArray();
        foreach (var bot in all) bot.NextWalkMs = long.MaxValue; // keep AOI membership stable during the test
        var hidden = all.Take(3).ToArray();
        foreach (var bot in hidden) bot.Viewers.Remove(player);
        options.Population.PerMap = 3;
        await population.UpdateAsync(1500); await population.UpdateAsync(2600);
        Assert.Equal(3, population.Count);
        Assert.All(hidden, bot => Assert.True(bot.Removed));
        Assert.All(all.Except(hidden), bot => Assert.False(bot.Removed));
    }

    [Fact]
    public async Task CompanionReserveIsSubtractedFromTheAmbientBudgetAndBounded()
    {
        options.Population.CompanionReserve = 4;
        var a = Player(); var b = Player(2, line: 2);
        await world.JoinAsync(a); await world.JoinAsync(b); await population.UpdateAsync(1000);
        Assert.Equal(6, population.AmbientLimit); Assert.Equal(3, On(a)); Assert.Equal(3, On(b));
        options.Population.CompanionReserve = -5; Assert.Equal(10, population.AmbientLimit);
        options.Population.CompanionReserve = 5000; options.Population.DespawnDelaySeconds = 0;
        Assert.Equal(100, population.CompanionReserve); Assert.Equal(0, population.AmbientLimit);
        await population.UpdateAsync(2000); Assert.Equal(0, population.Count);
        Assert.Contains("companion reserve", await population.DensityStatusAsync(a));
        Assert.Equal(0, new ServerOptions().Population.CompanionReserve);
    }

    [Fact]
    public async Task SpawnsAndNpcVisitSpotsKeepClearOfPortalsNpcsAndArrivalPoints()
    {
        options.Population.LandmarkClearance = 6;
        var map = worldData.Map(3)!;
        var marks = population.Landmarks(map);
        Assert.Contains((128f, 128f), marks); Assert.Contains((140f, 128f), marks); Assert.Contains((143f, 128f), marks); Assert.Contains((118f, 128f), marks);
        Assert.False(population.ClearOfLandmarks(map, 142, 129)); Assert.True(population.ClearOfLandmarks(map, 128, 150));
        var player = Player(scene: 3); await world.JoinAsync(player); await population.UpdateAsync(1000);
        Assert.Equal(6, population.Count);
        Assert.All(world.AllUnits().OfType<PopulationUnit>(), bot =>
            Assert.All(marks, m => Assert.True((bot.X - m.X) * (bot.X - m.X) + (bot.Z - m.Z) * (bot.Z - m.Z) >= 36)));
        var stand = new MapPoint(128, 0, 128, 0);
        for (int i = 0; i < 20; i++)
        {
            var spot = population.VisitSpot(map, stand);
            if (spot is not { } point) continue;
            Assert.True(MathF.Sqrt((point.X - 128) * (point.X - 128) + (point.Z - 128) * (point.Z - 128)) >= 6);
            Assert.True(population.ClearOfLandmarks(map, point.X, point.Z));
        }
        options.Population.LandmarkClearance = 0; // off switch: previous behavior
        Assert.True(population.ClearOfLandmarks(map, 128, 128)); Assert.Equal(stand, population.VisitSpot(map, stand));
    }

    [Fact]
    public async Task LiveReloadRebindsInPlaceKeepsRuntimeOverridesAndRejectsBadInput()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ro3Emu:Population:Enabled"] = "true", ["Ro3Emu:Population:PerMap"] = "6", ["Ro3Emu:Population:MaxTotal"] = "10",
            ["Ro3Emu:Population:MaxSpawnsPerTick"] = "100", ["Ro3Emu:Population:Chat:Mode"] = "quiet",
        }).Build();
        population.WatchConfiguration(config);
        var chat = options.Population.Chat;
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        config["Ro3Emu:Population:PerMap"] = "3"; config["Ro3Emu:Population:Chat:Mode"] = "chatty"; config["Ro3Emu:Population:CompanionReserve"] = "2";
        config.Reload(); population.ApplyPendingReload();
        Assert.Equal(3, options.Population.PerMap); Assert.Equal(2, population.CompanionReserve);
        Assert.Same(chat, options.Population.Chat); Assert.Equal("chatty", population.ChatMode);
        options.Population.DespawnDelaySeconds = 0; await population.UpdateAsync(2000); Assert.Equal(3, population.Count);
        await population.SetCountAsync(5); Assert.Equal(5, population.Count);
        config["Ro3Emu:Population:PerMap"] = "2"; config.Reload(); population.ApplyPendingReload();
        Assert.Equal(5, population.Settings(1).Count); // runtime `!bots 5` keeps precedence over the file
        await population.SetCountAsync(null); Assert.Equal(2, population.Settings(1).Count);
        config["Ro3Emu:Population:PerMap"] = "abc"; config.Reload(); population.ApplyPendingReload();
        population.QueueReload(config.GetSection("Ro3Emu:Missing")); population.ApplyPendingReload();
        Assert.Equal(2, options.Population.PerMap); Assert.True(options.Population.Enabled);
        Assert.Contains("rejected 2", await population.StatsAsync(5000));
        config["Ro3Emu:Population:PerMap"] = "200"; config["Ro3Emu:Population:ReloadOnChange"] = "false"; config.Reload(); population.ApplyPendingReload();
        Assert.Equal(10, population.Settings(1).Count); // reloaded values keep the usual clamps (MaxTotal 10)
        config["Ro3Emu:Population:PerMap"] = "1"; config.Reload(); population.ApplyPendingReload();
        Assert.Equal(200, options.Population.PerMap); // ReloadOnChange=false ignores later file changes
    }

    [Fact]
    public async Task StatusExplainsZeroPopulationAndStatsReportBoundedCounters()
    {
        var a = Player(); var b = Player(2, line: 2);
        await world.JoinAsync(a);
        Assert.Contains("waiting for the next pass", await population.DensityStatusAsync(a));
        await population.UpdateAsync(1000);
        Assert.Contains("map 1 line 1 field: 6 actual / 6 target / 6 allocated", await population.DensityStatusAsync(a));
        options.Population.MaxTotal = 6; await world.JoinAsync(b); await population.UpdateAsync(1250);
        string status = await population.DensityStatusAsync(b);
        Assert.Contains("0 actual / 6 target / 3 allocated", status); Assert.Contains("zero: waiting for other maps to shrink", status);
        options.Population.MaxTotal = 1; await population.UpdateAsync(1500);
        Assert.Contains("zero: global budget exhausted by other maps", await population.DensityStatusAsync(b));
        Assert.Contains("private instance", await population.DensityStatusAsync(Player(3, instance: 77)));
        Assert.Contains("no navigation grid", await population.DensityStatusAsync(Player(4, scene: 2)));
        options.Population.Maps[1] = new() { Enabled = false };
        Assert.Contains("map disabled in configuration", await population.DensityStatusAsync(a));
        options.Population.Enabled = false; Assert.Contains("population disabled", await population.DensityStatusAsync(a));
        var nav = worldData.Map(1)!.Nav!;
        Assert.Empty(population.Route(nav, new MapPoint(128, 0, 128, 0), new MapPoint(1000, 0, 1000, 0)));
        for (int i = 0; i < 600; i++) population.NoteChat(10_000);
        Assert.Equal(512, population.ChatLastMinute(10_001)); Assert.Equal(0, population.ChatLastMinute(70_000));
        string stats = await population.StatsAsync(70_000);
        Assert.Contains("path failures 1", stats); Assert.Contains("chat lines last minute 0", stats); Assert.Contains("pending despawns", stats);
        Assert.Contains("1/L1", stats);
    }

    public void Dispose() { loop.Dispose(); Directory.Delete(root, true); }

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
