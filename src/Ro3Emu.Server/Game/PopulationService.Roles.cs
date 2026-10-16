using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Behavior roles on top of the journey loop: destinations, hunting willingness, pauses and social groups.</summary>
public sealed partial class PopulationService
{
    private const int MaxClustersPerMap = 8;
    private readonly Dictionary<RuntimeMapKey, List<PopulationCluster>> clusters = new();

    private void AssignRole(PopulationUnit bot, string profile, long seed)
    {
        if (!options.Roles.Enabled) return;
        bot.Behavior = PopulationRoles.Pick(PopulationRoles.Weights(options, profile), seed);
        bot.RoleIdle = random.NextDouble() >= PopulationRoles.HuntChance(bot.Behavior);
    }

    /// <summary>Runs after the shared arrival bookkeeping; true when the bot turned and viewers need a stop packet.</summary>
    private bool RoleArrive(PopulationUnit bot, long now)
    {
        if (bot.Behavior == PopulationRole.None) return false;
        var (min, max) = PopulationRoles.Pause(bot.Behavior);
        bot.ActivityUntilMs = now + random.Next(min, max + 1);
        if (PopulationRoles.ChatKey(bot.Behavior) is { } key && random.Next(5) < 3) bot.IdleChatContext = key;
        bot.RoleIdle = random.NextDouble() >= PopulationRoles.HuntChance(bot.Behavior);
        if (bot.LookAt is not { } look) return false;
        bot.LookAt = null;
        if (Distance2(bot.X, bot.Z, look.X, look.Z) < 0.01f) return false;
        // A stationary MovementInfo carries Face; the client turning a stopped actor to it is (inferred).
        bot.Face = MonsterService.Angle(bot.X, bot.Z, look.X, look.Z);
        return true;
    }

    /// <summary>False leaves the original chooser in charge (roles off, hunters). Bounded to three routes per call.</summary>
    internal bool RoleJourney(PopulationUnit bot, MapWorld map, long now)
    {
        var left = bot.Cluster;
        LeaveCluster(bot); bot.LookAt = null;
        if (bot.Behavior is PopulationRole.None or PopulationRole.Hunter || map.Nav is not { } nav) return false;
        int attempts = 3;
        bool town = Settings(bot.SceneEntry).Profile is "city" or "town";
        if (bot.Behavior == PopulationRole.Socializer && town && options.Roles.SocialClusters && JoinCluster(bot, map, nav, left, ref attempts)) return true;
        foreach (var (goal, look) in RoleGoals(bot, map, nav))
        {
            if (attempts-- <= 0) break;
            if (TryRoute(bot, nav, goal)) { bot.LookAt = look; return true; }
        }
        bot.NextWalkMs = now + 3000;
        return true;
    }

