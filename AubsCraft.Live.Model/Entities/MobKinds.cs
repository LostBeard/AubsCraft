namespace AubsCraft.Live.Model.Entities;

/// <summary>How a creature stands to a player: what the minimap colours it by.</summary>
public enum MobKind { Passive, Neutral, Hostile, Villager }

/// <summary>Which creature is which kind (the game's own mob categories: monsters attack on sight, neutral ones only when provoked, villagers trade).</summary>
public static class MobKinds
{
    private static readonly HashSet<string> Hostile = new(StringComparer.Ordinal)
    {
        "blaze", "bogged", "breeze", "cave_spider", "creaking", "creeper", "drowned", "elder_guardian", "ender_dragon", "endermite", "evoker", "ghast", "giant", "guardian", "hoglin",
        "husk", "illusioner", "magma_cube", "phantom", "piglin_brute", "pillager", "ravager", "shulker", "silverfish", "skeleton", "slime", "stray", "vex", "vindicator", "warden",
        "witch", "wither", "wither_skeleton", "zoglin", "zombie", "zombie_villager",
    };

    private static readonly HashSet<string> Neutral = new(StringComparer.Ordinal)
    {
        "bee", "dolphin", "enderman", "goat", "iron_golem", "llama", "trader_llama", "panda", "piglin", "polar_bear", "spider", "wolf", "zombified_piglin", "snow_golem",
    };

    /// <summary>The kind of a creature type ("zombie", "minecraft:zombie"); anything not listed (animals, fish, a mod's creature) is passive.</summary>
    public static MobKind Of(string type)
    {
        var key = EntityModels.Normalise(type);
        if (key is "villager" or "wandering_trader") return MobKind.Villager;
        if (Hostile.Contains(key)) return MobKind.Hostile;
        if (Neutral.Contains(key)) return MobKind.Neutral;
        return MobKind.Passive;
    }
}
