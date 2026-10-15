namespace Ro3Emu.Server.Maps;

/// <summary>Bounded A* over the exported height grid, followed by line-of-sight simplification.</summary>
internal static class PopulationNavigation
{
    private static readonly (int X, int Z)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    public static IReadOnlyList<MapPoint> Route(NavGrid nav, MapPoint start, MapPoint goal, int budget = 3000)
    {
        if (!nav.IsWalkable(start.X, start.Y, start.Z) || !nav.IsWalkable(goal.X, goal.Y, goal.Z)) return [];
        if (nav.CanWalk(start.X, start.Y, start.Z, goal.X, goal.Z)) return Segments(start, goal);
        var first = ((int)MathF.Floor(start.X), (int)MathF.Floor(start.Z));
        var last = ((int)MathF.Floor(goal.X), (int)MathF.Floor(goal.Z));
        var open = new PriorityQueue<(int X, int Z), float>();
        var costs = new Dictionary<(int, int), float> { [first] = 0 };
        var heights = new Dictionary<(int, int), float> { [first] = start.Y };
        var previous = new Dictionary<(int, int), (int, int)>();
        var closed = new HashSet<(int, int)>();
        open.Enqueue(first, 0);
        while (open.TryDequeue(out var cell, out _) && closed.Count < budget)
        {
            if (!closed.Add(cell)) continue;
            if (cell == last)
            {
                var path = new List<MapPoint> { goal };
                for (var c = cell; c != first; c = previous[c]) path.Add(new(c.Item1 + 0.5f, heights[c], c.Item2 + 0.5f, 0));
                path.Add(start); path.Reverse();
                var result = new List<MapPoint>();
                int i = 0;
                while (i < path.Count - 1)
                {
                    int j = i + 1;
                    // Keep simplification bounded rather than testing every remaining cell against every node.
                    for (int k = j + 1; k < path.Count && k <= i + 20; k++)
                        if (nav.CanWalk(path[i].X, path[i].Y, path[i].Z, path[k].X, path[k].Z)) j = k;
                    result.AddRange(Segments(path[i], path[j])); i = j;
                }
                return result;
            }
            foreach (var (dx, dz) in Neighbours)
            {
                var next = (cell.X + dx, cell.Z + dz);
                if (closed.Contains(next)) continue;
                float ax = cell == first ? start.X : cell.X + 0.5f, az = cell == first ? start.Z : cell.Z + 0.5f;
                float x = next.Item1 + 0.5f, z = next.Item2 + 0.5f, y = heights[cell];
                if (nav.WalkHeight(x, z, y) is not { } h || MathF.Abs(h - y) > NavGrid.WalkHeightTolerance || !nav.CanWalk(ax, y, az, x, z)) continue;
                float cost = costs[cell] + 1;
                if (costs.TryGetValue(next, out float old) && old <= cost) continue;
                costs[next] = cost; heights[next] = h; previous[next] = cell;
                open.Enqueue(next, cost + Math.Abs(next.Item1 - last.Item1) + Math.Abs(next.Item2 - last.Item2));
            }
        }
        return [];
    }

    private static IReadOnlyList<MapPoint> Segments(MapPoint from, MapPoint to)
    {
        float dx = to.X - from.X, dz = to.Z - from.Z;
        int count = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / 12));
        return Enumerable.Range(1, count).Select(i => new MapPoint(from.X + dx * i / count,
            from.Y + (to.Y - from.Y) * i / count, from.Z + dz * i / count, 0)).ToArray();
    }
}
