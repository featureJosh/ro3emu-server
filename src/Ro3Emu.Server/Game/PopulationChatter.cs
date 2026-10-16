using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Bounded original offline chatter; never simulates player messages or real quest completion.</summary>
internal sealed class PopulationChatter(PopulationChatOptions options)
{
    internal sealed record Line(PopulationUnit Bot, string Text, GameSession? Subject = null, long SubjectGeneration = 0, long SubjectRole = 0, int SubjectLevel = 0);
    private readonly PopulationReactions reactions = new(options);
    private sealed class MapState
    {
        public long Ready;
        public Queue<string> Recent { get; } = new();
    }
    private readonly Dictionary<RuntimeMapKey, MapState> maps = new();
    private readonly Random random = new();
    public string Mode { get; set; } = options.Mode.ToLowerInvariant();
    internal int ActiveMaps => maps.Count;
    public void Clear() { maps.Clear(); reactions.Clear(); }
    internal static bool ValidMode(string mode) => mode.ToLowerInvariant() is "off" or "quiet" or "normal" or "chatty";

    public IReadOnlyList<Line> Plan(IEnumerable<PopulationUnit> population, Func<long, string> profile, long now, Func<PopulationUnit, GameSession[]>? getAudience = null)
    {
        var groups = population.GroupBy(b => b.RuntimeMap).ToArray();
        var audience = groups.SelectMany(g => g).ToDictionary(b => b, b => getAudience?.Invoke(b)
            ?? b.Viewers.Where(p => ChatService.PopulationChannel(b, p) != null).ToArray());
        reactions.Observe(audience, now, ValidMode(Mode) && Mode != "off");
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
            foreach (var bot in group.OrderByDescending(b => reactions.Find(b, audience[b], now) != null)
                .ThenBy(b => b.NextChatMs).ThenBy(b => b.Uid))
            {
                if (bot.Removed || bot.Dead || bot.InCombat || bot.NextChatMs > now
                    || audience[bot].Length == 0) continue;
                var reaction = reactions.Find(bot, audience[bot], now);
                string context = reaction?.Context ?? Context(bot, profile(bot.SceneEntry), now);
                string[] Candidates(string key)
                {
                    if (!options.Phrases.TryGetValue(key, out var phrases)
                        && !PopulationChatOptions.DefaultPhrases.TryGetValue(key, out phrases)) return [];
                    return (phrases ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 180
                        && !p.Any(char.IsControl) && !state.Recent.Contains(p)).Distinct().ToArray();
                }
                var candidates = Candidates(context);
                if (reaction != null && candidates.Length == 0)
                {
                    reaction = null; // an exhausted/disabled reaction pack must not suppress ordinary chatter
                    candidates = Candidates(Context(bot, profile(bot.SceneEntry), now));
                }
                if (candidates.Length == 0) continue; // small custom packs become quiet instead of repeating
                string template = candidates[random.Next(candidates.Length)];
                string text = reaction == null ? template : PopulationReactions.Expand(template, reaction.Subject);
                if (text.Length > 180) continue;
                state.Recent.Enqueue(template);
                while (state.Recent.Count > 8) state.Recent.Dequeue();
                state.Ready = now + mapSeconds * 1000L + random.Next(0, mapSeconds * 500 + 1);
                bot.NextChatMs = now + botSeconds * 1000L + random.Next(0, botSeconds * 500 + 1);
                if (reaction != null) reactions.Consume(reaction, now);
                lines.Add(new(bot, text, reaction?.Subject, reaction?.Subject.SceneGeneration ?? 0, reaction?.Subject.Role?.Id ?? 0, reaction?.Context == "levelup" ? reaction.Subject.Progress?.Level ?? reaction.Subject.Role?.Level ?? 1 : 0));
                break;
            }
        }
        return lines;
    }

    internal static string Context(PopulationUnit bot, string profile, long now = 0) => bot.Moving ? "travel"
        : bot.ActivityUntilMs > now && bot.IdleChatContext != null ? bot.IdleChatContext
        : profile is "city" or "town" ? "town" : bot.VisitingNpc ? "rest" : "hunt";
}
