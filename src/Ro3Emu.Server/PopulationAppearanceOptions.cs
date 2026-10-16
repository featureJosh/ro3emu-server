namespace Ro3Emu.Server;

/// <summary>Seeded bot looks (inferred offline policy). Enabled=false restores the original hair/headgear rule.</summary>
public sealed class PopulationAppearanceOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Also allow non-luxury, effect-free collectible fashion, not only default-unlocked rows (off by default).</summary>
    public bool UnlockableFashion { get; set; }
    public float DistinctRadius { get; set; } = 30;
    public int DistinctRetries { get; set; } = 4;
    /// <summary>Per-preset style weights; missing presets use the "field" entry.</summary>
    public Dictionary<string, PopulationAppearanceWeights> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["city"] = new() { Plain = 35, Fashion = 58, Distinctive = 7 },
        ["town"] = new() { Plain = 45, Fashion = 50, Distinctive = 5 },
        ["field"] = new() { Plain = 80, Fashion = 18, Distinctive = 2 },
        ["remote"] = new() { Plain = 65, Fashion = 30, Distinctive = 5 },
    };
}

/// <summary>Relative chances: plain job gear and hair; town fashion; rare distinctive higher-rarity looks.</summary>
public sealed class PopulationAppearanceWeights
{
    public int Plain { get; set; } = 80;
    public int Fashion { get; set; } = 18;
    public int Distinctive { get; set; } = 2;
}
