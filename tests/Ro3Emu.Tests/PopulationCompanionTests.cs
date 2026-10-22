using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Tencent.IM;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

internal static class CompanionFixture
{
    /// <summary>A flat, fully walkable 256x256 grid for <paramref name="scene"/> (same layout as PopulationTests).</summary>
    public static void Write(string root, long scene)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "nav"));
        File.WriteAllText(Path.Combine(root, "config", "MapConfig.json"), "{\"" + scene + "\":{\"_iMapResourceID\":1}}");
        File.WriteAllText(Path.Combine(root, "config", "MapResource.json"), """{"1":{"_kNavDataName":"test_dynamicmapdata.bytes"}}""");
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """{"1000":{"_iUnitB":1000,"_iUnitG":1001},"1100":{"_iUnitB":1100,"_iUnitG":1101}}""");
        byte[] grid = new byte[64 + 256 * 256 * 4];
        BitConverter.GetBytes(256).CopyTo(grid, 20);
        BitConverter.GetBytes(256).CopyTo(grid, 24);
        BitConverter.GetBytes(256).CopyTo(grid, 28);
        BitConverter.GetBytes(64).CopyTo(grid, 48);
        for (int i = 64; i < grid.Length; i += 4) grid[i] = 0x80;
        File.WriteAllBytes(Path.Combine(root, "nav", "test_dynamicmapdata.bytes"), grid);
    }
}

