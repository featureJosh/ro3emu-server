using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Temporary party companions: population actors that leave the ambient pool, join their owner's real party roster
/// and follow/assist only that owner. They have no account, saved role, inventory or rewards. Formation, catch-up,
/// grace and waiting-outside rules are inferred offline-server policy.
/// </summary>
public sealed partial class PopulationService
{
    private sealed class Companion
    {
        public required PopulationUnit Bot;
        public required long Owner;
        public int Slot, FailedPaths;
        public bool Stay, Passive;
        public long OwnerGoneMs, CreatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public MapPoint FollowAnchor;
        public MonsterUnit? Target;
        public CompanionSupportState Support = new();
    }
    private const float CatchUpDistance = 40, FollowDistance = 6, AssistRadius = 20;
    private const int MaxFailedPaths = 3;
    private readonly Dictionary<long, Companion> companions = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<long> droppedCompanions = new(); // fed from team scopes
    private TeamService? teams;

    internal int CompanionCount => companions.Count;
    private PopulationCompanionOptions CompanionOptions => options.Companions;
    private int MaxCompanions => Math.Clamp(CompanionOptions.MaxPerPlayer, 0, 4);
    private int OwnedCompanions(long owner) => companions.Values.Count(c => c.Owner == owner);
    internal PopulationUnit? CompanionActor(long uid) => companions.GetValueOrDefault(uid)?.Bot;
    internal long CompanionOwner(long uid) => companions.GetValueOrDefault(uid)?.Owner ?? 0;
    private bool Invitable(PopulationUnit bot) => CompanionOptions.Enabled && !companions.ContainsKey(bot.Uid);

    /// <summary>Wire the real party roster (ServerHost). Callbacks run on the loop and never wait on the population gate.</summary>
    public void UseTeams(TeamService teams)
    {
        this.teams = teams;
        teams.CompanionCard = CompanionCard;
        teams.CompanionDropped = droppedCompanions.Enqueue;
        teams.CompanionClaim = ClaimInvitedAsync;
        teams.CompanionInvited = GreetParty;
    }

    internal TeamCompanionCard? CompanionCard(long uid)
    {
        if (!companions.TryGetValue(uid, out var c)) return null;
        var b = c.Bot;
        return new(b.Name, b.Career, b.Level, b.Gender, (int)b.SceneEntry, b.RuntimeMap.LineNumber,
            b.Hp, b.MaxHp, b.MaxSp, b.MaxSp, b.Pos, b.Dead, !b.Removed);
    }