    private IEnumerable<(MapPoint Goal, MapPoint? Look)> RoleGoals(PopulationUnit bot, MapWorld map, NavGrid nav)
    {
        var npcs = map.Npcs.Where(n => n.Kind != WorldNpcKind.Portal && Distance2(bot.X, bot.Z, n.X, n.Z) < 140 * 140)
            .OrderBy(n => bot.Behavior == PopulationRole.Merchant && n.Kind == WorldNpcKind.Kafra ? 0 : 1).ThenBy(_ => random.Next()).Take(3).ToArray();
        MapPoint At(WorldNpc n) => new(n.X, n.Y, n.Z, 0);
        switch (bot.Behavior)
        {
            case PopulationRole.Explorer when random.Next(4) == 0:
                foreach (var p in map.SpawnGroups.SelectMany(g => g.Points).Select(p => new MapPoint(p.X, p.Y, p.Z, 0)).Concat(map.Npcs.Where(n => n.Kind != WorldNpcKind.Portal).Select(At))
                    .Where(p => Distance2(bot.X, bot.Z, p.X, p.Z) is > 40 * 40 and < 140 * 140).OrderBy(_ => random.Next()).Take(1))
                    if (NearPoint(map, nav, p, 2, 5) is { } goal) yield return (goal, null);
                break;
            case PopulationRole.Crafter:
                // Stand beside a known life-collection point without collecting; no items or quest state are produced.
                foreach (var g in map.GatheringPoints.Where(g => Distance2(bot.X, bot.Z, g.Position.X, g.Position.Z) < 140 * 140).OrderBy(_ => random.Next()).Take(2))
                    if (NearPoint(map, nav, g.Position, 1.5f, 2.5f) is { } goal) yield return (goal, g.Position);
                foreach (var n in npcs) if (NearPoint(map, nav, At(n), 3, 5) is { } goal) yield return (goal, At(n));
                break;
            case PopulationRole.Merchant:
                // Idles near function NPCs (Kafra first) facing passers-by; it never opens a real trade. (inferred)
                foreach (var n in npcs)
                    if (NearPoint(map, nav, At(n), 3, 6) is { } goal) yield return (goal, goal with { X = goal.X * 2 - n.X, Z = goal.Z * 2 - n.Z });
                break;
            case PopulationRole.Socializer or PopulationRole.Rester:
                foreach (var n in npcs) if (random.Next(3) > 0 && NearPoint(map, nav, At(n), 5, 9) is { } goal) yield return (goal, null);
                break;
        }
        for (int i = 0; i < 4; i++)
        {
            double angle = random.NextDouble() * Math.Tau;
            float distance = bot.Behavior == PopulationRole.Explorer ? random.Next(50, 130) : random.Next(35, 100);
            float x = bot.X + (float)Math.Sin(angle) * distance, z = bot.Z + (float)Math.Cos(angle) * distance;
            if (nav.WalkHeight(x, z, bot.Y) is { } y && Clear(map, x, z)) yield return (new(x, y, z, 0), null);
        }
    }

    private bool JoinCluster(PopulationUnit bot, MapWorld map, NavGrid nav, PopulationCluster? left, ref int attempts)
    {
        PruneClusters();
        var list = clusters.GetValueOrDefault(bot.RuntimeMap) ?? new();
        var cluster = list.Where(c => c != left && c.Count < c.Slots.Length && Distance2(bot.X, bot.Z, c.Center.X, c.Center.Z) < 140 * 140)
            .OrderBy(_ => random.Next()).FirstOrDefault();
        bool created = cluster == null;
        if (cluster == null && list.Count < MaxClustersPerMap && ClusterCenter(bot, map, nav, list) is { } center)
            cluster = new(bot.RuntimeMap, center, random.Next(2, Math.Clamp(options.Roles.ClusterSize, 2, 4) + 1), random.NextSingle() * 360);
        if (cluster == null) return false;
        for (int slot = 0; slot < cluster.Slots.Length; slot++)
        {
            if (cluster.Slots[slot] != null) continue;
            // One ring slot per member: loose 1.7-2.3m radius, spaced by capacity so nobody stands on another.
            float angle = (cluster.BaseAngle + slot * 360f / cluster.Slots.Length + random.Next(-15, 16)) * MathF.PI / 180, radius = 1.7f + random.NextSingle() * 0.6f;
            float x = cluster.Center.X + MathF.Sin(angle) * radius, z = cluster.Center.Z + MathF.Cos(angle) * radius;
            if (nav.WalkHeight(x, z, cluster.Center.Y) is not { } y || MathF.Abs(y - cluster.Center.Y) > NavGrid.WalkHeightTolerance
                || !nav.CanWalk(cluster.Center.X, cluster.Center.Y, cluster.Center.Z, x, z)) continue;
            attempts--;
            // Already standing on the slot: an empty journey still arrives next pass and turns to the group.
            if (Distance2(bot.X, bot.Z, x, z) >= 0.25f && !TryRoute(bot, nav, new(x, y, z, 0), 0.5f)) return false;
            bot.JourneyActive = true; cluster.Slots[slot] = bot; bot.Cluster = cluster; bot.LookAt = cluster.Center;
            if (created) { list.Add(cluster); clusters[bot.RuntimeMap] = list; }
            return true;
        }
        return false;
    }

