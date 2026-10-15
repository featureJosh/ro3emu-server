using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Observed nearby player events only. No human state is changed and offline events are never replayed.</summary>
internal sealed class PopulationReactions(PopulationChatOptions options)
{
    internal sealed record Reaction(GameSession Subject, string Context);
    private sealed class Visitor
    {
        public long LastSeen, GreetingReady, LevelReady, PendingReady, PendingUntil, Generation;
        public int Level, PendingLevel;
        public bool Suspended;
        public RuntimeMapKey Map;
        public string? Pending;
    }
    private readonly Dictionary<long, Visitor> visitors = new();
    internal int Count => visitors.Count;
    public void Clear() => visitors.Clear();
    internal static bool Nearby(PopulationUnit bot, GameSession player) => !player.Dead
        && MathF.Pow(player.Position.X - bot.X, 2) + MathF.Pow(player.Position.Z - bot.Z, 2) <= 12 * 12;

    public void Observe(IReadOnlyDictionary<PopulationUnit, GameSession[]> audience, long now, bool enabled)
    {
        long greetingCooldown = Math.Clamp(options.GreetingCooldownSeconds, 60, 3600) * 1000L;
        foreach (var id in visitors.Where(p => now - p.Value.LastSeen > greetingCooldown + 120_000).Select(p => p.Key).ToArray())
            visitors.Remove(id);
        var seen = audience.SelectMany(p => p.Value.Where(v => Nearby(p.Key, v))).Distinct().ToArray();
        var ids = new HashSet<long>();
        foreach (var player in seen)
        {
            if (player.Role == null) continue;
            long id = player.Role.Id;
            ids.Add(id);
            int level = player.Progress?.Level ?? player.Role.Level;
            if (!visitors.TryGetValue(id, out var visitor))
            {
                if (visitors.Count >= 2048) visitors.Remove(visitors.MinBy(p => p.Value.LastSeen).Key);
                visitors[id] = visitor = new() { Level = level, Map = player.RuntimeMap, Generation = player.SceneGeneration, LastSeen = now - 3000 };
            }
            bool continuous = !visitor.Suspended && visitor.Map == player.RuntimeMap && visitor.Generation == player.SceneGeneration && now - visitor.LastSeen <= 2000;
            string? context = enabled && continuous && level > visitor.Level && options.LevelUpReactionsEnabled && now >= visitor.LevelReady
                ? "levelup" : enabled && !continuous && options.GreetingsEnabled && now >= visitor.GreetingReady ? "greeting" : null;
            // Suppressed/absent events update the baseline, never forming a backlog on chat enable/unmute.
            if (!enabled || !continuous || visitor.Pending == "levelup" && level < visitor.PendingLevel) visitor.Pending = null;
            if (context != null)
            {
                visitor.Pending = context; visitor.PendingLevel = level;
                visitor.PendingReady = now + 5000;
                visitor.PendingUntil = now + 90_000;
            }
            visitor.Level = level; visitor.Map = player.RuntimeMap; visitor.Generation = player.SceneGeneration; visitor.LastSeen = now; visitor.Suspended = false;
        }
        foreach (var (id, visitor) in visitors)
        {
            if (!ids.Contains(id)) { visitor.Pending = null; visitor.Suspended = true; }
            if (visitor.PendingUntil < now || visitor.Pending == "greeting" && !options.GreetingsEnabled
                || visitor.Pending == "levelup" && !options.LevelUpReactionsEnabled) visitor.Pending = null;
        }
    }

    public Reaction? Find(PopulationUnit bot, IEnumerable<GameSession> audience, long now)
    {
        foreach (var player in audience.Where(p => p.Role != null && Nearby(bot, p)).OrderBy(p => p.Role!.Id))
            if (visitors.TryGetValue(player.Role!.Id, out var visitor) && visitor.Map == bot.RuntimeMap && visitor.Generation == player.SceneGeneration
                && visitor.Pending != null && visitor.PendingReady <= now && visitor.PendingUntil >= now)
                return new(player, visitor.Pending);
        return null;
    }

    public void Consume(Reaction reaction, long now)
    {
        if (reaction.Subject.Role == null || !visitors.TryGetValue(reaction.Subject.Role.Id, out var visitor)) return;
        visitor.Pending = null;
        visitor.GreetingReady = now + Math.Clamp(options.GreetingCooldownSeconds, 60, 3600) * 1000L;
        if (reaction.Context == "levelup") visitor.LevelReady = now + 120_000;
    }

    internal static string Expand(string template, GameSession player)
    {
        // Names appear as plain text, never as rich-text or special chat markup.
        string name = new((player.Role?.Name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or ' ').Take(40).ToArray());
        if (string.IsNullOrWhiteSpace(name)) name = "adventurer";
        return template.Replace("{player}", name, StringComparison.Ordinal)
            .Replace("{level}", (player.Progress?.Level ?? player.Role?.Level ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
