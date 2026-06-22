namespace MTerminal.Services;

public static class TileNameGenerator
{
    private static readonly string[] Adjectives =
    [
        "Swift", "Brave", "Calm", "Dark", "Epic", "Fast", "Gold", "Hazy",
        "Iron", "Jade", "Keen", "Loud", "Mild", "Neon", "Odd", "Pure",
        "Quick", "Raw", "Shy", "Tidy", "Uber", "Vast", "Warm", "Zen",
        "Agile", "Bold", "Cool", "Deep", "Easy", "Fine", "Grim", "Hype",
        "Icy", "Just", "Kind", "Lean", "Mean", "Nice", "Open", "Peak",
        "Quiet", "Rich", "Slim", "True", "Ultra", "Vivid", "Wild", "Young",
        "Acid", "Blue", "Crisp", "Deft", "Edge", "Flat", "Gray", "High",
        "Inky", "Jolly", "Lucky", "Mint", "Next", "Opal", "Pink", "Red",
        "Soft", "Tiny", "Vital", "Wise", "Zany", "Amber", "Black", "Clear",
        "Dry", "Fancy", "Grand", "Hot", "Ivory", "Jumpy", "Lazy", "Misty",
        "Noble", "Plush", "Royal", "Snowy", "Thick", "Void", "White", "Zero",
        "Ashen", "Burnt", "Coral", "Dusty", "Foggy", "Green", "Lunar", "Rusty",
        "Sandy", "Stormy", "Solar", "Frosty"
    ];

    private static readonly string[] Animals =
    [
        "Fox", "Owl", "Cat", "Dog", "Bear", "Hawk", "Wolf", "Deer",
        "Lynx", "Crow", "Hare", "Duck", "Frog", "Goat", "Lion", "Mole",
        "Newt", "Orca", "Pike", "Ram", "Seal", "Toad", "Vole", "Wren",
        "Ape", "Bat", "Crab", "Dove", "Elk", "Finch", "Gecko", "Horse",
        "Ibis", "Jay", "Koi", "Lark", "Moth", "Osprey", "Puma", "Quail",
        "Robin", "Swan", "Tiger", "Viper", "Whale", "Yak", "Zebra", "Ant",
        "Bee", "Clam", "Dingo", "Eagle", "Ferret", "Gull", "Hippo", "Iguana",
        "Jackal", "Koala", "Llama", "Moose", "Narwhal", "Otter", "Panda", "Raven",
        "Shark", "Turtle", "Urchin", "Vulture", "Wasp", "Axolotl", "Bison", "Cobra",
        "Crane", "Falcon", "Grouse", "Heron", "Lemur", "Mamba", "Parrot", "Squid",
        "Stork", "Trout", "Wombat", "Badger", "Coyote", "Donkey", "Ermine", "Gibbon",
        "Hornet", "Mantis", "Oyster", "Pelican", "Salmon", "Toucan", "Walrus", "Alpaca",
        "Panther", "Sparrow", "Condor", "Magpie"
    ];

    // 100 × 100 = 10,000 unique combinations
    public static string Generate(IReadOnlySet<string> usedNames)
    {
        const int maxAttempts = 50;
        for (var i = 0; i < maxAttempts; i++)
        {
            var adj = Adjectives[Random.Shared.Next(Adjectives.Length)];
            var animal = Animals[Random.Shared.Next(Animals.Length)];
            var name = $"Terminal#{adj}{animal}";
            if (!usedNames.Contains(name))
                return name;
        }

        // Exhaustive fallback — scan all combos
        foreach (var adj in Adjectives)
        foreach (var animal in Animals)
        {
            var name = $"Terminal#{adj}{animal}";
            if (!usedNames.Contains(name))
                return name;
        }

        // Truly exhausted (10k+ terminals) — numeric fallback
        var n = usedNames.Count + 1;
        while (usedNames.Contains($"Terminal#{n}")) n++;
        return $"Terminal#{n}";
    }
}
