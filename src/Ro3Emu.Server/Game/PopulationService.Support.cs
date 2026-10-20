using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>Per-companion support bookkeeping: derived kit, SP pool, per-skill cooldowns and counters (all in memory, inferred).</summary>
internal sealed class CompanionSupportState
{
    public SupportKit Kit = SupportKit.None;
    public double Sp = -1;
    public long SpAtMs, FightStartMs, NextCheckMs;
    public readonly Dictionary<int, long> ReadyMs = new();
    public int Heals, Buffs, Revives;
    public CompanionRole Role => Kit.Role;
}

/// <summary>
/// Support companions: a companion whose job owns heal/buff/revive skills heals hurt party members, keeps buffs on the
/// owner and resurrects dead humans out of combat. Skills come from the job's real skill tree (see
/// <see cref="CompanionSupportRules.Resolve"/>); costs and cooldowns are SkillConfig._kCost/_iCD. The bot SP pool,
/// regeneration, the 500 ms decision cadence and the combat gates are inferred offline policy.
/// </summary>
public sealed partial class PopulationService
{
    /// <summary>The combat side; tests substitute a recorder. Null (no combat service) turns support off.</summary>
    internal ICompanionSupportCaster? SupportCaster { get => supportOverride ?? combat; set => supportOverride = value; }
    private ICompanionSupportCaster? supportOverride;

    internal CompanionSupportState? SupportState(long uid) => companions.GetValueOrDefault(uid)?.Support;

    internal void SetCompanionKit(long uid, SupportKit kit)
    {
        if (companions.TryGetValue(uid, out var c)) c.Support.Kit = kit;
    }

    private SupportKit ResolveKit(int career, int level) => skills == null ? SupportKit.None
        : CompanionSupportRules.Resolve(data, skills.Tree(career).Select(t => skills.RealId(t.Group, 1)), level);

