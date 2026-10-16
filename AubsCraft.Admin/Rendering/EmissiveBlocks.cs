namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Which blocks make their own light (the game's light-emitting blocks that glow in the picture itself): the viewer draws them at full brightness at any hour, so a lava lake, a glowstone
/// lamp or a row of torches is lit at midnight though nothing in the viewer lights the world by block light. A block name with its state: <c>minecraft:redstone_lamp[lit=true]</c>.
/// </summary>
public static class EmissiveBlocks
{
    private static readonly HashSet<string> Always = new(StringComparer.Ordinal)
    {
        "lava", "magma_block", "glowstone", "sea_lantern", "shroomlight", "ochre_froglight", "verdant_froglight", "pearlescent_froglight", "beacon", "jack_o_lantern", "fire", "soul_fire",
        "lantern", "soul_lantern", "torch", "wall_torch", "soul_torch", "soul_wall_torch", "end_rod", "nether_portal", "crying_obsidian", "conduit", "glow_lichen", "end_portal", "end_gateway",
    };

    // These give light only when lit, or charged
    private static readonly HashSet<string> WhenLit = new(StringComparer.Ordinal)
    {
        "redstone_torch", "redstone_wall_torch", "redstone_lamp", "campfire", "soul_campfire", "copper_bulb", "exposed_copper_bulb", "weathered_copper_bulb", "oxidized_copper_bulb",
        "waxed_copper_bulb", "waxed_exposed_copper_bulb", "waxed_weathered_copper_bulb", "waxed_oxidized_copper_bulb",
    };

    public static bool Is(string name)
    {
        int bracket = name.IndexOf('[');
        var bare = bracket < 0 ? name : name[..bracket];
        if (bare.StartsWith("minecraft:", StringComparison.Ordinal)) bare = bare[10..];
        if (Always.Contains(bare)) return true;
        if (!WhenLit.Contains(bare)) return bare == "respawn_anchor" && bracket >= 0 && !name.Contains("charges=0", StringComparison.Ordinal);
        // A torch or a campfire with no "lit" in its name is lit (the namer keeps only what changes the look)
        return bracket < 0 || !name.Contains("lit=false", StringComparison.Ordinal);
    }
}
