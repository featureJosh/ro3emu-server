using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>What a companion is for. Derived from its job's skill tree, never stored (inferred offline-server policy).</summary>
public enum CompanionRole { Damage, Tank, Support }

/// <summary>
/// The support skills a job owns, found in the real SkillConfig/skill-template rows (not hard-coded ids). Heal is
/// an ally-targetable recovery (client Heal rows 14007/11405: _iDmgType 2, _iRelease 1, selector type 1|2 with sub-type
/// 0), Buffs are party-wide self-centred buffs (Blessing 14001, IncreaseAGI 14010: _iRelease 2, selector type 2, _kBuff1),
/// Revive is a dead-ally Resurrection paid in SP only (Priest 14112: selector type 32; item-cost rows such as 22001
/// are the paid revive and are never used by a bot).
/// </summary>
public sealed record SupportKit(int Heal, int[] Buffs, int Revive, bool Provokes)
{
    public static readonly SupportKit None = new(0, [], 0, false);
    public bool IsSupport => Heal != 0 || Revive != 0 || Buffs.Length > 0;
    public CompanionRole Role => IsSupport ? CompanionRole.Support : Provokes ? CompanionRole.Tank : CompanionRole.Damage;
}

/// <summary>One party member a support companion may act on: a human session or another companion actor.</summary>
public sealed class SupportMember
{
    public GameSession? Session;
    public PopulationUnit? Bot;
    public long Uid, Hp, MaxHp;
    public bool Dead, Owner;
    public float Distance2;
    public bool Human => Session != null;
}

public enum SupportKind { None, Heal, Buff, Revive }
public sealed record SupportAction(SupportKind Kind, int Skill = 0, SupportMember? Target = null, IReadOnlyList<SupportMember>? Also = null)
{
    public static readonly SupportAction Nothing = new(SupportKind.None);
}

/// <summary>Everything the decision needs; plain data so the rules are testable without a combat service.</summary>
public sealed class SupportContext
{
    public required SupportKit Kit;
    public CompanionRole Role = CompanionRole.Support;
    public int HealBelowPercent = 60;
    public bool Buffs = true, Revive = true;
    public bool OwnerInCombat;
    /// <summary>A buff is only started out of combat or in the first <see cref="FightStartWindowMs"/> of a fight.</summary>
    public long FightAgeMs;
    public required long Now;
    public required Func<int, bool> Ready;       // cooldown elapsed and SP/HP affordable for this skill id
    public required Func<int, float> Range;      // skill reach in map units
    public required Func<int, bool> OwnerLacks;  // the owner does not carry the buff of this skill
    public required IReadOnlyList<SupportMember> Members;
}

/// <summary>The combat side of companion support: reads vitals/fight state and publishes the real heal, buff and revive packets.</summary>
internal interface ICompanionSupportCaster
{
    (long Hp, long MaxHp) Vitals(GameSession s);
    bool InFight(GameSession s);
    bool Lacks(GameSession s, int skill);
    Task CastAsync(PopulationUnit bot, SupportAction action, long now);
}

internal static class CompanionSupportRules
{
    internal const long FightStartWindowMs = 10_000;
    /// <summary>Priority order for buffs; only the first two the job owns are kept (Blessing/IncreaseAGI rows 1400101/1401001).</summary>
    private static readonly string[] BuffScripts = ["Blessing", "IncreaseAGI"];
    private static readonly string[] HealScripts = ["Heal"];
    private static readonly string[] ProvokeScripts = ["Provoke", "Taunt"]; // CombatService.Effects: Engage(target, caster)

    private static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Mirror of CombatService.Relation for the selectors of a skill's template (friendly/dead/self-only bits).</summary>
    internal static bool Selects(GameData data, JsonElement row, bool friendly, bool dead, bool self = false)
    {
        if (data.SkillTemplate(GameData.Int(row, "_iScriptShow")) is not { } t || !t.TryGetProperty("m_kSelectInfo", out var selectors)
            || selectors.ValueKind != JsonValueKind.Array) return false;
        foreach (var select in selectors.EnumerateArray())
        {
            int relation = (int)GameData.Int(select, "m_kSelectType"), subtype = (int)GameData.Int(select, "m_kSelectSubType");
            if ((relation & (dead ? friendly ? 32 : 16 : friendly ? 2 : 1)) == 0 && (relation & (dead ? 64 : 4)) == 0) continue;
            if (subtype == 2 && !self || subtype == 16 && self) continue;
            return true;
        }
        return false;
    }

