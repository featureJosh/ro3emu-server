using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Companion support role: kit/role derivation from skill rows, the heal/buff/revive decision rules, and the live
/// companion pass with a recording caster. Rows mirror the shipped SkillConfig/skill templates they cite.
/// </summary>
public sealed class PopulationSupportTests : IDisposable
{
    private const int Heal = 1400701, Blessing = 1400101, Revive = 1411201, PaidRevive = 2200101, Provoke = 1100401, SelfHeal = 1000301;
    private readonly string root = Path.Combine(Path.GetTempPath(), "ro3-support-" + Guid.NewGuid().ToString("N"));
    private readonly GameLoop loop = new(NullLogger<GameLoop>.Instance);
    private readonly World world;
    private readonly ServerOptions options;
    private readonly GameData data;
    private readonly PopulationService population;
    private readonly Recorder caster = new();

    public PopulationSupportTests()
    {
        CompanionFixture.Write(root, 1);
        Directory.CreateDirectory(Path.Combine(root, "skills"));
        // Heal 14007 (1400701): _iDmgType 2, _iRelease 1, enemy-undead + friendly selectors, SP cost, 5 s CD.
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """
            {"1400701":{"_kScriptLogic":"Heal","_iRelease":1,"_iDmgType":2,"_iScriptShow":14007,"_iDistanceMax":2000,"_iActionTime":500,"_iCD":5000,
              "_kCost":[[1,2001,0,30]],"_kDamageParam1":[9000,4100],"_kDamageParam2":[135,30]},
             "1400101":{"_kScriptLogic":"Blessing","_iRelease":2,"_iDmgType":0,"_iScriptShow":14001,"_iDistanceMax":0,"_iActionTime":500,"_iCD":5000,
              "_kCost":[[1,2001,0,10]],"_kBuff1":[[1400101,10000,120000]],"_kRange":[3000,0,0,0]},
             "1411201":{"_kScriptLogic":"Resurrection","_iRelease":1,"_iDmgType":0,"_iScriptShow":14112,"_iDistanceMax":3000,"_iActionTime":500,"_iCD":30000,
              "_kCost":[[1,2001,0,5]],"_kDamageParamExtra":[[2,3000,101,3],[2,3000,102,3]]},
             "2200101":{"_kScriptLogic":"Resurrection","_iRelease":1,"_iDmgType":0,"_iScriptShow":14112,"_iDistanceMax":3000,"_iActionTime":500,"_iCD":0,
              "_kCost":[[2,1002004,1]]},
             "1100401":{"_kScriptLogic":"Provoke","_iRelease":1,"_iDmgType":0,"_iScriptShow":11004,"_iDistanceMax":900,"_iActionTime":500,"_iCD":8000},
             "1000301":{"_kScriptLogic":"JinJiZhiLiao","_iRelease":2,"_iDmgType":1,"_iScriptShow":10003,"_iDistanceMax":200,"_iActionTime":500,"_iCD":10000,"_kDamageParam2":[150]}}
            """);
        void Template(int id, string selectors) => File.WriteAllText(Path.Combine(root, "skills", $"skill_{id}.json"), "{\"" + id + "\":{\"m_kSelectInfo\":" + selectors + "}}");
        Template(14007, """[{"m_kSelectRaceType":16,"m_kSelectSubType":-1,"m_kSelectType":1},{"m_kSelectRaceType":-1,"m_kSelectSubType":0,"m_kSelectType":2}]""");
        Template(14001, """[{"m_kSelectRaceType":-1,"m_kSelectSubType":-1,"m_kSelectType":2}]""");
        Template(14112, """[{"m_kSelectRaceType":-1,"m_kSelectSubType":-1,"m_kSelectType":32}]""");
        Template(10003, """[{"m_kSelectRaceType":-1,"m_kSelectSubType":2,"m_kSelectType":2}]""");
        options = new ServerOptions
        {
            DataDir = root, Population = new()
            {
                Enabled = false, PerMap = 6, MaxTotal = 10, MaxSpawnsPerTick = 100,
                Maps = new() { [1] = new() { AreaLevel = 5, MinLevel = 5, MaxLevel = 5 } }, Companions = new() { Enabled = true, DisconnectGraceSeconds = 60 }
            }
        };
        var wrapped = Options.Create(options);
        data = new GameData(wrapped, NullLogger<GameData>.Instance);
        world = new World(new NullSink(), loop, NullLogger<World>.Instance);
        population = new PopulationService(world, new WorldData(wrapped, data, NullLogger<WorldData>.Instance), data, loop, wrapped) { SupportCaster = caster };
    }

    // ---------------------------------------------------------------- kit and role derivation

    [Fact]
    public void KitComesFromSkillRowsAndPaidReviveIsNeverUsed()
    {
        var kit = CompanionSupportRules.Resolve(data, [Heal, Blessing, Revive, PaidRevive, SelfHeal], 20);
        Assert.Equal(Heal, kit.Heal); Assert.Equal([Blessing], kit.Buffs); Assert.Equal(Revive, kit.Revive);
        Assert.Equal(CompanionRole.Support, kit.Role);
        // Only the item-cost Resurrection (the paid revive row) is offered: no kit, so no bot revive at all.
        Assert.Equal(0, CompanionSupportRules.Resolve(data, [PaidRevive], 20).Revive);
        // Novice emergency heal (rel 2, self only) is not an ally heal: not a support job.
        Assert.Equal(CompanionRole.Damage, CompanionSupportRules.Resolve(data, [SelfHeal], 20).Role);
        Assert.Equal(CompanionRole.Tank, CompanionSupportRules.Resolve(data, [Provoke], 20).Role);
        Assert.Equal(CompanionRole.Damage, CompanionSupportRules.Resolve(data, [], 20).Role);
    }

    // ---------------------------------------------------------------- pure decision rules

    private static SupportMember Member(long uid, long hp, long max = 1000, bool owner = false, bool dead = false, float d2 = 4, bool human = true) =>
        new() { Uid = uid, Hp = hp, MaxHp = max, Owner = owner, Dead = dead, Distance2 = d2, Session = human ? new GameSession(null!, null!) : null };

    private static SupportContext Context(SupportKit kit, IReadOnlyList<SupportMember> members, bool combat = false, bool lacks = true, Func<int, bool>? ready = null,
        CompanionRole? role = null, int percent = 60, long fightAge = 0) => new()
        {
            Kit = kit, Role = role ?? kit.Role, HealBelowPercent = percent, OwnerInCombat = combat, FightAgeMs = fightAge, Now = 1,
            Ready = ready ?? (_ => true), Range = id => id == Blessing ? 30 : 20, OwnerLacks = _ => lacks, Members = members,
        };

    private static readonly SupportKit Priest = new(Heal, [Blessing], Revive, false);

    [Fact]
    public void HealTriggersBelowThresholdAndNotAbove()
    {
        var below = CompanionSupportRules.Plan(Context(Priest, [Member(1, 599, owner: true)], lacks: false));
        Assert.Equal(SupportKind.Heal, below.Kind); Assert.Equal(Heal, below.Skill); Assert.Equal(1, below.Target!.Uid);
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, [Member(1, 600, owner: true)], lacks: false)).Kind); // exactly 60 %: not below
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, [Member(1, 900, owner: true)], lacks: false)).Kind);
        // The threshold is configurable and the neediest member goes first; out-of-reach and dead members are ignored.
        Assert.Equal(SupportKind.Heal, CompanionSupportRules.Plan(Context(Priest, [Member(1, 800, owner: true)], lacks: false, percent: 90)).Kind);
        var pick = CompanionSupportRules.Plan(Context(Priest, [Member(1, 500, owner: true), Member(2, 100, human: false), Member(3, 50, d2: 900), Member(4, 0, dead: true)], lacks: false));
        Assert.Equal(2, pick.Target!.Uid);
    }

    [Fact]
    public void HealRespectsCooldownAndCost()
    {
        var hurt = new[] { Member(1, 100, owner: true) };
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, hurt, lacks: false, ready: id => id != Heal)).Kind);
        Assert.Equal(SupportKind.Heal, CompanionSupportRules.Plan(Context(Priest, hurt, lacks: false, ready: id => id == Heal)).Kind);
    }

    [Fact]
    public void ReviveOnlyForADeadHumanOutOfCombat()
    {
        var dead = new[] { Member(1, 0, owner: true, dead: true) };
        var revive = CompanionSupportRules.Plan(Context(Priest, dead, lacks: false));
        Assert.Equal(SupportKind.Revive, revive.Kind); Assert.Equal(Revive, revive.Skill);
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, dead, combat: true, lacks: false)).Kind);
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, [Member(1, 1000, owner: true)], lacks: false)).Kind); // nobody is dead
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, [Member(9, 0, dead: true, human: false)], lacks: false)).Kind); // companions come back on their own
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, [Member(1, 0, owner: true, dead: true, d2: 2500)], lacks: false)).Kind); // beyond 30 units
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(new SupportKit(Heal, [], 0, false), dead, lacks: false)).Kind); // no revive skill
    }

    [Fact]
    public void BuffsGoOnTheOwnerOutOfCombatOrAtFightStartOnly()
    {
        var owner = new[] { Member(1, 1000, owner: true) };
        var buff = CompanionSupportRules.Plan(Context(Priest, owner));
        Assert.Equal(SupportKind.Buff, buff.Kind); Assert.Equal(Blessing, buff.Skill);
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, owner, lacks: false)).Kind); // already buffed
        Assert.Equal(SupportKind.Buff, CompanionSupportRules.Plan(Context(Priest, owner, combat: true, fightAge: 2000)).Kind);
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, owner, combat: true, fightAge: 60_000)).Kind);
    }

    [Fact]
    public void DamageAndTankCompanionsNeverHealBuffOrRevive()
    {
        var party = new[] { Member(1, 1, owner: true), Member(2, 0, dead: true) };
        foreach (var kit in new[] { SupportKit.None, new SupportKit(0, [], 0, true) })
            Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(kit, party)).Kind);
        // Even a kit that owns support skills stays quiet when its role is forced to damage.
        Assert.Equal(SupportKind.None, CompanionSupportRules.Plan(Context(Priest, party, role: CompanionRole.Damage)).Kind);
    }

    // ---------------------------------------------------------------- the live companion pass

    private async Task<(GameSession Owner, PopulationUnit Bot)> Companion(SupportKit kit)
    {
        var owner = new GameSession(null!, null!)
        {
            Role = new Role(1, "test", 1, "Player1", 1, 1000, 5, 0, 0, 0, 1, 128, 0, 128, 0),
            SceneEntry = 1, LineNumber = 1, Position = new Vec3 { X = 128, Z = 128 },
        };
        await world.JoinAsync(owner);
        Assert.Null(await population.RecruitAsync(owner));
        var bot = world.AllUnits().OfType<PopulationUnit>().Single();
        bot.Viewers.Clear(); bot.GearAttributes[102] = 0; // MaxSp = 50
        population.SetCompanionKit(bot.Uid, kit);
        caster.Hp = 1000; caster.MaxHp = 1000;
        return (owner, bot);
    }

    [Fact]
    public async Task SupportCompanionHealsOnlyBelowThresholdAndHonoursCooldownAndSp()
    {
        var (_, bot) = await Companion(Priest);
        long now = 10_000;
        caster.Lacking = false; caster.Hp = 700; await population.UpdateCompanionsAsync(now);
        Assert.Empty(caster.Casts); // 70 % is above the 60 % default
        caster.Hp = 500; await population.UpdateCompanionsAsync(now += 1000);
        Assert.Equal([SupportKind.Heal], caster.Casts.Select(c => c.Kind)); // 30 SP of 50
        await population.UpdateCompanionsAsync(now += 1000); await population.UpdateCompanionsAsync(now += 1000);
        Assert.Single(caster.Casts); // 5 s cooldown still running
        await population.UpdateCompanionsAsync(now += 4000); // cooldown over, but only ~25 of the 30 SP have regenerated
        Assert.Single(caster.Casts);
        await population.UpdateCompanionsAsync(now += 6000);
        Assert.Equal(2, caster.Casts.Count);
        var state = population.SupportState(bot.Uid)!;
        Assert.Equal(2, state.Heals); Assert.True(state.Sp < 30);
    }

    [Fact]
    public async Task SupportCompanionRevivesOnlyAfterTheFightAndKeepsABuffUp()
    {
        var (owner, _) = await Companion(Priest);
        long now = 10_000;
        owner.Dead = true; caster.Fight = true;
        await population.UpdateCompanionsAsync(now);
        Assert.Empty(caster.Casts); // owner still in combat: no resurrection
        caster.Fight = false;
        await population.UpdateCompanionsAsync(now += 1000);
        Assert.Equal([SupportKind.Revive], caster.Casts.Select(c => c.Kind));
        owner.Dead = false;
        await population.UpdateCompanionsAsync(now += 1000);
        Assert.Equal([SupportKind.Revive, SupportKind.Buff], caster.Casts.Select(c => c.Kind));
        caster.Lacking = false;
        await population.UpdateCompanionsAsync(now += 10_000);
        Assert.Equal(2, caster.Casts.Count); // buff is still on the owner
    }

    [Fact]
    public async Task DamageRoleCompanionNeverHealsAndSupportCanBeSwitchedOff()
    {
        var (owner, bot) = await Companion(SupportKit.None);
        caster.Hp = 10; owner.Dead = false;
        for (long now = 10_000; now < 40_000; now += 1000) await population.UpdateCompanionsAsync(now);
        Assert.Empty(caster.Casts);
        Assert.Contains("damage", await population.CompanionStatusAsync(owner));
        population.SetCompanionKit(bot.Uid, Priest);
        Assert.Contains("support (heal <60%", await population.CompanionStatusAsync(owner));
        options.Population.Companions.Support = false;
        await population.UpdateCompanionsAsync(50_000);
        Assert.Empty(caster.Casts);
    }

    [Fact]
    public void RoleOptionDefaultsAndPreference()
    {
        var defaults = new PopulationCompanionOptions();
        Assert.True(defaults.Support); Assert.Equal(60, defaults.HealBelowPercent); Assert.True(defaults.Buffs); Assert.True(defaults.Revive);
        Assert.Equal("auto", defaults.PreferredRole);
        Assert.True(CompanionSupportRules.WantsRole("auto", CompanionRole.Damage));
        Assert.True(CompanionSupportRules.WantsRole("Support", CompanionRole.Support));
        Assert.False(CompanionSupportRules.WantsRole("support", CompanionRole.Tank));
    }

    public void Dispose() { loop.Dispose(); Directory.Delete(root, true); }

    private sealed class Recorder : ICompanionSupportCaster
    {
        public long Hp = 1000, MaxHp = 1000;
        public bool Fight, Lacking = true;
        public readonly List<SupportAction> Casts = new();
        public (long Hp, long MaxHp) Vitals(GameSession s) => (s.Dead ? 0 : Hp, MaxHp);
        public bool InFight(GameSession s) => Fight;
        public bool Lacks(GameSession s, int skill) => Lacking;
        public Task CastAsync(PopulationUnit bot, SupportAction action, long now) { Casts.Add(action); return Task.CompletedTask; }
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
