using Microsoft.Extensions.Options;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Optional ambient population. Placement and roaming are inferred offline-server policy.</summary>
public sealed partial class PopulationService : IGameTicker
{
    // Disjoint from NPCs, monsters, stalls and tower pets; exact in the client's double UUID storage (< 2^53).
    private const long UidBase = 1L << 50;
    private readonly World world;
    private readonly WorldData worldData;
    private readonly GameData data;
    private readonly GameLoop loop;
    private readonly PopulationOptions options;
    private readonly CombatService? combat;
    private readonly SkillService? skills;
    private readonly PopulationPolicy policy;
    private readonly PopulationLoadouts loadouts;
    private readonly PopulationChatter chatter;
    private readonly ChatService? chat;
    private readonly Random random = new();
    private readonly List<PopulationUnit> bots = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private long serial;
    private bool running;
    private TimeSpan nextTick;
    private int? overrideCount;
    private bool? enabledOverride;
    private readonly Dictionary<long, int> mapCounts = new();
    private static readonly int[] Careers = [1000, 1100, 2100, 3100, 4100, 5100, 6100];

    public PopulationService(World world, WorldData worldData, GameData data, GameLoop loop, IOptions<ServerOptions> options, CombatService? combat = null, SkillService? skills = null, ChatService? chat = null)
    {
        this.world = world; this.worldData = worldData; this.data = data; this.loop = loop;
        this.options = options.Value.Population; this.combat = combat; this.skills = skills;
        policy = new(data, worldData, this.options); loadouts = new(data);
        chatter = new(this.options.Chat); this.chat = chat;
        InitReactions();
        loop.Add(this);
    }

    public int Count => bots.Count;
    public int Limit => Math.Clamp(options.MaxTotal, 0, 500);
    private bool Enabled => overrideCount is { } count ? count > 0 : enabledOverride ?? options.Enabled;
    public int PerMap => Enabled ? Math.Clamp(overrideCount ?? options.PerMap, 0, Math.Min(100, Limit)) : 0;
    internal PopulationMapSettings Settings(long scene)
    {
        var resolved = policy.Resolve(scene, mapCounts.TryGetValue(scene, out int count) ? count : overrideCount);
        return resolved with { Count = Enabled ? resolved.Count : 0 };
    }

    public void Tick(GameTime time)
    {
        ApplyPendingReload();
        if (running || time.Now < nextTick || (!Enabled || Limit == 0) && bots.Count == 0) return;
        nextTick = time.Now + TimeSpan.FromMilliseconds(250);
        running = true;
        loop.TrackRuntimeDetached("offline population", async () =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { await UpdateAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
            finally { NotePass(started); running = false; }
        });
    }

