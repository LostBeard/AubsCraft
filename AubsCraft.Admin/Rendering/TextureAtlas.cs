namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Maps Minecraft block names to texture atlas UV coordinates.
/// The atlas is an 8x8 grid of 16x16 textures (128x128 pixels).
/// Each tile occupies 1/8 of the atlas in each dimension.
/// </summary>
public static class TextureAtlas
{
    private const float TileSize = 1f / 8f; // 0.125 - each tile is 1/8 of the atlas

    // Atlas layout: row-major, 8 tiles per row
    // Index = row * 8 + col, UV = (col/8, row/8)
    private static readonly Dictionary<string, int> BlockToIndex = new()
    {
        // Row 0
        ["minecraft:grass_block"] = 0,      // grass_block_top
        ["minecraft:dirt"] = 1,
        ["minecraft:stone"] = 2,
        ["minecraft:sand"] = 3,
        ["minecraft:gravel"] = 4,
        ["minecraft:cobblestone"] = 5,
        ["minecraft:oak_log"] = 6,          // oak_log_top
        ["minecraft:birch_log"] = 7,        // birch_log_top

        // Row 1
        ["minecraft:spruce_log"] = 8,       // spruce_log_top
        ["minecraft:dark_oak_log"] = 9,     // dark_oak_log_top
        ["minecraft:oak_leaves"] = 10,
        ["minecraft:birch_leaves"] = 11,
        ["minecraft:spruce_leaves"] = 12,
        ["minecraft:dark_oak_leaves"] = 13,
        ["minecraft:jungle_leaves"] = 14,
        // 15 = oak_log side (not used for top view)

        // Row 2
        // 16-19 = log sides
        ["minecraft:andesite"] = 20,
        ["minecraft:diorite"] = 21,
        ["minecraft:granite"] = 22,
        ["minecraft:deepslate"] = 23,

        // Row 3
        ["minecraft:bedrock"] = 24,
        ["minecraft:clay"] = 25,
        ["minecraft:sandstone"] = 26,       // sandstone_top
        ["minecraft:coal_ore"] = 27,
        ["minecraft:iron_ore"] = 28,
        ["minecraft:gold_ore"] = 29,
        ["minecraft:diamond_ore"] = 30,
        ["minecraft:snow_block"] = 31,
        ["minecraft:snow"] = 31,

        // Row 4
        ["minecraft:ice"] = 32,
        ["minecraft:water"] = 33,
        ["minecraft:flowing_water"] = 33,
        ["minecraft:podzol"] = 34,          // podzol_top
        ["minecraft:mycelium"] = 35,        // mycelium_top
        ["minecraft:netherrack"] = 36,
        ["minecraft:cobbled_deepslate"] = 5,  // reuse cobblestone-like
        ["minecraft:coarse_dirt"] = 1,        // reuse dirt
        ["minecraft:farmland"] = 1,
        ["minecraft:dirt_path"] = 1,
        ["minecraft:rooted_dirt"] = 1,
        ["minecraft:mud"] = 1,

        // Reuse mappings for common variants
        ["minecraft:stone_bricks"] = 2,
        ["minecraft:mossy_cobblestone"] = 5,
        ["minecraft:mossy_stone_bricks"] = 2,
        ["minecraft:smooth_stone"] = 2,
        ["minecraft:packed_ice"] = 32,
        ["minecraft:blue_ice"] = 32,
        ["minecraft:red_sand"] = 3,
        ["minecraft:acacia_log"] = 6,
        ["minecraft:jungle_log"] = 6,
        ["minecraft:cherry_log"] = 6,
        ["minecraft:mangrove_log"] = 6,
        ["minecraft:acacia_leaves"] = 10,
        ["minecraft:cherry_leaves"] = 11,
        ["minecraft:azalea_leaves"] = 10,
        ["minecraft:mangrove_leaves"] = 14,
    };

    /// <summary>
    /// Get the UV coordinates for a block's top face in the atlas.
    /// Returns (u, v) for the top-left corner of the tile.
    /// Returns (-1, -1) if the block has no atlas entry (use flat color instead).
    /// </summary>
    public static (float u, float v) GetUV(string blockName)
    {
        if (BlockToIndex.TryGetValue(blockName, out var index))
        {
            float u = (index % 8) * TileSize;
            float v = (index / 8) * TileSize;
            return (u, v);
        }
        return (-1f, -1f);
    }

    /// <summary>
    /// Get UV coordinates for all 4 corners of a tile: (u0,v0), (u1,v1).
    /// </summary>
    public static (float u0, float v0, float u1, float v1) GetTileUVs(string blockName)
    {
        var (u, v) = GetUV(blockName);
        if (u < 0) return (-1, -1, -1, -1);
        return (u, v, u + TileSize, v + TileSize);
    }
}
