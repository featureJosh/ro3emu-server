using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Placement clearance (inferred offline policy): spawn points and pause destinations keep LandmarkClearance
/// map units away from portals, Kafra/teleport points, server NPC stand points and the born/revive points.</summary>
public sealed partial class PopulationService
{
    private readonly Dictionary<long, (float X, float Z)[]> landmarks = new();
    private float Clearance => Math.Clamp(options.LandmarkClearance, 0f, 30f);

    internal (float X, float Z)[] Landmarks(MapWorld map)
    {
        if (landmarks.TryGetValue(map.SceneEntry, out var cached)) return cached;
        var points = new List<(float X, float Z)>();
        foreach (var npc in map.Npcs)
        {
            points.Add((npc.X, npc.Z));
            // Portal/Kafra footholds are where travellers land; a bot resting there would stand on arrivals.
            if (npc.Kind != WorldNpcKind.Npc && npc.Arrival is var arrival && Distance2(arrival.X, arrival.Z, npc.X, npc.Z) > 0.01f)
                points.Add((arrival.X, arrival.Z));
        }
        foreach (var point in new[] { map.Born, map.Revive }) if (point is { } p) points.Add((p.X, p.Z));
        return landmarks[map.SceneEntry] = points.ToArray(); // one entry per loaded map; MapWorld is immutable
    }

    internal bool ClearOfLandmarks(MapWorld? map, float x, float z)
    {
        float radius = Clearance;
        if (radius <= 0 || map == null) return true;
        foreach (var (lx, lz) in Landmarks(map)) if (Distance2(x, z, lx, lz) < radius * radius) return false;
        return true;
    }

    /// <summary>A walkable spot just outside the clearance ring around an NPC's stand point, or null (the NPC is skipped).</summary>
    internal MapPoint? VisitSpot(MapWorld map, MapPoint stand)
    {
        float radius = Clearance;
        if (radius <= 0) return stand;
        for (int i = 0; i < 6; i++)
        {
            float angle = random.NextSingle() * MathF.Tau, distance = radius + 0.5f + random.NextSingle() * 2;
            float x = stand.X + MathF.Sin(angle) * distance, z = stand.Z + MathF.Cos(angle) * distance;
            // Same floor as the NPC; editor NPC heights can sit slightly off the ground.
            if (map.Nav?.WalkHeight(x, z, stand.Y) is { } y && MathF.Abs(y - stand.Y) <= NavGrid.WalkHeightTolerance * 2
                && ClearOfLandmarks(map, x, z)) return new(x, y, z, (MathF.Atan2(stand.X - x, stand.Z - z) * 180 / MathF.PI + 360) % 360);
        }
        return null;
    }
}
