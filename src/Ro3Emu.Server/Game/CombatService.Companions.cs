using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>
    /// A companion's support hit on a monster its owner is fighting. Damage uses the shared formula and 31100/31101
    /// packets but never takes the monster below 1 HP: the human lands the finishing blow, so the existing kill
    /// settlement grants the one reward to that human and nothing is ever settled for the ephemeral bot (inferred policy).
    /// </summary>
    internal async Task CompanionAttackAsync(PopulationUnit bot, MonsterUnit monster, int skillId, long now, Func<MonsterUnit, bool> valid)
    {
        if (bot.Removed || bot.Dead || !valid(monster) || _data.Row("SkillConfig", skillId) is not { } row) return;
        float range = Math.Max(2, GameData.Int(row, "_iDistanceMax") / 100f);
        if (MonsterService.Distance(bot.X, bot.Z, monster.X, monster.Z) > range + 0.5f) return;
        bot.Face = MonsterService.Angle(bot.X, bot.Z, monster.X, monster.Z);
        int duration = (int)Math.Clamp(GameData.Int(row, "_iActionTime"), 300, 3000);
        bot.NextAttackMs = Math.Max(bot.NextAttackMs, now + duration);
        if (skillId != bot.NormalSkill) bot.SkillReadyMs = Math.Max(bot.SkillReadyMs, now + Math.Max(8000, GameData.Int(row, "_iCD")));
        long cast = ++_sessionIds;
        await _world.ToUnitViewersAsync(bot, UseInSkill, new UseInSkillInfo
        {
            SrcID = bot.Uid, SkillID = skillId, TargetId = monster.Uid, TargetPos = monster.Pos,
            Face = bot.Face, SessionId = cast, SkillMs = duration, StartTime = now, PlayAct = true,
        });
        ScheduleRuntime(bot.WorldScene, TimeSpan.FromMilliseconds(duration * 0.4), async () =>
        {
            if (bot.Removed || bot.Dead || !valid(monster) || MonsterService.Distance(bot.X, bot.Z, monster.X, monster.Z) > range + 2) return;
            var attacker = new Fighter(bot.Attack, bot.MagicAttack, bot.Hit, bot.Critical, bot.Level, 1);
            var defender = new Fighter(0, 0, 0, 0, monster.Template.Level, monster.Template.Element)
            { Def = monster.Template.Def, Mdef = monster.Template.Mdef, Flee = monster.Template.Flee };
            var (damage, result) = Damage(attacker, defender, row);
            damage = Math.Clamp(damage, 0, monster.Hp - 1);
            monster.Hp -= damage;
            await _world.ToUnitViewersAsync(bot, SkillHit, new SkillHitInfo
            {
                SrcID = bot.Uid, SkillId = skillId, SessionId = cast, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")), SrcElement = 1,
                Dsts = { new DamageInfo { DstID = monster.Uid, Damage = damage, HitResult = result, DstElement = monster.Template.Element } },
            });
            if (damage > 0) await _world.ToUnitViewersAsync(monster, AoiAttrChange, MonsterService.Hp(monster));
        });
    }
}
