using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Synthetic config/nav and the real World AOI path; no game exports or network required.</summary>
public sealed class PopulationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ro3-population-" + Guid.NewGuid().ToString("N"));
    private readonly GameLoop loop = new(NullLogger<GameLoop>.Instance);
    private readonly RecordingSink sink = new();
    private readonly World world;
    private readonly PopulationService population;
    private readonly ServerOptions options;

    public PopulationTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "nav"));
        File.WriteAllText(Path.Combine(root, "config", "MapConfig.json"), """{"1":{"_iMapResourceID":1},"2":{"_iMapResourceID":2}}""");
        File.WriteAllText(Path.Combine(root, "config", "MapResource.json"), """{"1":{"_kNavDataName":"test_dynamicmapdata.bytes"}}""");
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """{"1000":{"_iUnitB":1000,"_iUnitG":1001},"1100":{"_iUnitB":1100,"_iUnitG":1101}}""");
        // One flat 256x256 block; valid NavGrid layout, every cell walkable at y=0.
        byte[] grid = new byte[64 + 256 * 256 * 4];
        BitConverter.GetBytes(256).CopyTo(grid, 20);
        BitConverter.GetBytes(256).CopyTo(grid, 24);
        BitConverter.GetBytes(256).CopyTo(grid, 28);
        BitConverter.GetBytes(64).CopyTo(grid, 48);
        for (int i = 64; i < grid.Length; i += 4) grid[i] = 0x80;
        File.WriteAllBytes(Path.Combine(root, "nav", "test_dynamicmapdata.bytes"), grid);
        options = new ServerOptions { DataDir = root, Population = new() { Enabled = true, PerMap = 6, MaxTotal = 10 } };
        var wrapped = Options.Create(options);
        var data = new GameData(wrapped, NullLogger<GameData>.Instance);
        world = new World(sink, loop, NullLogger<World>.Instance);
        population = new PopulationService(world, new WorldData(wrapped, data, NullLogger<WorldData>.Instance), data, loop, wrapped);
    }

    private static GameSession Player(int id = 1, int line = 1, long scene = 1, long instance = 0) => new(null!, null!)
    {
        Role = new Role(id, "test", 1, "Player" + id, 1, 1000, 5, 0, 0, 0, scene, 128, 0, 128, 0),
        SceneEntry = scene, InstanceId = instance, LineNumber = line, Position = new Vec3 { X = 128, Z = 128 },
    };

    [Fact]
    public async Task PopulationCreatesPlayerBasesAndLateJoinerSeesSameBots()
    {
        var player = Player(); await world.JoinAsync(player);
        await population.UpdateAsync(1000);
        Assert.Equal(6, population.Count);
        Assert.Equal(6, player.VisibleUnits.Count);
        Assert.All(sink.Bases, entity =>
        {
            Assert.Equal(3, entity.EntityType);
            Assert.Contains((int)entity.EntityTemplateID, new[] { 1000, 1100 });
            Assert.InRange(entity.Gender, 1, 2);
            Assert.InRange(entity.Uuid, 1UL << 50, (1UL << 51) - 1);
            Assert.Equal(5, entity.Property.Single(p => p.Idx == 2002).Data);
        });
        Assert.Equal(6, sink.Bases.Select(b => b.Name).Distinct().Count());
        var other = Player(2); await world.JoinAsync(other);
        Assert.Equal(player.VisibleUnits.Select(u => u.Uid).Order(), other.VisibleUnits.Select(u => u.Uid).Order());
        Assert.Equal(2, world.ActivePlayers().Count); // bots never enter real player admission/counting
    }

    [Fact]
    public async Task LastPlayerLeaveReleasesBotsAndTheirViewerMembership()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bots = world.AllUnits().ToArray();
        await world.LeaveAsync(player); await population.UpdateAsync(2000);
        Assert.Equal(0, population.Count); Assert.Empty(world.AllUnits());
        Assert.Empty(player.VisibleUnits); Assert.All(bots, b => Assert.Empty(b.Viewers));
    }

    [Fact]
    public async Task LinesAreIsolatedAndGlobalBudgetIsEnforced()
    {
        var a = Player(); var b = Player(2, line: 2);
        await world.JoinAsync(a); await world.JoinAsync(b); await population.UpdateAsync(1000);
        Assert.Equal(10, population.Count);
        Assert.Equal(6, a.VisibleUnits.Count); Assert.Equal(4, b.VisibleUnits.Count);
        Assert.All(a.VisibleUnits, u => Assert.Equal(a.RuntimeMap, u.RuntimeMap));
        Assert.All(b.VisibleUnits, u => Assert.Equal(b.RuntimeMap, u.RuntimeMap));
        await world.LeaveAsync(a); await population.UpdateAsync(2000);
        Assert.Equal(6, b.VisibleUnits.Count); Assert.Equal(6, population.Count);
    }

    [Fact]
    public async Task PrivateMapsLoadingPlayersAndMissingNavigationHaveNoBots()
    {
        var privatePlayer = Player(instance: 123); var missingNav = Player(2, scene: 2);
        var loading = Player(3); loading.SceneLoading = true;
        await world.JoinAsync(privatePlayer); await world.JoinAsync(missingNav); await world.JoinAsync(loading);
        await population.UpdateAsync(1000);
        Assert.Equal(0, population.Count);
    }

    [Fact]
    public async Task UnwalkableMapsNeverReceiveBots()
    {
        string path = Path.Combine(root, "nav", "test_dynamicmapdata.bytes");
        var grid = File.ReadAllBytes(path);
        for (int i = 64; i < grid.Length; i += 4) grid[i] = 0;
        File.WriteAllBytes(path, grid);
        await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        Assert.Equal(0, population.Count); Assert.Empty(sink.Bases);
    }

    [Fact]
    public async Task DisabledByDefaultAndNoPlayersMeansNoPopulation()
    {
        Assert.False(new ServerOptions().Population.Enabled);
        await population.UpdateAsync(1000); Assert.Equal(0, population.Count);
        options.Population.Enabled = false;
        await world.JoinAsync(Player()); await population.UpdateAsync(2000);
        Assert.Equal(0, population.Count);
    }

    [Fact]
    public async Task DisableReenableAndCountReductionPublishLeaves()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        await population.SetCountAsync(2);
        Assert.Equal(2, population.Count); Assert.Equal(4, sink.Left.Count);
        await population.SetCountAsync(0);
        Assert.Equal(0, population.Count); Assert.Empty(player.VisibleUnits); Assert.Equal(6, sink.Left.Count);
        options.Population.Enabled = false;
        await population.SetCountAsync(null); Assert.Equal(0, population.Count);
        await population.SetCountAsync(3); Assert.Equal(3, population.Count);
    }

    [Fact]
    public async Task PopulationRelocatesWhenPlayerMovesAcrossMap()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var oldIds = world.AllUnits().Select(u => u.Uid).ToHashSet();
        player.Position = new Vec3 { X = 20, Z = 20 };
        await world.MoveAsync(player, new WalkReq { Pos = player.Position });
        await population.UpdateAsync(2000);
        Assert.Equal(6, population.Count);
        Assert.All(world.AllUnits(), u => Assert.DoesNotContain(u.Uid, oldIds));
        Assert.Equal(6, player.VisibleUnits.Count);
    }

    [Fact]
    public async Task RoamingSendsWalkPacketsAndStaysOnNavigation()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        for (long now = 2000; now <= 20000; now += 1000) await population.UpdateAsync(now);
        Assert.Contains(sink.Moves, m => m.MovementInfo.MoveType == MovementType.MoveTypeWalk);
        Assert.All(world.AllUnits(), u => Assert.InRange(u.Y, -0.01f, 0.01f));
        Assert.All(sink.Moves, m => Assert.InRange(m.MovementInfo.MoveDuration, 0, 2400));
    }

    [Fact]
    public void LateViewerReceivesRemainingWalkAndExactStop()
    {
        var bot = new PopulationUnit { Uid = 1L << 50, Name = "Test", X = 10, Z = 10 };
        bot.WalkTo(new MapPoint(15, 0, 10, 0), 1000);
        bot.Advance(2000);
        var entity = bot.Entity();
        Assert.Equal(12.5f, entity.MovementInfo.Pos.X);
        Assert.Equal(1000, entity.MovementInfo.MoveDuration);
        bot.Advance(3000);
        Assert.False(bot.Moving); Assert.Equal(15, bot.X);
        Assert.Equal(MovementType.MoveTypeNone, bot.Entity().MovementInfo.MoveType);
    }

    [Theory]
    [InlineData(-10, -1, 0, 0)]
    [InlineData(1000, 1000, 100, 500)]
    public void ConfigurationIsBounded(int perMap, int total, int expectedMap, int expectedTotal)
    {
        options.Population.PerMap = perMap; options.Population.MaxTotal = total;
        Assert.Equal(expectedMap, population.PerMap); Assert.Equal(expectedTotal, population.Limit);
    }

    public void Dispose() { loop.Dispose(); Directory.Delete(root, true); }

    private sealed class RecordingSink : IAoiSink
    {
        public List<EntityBase> Bases { get; } = new();
        public List<long> Left { get; } = new();
        public List<MovementChangeNtf> Moves { get; } = new();
        public Task EnterAsync(GameSession v, GameSession s) => Task.CompletedTask;
        public Task MoveAsync(GameSession v, GameSession s, WalkReq w) => Task.CompletedTask;
        public Task TurnAsync(GameSession v, GameSession s, float f) => Task.CompletedTask;
        public Task LeaveAsync(GameSession v, GameSession s) => Task.CompletedTask;
        public Task UnitsChangedAsync(GameSession v, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left)
        {
            Bases.AddRange(entered.Select(u => u.Entity())); Left.AddRange(left); return Task.CompletedTask;
        }
        public Task UnitMoveAsync(GameSession v, MovementChangeNtf movement) { Moves.Add(movement); return Task.CompletedTask; }
    }
}
