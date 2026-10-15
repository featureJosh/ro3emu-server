namespace Ro3Emu.Server.Game;

/// <summary>Offline, deterministic handle generator. Mixed-radix parts are unique until the suffix rolls over.</summary>
internal static class PopulationNames
{
    private static readonly string[] First = ["Ari", "Bri", "Cae", "Dae", "Eli", "Fae", "Gali", "Haru", "Iri", "Juni", "Kai", "Lumi", "Mira", "Nori", "Ori", "Piri", "Quin", "Rin", "Sora", "Tali", "Una", "Vivi", "Wren", "Xan", "Yuki", "Zeri", "Aster", "Ember", "Luna", "Nova", "Rowan", "Vale"];
    private static readonly string[] Last = ["wyn", "fall", "leaf", "bloom", "song", "light", "dawn", "moon", "vale", "mist", "star", "wing", "wind", "heart", "thorn", "spark", "frost", "rose", "rain", "snow", "glow", "brook", "shade", "flame", "sky", "stone", "fern", "cloud", "crest", "moss", "dream", "rune"];
    public static string ForSerial(long serial)
    {
        if (serial <= 0) throw new ArgumentOutOfRangeException(nameof(serial));
        long n = serial - 1;
        string name = First[(int)(n % First.Length)] + Last[(int)(n / First.Length % Last.Length)];
        long cycle = n / (First.Length * Last.Length);
        return cycle == 0 ? name : name + cycle.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