    /// <summary>SP cost and HP cost of a skill row, or null when it needs anything else (items, piles, energy).</summary>
    private (long Sp, long Hp)? SupportCost(int skill, long maxSp, long maxHp)
    {
        if (data.Row("SkillConfig", skill) is not { } row) return null;
        long sp = 0, hp = 0;
        if (row.TryGetProperty("_kCost", out var cost) && cost.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var tuple in cost.EnumerateArray())
            {
                if (tuple.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                var c = tuple.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
                if (c.Length < 4 || c[0] != 1) return null;
                long amount = c[3] + (c[1] == 2000 ? maxHp : maxSp) * c[2] / 10000;
                if (c[1] == 2001) sp += amount; else if (c[1] == 2000) hp += amount; else return null;
            }
        return (sp, hp);
    }

    private float SupportRange(int skill) => data.Row("SkillConfig", skill) is not { } row ? 0
        : GameData.Int(row, "_iRelease") == 2 && row.TryGetProperty("_kRange", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array && r.GetArrayLength() > 0
            ? (float)((GameData.Number(r[0]) ?? 300) / 100) : Math.Max(2, GameData.Int(row, "_iDistanceMax") / 100f) + 0.5f;

    private List<SupportMember> SupportMembers(Companion c, GameSession owner, Dictionary<long, GameSession> active, ICompanionSupportCaster caster)
    {
        var bot = c.Bot;
        var members = new List<SupportMember>();
        SupportMember Human(GameSession s, bool isOwner)
        {
            var (hp, max) = caster.Vitals(s);
            return new() { Session = s, Uid = s.Role!.Id, Hp = hp, MaxHp = max, Dead = s.Dead, Owner = isOwner, Distance2 = Distance2(bot.X, bot.Z, s.Position.X, s.Position.Z) };
        }
        members.Add(Human(owner, true));
        long ownerId = owner.Role!.Id;
        foreach (long id in teams?.MembersOf(ownerId) ?? [])
            if (id != ownerId && active.TryGetValue(id, out var s) && s.Role != null && !s.SceneLoading && s.RuntimeMap == owner.RuntimeMap)
                members.Add(Human(s, false));
        foreach (var other in companions.Values)
            if (other != c && other.Owner == c.Owner && !other.Bot.Removed && other.Bot.RuntimeMap == bot.RuntimeMap)
                members.Add(new() { Bot = other.Bot, Uid = other.Bot.Uid, Hp = other.Bot.Hp, MaxHp = other.Bot.MaxHp, Dead = other.Bot.Dead,
                    Distance2 = Distance2(bot.X, bot.Z, other.Bot.X, other.Bot.Z) });
        return members;
    }

    /// <summary>One support decision for a living companion; true when it started a cast this pass (it then skips attacking/walking).</summary>
    private async Task<bool> SupportAsync(Companion c, GameSession owner, Dictionary<long, GameSession> active, long now)
    {
        var st = c.Support; var bot = c.Bot;
        if (!CompanionOptions.Support || SupportCaster is not { } caster || st.Role != CompanionRole.Support || owner.Role == null) return false;
        if (st.Sp < 0) { st.Sp = bot.MaxSp; st.SpAtMs = now; }
        // (inferred) 2 % of max SP per second, so a healer that spends its pool slowly recovers.
        st.Sp = Math.Min(bot.MaxSp, st.Sp + Math.Max(0, now - st.SpAtMs) * bot.MaxSp / 50_000.0); st.SpAtMs = now;
        bool fighting = c.Target != null || caster.InFight(owner);
        st.FightStartMs = fighting ? (st.FightStartMs == 0 ? now : st.FightStartMs) : 0;
        if (now < st.NextCheckMs || now < bot.NextAttackMs) return false;
        st.NextCheckMs = now + 500;
        bool Affordable(int skill) => SupportCost(skill, bot.MaxSp, bot.MaxHp) is { } cost && st.Sp >= cost.Sp && bot.Hp > cost.Hp;
        var action = CompanionSupportRules.Plan(new SupportContext
        {
            Kit = st.Kit, Role = st.Role, HealBelowPercent = CompanionOptions.HealBelowPercent, Buffs = CompanionOptions.Buffs, Revive = CompanionOptions.Revive,
            OwnerInCombat = fighting, FightAgeMs = st.FightStartMs == 0 ? 0 : now - st.FightStartMs, Now = now,
            Ready = skill => now >= st.ReadyMs.GetValueOrDefault(skill) && Affordable(skill),
            Range = SupportRange, OwnerLacks = skill => caster.Lacks(owner, skill),
            Members = SupportMembers(c, owner, active, caster),
        });
        if (action.Kind == SupportKind.None || data.Row("SkillConfig", action.Skill) is not { } row || SupportCost(action.Skill, bot.MaxSp, bot.MaxHp) is not { } cost) return false;
        int actionMs = (int)Math.Clamp(GameData.Int(row, "_iActionTime"), 300, 3000);
        st.Sp -= cost.Sp; bot.Hp -= cost.Hp;
        st.ReadyMs[action.Skill] = now + Math.Max(GameData.Int(row, "_iCD"), actionMs);
        bot.NextAttackMs = now + actionMs + 500; // (inferred) half a second of recovery before the next action
        bot.Stop(now); bot.Route.Clear();
        if (action.Kind == SupportKind.Heal) st.Heals++; else if (action.Kind == SupportKind.Buff) st.Buffs++; else st.Revives++;
        if (cost.Hp > 0)
            await world.ToUnitViewersAsync(bot, CombatService.AoiAttrChange, new AOIAttrChange
                { EntityUuid = (ulong)bot.Uid, Property = { new AoIProperty { Idx = 2000, Data = bot.Hp, BaseData = bot.Hp } } });
        await caster.CastAsync(bot, action, now);
        return true;
    }

    /// <summary>!bots status line: each companion's derived role and what it has done (null when the player has none).</summary>
    internal async Task<string?> CompanionStatusAsync(GameSession session)
    {
        if (session.Role == null) return null;
        await gate.WaitAsync();
        try
        {
            var mine = companions.Values.Where(c => c.Owner == session.Role.Id).OrderBy(c => c.Slot).ToArray();
            if (mine.Length == 0) return null;
            return "Companions: " + string.Join("; ", mine.Select(c =>
            {
                var s = c.Support;
                string role = s.Role.ToString().ToLowerInvariant();
                string detail = s.Role == CompanionRole.Support
                    ? $" (heal {(s.Kit.Heal != 0 ? $"<{Math.Clamp(CompanionOptions.HealBelowPercent, 1, 99)}%" : "no")}, buffs {s.Kit.Buffs.Length}, revive {(s.Kit.Revive != 0 ? "yes" : "no")}; SP {Math.Max(0, (long)s.Sp)}/{c.Bot.MaxSp}; casts {s.Heals}/{s.Buffs}/{s.Revives})" : "";
                return $"{c.Bot.Name} L{c.Bot.Level} job {c.Bot.Career} {role}{detail}";
            })) + ".";
        }
        finally { gate.Release(); }
    }
}
