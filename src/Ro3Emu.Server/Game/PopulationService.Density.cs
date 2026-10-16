using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Primitives;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Density control (inferred offline policy): gradual shrink with hysteresis, companion reserve,
/// live settings reload and bounded operator counters.</summary>
public sealed partial class PopulationService
{
    private readonly Dictionary<RuntimeMapKey, long> overSince = new(), spawnHold = new();
    private IReadOnlyDictionary<RuntimeMapKey, int> lastBudgets = new Dictionary<RuntimeMapKey, int>();
    private readonly Queue<long> chatTimes = new();
    private bool trimNow, chatOverridden;
    private long pathFailures, reloads, rejectedReloads;
    private double lastPassMs;
    private PopulationOptions? pendingReload;
    private IDisposable? reloadWatch;

    /// <summary>Capacity kept free under MaxTotal for future recruited companions; ambient bots never use it.</summary>
    public int CompanionReserve => Math.Clamp(options.CompanionReserve, 0, 100);
    public int AmbientLimit => Math.Max(0, Limit - CompanionReserve);
    internal string ChatMode => chatter.Mode;
    private long DespawnDelayMs => Math.Clamp(options.DespawnDelaySeconds, 0, 600) * 1000L;

    // Explicit operator commands, `!bots off`, emptied/disabled maps and a lowered MaxTotal remove at once. Automatic
    // shrink (fair-share budget moved to a newly occupied line, a reloaded lower target or larger reserve) waits until a
    // map/line has stayed over budget for DespawnDelaySeconds, then removes at most MaxDespawnsPerTick per pass.
    private async Task TrimAsync(Dictionary<RuntimeMapKey, int> budgets, Dictionary<RuntimeMapKey, GameSession[]> occupied, long now)
    {
        lastBudgets = budgets;
        bool immediate = trimNow || DespawnDelayMs == 0;
        trimNow = false;
        foreach (var key in overSince.Keys.Where(k => !occupied.ContainsKey(k)).ToArray()) overSince.Remove(key);
        foreach (var (key, until) in spawnHold.ToArray()) if (until <= now || !occupied.ContainsKey(key)) spawnHold.Remove(key);
        var excess = bots.GroupBy(b => b.RuntimeMap).ToDictionary(g => g.Key, g => g.Count() - budgets.GetValueOrDefault(g.Key));
        if (bots.Count > Limit)
            foreach (var bot in DespawnOrder(bots, occupied, b => excess[b.RuntimeMap] > 0).Take(bots.Count - Limit).ToArray())
            { await RemoveAsync(bot); excess[bot.RuntimeMap]--; }
        int allowance = immediate ? int.MaxValue : Math.Clamp(options.MaxDespawnsPerTick, 1, 100);
        foreach (var (map, over) in excess.OrderByDescending(e => e.Value).ThenBy(e => e.Key.SceneEntry).ThenBy(e => e.Key.Id))
        {
            if (over <= 0) { overSince.Remove(map); continue; }
            if (!immediate)
            {
                if (!overSince.TryGetValue(map, out long since)) { overSince[map] = now; continue; }
                if (now - since < DespawnDelayMs || allowance == 0) continue;
            }
            var chosen = DespawnOrder(bots.Where(b => b.RuntimeMap == map), occupied, _ => true).Take(Math.Min(over, allowance)).ToArray();
            foreach (var bot in chosen) await RemoveAsync(bot);
            if (immediate) { overSince.Remove(map); continue; }
            allowance -= chosen.Length;
            spawnHold[map] = now + DespawnDelayMs; // no refill flapping straight after a shrink
        }
    }

    /// <summary>Out-of-sight first (no AOI viewer), then idle, then farthest from the map's players, newest last.</summary>
    private static IEnumerable<PopulationUnit> DespawnOrder(IEnumerable<PopulationUnit> candidates,
        Dictionary<RuntimeMapKey, GameSession[]> occupied, Func<PopulationUnit, bool> preferred) =>
        candidates.OrderByDescending(preferred).ThenByDescending(b => b.Viewers.Count == 0).ThenBy(b => b.InCombat)
            .ThenByDescending(b => occupied.TryGetValue(b.RuntimeMap, out var players) && players.Length > 0
                ? players.Min(p => Distance2(b.X, b.Z, p.Position.X, p.Position.Z)) : float.MaxValue)
            .ThenByDescending(b => b.Uid);

    private bool SpawnHeld(RuntimeMapKey map, long now) => spawnHold.TryGetValue(map, out long until) && until > now;

    internal int PendingDespawns => bots.GroupBy(b => b.RuntimeMap)
        .Sum(g => Math.Max(0, g.Count() - (lastBudgets.TryGetValue(g.Key, out int budget) ? budget : g.Count())));

    internal IReadOnlyList<MapPoint> Route(NavGrid nav, MapPoint start, MapPoint goal)
    {
        var path = PopulationNavigation.Route(nav, start, goal);
        if (path.Count == 0) pathFailures++;
        return path;
    }

    internal void NoteChat(long now)
    {
        chatTimes.Enqueue(now);
        while (chatTimes.Count > 512) chatTimes.Dequeue(); // bounded even if the chat budgets change
    }

    internal int ChatLastMinute(long now)
    {
        while (chatTimes.TryPeek(out long sent) && sent <= now - 60_000) chatTimes.Dequeue();
        return chatTimes.Count;
    }

    private void NotePass(long started) => lastPassMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    // ---- Live reload --------------------------------------------------------------------------------------------

