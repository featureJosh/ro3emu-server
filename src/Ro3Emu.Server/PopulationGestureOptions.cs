namespace Ro3Emu.Server;

/// <summary>Optional bot body language (Population:Gestures). Emotes play only when PerformanceConfig proves the row.</summary>
public sealed class PopulationGestureOptions
{
    public bool Enabled { get; set; } = true;
    public bool FaceSpeakers { get; set; } = true;
    /// <summary>(inferred) 50001 is a level-1 single emote unlocked beside Meditate/Sit; set 0 to disable waving.</summary>
    public int WaveEmojiId { get; set; } = 50001;
    public int WaveChancePercent { get; set; } = 50;
    /// <summary>PerformanceConfig 50002, "Sit" in EmojiTests; set 0 to disable sitting.</summary>
    public int SitEmojiId { get; set; } = 50002;
    public int SitChancePercent { get; set; } = 35;
}
