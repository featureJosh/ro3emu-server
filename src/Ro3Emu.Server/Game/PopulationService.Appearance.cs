using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class PopulationService
{
    /// <summary>Chosen once at spawn and never re-rolled while the actor lives (death/revival keeps it).</summary>
    private void ApplyLook(PopulationUnit bot, PopulationMapSettings settings, long seed)
    {
        var look = options.Appearance;
        if (!look.Enabled) { loadouts.Apply(bot, settings.MaxQuality, seed); bot.LookSeed = seed; return; }
        float radius = Math.Clamp(look.DistinctRadius, 0, 60);
        var nearby = radius <= 0 ? [] : bots.Where(b => b.RuntimeMap == bot.RuntimeMap
            && Distance2(b.X, b.Z, bot.X, bot.Z) < radius * radius).ToArray();
        loadouts.ApplyDistinct(bot, settings.MaxQuality, seed, PopulationLookStyle.For(look, settings.Profile), nearby, look.DistinctRetries);
    }
}
