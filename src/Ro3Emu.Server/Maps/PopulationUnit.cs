using Romsg;

namespace Ro3Emu.Server.Maps;

/// <summary>An ephemeral ambient player. It has no account, inventory, combat target or persistence.</summary>
public sealed class PopulationUnit : WorldUnit
{
    public required string Name { get; init; }
    public int Career { get; init; } = 1000;
    public int Gender { get; init; } = 1;
    public int Level { get; init; } = 1;
    public MapPoint Home { get; init; }
    internal long NextWalkMs;
    private long startMs, endMs, currentMs;
    private MapPoint from, to;
    public bool Moving => endMs > currentMs;
    public const float WalkSpeed = 2.5f;

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
            Name = Name, Gender = Gender, EntityCamp = 1, State = EntityState.Alive,
            Face = Face, MovementInfo = Movement(), NotifyType = EntityBase.Types.NotifyType.All,
        };
        foreach (var (id, value) in new (long, long)[]
            { (101, 100), (2000, 100), (102, 50), (2001, 50), (2002, Level), (113, 250), (115, 100) })
            entity.Property.Add(new AoIProperty { Idx = id, Data = value, BaseData = value });
        entity.EquipInfo.Add(new long[7]);
        return entity;
    }
}
