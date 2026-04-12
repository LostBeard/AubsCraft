# Minecraft Block Texture Reference

## Asset Source

Client jar: `C:\Users\TJ\AppData\Roaming\.minecraft\versions\26.1.2\26.1.2.jar`
Texture path inside jar: `assets/minecraft/textures/block/`

Extract with: `jar xf "<jar-path>" assets/minecraft/textures/block/<name>.png`

## Per-Face Blocks

These blocks have different textures on different faces (top/side/bottom):

### Logs (top = rings, side = bark, bottom = rings)

| Block | Top Texture | Side (Bark) Texture | Status |
|-------|------------|-------------------|--------|
| oak_log | oak_log_top.png | oak_log.png | In atlas (80) |
| birch_log | birch_log_top.png | birch_log.png | In atlas (81) |
| spruce_log | spruce_log_top.png | spruce_log.png | In atlas (82) |
| dark_oak_log | dark_oak_log_top.png | dark_oak_log.png | In atlas (83) |
| jungle_log | jungle_log_top.png | jungle_log.png | PNG extracted, needs atlas index 85 |
| acacia_log | acacia_log_top.png | acacia_log.png | PNG extracted, needs atlas index 86 |
| cherry_log | cherry_log_top.png | cherry_log.png | PNG extracted, needs atlas index 87 |
| mangrove_log | mangrove_log_top.png | mangrove_log.png | PNG extracted, needs atlas index 88 |
| pale_oak_log | pale_oak_log_top.png | pale_oak_log.png | Available in jar, not yet extracted |

### Stripped Logs

All stripped variants available in jar (`stripped_oak_log.png`, etc.) - lighter planed texture, distinct from bark. Not yet in atlas.

### Grass/Dirt Variants

| Block | Top | Side | Bottom |
|-------|-----|------|--------|
| grass_block | grass_block_top.png (tinted) | grass_block_side.png | dirt.png |
| podzol | podzol_top.png | dirt.png | dirt.png |
| mycelium | mycelium_top.png | dirt.png | dirt.png |

### Other Per-Face Blocks (not yet in atlas)

| Block | Top | Side | Bottom | Notes |
|-------|-----|------|--------|-------|
| pumpkin | pumpkin_top.png | pumpkin_side.png | pumpkin_top.png | carved_pumpkin has face texture |
| melon | melon_top.png | melon_side.png | melon_top.png | |
| tnt | tnt_top.png | tnt_side.png | tnt_bottom.png | 3 distinct faces |
| furnace | furnace_top.png | furnace_side.png + furnace_front.png | furnace_top.png | front face is distinct |
| crafting_table | crafting_table_top.png | crafting_table_side.png + crafting_table_front.png | oak_planks.png | |
| hay_block | hay_block_top.png | hay_block_side.png | hay_block_top.png | |
| bone_block | bone_block_top.png | bone_block_side.png | bone_block_top.png | |

## Atlas Layout (current)

16x16 grid, 256x256 pixels total. Each tile is 16x16.

| Row | Indices | Contents |
|-----|---------|----------|
| 0 | 0-7 | Terrain basics (grass_top, dirt, stone, sand, gravel, cobblestone, clay, coarse_dirt) |
| 0 | 8-15 | Log tops (oak, birch, spruce, dark_oak, jungle, acacia, cherry, mangrove) |
| 1 | 16-22 | Leaves (oak, birch, spruce, dark_oak, jungle, acacia, cherry) |
| 1 | 23 | Fern |
| 2 | 24-31 | Stone types (andesite, diorite, granite, deepslate, tuff, calcite, bedrock, obsidian) |
| 3 | 32-39 | Ores (coal, iron, copper, gold, redstone, lapis, diamond, emerald) |
| 4 | 40-47 | Planks + bricks |
| 5 | 48-55 | Natural (snow, ice, water, lava, podzol, mycelium, netherrack, mossy_cobble) |
| 5 | 56-63 | Misc (red_sand, sandstone, terracotta, glowstone, soul_sand, bone, hay, pumpkin) |
| 6 | 64-71 | (unused or extended) |
| 7 | 72-79 | Row 9 entries (melon at 72, farmland at 73) |
| 8 | 80-84 | Log bark sides + grass_block_side |
| 8 | 85-88 | PLANNED: jungle/acacia/cherry/mangrove bark |

## Biome Tinting

Minecraft uses grayscale textures for grass/leaves/water that are multiplied by a biome-specific color at runtime. Our viewer uses static per-block tint colors from `BlockColorMap.cs` instead of real biome data.

Tinted blocks (detected by substring): grass, leaves, water, vine, fern, lily.
Non-tinted blocks get white vertex color (1,1,1) so texture shows true color.

## Leaf Transparency

Leaf PNGs have alpha channel cutouts. The WGSL shader discards pixels with alpha < 0.3.
Leaves are treated as opaque for face culling (not in IsAir list) - leaf-adjacent faces of other leaf blocks are culled.