    private MapPoint? ClusterCenter(PopulationUnit bot, MapWorld map, NavGrid nav, List<PopulationCluster> existing)
    {
        var npcs = map.Npcs.Where(n => n.Kind != WorldNpcKind.Portal && Distance2(bot.X, bot.Z, n.X, n.Z) < 120 * 120).ToArray();
        for (int i = 0; i < 4; i++)
        {
            var npc = npcs.Length > 0 && i < 3 ? npcs[random.Next(npcs.Length)] : null;
            var center = npc != null ? NearPoint(map, nav, new(npc.X, npc.Y, npc.Z, 0), 6, 10) : NearPoint(map, nav, bot.Current, 8, 20);
            if (center is { } c && existing.All(o => Distance2(o.Center.X, o.Center.Z, c.X, c.Z) > 8 * 8)) return c;
        }
        return null;
    }

    private bool TryRoute(PopulationUnit bot, NavGrid nav, MapPoint goal, float min = 3)
    {
        if (Distance2(bot.X, bot.Z, goal.X, goal.Z) < min * min) return false;
        var path = PopulationNavigation.Route(nav, bot.Current, goal);
        if (path.Count == 0) return false;
        bot.JourneyActive = true;
        foreach (var p in path) bot.Route.Enqueue(p);
        return true;
    }

    /// <summary>A walkable same-floor spot near a landmark, clear of portals and NPC interaction footholds.</summary>
    private MapPoint? NearPoint(MapWorld map, NavGrid nav, MapPoint origin, float min, float max)
    {
        for (int i = 0; i < 4; i++)
        {
            float angle = random.NextSingle() * MathF.Tau, distance = min + random.NextSingle() * (max - min);
            float x = origin.X + MathF.Sin(angle) * distance, z = origin.Z + MathF.Cos(angle) * distance;
            if (nav.WalkHeight(x, z, origin.Y) is { } y && MathF.Abs(y - origin.Y) <= NavGrid.WalkHeightTolerance && Clear(map, x, z))
                return new(x, y, z, 0);
        }
        return null;
    }

    private static bool Clear(MapWorld map, float x, float z) => map.Npcs.All(n => n.Kind == WorldNpcKind.Portal
        ? Distance2(x, z, n.X, n.Z) > 6 * 6 && Distance2(x, z, n.Arrival.X, n.Arrival.Z) > 6 * 6
        : Distance2(x, z, n.X, n.Z) > 1.5f * 1.5f && Distance2(x, z, n.Arrival.X, n.Arrival.Z) > 2 * 2);

    private static void LeaveCluster(PopulationUnit bot)
    {
        if (bot.Cluster is not { } cluster) return;
        int slot = Array.IndexOf(cluster.Slots, bot);
        if (slot >= 0) cluster.Slots[slot] = null;
        bot.Cluster = null;
    }

    private void PruneClusters()
    {
        foreach (var (key, list) in clusters.ToArray())
        {
            foreach (var cluster in list)
                for (int i = 0; i < cluster.Slots.Length; i++)
                    if (cluster.Slots[i] is { } member && (member.Removed || member.Dead || member.Cluster != cluster || member.RuntimeMap != key))
                    {
                        cluster.Slots[i] = null;
                        if (member.Cluster == cluster) member.Cluster = null;
                    }
            list.RemoveAll(c => c.Count == 0);
            if (list.Count == 0) clusters.Remove(key);
        }
    }

    internal async Task<string> RoleStatusAsync(RuntimeMapKey map)
    {
        await gate.WaitAsync();
        try { PruneClusters(); return PopulationRoles.Mix(bots.Where(b => b.RuntimeMap == map), clusters.GetValueOrDefault(map)?.Count ?? 0); }
        finally { gate.Release(); }
    }
}