    private static bool OnlySpCost(JsonElement row)
    {
        if (!row.TryGetProperty("_kCost", out var cost) || cost.ValueKind != JsonValueKind.Array) return true;
        foreach (var tuple in cost.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array) return false;
            var c = tuple.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            if (c.Length < 4 || c[0] != 1 || c[1] is not (2000 or 2001)) return false; // items/buff piles/energy: not a free bot cast
        }
        return true;
    }

    /// <summary>The bot-usable support skills among a job's skill rows (level 1 of each tree group, as the attack path does).</summary>
    internal static SupportKit Resolve(GameData data, IEnumerable<int> rowIds, int level)
    {
        int heal = 0, revive = 0; bool provoke = false;
        var buffs = new List<(int Order, int Id)>();
        foreach (int id in rowIds)
        {
            if (data.Row("SkillConfig", id) is not { } row || GameData.Int(row, "_iActivateCondition") > level) continue;
            string script = Script(row);
            int release = (int)GameData.Int(row, "_iRelease");
            if (ProvokeScripts.Contains(script)) provoke = true;
            if (!OnlySpCost(row)) continue;
            if (script == "Resurrection" && release == 1 && revive == 0 && Selects(data, row, friendly: true, dead: true)) revive = id;
            else if (HealScripts.Contains(script) && release == 1 && heal == 0 && GameData.Int(row, "_iDmgType") == 2
                && Selects(data, row, friendly: true, dead: false)
                && (row.TryGetProperty("_kDamageParam2", out var flat) && flat.ValueKind == JsonValueKind.Array && flat.GetArrayLength() > 0
                    || row.TryGetProperty("_kDamageParam1", out var mult) && mult.ValueKind == JsonValueKind.Array && mult.GetArrayLength() > 0))
                heal = id;
            else if (release == 2 && GameData.Int(row, "_iDmgType") == 0 && Array.IndexOf(BuffScripts, script) is var order and >= 0
                && row.TryGetProperty("_kBuff1", out var b1) && b1.ValueKind == JsonValueKind.Array && b1.GetArrayLength() > 0
                && Selects(data, row, friendly: true, dead: false)) buffs.Add((order, id));
        }
        return new(heal, buffs.OrderBy(b => b.Order).Select(b => b.Id).Take(2).ToArray(), revive, provoke);
    }

    /// <summary>First buff effect id of a skill row (SkillConfig._kBuff1 [id, chance, duration ms]) and its duration.</summary>
    internal static (int Buff, int DurationMs) BuffOf(GameData data, int skill)
    {
        if (data.Row("SkillConfig", skill) is { } row && row.TryGetProperty("_kBuff1", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var t in list.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 3)
                    return ((int)(GameData.Number(t[0]) ?? 0), (int)(GameData.Number(t[2]) ?? 0));
        return (0, 0);
    }

    /// <summary>Heal, then revive, then buff; the first that is allowed wins. Damage and tank companions never act.</summary>
    internal static SupportAction Plan(SupportContext c)
    {
        if (c.Role != CompanionRole.Support || !c.Kit.IsSupport) return SupportAction.Nothing;
        int threshold = Math.Clamp(c.HealBelowPercent, 1, 99);
        if (c.Kit.Heal != 0 && c.Ready(c.Kit.Heal))
        {
            float reach = c.Range(c.Kit.Heal);
            var hurt = c.Members.Where(m => !m.Dead && m.MaxHp > 0 && m.Hp * 100 < m.MaxHp * threshold && m.Distance2 <= reach * reach)
                .OrderBy(m => (double)m.Hp / m.MaxHp).FirstOrDefault();
            if (hurt != null) return new(SupportKind.Heal, c.Kit.Heal, hurt);
        }
        // (inferred) never resurrect with monsters still on the owner, mirroring the client's out-of-combat resurrection use.
        if (c.Revive && !c.OwnerInCombat && c.Kit.Revive != 0 && c.Ready(c.Kit.Revive))
        {
            float reach = c.Range(c.Kit.Revive);
            var dead = c.Members.Where(m => m.Dead && m.Human && m.Distance2 <= reach * reach).OrderBy(m => m.Owner ? 0 : 1).ThenBy(m => m.Distance2).FirstOrDefault();
            if (dead != null) return new(SupportKind.Revive, c.Kit.Revive, dead);
        }
        if (c.Buffs && (!c.OwnerInCombat || c.FightAgeMs <= FightStartWindowMs))
            foreach (int buff in c.Kit.Buffs)
                if (c.Ready(buff) && c.OwnerLacks(buff) && c.Members.FirstOrDefault(m => m.Owner && !m.Dead) is { } owner && owner.Distance2 <= c.Range(buff) * c.Range(buff))
                    return new(SupportKind.Buff, buff, owner, c.Members.Where(m => m.Human && !m.Owner && !m.Dead && m.Distance2 <= c.Range(buff) * c.Range(buff)).ToArray());
        return SupportAction.Nothing;
    }

    /// <summary>Full-or-nothing config switch: "auto" keeps whatever the job derives, otherwise the job must match.</summary>
    internal static bool WantsRole(string preferred, CompanionRole role) =>
        !Enum.TryParse<CompanionRole>(preferred, true, out var wanted) || wanted == role;
}