/// <summary>Companion lifecycle, follow, ownership and combat on synthetic navigation and the real World AOI path.</summary>
public sealed class PopulationCompanionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ro3-companion-" + Guid.NewGuid().ToString("N"));
    private readonly GameLoop loop = new(NullLogger<GameLoop>.Instance);
    private readonly World world;
    private readonly ServerOptions options;
    private readonly GameData data;
    private readonly WorldData worldData;
    private PopulationService population;

    public PopulationCompanionTests()
    {
        CompanionFixture.Write(root, 1);
        options = new ServerOptions
        {
            DataDir = root, Population = new()
            {
                Enabled = true, PerMap = 6, MaxTotal = 10, MaxSpawnsPerTick = 100,
                Maps = new() { [1] = new() { AreaLevel = 5, MinLevel = 5, MaxLevel = 5 } },
                Companions = new() { Enabled = true, DisconnectGraceSeconds = 1 }
            }
        };
        var wrapped = Options.Create(options);
        data = new GameData(wrapped, NullLogger<GameData>.Instance);
        world = new World(new NullSink(), loop, NullLogger<World>.Instance);
        worldData = new WorldData(wrapped, data, NullLogger<WorldData>.Instance);
        population = new PopulationService(world, worldData, data, loop, wrapped);
    }

    private static GameSession Player(int id = 1, int line = 1, long instance = 0) => new(null!, null!)
    {
        Role = new Role(id, "test", 1, "Player" + id, 1, 1000, 5, 0, 0, 0, 1, 128, 0, 128, 0),
        SceneEntry = 1, InstanceId = instance, LineNumber = line, Position = new Vec3 { X = 128, Z = 128 },
    };

    private static float Distance(PopulationUnit bot, GameSession p) => MathF.Sqrt(MathF.Pow(bot.X - p.Position.X, 2) + MathF.Pow(bot.Z - p.Position.Z, 2));

    private async Task<(GameSession Owner, PopulationUnit Bot)> SoloCompanion()
    {
        options.Population.Enabled = false;
        var owner = Player(); await world.JoinAsync(owner);
        Assert.Null(await population.RecruitAsync(owner));
        return (owner, world.AllUnits().OfType<PopulationUnit>().Single());
    }

    [Fact]
    public void CompanionsAreEnabledByDefaultWithOneConservativeSlot()
    {
        var defaults = new PopulationOptions().Companions;
        Assert.True(defaults.Enabled); Assert.Equal(1, defaults.MaxPerPlayer);
        Assert.Equal(60, defaults.DisconnectGraceSeconds);
    }

    [Fact]
    public async Task RecruitTakesNearestAmbientBotOutOfTheAmbientPoolAndBudget()
    {
        var owner = Player(); await world.JoinAsync(owner); await population.UpdateAsync(1000);
        Assert.Equal(6, population.Count);
        var nearest = world.AllUnits().OfType<PopulationUnit>().MinBy(b => Distance(b, owner))!;
        nearest.X = 130; nearest.Z = 128;
        Assert.Null(await population.RecruitAsync(owner));
        Assert.Equal(5, population.Count); Assert.Equal(1, population.CompanionCount);
        Assert.Same(nearest, population.CompanionActor(nearest.Uid)); Assert.False(nearest.Removed);
        Assert.Equal(owner.Role!.Id, population.CompanionOwner(nearest.Uid));
        Assert.NotNull(await population.RecruitAsync(owner)); // MaxPerPlayer = 1
        await population.UpdateAsync(2000);
        Assert.Equal(6, population.Count); // ambient refills; the companion is not counted twice
        Assert.Equal(7, world.AllUnits().OfType<PopulationUnit>().Count());
        static bool Invitable(PopulationService p, long uid) => !RoleAdditionBaseInfo.Parser.ParseFrom(
            (byte[])((Dictionary<string, object?>)p.Profile(uid, 1, 1)!["Data"]!)["Base"]!).CantBeInvited;
        Assert.False(Invitable(population, nearest.Uid));
        Assert.True(Invitable(population, world.AllUnits().OfType<PopulationUnit>().First(b => b != nearest).Uid));
        options.Population.Companions.Enabled = false;
        Assert.False(Invitable(population, world.AllUnits().OfType<PopulationUnit>().First(b => b != nearest).Uid));
    }

    [Fact]
    public async Task RecruitSpawnsBesideOwnerAtOwnerLevelOnlyOnPublicMaps()
    {
        options.Population.Enabled = false;
        var owner = Player(); owner.Progress = new RoleProgress { Level = 12 }; await world.JoinAsync(owner);
        options.Population.Companions.Enabled = false;
        Assert.NotNull(await population.RecruitAsync(owner));
        options.Population.Companions.Enabled = true;
        var dungeon = Player(2, instance: 9); await world.JoinAsync(dungeon);
        Assert.NotNull(await population.RecruitAsync(dungeon));
        Assert.Null(await population.RecruitAsync(owner));
        var bot = world.AllUnits().OfType<PopulationUnit>().Single();
        Assert.Equal(12, bot.Level); Assert.InRange(Distance(bot, owner), 0, 5.01f);
        Assert.True(PopulationService.IsUid(bot.Uid)); Assert.Equal(0, population.Count);
        var card = population.CompanionCard(bot.Uid)!;
        Assert.Equal(bot.Name, card.Name); Assert.Equal(1, card.MapId); Assert.True(card.Present); Assert.Equal(bot.MaxHp, card.Hp);
    }

    [Fact]
    public async Task CompanionFollowsCatchesUpAndRespawnsBesideOwnerAfterLineAndInstanceChanges()
    {
        var (owner, bot) = await SoloCompanion();
        owner.Position = new Vec3 { X = 148, Z = 128 };
        long now = 2000;
        await population.UpdateCompanionsAsync(now);
        Assert.True(bot.Moving);
        for (int i = 0; i < 40 && Distance(bot, owner) > 6; i++) await population.UpdateCompanionsAsync(now += 250);
        Assert.InRange(Distance(bot, owner), 0, 6);
        Assert.Same(bot, population.CompanionActor(bot.Uid));

        owner.Position = new Vec3 { X = 220, Z = 220 }; // beyond the 40-unit catch-up distance
        await population.UpdateCompanionsAsync(now += 250);
        var caught = population.CompanionActor(bot.Uid)!;
        Assert.True(bot.Removed); Assert.False(caught.Removed); Assert.InRange(Distance(caught, owner), 0, 5.01f);

        await world.LeaveAsync(owner); owner.LineNumber = 2; await world.JoinAsync(owner);
        await population.UpdateCompanionsAsync(now += 250);
        var lined = population.CompanionActor(bot.Uid)!;
        Assert.True(caught.Removed); Assert.Equal(owner.RuntimeMap, lined.RuntimeMap); Assert.Equal(bot.Name, lined.Name);

        owner.SceneLoading = true;
        await population.UpdateCompanionsAsync(now += 250);
        Assert.True(lined.Removed); Assert.False(population.CompanionCard(bot.Uid)!.Present);
        await world.LeaveAsync(owner); owner.SceneLoading = false; owner.InstanceId = 77; owner.LineNumber = 1; await world.JoinAsync(owner);
        await population.UpdateCompanionsAsync(now += 250);
        Assert.Equal(1, population.CompanionCount);
        Assert.DoesNotContain(world.AllUnits().OfType<PopulationUnit>(), u => u.WorldScene == owner.WorldScene); // waits outside
        await world.LeaveAsync(owner); owner.InstanceId = 0; await world.JoinAsync(owner);
        await population.UpdateCompanionsAsync(now += 250);
        var back = population.CompanionActor(bot.Uid)!;
        Assert.False(back.Removed); Assert.Equal(owner.RuntimeMap, back.RuntimeMap);
        Assert.Single(world.AllUnits().OfType<PopulationUnit>());
    }

    [Fact]
    public async Task StayHoldsPositionAndFollowResumes()
    {
        var (owner, bot) = await SoloCompanion();
        float x = bot.X, z = bot.Z;
        Assert.Equal(1, await population.SetCompanionModeAsync(owner, "stay"));
        owner.Position = new Vec3 { X = 150, Z = 128 };
        for (long now = 2000; now < 4000; now += 250) await population.UpdateCompanionsAsync(now);
        Assert.Equal((x, z), (bot.X, bot.Z));
        Assert.Equal(1, await population.SetCompanionModeAsync(owner, "follow"));
        await population.UpdateCompanionsAsync(4250);
        Assert.True(bot.Moving);
    }

    [Fact]
    public async Task OnlyTheOwnerCommandsOrDismissesAndBotsOffRemovesEveryCompanion()
    {
        var (owner, bot) = await SoloCompanion();
        var friend = Player(2); await world.JoinAsync(friend);
        Assert.Equal(0, await population.SetCompanionModeAsync(friend, "passive"));
        Assert.Equal(0, await population.DismissCompanionsAsync(friend.Role!.Id, all: true));
        Assert.Equal(1, population.CompanionCount); Assert.False(bot.Removed);
        Assert.Null(await population.RecruitAsync(friend));
        Assert.Equal(2, population.CompanionCount);
        Assert.Equal(1, await population.DismissCompanionsAsync(owner.Role!.Id, all: false));
        Assert.True(bot.Removed); Assert.Equal(1, population.CompanionCount);
        Assert.Equal(1, await population.DismissCompanionsAsync(null, all: true));
        Assert.Equal(0, population.CompanionCount); Assert.Empty(world.AllUnits().OfType<PopulationUnit>());
    }

    [Fact]
    public async Task OwnerDisconnectDespawnsThenDismissesAfterGraceAndReconnectRestores()
    {
        var (owner, bot) = await SoloCompanion();
        await world.LeaveAsync(owner);
        await population.UpdateCompanionsAsync(10_000);
        Assert.True(bot.Removed); Assert.Equal(1, population.CompanionCount);
        await world.JoinAsync(owner);
        await population.UpdateCompanionsAsync(10_500);
        var restored = population.CompanionActor(bot.Uid)!;
        Assert.False(restored.Removed);
        await world.LeaveAsync(owner);
        await population.UpdateCompanionsAsync(20_000);
        await population.UpdateCompanionsAsync(20_900);
        Assert.Equal(1, population.CompanionCount);
        await population.UpdateCompanionsAsync(21_000);
        Assert.Equal(0, population.CompanionCount); Assert.Empty(world.AllUnits().OfType<PopulationUnit>());
    }

    [Fact]
    public async Task DeadCompanionRevivesInPlaceAfterThirtySeconds()
    {
        var (_, bot) = await SoloCompanion();
        bot.Viewers.Clear(); // fixture sessions have no network channel for 31049
        bot.Hp = 0;
        await population.UpdateCompanionsAsync(1000);
        Assert.True(population.CompanionCard(bot.Uid)!.Dead);
        await population.UpdateCompanionsAsync(30_000); Assert.True(bot.Dead);
        await population.UpdateCompanionsAsync(31_000);
        Assert.False(bot.Dead); Assert.Equal(bot.MaxHp, bot.Hp);
    }

    [Fact]
    public async Task AssistOnlyHitsTheOwnersFightAndNeverLandsAKillOrReward()
    {
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """{"1000001":{"_iActionTime":300,"_iDistanceMax":300,"_iSkillHit":10000,"_iDmgType":1,"_kDamageParam1":[10000]}}""");
        var character = new CharacterService(null!, null!, data, world, NullLogger<CharacterService>.Instance);
        var skills = new SkillService(data, character, null!, NullLogger<SkillService>.Instance);
        var sessions = new SessionRegistry(NullLogger<SessionRegistry>.Instance);
        var buffs = new BuffService(data, world, sessions, character, loop, NullLogger<BuffService>.Instance);
        var monsters = new MonsterService(world, worldData, data, loop, NullLogger<MonsterService>.Instance);
        var scene = new SceneService(Options.Create(options), world, worldData, monsters, buffs, data,
            NullLogger<SceneService>.Instance, new PublicLineManager(data, world), TimeProvider.System);
        var combat = new CombatService(world, monsters, character, skills, scene, buffs, worldData, sessions, data, loop, NullLogger<CombatService>.Instance, new PetService(data, character, null!, null!, null!, TimeProvider.System, null!));
        population = new PopulationService(world, worldData, data, loop, Options.Create(options), combat);
        int rewards = 0;
        combat.BeforeKilled.Add((_, _) => { rewards++; return Task.CompletedTask; });
        combat.Killed.Add((_, _) => { rewards++; return Task.CompletedTask; });
        var (owner, bot) = await SoloCompanion();
        bot.GearAttributes[103] = 1000; bot.NormalSkill = 1000001; bot.AttackRange = 3;
        var monster = new MonsterUnit
        {
            Uid = 1L << 41, SceneEntry = 1, X = bot.X + 1, Z = bot.Z, Hp = 40,
            Template = new MonsterTemplate(1, 1, 40, 0, 10, 10, 0, 0, 0, 0, 0, 0, 1, 1, 1, 2,
                false, 0, 1000, 10, 30, 1000001, 300, 0.05f, 2000, 2, 100),
            Home = new SpawnPoint(1, 128, 0, 128, 0, 1), Group = new SpawnGroup(1, false, 1, 10, [], []),
        };
        await loop.StartAsync(default);
        try
        {
            long now = MonsterService.NowMs;
            await loop.InvokeAsync(async () =>
            {
                await world.AddUnitAsync(monster);
                bot.Viewers.Clear(); monster.Viewers.Clear(); // fixture sessions have no network channel
                await population.UpdateCompanionsAsync(now);
            });
            await Task.Delay(300);
            Assert.Equal(40, await loop.InvokeAsync(() => Task.FromResult(monster.Hp))); // not the owner's fight: ignored
            await loop.InvokeAsync(() => { monster.Target = owner; return Task.CompletedTask; });
            for (int i = 0; i < 6; i++)
            {
                now += 2000;
                await loop.InvokeAsync(() => population.UpdateCompanionsAsync(now));
                await Task.Delay(250);
            }
            Assert.Equal(1, await loop.InvokeAsync(() => Task.FromResult(monster.Hp)));
            Assert.False(monster.Dead); Assert.Equal(0, rewards); Assert.Null(monster.RewardCommit);
            await loop.InvokeAsync(() => { monster.Hp = 30; return Task.CompletedTask; });
            Assert.Equal(1, await loop.InvokeAsync(() => population.SetCompanionModeAsync(owner, "passive")));
            await loop.InvokeAsync(() => population.UpdateCompanionsAsync(now += 2000));
            await Task.Delay(300);
            Assert.Equal(30, await loop.InvokeAsync(() => Task.FromResult(monster.Hp)));
        }
        finally { await loop.StopAsync(default); }
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

