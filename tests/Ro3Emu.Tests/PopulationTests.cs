using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Micro;
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
    private readonly GameData data;
    private readonly WorldData worldData;

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
        data = new GameData(wrapped, NullLogger<GameData>.Instance);
        world = new World(sink, loop, NullLogger<World>.Instance);
        worldData = new WorldData(wrapped, data, NullLogger<WorldData>.Instance);
        population = new PopulationService(world, worldData, data, loop, wrapped);
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
        player.Position = new Vec3 { X = 500, Z = 500 };
        await world.MoveAsync(player, new WalkReq { Pos = player.Position });
        await population.UpdateAsync(2000);
        Assert.Equal(0, population.Count);
        player.Position = new Vec3 { X = 128, Z = 128 };
        await world.MoveAsync(player, new WalkReq { Pos = player.Position });
        await population.UpdateAsync(3000);
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
        bot.WalkTo(new MapPoint(20, 0, 10, 0), 1000);
        bot.Advance(2000);
        var entity = bot.Entity();
        Assert.Equal(15f, entity.MovementInfo.Pos.X);
        Assert.Equal(1000, entity.MovementInfo.MoveDuration);
        bot.Advance(3000);
        Assert.False(bot.Moving); Assert.Equal(20, bot.X);
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

    [Fact]
    public void OfflineHandlesAreUniqueAndReadable()
    {
        var names = Enumerable.Range(1, 10000).Select(i => PopulationNames.ForSerial(i)).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var firstCohort = names.Take(500).ToArray();
        Assert.True(firstCohort.Count(n => n.Any(char.IsDigit)) > 50);
        Assert.True(firstCohort.Count(n => n.All(char.IsLetter)) > 200);
        Assert.True(firstCohort.Select(n => n[..Math.Min(3, n.Length)]).Distinct().Count() > 40);
        Assert.Equal(names, Enumerable.Range(1, 10000).Select(i => PopulationNames.ForSerial(i)).ToArray());
        Assert.All(names, name => { Assert.InRange(name.Length, 3, 16); Assert.All(name, c => Assert.True(char.IsLetterOrDigit(c) || c == '_')); });
    }

    [Fact]
    public async Task ProfileServiceSuppliesHeadNamesWithoutCreatingAccounts()
    {
        await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        var bot = (PopulationUnit)world.AllUnits().First();
        var profile = population.Profile(bot.Uid, 123, 7)!;
        var fields = (Dictionary<string, object?>)profile["Data"]!;
        Assert.Equal(bot.Name, fields["Name"]); Assert.Equal(true, fields["Online"]);
        Assert.Equal(7, fields["ClusterId"]);
        var info = RoleAdditionBaseInfo.Parser.ParseFrom((byte[])fields["Base"]!);
        Assert.Equal(bot.Career, info.Career); Assert.True(info.CantBeInvited);
        await population.SetCountAsync(0); Assert.Null(population.Profile(bot.Uid, 124, 7));
    }

    [Fact]
    public async Task RealUserDataServiceReturnsBotProfilesWithoutDatabaseReads()
    {
        await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        var bot = (PopulationUnit)world.AllUnits().First();
        var service = new UserDataService(null!, null!, loop, null!, null!, Options.Create(options),
            NullLogger<UserDataService>.Instance, population);
        var args = new Dictionary<object, object?> { ["UidFieldVersions"] = new Dictionary<object, object?>
            { [bot.Uid.ToString()] = new Dictionary<object, object?> { ["Name"] = 0L, ["Base"] = 0L } } };
        await loop.StartAsync(default);
        try
        {
            var response = (Dictionary<string, object?>)(await service.InvokeAsync(null!, "getAttributeBatchByVersion", args))!;
            var profile = (Dictionary<string, object?>)Assert.Single((object[])response["Results"]!);
            Assert.Equal(bot.Name, ((Dictionary<string, object?>)profile["Data"]!)["Name"]);
        }
        finally { await loop.StopAsync(default); }
    }

    [Fact]
    public async Task JourneysCoverLongDistancesWithoutIdleGapsBetweenSegments()
    {
        options.Population.PerMap = 1;
        await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        var bot = (PopulationUnit)world.AllUnits().Single();
        var start = bot.Current;
        float maximumDistance = 0;
        for (long now = 1250; now < 45000; now += 250)
        {
            await population.UpdateAsync(now);
            maximumDistance = Math.Max(maximumDistance, MonsterService.Distance(start.X, start.Z, bot.X, bot.Z));
            Assert.True(worldData.Map(1)!.Nav!.IsWalkable(bot.X, bot.Y, bot.Z));
        }
        Assert.True(maximumDistance > 25, $"Only travelled {maximumDistance}m");
        Assert.Contains(sink.Moves, m => m.MovementInfo.MoveDuration > 1500);
    }

    [Fact]
    public async Task HuntingArrivalKeepsHuntingAndNpcVisitResetsKillCount()
    {
        await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        var bot = (PopulationUnit)world.AllUnits().First();
        bot.Stop(1000); bot.Route.Clear(); bot.JourneyActive = true; bot.VisitingNpc = false;
        await population.UpdateAsync(1250);
        Assert.False(bot.VisitingNpc); Assert.True(bot.ActivityUntilMs > 1250);
        bot.JourneyActive = true; bot.VisitingNpc = true; bot.HuntKills = 3;
        await population.UpdateAsync(1500);
        Assert.False(bot.VisitingNpc); Assert.Equal(0, bot.HuntKills);
    }

    [Fact]
    public void RouteDetoursAroundWallsAndRejectsDisconnectedGoals()
    {
        string path = Path.Combine(root, "nav", "test_dynamicmapdata.bytes");
        var grid = File.ReadAllBytes(path);
        for (int z = 0; z < 35; z++) grid[64 + (z * 256 + 20) * 4] = 0;
        File.WriteAllBytes(path, grid);
        var nav = NavGrid.Load(path);
        var start = new MapPoint(10.5f, 0, 20.5f, 0); var goal = new MapPoint(30.5f, 0, 20.5f, 0);
        var route = PopulationNavigation.Route(nav, start, goal);
        Assert.NotEmpty(route); Assert.Contains(route, p => p.Z >= 35);
        foreach (var next in route) { Assert.True(nav.CanWalk(start.X, start.Y, start.Z, next.X, next.Z)); start = next; }
        Assert.Equal(goal, route.Last());
        for (int z = 0; z < 256; z++) grid[64 + (z * 256 + 20) * 4] = 0;
        File.WriteAllBytes(path, grid);
        Assert.Empty(PopulationNavigation.Route(NavGrid.Load(path), new(10.5f, 0, 20.5f, 0), goal, 100));
    }

    private (CombatService Combat, MonsterService Monsters) CombatServices()
    {
        var character = new CharacterService(null!, null!, data, world, NullLogger<CharacterService>.Instance);
        var skills = new SkillService(data, character, null!, NullLogger<SkillService>.Instance);
        var sessions = new SessionRegistry(NullLogger<SessionRegistry>.Instance);
        var buffs = new BuffService(data, world, sessions, character, loop, NullLogger<BuffService>.Instance);
        var monsters = new MonsterService(world, worldData, data, loop, NullLogger<MonsterService>.Instance);
        var scene = new SceneService(Options.Create(options), world, worldData, monsters, buffs, data,
            NullLogger<SceneService>.Instance, new PublicLineManager(data, world), TimeProvider.System);
        return (new CombatService(world, monsters, character, skills, scene, buffs, worldData, sessions, data, loop,
            NullLogger<CombatService>.Instance), monsters);
    }

    private static MonsterUnit Monster(long hp = 100) => new()
    {
        Uid = 1L << 41, SceneEntry = 1, X = 128, Z = 128, Hp = hp,
        Template = new MonsterTemplate(1, 1, hp, 0, 10, 10, 0, 0, 0, 0, 0, 0, 1, 1, 1, 2,
            false, 0, 1000, 10, 30, 1000001, 300, 0.05f, 2000, 2, 100),
        Home = new SpawnPoint(1, 128, 0, 128, 0, 1), Group = new SpawnGroup(1, false, 1, 10, [], []),
    };

    private static PopulationUnit Fighter() => new()
        { Uid = 1L << 50, Name = "Test", SceneEntry = 1, X = 128, Z = 128, Level = 5, Hp = 100 };

    [Fact]
    public void BotsYieldToPlayerTargetsDamageAndOtherBots()
    {
        var bot = Fighter(); var m = Monster();
        Assert.True(PopulationService.PopulationCanHunt(bot, m));
        m.PopulationTarget = Fighter(); Assert.False(PopulationService.PopulationCanHunt(bot, m));
        m.PopulationTarget = bot; bot.TargetExpectedHp = m.Hp;
        Assert.True(PopulationService.PopulationCanHunt(bot, m));
        m.Hp--; Assert.False(PopulationService.PopulationCanHunt(bot, m));
        m.Hp++; m.Target = Player(); Assert.False(PopulationService.PopulationCanHunt(bot, m));
        m.Target = null; m.RewardRecipient = 1; Assert.False(PopulationService.PopulationCanHunt(bot, m));
    }

    [Fact]
    public async Task BotKillsUseCombatWithoutPlayerRewardCallbacks()
    {
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """{"1000001":{"_iActionTime":300,"_iDistanceMax":300,"_iSkillHit":10000,"_iDmgType":1,"_kDamageParam1":[10000]}}""");
        var (combat, _) = CombatServices();
        int rewards = 0;
        combat.BeforeKilled.Add((_, _) => { rewards++; return Task.CompletedTask; });
        combat.Killed.Add((_, _) => { rewards++; return Task.CompletedTask; });
        var bot = Fighter(); var monster = Monster(10); bot.Target = monster; bot.TargetExpectedHp = 10;
        await loop.StartAsync(default);
        try
        {
            await loop.InvokeAsync(async () => { await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                await combat.PopulationAttackAsync(bot, monster, 1000001, MonsterService.NowMs); });
            await WaitFor(() => monster.Dead);
            Assert.Equal(0, monster.Hp); Assert.Equal(1, bot.HuntKills); Assert.Equal(0, rewards);
            Assert.Null(monster.RewardCommit); Assert.False(monster.DeathDurable);
        }
        finally { await loop.StopAsync(default); }
    }

    [Fact]
    public async Task PlayerDamageBeforeHitCancelsBotDamage()
    {
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """{"1000001":{"_iActionTime":300,"_iDistanceMax":300,"_iSkillHit":10000}}""");
        var (combat, _) = CombatServices();
        var bot = Fighter(); var monster = Monster(); bot.Target = monster; bot.TargetExpectedHp = 100;
        await loop.StartAsync(default);
        try
        {
            await loop.InvokeAsync(async () => { await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                await combat.PopulationAttackAsync(bot, monster, 1000001, MonsterService.NowMs); monster.Hp = 99; });
            await Task.Delay(300);
            Assert.Equal(99, await loop.InvokeAsync(() => Task.FromResult(monster.Hp)));
        }
        finally { await loop.StopAsync(default); }
    }

    [Fact]
    public async Task RemovingBotBeforeHitCancelsDamage()
    {
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """{"1000001":{"_iActionTime":300,"_iDistanceMax":300,"_iSkillHit":10000}}""");
        var (combat, _) = CombatServices();
        var bot = Fighter(); var monster = Monster(); bot.Target = monster; bot.TargetExpectedHp = 100;
        await loop.StartAsync(default);
        try
        {
            await loop.InvokeAsync(async () => { await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                await combat.PopulationAttackAsync(bot, monster, 1000001, MonsterService.NowMs); await world.RemoveUnitAsync(bot); });
            await Task.Delay(300);
            Assert.Equal(100, await loop.InvokeAsync(() => Task.FromResult(monster.Hp)));
        }
        finally { await loop.StopAsync(default); }
    }

    [Fact]
    public async Task MonsterRetaliationAndFirstAidChangeBotHp()
    {
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """{"1000001":{"_iActionTime":300,"_iDistanceMax":300,"_iSkillHit":10000,"_iDmgType":1,"_kDamageParam1":[10000]},"1000301":{"_iActionTime":300,"_iCD":10000,"_kDamageParam2":[150]}}""");
        var (combat, monsters) = CombatServices();
        var bot = Fighter(); var monster = Monster(); bot.Target = monster; bot.TargetExpectedHp = 100; bot.HealSkill = 1000301;
        await loop.StartAsync(default);
        try
        {
            await loop.InvokeAsync(async () => { await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                monsters.Engage(monster, bot); await monsters.AttackPopulation!(monster, bot); });
            await WaitFor(() => bot.Hp < 100);
            long damaged = await loop.InvokeAsync(() => Task.FromResult(bot.Hp));
            await loop.InvokeAsync(() => combat.PopulationHealAsync(bot, MonsterService.NowMs));
            await WaitFor(() => bot.Hp > damaged);
            Assert.True(bot.SkillReadyMs > MonsterService.NowMs);
            Assert.InRange(bot.Hp, damaged + 1, bot.MaxHp);
        }
        finally { await loop.StopAsync(default); }
    }

    private async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await loop.InvokeAsync(() => Task.FromResult(condition()))) await Task.Delay(10, timeout.Token);
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
