using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Actual world events near bots: support received, shared kills and field bosses. Each event yields at most one
/// line from one bot, whatever the number of spectators. Recorders run from combat callbacks, so this state is locked.
/// Nothing is recorded while chat is off or the population is disabled, so enabling chat never replays a backlog.</summary>
internal sealed partial class PopulationReactions
{
    internal const string Thanks = "thanks", Revived = "revived", SharedFight = "sharedfight", Boss = "boss";
    private sealed class WorldEvent
    {
        public required string Context;
        public RuntimeMapKey Map;
        public long Bot; // the supported bot; 0 = any bot in Bots near (X, Z)
        public HashSet<long>? Bots;
        public required HashSet<long> Players;
        public long Generation = -1; // the supporter's scene generation; -1 = unchecked
        public float X, Z;
        public long Ready, Until;
    }
    private sealed class Fight { public RuntimeMapKey Map; public readonly HashSet<long> Bots = new(), Players = new(); public long LastHit; }
    private sealed class BossSighting { public RuntimeMapKey Map; public long Seen; public bool Noticed; }
    private readonly object eventSync = new();
    private readonly List<WorldEvent> events = new();
    private readonly Dictionary<MonsterUnit, Fight> fights = new();
    private readonly Dictionary<MonsterUnit, BossSighting> bosses = new();
    private readonly Dictionary<(long Role, string Context), long> subjectReady = new();
    private bool accepting;
    internal int PendingEvents { get { lock (eventSync) return events.Count + bosses.Count(b => !b.Value.Noticed); } }
    internal int TrackedFights { get { lock (eventSync) return fights.Count; } }

    private bool Allowed(string context) => context switch
    {
        Thanks => options.HealReactionsEnabled, Revived => options.ReviveReactionsEnabled,
        SharedFight => options.SharedFightReactionsEnabled, Boss => options.BossReactionsEnabled, _ => false,
    };

    /// <summary>A real player's heal/buff (revive=false) or resurrection (revive=true) landed on this bot.
    /// Repeated casts on the same bot coalesce into its one pending line.</summary>
    public void RecordSupport(PopulationUnit bot, GameSession player, bool revive, long now)
    {
        string context = revive ? Revived : Thanks;
        lock (eventSync)
        {
            if (!accepting || !Allowed(context) || player.Role == null || bot.Removed || player.RuntimeMap != bot.RuntimeMap
                || events.Count >= 256 || events.Any(e => e.Bot == bot.Uid && e.Context == context)) return;
            events.Add(new() { Context = context, Map = bot.RuntimeMap, Bot = bot.Uid, Players = [player.Role.Id],
                Generation = player.SceneGeneration, Ready = now + 5000, Until = now + 90_000 });
        }
    }

    /// <summary>Bots hit only untouched monsters (PopulationCanHunt), so a shared fight always starts with a bot hit.</summary>
    public void RecordBotHit(PopulationUnit bot, MonsterUnit monster, long now)
    {
        lock (eventSync)
        {
            if (!accepting || !options.SharedFightReactionsEnabled || monster.InstanceId != 0) return;
            if (!fights.TryGetValue(monster, out var fight))
            {
                if (fights.Count >= 512) fights.Remove(fights.MinBy(p => p.Value.LastHit).Key);
                fights[monster] = fight = new() { Map = bot.RuntimeMap };
            }
            if (fight.Bots.Count < 16) fight.Bots.Add(bot.Uid);
            fight.LastHit = now;
        }
    }

    /// <summary>Only monsters a bot already damaged are tracked; ordinary player combat allocates nothing here.</summary>
    public void RecordPlayerHit(GameSession player, MonsterUnit monster, long now)
    {
        lock (eventSync)
            if (accepting && player.Role != null && fights.TryGetValue(monster, out var fight) && fight.Players.Count < 16)
            { fight.Players.Add(player.Role.Id); fight.LastHit = now; }
    }

