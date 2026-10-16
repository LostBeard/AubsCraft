namespace AubsCraft.Live.Model.Biomes;

/// <summary>How a biome changes the grass colour: not at all, the dark forest's darkening, or the swamp's fixed murk.</summary>
public enum GrassModifier : byte { None = 0, DarkForest = 1, Swamp = 2 }

/// <summary>A biome's numbers that decide its colours (from the game's own biome files, 1.21.5): climate, and the colours it fixes itself (-1: not fixed, the colour map decides).</summary>
public sealed record BiomeInfo(string Name, float Temperature, float Downfall, int Water, int Grass, int Foliage, GrassModifier Modifier)
{
    public BiomeInfo(string name, float temperature, float downfall, int water, int grass, int foliage, int modifier)
        : this(name, temperature, downfall, water, grass, foliage, (GrassModifier)modifier) { }
}

/// <summary>
/// The vanilla biomes of Minecraft 1.21.5 in a fixed order: a biome's id is its index here, on the server (which tells the viewer the id of the biome of each 4 x 4 block of a column) and in the
/// viewer (which has a colour for each id). A biome that is not in the list (a data pack's, a newer version's) is <see cref="Unknown"/>: the viewer draws it as plains.
/// </summary>
public static class BiomeData
{
    public const byte Unknown = 255;

    public static readonly BiomeInfo[] All =
    {
        new("minecraft:badlands", 2.0f, 0.0f, 0x3F76E4, 0x90814D, 0x9E814D, 0),
        new("minecraft:bamboo_jungle", 0.95f, 0.9f, 0x3F76E4, -1, -1, 0),
        new("minecraft:basalt_deltas", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:beach", 0.8f, 0.4f, 0x3F76E4, -1, -1, 0),
        new("minecraft:birch_forest", 0.6f, 0.6f, 0x3F76E4, -1, -1, 0),
        new("minecraft:cherry_grove", 0.5f, 0.8f, 0x5DB7EF, 0xB6DB61, 0xB6DB61, 0),
        new("minecraft:cold_ocean", 0.5f, 0.5f, 0x3D57D6, -1, -1, 0),
        new("minecraft:crimson_forest", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:dark_forest", 0.7f, 0.8f, 0x3F76E4, -1, -1, 1),
        new("minecraft:deep_cold_ocean", 0.5f, 0.5f, 0x3D57D6, -1, -1, 0),
        new("minecraft:deep_dark", 0.8f, 0.4f, 0x3F76E4, -1, -1, 0),
        new("minecraft:deep_frozen_ocean", 0.5f, 0.5f, 0x3938C9, -1, -1, 0),
        new("minecraft:deep_lukewarm_ocean", 0.5f, 0.5f, 0x45ADF2, -1, -1, 0),
        new("minecraft:deep_ocean", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:desert", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:dripstone_caves", 0.8f, 0.4f, 0x3F76E4, -1, -1, 0),
        new("minecraft:end_barrens", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:end_highlands", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:end_midlands", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:eroded_badlands", 2.0f, 0.0f, 0x3F76E4, 0x90814D, 0x9E814D, 0),
        new("minecraft:flower_forest", 0.7f, 0.8f, 0x3F76E4, -1, -1, 0),
        new("minecraft:forest", 0.7f, 0.8f, 0x3F76E4, -1, -1, 0),
        new("minecraft:frozen_ocean", 0.0f, 0.5f, 0x3938C9, -1, -1, 0),
        new("minecraft:frozen_peaks", -0.7f, 0.9f, 0x3F76E4, -1, -1, 0),
        new("minecraft:frozen_river", 0.0f, 0.5f, 0x3938C9, -1, -1, 0),
        new("minecraft:grove", -0.2f, 0.8f, 0x3F76E4, -1, -1, 0),
        new("minecraft:ice_spikes", 0.0f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:jagged_peaks", -0.7f, 0.9f, 0x3F76E4, -1, -1, 0),
        new("minecraft:jungle", 0.95f, 0.9f, 0x3F76E4, -1, -1, 0),
        new("minecraft:lukewarm_ocean", 0.5f, 0.5f, 0x45ADF2, -1, -1, 0),
        new("minecraft:lush_caves", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:mangrove_swamp", 0.8f, 0.9f, 0x3A7A6A, -1, 0x8DB127, 2),
        new("minecraft:meadow", 0.5f, 0.8f, 0x0E4ECF, -1, -1, 0),
        new("minecraft:mushroom_fields", 0.9f, 1.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:nether_wastes", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:ocean", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:old_growth_birch_forest", 0.6f, 0.6f, 0x3F76E4, -1, -1, 0),
        new("minecraft:old_growth_pine_taiga", 0.3f, 0.8f, 0x3F76E4, -1, -1, 0),
        new("minecraft:old_growth_spruce_taiga", 0.25f, 0.8f, 0x3F76E4, -1, -1, 0),
        new("minecraft:pale_garden", 0.7f, 0.8f, 0x76889D, 0x778272, 0x878D76, 0),
        new("minecraft:plains", 0.8f, 0.4f, 0x3F76E4, -1, -1, 0),
        new("minecraft:river", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:savanna", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:savanna_plateau", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:small_end_islands", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:snowy_beach", 0.05f, 0.3f, 0x3D57D6, -1, -1, 0),
        new("minecraft:snowy_plains", 0.0f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:snowy_slopes", -0.3f, 0.9f, 0x3F76E4, -1, -1, 0),
        new("minecraft:snowy_taiga", -0.5f, 0.4f, 0x3D57D6, -1, -1, 0),
        new("minecraft:soul_sand_valley", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:sparse_jungle", 0.95f, 0.8f, 0x3F76E4, -1, -1, 0),
        new("minecraft:stony_peaks", 1.0f, 0.3f, 0x3F76E4, -1, -1, 0),
        new("minecraft:stony_shore", 0.2f, 0.3f, 0x3F76E4, -1, -1, 0),
        new("minecraft:sunflower_plains", 0.8f, 0.4f, 0x3F76E4, -1, -1, 0),
        new("minecraft:swamp", 0.8f, 0.9f, 0x617B64, -1, 0x6A7039, 2),
        new("minecraft:taiga", 0.25f, 0.8f, 0x3F76E4, -1, -1, 0),
        new("minecraft:the_end", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:the_void", 0.5f, 0.5f, 0x3F76E4, -1, -1, 0),
        new("minecraft:warm_ocean", 0.5f, 0.5f, 0x43D5EE, -1, -1, 0),
        new("minecraft:warped_forest", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:windswept_forest", 0.2f, 0.3f, 0x3F76E4, -1, -1, 0),
        new("minecraft:windswept_gravelly_hills", 0.2f, 0.3f, 0x3F76E4, -1, -1, 0),
        new("minecraft:windswept_hills", 0.2f, 0.3f, 0x3F76E4, -1, -1, 0),
        new("minecraft:windswept_savanna", 2.0f, 0.0f, 0x3F76E4, -1, -1, 0),
        new("minecraft:wooded_badlands", 2.0f, 0.0f, 0x3F76E4, 0x90814D, 0x9E814D, 0),
    };

    private static readonly Dictionary<string, byte> Ids = All.Select((b, i) => (b.Name, (byte)i)).ToDictionary(p => p.Name, p => p.Item2);

    /// <summary>The id of a biome by its name (<c>minecraft:plains</c>), or <see cref="Unknown"/>.</summary>
    public static byte IdOf(string name) => Ids.TryGetValue(name, out var id) ? id : Unknown;

    public static byte Plains => Ids["minecraft:plains"];
}
