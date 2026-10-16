using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

internal sealed record PopulationLookStyle(int Plain, int Fashion, int Distinctive, bool UnlockableFashion = false)
{
    internal static PopulationLookStyle For(PopulationAppearanceOptions options, string profile)
    {
        var w = options.Profiles.TryGetValue(profile, out var found) || options.Profiles.TryGetValue("field", out found) ? found : new();
        return new(Math.Max(0, w.Plain), Math.Max(0, w.Fashion), Math.Max(0, w.Distinctive), options.UnlockableFashion);
    }
}

/// <summary>
/// Seeded style pools (inferred policy). The wire look is only EquipInfo plus the EAppearance_Type slot map
/// (MG_Entity curUseAppearances, RoleAdditionBaseInfo.appearances); there is no hair-color or facial-feature field and
/// WardRobeConfig has no dye column, so hair color cannot vary. Slot 2 is the face accessory. Wardrobe rows have no
/// job column; sex, per-sex model, disabled, luxury and effect paths are the compatibility rules. Wardrobe attributes
/// are not applied to bots: cosmetic slots never change stats.
/// </summary>
internal sealed partial class PopulationLoadouts
{
    internal const int FashionQuality = 3; // (inferred) ordinary town fashion stays at common rarity
    private static readonly int[] FashionSlots = [4, 2, 3, 9]; // clothing, face, mouth, foot; never back, mount or body effects

    internal static (int Hair, int Face, int Outfit, long Weapon) LookKey(PopulationUnit bot) => (bot.Appearances.GetValueOrDefault(7),
        bot.Appearances.GetValueOrDefault(2), bot.Appearances.GetValueOrDefault(4), bot.EquipmentLook[0]);

    /// <summary>Bounded rerolls away from nearby identical looks; the chosen seed is kept so the look can be rebuilt.</summary>
    internal long ApplyDistinct(PopulationUnit bot, int areaQuality, long seed, PopulationLookStyle style, IEnumerable<PopulationUnit> nearby, int retries)
    {
        var taken = nearby.Where(other => other != bot).Select(LookKey).ToHashSet();
        retries = taken.Count == 0 ? 0 : Math.Clamp(retries, 0, 8);
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            long candidate = attempt == 0 ? seed : unchecked(seed + attempt * (long)0x9E3779B97F4A7C15UL);
            Apply(bot, areaQuality, candidate, style);
            if (!taken.Contains(LookKey(bot))) return bot.LookSeed = candidate;
        }
        if (retries > 0) Apply(bot, areaQuality, seed, style); // every variant collided: keep the identity look
        return bot.LookSeed = seed;
    }

    private void ApplyStyle(PopulationUnit bot, int quality, long seed, PopulationLookStyle style)
    {
        // Own stream: hair and style tier depend only on the seed, not on the area's gear pool size.
        var random = new Random(unchecked((int)(seed ^ (seed >> 32)) ^ 0x4C4F4F4B));
        var hair = wardrobe!.Where(r => Type(r) == 7 && GameData.Int(r, "_iDefaultLightUp") > 0 && Wearable(r, bot.Gender)).ToArray();
        if (hair.Length > 0) bot.Appearances[7] = (int)GameData.Int(hair[random.Next(hair.Length)], "_iID");
        int total = style.Plain + style.Fashion + style.Distinctive, ticket = total > 0 ? random.Next(total) : 0;
        int tier = total == 0 || ticket < style.Plain ? 0 : ticket < style.Plain + style.Fashion ? 1 : 2;
        if (tier == 0) return;
        int worn = bot.EquipmentLook.Where(id => id > 0).Select(id => (int)GameData.Int(data.Row("ItemConfig", id)!.Value, "_iQuality")).DefaultIfEmpty(2).Max();
        int cap = Math.Min(quality, worn);
        if (tier == 1) cap = Math.Min(cap, FashionQuality);
        bool Fits(JsonElement r) => Wearable(r, bot.Gender) && GameData.Int(r, "_iQuality") <= cap
            && NoEffects(r, "_kEffectPathsB") && NoEffects(r, "_kEffectPathsG");
        JsonElement? Pick(IEnumerable<JsonElement> rows)
        {
            var list = rows.ToArray();
            if (tier == 2 && list.Any(r => GameData.Int(r, "_iQuality") > FashionQuality)) list = list.Where(r => GameData.Int(r, "_iQuality") > FashionQuality).ToArray();
            return list.Length == 0 ? null : list[random.Next(list.Length)];
        }
        if (bot.Level >= 20 && (tier == 2 || random.Next(3) != 0) && Pick(wardrobe!.Where(r => Type(r) == 1 && Fits(r))) is { } hat)
            bot.Appearances[1] = (int)GameData.Int(hat, "_iID");
        foreach (int slot in FashionSlots)
            if ((tier == 2 && slot == 4 || random.Next(2) == 0) && Pick(wardrobe!.Where(r => Type(r) == slot && Fits(r)
                && (style.UnlockableFashion || GameData.Int(r, "_iDefaultLightUp") > 0))) is { } piece)
                bot.Appearances[slot] = (int)GameData.Int(piece, "_iID");
    }

    private static int Type(JsonElement row) => (int)GameData.Int(row, "_iType");
    private static bool Wearable(JsonElement row, int gender) => GameData.Int(row, "_iID") > 0 && GameData.Int(row, "_iDisable") == 0
        && GameData.Int(row, "_iLuxury") == 0 && (GameData.Int(row, "_iSexNeed") == 0 || GameData.Int(row, "_iSexNeed") == gender)
        && HasModel(row, gender == 2 ? "_kModelResourceG" : "_kModelResourceB");
}