    /// <summary>One sighting per boss unit; a respawn is a new unit and can be noticed again.</summary>
    public void ObserveBoss(MonsterUnit boss, long now)
    {
        lock (eventSync)
            if (accepting && options.BossReactionsEnabled && !boss.Dead && boss.Hp > 0 && !boss.Removed && boss.InstanceId == 0
                && !bosses.ContainsKey(boss) && bosses.Count < 64)
                bosses[boss] = new() { Map = boss.RuntimeMap, Seen = now };
    }

    private void ObserveEvents(long now, bool enabled)
    {
        lock (eventSync)
        {
            accepting = enabled;
            foreach (var boss in bosses.Keys.Where(b => b.Dead || b.Hp <= 0 || b.Removed).ToArray()) bosses.Remove(boss);
            // Noticed bosses stay remembered across chat off/on; pending events and fights do not form a backlog.
            if (!enabled) { events.Clear(); fights.Clear(); return; }
            foreach (var key in subjectReady.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) subjectReady.Remove(key);
            events.RemoveAll(e => e.Until < now || !Allowed(e.Context));
            foreach (var (monster, fight) in fights.ToArray())
            {
                if (monster.Dead)
                {
                    fights.Remove(monster);
                    // (inferred) a quick comment fits a kill better than the five-second greeting delay; bots hunt again soon.
                    if (fight.Players.Count > 0 && fight.Bots.Count > 0 && now - fight.LastHit <= 10_000 && events.Count < 256
                        && options.SharedFightReactionsEnabled)
                        events.Add(new() { Context = SharedFight, Map = fight.Map, Bots = fight.Bots, Players = fight.Players,
                            X = monster.X, Z = monster.Z, Ready = now + 2000, Until = now + 30_000 });
                }
                else if (monster.Removed || now - fight.LastHit > 60_000) fights.Remove(monster);
            }
        }
    }

    private Reaction? FindEvent(PopulationUnit bot, IEnumerable<GameSession> audience, long now)
    {
        lock (eventSync)
        {
            if (!accepting || events.Count == 0 && bosses.Count == 0) return null;
            var nearby = audience.Where(p => p.Role != null && p.RuntimeMap == bot.RuntimeMap && Nearby(bot, p)).OrderBy(p => p.Role!.Id).ToArray();
            if (nearby.Length == 0) return null;
            foreach (var e in events)
            {
                if (e.Map != bot.RuntimeMap || e.Ready > now || e.Until < now || !Allowed(e.Context)
                    || e.Bot != 0 && e.Bot != bot.Uid || e.Bots != null && (!e.Bots.Contains(bot.Uid)
                        || MathF.Pow(e.X - bot.X, 2) + MathF.Pow(e.Z - bot.Z, 2) > 20 * 20)) continue;
                var subject = nearby.FirstOrDefault(p => e.Players.Contains(p.Role!.Id) && (e.Generation < 0 || p.SceneGeneration == e.Generation)
                    && subjectReady.GetValueOrDefault((p.Role.Id, e.Context)) <= now);
                if (subject != null) return new(subject, e.Context, e);
            }
            if (options.BossReactionsEnabled)
                foreach (var (boss, sighting) in bosses)
                    if (!sighting.Noticed && sighting.Map == bot.RuntimeMap && sighting.Seen + 5000 <= now && !boss.Dead && boss.Hp > 0
                        && !boss.Removed && MathF.Pow(boss.X - bot.X, 2) + MathF.Pow(boss.Z - bot.Z, 2) <= 30 * 30)
                        return new(nearby[0], Boss, sighting);
        }
        return null;
    }

    private void ConsumeEvent(Reaction reaction, long now)
    {
        lock (eventSync)
        {
            if (reaction.Event is BossSighting sighting) { sighting.Noticed = true; return; }
            if (reaction.Event is WorldEvent e) events.Remove(e);
            if (reaction.Subject.Role is { } role && subjectReady.Count < 2048) subjectReady[(role.Id, reaction.Context)] = now + 120_000;
        }
    }

    private void ClearEvents()
    {
        lock (eventSync) { events.Clear(); fights.Clear(); bosses.Clear(); subjectReady.Clear(); accepting = false; }
    }
}
