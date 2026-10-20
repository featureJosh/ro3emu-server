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
    /// <summary>Companions whose job owns heal/buff/revive skills cast them for the party (inferred; off keeps the old assist-only behaviour).</summary>
    public bool Support { get; set; } = true;
    /// <summary>A support companion heals a party member below this share of max HP (clamped 1-99).</summary>
    public int HealBelowPercent { get; set; } = 60;
    /// <summary>Keep up to two job buffs (Blessing, Increase AGI) on the owner, out of combat or at fight start.</summary>
    public bool Buffs { get; set; } = true;
    /// <summary>Resurrect a dead human party member out of combat with the job's SP-cost Resurrection skill.</summary>
    public bool Revive { get; set; } = true;
    /// <summary>"auto" keeps any job; "support", "tank" or "damage" limits the job of a companion spawned for the player (recruited bots keep theirs).</summary>
    public string PreferredRole { get; set; } = "auto";
}