    private async Task<bool> CompanionCommandAsync(GameSession session, string arg, string[] args)
    {
        string? reply;
        switch (arg)
        {
            case "off":
                await DismissCompanionsAsync(null, all: true); // !bots off removes every companion, then the ambient pool
                return false;
            case "recruit":
                if (args.Length != 1) throw new ArgumentException();
                reply = await RecruitAsync(session) ?? "A companion joined your party. !bots follow/stay/assist/passive/dismiss.";
                break;
            case "dismiss":
                if (args.Length > 2 || args.Length == 2 && !args[1].Equals("all", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
                int dismissed = await DismissCompanionsAsync(session.Role?.Id ?? -1, all: args.Length == 2);
                reply = dismissed == 0 ? "You have no companion." : $"Dismissed {dismissed} companion(s).";
                break;
            case "follow" or "stay" or "assist" or "passive":
                if (args.Length != 1) throw new ArgumentException();
                int changed = await SetCompanionModeAsync(session, arg);
                reply = changed == 0 ? "You have no companion." : $"Companion mode: {arg}.";
                break;
            default: return false;
        }
        await GmCommands.ReplyAsync(session, reply);
        return true;
    }

    private static string? CompanionPlacementError(GameSession owner) =>
        owner.Role == null || owner.SceneLoading || owner.InstanceId != 0 || owner.SceneEntry <= 0
            ? "Companions can only join you on a public map." : null;

    /// <summary>Recruit the nearest eligible ambient bot, or spawn one beside the owner; null on success.</summary>
    internal async Task<string?> RecruitAsync(GameSession owner)
    {
        if (!CompanionOptions.Enabled) return "Companions are disabled (Population:Companions:Enabled).";
        if (CompanionPlacementError(owner) is { } error) return error;
        Companion c;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await gate.WaitAsync();
        try
        {
            if (OwnedCompanions(owner.Role!.Id) >= MaxCompanions) return $"You already have {MaxCompanions} companion(s).";
            float range = Math.Clamp(CompanionOptions.RecruitRange, 1, 60);
            var nearest = bots.Where(b => b.RuntimeMap == owner.RuntimeMap && !b.Dead && !b.Removed
                && Distance2(b.X, b.Z, owner.Position.X, owner.Position.Z) <= range * range)
                .MinBy(b => Distance2(b.X, b.Z, owner.Position.X, owner.Position.Z));
            if (nearest != null) c = Adopt(nearest, owner);
            else
            {
                c = Adopt(NewCompanion(owner, BesideOwner(owner), now), owner);
                await world.AddUnitAsync(c.Bot);
            }
        }
        finally { gate.Release(); }
        if (teams != null && await teams.AddCompanionAsync(owner, c.Bot.Uid) is var code && code != 0)
        {
            await gate.WaitAsync();
            try { if (companions.Remove(c.Bot.Uid)) await world.RemoveUnitAsync(c.Bot); }
            finally { gate.Release(); }
            return code == ErrorCodes.PartyFull ? "Your party is full." : $"The companion could not join your party (code {code}).";
        }
        return null;
    }

    /// <summary>Normal party invite to a visible ambient bot: it auto-accepts when eligible (called inside the team scope).</summary>
    private async Task<int> ClaimInvitedAsync(GameSession owner, long uid)
    {
        if (!CompanionOptions.Enabled) return ErrorCodes.FeatureUnavailable;
        if (CompanionPlacementError(owner) != null) return ErrorCodes.PlayerOffline;
        await gate.WaitAsync();
        try
        {
            if (OwnedCompanions(owner.Role!.Id) >= MaxCompanions) return ErrorCodes.PartyFull;
            float range = Math.Clamp(CompanionOptions.RecruitRange, 1, 60);
            // (inferred) a bot outside recruit range, dead, or already a companion answers like an absent player.
            if (bots.FirstOrDefault(b => b.Uid == uid) is not { Dead: false, Removed: false } bot || bot.RuntimeMap != owner.RuntimeMap
                || Distance2(bot.X, bot.Z, owner.Position.X, owner.Position.Z) > range * range) return ErrorCodes.PlayerOffline;
            Adopt(bot, owner);
            return 0;
        }
        finally { gate.Release(); }
    }

    private Companion Adopt(PopulationUnit bot, GameSession owner)
    {
        bots.Remove(bot); // leaves the ambient pool: never reclaimed, budgeted, chatting or hunting as ambient
        LeaveCluster(bot); bot.LookAt = null; // frees its town social-group slot
        bot.Target = null; bot.Route.Clear(); bot.JourneyActive = false; bot.VisitingNpc = false; bot.HuntKills = 0;
        var used = companions.Values.Where(c => c.Owner == owner.Role!.Id).Select(c => c.Slot).ToHashSet();
        var c = new Companion { Bot = bot, Owner = owner.Role!.Id, Slot = Enumerable.Range(0, 4).First(s => !used.Contains(s)) };
        c.Support.Kit = ResolveKit(bot.Career, bot.Level);
        companions[bot.Uid] = c;
        return c;
    }

    private PopulationUnit NewCompanion(GameSession owner, MapPoint point, long now)
    {
        long id = ++serial;
        var settings = Settings(owner.SceneEntry);
        // A spawned companion matches its owner's level (inferred); a recruited ambient bot keeps its own.
        int level = Math.Clamp(owner.Progress?.Level ?? owner.Role!.Level, 1, 100);
        int[] careers = Careers.Where(c => (level >= 10 || c == 1000) && data.Row("JobConfig", c) is { } row
            && GameData.Int(row, "_iUnitB") > 0 && GameData.Int(row, "_iUnitG") > 0).ToArray();
        if (skills != null && !CompanionOptions.PreferredRole.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && careers.Where(c => CompanionSupportRules.WantsRole(CompanionOptions.PreferredRole, ResolveKit(c, level).Role)).ToArray() is { Length: > 0 } preferred) careers = preferred;
        var bot = new PopulationUnit
        {
            Uid = UidBase | id, SceneEntry = owner.SceneEntry, RuntimeMap = owner.RuntimeMap, Name = PopulationNames.ForSerial(id),
            Career = careers.Length == 0 ? 1000 : careers[random.Next(careers.Length)], Gender = random.Next(1, 3), Level = level,
            Home = point, X = point.X, Y = point.Y, Z = point.Z, Face = random.Next(360),
        };
        loadouts.Apply(bot, Math.Min(PopulationLoadouts.QualityForLevel(level), settings.MaxQuality), id);
        bot.Hp = bot.MaxHp;
        bot.NormalSkill = skills?.NormalAttack(bot.Career) ?? 1000001;
        bot.AttackRange = data.Row("SkillConfig", bot.NormalSkill) is { } normal ? Math.Max(2, GameData.Int(normal, "_iDistanceMax") / 100f) : 2;
        bot.ActiveSkills = skills?.Tree(bot.Career).Select(t => skills.RealId(t.Group, 1))
            .Where(skill => data.Row("SkillConfig", skill) is { } row && GameData.Int(row, "_iBasicType") == 1
                && GameData.Int(row, "_iIsNormalAtk") == 0 && GameData.Int(row, "_iDmgType") is 1 or 2
                && GameData.Int(row, "_iActivateCondition") <= bot.Level && GameData.Int(row, "_iRangeType") == 1
                && data.SkillTemplate(GameData.Int(row, "_iScriptShow")) is { } template
                && template.TryGetProperty("m_kSelectInfo", out var selectors) && selectors.ValueKind == System.Text.Json.JsonValueKind.Array
                && selectors.EnumerateArray().Any(selector => (GameData.Int(selector, "m_kSelectType") & 1) != 0)).Take(4).ToArray() ?? [];
        bot.Advance(now);
        return bot;
    }

    /// <summary>The same companion identity as a fresh actor on the owner's current runtime map.</summary>
    private static PopulationUnit Relocated(PopulationUnit old, GameSession owner, MapPoint p)
    {
        var bot = new PopulationUnit
        {
            Uid = old.Uid, SceneEntry = owner.SceneEntry, RuntimeMap = owner.RuntimeMap, Name = old.Name, Career = old.Career,
            Gender = old.Gender, Level = old.Level, Home = p, X = p.X, Y = p.Y, Z = p.Z, Face = old.Face, Hp = old.Hp,
            NormalSkill = old.NormalSkill, HealSkill = old.HealSkill, ActiveSkills = old.ActiveSkills, AttackRange = old.AttackRange,
            NextAttackMs = old.NextAttackMs, SkillReadyMs = old.SkillReadyMs, RespawnMs = old.RespawnMs,
        };
        Array.Copy(old.EquipmentLook, bot.EquipmentLook, bot.EquipmentLook.Length);
        foreach (var (k, v) in old.Appearances) bot.Appearances[k] = v;
        foreach (var (k, v) in old.GearAttributes) bot.GearAttributes[k] = v;
        return bot;
    }

    private MapPoint BesideOwner(GameSession owner)
    {
        var anchor = new MapPoint(owner.Position.X, owner.Position.Y, owner.Position.Z, 0);
        return worldData.Map(owner.SceneEntry)?.Nav is { } nav ? PointNear(nav, anchor, 2, 5) ?? anchor : anchor;
    }

    private async Task PlaceBesideAsync(Companion c, GameSession owner, long now)
    {
        if (!c.Bot.Removed) await world.RemoveUnitAsync(c.Bot); // pending hits/monster claims on the old actor cancel
        c.Bot = Relocated(c.Bot, owner, BesideOwner(owner));
        c.Bot.Advance(now);
        c.FailedPaths = 0; c.Target = null;
        await world.AddUnitAsync(c.Bot);
    }

    private async Task DespawnAsync(Companion c)
    {
        c.Target = null; c.Bot.Route.Clear();
        if (!c.Bot.Removed) await world.RemoveUnitAsync(c.Bot);
    }

    /// <summary>Remove one/all of an owner's companions (owner null: every companion) and then their roster entries.</summary>
    internal async Task<int> DismissCompanionsAsync(long? owner, bool all)
    {
        var leaving = new List<long>();
        await gate.WaitAsync();
        try
        {
            var mine = companions.Values.Where(c => owner == null || c.Owner == owner).OrderByDescending(c => c.Bot.Uid).ToArray();
            foreach (var c in all ? mine : mine.Take(1))
            {
                companions.Remove(c.Bot.Uid); leaving.Add(c.Bot.Uid);
                await DespawnAsync(c);
            }
        }
        finally { gate.Release(); }
        foreach (long uid in leaving) if (teams != null) await teams.RemoveCompanionAsync(uid);
        return leaving.Count;
    }

    /// <summary>Only the owner's own companions change: party friends see them but cannot command them.</summary>
    internal async Task<int> SetCompanionModeAsync(GameSession owner, string mode)
    {
        if (owner.Role == null) return 0;
        await gate.WaitAsync();
        try
        {
            var mine = companions.Values.Where(c => c.Owner == owner.Role.Id).ToArray();
            foreach (var c in mine)
            {
                if (mode is "follow" or "stay") { c.Stay = mode == "stay"; c.Bot.Route.Clear(); }
                else { c.Passive = mode == "passive"; if (c.Passive) c.Target = null; }
            }
            return mine.Length;
        }
        finally { gate.Release(); }
    }

    /// <summary>Owned monster engagement only: a public, ordinary monster currently fighting the owner, with no lethal hit pending.</summary>
    internal static bool CompanionCanAssist(GameSession owner, PopulationUnit bot, MonsterUnit m) =>
        !m.Dead && m.Hp > 1 && m.WorldScene == bot.WorldScene && m.InstanceId == 0 && m.Group.Id >= 0
        && ReferenceEquals(m.Target, owner) && m.RewardCommit == null && m.RewardSettlement == null && !m.DeathDurable
        && m.Template.MaxHp > 0;

    internal async Task UpdateCompanionsAsync(long now)
    {
        if (companions.Count == 0 && droppedCompanions.IsEmpty) return;
        var leaving = new List<long>();
        await gate.WaitAsync();
        try
        {
            // The roster dropped these (leave, kick, disband, matched party): despawn without another team call.
            while (droppedCompanions.TryDequeue(out long uid))
                if (companions.Remove(uid, out var gone)) await DespawnAsync(gone);
            var owners = world.ActivePlayers().Where(p => p.Role != null).GroupBy(p => p.Role!.Id).ToDictionary(g => g.Key, g => g.First());
            int routeBudget = 2, grace = Math.Clamp(CompanionOptions.DisconnectGraceSeconds, 0, 600) * 1000;
            foreach (var c in companions.Values.ToArray())
            {
                // A claim whose roster admission never completed (e.g. the owner vanished mid-invite) is not kept.
                if (teams != null && teams.CompanionOwner(c.Bot.Uid) == 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - c.CreatedMs > 10_000)
                {
                    companions.Remove(c.Bot.Uid); await DespawnAsync(c); continue;
                }
                if (!owners.TryGetValue(c.Owner, out var owner))
                {
                    if (c.OwnerGoneMs == 0) c.OwnerGoneMs = now;
                    await DespawnAsync(c);
                    if (now - c.OwnerGoneMs >= grace) { companions.Remove(c.Bot.Uid); leaving.Add(c.Bot.Uid); }
                    continue;
                }
                c.OwnerGoneMs = 0;
                // Waits outside private dungeons/events and during loads; never enters another party's instance.
                if (CompanionPlacementError(owner) != null) { await DespawnAsync(c); continue; }
                if (c.Bot.Removed || c.Bot.RuntimeMap != owner.RuntimeMap) { await PlaceBesideAsync(c, owner, now); continue; }
                var bot = c.Bot;
                bool wasMoving = bot.Moving;
                bot.Advance(now);
                if (bot.Dead)
                {
                    if (bot.RespawnMs == 0) bot.RespawnMs = now + 30000;
                    if (now >= bot.RespawnMs) // same 30 s in-place revival as ambient bots
                    {
                        bot.Hp = bot.MaxHp; bot.RespawnMs = 0; bot.Route.Clear(); c.Target = null;
                        await world.ToUnitViewersAsync(bot, CombatService.EntityBaseChange, bot.Entity());
                    }
                    continue;
                }
                float ownerDistance2 = Distance2(bot.X, bot.Z, owner.Position.X, owner.Position.Z);
                if (ownerDistance2 <= CatchUpDistance * CatchUpDistance && c.FailedPaths < MaxFailedPaths && await SupportAsync(c, owner, owners, now))
                {
                    if (wasMoving) await world.MoveUnitAsync(bot, new MovementChangeNtf { Uid = bot.Uid, TransType = MovementTransitionType.MoveTransNormal, MovementInfo = bot.Movement() });
                    continue;
                }
                if (ownerDistance2 > CatchUpDistance * CatchUpDistance || c.FailedPaths >= MaxFailedPaths)
                {
                    await PlaceBesideAsync(c, owner, now); continue;
                }
                var nav = worldData.Map(bot.SceneEntry)?.Nav;
                if (c.Target is { } held && !CompanionCanAssist(owner, bot, held)) c.Target = null;
                if (c.Target == null && !c.Passive && combat != null)
                    c.Target = world.Map(bot.WorldScene).Near(owner.Position.X, owner.Position.Z, AssistRadius).OfType<MonsterUnit>()
                        .Where(m => CompanionCanAssist(owner, bot, m) && Distance2(m.X, m.Z, owner.Position.X, owner.Position.Z) < AssistRadius * AssistRadius
                            && (!c.Stay || Distance2(m.X, m.Z, bot.X, bot.Z) <= MathF.Pow(bot.AttackRange + 0.5f, 2)))
                        .OrderBy(m => m.Uid == owner.LockTarget ? 0 : 1).ThenBy(m => Distance2(bot.X, bot.Z, m.X, m.Z)).FirstOrDefault();
                if (c.Target is { } enemy)
                {
                    float distance = MathF.Sqrt(Distance2(bot.X, bot.Z, enemy.X, enemy.Z));
                    if (distance <= bot.AttackRange + 0.5f)
                    {
                        bot.Stop(now); bot.Route.Clear();
                        if (now >= bot.NextAttackMs)
                        {
                            var available = bot.ActiveSkills.Where(id => data.Row("SkillConfig", id) is { } row
                                && Math.Max(2, GameData.Int(row, "_iDistanceMax") / 100f) + 0.5f >= distance).ToArray();
                            int skill = available.Length > 0 && now >= bot.SkillReadyMs ? available[random.Next(available.Length)] : bot.NormalSkill;
                            bot.NextAttackMs = now + 1800;
                            await combat!.CompanionAttackAsync(bot, enemy, skill, now, m => CompanionCanAssist(owner, bot, m));
                        }
                    }
                    else if (c.Stay) c.Target = null;
                    else if (!bot.Moving && routeBudget > 0)
                    {
                        routeBudget--;
                        var goal = new MapPoint(enemy.X, enemy.Y, enemy.Z, 0);
                        IReadOnlyList<MapPoint> path = nav == null ? [goal] : PopulationNavigation.Route(nav, bot.Current, goal);
                        if (path.Count > 0) bot.WalkTo(path[0], now); else { c.Target = null; c.FailedPaths++; }
                    }
                }
                else if (!c.Stay && !bot.Moving && ownerDistance2 > FollowDistance * FollowDistance)
                {
                    var anchor = new MapPoint(owner.Position.X, owner.Position.Y, owner.Position.Z, 0);
                    // Continue a planned route only while the owner stays near the point it was planned for.
                    if (bot.Route.Count > 0 && Distance2(anchor.X, anchor.Z, c.FollowAnchor.X, c.FollowAnchor.Z) > 4 * 4) bot.Route.Clear();
                    if (bot.Route.Count == 0 && routeBudget > 0)
                    {
                        routeBudget--;
                        var goal = FollowGoal(c, bot, anchor, nav);
                        IReadOnlyList<MapPoint> path = nav == null ? [goal] : PopulationNavigation.Route(nav, bot.Current, goal);
                        if (path.Count == 0) c.FailedPaths++;
                        else { c.FailedPaths = 0; c.FollowAnchor = anchor; foreach (var p in path) bot.Route.Enqueue(p); }
                    }
                    if (bot.Route.TryDequeue(out var next))
                    {
                        if (nav == null) bot.WalkTo(next, now);
                        else if (nav.WalkHeight(next.X, next.Z, bot.Y) is { } height && nav.CanWalk(bot.X, bot.Y, bot.Z, next.X, next.Z))
                            bot.WalkTo(next with { Y = height }, now);
                        else { bot.Route.Clear(); c.FailedPaths++; }
                    }
                }
                if (wasMoving || bot.Moving)
                    await world.MoveUnitAsync(bot, new MovementChangeNtf
                    {
                        Uid = bot.Uid,
                        TransType = MovementTransitionType.MoveTransNormal, MovementInfo = bot.Movement()
                    });
            }
        }
        finally { gate.Release(); }
        // Roster changes run after the gate: a team scope may itself wait for the population gate (normal invites).
        foreach (long uid in leaving) if (teams != null) await teams.RemoveCompanionAsync(uid);
    }

    /// <summary>A loose formation point behind the owner on the companion's side, separated by slot.</summary>
    private static MapPoint FollowGoal(Companion c, PopulationUnit bot, MapPoint owner, NavGrid? nav)
    {
        float dx = bot.X - owner.X, dz = bot.Z - owner.Z, length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.01f) (dx, dz, length) = (0, 1, 1);
        float angle = (c.Slot % 2 == 0 ? 1 : -1) * (0.35f + c.Slot / 2 * 0.5f), sin = MathF.Sin(angle), cos = MathF.Cos(angle);
        float x = owner.X + (dx * cos - dz * sin) / length * 3, z = owner.Z + (dx * sin + dz * cos) / length * 3;
        if (nav == null) return new(x, owner.Y, z, 0);
        return nav.WalkHeight(x, z, owner.Y) is { } y && MathF.Abs(y - owner.Y) <= NavGrid.WalkHeightTolerance
            && nav.CanWalk(owner.X, owner.Y, owner.Z, x, z) ? new(x, y, z, 0) : owner;
    }
}
