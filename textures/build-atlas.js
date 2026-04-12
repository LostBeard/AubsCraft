// Atlas builder: composites individual 16x16 PNGs into a 256x256 atlas
// Usage: node build-atlas.js
// Output: ../AubsCraft.Admin/wwwroot/atlas.rgba + atlas.png
//
// Tile layout: 16x16 grid. Each tile is 16x16 pixels.
// Index N maps to grid position: col = N % 16, row = N / 16 (integer division)
// The tile order below MUST match TextureAtlas.cs BlockToIndex and PerFaceBlocks.

const { PNG } = require("pngjs");
const fs = require("fs");
const path = require("path");

const TILE = 16;
const GRID = 16;
const SIZE = TILE * GRID; // 256

// Tile index -> filename (without .png)
// Order matches TextureAtlas.cs exactly
const tiles = [
    /* Row 0: terrain basics + log tops */
    /*  0 */ "grass_block_top",
    /*  1 */ "dirt",
    /*  2 */ "stone",
    /*  3 */ "sand",
    /*  4 */ "gravel",
    /*  5 */ "cobblestone",
    /*  6 */ "clay",
    /*  7 */ "coarse_dirt",
    /*  8 */ "oak_log_top",
    /*  9 */ "birch_log_top",
    /* 10 */ "spruce_log_top",
    /* 11 */ "dark_oak_log_top",
    /* 12 */ "jungle_log_top",
    /* 13 */ "acacia_log_top",
    /* 14 */ "cherry_log_top",
    /* 15 */ "mangrove_log_top",

    /* Row 1: leaves + fern */
    /* 16 */ "oak_leaves",
    /* 17 */ "birch_leaves",
    /* 18 */ "spruce_leaves",
    /* 19 */ "dark_oak_leaves",
    /* 20 */ "jungle_leaves",
    /* 21 */ "acacia_leaves",
    /* 22 */ "cherry_leaves",
    /* 23 */ "fern",
    /* 24 */ "andesite",
    /* 25 */ "diorite",
    /* 26 */ "granite",
    /* 27 */ "deepslate",
    /* 28 */ "tuff",
    /* 29 */ "calcite",
    /* 30 */ "bedrock",
    /* 31 */ "obsidian",

    /* Row 2: ores */
    /* 32 */ "coal_ore",
    /* 33 */ "iron_ore",
    /* 34 */ "copper_ore",
    /* 35 */ "gold_ore",
    /* 36 */ "redstone_ore",
    /* 37 */ "lapis_ore",
    /* 38 */ "diamond_ore",
    /* 39 */ "emerald_ore",
    /* 40 */ "oak_planks",
    /* 41 */ "birch_planks",
    /* 42 */ "spruce_planks",
    /* 43 */ "dark_oak_planks",
    /* 44 */ "jungle_planks",
    /* 45 */ "pale_oak_planks",
    /* 46 */ "bricks",
    /* 47 */ "stone_bricks",

    /* Row 3: natural */
    /* 48 */ "snow",
    /* 49 */ "ice",
    /* 50 */ "water_still",
    /* 51 */ "lava_still",
    /* 52 */ "podzol_top",
    /* 53 */ "mycelium_top",
    /* 54 */ "netherrack",
    /* 55 */ "mossy_cobblestone",
    /* 56 */ "red_sand",
    /* 57 */ "sandstone_top",
    /* 58 */ "terracotta",
    /* 59 */ "glowstone",
    /* 60 */ "soul_sand",
    /* 61 */ "bone_block_top",
    /* 62 */ "hay_block_top",
    /* 63 */ "pumpkin_top",

    /* Row 4: flowers + plants + misc */
    /* 64 */ "dandelion",
    /* 65 */ "poppy",
    /* 66 */ "cornflower",
    /* 67 */ "azure_bluet",
    /* 68 */ "oxeye_daisy",
    /* 69 */ "short_grass",
    /* 70 */ "tall_grass_top",
    /* 71 */ "sugar_cane",
    /* 72 */ "melon_top",
    /* 73 */ "farmland_moist",
    /* 74 */ null, // unused
    /* 75 */ null,
    /* 76 */ null,
    /* 77 */ null,
    /* 78 */ null,
    /* 79 */ null,

    /* Row 5: bark sides + grass side */
    /* 80 */ "oak_log",
    /* 81 */ "birch_log",
    /* 82 */ "spruce_log",
    /* 83 */ "dark_oak_log",
    /* 84 */ "grass_block_side",
    /* 85 */ "jungle_log",
    /* 86 */ "acacia_log",
    /* 87 */ "cherry_log",
    /* 88 */ "mangrove_log",
];

// Create output image
const atlas = new PNG({ width: SIZE, height: SIZE });

// Fill with transparent black
for (let i = 0; i < atlas.data.length; i += 4) {
    atlas.data[i] = 0;
    atlas.data[i + 1] = 0;
    atlas.data[i + 2] = 0;
    atlas.data[i + 3] = 0;
}

let placed = 0;
let missing = 0;

for (let idx = 0; idx < tiles.length; idx++) {
    const name = tiles[idx];
    if (!name) continue;

    const file = path.join(__dirname, `${name}.png`);
    if (!fs.existsSync(file)) {
        console.warn(`MISSING: index ${idx} -> ${name}.png`);
        missing++;
        continue;
    }

    const png = PNG.sync.read(fs.readFileSync(file));
    const col = idx % GRID;
    const row = Math.floor(idx / GRID);
    const ox = col * TILE;
    const oy = row * TILE;

    // Handle animated textures (e.g. lava_still is 16x512 - use first frame)
    const srcH = Math.min(png.height, TILE);

    for (let y = 0; y < srcH; y++) {
        for (let x = 0; x < Math.min(png.width, TILE); x++) {
            const si = (y * png.width + x) * 4;
            const di = ((oy + y) * SIZE + (ox + x)) * 4;
            atlas.data[di] = png.data[si];
            atlas.data[di + 1] = png.data[si + 1];
            atlas.data[di + 2] = png.data[si + 2];
            atlas.data[di + 3] = png.data[si + 3];
        }
    }
    placed++;
}

// Write atlas.rgba (raw RGBA bytes)
const rgbaPath = path.join(__dirname, "..", "AubsCraft.Admin", "wwwroot", "atlas.rgba");
fs.writeFileSync(rgbaPath, atlas.data);

// Write atlas.png
const pngPath = path.join(__dirname, "..", "AubsCraft.Admin", "wwwroot", "atlas.png");
fs.writeFileSync(pngPath, PNG.sync.write(atlas));

console.log(`Atlas built: ${placed} tiles placed, ${missing} missing, ${tiles.length} total slots`);
console.log(`Output: ${rgbaPath}`);
console.log(`Output: ${pngPath}`);
