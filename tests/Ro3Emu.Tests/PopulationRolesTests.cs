using System.Text.Json;
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

/// <summary>Behavior roles on the synthetic flat-nav fixture used by PopulationTests (minimum copy).</summary>
public sealed class PopulationRolesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ro3-population-roles-" + Guid.NewGuid().ToString("N"));
    private readonly GameLoop loop = new(NullLogger<GameLoop>.Instance);
    private readonly RecordingSink sink = new();
    private readonly World world;
    private readonly PopulationService population;
    private readonly ServerOptions options;
    private readonly WorldData worldData;

    public PopulationRolesTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "nav"));
        File.WriteAllText(Path.Combine(root, "config", "MapConfig.json"), """{"1":{"_iMapResourceID":1}}""");
        File.WriteAllText(Path.Combine(root, "config", "MapResource.json"), """{"1":{"_kNavDataName":"test_dynamicmapdata.bytes"}}""");
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """{"1000":{"_iUnitB":1000,"_iUnitG":1001}}""");
        byte[] grid = new byte[64 + 256 * 256 * 4];
        BitConverter.GetBytes(256).CopyTo(grid, 20);
        BitConverter.GetBytes(256).CopyTo(grid, 24);
        BitConverter.GetBytes(256).CopyTo(grid, 28);
        BitConverter.GetBytes(64).CopyTo(grid, 48);
        for (int i = 64; i < grid.Length; i += 4) grid[i] = 0x80;
        File.WriteAllBytes(Path.Combine(root, "nav", "test_dynamicmapdata.bytes"), grid);
        options = new ServerOptions { DataDir = root, Population = new() { Enabled = true, PerMap = 6, MaxTotal = 10, MaxSpawnsPerTick = 100, Maps = new() { [1] = new() { AreaLevel = 5, MinLevel = 5, MaxLevel = 5 } } } };
        var wrapped = Options.Create(options);
        var data = new GameData(wrapped, NullLogger<GameData>.Instance);
        world = new World(sink, loop, NullLogger<World>.Instance);
        worldData = new WorldData(wrapped, data, NullLogger<WorldData>.Instance);
        population = new PopulationService(world, worldData, data, loop, wrapped);
    }

    private static GameSession Player(int id = 1) => new(null!, null!)
    {
        Role = new Role(id, "test", 1, "Player" + id, 1, 1000, 5, 0, 0, 0, 1, 128, 0, 128, 0),
        SceneEntry = 1, LineNumber = 1, Position = new Vec3 { X = 128, Z = 128 },
    };

    private static PopulationUnit Bot(PopulationRole role, long id = 1) => new()
        { Uid = (1L << 50) | id, Name = "Bot" + id, SceneEntry = 1, X = 128, Z = 128, Level = 5, Hp = 100, Behavior = role };

    private static WorldNpc Npc(WorldNpcKind kind, float x, float z, int index) =>
        new((1L << 40) | (uint)index, 1, kind, 100 + index, "npc" + index, 0, x, 0, z, 0, JsonDocument.Parse("{}").RootElement);

    private static int Share(int[] weights, params PopulationRole[] roles) =>
        Enumerable.Range(1, 2000).Count(seed => roles.Contains(PopulationRoles.Pick(weights, seed)));

    [Fact]
    public void RolesAreStablePerSeedWeightedByAreaAndConfigurationReplacesTheMix()
    {
        var p = options.Population;
        var city = PopulationRoles.Weights(p, "city"); var field = PopulationRoles.Weights(p, "field"); var remote = PopulationRoles.Weights(p, "remote");
        Assert.All(Enumerable.Range(1, 200), seed => Assert.Equal(PopulationRoles.Pick(city, seed), PopulationRoles.Pick(city, seed)));
        Assert.Equal(6, Enumerable.Range(1, 2000).Select(seed => PopulationRoles.Pick(city, seed)).Distinct().Count());
        Assert.InRange(Share(city, PopulationRole.Socializer, PopulationRole.Merchant, PopulationRole.Rester), 1300, 1500);
        Assert.InRange(Share(field, PopulationRole.Hunter, PopulationRole.Explorer), 1400, 1600);
        Assert.True(Share(remote, PopulationRole.Explorer) > 1000);
        Assert.Equal(0, Share(remote, PopulationRole.Socializer, PopulationRole.Merchant));
        Assert.Equal(field, PopulationRoles.Weights(p, "unknown-profile"));
        p.Profiles["field"].Roles = new() { ["Crafter"] = 3 };
        Assert.Equal(2000, Share(PopulationRoles.Weights(p, "field"), PopulationRole.Crafter)); // replaces, never merges
        p.Profiles["field"].Roles = new() { ["bard"] = 5, ["hunter"] = -4 };
        Assert.Equal(PopulationRoles.Defaults("field"), PopulationRoles.Weights(p, "field"));
        var bound = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Population:Roles:Enabled"] = "false", ["Population:Roles:ClusterSize"] = "2",
            ["Population:Profiles:town:Roles:merchant"] = "7",
        }).Build().Get<ServerOptions>()!;
        Assert.False(bound.Population.Roles.Enabled); Assert.Equal(2, bound.Population.Roles.ClusterSize);
        Assert.Equal(7, bound.Population.Profiles["town"].Roles!["merchant"]);
        Assert.Null(bound.Population.Profiles["city"].Roles);
        Assert.True(new ServerOptions().Population.Roles.Enabled);
    }

    [Fact]
    public async Task SpawnedBotsCarryRolesStatusReportsTheMapAndOffSwitchKeepsTheOriginalLoop()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bots = world.AllUnits().OfType<PopulationUnit>().ToArray();
        Assert.Equal(6, bots.Length); Assert.All(bots, b => Assert.NotEqual(PopulationRole.None, b.Behavior));
        string status = await population.RoleStatusAsync(player.RuntimeMap);
        Assert.StartsWith("Roles here: ", status); Assert.DoesNotContain('\n', status);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(status, @"[a-z]+ (\d+)").Where(m => !m.Value.StartsWith("groups"))
            .Sum(m => int.Parse(m.Groups[1].Value)));
        Assert.Equal("Roles here: none.", await population.RoleStatusAsync(RuntimeMapKey.Public(1, 2)));
        options.Population.Roles.Enabled = false;
        await population.SetCountAsync(0); await population.SetCountAsync(null);
        bots = world.AllUnits().OfType<PopulationUnit>().ToArray();
        Assert.Equal(6, bots.Length); Assert.All(bots, b => { Assert.Equal(PopulationRole.None, b.Behavior); Assert.False(b.RoleIdle); });
        var bot = bots[0]; bot.Stop(1000); bot.Route.Clear(); bot.JourneyActive = true;
        await population.UpdateAsync(1250);
        Assert.InRange(bot.ActivityUntilMs - 1250, 5000, 10000); // original arrival pause
        Assert.False(population.RoleJourney(bot, worldData.Map(1)!, 1250));
    }

    [Theory]
    [InlineData("merchant", 35000, 90000, true)]
    [InlineData("hunter", 2000, 9000, false)]
    public async Task RolesDriveHuntingWillingnessAndJitteredArrivalPauses(string role, int min, int max, bool idle)
    {
        options.Population.Profiles["field"].Roles = new() { [role] = 1 };
        await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        var bots = world.AllUnits().OfType<PopulationUnit>().ToArray();
        foreach (var bot in bots) { bot.Stop(1000); bot.Route.Clear(); bot.JourneyActive = true; }
        await population.UpdateAsync(1250);
        Assert.All(bots, b => { Assert.Equal(idle, b.RoleIdle); Assert.InRange(b.ActivityUntilMs - 1250, min, max); });
        Assert.True(bots.Select(b => b.ActivityUntilMs).Distinct().Count() > 1, "pauses must not be in lockstep");
    }

    [Fact]
    public async Task SocializersFormLooseFacingClustersWithoutStandingOnEachOther()
    {
        options.Population.PerMap = 3;
        options.Population.Maps[1].Profile = "town";
        options.Population.Profiles["town"].Roles = new() { ["socializer"] = 1 };
        await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        for (long now = 1250; now <= 26000; now += 250) await population.UpdateAsync(now);
        var bots = world.AllUnits().OfType<PopulationUnit>().ToArray();
        Assert.Equal(3, bots.Length);
        Assert.All(bots, b =>
        {
            var cluster = Assert.IsType<PopulationCluster>(b.Cluster);
            Assert.False(b.Moving); Assert.Empty(b.Route); Assert.False(b.JourneyActive);
            Assert.InRange(MonsterService.Distance(b.X, b.Z, cluster.Center.X, cluster.Center.Z), 1.6f, 2.4f);
            Assert.InRange(MathF.Abs(b.Face - MonsterService.Angle(b.X, b.Z, cluster.Center.X, cluster.Center.Z)), 0, 0.5f);
            Assert.Contains(sink.Moves, m => m.Uid == b.Uid && m.MovementInfo.MoveType == MovementType.MoveTypeNone && m.MovementInfo.Face == b.Face);
            Assert.InRange(cluster.Slots.Length, 2, 3);
        });
        Assert.Contains(bots, b => b.Cluster!.Count >= 2);
        foreach (var a in bots) foreach (var b in bots.Where(b => b != a && b.Cluster == a.Cluster))
            Assert.True(MonsterService.Distance(a.X, a.Z, b.X, b.Z) >= 1.5f);
        string status = await population.RoleStatusAsync(bots[0].RuntimeMap);
        Assert.Contains("socializer 3", status); Assert.Matches(@"social groups [12]\.", status);
        var leaver = bots[0]; var old = leaver.Cluster!;
        population.RoleJourney(leaver, worldData.Map(1)!, 26000);
        Assert.DoesNotContain(leaver, old.Slots);
        await world.RemoveUnitAsync(bots[1]); await world.RemoveUnitAsync(bots[2]);
        await population.RoleStatusAsync(bots[0].RuntimeMap);
        Assert.All(bots.Skip(1), b => Assert.Null(b.Cluster)); // removal releases slots
    }

    [Fact]
    public void ClustersAreTownOnlyAndHaveAnOffSwitch()
    {
        var map = worldData.Map(1)!;
        var field = Bot(PopulationRole.Socializer);
        Assert.True(population.RoleJourney(field, map, 0)); Assert.Null(field.Cluster); Assert.NotEmpty(field.Route);
        options.Population.Maps[1].Profile = "town";
        var town = Bot(PopulationRole.Socializer, 2);
        Assert.True(population.RoleJourney(town, map, 0)); Assert.NotNull(town.Cluster); Assert.NotNull(town.LookAt);
        options.Population.Roles.SocialClusters = false;
        var off = Bot(PopulationRole.Socializer, 3);
        Assert.True(population.RoleJourney(off, map, 0)); Assert.Null(off.Cluster);
        Assert.False(population.RoleJourney(Bot(PopulationRole.Hunter, 4), map, 0)); // hunters keep the original itinerary
    }

    [Fact]
    public void MerchantsAndCraftersStandBesideLandmarksClearOfPortalsAndFootholds()
    {
        var nav = worldData.Map(1)!.Nav!;
        var kafra = Npc(WorldNpcKind.Kafra, 150, 128, 1); var portal = Npc(WorldNpcKind.Portal, 128, 150, 2);
        var gather = new GatheringSpawnPoint(1, 1, 1, new MapPoint(110, 0, 110, 0), [(1, 1)], 1, 1, 0);
        var map = new MapWorld { SceneEntry = 1, Nav = nav, Npcs = [kafra, portal, Npc(WorldNpcKind.Npc, 100, 150, 3)], GatheringPoints = [gather] };
        for (int i = 0; i < 20; i++)
        {
            var merchant = Bot(PopulationRole.Merchant);
            Assert.True(population.RoleJourney(merchant, map, 0));
            var goal = merchant.Route.Last(); var look = merchant.LookAt!.Value;
            Assert.InRange(MonsterService.Distance(goal.X, goal.Z, kafra.X, kafra.Z), 2.9f, 6.1f); // Kafra/function NPC first
            Assert.True((look.X - goal.X) * (goal.X - kafra.X) + (look.Z - goal.Z) * (goal.Z - kafra.Z) > 0); // faces passers-by
            Assert.True(MonsterService.Distance(goal.X, goal.Z, portal.X, portal.Z) > 6);
            var crafter = Bot(PopulationRole.Crafter);
            Assert.True(population.RoleJourney(crafter, map, 0));
            goal = crafter.Route.Last();
            Assert.InRange(MonsterService.Distance(goal.X, goal.Z, gather.Position.X, gather.Position.Z), 1.4f, 2.6f);
            Assert.Equal(gather.Position, crafter.LookAt);
            var explorer = Bot(PopulationRole.Explorer);
            Assert.True(population.RoleJourney(explorer, map, 0));
            Assert.All(explorer.Route, p => Assert.True(nav.IsWalkable(p.X, p.Y, p.Z)));
        }
    }

    [Fact]
    public async Task RoleChatUsesExistingBudgetsWithRoleCooldownsAndReplaceablePacks()
    {
        Assert.True(PopulationRoles.ChatScale(PopulationRole.Socializer) < 1 && PopulationRoles.ChatScale(PopulationRole.Hunter) > 1);
        Assert.Equal(1, PopulationRoles.ChatScale(PopulationRole.None));
        foreach (var key in new[] { "explorer", "socializer", "crafter", "merchant" })
            Assert.All(PopulationChatOptions.DefaultPhrases[key], line => Assert.InRange(line.Length, 1, 180));
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bot = world.AllUnits().OfType<PopulationUnit>().First();
        player.Position = bot.Pos; player.ChatGroups.Add("4_1");
        bot.IdleChatContext = "merchant"; bot.ActivityUntilMs = long.MaxValue; bot.Behavior = PopulationRole.Socializer;
        var chatter = new PopulationChatter(new() { GreetingsEnabled = false }); // quiet: 180-270s per bot
        Assert.Empty(chatter.Plan([bot], _ => "town", 1000));
        var line = Assert.Single(chatter.Plan([bot], _ => "town", 51001));
        Assert.Contains(line.Text, PopulationChatOptions.DefaultPhrases["merchant"]);
        Assert.InRange(bot.NextChatMs - 51001, 108000, 162000);
        var custom = new PopulationChatOptions { GreetingsEnabled = false };
        custom.Phrases["merchant"] = ["Custom stall line."];
        chatter = new PopulationChatter(custom); bot.Behavior = PopulationRole.Hunter; bot.NextChatMs = 0;
        chatter.Plan([bot], _ => "town", 1000);
        Assert.Equal("Custom stall line.", Assert.Single(chatter.Plan([bot], _ => "town", 51001)).Text);
        Assert.InRange(bot.NextChatMs - 51001, 288000, 432000);
        custom.Phrases["merchant"] = [];
        Assert.Empty(chatter.Plan([bot], _ => "town", 2_000_000)); // an emptied role pack stays quiet, like other packs
    }

    public void Dispose() { loop.Dispose(); Directory.Delete(root, true); }

    private sealed class RecordingSink : IAoiSink
    {
        public List<MovementChangeNtf> Moves { get; } = new();
        public Task EnterAsync(GameSession v, GameSession s) => Task.CompletedTask;
        public Task MoveAsync(GameSession v, GameSession s, WalkReq w) => Task.CompletedTask;
        public Task TurnAsync(GameSession v, GameSession s, float f) => Task.CompletedTask;
        public Task LeaveAsync(GameSession v, GameSession s) => Task.CompletedTask;
        public Task UnitsChangedAsync(GameSession v, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left) => Task.CompletedTask;
        public Task UnitMoveAsync(GameSession v, MovementChangeNtf movement) { Moves.Add(movement); return Task.CompletedTask; }
    }
}
