using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Bounded original offline chatter; never simulates player messages or real quest completion.</summary>
internal sealed class PopulationChatter(PopulationChatOptions options)
{
    internal sealed record Line(PopulationUnit Bot, string Text);
    private sealed class MapState
    {
        public long Ready;
        public Queue<string> Recent { get; } = new();
    }
    private readonly Dictionary<RuntimeMapKey, MapState> maps = new();
    private readonly Random random = new();
    public string Mode { get; set; } = options.Mode.ToLowerInvariant();
    internal int ActiveMaps => maps.Count;
    public void Clear() => maps.Clear();
    internal static bool ValidMode(string mode) => mode.ToLowerInvariant() is "off" or "quiet" or "normal" or "chatty";

    public IReadOnlyList<Line> Plan(IEnumerable<PopulationUnit> population, Func<long, string> profile, long now, Func<PopulationUnit, bool>? hasAudience = null)
    {
        var groups = population.GroupBy(b => b.RuntimeMap).ToArray();
        var active = groups.Select(g => g.Key).ToHashSet();
        foreach (var map in maps.Keys.Where(k => !active.Contains(k)).ToArray()) maps.Remove(map);
        var lines = new List<Line>();
        if (!ValidMode(Mode) || Mode == "off") return lines;
        (int mapSeconds, int botSeconds) = Mode switch { "chatty" => (8, 45), "normal" => (20, 90), _ => (45, 180) };
        foreach (var group in groups)
        {
            if (!maps.TryGetValue(group.Key, out var state))
            {
                maps[group.Key] = new() { Ready = now + random.Next(20_000, 50_001) };
                continue; // no burst of chatter as a player loads a map
            }
            if (state.Ready > now || lines.Count >= 2) continue; // bounded server work even with many occupied lines
            foreach (var bot in group.OrderBy(b => b.NextChatMs).ThenBy(b => b.Uid))
            {
                if (bot.Removed || bot.Dead || bot.InCombat || bot.NextChatMs > now
                    || !(hasAudience?.Invoke(bot) ?? bot.Viewers.Any(p => ChatService.PopulationChannel(bot, p) != null))) continue;
                string context = Context(bot, profile(bot.SceneEntry), now);
                if (!options.Phrases.TryGetValue(context, out var phrases)
                    && !PopulationChatOptions.DefaultPhrases.TryGetValue(context, out phrases)) continue;
                var candidates = (phrases ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 180
                    && !p.Any(char.IsControl) && !state.Recent.Contains(p)).Distinct().ToArray();
                if (candidates.Length == 0) continue; // small custom packs become quiet instead of repeating
                string text = candidates[random.Next(candidates.Length)];
                state.Recent.Enqueue(text);
                while (state.Recent.Count > 8) state.Recent.Dequeue();
                state.Ready = now + mapSeconds * 1000L + random.Next(0, mapSeconds * 500 + 1);
                bot.NextChatMs = now + botSeconds * 1000L + random.Next(0, botSeconds * 500 + 1);
                lines.Add(new(bot, text));
                break;
            }
        }
        return lines;
    }

    internal static string Context(PopulationUnit bot, string profile, long now = 0) => bot.Moving ? "travel"
        : bot.ActivityUntilMs > now && bot.IdleChatContext != null ? bot.IdleChatContext
        : profile is "city" or "town" ? "town" : bot.VisitingNpc ? "rest" : "hunt";
}
