namespace Ro3Emu.Server.Game;

/// <summary>Original offline handle bank with mixed player-name styles, shuffled without replacement.</summary>
internal static class PopulationNames
{
    private static readonly string[] Aliases = ["Ren", "Mika", "Kuro", "Aki", "Neko", "Rook", "Zed", "Mochi", "Miso", "Nox", "Kira", "Yuna", "Suki", "Finn", "Jinx", "Echo", "Lynx", "Raven", "Ash", "Dex", "Theo", "Remy", "Juno", "Nina", "Alex", "Sam", "Leo", "Kai", "Ruby", "Ivy", "Ezra", "Nico", "Koda", "Faye", "Rei", "Luca", "Rory", "Tess", "Ava", "Milo", "Taro", "Lily", "Zoe", "Arlo", "Rin", "Noel", "Mina", "Sage", "Cleo", "Toby", "Ellie", "Robin", "Miles", "Pip", "Wren", "Seth", "Jade", "Quinn", "Lark", "Kian", "Eden", "Cody", "Sora", "Nyx"];
    private static readonly string[] Descriptors = ["Sleepy", "Lucky", "Tiny", "Lazy", "Sneaky", "Lost", "Quiet", "Salty", "Happy", "Cozy", "Chill", "Spicy", "Swift", "Hidden", "Silver", "Midnight", "Crimson", "Dusty", "Wild", "Fuzzy", "Velvet", "Pocket", "Pixel", "Turbo", "Rusty", "Golden", "Wandering", "Little", "Ghost", "Lunar", "Mossy", "Sunny"];
    private static readonly string[] Things = ["Poring", "Bard", "Knight", "Mage", "Healer", "Merchant", "Archer", "Rogue", "Paladin", "Fox", "Wolf", "Crow", "Otter", "Panda", "Cat", "Owl", "Ramen", "Coffee", "Toast", "Waffle", "Noodle", "Mochi", "Pickle", "Peach", "Cookie", "Moon", "Comet", "Meteor", "Lotus", "Clover", "Flame", "Spark", "Frost", "Storm", "Rune", "Blade", "Arrow", "Potion", "Leaf", "Wisp"];
    private static readonly int[] Numbers = [7, 13, 21, 27, 42, 64, 77, 88, 99];
    private static readonly string[] Pool = BuildPool();

    public static string ForSerial(long serial)
    {
        if (serial <= 0) throw new ArgumentOutOfRangeException(nameof(serial));
        long n = serial - 1;
        string name = Pool[(int)(n % Pool.Length)];
        long cycle = n / Pool.Length;
        // The bank has no underscores, so the rollover namespace cannot collide with its numeric handles.
        return cycle == 0 ? name : name + "_" + cycle.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] BuildPool()
    {
        var names = new List<string>();
        foreach (string alias in Aliases)
        {
            names.Add(alias);
            names.Add("x" + alias + "x");
            foreach (int number in Numbers) names.Add(alias + number);
        }
        foreach (string descriptor in Descriptors)
            foreach (string thing in Things)
            {
                string handle = descriptor + thing;
                names.Add(names.Count % 3 == 0 ? char.ToLowerInvariant(handle[0]) + handle[1..] : handle);
            }
        foreach (string alias in Aliases)
            foreach (string thing in new[] { "Plays", "AFK", "Main", "RO", "Quest", "Online" }) names.Add(alias + thing);
        // Treat case-only variants as the same handle; each active cohort gets a mixed, non-repeating bank.
        var bank = names.Where(n => n.Length <= 12).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var random = new Random(0x524f33);
        for (int i = bank.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (bank[i], bank[j]) = (bank[j], bank[i]);
        }
        return bank;
    }
}
