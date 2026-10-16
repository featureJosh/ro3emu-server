using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Original offline policy: visible equipment supplies real base attributes, not decorative power claims.</summary>
internal sealed partial class PopulationLoadouts(GameData data)
{
    private JsonElement[]? equipment, wardrobe;
    internal static int QualityForLevel(int level) => level < 20 ? 2 : level < 40 ? 3 : level < 60 ? 4 : level < 80 ? 5 : 6;

    public void Apply(PopulationUnit bot, int areaQuality, long seed, PopulationLookStyle? style = null)
    {
        var random = new Random(unchecked((int)(seed ^ (seed >> 32))));
        int quality = Math.Min(QualityForLevel(bot.Level), areaQuality);
        equipment ??= data.Table("ItemConfig").Values.Where(r => GameData.Int(r, "_iType") == 5).ToArray();
        wardrobe ??= data.Table("WardRobeConfig").Values.ToArray();
        bot.Appearances.Clear(); bot.GearAttributes.Clear(); Array.Clear(bot.EquipmentLook);
        var raw = new Dictionary<int, double>();
        var eligible = equipment.Where(item => Eligible(item, bot, quality)).ToArray();
        bool twoHanded = false;
        for (int grid = 0; grid < 7; grid++)
        {
            if (grid == 1 && twoHanded) continue;
            int[] parts = grid switch { 0 => [1, 2], 1 => [3], 2 => [4], 3 => [5], 4 => [6], _ => [7] };
            var candidates = eligible.Where(r => parts.Contains((int)GameData.Int(r, "_iEquipPart"))).ToArray();
            if (candidates.Length == 0) continue;
            long bestLevel = candidates.Max(r => GameData.Int(r, "_iLevelNeed"));
            candidates = candidates.Where(r => GameData.Int(r, "_iLevelNeed") >= bestLevel - 10).ToArray();
            int Weight(JsonElement row) => 1 << (6 - (int)Math.Clamp(GameData.Int(row, "_iQuality"), 2, 6));
            int ticket = random.Next(candidates.Sum(Weight));
            var chosen = candidates[0];
            foreach (var candidate in candidates)
            {
                ticket -= Weight(candidate);
                if (ticket < 0) { chosen = candidate; break; }
            }
            bot.EquipmentLook[grid] = GameData.Int(chosen, "_iID");
            if (grid == 0) twoHanded = GameData.Int(chosen, "_iEquipPart") == 1;
            foreach (var (id, amount) in GameData.Pairs(data.Row("EquipConfig", GameData.Int(chosen, "_iEquipId"))!.Value, "_kBasicAttribute"))
                raw[(int)id] = raw.GetValueOrDefault((int)id) + amount; // minimum roll; no refine, cards or unimplemented affixes
        }
        bool ranged = data.Row("JobConfig", bot.Career) is { } job && GameData.Int(job, "_iDistanceType") == 2;
        foreach (var (id, amount) in raw.Where(p => p.Key is >= 1 and <= 6).ToArray())
            if (data.Row("AttributeConversionConfig", id) is { } conversion)
                foreach (var (derived, factor) in GameData.Pairs(conversion, ranged ? "_kAttribute2" : "_kAttribute1"))
                    raw[(int)derived] = raw.GetValueOrDefault((int)derived) + amount * factor;
        foreach (var (id, value) in raw) bot.GearAttributes[id] = (long)Math.Ceiling(value);
        if (style != null) { ApplyStyle(bot, quality, seed, style); return; }

        bool Compatible(JsonElement row) => GameData.Int(row, "_iDisable") == 0 && GameData.Int(row, "_iLuxury") == 0
            && (GameData.Int(row, "_iSexNeed") == 0 || GameData.Int(row, "_iSexNeed") == bot.Gender)
            && HasModel(row, bot.Gender == 2 ? "_kModelResourceG" : "_kModelResourceB");
        var hair = wardrobe.Where(r => GameData.Int(r, "_iType") == 7 && GameData.Int(r, "_iDefaultLightUp") > 0 && Compatible(r)).ToArray();
        if (hair.Length > 0) bot.Appearances[7] = (int)GameData.Int(hair[random.Next(hair.Length)], "_iID");
        // Keep flashy outfits, wings, mounts and premium effects out of the automatic pool entirely.
        int wornQuality = bot.EquipmentLook.Where(id => id > 0).Select(id => (int)GameData.Int(data.Row("ItemConfig", id)!.Value, "_iQuality")).DefaultIfEmpty(2).Max();
        var hats = wardrobe.Where(r => GameData.Int(r, "_iType") == 1 && Compatible(r)
            && GameData.Int(r, "_iQuality") <= Math.Min(quality, wornQuality) && bot.Level >= 20
            && NoEffects(r, "_kEffectPathsB") && NoEffects(r, "_kEffectPathsG")).ToArray();
        if (hats.Length > 0 && random.Next(3) != 0) bot.Appearances[1] = (int)GameData.Int(hats[random.Next(hats.Length)], "_iID");
    }

    private bool Eligible(JsonElement item, PopulationUnit bot, int quality)
    {
        long id = GameData.Int(item, "_iID"), needed = GameData.Int(item, "_iLevelNeed"), rarity = GameData.Int(item, "_iQuality");
        if (id is <= 0 or >= 90000000 || needed < 1 || needed > bot.Level || rarity < 2 || rarity > quality
            || GameData.Int(item, "_iSexNeed") is var sex && sex != 0 && sex != bot.Gender) return false;
        if (!Allows(item, "_kJobNeed", bot.Career)) return false;
        long series = data.Row("JobConfig", bot.Career) is { } job ? GameData.Int(job, "_iSeries") : 0;
        if (!Allows(item, "_kSeriesNeed", series)) return false;
        if (data.Row("EquipConfig", GameData.Int(item, "_iEquipId")) is not { } equip) return false;
        // Rank/level labels alone are insufficient: exported debug items can claim level 1 and green rarity.
        foreach (var (attribute, amount) in GameData.Pairs(equip, "_kBasicAttribute"))
        {
            double cap = attribute switch { 101 => (200 + bot.Level * 50) * 2, 103 or 104 => (12 + bot.Level * 4) * 2,
                105 or 106 => Math.Max(20, bot.Level * 6), >= 1 and <= 6 => Math.Max(10, bot.Level * 3), _ => 10000 };
            if (amount < 0 || amount > cap) return false;
        }
        return true;
    }

    private static bool Allows(JsonElement row, string field, long value) => !row.TryGetProperty(field, out var list)
        || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0
        || list.EnumerateArray().Any(v => GameData.Number(v) is { } n && (n == 0 || n == value));
    private static bool HasModel(JsonElement row, string field) => row.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
    private static bool NoEffects(JsonElement row, string field) => !row.TryGetProperty(field, out var list)
        || list.ValueKind != JsonValueKind.Array || list.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.GetString()));
}
