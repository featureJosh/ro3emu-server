using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Behavior-role switches; per-area weights live on <see cref="PopulationPreset.Roles"/>.</summary>
public sealed class PopulationRoleOptions
{
    public bool Enabled { get; set; } = true;
    public bool SocialClusters { get; set; } = true;
    public int ClusterSize { get; set; } = 3;
}

/// <summary>None means roles were off at spawn: the bot keeps the original journey loop.</summary>
public enum PopulationRole { None, Explorer, Hunter, Socializer, Crafter, Merchant, Rester }

/// <summary>Two to four socializers sharing one town rest point, one ring slot each.</summary>
internal sealed class PopulationCluster(RuntimeMapKey map, MapPoint center, int capacity, float baseAngle)
{
    public RuntimeMapKey Map { get; } = map;
    public MapPoint Center { get; } = center;
    public float BaseAngle { get; } = baseAngle;
    public PopulationUnit?[] Slots { get; } = new PopulationUnit?[capacity];
    public int Count => Slots.Count(s => s != null);
}

/// <summary>Offline behavior-role tables. All weights, pauses and chances are inferred population policy.</summary>
internal static class PopulationRoles
{
    // Index + 1 is the PopulationRole value; these are also the configuration keys.
    internal static readonly string[] Keys = ["explorer", "hunter", "socializer", "crafter", "merchant", "rester"];

    internal static int[] Defaults(string profile) => profile switch
    {
        "city" => [10, 5, 30, 15, 20, 20],
        "town" => [15, 10, 25, 15, 15, 20],
        "remote" => [60, 25, 0, 5, 0, 10],
        _ => [30, 45, 5, 10, 0, 10],
    };

    /// <summary>A configured mix replaces the built-in one; unknown keys are ignored, an all-zero mix falls back.</summary>
    internal static int[] Weights(PopulationOptions options, string profile)
    {
        if (options.Profiles.TryGetValue(profile, out var preset) && preset.Roles is { Count: > 0 } configured)
        {
            var weights = Keys.Select(k => Math.Clamp(configured.Where(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value).FirstOrDefault(), 0, 1000)).ToArray();
            if (weights.Sum() > 0) return weights;
        }
        return Defaults(profile);
    }

    /// <summary>Stable for an identity seed: the same seed and mix always yield the same role.</summary>
    internal static PopulationRole Pick(int[] weights, long seed)
    {
        int total = weights.Sum();
        if (total <= 0) return PopulationRole.Hunter;
        ulong h = (ulong)seed + 0x9E3779B97F4A7C15UL;
        h = (h ^ (h >> 30)) * 0xBF58476D1CE4E5B9UL; h = (h ^ (h >> 27)) * 0x94D049BB133111EBUL; h ^= h >> 31;
        int roll = (int)(h % (ulong)total);
        for (int i = 0; i < weights.Length; i++) if ((roll -= weights[i]) < 0) return (PopulationRole)(i + 1);
        return PopulationRole.Hunter;
    }

    internal static string Key(PopulationRole role) => role == PopulationRole.None ? "none" : Keys[(int)role - 1];

    internal static double HuntChance(PopulationRole role) => role switch
    {
        PopulationRole.None or PopulationRole.Hunter => 1,
        PopulationRole.Explorer => 0.35,
        PopulationRole.Rester => 0.15,
        PopulationRole.Crafter => 0.1,
        PopulationRole.Socializer => 0.05,
        _ => 0,
    };

    /// <summary>Arrival pause range in milliseconds; randomized per stop so bots drift out of step.</summary>
    internal static (int Min, int Max) Pause(PopulationRole role) => role switch
    {
        PopulationRole.Explorer => (2000, 7000),
        PopulationRole.Hunter => (2000, 9000),
        PopulationRole.Socializer => (25000, 60000),
        PopulationRole.Crafter => (12000, 30000),
        PopulationRole.Merchant => (35000, 90000),
        PopulationRole.Rester => (25000, 70000),
        _ => (5000, 10000),
    };

    /// <summary>Multiplier on the existing per-bot chat cooldown; map budgets are unchanged.</summary>
    internal static double ChatScale(PopulationRole role) => role switch
    {
        PopulationRole.Socializer => 0.6,
        PopulationRole.Merchant => 0.85,
        PopulationRole.Hunter => 1.6,
        _ => 1,
    };

    internal static string? ChatKey(PopulationRole role) => role switch
    {
        PopulationRole.Explorer => "explorer",
        PopulationRole.Socializer => "socializer",
        PopulationRole.Crafter => "crafter",
        PopulationRole.Merchant => "merchant",
        PopulationRole.Rester => "rest",
        _ => null,
    };

    internal static string Mix(IEnumerable<PopulationUnit> bots, int clusters)
    {
        var counts = bots.GroupBy(b => b.Behavior).OrderBy(g => g.Key).Select(g => $"{Key(g.Key)} {g.Count()}").ToArray();
        return counts.Length == 0 ? "Roles here: none." : $"Roles here: {string.Join(", ", counts)}; social groups {clusters}.";
    }
}
