using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Optional ambient population. Placement and roaming are inferred offline-server policy.</summary>
public sealed class PopulationService : IGameTicker
{
    // Disjoint from NPCs, monsters, stalls and tower pets; exact in the client's double UUID storage (< 2^53).
    private const long UidBase = 1L << 50;
    private readonly World world;
    private readonly WorldData worldData;
    private readonly GameData data;
    private readonly GameLoop loop;
    private readonly PopulationOptions options;
    private readonly Random random = new();
    private readonly List<PopulationUnit> bots = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private long serial;
    private bool running;
    private TimeSpan nextTick;
    private int? overrideCount;
    private static readonly string[] Names = ["Aster", "Bramble", "Clover", "Dusk", "Ember", "Fern", "Gale", "Hazel", "Iris", "Juniper", "Kestrel", "Luna", "Maple", "Nova", "Orion", "Poppy", "Quartz", "Rowan", "Sora", "Thistle", "Una", "Vale", "Willow", "Yarrow"];
    private static readonly int[] Careers = [1000, 1100, 2100, 3100, 4100, 5100, 6100];

    public PopulationService(World world, WorldData worldData, GameData data, GameLoop loop, IOptions<ServerOptions> options)
    {
        this.world = world; this.worldData = worldData; this.data = data; this.loop = loop;
        this.options = options.Value.Population;
        loop.Add(this);
    }

    public int Count => bots.Count;
    public int Limit => Math.Clamp(options.MaxTotal, 0, 500);
    public int PerMap => Math.Clamp(overrideCount ?? (options.Enabled ? options.PerMap : 0), 0, Math.Min(100, Limit));

    public void Tick(GameTime time)
    {
        if (running || time.Now < nextTick || PerMap == 0 && bots.Count == 0) return;
        nextTick = time.Now + TimeSpan.FromSeconds(1);
        running = true;
        loop.TrackRuntimeDetached("offline population", async () =>
        {
            try { await UpdateAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
            finally { running = false; }
        });
    }

    public void RegisterCommands(GmCommands gm) => gm.Register("bots", "[0..100|off|status|reset]", async (session, args) =>
    {
        if (args.Length > 1) throw new ArgumentException();
        string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        if (arg != "status")
        {
            int? count = arg == "reset" ? null : arg == "off" ? 0 : int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
            if (count is < 0 or > 100) throw new ArgumentException();
            await SetCountAsync(count);
        }
        await GmCommands.ReplyAsync(session, $"Ambient bots: {Count} active; {PerMap}/occupied public map; total limit {Limit}. Changes last until restart.");
    });

    internal async Task SetCountAsync(int? count)
    {
        await gate.WaitAsync();
        try { overrideCount = count; }
        finally { gate.Release(); }
        await UpdateAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal async Task UpdateAsync(long now)
    {
        await gate.WaitAsync();
        try
        {
            var occupied = world.ActivePlayers().Where(p => p.Role != null && !p.SceneLoading && p.InstanceId == 0 && p.SceneEntry > 0)
                .GroupBy(p => p.RuntimeMap).OrderBy(g => g.Key.SceneEntry).ThenBy(g => g.Key.Id)
                .ToDictionary(g => g.Key, g => g.ToArray());
            // Reclaim empty maps and old neighbourhoods before assigning the global budget.
            foreach (var bot in bots.ToArray())
            {
                if (PerMap == 0 || !occupied.TryGetValue(bot.RuntimeMap, out var players) ||
                    !players.Any(p => Distance2(bot.X, bot.Z, p.Position.X, p.Position.Z) < 100 * 100))
                    await RemoveAsync(bot);
            }
            // Trim before spawning so reductions never temporarily exceed the new limit.
            foreach (var group in bots.GroupBy(b => b.RuntimeMap).ToArray())
                foreach (var bot in group.Skip(PerMap).ToArray()) await RemoveAsync(bot);
            foreach (var bot in bots.Skip(Limit).ToArray()) await RemoveAsync(bot);
            if (PerMap == 0) return;
            int[] careers = Careers.Where(c => data.Row("JobConfig", c) is { } row && GameData.Int(row, "_iUnitB") > 0 && GameData.Int(row, "_iUnitG") > 0).ToArray();
            foreach (var (runtime, players) in occupied)
            {
                // Missing navigation means no safe placement; never guess around walls or on another floor.
                if (worldData.Map(runtime.SceneEntry)?.Nav is not { } nav) continue;
                int have = bots.Count(b => b.RuntimeMap == runtime);
                int attempts = Math.Min(PerMap - have, Limit - bots.Count) * 8;
                for (int attempt = 0; have < PerMap && bots.Count < Limit && attempt < attempts; attempt++)
                {
                    var player = players[have % players.Length];
                    var anchor = new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, 0);
                    if (PointNear(nav, anchor, 4, 24) is not { } point) continue;
                    long id = ++serial;
                    var bot = new PopulationUnit
                    {
                        Uid = UidBase | id, SceneEntry = runtime.SceneEntry, RuntimeMap = runtime,
                        Name = $"{Names[(int)((id - 1) % Names.Length)]}-{id}",
                        Career = careers.Length == 0 ? 1000 : careers[random.Next(careers.Length)],
                        Gender = random.Next(1, 3), Level = Math.Max(1, player.Role!.Level),
                        Home = point, X = point.X, Y = point.Y, Z = point.Z, Face = random.Next(360),
                        NextWalkMs = now + random.Next(1000, 8000),
                    };
                    bot.Advance(now);
                    bots.Add(bot);
                    await world.AddUnitAsync(bot);
                    have++;
                }
            }
            foreach (var bot in bots)
            {
                bool wasMoving = bot.Moving;
                bot.Advance(now);
                if (!bot.Moving && now >= bot.NextWalkMs && worldData.Map(bot.SceneEntry)?.Nav is { } nav)
                {
                    var current = new MapPoint(bot.X, bot.Y, bot.Z, bot.Face);
                    if (PointNear(nav, current, 1, 6) is { } next && Distance2(next.X, next.Z, bot.Home.X, bot.Home.Z) <= 18 * 18)
                        bot.WalkTo(next, now);
                    bot.NextWalkMs = now + random.Next(5000, 12000);
                }
                if (wasMoving || bot.Moving)
                    await world.MoveUnitAsync(bot, new MovementChangeNtf { Uid = bot.Uid,
                        TransType = MovementTransitionType.MoveTransNormal, MovementInfo = bot.Movement() });
            }
        }
        finally { gate.Release(); }
    }

    private async Task RemoveAsync(PopulationUnit bot)
    {
        bots.Remove(bot);
        await world.RemoveUnitAsync(bot);
    }

    private MapPoint? PointNear(NavGrid nav, MapPoint origin, float min, float max)
    {
        for (int i = 0; i < 8; i++)
        {
            float angle = random.NextSingle() * MathF.Tau, distance = min + random.NextSingle() * (max - min);
            float x = origin.X + MathF.Sin(angle) * distance, z = origin.Z + MathF.Cos(angle) * distance;
            if (nav.WalkHeight(x, z, origin.Y) is { } y && MathF.Abs(y - origin.Y) <= NavGrid.WalkHeightTolerance &&
                nav.CanWalk(origin.X, origin.Y, origin.Z, x, z)) return new(x, y, z, 0);
        }
        return null;
    }

    private static float Distance2(float x, float z, float bx, float bz) => (x - bx) * (x - bx) + (z - bz) * (z - bz);
}
