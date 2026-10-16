using Romsg;

namespace Ro3Emu.Server.Maps;

/// <summary>An ephemeral simulated player, with no account or persistent inventory.</summary>
public sealed class PopulationUnit : WorldUnit
{
    public required string Name { get; init; }
    public int Career { get; init; } = 1000;
    public int Gender { get; init; } = 1;
    public int Level { get; init; } = 1;
    public MapPoint Home { get; init; }
    internal long NextWalkMs;
    internal long NextChatMs;
    internal string? IdleChatContext;
    internal long NextAttackMs, SkillReadyMs, RespawnMs, ActivityUntilMs;
    internal readonly Queue<MapPoint> Route = new();
    internal MonsterUnit? Target;
    internal long TargetExpectedHp;
    internal bool VisitingNpc, JourneyActive;
    internal int HuntKills;
    public long[] EquipmentLook { get; } = new long[7];
    public Dictionary<int, int> Appearances { get; } = new();
    public Dictionary<int, long> GearAttributes { get; } = new();
    public long MaxHp => 200 + Level * 50L + GearAttributes.GetValueOrDefault(101);
    public long Attack => 12 + Level * 4L + GearAttributes.GetValueOrDefault(103);
    public long MagicAttack => 12 + Level * 4L + GearAttributes.GetValueOrDefault(104);
    public long MaxSp => 50 + GearAttributes.GetValueOrDefault(102);
    public long Hit => 100 + GearAttributes.GetValueOrDefault(109);
    public long Critical => 300 + GearAttributes.GetValueOrDefault(111);
    public long Flee => Level * 2L + GearAttributes.GetValueOrDefault(110);
    public long Defence => Level * 2L + GearAttributes.GetValueOrDefault(105);
    public long MagicDefence => Level * 2L + GearAttributes.GetValueOrDefault(106);
    public long Hp { get; set; } = 250;
    public bool Dead => Hp <= 0;
    internal int NormalSkill = 1000001;
    internal int HealSkill;
    internal int[] ActiveSkills = [];
    internal float AttackRange = 2;
    internal bool InCombat => Target is { Dead: false, Hp: > 0 };
    internal MapPoint Current => new(X, Y, Z, Face);
    internal void Stop(long now)
    {
        Advance(now); endMs = startMs = 0;
    }
    private long startMs, endMs, currentMs;
    private MapPoint from, to;
    public bool Moving => endMs > currentMs;
    public const float WalkSpeed = 5f;

    internal void Advance(long now)
    {
        currentMs = now;
        if (endMs == 0) return;
        float t = Math.Clamp((now - startMs) / (float)(endMs - startMs), 0, 1);
        X = from.X + (to.X - from.X) * t;
        Y = from.Y + (to.Y - from.Y) * t;
        Z = from.Z + (to.Z - from.Z) * t;
    }

    internal void WalkTo(MapPoint destination, long now)
    {
        from = new(X, Y, Z, Face); to = destination;
        float dx = to.X - X, dz = to.Z - Z;
        Face = (MathF.Atan2(dx, dz) * 180 / MathF.PI + 360) % 360;
        currentMs = startMs = now;
        endMs = now + Math.Max(1, (long)(MathF.Sqrt(dx * dx + dz * dz) / WalkSpeed * 1000));
    }

    internal MovementInfo Movement() => new()
    {
        Pos = Pos, Face = Face, ServerTime = currentMs,
        MoveType = Moving ? MovementType.MoveTypeWalk : MovementType.MoveTypeNone,
        MoveDuration = Moving ? (int)(endMs - currentMs) : 0,
        MovementWalk = Moving ? new MovementWalk { Dir = Face, Speed = WalkSpeed, EndHeight = to.Y } : null,
    };

    public override EntityBase Entity()
    {
        var entity = new EntityBase
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)Career, EntityType = 3,
            Name = Name, Gender = Gender, EntityCamp = 1, State = Dead ? EntityState.Dead : EntityState.Alive,
            Face = Face, MovementInfo = Movement(), NotifyType = EntityBase.Types.NotifyType.All,
        };
        foreach (var (id, value) in new (long, long)[]
            { (101, MaxHp), (2000, Hp), (102, MaxSp), (2001, MaxSp), (2002, Level), (113, 500), (115, 100) })
            entity.Property.Add(new AoIProperty { Idx = id, Data = value, BaseData = value });
        entity.EquipInfo.Add(EquipmentLook);
        entity.CurUseAppearances.Add(Appearances);
        foreach (var (id, amount) in new (long, long)[] { (103, Attack), (104, MagicAttack), (105, Defence), (106, MagicDefence), (109, Hit), (110, Flee), (111, Critical) })
            entity.Property.Add(new AoIProperty { Idx = id, Data = amount, BaseData = amount });
        return entity;
    }
}
