namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Maps Minecraft block names to texture atlas UV coordinates.
/// Atlas: 16x16 grid of 16x16 textures (256x256 pixels).
/// </summary>
public static class TextureAtlas
{
    private const float T = 1f / 16f; // tile size in UV space

    private static readonly Dictionary<string, int> BlockToIndex = new()
    {
        // Row 0: terrain basics
        ["minecraft:grass_block"] = 0,
        ["minecraft:dirt"] = 1,
        ["minecraft:stone"] = 2,
        ["minecraft:sand"] = 3,
        ["minecraft:gravel"] = 4,
        ["minecraft:cobblestone"] = 5,
        ["minecraft:clay"] = 6,
        ["minecraft:coarse_dirt"] = 7,
        ["minecraft:rooted_dirt"] = 1,
        ["minecraft:mud"] = 1,
        ["minecraft:dirt_path"] = 1,
        ["minecraft:farmland"] = 73,

        // Row 1: log tops
        ["minecraft:oak_log"] = 8,
        ["minecraft:birch_log"] = 9,
        ["minecraft:spruce_log"] = 10,
        ["minecraft:dark_oak_log"] = 11,
        ["minecraft:jungle_log"] = 12,
        ["minecraft:acacia_log"] = 13,
        ["minecraft:cherry_log"] = 14,
        ["minecraft:mangrove_log"] = 15,
        ["minecraft:stripped_oak_log"] = 8,
        ["minecraft:stripped_oak_wood"] = 8,

        // Row 2: leaves
        ["minecraft:oak_leaves"] = 16,
        ["minecraft:birch_leaves"] = 17,
        ["minecraft:spruce_leaves"] = 18,
        ["minecraft:dark_oak_leaves"] = 19,
        ["minecraft:jungle_leaves"] = 20,
        ["minecraft:acacia_leaves"] = 21,
        ["minecraft:cherry_leaves"] = 22,
        ["minecraft:azalea_leaves"] = 16,
        ["minecraft:mangrove_leaves"] = 20,
        ["minecraft:fern"] = 23,
        ["minecraft:large_fern"] = 23,

        // Row 3: stone types
        ["minecraft:andesite"] = 24,
        ["minecraft:diorite"] = 25,
        ["minecraft:granite"] = 26,
        ["minecraft:deepslate"] = 27,
        ["minecraft:cobbled_deepslate"] = 27,
        ["minecraft:tuff"] = 28,
        ["minecraft:tuff_bricks"] = 28,
        ["minecraft:polished_tuff"] = 28,
        ["minecraft:calcite"] = 29,
        ["minecraft:bedrock"] = 30,
        ["minecraft:obsidian"] = 31,
        ["minecraft:crying_obsidian"] = 31,

        // Row 4: ores
        ["minecraft:coal_ore"] = 32,
        ["minecraft:iron_ore"] = 33,
        ["minecraft:copper_ore"] = 34,
        ["minecraft:gold_ore"] = 35,
        ["minecraft:redstone_ore"] = 36,
        ["minecraft:lapis_ore"] = 37,
        ["minecraft:diamond_ore"] = 38,
        ["minecraft:emerald_ore"] = 39,
        ["minecraft:deepslate_coal_ore"] = 32,
        ["minecraft:deepslate_iron_ore"] = 33,
        ["minecraft:deepslate_copper_ore"] = 34,
        ["minecraft:deepslate_gold_ore"] = 35,
        ["minecraft:deepslate_redstone_ore"] = 36,
        ["minecraft:deepslate_lapis_ore"] = 37,
        ["minecraft:deepslate_diamond_ore"] = 38,

        // Row 5: planks + bricks
        ["minecraft:oak_planks"] = 40,
        ["minecraft:birch_planks"] = 41,
        ["minecraft:spruce_planks"] = 42,
        ["minecraft:dark_oak_planks"] = 43,
        ["minecraft:jungle_planks"] = 44,
        ["minecraft:pale_oak_planks"] = 45,
        ["minecraft:cherry_planks"] = 45,
        ["minecraft:acacia_planks"] = 40,
        ["minecraft:bricks"] = 46,
        ["minecraft:stone_bricks"] = 47,
        ["minecraft:mossy_stone_bricks"] = 47,
        ["minecraft:cracked_stone_bricks"] = 47,
        ["minecraft:chiseled_stone_bricks"] = 47,

        // Stairs/slabs reuse their base material
        ["minecraft:oak_stairs"] = 40,
        ["minecraft:oak_slab"] = 40,
        ["minecraft:spruce_stairs"] = 42,
        ["minecraft:birch_stairs"] = 41,
        ["minecraft:dark_oak_stairs"] = 43,
        ["minecraft:jungle_stairs"] = 44,
        ["minecraft:cobblestone_stairs"] = 5,
        ["minecraft:stone_brick_stairs"] = 47,
        ["minecraft:stone_brick_slab"] = 47,

        // Row 6: natural
        ["minecraft:snow_block"] = 48,
        ["minecraft:snow"] = 48,
        ["minecraft:powder_snow"] = 48,
        ["minecraft:ice"] = 49,
        ["minecraft:packed_ice"] = 49,
        ["minecraft:blue_ice"] = 49,
        ["minecraft:water"] = 50,
        ["minecraft:flowing_water"] = 50,
        ["minecraft:lava"] = 51,
        ["minecraft:flowing_lava"] = 51,
        ["minecraft:podzol"] = 52,
        ["minecraft:mycelium"] = 53,
        ["minecraft:netherrack"] = 54,
        ["minecraft:mossy_cobblestone"] = 55,

        // Row 7: misc
        ["minecraft:red_sand"] = 56,
        ["minecraft:sandstone"] = 57,
        ["minecraft:chiseled_sandstone"] = 57,
        ["minecraft:cut_sandstone"] = 57,
        ["minecraft:red_sandstone"] = 56,
        ["minecraft:terracotta"] = 58,
        ["minecraft:white_terracotta"] = 58,
        ["minecraft:light_blue_terracotta"] = 58,
        ["minecraft:glowstone"] = 59,
        ["minecraft:sea_lantern"] = 59,
        ["minecraft:soul_sand"] = 60,
        ["minecraft:soul_soil"] = 60,
        ["minecraft:bone_block"] = 61,
        ["minecraft:hay_block"] = 62,
        ["minecraft:pumpkin"] = 63,

        // Plants/flowers: use flat color (they're cross-shaped, not blocks)
        // Do NOT map these to atlas - their textures are side views that look wrong on top faces

        // Row 9: more
        ["minecraft:melon"] = 72,
        ["minecraft:smooth_stone"] = 2,
        ["minecraft:smooth_basalt"] = 27,
        ["minecraft:basalt"] = 27,
        ["minecraft:blackstone"] = 30,

        // Fences/doors use base wood
        ["minecraft:oak_fence"] = 40,
        ["minecraft:spruce_fence"] = 42,
        ["minecraft:birch_fence"] = 41,
        ["minecraft:dark_oak_fence"] = 43,
        ["minecraft:jungle_fence"] = 44,
    };

    public static (float u, float v) GetUV(string blockName)
    {
        if (BlockToIndex.TryGetValue(blockName, out var index))
            return ((index % 16) * T, (index / 16) * T);
        return (-1f, -1f);
    }

    public static (float u0, float v0, float u1, float v1) GetTileUVs(string blockName)
    {
        var (u, v) = GetUV(blockName);
        if (u < 0) return (-1, -1, -1, -1);
        return (u, v, u + T, v + T);
    }
}
