using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

internal sealed record PopulationMapSettings(string Profile, int Count, int MinLevel, int MaxLevel, int MaxQuality);

/// <summary>Offline population policy, inferred. Area difficulty is independent of visiting human levels.</summary>
internal sealed class PopulationPolicy(GameData data, WorldData worldData, PopulationOptions options)
{
    private readonly Dictionary<long, int> areaLevels = new();
    public void Reset() => areaLevels.Clear(); // live reload can change DefaultAreaLevel

    public PopulationMapSettings Resolve(long scene, int? uniformCount = null)
    {
        options.Maps.TryGetValue(scene, out var map);
        string profile = map?.Profile?.ToLowerInvariant() ?? Classify(scene);
        if (!options.Profiles.TryGetValue(profile, out var preset)) preset = new PopulationPreset();
        int count = uniformCount ?? map?.Count ?? (options.UseMapProfiles ? preset.Count : options.PerMap);
        if (map?.Enabled == false) count = 0;
        int area = Math.Clamp(map?.AreaLevel ?? AreaLevel(scene), 1, 100);
        int max = Math.Clamp(Math.Min(area + 2, map?.MaxLevel ?? preset.MaxLevel), 1, 100);
        int min = Math.Clamp(Math.Max(area - 2, map?.MinLevel ?? preset.MinLevel), 1, max);
        return new(profile, Math.Clamp(count, 0, Math.Min(100, Math.Clamp(options.MaxTotal, 0, 500))), min, max,
            Math.Min(PopulationLoadouts.QualityForLevel(max), Math.Clamp(map?.MaxEquipmentQuality ?? preset.MaxEquipmentQuality, 2, 6)));
    }

    private string Classify(long scene)
    {
        if (data.Row("MapConfig", scene) is not { } row) return "field";
        string icon = row.TryGetProperty("_kWorldMapIcon", out var value) ? value.GetString() ?? "" : "";
        if (icon.Contains("city", StringComparison.OrdinalIgnoreCase)) return "city";
        return row.TryGetProperty("_iSceneType", out _) && GameData.Int(row, "_iSceneType") == 0 ? "town" : "field";
    }

    private int AreaLevel(long scene)
    {
        if (areaLevels.TryGetValue(scene, out int cached)) return cached;
        var map = worldData.Map(scene);
        var levels = (map?.SpawnGroups ?? []).SelectMany(g => g.Limits.Select(l => l.NpcId).Concat(g.Points.Select(p => p.ConfigId)))
            .Where(id => id > 0).Distinct().Select(id => data.Row("NPCConfig", id))
            .Where(row => row is { } r && GameData.Int(r, "_iNPCType") == 1)
            .Select(row => (int)GameData.Int(row!.Value, "_iLevel")).Where(level => level is > 0 and <= 100).Order().ToArray();
        return areaLevels[scene] = levels.Length > 0 ? levels[(levels.Length - 1) / 2] : Math.Clamp(options.DefaultAreaLevel, 1, 100);
    }
}
