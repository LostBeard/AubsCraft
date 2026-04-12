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
        // Natural terrain (colors from actual Minecraft textures where applicable)
        ["minecraft:grass_block"] = (0.30f, 0.65f, 0.20f), // biome-tinted, manual color
        ["minecraft:dirt"] = (0.53f, 0.38f, 0.26f),
        ["minecraft:coarse_dirt"] = (0.50f, 0.33f, 0.17f),
        ["minecraft:rooted_dirt"] = (0.52f, 0.34f, 0.18f),
        ["minecraft:podzol"] = (0.45f, 0.30f, 0.15f),
        ["minecraft:mycelium"] = (0.50f, 0.40f, 0.50f),
        ["minecraft:mud"] = (0.35f, 0.28f, 0.22f),
        ["minecraft:farmland"] = (0.48f, 0.30f, 0.15f),
        ["minecraft:dirt_path"] = (0.58f, 0.45f, 0.25f),
        ["minecraft:sand"] = (0.86f, 0.81f, 0.64f),
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

        // Plants and flowers
        ["minecraft:short_grass"] = (0.30f, 0.60f, 0.18f),
        ["minecraft:tall_grass"] = (0.28f, 0.58f, 0.16f),
        ["minecraft:fern"] = (0.25f, 0.55f, 0.20f),
        ["minecraft:dandelion"] = (0.90f, 0.85f, 0.15f),
        ["minecraft:poppy"] = (0.80f, 0.15f, 0.10f),
        ["minecraft:cornflower"] = (0.30f, 0.40f, 0.85f),
        ["minecraft:azure_bluet"] = (0.85f, 0.90f, 0.92f),
        ["minecraft:oxeye_daisy"] = (0.90f, 0.90f, 0.80f),
        ["minecraft:lilac"] = (0.65f, 0.40f, 0.65f),
        ["minecraft:rose_bush"] = (0.70f, 0.15f, 0.12f),
        ["minecraft:wildflowers"] = (0.75f, 0.50f, 0.60f),
        ["minecraft:firefly_bush"] = (0.30f, 0.55f, 0.20f),
        ["minecraft:sugar_cane"] = (0.45f, 0.70f, 0.30f),
        ["minecraft:kelp"] = (0.20f, 0.45f, 0.25f),
        ["minecraft:kelp_plant"] = (0.18f, 0.42f, 0.22f),
        ["minecraft:seagrass"] = (0.15f, 0.50f, 0.25f),
        ["minecraft:tall_seagrass"] = (0.15f, 0.48f, 0.22f),
        ["minecraft:vine"] = (0.18f, 0.50f, 0.15f),
        ["minecraft:glow_lichen"] = (0.55f, 0.70f, 0.50f),
        ["minecraft:brown_mushroom"] = (0.55f, 0.40f, 0.25f),
        ["minecraft:red_mushroom"] = (0.75f, 0.15f, 0.10f),
        ["minecraft:pumpkin"] = (0.85f, 0.55f, 0.10f),
        ["minecraft:melon"] = (0.45f, 0.65f, 0.20f),
        ["minecraft:wheat"] = (0.80f, 0.70f, 0.25f),
        ["minecraft:carrots"] = (0.85f, 0.50f, 0.10f),
        ["minecraft:potatoes"] = (0.55f, 0.50f, 0.25f),
        ["minecraft:beetroots"] = (0.60f, 0.20f, 0.15f),
        ["minecraft:leaf_litter"] = (0.40f, 0.35f, 0.20f),
        ["minecraft:hay_block"] = (0.80f, 0.72f, 0.20f),
        ["minecraft:bubble_column"] = (0.30f, 0.50f, 0.85f),
        ["minecraft:cave_air"] = (0f, 0f, 0f), // transparent, treat as air
        ["minecraft:cobweb"] = (0.85f, 0.85f, 0.85f),

        // Stairs/slabs/walls (use base material color)
        ["minecraft:oak_stairs"] = (0.60f, 0.48f, 0.28f),
        ["minecraft:oak_slab"] = (0.60f, 0.48f, 0.28f),
        ["minecraft:spruce_stairs"] = (0.40f, 0.28f, 0.15f),
        ["minecraft:spruce_slab"] = (0.40f, 0.28f, 0.15f),
        ["minecraft:birch_stairs"] = (0.75f, 0.70f, 0.50f),
        ["minecraft:birch_slab"] = (0.75f, 0.70f, 0.50f),
        ["minecraft:jungle_stairs"] = (0.55f, 0.38f, 0.22f),
        ["minecraft:jungle_slab"] = (0.55f, 0.38f, 0.22f),
        ["minecraft:dark_oak_stairs"] = (0.30f, 0.20f, 0.10f),
        ["minecraft:cherry_stairs"] = (0.70f, 0.45f, 0.45f),
        ["minecraft:pale_oak_planks"] = (0.78f, 0.75f, 0.70f),
        ["minecraft:pale_oak_stairs"] = (0.78f, 0.75f, 0.70f),
        ["minecraft:cobblestone_stairs"] = (0.48f, 0.48f, 0.48f),
        ["minecraft:cobblestone_wall"] = (0.48f, 0.48f, 0.48f),
        ["minecraft:stone_brick_stairs"] = (0.50f, 0.50f, 0.50f),
        ["minecraft:stone_brick_slab"] = (0.50f, 0.50f, 0.50f),
        ["minecraft:mossy_stone_brick_stairs"] = (0.40f, 0.50f, 0.35f),
        ["minecraft:mossy_stone_brick_slab"] = (0.40f, 0.50f, 0.35f),
        ["minecraft:smooth_stone_slab"] = (0.55f, 0.55f, 0.55f),
        ["minecraft:polished_granite"] = (0.62f, 0.44f, 0.37f),
        ["minecraft:polished_diorite"] = (0.72f, 0.72f, 0.72f),
        ["minecraft:polished_tuff"] = (0.47f, 0.49f, 0.44f),
        ["minecraft:polished_tuff_slab"] = (0.47f, 0.49f, 0.44f),
        ["minecraft:tuff_bricks"] = (0.45f, 0.47f, 0.42f),
        ["minecraft:chiseled_tuff"] = (0.45f, 0.47f, 0.42f),
        ["minecraft:chiseled_tuff_bricks"] = (0.45f, 0.47f, 0.42f),
        ["minecraft:chiseled_sandstone"] = (0.80f, 0.75f, 0.55f),
        ["minecraft:chiseled_stone_bricks"] = (0.50f, 0.50f, 0.50f),
        ["minecraft:cracked_stone_bricks"] = (0.48f, 0.48f, 0.48f),
        ["minecraft:cut_sandstone"] = (0.80f, 0.75f, 0.55f),

        // Deepslate ores
        ["minecraft:deepslate_coal_ore"] = (0.30f, 0.30f, 0.32f),
        ["minecraft:deepslate_iron_ore"] = (0.35f, 0.32f, 0.30f),
        ["minecraft:deepslate_copper_ore"] = (0.35f, 0.30f, 0.28f),
        ["minecraft:deepslate_gold_ore"] = (0.38f, 0.35f, 0.25f),
        ["minecraft:deepslate_redstone_ore"] = (0.38f, 0.25f, 0.25f),
        ["minecraft:deepslate_lapis_ore"] = (0.28f, 0.30f, 0.42f),
        ["minecraft:deepslate_diamond_ore"] = (0.30f, 0.38f, 0.38f),

        // Copper variants
        ["minecraft:raw_copper_block"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:raw_iron_block"] = (0.65f, 0.60f, 0.55f),
        ["minecraft:waxed_copper_block"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:waxed_exposed_copper_bulb"] = (0.60f, 0.52f, 0.42f),
        ["minecraft:waxed_oxidized_copper"] = (0.35f, 0.60f, 0.50f),
        ["minecraft:waxed_oxidized_cut_copper"] = (0.35f, 0.60f, 0.50f),
        ["minecraft:waxed_oxidized_cut_copper_slab"] = (0.35f, 0.60f, 0.50f),
        ["minecraft:waxed_oxidized_cut_copper_stairs"] = (0.35f, 0.60f, 0.50f),
        ["minecraft:waxed_oxidized_chiseled_copper"] = (0.35f, 0.60f, 0.50f),
        ["minecraft:waxed_oxidized_copper_door"] = (0.35f, 0.60f, 0.50f),
        ["minecraft:waxed_oxidized_copper_bulb"] = (0.35f, 0.60f, 0.50f),
        ["minecraft:waxed_oxidized_copper_grate"] = (0.35f, 0.60f, 0.50f),
        ["minecraft:waxed_chiseled_copper"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:waxed_cut_copper"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:waxed_cut_copper_slab"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:waxed_cut_copper_stairs"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:waxed_copper_bulb"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:waxed_copper_grate"] = (0.70f, 0.48f, 0.30f),
        ["minecraft:waxed_weathered_copper_bulb"] = (0.50f, 0.58f, 0.45f),

        // Amethyst
        ["minecraft:budding_amethyst"] = (0.55f, 0.35f, 0.70f),
        ["minecraft:amethyst_cluster"] = (0.60f, 0.40f, 0.75f),
        ["minecraft:large_amethyst_bud"] = (0.58f, 0.38f, 0.72f),
        ["minecraft:medium_amethyst_bud"] = (0.55f, 0.35f, 0.70f),
        ["minecraft:small_amethyst_bud"] = (0.52f, 0.32f, 0.68f),

        // Misc blocks
        ["minecraft:smooth_basalt"] = (0.32f, 0.32f, 0.37f),
        ["minecraft:magma_block"] = (0.55f, 0.25f, 0.05f),
        ["minecraft:bone_block"] = (0.85f, 0.82f, 0.72f),
        ["minecraft:crying_obsidian"] = (0.15f, 0.05f, 0.25f),
        ["minecraft:mob_spawner"] = (0.15f, 0.20f, 0.25f),
        ["minecraft:trial_spawner"] = (0.20f, 0.25f, 0.30f),
        ["minecraft:vault"] = (0.30f, 0.35f, 0.40f),
        ["minecraft:iron_bars"] = (0.55f, 0.55f, 0.55f),
        ["minecraft:chain"] = (0.35f, 0.35f, 0.38f),
        ["minecraft:rail"] = (0.50f, 0.45f, 0.35f),
        ["minecraft:powered_rail"] = (0.65f, 0.50f, 0.15f),
        ["minecraft:torch"] = (0.90f, 0.80f, 0.30f),
        ["minecraft:wall_torch"] = (0.90f, 0.80f, 0.30f),
        ["minecraft:soul_torch"] = (0.30f, 0.70f, 0.70f),
        ["minecraft:campfire"] = (0.50f, 0.35f, 0.20f),
        ["minecraft:ladder"] = (0.55f, 0.42f, 0.22f),
        ["minecraft:composter"] = (0.45f, 0.35f, 0.18f),
        ["minecraft:barrel"] = (0.50f, 0.38f, 0.20f),
        ["minecraft:bee_nest"] = (0.70f, 0.58f, 0.25f),
        ["minecraft:beehive"] = (0.65f, 0.55f, 0.25f),
        ["minecraft:bell"] = (0.80f, 0.70f, 0.20f),
        ["minecraft:grindstone"] = (0.50f, 0.48f, 0.45f),
        ["minecraft:hopper"] = (0.35f, 0.35f, 0.35f),
        ["minecraft:dispenser"] = (0.48f, 0.48f, 0.48f),
        ["minecraft:decorated_pot"] = (0.60f, 0.40f, 0.30f),
        ["minecraft:suspicious_gravel"] = (0.55f, 0.52f, 0.50f),
        ["minecraft:suspicious_sand"] = (0.85f, 0.78f, 0.52f),
        ["minecraft:nether_portal"] = (0.40f, 0.15f, 0.70f),
        ["minecraft:light"] = (0.95f, 0.95f, 0.80f),

        // Colored blocks
        ["minecraft:light_blue_terracotta"] = (0.45f, 0.52f, 0.58f),
        ["minecraft:white_terracotta"] = (0.80f, 0.72f, 0.65f),
        ["minecraft:red_glazed_terracotta"] = (0.70f, 0.25f, 0.20f),
        ["minecraft:light_gray_wool"] = (0.60f, 0.60f, 0.60f),
        ["minecraft:light_gray_stained_glass"] = (0.60f, 0.60f, 0.60f),
        ["minecraft:brown_stained_glass_pane"] = (0.40f, 0.25f, 0.12f),

        // Fences/doors/trapdoors (use base wood color)
        ["minecraft:oak_fence"] = (0.60f, 0.48f, 0.28f),
        ["minecraft:birch_fence"] = (0.75f, 0.70f, 0.50f),
        ["minecraft:spruce_fence"] = (0.40f, 0.28f, 0.15f),
        ["minecraft:jungle_fence"] = (0.55f, 0.38f, 0.22f),
        ["minecraft:dark_oak_fence"] = (0.30f, 0.20f, 0.10f),
        ["minecraft:oak_trapdoor"] = (0.60f, 0.48f, 0.28f),
        ["minecraft:spruce_trapdoor"] = (0.40f, 0.28f, 0.15f),
        ["minecraft:jungle_trapdoor"] = (0.55f, 0.38f, 0.22f),
        ["minecraft:dark_oak_trapdoor"] = (0.30f, 0.20f, 0.10f),
        ["minecraft:spruce_door"] = (0.40f, 0.28f, 0.15f),
        ["minecraft:cherry_door"] = (0.70f, 0.45f, 0.45f),
        ["minecraft:pale_oak_door"] = (0.78f, 0.75f, 0.70f),
        ["minecraft:stripped_oak_log"] = (0.58f, 0.45f, 0.25f),
        ["minecraft:stripped_oak_wood"] = (0.58f, 0.45f, 0.25f),
        ["minecraft:cherry_wood"] = (0.60f, 0.35f, 0.38f),
        ["minecraft:oak_pressure_plate"] = (0.60f, 0.48f, 0.28f),
        ["minecraft:oak_button"] = (0.60f, 0.48f, 0.28f),
        ["minecraft:flower_pot"] = (0.55f, 0.30f, 0.20f),
        ["minecraft:potted_dead_bush"] = (0.55f, 0.30f, 0.20f),

        // Beds (use base color)
        ["minecraft:red_bed"] = (0.65f, 0.15f, 0.15f),
        ["minecraft:white_bed"] = (0.90f, 0.90f, 0.90f),
        ["minecraft:light_blue_bed"] = (0.40f, 0.55f, 0.80f),
        ["minecraft:light_gray_bed"] = (0.60f, 0.60f, 0.60f),
        ["minecraft:orange_bed"] = (0.85f, 0.50f, 0.10f),
        ["minecraft:magenta_bed"] = (0.70f, 0.25f, 0.65f),
        ["minecraft:yellow_bed"] = (0.85f, 0.80f, 0.20f),
        ["minecraft:purple_bed"] = (0.50f, 0.20f, 0.60f),
        ["minecraft:candle"] = (0.80f, 0.70f, 0.50f),
        ["minecraft:red_candle"] = (0.70f, 0.15f, 0.10f),

        // Banners
        ["minecraft:white_banner"] = (0.90f, 0.90f, 0.90f),

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
