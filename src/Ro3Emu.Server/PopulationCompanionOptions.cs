namespace Ro3Emu.Server;

/// <summary>Temporary party companions recruited from the offline population; limits are inferred offline policy.</summary>
public sealed class PopulationCompanionOptions
{
    public bool Enabled { get; set; }
    /// <summary>Clamped to 0-4: a five-person party holds the owner and at most four others.</summary>
    public int MaxPerPlayer { get; set; } = 1;
    /// <summary>Ambient bots within this many map units can be recruited; otherwise a new one is spawned beside the player.</summary>
    public int RecruitRange { get; set; } = 20;
    /// <summary>A disconnected owner's companion waits this long (0-600 s) before it is dismissed from the party.</summary>
    public int DisconnectGraceSeconds { get; set; } = 60;
}
