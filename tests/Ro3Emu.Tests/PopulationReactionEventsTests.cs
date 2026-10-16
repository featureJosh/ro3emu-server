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

/// <summary>Real-event reactions (support, shared fights, bosses) and bot body language; synthetic config only.</summary>
public sealed class PopulationReactionEventsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ro3-population-events-" + Guid.NewGuid().ToString("N"));
    private readonly GameData data;

    public PopulationReactionEventsTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "PerformanceConfig.json"), """
            {"50001":{"_iID":50001,"_iSort":1,"_iDuration":2000},"50002":{"_iID":50002,"_iSort":1,"_iDuration":0},
             "50009":{"_iID":50009,"_iSort":1,"_iSex":2},"20205":{"_iID":20205,"_iSort":2}}
            """);
        File.WriteAllText(Path.Combine(root, "config", "MapConfig.json"), """{"1":{},"3":{"_iPerformanceForbidden":1}}""");
        File.WriteAllText(Path.Combine(root, "config", "NPCConfig.json"), """{"1":{"_iNPCSubType":1},"7":{"_iNPCSubType":4},"8":{"_iNPCSubType":3}}""");
        data = new GameData(Options.Create(new ServerOptions { DataDir = root }), NullLogger<GameData>.Instance);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static GameSession Player(int id = 1) => new(null!, null!)
    {
        Role = new Role(id, "test", 1, "Player" + id, 1, 1000, 5, 0, 0, 0, 1, 128, 0, 128, 0),
        SceneEntry = 1, LineNumber = 1, Position = new Vec3 { X = 128, Z = 128 },
    };

    private static PopulationUnit Bot(long serial = 1, float x = 128)
    {
        var bot = new PopulationUnit { Uid = (1L << 50) | serial, Name = "Bot" + serial, SceneEntry = 1, X = x, Z = 128, Level = 5 };
        bot.Hp = bot.MaxHp;
        return bot;
    }

    private static MonsterUnit Monster(long uid = 1L << 41, int npc = 1, long hp = 100) => new()
    {
        Uid = uid, SceneEntry = 1, X = 130, Z = 128, Hp = hp,
        Template = new MonsterTemplate(npc, 1, hp, 0, 10, 10, 0, 0, 0, 0, 0, 0, 1, 1, 1, 2,
            false, 0, 1000, 10, 30, 1000001, 300, 0.05f, 2000, 2, 100),
        Home = new SpawnPoint(1, 128, 0, 128, 0, 1), Group = new SpawnGroup(1, false, 1, 10, [], []),
    };

    private static Dictionary<PopulationUnit, GameSession[]> Audience(PopulationUnit[] bots, params GameSession[] players) =>
        bots.ToDictionary(b => b, _ => players);

    [Fact]
    public void SupportReactionsThankOnlyTheSupportedBotOnceAndHonorSwitchesAndExpiry()
    {
        var healed = Bot(1); var other = Bot(2); var player = Player();
        var options = new PopulationChatOptions { GreetingsEnabled = false };
        var reactions = new PopulationReactions(options);
        var audience = Audience([healed, other], player);
        reactions.RecordSupport(healed, player, false, 0);
        Assert.Equal(0, reactions.PendingEvents); // nothing is recorded before the first enabled pass
        reactions.Observe(audience, 0, true);
        reactions.RecordSupport(healed, player, false, 0); reactions.RecordSupport(healed, player, false, 100);
        Assert.Equal(1, reactions.PendingEvents); // repeated casts coalesce
        Assert.Null(reactions.Find(healed, [player], 4000)); // shared five-second delay
        Assert.Null(reactions.Find(other, [player], 6000));
        var thanks = reactions.Find(healed, [player], 6000)!;
        Assert.Equal("thanks", thanks.Context); Assert.Same(player, thanks.Subject);
        reactions.Consume(thanks, 6000);
        Assert.Null(reactions.Find(healed, [player], 7000));
        reactions.RecordSupport(healed, player, false, 8000); reactions.Observe(audience, 8000, true);
        Assert.Null(reactions.Find(healed, [player], 20000)); // per-player cooldown
        reactions.RecordSupport(healed, player, true, 9000);
        Assert.Equal("revived", reactions.Find(healed, [player], 14000)!.Context); // its own context and cooldown
        options.ReviveReactionsEnabled = false;
        Assert.Null(reactions.Find(healed, [player], 14000));
        options.ReviveReactionsEnabled = true;
        player.SceneGeneration++;
        Assert.Null(reactions.Find(healed, [player], 14000)); // the supporter changed map/line generation
        player.SceneGeneration--;
        reactions.Observe(audience, 100_000, true);
        Assert.Null(reactions.Find(healed, [player], 100_000)); // expired after 90 seconds
        options.HealReactionsEnabled = false;
        reactions.RecordSupport(other, player, false, 200_000); reactions.Observe(audience, 200_000, true);
        Assert.Null(reactions.Find(other, [player], 210_000));
        options.HealReactionsEnabled = true;
        reactions.RecordSupport(other, player, false, 300_000);
        reactions.Observe(audience, 300_000, false); // chat off: pending events are dropped, never replayed
        reactions.Observe(audience, 301_000, true);
        Assert.Null(reactions.Find(other, [player], 310_000));
    }

    [Fact]
    public void SharedFightNeedsBotAndPlayerDamageAndProducesOneLineForTheWholeAudience()
    {
        var hunter = Bot(1); var bystander = Bot(2); var a = Player(1); var b = Player(2);
        var reactions = new PopulationReactions(new() { GreetingsEnabled = false });
        var audience = Audience([hunter, bystander], a, b);
        reactions.Observe(audience, 0, true);
        var solo = Monster(1); reactions.RecordPlayerHit(a, solo, 0);
        Assert.Equal(0, reactions.TrackedFights); // player-only combat allocates nothing
        var monster = Monster(2);
        reactions.RecordBotHit(hunter, monster, 1000); reactions.RecordPlayerHit(a, monster, 1500); reactions.RecordPlayerHit(b, monster, 1600);
        reactions.Observe(audience, 2000, true);
        Assert.Null(reactions.Find(hunter, [a, b], 9000)); // still alive
        monster.Dead = true; reactions.Observe(audience, 3000, true);
        Assert.Equal(0, reactions.TrackedFights);
        Assert.Null(reactions.Find(hunter, [a, b], 4000));
        Assert.Null(reactions.Find(bystander, [a, b], 6000)); // did not take part
        var line = reactions.Find(hunter, [a, b], 6000)!;
        Assert.Equal("sharedfight", line.Context); Assert.Same(a, line.Subject);
        reactions.Consume(line, 6000);
        Assert.Null(reactions.Find(hunter, [a, b], 7000)); // one line per fight, not per participant/viewer
        Assert.Null(reactions.Find(hunter, [b], 7000));
        var far = Monster(3); far.X = 128 + 25;
        reactions.RecordBotHit(hunter, far, 10_000); reactions.RecordPlayerHit(b, far, 10_000);
        far.Dead = true; reactions.Observe(audience, 11_000, true);
        Assert.Null(reactions.Find(hunter, [b], 14_000)); // the kill was not nearby
        var stale = Monster(4);
        reactions.RecordBotHit(hunter, stale, 20_000); reactions.RecordPlayerHit(b, stale, 20_000);
        reactions.Observe(audience, 90_000, true); Assert.Equal(0, reactions.TrackedFights); // abandoned fights are forgotten
        for (int i = 0; i < 600; i++) reactions.RecordBotHit(hunter, Monster(100 + i), 100_000 + i);
        Assert.Equal(512, reactions.TrackedFights);
    }

    [Fact]
    public void BossIsNoticedOncePerSpawnByOneNearbyBot()
    {
        var near = Bot(1); var second = Bot(2, 130); var far = Bot(3, 128 + 60); var a = Player(1); var b = Player(2);
        var options = new PopulationChatOptions { GreetingsEnabled = false };
        var reactions = new PopulationReactions(options);
        var audience = Audience([near, second, far], a, b);
        reactions.Observe(audience, 0, true);
        var boss = Monster(9, 7); boss.X = 150;
        reactions.ObserveBoss(boss, 0); reactions.ObserveBoss(boss, 1000);
        Assert.Equal(1, reactions.PendingEvents);
        Assert.Null(reactions.Find(near, [a, b], 4000));
        far.X = boss.X + 40; var c = Player(3); c.Position = new Vec3 { X = far.X, Z = 128 };
        Assert.Null(reactions.Find(far, [c], 6000)); // a listener, but the boss is out of the bot's range
        var notice = reactions.Find(near, [a, b], 6000)!;
        Assert.Equal("boss", notice.Context);
        reactions.Consume(notice, 6000);
        Assert.Null(reactions.Find(second, [a, b], 7000)); // not per bot or per viewer
        reactions.ObserveBoss(boss, 8000); Assert.Null(reactions.Find(near, [a, b], 20000)); // not re-noticed while alive
        reactions.Observe(audience, 30_000, false); reactions.Observe(audience, 31_000, true);
        reactions.ObserveBoss(boss, 31_000); Assert.Null(reactions.Find(near, [a, b], 40_000)); // chat off/on keeps the notice
        boss.Dead = true; reactions.Observe(audience, 50_000, true);
        var respawn = Monster(10, 7); respawn.X = 150;
        reactions.ObserveBoss(respawn, 50_000);
        Assert.Equal("boss", reactions.Find(second, [a, b], 56_000)!.Context); // a new spawn is a new sighting
        options.BossReactionsEnabled = false;
        Assert.Null(reactions.Find(second, [a, b], 56_000));
        options.BossReactionsEnabled = true;
        Assert.Null(reactions.Find(near, [], 56_000)); // no listener, no line
    }

    [Fact]
    public void ChatPlannerDeliversOneSharedFightLineAndDefaultPacksExist()
    {
        var hunter = Bot(1); var helper = Bot(2); var a = Player(1); var b = Player(2);
        foreach (var bot in new[] { hunter, helper }) { bot.Viewers.Add(a); bot.Viewers.Add(b); }
        var options = new PopulationChatOptions { Mode = "chatty", GreetingsEnabled = false };
        options.Phrases["sharedfight"] = ["Nice hit, {player}!"];
        foreach (var key in new[] { "hunt", "rest", "travel", "town", "npc" }) options.Phrases[key] = [];
        var chatter = new PopulationChatter(options);
        PopulationUnit[] bots = [hunter, helper];
        chatter.Plan(bots, _ => "field", 0, _ => [a, b]);
        var monster = Monster();
        chatter.Reactions.RecordBotHit(hunter, monster, 1000); chatter.Reactions.RecordBotHit(helper, monster, 1000);
        chatter.Reactions.RecordPlayerHit(a, monster, 1000); chatter.Reactions.RecordPlayerHit(b, monster, 1000);
        monster.Dead = true;
        var lines = new List<PopulationChatter.Line>();
        for (long now = 60_000; now <= 200_000; now += 1000) lines.AddRange(chatter.Plan(bots, _ => "field", now, _ => [a, b]));
        Assert.Empty(lines); // the fight ended before the map's arrival stagger: expired, not queued
        chatter.Reactions.RecordBotHit(hunter, monster = Monster(), 201_000); chatter.Reactions.RecordPlayerHit(b, monster, 201_000);
        monster.Dead = true;
        for (long now = 201_000; now <= 400_000; now += 1000) lines.AddRange(chatter.Plan(bots, _ => "field", now, _ => [a, b]));
        var line = Assert.Single(lines);
        Assert.Equal("Nice hit, Player2!", line.Text); Assert.Same(hunter, line.Bot); Assert.Equal("sharedfight", line.Context);
        foreach (var key in new[] { "thanks", "revived", "sharedfight", "boss" })
            Assert.InRange(PopulationChatOptions.DefaultPhrases[key].Distinct().Count(), 10, 12);
    }

    [Fact]
    public void ReactionSwitchesAndGestureOptionsBindFromConfiguration()
    {
        var bound = new ServerOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Population:Chat:HealReactionsEnabled"] = "false", ["Population:Chat:BossReactionsEnabled"] = "false",
            ["Population:Gestures:Enabled"] = "false", ["Population:Gestures:SitEmojiId"] = "0",
        }).Build().Bind(bound);
        Assert.False(bound.Population.Chat.HealReactionsEnabled); Assert.True(bound.Population.Chat.ReviveReactionsEnabled);
        Assert.True(bound.Population.Chat.SharedFightReactionsEnabled); Assert.False(bound.Population.Chat.BossReactionsEnabled);
        Assert.False(bound.Population.Gestures.Enabled); Assert.Equal(0, bound.Population.Gestures.SitEmojiId);
        Assert.True(new ServerOptions().Population.Gestures.Enabled);
    }

    [Fact]
    public void RestingBotsSitBoundedAndStandUpBeforeMovingOrFighting()
    {
        var gestures = new PopulationGestures(new() { SitChancePercent = 100 }, data);
        var bot = Bot(1); bot.IdleChatContext = "rest"; bot.ActivityUntilMs = 10_000;
        var sit = Assert.Single(gestures.Plan([bot], 1000));
        var state = Assert.IsType<AOIAttrChange>(sit.Body);
        Assert.Equal(CombatService.AoiAttrChange, sit.MsgId); Assert.Equal((ulong)bot.Uid, state.EntityUuid);
        Assert.Equal(ObjectState.EObjectStateSmemojiPlay, state.BehaviorStateMachine.CurState);
        Assert.Equal(50002, state.BehaviorStateMachine.Param.EmojiPlay.EmojiId);
        Assert.Equal(ObjectState.EObjectStateSmemojiPlay, bot.Entity().BehaviorStateMachineInfo.CurState); // late viewers
        Assert.Empty(gestures.Plan([bot], 2000));
        var stand = Assert.Single(gestures.Plan([bot], 9800)); // before the rest ends and the bot may walk
        Assert.Equal(ObjectState.EObjectStateSmidle, ((AOIAttrChange)stand.Body).BehaviorStateMachine.CurState);
        Assert.Null(bot.Entity().BehaviorStateMachineInfo);
        Assert.Empty(gestures.Plan([bot], 9900)); // one roll per rest stop
        bot.ActivityUntilMs = 30_000; Assert.Single(gestures.Plan([bot], 20_000));
        bot.Target = Monster(); Assert.Single(gestures.Plan([bot], 21_000)); Assert.Equal(0, gestures.Active);
        bot.Target = null; bot.ActivityUntilMs = 50_000; gestures.Plan([bot], 40_000);
        bot.WalkTo(new MapPoint(bot.X + 5, 0, bot.Z, 0), 41_000);
        Assert.Single(gestures.Plan([bot], 41_000)); Assert.Equal(0, gestures.Active);
        var moving = Bot(2); moving.IdleChatContext = "rest"; moving.ActivityUntilMs = 70_000; moving.WalkTo(new MapPoint(140, 0, 128, 0), 60_000);
        var fighting = Bot(3); fighting.IdleChatContext = "rest"; fighting.ActivityUntilMs = 70_000; fighting.Target = Monster();
        var hurt = Bot(4); hurt.IdleChatContext = "rest"; hurt.ActivityUntilMs = 70_000; hurt.Hp = 10;
        var forbidden = Bot(5); forbidden.SceneEntry = 3; forbidden.IdleChatContext = "rest"; forbidden.ActivityUntilMs = 70_000;
        Assert.Empty(gestures.Plan([moving, fighting, hurt, forbidden], 60_000));
        var crowd = Enumerable.Range(10, 20).Select(i => { var b = Bot(i); b.IdleChatContext = "rest"; b.ActivityUntilMs = 90_000; return b; }).ToArray();
        Assert.Equal(4, gestures.Plan(crowd, 80_000).Count); // bounded starts per pass
        var removed = crowd.First(b => b.Behavior != null); removed.Removed = true;
        Assert.DoesNotContain(gestures.Plan(crowd, 80_250), p => p.Bot == removed);
        Assert.Null(removed.Behavior);
        var off = new PopulationGestures(new() { SitChancePercent = 100, SitEmojiId = 0 }, data);
        var resting = Bot(40); resting.IdleChatContext = "rest"; resting.ActivityUntilMs = 90_000;
        Assert.Empty(off.Plan([resting], 80_000));
        var unknown = new PopulationGestures(new() { SitChancePercent = 100, SitEmojiId = 12345 }, data);
        Assert.Empty(unknown.Plan([resting], 80_000)); // IDs the client data cannot prove are never sent
    }

    [Fact]
    public void BotsFaceGreetedPlayersAndNearbySpeakersAndOccasionallyWave()
    {
        var gestures = new PopulationGestures(new() { WaveChancePercent = 100 }, data);
        var bot = Bot(1); bot.Face = 0; var player = Player(); player.Position = new Vec3 { X = 128 + 5, Z = 128 };
        gestures.Spoke(new PopulationChatter.Line(bot, "Hello, Player1!", player, 0, 1, 0, "greeting"), 1000);
        var packets = gestures.Plan([bot], 1200);
        Assert.Equal(2, packets.Count);
        var turn = Assert.IsType<EntityFaceToAll>(packets[0].Body);
        Assert.Equal(GameAoiSink.EntityFaceToAllId, packets[0].MsgId); Assert.Equal(90, turn.Face, 1); Assert.Equal(90, bot.Face, 1);
        Assert.Equal(50001, ((AOIAttrChange)packets[1].Body).BehaviorStateMachine.Param.EmojiPlay.EmojiId);
        Assert.True(bot.NextWalkMs >= 4200); // a short pause instead of walking off mid-greeting
        Assert.Equal(ObjectState.EObjectStateSmidle, ((AOIAttrChange)Assert.Single(gestures.Plan([bot], 3300)).Body).BehaviorStateMachine.CurState);
        // A nearby player's chat turns at most two idle bots once; moving/fighting/far bots never turn.
        var bots = Enumerable.Range(2, 4).Select(i => { var b = Bot(i, 128 - i); b.Face = 0; return b; }).ToArray();
        var walker = Bot(10, 127); walker.WalkTo(new MapPoint(110, 0, 128, 0), 4000);
        var fighter = Bot(11, 127); fighter.Target = Monster();
        var far = Bot(12, 128 - 30);
        var speaker = Player(2); speaker.Position = new Vec3 { X = 128, Z = 128 + 6 };
        gestures.NoteSpeaker(speaker, 4000); gestures.NoteSpeaker(speaker, 4100);
        var turns = gestures.Plan([.. bots, walker, fighter, far], 4200);
        Assert.Equal(2, turns.Count); Assert.All(turns, t => Assert.Contains(t.Bot, bots));
        gestures.NoteSpeaker(speaker, 6000); Assert.Empty(gestures.Plan([.. bots], 6100)); // per-speaker cooldown
        gestures.NoteSpeaker(speaker, 20_000); Assert.Empty(gestures.Plan([.. bots], 30_000)); // stale speech is not acted on later
        var noWave = new PopulationGestures(new() { WaveChancePercent = 100 }, data);
        var facing = Bot(20); facing.Face = 90;
        noWave.Spoke(new PopulationChatter.Line(facing, "Hi", player, 0, 1, 0, "levelup"), 1000);
        Assert.Empty(noWave.Plan([facing], 1100)); // already facing and not a greeting: nothing to send
        var disabled = new PopulationGestures(new() { Enabled = false }, data);
        disabled.NoteSpeaker(speaker, 1000); disabled.Spoke(new PopulationChatter.Line(bot, "Hello", player, 0, 1, 0, "greeting"), 1000);
        Assert.Empty(disabled.Plan([.. bots], 1100));
    }

    [Fact]
    public async Task PopulationServiceRecognisesBossTemplatesAndExposesSupportPlumbing()
    {
        var options = new ServerOptions { DataDir = root, Population = new() { Enabled = true } };
        var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        var world = new World(new NullSink(), loop, NullLogger<World>.Instance);
        var wrapped = Options.Create(options);
        var population = new PopulationService(world, new WorldData(wrapped, data, NullLogger<WorldData>.Instance), data, loop, wrapped);
        Assert.False(population.IsBoss(1)); Assert.True(population.IsBoss(7)); Assert.True(population.IsBoss(8)); Assert.False(population.IsBoss(999));
        population.NotePlayerSupport(Bot(1), Player(), false); // safe before any population pass; nothing recorded
        await population.UpdateAsync(1000);
        loop.Dispose();
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
