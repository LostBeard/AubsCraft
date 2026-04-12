namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Maps Minecraft block names to RGB colors for the voxel renderer.
/// Colors are stored as packed float triplets (R, G, B) in [0-1] range.
/// </summary>
public static class BlockColorMap
{
    // Fallback color for unknown blocks (magenta)
    private static readonly (float R, float G, float B) Unknown = (0.9f, 0.1f, 0.9f);

    private static readonly Dictionary<string, (float R, float G, float B)> Colors = new()
    {
        // Natural terrain
        ["minecraft:grass_block"] = (0.30f, 0.65f, 0.20f),
        ["minecraft:dirt"] = (0.55f, 0.35f, 0.18f),
        ["minecraft:coarse_dirt"] = (0.50f, 0.33f, 0.17f),
        ["minecraft:rooted_dirt"] = (0.52f, 0.34f, 0.18f),
        ["minecraft:podzol"] = (0.45f, 0.30f, 0.15f),
        ["minecraft:mycelium"] = (0.50f, 0.40f, 0.50f),
        ["minecraft:mud"] = (0.35f, 0.28f, 0.22f),
        ["minecraft:farmland"] = (0.48f, 0.30f, 0.15f),
        ["minecraft:dirt_path"] = (0.58f, 0.45f, 0.25f),
        ["minecraft:sand"] = (0.85f, 0.78f, 0.52f),
        ["minecraft:red_sand"] = (0.72f, 0.42f, 0.18f),
        ["minecraft:gravel"] = (0.55f, 0.52f, 0.50f),
        ["minecraft:clay"] = (0.62f, 0.63f, 0.68f),
        ["minecraft:soul_sand"] = (0.35f, 0.28f, 0.20f),
        ["minecraft:soul_soil"] = (0.30f, 0.24f, 0.18f),

        // Stone types
        ["minecraft:stone"] = (0.50f, 0.50f, 0.50f),
        ["minecraft:granite"] = (0.60f, 0.42f, 0.35f),
        ["minecraft:diorite"] = (0.70f, 0.70f, 0.70f),
        ["minecraft:andesite"] = (0.55f, 0.55f, 0.55f),
        ["minecraft:cobblestone"] = (0.48f, 0.48f, 0.48f),
        ["minecraft:mossy_cobblestone"] = (0.40f, 0.50f, 0.35f),
        ["minecraft:smooth_stone"] = (0.55f, 0.55f, 0.55f),
        ["minecraft:stone_bricks"] = (0.50f, 0.50f, 0.50f),
        ["minecraft:deepslate"] = (0.30f, 0.30f, 0.32f),
        ["minecraft:cobbled_deepslate"] = (0.32f, 0.32f, 0.34f),
        ["minecraft:tuff"] = (0.45f, 0.47f, 0.42f),
        ["minecraft:calcite"] = (0.82f, 0.82f, 0.80f),
        ["minecraft:dripstone_block"] = (0.55f, 0.45f, 0.35f),
        ["minecraft:bedrock"] = (0.25f, 0.25f, 0.25f),
        ["minecraft:obsidian"] = (0.10f, 0.05f, 0.15f),
        ["minecraft:netherrack"] = (0.55f, 0.20f, 0.18f),
        ["minecraft:basalt"] = (0.30f, 0.30f, 0.35f),
        ["minecraft:blackstone"] = (0.18f, 0.15f, 0.18f),
        ["minecraft:end_stone"] = (0.85f, 0.85f, 0.60f),

        // Wood
        ["minecraft:oak_log"] = (0.45f, 0.35f, 0.20f),
        ["minecraft:spruce_log"] = (0.30f, 0.20f, 0.12f),
        ["minecraft:birch_log"] = (0.80f, 0.78f, 0.70f),
        ["minecraft:jungle_log"] = (0.40f, 0.30f, 0.15f),
        ["minecraft:acacia_log"] = (0.50f, 0.35f, 0.25f),
        ["minecraft:dark_oak_log"] = (0.25f, 0.18f, 0.10f),
        ["minecraft:cherry_log"] = (0.60f, 0.35f, 0.38f),
        ["minecraft:mangrove_log"] = (0.35f, 0.25f, 0.15f),
        ["minecraft:oak_planks"] = (0.60f, 0.48f, 0.28f),
        ["minecraft:spruce_planks"] = (0.40f, 0.28f, 0.15f),
        ["minecraft:birch_planks"] = (0.75f, 0.70f, 0.50f),
        ["minecraft:jungle_planks"] = (0.55f, 0.38f, 0.22f),
        ["minecraft:acacia_planks"] = (0.65f, 0.38f, 0.18f),
        ["minecraft:dark_oak_planks"] = (0.30f, 0.20f, 0.10f),
        ["minecraft:cherry_planks"] = (0.70f, 0.45f, 0.45f),

        // Leaves
        ["minecraft:oak_leaves"] = (0.18f, 0.55f, 0.15f),
        ["minecraft:spruce_leaves"] = (0.15f, 0.40f, 0.15f),
        ["minecraft:birch_leaves"] = (0.35f, 0.58f, 0.22f),
        ["minecraft:jungle_leaves"] = (0.12f, 0.50f, 0.10f),
        ["minecraft:acacia_leaves"] = (0.18f, 0.52f, 0.12f),
        ["minecraft:dark_oak_leaves"] = (0.12f, 0.42f, 0.10f),
        ["minecraft:cherry_leaves"] = (0.80f, 0.55f, 0.65f),
        ["minecraft:azalea_leaves"] = (0.25f, 0.55f, 0.20f),
        ["minecraft:mangrove_leaves"] = (0.18f, 0.48f, 0.12f),

        // Ores
        ["minecraft:coal_ore"] = (0.42f, 0.42f, 0.42f),
        ["minecraft:iron_ore"] = (0.52f, 0.45f, 0.40f),
        ["minecraft:copper_ore"] = (0.55f, 0.45f, 0.35f),
        ["minecraft:gold_ore"] = (0.60f, 0.55f, 0.30f),
        ["minecraft:redstone_ore"] = (0.55f, 0.30f, 0.30f),
        ["minecraft:emerald_ore"] = (0.35f, 0.55f, 0.35f),
        ["minecraft:lapis_ore"] = (0.35f, 0.40f, 0.60f),
        ["minecraft:diamond_ore"] = (0.40f, 0.60f, 0.60f),
        ["minecraft:ancient_debris"] = (0.35f, 0.25f, 0.20f),

        // Mineral blocks
        ["minecraft:coal_block"] = (0.15f, 0.15f, 0.15f),
        ["minecraft:iron_block"] = (0.72f, 0.72f, 0.72f),
        ["minecraft:gold_block"] = (0.85f, 0.75f, 0.20f),
        ["minecraft:diamond_block"] = (0.40f, 0.80f, 0.80f),
        ["minecraft:emerald_block"] = (0.20f, 0.75f, 0.30f),
        ["minecraft:lapis_block"] = (0.15f, 0.25f, 0.65f),
        ["minecraft:redstone_block"] = (0.70f, 0.10f, 0.05f),
        ["minecraft:copper_block"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:netherite_block"] = (0.25f, 0.22f, 0.22f),
        ["minecraft:amethyst_block"] = (0.55f, 0.35f, 0.70f),

        // Water and lava
        ["minecraft:water"] = (0.20f, 0.40f, 0.80f),
        ["minecraft:lava"] = (0.90f, 0.45f, 0.05f),

        // Snow and ice
        ["minecraft:snow_block"] = (0.92f, 0.95f, 0.98f),
        ["minecraft:snow"] = (0.92f, 0.95f, 0.98f),
        ["minecraft:ice"] = (0.60f, 0.75f, 0.95f),
        ["minecraft:packed_ice"] = (0.55f, 0.70f, 0.90f),
        ["minecraft:blue_ice"] = (0.45f, 0.60f, 0.90f),
        ["minecraft:powder_snow"] = (0.95f, 0.97f, 1.00f),

        // Building materials
        ["minecraft:bricks"] = (0.60f, 0.35f, 0.28f),
        ["minecraft:sandstone"] = (0.80f, 0.75f, 0.55f),
        ["minecraft:red_sandstone"] = (0.70f, 0.40f, 0.20f),
        ["minecraft:prismarine"] = (0.35f, 0.60f, 0.55f),
        ["minecraft:dark_prismarine"] = (0.20f, 0.40f, 0.35f),
        ["minecraft:sea_lantern"] = (0.75f, 0.85f, 0.85f),
        ["minecraft:glowstone"] = (0.85f, 0.75f, 0.40f),
        ["minecraft:nether_bricks"] = (0.25f, 0.12f, 0.15f),
        ["minecraft:purpur_block"] = (0.60f, 0.45f, 0.60f),
        ["minecraft:quartz_block"] = (0.88f, 0.85f, 0.82f),
        ["minecraft:terracotta"] = (0.60f, 0.40f, 0.30f),

        // Colored blocks (wool, concrete - representative set)
        ["minecraft:white_wool"] = (0.90f, 0.90f, 0.90f),
        ["minecraft:white_concrete"] = (0.88f, 0.88f, 0.88f),
        ["minecraft:black_wool"] = (0.10f, 0.10f, 0.10f),
        ["minecraft:red_wool"] = (0.65f, 0.15f, 0.15f),
        ["minecraft:blue_wool"] = (0.20f, 0.25f, 0.65f),
        ["minecraft:green_wool"] = (0.30f, 0.50f, 0.15f),
        ["minecraft:yellow_wool"] = (0.85f, 0.80f, 0.20f),

        // Glass
        ["minecraft:glass"] = (0.70f, 0.80f, 0.85f),
        ["minecraft:tinted_glass"] = (0.20f, 0.18f, 0.25f),

        // Functional
        ["minecraft:crafting_table"] = (0.55f, 0.38f, 0.20f),
        ["minecraft:furnace"] = (0.48f, 0.48f, 0.48f),
        ["minecraft:chest"] = (0.55f, 0.40f, 0.18f),
        ["minecraft:bookshelf"] = (0.50f, 0.38f, 0.22f),
        ["minecraft:enchanting_table"] = (0.15f, 0.10f, 0.20f),
        ["minecraft:anvil"] = (0.35f, 0.35f, 0.35f),
        ["minecraft:beacon"] = (0.50f, 0.85f, 0.85f),
        ["minecraft:tnt"] = (0.75f, 0.20f, 0.15f),
        ["minecraft:sponge"] = (0.80f, 0.80f, 0.30f),
    };

    /// <summary>
    /// Get the RGB color for a block name. Returns magenta for unknown blocks.
    /// </summary>
    public static (float R, float G, float B) GetColor(string blockName)
    {
        return Colors.GetValueOrDefault(blockName, Unknown);
    }

    /// <summary>
    /// Build a flat float array of RGB colors for a palette (3 floats per entry).
    /// Index into this array as: paletteIndex * 3 + channel.
    /// </summary>
    public static float[] BuildPaletteColors(List<string> palette)
    {
        var colors = new float[palette.Count * 3];
        for (int i = 0; i < palette.Count; i++)
        {
            var (r, g, b) = GetColor(palette[i]);
            colors[i * 3] = r;
            colors[i * 3 + 1] = g;
            colors[i * 3 + 2] = b;
        }
        return colors;
    }
}
