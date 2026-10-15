using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>Simulated solo combat uses the normal formula/packets but has no economic or quest recipient.</summary>
    internal async Task PopulationAttackAsync(PopulationUnit bot, MonsterUnit monster, int skillId, long now)
    {
        if (bot.Removed || bot.Dead || bot.Target != monster || !PopulationService.PopulationCanHunt(bot, monster)
            || data.Row("SkillConfig", skillId) is not { } row) return;
        float range = Math.Max(2, GameData.Int(row, "_iDistanceMax") / 100f);
        if (MonsterService.Distance(bot.X, bot.Z, monster.X, monster.Z) > range + 0.5f) return;
        monsters.Engage(monster, bot); // Claim before the first await. Other bots must choose another monster.
        bot.Face = MonsterService.Angle(bot.X, bot.Z, monster.X, monster.Z);
        int duration = (int)Math.Clamp(GameData.Int(row, "_iActionTime"), 300, 3000);
        bot.NextAttackMs = Math.Max(bot.NextAttackMs, now + duration);
        if (skillId != bot.NormalSkill) bot.SkillReadyMs = Math.Max(bot.SkillReadyMs, now + Math.Max(8000, GameData.Int(row, "_iCD")));
        long cast = ++_sessionIds;
        await world.ToUnitViewersAsync(bot, UseInSkill, new UseInSkillInfo
        {
            SrcID = bot.Uid, SkillID = skillId, TargetId = monster.Uid, TargetPos = monster.Pos,
            Face = bot.Face, SessionId = cast, SkillMs = duration, StartTime = now, PlayAct = true,
        });
        ScheduleRuntime(bot.WorldScene, TimeSpan.FromMilliseconds(duration * 0.4), async () =>
        {
            if (bot.Removed || bot.Dead || bot.Target != monster || !PopulationService.PopulationCanHunt(bot, monster)
                || MonsterService.Distance(bot.X, bot.Z, monster.X, monster.Z) > range + 2) return;
            // Attribute progression is inferred offline policy; damage math is shared with real combat.
            var attacker = new Fighter(bot.Attack, bot.MagicAttack, bot.Hit, bot.Critical, bot.Level, 1);
            var defender = new Fighter(0, 0, 0, 0, monster.Template.Level, monster.Template.Element)
                { Def = monster.Template.Def, Mdef = monster.Template.Mdef, Flee = monster.Template.Flee };
            var (damage, result) = Damage(attacker, defender, row);
            monster.Hp = Math.Max(0, monster.Hp - damage);
            bot.TargetExpectedHp = monster.Hp;
            bool killed = monster.Hp == 0;
            // Reserve death synchronously; player/tower casts already reject zero HP before reward admission.
            if (killed) { monster.Dead = true; bot.HuntKills++; bot.Target = null; }
            await world.ToUnitViewersAsync(bot, SkillHit, new SkillHitInfo
            {
                SrcID = bot.Uid, SkillId = skillId, SessionId = cast, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")), SrcElement = 1,
                Dsts = { new DamageInfo { DstID = monster.Uid, Damage = damage, HitResult = result, DstElement = monster.Template.Element, IsDead = killed } },
            });
            await world.ToUnitViewersAsync(monster, AoiAttrChange, MonsterService.Hp(monster));
            if (killed)
            {
                // KillAsync owns corpse cleanup and public spawn timers, with no player reward settlement.
                monster.Dead = false;
                await monsters.KillAsync(monster);
            }
        });
    }

    internal async Task PopulationHealAsync(PopulationUnit bot, long now)
    {
        if (bot.Dead || bot.Removed || data.Row("SkillConfig", bot.HealSkill) is not { } row) return;
        long cast = ++_sessionIds;
        int duration = (int)Math.Clamp(GameData.Int(row, "_iActionTime"), 300, 3000);
        bot.SkillReadyMs = now + Math.Max(10000, GameData.Int(row, "_iCD"));
        await world.ToUnitViewersAsync(bot, UseInSkill, new UseInSkillInfo
        {
            SrcID = bot.Uid, SkillID = bot.HealSkill, TargetId = bot.Uid, TargetPos = bot.Pos, Face = bot.Face,
            SessionId = cast, SkillMs = duration, StartTime = now, PlayAct = true,
        });
        ScheduleRuntime(bot.WorldScene, TimeSpan.FromMilliseconds(duration * 0.4), async () =>
        {
            if (bot.Dead || bot.Removed) return;
            long amount = Math.Min(bot.MaxHp - bot.Hp, Math.Max(1, (long)Numbers(row, "_kDamageParam2").FirstOrDefault()));
            bot.Hp += amount;
            await world.ToUnitViewersAsync(bot, SkillHit, new SkillHitInfo
            {
                SrcID = bot.Uid, SkillId = bot.HealSkill, SessionId = cast,
                Dsts = { new DamageInfo { DstID = bot.Uid, Damage = amount, DamageType = DamageType.Heal } },
            });
            await world.ToUnitViewersAsync(bot, AoiAttrChange, new AOIAttrChange
                { EntityUuid = (ulong)bot.Uid, Property = { new AoIProperty { Idx = 2000, Data = bot.Hp, BaseData = bot.Hp } } });
        });
    }

    private async Task MonsterAttackPopulationAsync(MonsterUnit monster, PopulationUnit bot)
    {
        if (bot.Dead || bot.Removed || monster.Dead || monster.PopulationTarget != bot || monster.Target != null) return;
        long cast = ++_sessionIds;
        await world.ToUnitViewersAsync(monster, UseInSkill, new UseInSkillInfo
        {
            SrcID = monster.Uid, SkillID = monster.Template.NormalSkill, TargetId = bot.Uid, TargetPos = bot.Pos,
            Face = monster.Face, SessionId = cast, SkillMs = monster.Template.SkillMs, StartTime = Now, PlayAct = true,
        });
        ScheduleRuntime(monster.WorldScene, TimeSpan.FromSeconds(monster.Template.HitPointSeconds), async () =>
        {
            if (bot.Dead || bot.Removed || monster.Dead || monster.Hp <= 0 || monster.PopulationTarget != bot || monster.Target != null
                || MonsterService.Distance(monster.X, monster.Z, bot.X, bot.Z) > monster.Template.AttackRange + 2) return;
            var attacker = new Fighter(monster.Template.Atk, monster.Template.Matk, monster.Template.Hit, monster.Template.Cri, monster.Template.Level, monster.Template.Element);
            var defender = new Fighter(0, 0, 0, 0, bot.Level, 1) { Def = bot.Defence, Mdef = bot.MagicDefence, Flee = bot.Flee };
            var (damage, result) = data.Row("SkillConfig", monster.Template.NormalSkill) is { } row
                ? Damage(attacker, defender, row) : (Math.Max(1, monster.Template.Atk), DamageHitResult.Normal);
            bot.Hp = Math.Max(0, bot.Hp - damage);
            if (bot.Dead) { bot.Stop(Now); bot.RespawnMs = Now + 30000; }
            await world.ToUnitViewersAsync(monster, SkillHit, new SkillHitInfo
            {
                SrcID = monster.Uid, SkillId = monster.Template.NormalSkill, SessionId = cast, SrcElement = monster.Template.Element,
                Dsts = { new DamageInfo { DstID = bot.Uid, Damage = damage, HitResult = result, DstElement = 1, IsDead = bot.Dead } },
            });
            await world.ToUnitViewersAsync(bot, AoiAttrChange, new AOIAttrChange
                { EntityUuid = (ulong)bot.Uid, Property = { new AoIProperty { Idx = 2000, Data = bot.Hp, BaseData = bot.Hp } } });
            if (bot.Dead) await world.ToUnitViewersAsync(bot, EntityBaseChange, bot.Entity());
        });
    }
}