    /// <summary>Rebinds Ro3Emu:Population when the host configuration reloads (appsettings.json, reloadOnChange).</summary>
    public void WatchConfiguration(IConfiguration configuration)
    {
        reloadWatch?.Dispose();
        reloadWatch = ChangeToken.OnChange(configuration.GetReloadToken, () => QueueReload(configuration.GetSection("Ro3Emu:Population")));
    }

    internal void QueueReload(IConfigurationSection section)
    {
        // A missing section is usually a half-written file; it must not silently switch the population off.
        if (!section.Exists()) { Interlocked.Increment(ref rejectedReloads); return; }
        try { if (section.Get<PopulationOptions>() is { } next) Volatile.Write(ref pendingReload, next); }
        catch (InvalidOperationException) { Interlocked.Increment(ref rejectedReloads); } // malformed value: keep current settings
    }

    /// <summary>Applied on the loop under the population gate. Values keep their usual clamps where they are read;
    /// runtime `!bots` overrides (uniform/map counts, on/off, chat mode) keep precedence until `!bots reset`.</summary>
    internal void ApplyPendingReload()
    {
        if (Volatile.Read(ref pendingReload) == null || !gate.Wait(0)) return;
        try
        {
            if (Interlocked.Exchange(ref pendingReload, null) is not { } next || !options.ReloadOnChange) return;
            CopyInto(next, options); // in place: policy, chatter and reactions hold these same instances
            policy.Reset();
            if (!chatOverridden) chatter.Mode = options.Chat.Mode.ToLowerInvariant();
            reloads++;
        }
        finally { gate.Release(); }
    }

    private static void CopyInto(object source, object target)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            object? value = property.GetValue(source), current = property.GetValue(target);
            if (value != null && current != null && property.PropertyType.IsClass && property.PropertyType.Namespace == typeof(PopulationOptions).Namespace
                && property.PropertyType.Name.EndsWith("Options", StringComparison.Ordinal)) CopyInto(value, current);
            else property.SetValue(target, value);
        }
    }

    // ---- Operator readouts --------------------------------------------------------------------------------------

    internal async Task<string> DensityStatusAsync(GameSession session)
    {
        await gate.WaitAsync();
        try
        {
            var runtime = session.RuntimeMap;
            int actual = bots.Count(b => b.RuntimeMap == runtime);
            bool known = lastBudgets.TryGetValue(runtime, out int budget);
            var settings = Settings(session.SceneEntry);
            string? reason = actual > 0 && budget > 0 && settings.Count > 0 ? null : ZeroReason(session, known, budget, actual);
            int pending = known ? Math.Max(0, actual - budget) : 0;
            return string.Create(CultureInfo.InvariantCulture,
                $"Density: map {session.SceneEntry} line {session.LineNumber} {settings.Profile}: {actual} actual / {settings.Count} target / {(known ? budget.ToString(CultureInfo.InvariantCulture) : "-")} allocated" +
                $"{(pending > 0 ? $", {pending} pending despawn" : "")}; ambient {bots.Count}/{AmbientLimit} (reserve {CompanionReserve}, ceiling {Limit})" +
                $"{(reason != null ? $"; zero: {reason}" : "")}.");
        }
        finally { gate.Release(); }
    }

    private string? ZeroReason(GameSession session, bool known, int budget, int actual)
    {
        if (!Enabled) return "population disabled";
        if (Limit == 0) return "MaxTotal is 0";
        if (AmbientLimit == 0) return "companion reserve takes the whole budget";
        if (session.InstanceId != 0) return "private instance";
        if (session.SceneEntry <= 0) return "no map";
        if (options.Maps.TryGetValue(session.SceneEntry, out var map) && !map.Enabled) return "map disabled in configuration";
        if (Settings(session.SceneEntry).Count == 0) return mapCounts.ContainsKey(session.SceneEntry) ? "map turned off at runtime" : "map target is 0";
        if (worldData.Map(session.SceneEntry)?.Nav == null) return "no navigation grid";
        if (session.SceneLoading) return "player still loading";
        if (!known) return "waiting for the next pass";
        if (budget == 0) return "global budget exhausted by other maps";
        if (actual > 0) return null;
        if (spawnHold.ContainsKey(session.RuntimeMap)) return "refill held after a recent shrink";
        return bots.Count >= AmbientLimit ? "waiting for other maps to shrink" : "no clear walkable spot near players yet";
    }

    internal async Task<string> StatsAsync(long now)
    {
        await gate.WaitAsync();
        try
        {
            var groups = bots.GroupBy(b => b.RuntimeMap).Select(g => (g.Key, Count: g.Count())).OrderByDescending(g => g.Count)
                .ThenBy(g => g.Key.SceneEntry).ThenBy(g => g.Key.Id).ToArray();
            string maps = string.Join(", ", groups.Take(8).Select(g => string.Create(CultureInfo.InvariantCulture,
                $"{g.Key.SceneEntry}/L{g.Key.LineNumber} {g.Count}/{lastBudgets.GetValueOrDefault(g.Key)}")));
            if (groups.Length > 8) maps += $", +{groups.Length - 8} more";
            return string.Create(CultureInfo.InvariantCulture,
                $"Population stats: {bots.Count}/{AmbientLimit} ambient (reserve {CompanionReserve}, ceiling {Limit}); {groups.Length} maps/lines" +
                $"{(groups.Length > 0 ? $" [{maps}]" : "")}; pending despawns {PendingDespawns}; path failures {pathFailures}; " +
                $"chat lines last minute {ChatLastMinute(now)}; last pass {lastPassMs:0.#} ms; reloads {reloads} (rejected {Interlocked.Read(ref rejectedReloads)}).");
        }
        finally { gate.Release(); }
    }
}
