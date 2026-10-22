using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
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
        options = new ServerOptions { DataDir = root, Population = new() { Enabled = true, PerMap = 6, MaxTotal = 10, MaxSpawnsPerTick = 100, Maps = new() { [1] = new() { AreaLevel = 5, MinLevel = 5, MaxLevel = 5 } } } };
        var wrapped = Options.Create(options);
        data = new GameData(wrapped, NullLogger<GameData>.Instance);
        world = new World(sink, loop, NullLogger<World>.Instance);
        worldData = new WorldData(wrapped, data, NullLogger<WorldData>.Instance);
        population = new PopulationService(world, worldData, data, loop, wrapped);
    }

    [Fact]
    public async Task BotChatOnlyReachesNearbyVisibleReadyMapSubscribersAndHonorsMute()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bot = world.AllUnits().OfType<PopulationUnit>().First();
        player.Position = bot.Pos;
        Assert.Null(ChatService.PopulationChannel(bot, player));
        player.ChatGroups.Add("4_1"); player.ChatGroups.Add("@TGS#_4_1");
        Assert.Equal("4_1", ChatService.PopulationChannel(bot, player));
        player.BotChatMuted = true; Assert.Null(ChatService.PopulationChannel(bot, player)); player.BotChatMuted = false;
        player.SceneLoading = true; Assert.Null(ChatService.PopulationChannel(bot, player)); player.SceneLoading = false;
        player.LineNumber = 2; Assert.Null(ChatService.PopulationChannel(bot, player)); player.LineNumber = 1;
        player.Position = new Vec3 { X = bot.X + 31, Y = bot.Y, Z = bot.Z };
        Assert.Null(ChatService.PopulationChannel(bot, player)); player.Position = bot.Pos;
        bot.Viewers.Remove(player); Assert.Null(ChatService.PopulationChannel(bot, player)); bot.Viewers.Add(player);
        bot.Hp = 0; Assert.Null(ChatService.PopulationChannel(bot, player)); bot.Hp = bot.MaxHp;
        bot.Removed = true; Assert.Null(ChatService.PopulationChannel(bot, player));
    }

    [Fact]
    public async Task BotChatPublisherHonorsBlacklistAndDoesNotReplayNearbyLinesAsHistory()
    {
        using var accounts = new AccountStore(Options.Create(options));
        var friends = new FriendStore(accounts);
        var chat = new ChatService(null!, null!, null!, friends, new CharacterService(null!, null!, null!, world, NullLogger<CharacterService>.Instance), null!, null!, Options.Create(options), NullLogger<ChatService>.Instance);
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bot = world.AllUnits().OfType<PopulationUnit>().First();
        player.Position = bot.Pos; player.ChatGroups.Add("4_1");
        // Seed a stored preference directly in this temporary fixture: ephemeral bot IDs intentionally
        // have no durable role lifecycle, so the normal player-to-player friendship write API is inapplicable.
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={accounts.DatabasePath}");
        db.Open();
        using var preference = db.CreateCommand();
        preference.CommandText = "INSERT INTO blacklist(owner,other) VALUES($owner,$other)";
        preference.Parameters.AddWithValue("$owner", player.Role!.Id);
        preference.Parameters.AddWithValue("$other", bot.Uid);
        preference.ExecuteNonQuery();
        Assert.True(friends.Blocks(player.Role.Id, bot.Uid));
        // These sessions have no network channel: a forbidden send would fail instead of silently passing.
        await chat.PublishPopulationAsync(bot, "Taking a breather.");
        preference.CommandText = "DELETE FROM blacklist WHERE owner=$owner AND other=$other";
        preference.ExecuteNonQuery(); player.BotChatMuted = true;
        await chat.PublishPopulationAsync(bot, "Taking a breather.");
        player.BotChatMuted = false; player.ChatGroups.Clear();
        await chat.PublishPopulationAsync(bot, "Taking a breather.");
        var history = typeof(ChatService).GetField("_recent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Empty((System.Collections.IDictionary)history.GetValue(chat)!);
    }

    [Fact]
    public async Task BotChatterDelaysArrivalBoundsCadenceAndStopsForCombatDeathOrMute()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bot = world.AllUnits().OfType<PopulationUnit>().First();
        player.ChatGroups.Add("4_1"); player.Position = bot.Pos;
        var chatter = new PopulationChatter(new());
        Assert.Empty(chatter.Plan([bot], _ => "field", 1000));
        var line = Assert.Single(chatter.Plan([bot], _ => "field", 51001));
        Assert.Same(bot, line.Bot); Assert.False(string.IsNullOrWhiteSpace(line.Text));
        Assert.Empty(chatter.Plan([bot], _ => "field", 51002));
        Assert.Empty(chatter.Plan([bot], _ => "field", 150000)); // per-bot quiet cooldown even after map cooldown
        player.BotChatMuted = true; Assert.Empty(chatter.Plan([bot], _ => "field", 1000000)); player.BotChatMuted = false;
        bot.Hp = 0; Assert.Empty(chatter.Plan([bot], _ => "field", 1000000)); bot.Hp = bot.MaxHp;
        bot.Target = Monster(); Assert.Empty(chatter.Plan([bot], _ => "field", 1000000)); bot.Target = null;
        chatter.Mode = "off"; Assert.Empty(chatter.Plan([bot], _ => "field", 1000000));
        chatter.Mode = "invalid"; Assert.Empty(chatter.Plan([bot], _ => "field", 1000000));
        chatter.Plan([], _ => "field", 1000001); Assert.Equal(0, chatter.ActiveMaps);
    }

    [Fact]
    public async Task BotChatterUsesEditableContextPacksAndAvoidsRecentRepetition()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bot = world.AllUnits().OfType<PopulationUnit>().First(); bot.VisitingNpc = false;
        player.Position = bot.Pos; player.ChatGroups.Add("4_1");
        var options = new PopulationChatOptions { Mode = "chatty", GreetingsEnabled = false };
        options.Phrases["hunt"] = Enumerable.Range(0, 12).Select(i => "Custom hunt line " + i).ToArray();
        var chatter = new PopulationChatter(options);
        Assert.Empty(chatter.Plan([bot], _ => "field", 0));
        var recent = new Queue<string>();
        for (long time = 1000000; time <= 25000000; time += 1000000)
        {
            string text = Assert.Single(chatter.Plan([bot], _ => "field", time)).Text;
            Assert.StartsWith("Custom hunt line ", text); Assert.DoesNotContain(text, recent);
            recent.Enqueue(text); if (recent.Count > 8) recent.Dequeue();
        }
        options.Phrases["hunt"] = ["", "bad\nline", new string('a', 181)];
        Assert.Empty(chatter.Plan([bot], _ => "field", 30000000));
        Assert.Equal("town", PopulationChatter.Context(bot, "city"));
        bot.WalkTo(new MapPoint(bot.X + 10, bot.Y, bot.Z, 0), 40000000);
        Assert.Equal("travel", PopulationChatter.Context(bot, "city"));
    }

    [Fact]
    public void BotPhraseConfigurationReplacesBuiltInPacksRatherThanAppending()
    {
        var configured = new ServerOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ro3Emu:Population:Chat:Mode"] = "normal",
            ["Ro3Emu:Population:Chat:Phrases:travel:0"] = "Custom journey.",
        }).Build().GetSection("Ro3Emu").Bind(configured);
        Assert.Equal("normal", configured.Population.Chat.Mode);
        Assert.Equal("Custom journey.", Assert.Single(configured.Population.Chat.Phrases["travel"]));
        Assert.False(configured.Population.Chat.Phrases.ContainsKey("town"));
        Assert.True(PopulationChatOptions.DefaultPhrases.ContainsKey("town"));
        var bot = Fighter(); bot.IdleChatContext = "npc"; bot.ActivityUntilMs = 100;
        Assert.Equal("npc", PopulationChatter.Context(bot, "field", 50));
        Assert.Equal("hunt", PopulationChatter.Context(bot, "field", 101));
    }

    [Fact]
    public async Task BotChatterSharesMapBudgetAndBoundsServerOutputAcrossLines()
    {
        var players = new[] { Player(), Player(2, line: 2), Player(3, line: 3) };
        foreach (var player in players) { player.ChatGroups.Add("4_1"); await world.JoinAsync(player); }
        await population.UpdateAsync(1000);
        var bots = world.AllUnits().OfType<PopulationUnit>().ToArray();
        foreach (var player in players) player.Position = bots.First(b => b.RuntimeMap == player.RuntimeMap).Pos;
        var chatter = new PopulationChatter(new());
        Assert.Empty(chatter.Plan(bots, _ => "field", 1000));
        var lines = chatter.Plan(bots, _ => "field", 51001);
        Assert.Equal(2, lines.Count); Assert.Equal(2, lines.Select(l => l.Bot.RuntimeMap).Distinct().Count());
        var rest = Assert.Single(chatter.Plan(bots, _ => "field", 51002));
        Assert.DoesNotContain(rest.Bot.RuntimeMap, lines.Select(l => l.Bot.RuntimeMap));
        Assert.Empty(chatter.Plan(bots, _ => "field", 51003));
    }

    [Fact]
    public async Task BotGreetingsProduceOneNamedReactionAcrossCrowdAndShareChatBudgets()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bots = world.AllUnits().OfType<PopulationUnit>().ToArray();
        foreach (var bot in bots) { bot.X = 128; bot.Z = 128; }
        player.ChatGroups.Add("4_1");
        var options = new PopulationChatOptions { Mode = "chatty" };
        options.Phrases["greeting"] = ["Welcome, {player}!"];
        var chatter = new PopulationChatter(options);
        Assert.Empty(chatter.Plan(bots, _ => "city", 0));
        IReadOnlyList<PopulationChatter.Line> lines = [];
        for (long now = 1000; now <= 51000; now += 1000)
        {
            var batch = chatter.Plan(bots, _ => "city", now);
            if (batch.Any(l => l.Subject != null)) lines = batch;
        }
        var greeting = Assert.Single(lines);
        Assert.Equal("Welcome, Player1!", greeting.Text); Assert.Same(player, greeting.Subject);
        Assert.Equal(player.SceneGeneration, greeting.SubjectGeneration); Assert.Equal(player.Role!.Id, greeting.SubjectRole);
        Assert.Empty(chatter.Plan(bots, _ => "city", 51001)); // map budget also covers reactions
        for (long now = 52000; now <= 150000; now += 1000)
            Assert.DoesNotContain(chatter.Plan(bots, _ => "city", now), line => line.Subject != null);
    }

    [Fact]
    public void BotGreetingCooldownSurvivesOtherBotsMapChangesAndShortAbsences()
    {
        var bot = Fighter(); var player = Player(); player.Position = bot.Pos;
        var reactions = new PopulationReactions(new() { GreetingCooldownSeconds = 600 });
        var audience = new Dictionary<PopulationUnit, GameSession[]> { [bot] = [player] };
        reactions.Observe(audience, 0, true);
        for (long now = 1000; now <= 5000; now += 1000) reactions.Observe(audience, now, true);
        var greeting = reactions.Find(bot, [player], 5000)!; Assert.NotNull(greeting);
        Assert.Equal("greeting", greeting.Context); reactions.Consume(greeting, 5000);
        reactions.Observe(new Dictionary<PopulationUnit, GameSession[]>(), 6000, true);
        var other = Fighter(); other.SceneEntry = 2; player.SceneEntry = 2;
        audience = new() { [other] = [player] };
        for (long now = 7000; now <= 20000; now += 1000) reactions.Observe(audience, now, true);
        Assert.Null(reactions.Find(other, [player], 20000));
        reactions.Observe(new Dictionary<PopulationUnit, GameSession[]>(), 606000, true);
        for (long now = 607000; now <= 612000; now += 1000) reactions.Observe(audience, now, true);
        Assert.Equal("greeting", reactions.Find(other, [player], 612000)!.Context);
        reactions.Clear(); Assert.Equal(0, reactions.Count);
    }

    [Fact]
    public void BotLevelReactionsObserveActualNearbyGainsAndLimitRapidLeveling()
    {
        var bot = Fighter(); var player = Player(); player.Position = bot.Pos;
        player.Progress = new RoleProgress { Level = 5 };
        var options = new PopulationChatOptions { GreetingsEnabled = false };
        var reactions = new PopulationReactions(options);
        var audience = new Dictionary<PopulationUnit, GameSession[]> { [bot] = [player] };
        reactions.Observe(audience, 0, true);
        Assert.Null(reactions.Find(bot, [player], 0)); // initial snapshot is not a level-up
        player.Progress.Level = 6; reactions.Observe(audience, 1000, true);
        for (long now = 2000; now <= 6000; now += 1000) reactions.Observe(audience, now, true);
        var reaction = reactions.Find(bot, [player], 6000)!;
        Assert.Equal("levelup", reaction.Context);
        Assert.Equal("Player1 reached 6", PopulationReactions.Expand("{player} reached {level}", player));
        reactions.Consume(reaction, 6000);
        player.Progress.Level = 7; reactions.Observe(audience, 7000, true);
        Assert.Null(reactions.Find(bot, [player], 13000)); // no queued chorus for consecutive gains
        reactions.Observe(audience, 126000, true); player.Progress.Level = 8;
        reactions.Observe(audience, 127000, true);
        for (long now = 128000; now <= 132000; now += 1000) reactions.Observe(audience, now, true);
        Assert.Equal("levelup", reactions.Find(bot, [player], 132000)!.Context);
    }

    [Fact]
    public void BotReactionsDropAbsentDisabledDeadFarawayAndExpiredEvents()
    {
        var bot = Fighter(); var player = Player(); player.Position = bot.Pos;
        player.Progress = new RoleProgress { Level = 5 };
        var options = new PopulationChatOptions { GreetingsEnabled = false };
        var reactions = new PopulationReactions(options);
        var audience = new Dictionary<PopulationUnit, GameSession[]> { [bot] = [player] };
        reactions.Observe(audience, 0, false); player.Progress.Level = 6;
        reactions.Observe(audience, 1000, false); reactions.Observe(audience, 2000, true);
        Assert.Null(reactions.Find(bot, [player], 10000)); // enabling does not replay hidden gains
        reactions.Observe(new Dictionary<PopulationUnit, GameSession[]>(), 3000, true);
        player.Progress.Level = 7; reactions.Observe(audience, 4000, true);
        Assert.Null(reactions.Find(bot, [player], 10000)); // returning/unmuting establishes a fresh baseline
        player.Progress.Level = 8; reactions.Observe(audience, 5000, true);
        player.Dead = true; Assert.Null(reactions.Find(bot, [player], 11000)); player.Dead = false;
        player.Position = new Vec3 { X = bot.X + 13, Z = bot.Z };
        Assert.Null(reactions.Find(bot, [player], 11000)); player.Position = bot.Pos;
        Assert.Null(reactions.Find(bot, [player], 96000)); // stale pending event expires
        options.LevelUpReactionsEnabled = false; reactions.Observe(audience, 6000, true);
        Assert.Null(reactions.Find(bot, [player], 11000));
        reactions.Observe(new Dictionary<PopulationUnit, GameSession[]>(), 1000000, true);
        Assert.Equal(0, reactions.Count);
    }

    [Fact]
    public async Task BotReactionDeliveryRejectsSubjectHandoffsGenerationChangesAndMarkupNames()
    {
        var player = Player(); await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bot = world.AllUnits().OfType<PopulationUnit>().First();
        player.Position = bot.Pos; player.ChatGroups.Add("4_1");
        Assert.True(ChatService.PopulationReactionStillValid(bot, player, 0, player.Role!.Id));
        player.SceneGeneration++;
        Assert.False(ChatService.PopulationReactionStillValid(bot, player, 0, player.Role.Id));
        Assert.False(ChatService.PopulationReactionStillValid(bot, player, 1, 999));
        player.SceneLoading = true; Assert.False(ChatService.PopulationReactionStillValid(bot, player, 1, player.Role.Id));
        player.SceneLoading = false; player.BotChatMuted = true;
        Assert.False(ChatService.PopulationReactionStillValid(bot, player, 1, player.Role.Id));
        player.Role = player.Role with { Name = "<b>Hero</b>\n" };
        string text = PopulationReactions.Expand("Hello {player}!", player);
        Assert.DoesNotContain("<", text); Assert.DoesNotContain(">", text); Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void BotQueuedLevelReactionsRejectSceneGenerationChangesAndLevelDrops()
    {
        var bot = Fighter(); var player = Player(); player.Position = bot.Pos;
        player.Progress = new RoleProgress { Level = 5 };
        var reactions = new PopulationReactions(new() { GreetingsEnabled = false });
        var audience = new Dictionary<PopulationUnit, GameSession[]> { [bot] = [player] };
        reactions.Observe(audience, 0, true);
        player.Progress.Level = 6; reactions.Observe(audience, 1000, true);
        player.SceneGeneration++;
        Assert.Null(reactions.Find(bot, [player], 6000));
        reactions.Observe(audience, 2000, true); Assert.Null(reactions.Find(bot, [player], 7000));
        player.Progress.Level = 7; reactions.Observe(audience, 3000, true);
        player.Progress.Level = 6; reactions.Observe(audience, 4000, true);
        Assert.Null(reactions.Find(bot, [player], 9000));
    }

    [Fact]
    public void BotReactionObservationsHaveABoundedLifetimeAndCapacity()
    {
        var bot = Fighter();
        var players = Enumerable.Range(1, 2050).Select(id => { var p = Player(id); p.Position = bot.Pos; return p; }).ToArray();
        var reactions = new PopulationReactions(new());
        reactions.Observe(new Dictionary<PopulationUnit, GameSession[]> { [bot] = players }, 0, true);
        Assert.Equal(2048, reactions.Count);
        reactions.Observe(new Dictionary<PopulationUnit, GameSession[]>(), 1000000, true);
        Assert.Equal(0, reactions.Count);
    }

    [Fact]
    public async Task BotLevelCongratulationsUseNormalChatPlannerAndEmptyGreetingPacksAllowAmbientLines()
    {
        var player = Player(); player.Progress = new RoleProgress { Level = 5 };
        await world.JoinAsync(player); await population.UpdateAsync(1000);
        var bots = world.AllUnits().OfType<PopulationUnit>().ToArray();
        foreach (var bot in bots) { bot.X = 128; bot.Z = 128; }
        player.ChatGroups.Add("4_1");
        var options = new PopulationChatOptions { Mode = "chatty" };
        options.Phrases["greeting"] = [];
        options.Phrases["levelup"] = ["Level {level}, {player}!"];
        var chatter = new PopulationChatter(options);
        int ambient = 0;
        for (long now = 0; now <= 51000; now += 1000)
        {
            var lines = chatter.Plan(bots, _ => "field", now);
            Assert.DoesNotContain(lines, line => line.Subject != null); ambient += lines.Count;
        }
        Assert.True(ambient > 0);
        player.Progress.Level = 6;
        var reactions = new List<PopulationChatter.Line>();
        for (long now = 52000; now <= 150000; now += 1000)
            reactions.AddRange(chatter.Plan(bots, _ => "field", now).Where(l => l.Subject != null));
        var congratulation = Assert.Single(reactions);
        Assert.Equal("Level 6, Player1!", congratulation.Text); Assert.Equal(6, congratulation.SubjectLevel);
        var botSender = congratulation.Bot;
        Assert.True(ChatService.PopulationReactionStillValid(botSender, player, player.SceneGeneration, player.Role!.Id, 6));
        player.Progress.Level = 5;
        Assert.False(ChatService.PopulationReactionStillValid(botSender, player, player.SceneGeneration, player.Role.Id, 6));
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
        Assert.Equal(5, a.VisibleUnits.Count); Assert.Equal(5, b.VisibleUnits.Count);
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
        Assert.Equal(bot.Career, info.Career); Assert.False(info.CantBeInvited); // companions are on by default: Team Up is offered
        await population.SetCountAsync(0); Assert.Null(population.Profile(bot.Uid, 124, 7));
    }

    [Fact]
    public async Task RealUserDataServiceReturnsBotProfilesWithoutDatabaseReads()
    {
        await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        var bot = (PopulationUnit)world.AllUnits().First();
        var service = new UserDataService(null!, null!, loop, null!, null!, null!, null!, TimeProvider.System, Options.Create(options),
            NullLogger<UserDataService>.Instance, population);
        var args = new Dictionary<object, object?>
        {
            ["UidFieldVersions"] = new Dictionary<object, object?>
            { [bot.Uid.ToString()] = new Dictionary<object, object?> { ["Name"] = 0L, ["Base"] = 0L } }
        };
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

    [Fact]
    public void ProfilesResolveCountsOverridesBoundsAndDisabledMaps()
    {
        options.Population.UseMapProfiles = true;
        options.Population.MaxTotal = 100;
        options.Population.Maps[1].Profile = "city";
        Assert.Equal(50, population.Settings(1).Count);
        options.Population.Maps[1].Count = 17;
        Assert.Equal(17, population.Settings(1).Count);
        options.Population.Maps[1].Enabled = false;
        Assert.Equal(0, population.Settings(1).Count);
        options.Population.Maps[1] = new() { Profile = "remote", AreaLevel = 1, MinLevel = 80, MaxLevel = 100 };
        var remote = population.Settings(1);
        Assert.Equal(3, remote.Count); Assert.InRange(remote.MaxLevel, 1, 3);
        Assert.True(remote.MinLevel <= remote.MaxLevel); // A minimum cannot raise an area's difficulty ceiling.
    }

    [Fact]
    public async Task RuntimeMapControlsAndGlobalOffRespectConfiguredProfiles()
    {
        options.Population.UseMapProfiles = true;
        options.Population.Profiles["field"].Count = 4;
        await world.JoinAsync(Player());
        await population.EnableConfiguredAsync(); Assert.Equal(4, population.Count);
        await population.SetMapCountAsync(1, 2); Assert.Equal(2, population.Count);
        await population.SetCountAsync(0); Assert.Equal(0, population.Count);
        await population.EnableConfiguredAsync(); Assert.Equal(2, population.Count);
        await population.SetMapCountAsync(1, null); Assert.Equal(4, population.Count);
        await population.SetMapCountAsync(1, 0); Assert.Equal(0, population.Count);
        await population.SetCountAsync(null); Assert.Equal(4, population.Count);
    }

    [Fact]
    public async Task SpawnsAreGradualAndHighLevelVisitorsDoNotRaiseBeginnerBots()
    {
        options.Population.MaxSpawnsPerTick = 2;
        options.Population.Maps[1] = new() { AreaLevel = 1, MinLevel = 1, MaxLevel = 1 };
        var player = Player(); player.Role = player.Role! with { Level = 99 };
        await world.JoinAsync(player); await population.UpdateAsync(1000);
        Assert.Equal(2, population.Count);
        await population.UpdateAsync(1250); Assert.Equal(4, population.Count);
        await population.UpdateAsync(1500); Assert.Equal(6, population.Count);
        Assert.All(world.AllUnits().OfType<PopulationUnit>(), b => { Assert.Equal(1, b.Level); Assert.Equal(1000, b.Career); });
    }

    [Fact]
    public void BudgetIsSharedAcrossLargeAndSmallMaps()
    {
        var city = RuntimeMapKey.Public(1); var field = RuntimeMapKey.Public(2); var remote = RuntimeMapKey.Public(3);
        var result = PopulationService.AllocateBudget(new Dictionary<RuntimeMapKey, int> { [city] = 50, [field] = 12, [remote] = 3 }, 20);
        Assert.Equal(3, result[remote]); Assert.InRange(result[city], 8, 9); Assert.InRange(result[field], 8, 9);
        Assert.Equal(20, result.Values.Sum());
    }

    [Fact]
    public void AreaPresetsUseMetadataAndConfigurationBindsMapOverrides()
    {
        File.WriteAllText(Path.Combine(root, "config", "MapConfig.json"), """{"1":{"_iSceneType":0,"_kWorldMapIcon":"worldmap_icon_city_test.png"},"2":{"_iSceneType":0},"3":{"_iSceneType":1}}""");
        options.Population.UseMapProfiles = true; options.Population.MaxTotal = 100;
        Assert.Equal(50, population.Settings(1).Count); Assert.Equal(30, population.Settings(2).Count);
        Assert.Equal(12, population.Settings(3).Count);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Population:Enabled"] = "true", ["Population:UseMapProfiles"] = "true",
            ["Population:Maps:10111:Count"] = "3", ["Population:Maps:10111:AreaLevel"] = "1",
            ["Population:Profiles:city:Count"] = "60"
        }).Build();
        var bound = Microsoft.Extensions.Configuration.ConfigurationBinder.Get<ServerOptions>(config)!;
        Assert.Equal(3, bound.Population.Maps[10111].Count); Assert.Equal(1, bound.Population.Maps[10111].AreaLevel);
        Assert.Equal(60, bound.Population.Profiles["city"].Count);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(19, 2)]
    [InlineData(20, 3)]
    [InlineData(40, 4)]
    [InlineData(60, 5)]
    [InlineData(80, 6)]
    public void EquipmentRarityTracksStrengthTier(int level, int quality) => Assert.Equal(quality, PopulationLoadouts.QualityForLevel(level));

    private void WriteLoadoutConfig()
    {
        File.WriteAllText(Path.Combine(root, "config", "ItemConfig.json"), """
        {
          "501":{"_iID":501,"_iType":5,"_iEquipId":501,"_iEquipPart":2,"_iLevelNeed":1,"_iQuality":2,"_kJobNeed":[1000]},
          "502":{"_iID":502,"_iType":5,"_iEquipId":502,"_iEquipPart":2,"_iLevelNeed":80,"_iQuality":6,"_kJobNeed":[1000]},
          "503":{"_iID":503,"_iType":5,"_iEquipId":503,"_iEquipPart":2,"_iLevelNeed":1,"_iQuality":6,"_kJobNeed":[1000]},
          "504":{"_iID":504,"_iType":5,"_iEquipId":504,"_iEquipPart":2,"_iLevelNeed":1,"_iQuality":2,"_kJobNeed":[1100]},
          "505":{"_iID":505,"_iType":5,"_iEquipId":505,"_iEquipPart":2,"_iLevelNeed":1,"_iQuality":2,"_kJobNeed":[1000]},
          "506":{"_iID":506,"_iType":5,"_iEquipId":506,"_iEquipPart":1,"_iLevelNeed":1,"_iQuality":2,"_kJobNeed":[3100]},
          "507":{"_iID":507,"_iType":5,"_iEquipId":507,"_iEquipPart":3,"_iLevelNeed":1,"_iQuality":2},
          "90000102":{"_iID":90000102,"_iType":5,"_iEquipId":501,"_iEquipPart":2,"_iLevelNeed":1,"_iQuality":2,"_kJobNeed":[1000]}
        }
        """);
        File.WriteAllText(Path.Combine(root, "config", "EquipConfig.json"), """
        {
          "501":{"_kBasicAttribute":[[103,7],[104,3]]}, "502":{"_kBasicAttribute":[[103,60],[101,500]]},
          "503":{"_kBasicAttribute":[[103,100]]}, "504":{"_kBasicAttribute":[[103,7]]},
          "505":{"_kBasicAttribute":[[101,1000000]]}, "506":{"_kBasicAttribute":[[103,12]]},
          "507":{"_kBasicAttribute":[[101,20]]}
        }
        """);
        File.WriteAllText(Path.Combine(root, "config", "WardRobeConfig.json"), """
        {
          "7001":{"_iID":7001,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"hair1"},
          "7002":{"_iID":7002,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_kModelResourceB":"hair2"},
          "7003":{"_iID":7003,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":2,"_kModelResourceG":"hair3"},
          "7004":{"_iID":7004,"_iType":7,"_iDefaultLightUp":1,"_iSexNeed":1,"_iDisable":1,"_kModelResourceB":"bad"},
          "1001":{"_iID":1001,"_iType":1,"_iQuality":6,"_iLuxury":1,"_kModelResourceB":"crown"},
          "1002":{"_iID":1002,"_iType":5,"_iQuality":2,"_kModelResourceB":"wings"}
        }
        """);
    }

    [Fact]
    public void BeginnerLoadoutsExcludeLegendariesDebugItemsWrongJobsAndOverpoweredStats()
    {
        WriteLoadoutConfig(); var loadouts = new PopulationLoadouts(data);
        var bot = new PopulationUnit { Uid = 1, Name = "Beginner", Level = 1, Gender = 1, Career = 1000 };
        loadouts.Apply(bot, 6, 123);
        Assert.Equal(501, bot.EquipmentLook[0]); Assert.Equal(23, bot.Attack); Assert.Equal(19, bot.MagicAttack);
        Assert.DoesNotContain(90000102L, bot.EquipmentLook); Assert.DoesNotContain(505L, bot.EquipmentLook);
        Assert.True(bot.Appearances.ContainsKey(7)); Assert.False(bot.Appearances.ContainsKey(1)); Assert.False(bot.Appearances.ContainsKey(5));
        Assert.Equal(bot.EquipmentLook, bot.Entity().EquipInfo.ToArray());
        Assert.Equal(bot.Attack, bot.Entity().Property.Single(p => p.Idx == 103).Data);
    }

    [Fact]
    public void StrongEquipmentRequiresLevelAndAreaPermissionAndChangesActualStats()
    {
        WriteLoadoutConfig(); var loadouts = new PopulationLoadouts(data);
        var strong = new PopulationUnit { Uid = 1, Name = "Veteran", Level = 80, Gender = 1, Career = 1000 };
        loadouts.Apply(strong, 6, 42);
        Assert.Equal(502, strong.EquipmentLook[0]); Assert.Equal(392, strong.Attack);
        Assert.Equal(4720, strong.MaxHp); // baseline 4200 + weapon 500 + offhand 20
        loadouts.Apply(strong, 2, 42);
        Assert.Equal(501, strong.EquipmentLook[0]); Assert.Equal(339, strong.Attack);
        Assert.False(strong.Appearances.ContainsKey(1));
    }

    [Fact]
    public void AppearanceSeedIsStableGenderCompatibleAndTwoHandedWeaponsExcludeOffhand()
    {
        WriteLoadoutConfig(); var loadouts = new PopulationLoadouts(data);
        var archer = new PopulationUnit { Uid = 1, Name = "Archer", Level = 15, Gender = 1, Career = 3100 };
        loadouts.Apply(archer, 6, 12);
        Assert.Equal(506, archer.EquipmentLook[0]); Assert.Equal(0, archer.EquipmentLook[1]);
        var before = archer.Entity(); loadouts.Apply(archer, 6, 12);
        Assert.Equal(before.CurUseAppearances, archer.Entity().CurUseAppearances);
        var seen = new HashSet<int>();
        for (int seed = 0; seed < 20; seed++) { loadouts.Apply(archer, 6, seed); seen.Add(archer.Appearances[7]); }
        Assert.Equal(2, seen.Count);
        var female = new PopulationUnit { Uid = 2, Name = "Female", Level = 1, Gender = 2 };
        loadouts.Apply(female, 2, 123); Assert.Equal(7003, female.Appearances[7]);
    }

    [Fact]
    public async Task AppearanceProfileAndWorldEntityUseTheSameStableLook()
    {
        WriteLoadoutConfig(); await world.JoinAsync(Player()); await population.UpdateAsync(1000);
        foreach (var bot in world.AllUnits().OfType<PopulationUnit>())
        {
            var profile = population.Profile(bot.Uid, 1, 1)!;
            var fields = (Dictionary<string, object?>)profile["Data"]!;
            var info = RoleAdditionBaseInfo.Parser.ParseFrom((byte[])fields["Base"]!);
            Assert.Equal(bot.Entity().CurUseAppearances, info.Appearances);
            Assert.True(info.Appearances.ContainsKey(7));
        }
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
            NullLogger<CombatService>.Instance, new PetService(data, character, null!, null!, null!, TimeProvider.System, null!)), monsters);
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
            await loop.InvokeAsync(async () =>
            {
                await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                await combat.PopulationAttackAsync(bot, monster, 1000001, MonsterService.NowMs);
            });
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
            await loop.InvokeAsync(async () =>
            {
                await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                await combat.PopulationAttackAsync(bot, monster, 1000001, MonsterService.NowMs); monster.Hp = 99;
            });
            await Task.Delay(300);
            Assert.Equal(99, await loop.InvokeAsync(() => Task.FromResult(monster.Hp)));
        }
        finally { await loop.StopAsync(default); }
    }

    [Fact]
    public async Task EquippedAttributeBonusesIncreaseActualCombatDamage()
    {
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """{"1000001":{"_iActionTime":300,"_iDistanceMax":300,"_iSkillHit":10000,"_iDmgType":1,"_kDamageParam1":[10000]}}""");
        var (combat, _) = CombatServices();
        var bot = Fighter(); var monster = Monster(1000); bot.Target = monster; bot.TargetExpectedHp = 1000;
        bot.GearAttributes[103] = 100;
        await loop.StartAsync(default);
        try
        {
            await loop.InvokeAsync(async () =>
            {
                await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                await combat.PopulationAttackAsync(bot, monster, 1000001, MonsterService.NowMs);
            });
            await WaitFor(() => monster.Hp < 1000);
            Assert.True(1000 - monster.Hp > 100, "Gear was shown visually but omitted from actual damage.");
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
            await loop.InvokeAsync(async () =>
            {
                await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                await combat.PopulationAttackAsync(bot, monster, 1000001, MonsterService.NowMs); await world.RemoveUnitAsync(bot);
            });
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
            await loop.InvokeAsync(async () =>
            {
                await world.AddUnitAsync(bot); await world.AddUnitAsync(monster);
                monsters.Engage(monster, bot); await monsters.AttackPopulation!(monster, bot);
            });
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
