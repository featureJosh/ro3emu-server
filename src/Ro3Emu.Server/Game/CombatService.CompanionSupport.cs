using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService : ICompanionSupportCaster
{
    public (long Hp, long MaxHp) Vitals(GameSession s)
    {
        if (s.Progress == null || s.Role == null) return (0, 0);
        var stats = character.Stats(s);
        return (stats[2000], stats[101]);
    }

    /// <summary>Battling status bit (eObjectStateBattling), cleared after PublicParamConfig.OutOfCombatTime.</summary>
    public bool InFight(GameSession s) => (s.StatusBits & FightBit) != 0;

    public bool Lacks(GameSession s, int skill) =>
        CompanionSupportRules.BuffOf(data, skill) is var (buff, _) && buff != 0 && !s.Buffs.Any(b => b.ConfigId == buff);

    /// <summary>
    /// A companion's support cast: the usual 31100 cast notice, then the effect 40 % into the action like the other bot
    /// casts. Heal uses the player formula (MATK x _kDamageParam1[0] / 10000 + _kDamageParam2[0], Effects.cs); buffs go
    /// through BuffService like a player's Blessing; revive is the immediate World-Tree-style path (claim, commit,
    /// publish) with the dungeon allowance check, never the paid item revive (inferred: a bot has no invitation UI).
    /// </summary>
    public async Task CastAsync(PopulationUnit bot, SupportAction action, long now)
    {
        if (bot.Removed || bot.Dead || action.Target == null || data.Row("SkillConfig", action.Skill) is not { } row) return;
        long cast = ++_sessionIds;
        int duration = (int)Math.Clamp(GameData.Int(row, "_iActionTime"), 300, 3000);
        long targetId = action.Kind == SupportKind.Buff ? bot.Uid : action.Target.Uid;
        var targetPos = action.Target.Session?.Position ?? action.Target.Bot?.Pos ?? bot.Pos;
        bot.Face = MonsterService.Angle(bot.X, bot.Z, targetPos.X, targetPos.Z);
        await world.ToUnitViewersAsync(bot, UseInSkill, new UseInSkillInfo
        {
            SrcID = bot.Uid, SkillID = action.Skill, TargetId = targetId, TargetPos = targetPos, Face = bot.Face,
            SessionId = cast, SkillMs = duration, StartTime = now, PlayAct = true,
        });
        ScheduleRuntime(bot.WorldScene, TimeSpan.FromMilliseconds(duration * 0.4), async () =>
        {
            if (bot.Removed || bot.Dead) return;
            switch (action.Kind)
            {
                case SupportKind.Heal: await ApplyCompanionHealAsync(bot, action, row, cast); break;
                case SupportKind.Buff: await ApplyCompanionBuffAsync(bot, action, row); break;
                case SupportKind.Revive: await ApplyCompanionReviveAsync(bot, action, row, cast); break;
            }
        });
    }

    private async Task ApplyCompanionHealAsync(PopulationUnit bot, SupportAction action, System.Text.Json.JsonElement row, long cast)
    {
        var target = action.Target!;
        double multiplier = Numbers(row, "_kDamageParam1").FirstOrDefault(), flat = Numbers(row, "_kDamageParam2").FirstOrDefault();
        long amount = (long)Math.Max(0, Math.Round(bot.MagicAttack * multiplier / 10000 + flat)), actual;
        if (target.Session is { } s)
        {
            if (s.Role == null || s.Dead || s.SceneLoading || s.WorldScene != bot.WorldScene || s.Progress == null) return;
            var (hp, max) = Vitals(s);
            actual = Math.Min(amount, Math.Max(0, max - hp));
            await character.SetHpSpAsync(s, hp + amount, null);
        }
        else if (target.Bot is { } ally)
        {
            if (ally.Removed || ally.Dead || ally.WorldScene != bot.WorldScene) return;
            actual = Math.Min(amount, Math.Max(0, ally.MaxHp - ally.Hp));
            ally.Hp += actual;
            await world.ToUnitViewersAsync(ally, AoiAttrChange, new AOIAttrChange
                { EntityUuid = (ulong)ally.Uid, Property = { new AoIProperty { Idx = 2000, Data = ally.Hp, BaseData = ally.Hp } } });
        }
        else return;
        await world.ToUnitViewersAsync(bot, SkillHit, new SkillHitInfo
        {
            SrcID = bot.Uid, SkillId = action.Skill, SessionId = cast, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")),
            SrcElement = (int)Math.Max(1, GameData.Int(row, "_iEleType")),
            Dsts = { new DamageInfo { DstID = target.Uid, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 } },
        });
    }

    private async Task ApplyCompanionBuffAsync(PopulationUnit bot, SupportAction action, System.Text.Json.JsonElement row)
    {
        var (buff, duration) = CompanionSupportRules.BuffOf(data, action.Skill);
        if (buff == 0) return;
        foreach (var member in new[] { action.Target! }.Concat(action.Also ?? []))
            if (member.Session is { Role: not null, Dead: false, SceneLoading: false } s && s.WorldScene == bot.WorldScene)
                await buffs.AddAsync(s, buff, duration, bot.Uid);
    }

    private async Task ApplyCompanionReviveAsync(PopulationUnit bot, SupportAction action, System.Text.Json.JsonElement row, long cast)
    {
        if (action.Target?.Session is not { } s || CompanionReviveError(bot, s) != 0 || !TryClaimRevive(s)) return;
        try
        {
            // WorldCoreService.OfferReviveAsync reads the same [2, rate, property] rows: 101 = HP rate, 102 = SP rate (10000 = full).
            long hpRate = 10000, spRate = 10000;
            foreach (var extra in Tuples(row, "_kDamageParamExtra"))
                if (extra.GetArrayLength() >= 3 && GameData.Number(extra[0]) == 2)
                {
                    if (GameData.Number(extra[2]) == 101) hpRate = (long)(GameData.Number(extra[1]) ?? 0);
                    if (GameData.Number(extra[2]) == 102) spRate = (long)(GameData.Number(extra[1]) ?? 0);
                }
            if (!CommitReviveClaim(s, hpRate, spRate)) return;
            // A role handover during the claim leaves nobody to publish to (WorldCoreService.ItemReviveAsync); reconnect reloads it.
            if (s.Role != null) await PublishReviveAsync(s, null);
        }
        finally { ReleaseReviveClaim(s); }
    }

    /// <summary>Revive guards a companion must pass: attached role, loaded public scene, no dungeon allowance involved, no 16 restraint.</summary>
    internal int CompanionReviveError(PopulationUnit bot, GameSession s) =>
        s.Role == null || !s.Dead || s.SceneLoading || s.WorldScene != bot.WorldScene || s.InstanceId != 0 || s.DungeonAttempt != null
        || buffs.HasForbidden(s, 16) ? ErrorCodes.SkillReviveNotAllowed : ReviveCheck?.Invoke(s) ?? 0;
}