/// <summary>The real party roster over the game gate: recruit, normal invite, LAN friend view, leave and relog.</summary>
public sealed class PopulationCompanionPartyTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-companion-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        CompanionFixture.Write(_dataDir, RoleStore.SpawnSceneEntry);
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.ReconnectGraceSeconds = 0;
            o.Population.Enabled = true; o.Population.PerMap = 3; o.Population.MaxTotal = 10;
            o.Population.Companions = new() { Enabled = true, RecruitRange = 60, DisconnectGraceSeconds = 1 };
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<(BotClient Bot, long Id)> InWorld(string name, long existing = 0)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = existing != 0 ? existing : (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private Task<T> OnLoop<T>(Func<T> read) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await OnLoop(condition)) await Task.Delay(50, timeout.Token);
    }

    [Fact]
    public async Task RecruitedCompanionIsARealRosterMemberSeenByAFriendAndLeavesWithItsOwner()
    {
        var population = _app.Services.GetRequiredService<PopulationService>();
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        await Gm("alice", "!bots recruit");
        Assert.Equal(0, (await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser)).Result);
        var joined = await Next(alice, TeamService.TeamNewMember, BroadCastNewMemberEnter.Parser, m => PopulationService.IsUid(m.DetailInfo.Guid));
        long botUid = joined.DetailInfo.Guid;
        Assert.False(string.IsNullOrEmpty(joined.DetailInfo.Name));
        Assert.Equal((int)RoleStore.SpawnSceneEntry, joined.DetailInfo.MapId);
        Assert.True(joined.DetailInfo.BattleInfo.MaxHpValue > 1); Assert.True(joined.DetailInfo.BattleInfo.HpPercent > 0);
        Assert.Equal(TeamTitleType.None, joined.DetailInfo.Title);
        Assert.Equal(aliceId, await OnLoop(() => population.CompanionOwner(botUid)));
        var profile = await alice.GetRoleAttributesAsync(botUid);
        Assert.NotEmpty(profile);

        // A LAN friend joins through the normal flow and receives the same roster, companion included.
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = bobId });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        var invite = await Next(bob, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await bob.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp { TeamId = invite.TeamTotalSimpleInfo.Info.TeamId, IsAgree = true, InviteGuid = aliceId });
        var roster = await Next(bob, TeamService.TeamAll, TeamTotalDetailInfo.Parser);
        Assert.Contains(botUid, roster.MemberList.Keys); Assert.Equal(3, roster.MemberList.Count);
        // Companions are never promoted: the leader cannot hand leadership to one.
        await alice.SendGameAsync(TeamService.TeamOperate, new TeamOperateReq { Oper = TeamOperationType.TeamOperTypeChangeLeader, TargetGuid = botUid });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TeamService.TeamOperate, TeamOperateResp.Parser)).Result);
        await Gm("bob", "!bots passive"); // not Bob's companion: nothing changes
        Assert.Equal(1, await OnLoop(() => population.CompanionCount));

        // The owner leaves: the companion leaves the roster with her and the remaining member sees both go.
        await alice.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq());
        Assert.Equal(0, (await Next(alice, TeamService.TeamLeave, LeaveTeamResp.Parser)).Result);
        await Next(bob, TeamService.TeamBroadcastLeave, BroadCastMemberLeave.Parser, m => m.Guid == aliceId);
        var botLeft = await Next(bob, TeamService.TeamBroadcastLeave, BroadCastMemberLeave.Parser, m => m.Guid == botUid);
        Assert.DoesNotContain(botUid, botLeft.MemberIndex.Keys);
        await Until(() => population.CompanionCount == 0);
        Assert.Equal(bobId, await OnLoop(() => _app.Services.GetRequiredService<TeamService>().TeamOf(bobId).Leader));
    }

    [Fact]
    public async Task NormalInviteOfAVisibleAmbientBotAutoAccepts()
    {
        var world = _app.Services.GetRequiredService<World>();
        var (alice, _) = await InWorld("Alice");
        await using var a = alice;
        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        Assert.Equal(0, (await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser)).Result);
        await Until(() => world.AllUnits().OfType<PopulationUnit>().Any());
        long botUid = await OnLoop(() => world.AllUnits().OfType<PopulationUnit>().First().Uid);
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = botUid });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        Assert.Equal(botUid, (await Next(alice, TeamService.TeamNewMember, BroadCastNewMemberEnter.Parser, m => m.DetailInfo.Guid == botUid)).DetailInfo.Guid);
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = botUid });
        Assert.Equal(ErrorCodes.InviteeInParty, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        // The leader can kick a companion through the normal roster operation; it is dismissed with its entry.
        await alice.SendGameAsync(TeamService.TeamOperate, new TeamOperateReq { Oper = TeamOperationType.TeamOperTypeKick, TargetGuid = botUid });
        Assert.Equal(0, (await Next(alice, TeamService.TeamOperate, TeamOperateResp.Parser)).Result);
        var population = _app.Services.GetRequiredService<PopulationService>();
        await Until(() => population.CompanionCount == 0 && world.AllUnits().All(u => u.Uid != botUid));
    }

    [Fact]
    public async Task SoloNormalInviteOfAVisibleBotCreatesThePartyAdmitsItAndGreetsInPartyChat()
    {
        var world = _app.Services.GetRequiredService<World>();
        var teams = _app.Services.GetRequiredService<TeamService>();
        var (alice, aliceId) = await InWorld("Alice");
        await using var a = alice;
        await Until(() => world.AllUnits().OfType<PopulationUnit>().Any());
        long botUid = await OnLoop(() => world.AllUnits().OfType<PopulationUnit>().First().Uid);
        // The card's Team Up needs teamstatus == None in the bot's role info (explicit zero, not absent).
        var results = (object[])(await alice.GetRoleFieldsAsync([botUid], "Base", "Team"))["Results"]!;
        var data = (IDictionary<object, object?>)((IDictionary<object, object?>)results[0])["Data"]!;
        Assert.Equal(new byte[] { 8, 0, 16, 0, 24, 0 }, (byte[])data["Team"]!);
        Assert.Equal(0, await OnLoop(() => teams.TeamOf(aliceId).TeamId));
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = botUid });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        Assert.Equal(0, (await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser)).Result);
        await Next(alice, TeamService.TeamNewMember, BroadCastNewMemberEnter.Parser, m => m.DetailInfo.Guid == botUid);
        var team = await OnLoop(() => teams.TeamOf(aliceId));
        Assert.Equal(aliceId, team.Leader); Assert.NotEqual(0, team.TeamId); Assert.Equal(1, await OnLoop(() => teams.CompanionOwner(botUid) == aliceId ? 1 : 0));
        var hello = await alice.Inbox.WaitAsync(e => e.IsGame(ChatService.TunnelDown) && e.Body.Length > 8
            && BitConverter.ToUInt32(e.Body, 0) == 4
            && NotifyNewTextChatMessage.Parser.ParseFrom(e.Body.AsSpan(8)).Msg.SenderId == botUid.ToString(), Wait);
        var line = NotifyNewTextChatMessage.Parser.ParseFrom(hello.Body.AsSpan(8)).Msg;
        Assert.Equal($"3_{team.TeamId}", line.ReceiveId); Assert.False(string.IsNullOrWhiteSpace(line.Content));
    }

    [Fact]
    public async Task NormalInviteBeyondTheCompanionCapIsRefusedAndDisabledCompanionsGiveASaneError()
    {
        var world = _app.Services.GetRequiredService<World>();
        var options = _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        var (alice, aliceId) = await InWorld("Alice");
        await using var a = alice;
        await Until(() => world.AllUnits().OfType<PopulationUnit>().Count() >= 2);
        long[] bots = await OnLoop(() => world.AllUnits().OfType<PopulationUnit>().Select(b => b.Uid).Take(2).ToArray());
        options.Population.Companions.Enabled = false;
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = bots[0] });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        Assert.Equal(0, await OnLoop(() => _app.Services.GetRequiredService<TeamService>().TeamOf(aliceId).TeamId)); // no party left behind
        options.Population.Companions.Enabled = true;
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = bots[0] });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = bots[1] }); // MaxPerPlayer = 1
        Assert.Equal(ErrorCodes.PartyFull, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
    }

    [Fact]
    public async Task CompanionRosterEntryDoesNotSurviveOwnerRelog()
    {
        var population = _app.Services.GetRequiredService<PopulationService>();
        var (alice, aliceId) = await InWorld("Alice");
        await Gm("alice", "!bots recruit");
        long botUid = (await Next(alice, TeamService.TeamNewMember, BroadCastNewMemberEnter.Parser, m => PopulationService.IsUid(m.DetailInfo.Guid))).DetailInfo.Guid;
        await alice.DisposeAsync();
        await Until(() => population.CompanionCount == 0); // 1 s disconnect grace, then dismissed from the roster
        var (again, _) = await InWorld("Alice", aliceId);
        await using var b = again;
        var roster = await Next(again, TeamService.TeamAll, TeamTotalDetailInfo.Parser);
        Assert.Equal([aliceId], roster.MemberList.Keys.ToArray());
        Assert.DoesNotContain(botUid, roster.MemberList.Keys);
    }
}