    public void RegisterCommands(GmCommands gm) => gm.Register("bots", "[on|0..100|off|status|stats|reset|map 0..100/off/reset|chat off/quiet/normal/chatty|mute|unmute]", async (session, args) =>
    {
        string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        if (arg == "stats" && args.Length == 1) { await GmCommands.ReplyAsync(session, await StatsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())); return; }
        if (arg == "chat")
        {
            if (args.Length != 2 || !PopulationChatter.ValidMode(args[1])) throw new ArgumentException();
            await gate.WaitAsync();
            try { chatter.Mode = args[1].ToLowerInvariant(); chatOverridden = true; }
            finally { gate.Release(); }
        }
        else if (arg is "mute" or "unmute")
        {
            if (args.Length != 1) throw new ArgumentException();
            session.BotChatMuted = arg == "mute";
        }
        else if (arg == "map")
        {
            if (args.Length != 2 || session.SceneEntry <= 0) throw new ArgumentException();
            int? count = args[1].Equals("reset", StringComparison.OrdinalIgnoreCase) ? null
                : args[1].Equals("off", StringComparison.OrdinalIgnoreCase) ? 0 : int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            if (count is < 0 or > 100) throw new ArgumentException();
            await SetMapCountAsync(session.SceneEntry, count);
        }
        else if (args.Length > 1) throw new ArgumentException();
        else if (arg == "on") await EnableConfiguredAsync();
        else if (arg != "status")
        {
            int? count = arg == "reset" ? null : arg == "off" ? 0 : int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
            if (count is < 0 or > 100) throw new ArgumentException();
            await SetCountAsync(count);
        }
        var map = Settings(session.SceneEntry);
        await GmCommands.ReplyAsync(session, $"Population: {Count}/{Limit} total; map {session.SceneEntry} {map.Profile}, target {map.Count}, levels {map.MinLevel}-{map.MaxLevel}, quality cap {map.MaxQuality}; chat {chatter.Mode}, muted {session.BotChatMuted}. Changes last until restart.");
        await GmCommands.ReplyAsync(session, await RoleStatusAsync(session.RuntimeMap));
        await GmCommands.ReplyAsync(session, await DensityStatusAsync(session));
    });

    internal async Task SetCountAsync(int? count)
    {
        await gate.WaitAsync();
        try { overrideCount = count; enabledOverride = null; trimNow = true; if (count == null) { mapCounts.Clear(); chatter.Mode = options.Chat.Mode.ToLowerInvariant(); chatOverridden = false; } }
        finally { gate.Release(); }
        await UpdateAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal async Task SetMapCountAsync(long scene, int? count)
    {
        await gate.WaitAsync();
        try { if (count is { } value) mapCounts[scene] = Math.Clamp(value, 0, 100); else mapCounts.Remove(scene); trimNow = true; }
        finally { gate.Release(); }
        await UpdateAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal async Task EnableConfiguredAsync()
    {
        await gate.WaitAsync();
        try { overrideCount = null; enabledOverride = true; }
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
            var targets = occupied.Keys.ToDictionary(runtime => runtime,
                runtime => Settings(runtime.SceneEntry).Count is var count && count > 0 && worldData.Map(runtime.SceneEntry)?.Nav != null ? count : 0);
            var budgets = AllocateBudget(targets, AmbientLimit);
            // Reclaim empty maps and old neighbourhoods before assigning the global budget.
            foreach (var bot in bots.ToArray())
            {
                if (Settings(bot.SceneEntry).Count == 0 || !occupied.TryGetValue(bot.RuntimeMap, out var players) ||
                    !players.Any(p => Distance2(bot.X, bot.Z, p.Position.X, p.Position.Z) < 220 * 220))
                    await RemoveAsync(bot);
            }
            // Trim before spawning: the hard ceiling at once, budget shrink gradually (PopulationService.Density.cs).
            await TrimAsync(budgets, occupied, now);
            if (!Enabled || Limit == 0) { chatter.Clear(); return; }
            int[] careers = Careers.Where(c => data.Row("JobConfig", c) is { } row && GameData.Int(row, "_iUnitB") > 0 && GameData.Int(row, "_iUnitG") > 0).ToArray();
            int spawned = 0, spawnLimit = Math.Clamp(options.MaxSpawnsPerTick, 1, 100);
            foreach (var (runtime, players) in occupied)
            {
                // Missing navigation means no safe placement; never guess around walls or on another floor.
                if (worldData.Map(runtime.SceneEntry) is not { Nav: { } nav } mapWorld) continue;
                if (SpawnHeld(runtime, now)) continue; // hysteresis: no refill right after a gradual shrink
                var settings = Settings(runtime.SceneEntry) with { Count = budgets.GetValueOrDefault(runtime) };
                int have = bots.Count(b => b.RuntimeMap == runtime);
                int attempts = Math.Min(settings.Count - have, AmbientLimit - bots.Count) * 8;
                for (int attempt = 0; have < settings.Count && bots.Count < AmbientLimit && spawned < spawnLimit && attempt < attempts; attempt++)
                {
                    var player = players[have % players.Length];
                    var anchor = new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, 0);
                    if (PointNear(nav, anchor, 4, 24, mapWorld) is not { } point) continue;
                    long id = ++serial;
                    int level = random.Next(settings.MinLevel, settings.MaxLevel + 1);
                    var allowedCareers = level < 10 ? careers.Where(c => c == 1000).ToArray() : careers;
                    var bot = new PopulationUnit
                    {
                        Uid = UidBase | id, SceneEntry = runtime.SceneEntry, RuntimeMap = runtime,
                        Name = PopulationNames.ForSerial(id),
                        Career = allowedCareers.Length == 0 ? 1000 : allowedCareers[random.Next(allowedCareers.Length)],
                        Gender = random.Next(1, 3), Level = level,
                        Home = point, X = point.X, Y = point.Y, Z = point.Z, Face = random.Next(360),
                        NextWalkMs = now + random.Next(1000, 8000), VisitingNpc = id % 3 == 0,
                    };
                    ApplyLook(bot, settings, id);
                    AssignRole(bot, settings.Profile, id);
                    bot.Hp = bot.MaxHp;
                    bot.NormalSkill = skills?.NormalAttack(bot.Career) ?? 1000001;
                    bot.AttackRange = data.Row("SkillConfig", bot.NormalSkill) is { } normal ? Math.Max(2, GameData.Int(normal, "_iDistanceMax") / 100f) : 2;
                    bot.ActiveSkills = skills?.Tree(bot.Career).Select(t => skills.RealId(t.Group, 1))
                        .Where(id => data.Row("SkillConfig", id) is { } row && GameData.Int(row, "_iBasicType") == 1
                            && GameData.Int(row, "_iIsNormalAtk") == 0 && GameData.Int(row, "_iDmgType") is 1 or 2
                            && GameData.Int(row, "_iActivateCondition") <= bot.Level && GameData.Int(row, "_iRangeType") == 1
                            && data.SkillTemplate(GameData.Int(row, "_iScriptShow")) is { } template
                            && template.TryGetProperty("m_kSelectInfo", out var selectors) && selectors.ValueKind == System.Text.Json.JsonValueKind.Array
                            && selectors.EnumerateArray().Any(selector => (GameData.Int(selector, "m_kSelectType") & 1) != 0)).Take(4).ToArray() ?? [];
                    bot.HealSkill = skills?.Tree(bot.Career).Any(t => t.Group == 10003) == true ? skills.RealId(10003, 1) : 0;
                    bot.Advance(now);
                    bots.Add(bot);
                    await world.AddUnitAsync(bot);
                    have++; spawned++;
                }
            }
            await ReactionsPassAsync(now);
            int routeBudget = 1; // Bound pathfinding admissions per pass, even at the population ceiling.
            foreach (var bot in bots)
            {
                bool wasMoving = bot.Moving, turned = false;
                bot.Advance(now);
                if (bot.Dead)
                {
                    if (now >= bot.RespawnMs)
                    {
                        bot.Hp = bot.MaxHp; bot.Route.Clear(); bot.Target = null; bot.JourneyActive = false;
                        await world.ToUnitViewersAsync(bot, CombatService.EntityBaseChange, bot.Entity());
                    }
                    continue;
                }
                if (worldData.Map(bot.SceneEntry) is not { Nav: { } nav } map) continue;
                if (!bot.Moving && bot.Route.Count == 0 && bot.JourneyActive)
                {
                    bot.JourneyActive = false;
                    bot.IdleChatContext = bot.VisitingNpc ? "npc" : "rest";
                    bot.ActivityUntilMs = now + random.Next(5000, 10000);
                    if (bot.VisitingNpc) { bot.HuntKills = 0; bot.VisitingNpc = false; }
                    turned = RoleArrive(bot, now);
                }
                if (bot.HuntKills >= 3) bot.VisitingNpc = true;
                if (bot.Target is { } target && !PopulationCanHunt(bot, target)) bot.Target = null;
                if (bot.Target == null && !bot.VisitingNpc && !bot.RoleIdle && bot.HuntKills < 3 && combat != null && now >= bot.ActivityUntilMs)
                {
                    bot.Target = world.Map(bot.WorldScene).Near(bot.X, bot.Z, 25).OfType<MonsterUnit>()
                        .Where(m => PopulationCanHunt(bot, m) && Distance2(bot.X, bot.Z, m.X, m.Z) < 25 * 25)
                        .OrderBy(m => Distance2(bot.X, bot.Z, m.X, m.Z)).FirstOrDefault();
                    if (bot.Target != null) bot.TargetExpectedHp = bot.Target.Hp;
                }
                if (combat != null && bot.HealSkill != 0 && bot.Hp * 2 < bot.MaxHp && now >= bot.SkillReadyMs && now >= bot.NextAttackMs)
                {
                    bot.Stop(now); bot.SkillReadyMs = now + 10000; bot.NextAttackMs = now + 1000;
                    await combat.PopulationHealAsync(bot, now);
                }
                else if (bot.Target is { } enemy)
                {
                    float distance = MathF.Sqrt(Distance2(bot.X, bot.Z, enemy.X, enemy.Z));
                    if (distance <= bot.AttackRange + 0.5f)
                    {
                        bot.Stop(now); bot.Route.Clear(); bot.JourneyActive = false;
                        if (now >= bot.NextAttackMs)
                        {
                            var available = bot.ActiveSkills.Where(id => data.Row("SkillConfig", id) is { } row
                                && Math.Max(2, GameData.Int(row, "_iDistanceMax") / 100f) + 0.5f >= distance).ToArray();
                            int skill = available.Length > 0 && now >= bot.SkillReadyMs
                                ? available[random.Next(available.Length)] : bot.NormalSkill;
                            if (skill != bot.NormalSkill) bot.SkillReadyMs = now + 8000;
                            bot.NextAttackMs = now + 1800;
                            await combat!.PopulationAttackAsync(bot, enemy, skill, now);
                        }
                    }
                    else if (!bot.Moving && routeBudget > 0)
                    {
                        routeBudget--;
                        var path = Route(nav, bot.Current, new(enemy.X, enemy.Y, enemy.Z, 0));
                        if (path.Count > 0) bot.WalkTo(path[0], now); else bot.Target = null;
                    }
                }
                else if (!bot.Moving && now >= bot.NextWalkMs && now >= bot.ActivityUntilMs && now >= bot.NextAttackMs)
                {
                    if (bot.Route.Count == 0 && routeBudget > 0)
                    {
                        routeBudget--;
                        ChooseJourney(bot, map, now);
                    }
                    if (bot.Route.TryDequeue(out var next))
                    {
                        // Recheck each segment and use the actual endpoint height on uneven terrain.
                        if (nav.WalkHeight(next.X, next.Z, bot.Y) is { } height && nav.CanWalk(bot.X, bot.Y, bot.Z, next.X, next.Z))
                            bot.WalkTo(next with { Y = height }, now);
                        else bot.Route.Clear();
                    }
                }
                if (wasMoving || bot.Moving || turned)
                    await world.MoveUnitAsync(bot, new MovementChangeNtf { Uid = bot.Uid,
                        TransType = MovementTransitionType.MoveTransNormal, MovementInfo = bot.Movement() });
            }
            if (chat != null)
                foreach (var line in chatter.Plan(bots, scene => Settings(scene).Profile, now, chat.PopulationAudience))
                {
                    NoteChat(now);
                    await chat.PublishPopulationAsync(line.Bot, line.Text, line.Subject, line.SubjectGeneration, line.SubjectRole, line.SubjectLevel);
                    NoteSpoken(line, now);
                }
        }
        finally { gate.Release(); }
    }


    internal static Dictionary<RuntimeMapKey, int> AllocateBudget(IReadOnlyDictionary<RuntimeMapKey, int> targets, int limit)
    {
        var result = targets.Keys.ToDictionary(key => key, _ => 0);
        int remaining = Math.Max(0, limit);
        while (remaining > 0)
        {
            bool allocated = false;
            foreach (var (map, target) in targets)
            {
                if (remaining == 0) break;
                if (result[map] >= target) continue;
                result[map]++; remaining--; allocated = true;
            }
            if (!allocated) break;
        }
        return result;
    }

    internal static bool IsUid(long uid) => uid >= UidBase && uid < UidBase * 2;

    internal Dictionary<string, object?>? Profile(long uid, long version, int cluster)
    {
        if (bots.FirstOrDefault(b => b.Uid == uid) is not { } bot) return null;
        var info = new RoleAdditionBaseInfo { Career = bot.Career, Gender = bot.Gender, JobLevel = 1,
            BornArea = 1, CantBeInvited = true, Offlinetime = -1 };
        info.Appearances.Add(bot.Appearances);
        var fields = new Dictionary<string, object?> { ["Name"] = bot.Name, ["Level"] = bot.Level,
            ["Base"] = info.ToByteArray(), ["Online"] = true, ["OfflineTime"] = -1L, ["ClusterId"] = cluster,
            ["GuildSnap"] = new GuildSnapInfo().ToByteArray() };
        return new() { ["Uid"] = uid, ["FieldsVersion"] = fields.Keys.ToDictionary(k => k, _ => (object?)version), ["Data"] = fields };
    }

    internal static bool PopulationCanHunt(PopulationUnit bot, MonsterUnit monster) =>
        !monster.Dead && monster.Hp > 0 && monster.WorldScene == bot.WorldScene && monster.InstanceId == 0
        && monster.Group.Id >= 0 && monster.Target == null && monster.PetTarget == null && monster.RewardRecipient == 0 && monster.RewardCommit == null
        && (monster.PopulationTarget == bot && monster.Hp == bot.TargetExpectedHp || monster.PopulationTarget == null && monster.Hp == monster.Template.MaxHp)
        && monster.Template.Level <= bot.Level + 5 && monster.Template.MaxHp > 0 && monster.Template.MaxHp < 10000;

    private void ChooseJourney(PopulationUnit bot, MapWorld map, long now)
    {
        // Quest-like itinerary: visit a local NPC, travel to hunting grounds, kill a few monsters, return.
        // No real quest state or rewards are fabricated.
        if (RoleJourney(bot, map, now)) return;
        var destinations = bot.VisitingNpc
            ? map.Npcs.Where(n => n.Kind != WorldNpcKind.Portal).Select(n => n.Arrival)
            : map.SpawnGroups.SelectMany(g => g.Points).Select(p => new MapPoint(p.X, p.Y, p.Z, p.Face));
        var choices = destinations.Where(p => Distance2(bot.X, bot.Z, p.X, p.Z) is > 25 * 25 and < 140 * 140)
            .OrderBy(_ => random.Next()).Select(p => bot.VisitingNpc ? VisitSpot(map, p) : p).OfType<MapPoint>()
            .Where(p => ClearOfLandmarks(map, p.X, p.Z)).Take(3).ToArray();
        foreach (var goal in choices)
        {
            if (map.Nav!.WalkHeight(goal.X, goal.Z, bot.Y) is not { } y) continue;
            var path = Route(map.Nav, bot.Current, goal with { Y = y });
            if (path.Count == 0) continue;
            bot.JourneyActive = true;
            foreach (var p in path) bot.Route.Enqueue(p);
            return;
        }
        for (int i = 0; i < 3; i++)
        {
            double angle = random.NextDouble() * Math.Tau;
            float distance = random.Next(35, 100), x = bot.X + (float)Math.Sin(angle) * distance, z = bot.Z + (float)Math.Cos(angle) * distance;
            if (map.Nav!.WalkHeight(x, z, bot.Y) is not { } y || !ClearOfLandmarks(map, x, z)) continue;
            var path = Route(map.Nav, bot.Current, new(x, y, z, 0));
            if (path.Count == 0) continue;
            bot.JourneyActive = true;
            foreach (var p in path) bot.Route.Enqueue(p);
            return;
        }
        bot.NextWalkMs = now + 3000; // Retry later without spending a pathfinding budget every tick.
    }

    private async Task RemoveAsync(PopulationUnit bot)
    {
        bots.Remove(bot);
        await world.RemoveUnitAsync(bot);
    }

    private MapPoint? PointNear(NavGrid nav, MapPoint origin, float min, float max, MapWorld? map = null)
    {
        for (int i = 0; i < 8; i++)
        {
            float angle = random.NextSingle() * MathF.Tau, distance = min + random.NextSingle() * (max - min);
            float x = origin.X + MathF.Sin(angle) * distance, z = origin.Z + MathF.Cos(angle) * distance;
            if (nav.WalkHeight(x, z, origin.Y) is { } y && MathF.Abs(y - origin.Y) <= NavGrid.WalkHeightTolerance &&
                nav.CanWalk(origin.X, origin.Y, origin.Z, x, z) && ClearOfLandmarks(map, x, z)) return new(x, y, z, 0);
        }
        return null;
    }

    private static float Distance2(float x, float z, float bx, float bz) => (x - bx) * (x - bx) + (z - bz) * (z - bz);
}
