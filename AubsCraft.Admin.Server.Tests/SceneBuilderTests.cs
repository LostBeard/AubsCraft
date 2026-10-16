using AubsCraft.Admin.Server.Tests.Live;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// A tool, not a check: builds a small SCENE on a real local Paper server (a flat world, RCON setblock/fill) with the blocks the viewer should draw by state - sloping and curved rails
/// in every direction, logs lying along both axes, furnaces and dispensers facing every way, crops of every age, glazed terracotta, pillars, carpets and plates, seagrass in a pool - saves it,
/// and copies the world folder to AUBS_SCENE_OUT (default D:/users/tj/Projects/_scratch/scene-server) for the viewer gate to shoot (AUBS_VIEWER_WORLD).
/// The scene is laid out on a grid south of the origin, every item at a known coordinate listed in SceneItems.
/// </summary>
[NonParallelizable]
public class SceneBuilderTests
{
    private const int GamePort = 25871, RconPort = 25872;
    public const int FloorY = -60;   // the first air block above the flat world's grass

    /// <summary>Where each item stands (x, y, z) and what it is: the viewer gate shoots these.</summary>
    public static readonly List<(string label, int x, int y, int z)> SceneItems = new();

    [Test]
    public async Task BuildTheScene()
    {
        var outDir = Environment.GetEnvironmentVariable("AUBS_SCENE_OUT") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var paper = await PaperServer.StartAsync("scene", GamePort, RconPort, 2, "minecraft:flat", "world", TimeSpan.FromMinutes(6));
        var rcon = new MinecraftRconClient("127.0.0.1", RconPort);
        Assert.That(await rcon.ConnectAsync(paper.RconPassword), Is.True);

        async Task Cmd(string c)
        {
            var r = await rcon.SendCommandAsync(c);
            if (r.Contains("Could not set") && !r.Contains("already")) TestContext.Progress.WriteLine($"[scene] {c} -> {r}");
        }
        async Task Set(int x, int y, int z, string block) => await Cmd($"setblock {x} {y} {z} {block}");
        var items = new List<string>();
        void Item(string label, int x, int y, int z) { items.Add($"{label}|{x}|{y}|{z}"); }

        // Everything near the origin (the spawn chunks are loaded); a ground of stone bricks to place on, 40 x 40
        await Cmd("gamerule doDaylightCycle false");
        await Cmd("time set 6000");
        await Cmd("weather clear");
        await Cmd($"fill -4 {FloorY - 1} -4 60 {FloorY - 1} 60 minecraft:stone_bricks");
        await Cmd($"fill -4 {FloorY} -4 60 {FloorY + 8} 60 minecraft:air");

        int y = FloorY;   // the floor is the stone brick layer below; things stand on y = FloorY

        // ---- ramps: a flat rail, the ramp, the next flat rail a block up (a stone block under it), in all four directions ----
        string[] dirs = ["east", "west", "south", "north"];
        (int dx, int dz)[] step = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        for (int i = 0; i < 4; i++)
        {
            int bx = 4, bz = 4 + i * 6;       // the ramp's cell
            var (dx, dz) = step[i];
            string flat = dx != 0 ? "east_west" : "north_south";
            await Set(bx - dx, y, bz - dz, $"minecraft:rail[shape={flat}]");
            await Set(bx, y, bz, $"minecraft:rail[shape=ascending_{dirs[i]}]");
            await Set(bx + dx, y, bz + dz, "minecraft:stone");
            await Set(bx + dx, y + 1, bz + dz, $"minecraft:rail[shape={flat}]");
            Item($"ramp-{dirs[i]}", bx, y, bz);
        }
        // a ruler beside each ramp: four blocks tall, white and red, so a screenshot can measure how high the ramp really rises
        for (int i = 0; i < 4; i++)
            for (int h = 0; h < 4; h++)
                await Set(8, y + h, 4 + i * 6, h % 2 == 0 ? "minecraft:white_concrete" : "minecraft:red_concrete");
        // a ramp ALONE (nothing beside it) with a ruler: does it rise one block?
        await Set(30, y, 40, "minecraft:rail[shape=ascending_west]");
        for (int h = 0; h < 4; h++) await Set(33, y + h, 40, h % 2 == 0 ? "minecraft:white_concrete" : "minecraft:red_concrete");
        Item("ramp-alone", 30, y, 40);
        // a powered rail ramp too
        await Set(14, y, 4, "minecraft:powered_rail[powered=true,shape=ascending_east]");
        await Set(15, y, 4, "minecraft:stone");
        await Set(15, y + 1, 4, "minecraft:powered_rail[powered=true,shape=east_west]");
        Item("powered-ramp-east", 14, y, 4);

        // ---- curves: a square loop of rail, the six shapes ----
        int cx = 4, cz = 34;
        await Set(cx, y, cz, "minecraft:rail[shape=south_east]");
        await Set(cx + 1, y, cz, "minecraft:rail[shape=east_west]");
        await Set(cx + 2, y, cz, "minecraft:rail[shape=south_west]");
        await Set(cx, y, cz + 1, "minecraft:rail[shape=north_south]");
        await Set(cx + 2, y, cz + 1, "minecraft:rail[shape=north_south]");
        await Set(cx, y, cz + 2, "minecraft:rail[shape=north_east]");
        await Set(cx + 1, y, cz + 2, "minecraft:rail[shape=east_west]");
        await Set(cx + 2, y, cz + 2, "minecraft:rail[shape=north_west]");
        Item("curves", cx + 1, y, cz + 1);

        // ---- logs lying along every axis, and upright ----
        int lx = 20, lz = 4;
        await Set(lx, y, lz, "minecraft:oak_log[axis=x]");
        await Set(lx + 3, y, lz, "minecraft:oak_log[axis=z]");
        await Set(lx + 6, y, lz, "minecraft:oak_log[axis=y]");
        await Set(lx, y, lz + 3, "minecraft:spruce_log[axis=x]");
        await Set(lx + 3, y, lz + 3, "minecraft:stripped_birch_log[axis=z]");
        await Set(lx + 6, y, lz + 3, "minecraft:hay_block[axis=x]");
        await Set(lx, y, lz + 6, "minecraft:quartz_pillar[axis=z]");
        await Set(lx + 3, y, lz + 6, "minecraft:bone_block[axis=x]");
        Item("logs", lx + 3, y, lz + 3);

        // ---- things that face a way: furnaces (lit and not), dispensers, observers, glazed terracotta, pumpkins ----
        int fx = 20, fz = 20;
        string[] facings = ["north", "east", "south", "west"];
        for (int i = 0; i < 4; i++)
        {
            await Set(fx + i * 2, y, fz, $"minecraft:furnace[facing={facings[i]},lit=false]");
            await Set(fx + i * 2, y, fz + 2, $"minecraft:furnace[facing={facings[i]},lit=true]");
            await Set(fx + i * 2, y, fz + 4, $"minecraft:red_glazed_terracotta[facing={facings[i]}]");
            await Set(fx + i * 2, y, fz + 6, $"minecraft:carved_pumpkin[facing={facings[i]}]");
        }
        await Set(fx + 9, y, fz, "minecraft:dispenser[facing=up]");
        await Set(fx + 9, y, fz + 2, "minecraft:dispenser[facing=down]");
        await Set(fx + 9, y, fz + 4, "minecraft:observer[facing=east]");
        Item("facing", fx + 3, y, fz + 3);

        // ---- crops of every age on farmland ----
        int wx = 40, wz = 4;
        await Cmd($"fill {wx} {y} {wz} {wx + 7} {y} {wz + 2} minecraft:farmland");
        for (int age = 0; age < 8; age++)
        {
            await Set(wx + age, y + 1, wz, $"minecraft:wheat[age={age}]");
            await Set(wx + age, y + 1, wz + 1, $"minecraft:carrots[age={Math.Min(age, 7)}]");
            await Set(wx + age, y + 1, wz + 2, $"minecraft:potatoes[age={age}]");
        }
        Item("crops", wx + 3, y + 1, wz + 1);

        // ---- thin things and shapes ----
        int tx = 40, tz = 14;
        await Set(tx, y, tz, "minecraft:white_carpet");
        await Set(tx + 2, y, tz, "minecraft:red_carpet");
        await Set(tx + 4, y, tz, "minecraft:stone_pressure_plate");
        await Set(tx + 6, y, tz, "minecraft:snow[layers=2]");
        await Set(tx + 8, y, tz, "minecraft:lily_pad");
        await Set(tx + 10, y, tz, "minecraft:oak_trapdoor[half=bottom,open=false]");
        Item("thin", tx + 4, y, tz);

        // ---- seagrass and kelp in a pool ----
        int px = 40, pz = 24;
        await Cmd($"fill {px} {y - 1} {pz} {px + 6} {y - 1} {pz + 6} minecraft:sand");
        await Cmd($"fill {px} {y} {pz} {px + 6} {y + 2} {pz + 6} minecraft:water");
        await Set(px + 2, y, pz + 2, "minecraft:seagrass");
        await Set(px + 4, y, pz + 2, "minecraft:tall_seagrass[half=lower]");
        await Set(px + 4, y + 1, pz + 2, "minecraft:tall_seagrass[half=upper]");
        await Set(px + 2, y, pz + 4, "minecraft:kelp[age=10]");
        await Set(px + 2, y + 1, pz + 4, "minecraft:kelp[age=10]");
        await Set(px + 2, y + 2, pz + 4, "minecraft:kelp[age=10]");
        await Set(px + 4, y, pz + 4, "minecraft:kelp[age=10]");
        await Set(px + 4, y + 1, pz + 4, "minecraft:kelp[age=10]");
        await Set(px + 4, y + 2, pz + 4, "minecraft:kelp[age=10]");
        Item("pool", px + 3, y, pz + 3);

        // ---- shapes: stairs, slabs, fences, walls, doors, ... (the blocks that are boxes of their cell) ----
        int sz = 46;
        await Cmd($"fill -4 {FloorY - 1} 44 70 {FloorY - 1} 70 minecraft:stone_bricks");   // more floor, south of the rest
        await Cmd($"fill -4 {FloorY} 44 70 {FloorY + 8} 70 minecraft:air");
        // stairs: every facing, bottom and top, straight; then the four corner shapes (a neighbour makes a stair turn into a corner, so place the neighbours too)
        string[] stairFacings = ["east", "south", "west", "north"];
        for (int i = 0; i < 4; i++)
        {
            await Set(6 + i * 2, y, sz, $"minecraft:oak_stairs[facing={stairFacings[i]},half=bottom]");
            await Set(6 + i * 2, y, sz + 2, $"minecraft:oak_stairs[facing={stairFacings[i]},half=top]");
        }
        // an inner and an outer corner: a stair facing north with a stair to its east that faces east makes it a corner; the game decides the shape from the neighbours
        await Set(6, y, sz + 5, "minecraft:stone_brick_stairs[facing=north,half=bottom]");
        await Set(7, y, sz + 5, "minecraft:stone_brick_stairs[facing=east,half=bottom]");
        await Set(10, y, sz + 5, "minecraft:stone_brick_stairs[facing=north,half=bottom]");
        await Set(10, y, sz + 4, "minecraft:stone_brick_stairs[facing=east,half=bottom]");
        await Set(13, y, sz + 5, "minecraft:stone_brick_stairs[facing=north,half=bottom]");
        await Set(13, y, sz + 6, "minecraft:stone_brick_stairs[facing=east,half=bottom]");
        Item("stairs", 9, y, sz + 2);
        // slabs
        await Set(20, y, sz, "minecraft:oak_slab[type=bottom]");
        await Set(22, y, sz, "minecraft:oak_slab[type=top]");
        await Set(24, y, sz, "minecraft:oak_slab[type=double]");
        await Set(26, y, sz, "minecraft:smooth_stone_slab[type=bottom]");
        await Set(20, y, sz + 2, "minecraft:stone_brick_slab[type=bottom]");
        await Set(22, y, sz + 2, "minecraft:stone_brick_slab[type=top]");
        Item("slabs", 23, y, sz + 1);
        // fences, with a branch and a gate (open and closed)
        for (int x = 30; x <= 34; x++) await Set(x, y, sz, "minecraft:oak_fence");
        await Set(32, y, sz + 1, "minecraft:oak_fence");
        await Set(32, y, sz + 2, "minecraft:oak_fence");
        await Set(35, y, sz, "minecraft:oak_fence_gate[facing=east,open=false]");
        await Set(36, y, sz, "minecraft:oak_fence");
        await Set(30, y, sz + 4, "minecraft:oak_fence_gate[facing=north,open=true]");
        await Set(29, y, sz + 4, "minecraft:oak_fence");
        await Set(31, y, sz + 4, "minecraft:oak_fence");
        Item("fences", 32, y, sz + 1);
        // walls: low and tall sides, a post
        for (int x = 40; x <= 44; x++) await Set(x, y, sz, "minecraft:cobblestone_wall");
        await Set(42, y, sz + 1, "minecraft:cobblestone_wall");
        await Set(42, y + 1, sz + 1, "minecraft:cobblestone_wall");
        await Set(46, y, sz, "minecraft:stone_brick_wall");
        await Set(46, y + 1, sz, "minecraft:stone_brick_wall");
        Item("walls", 43, y, sz + 1);
        // panes and bars
        for (int x = 50; x <= 54; x++) await Set(x, y, sz, "minecraft:glass_pane");
        await Set(52, y, sz + 1, "minecraft:glass_pane");
        for (int x = 56; x <= 59; x++) await Set(x, y, sz, "minecraft:iron_bars");
        await Set(57, y, sz + 1, "minecraft:iron_bars");
        Item("panes", 54, y, sz + 1);
        // doors: closed, open, iron; trapdoors: closed bottom, closed top, open
        int doorZ = sz + 10;
        await Set(6, y, doorZ, "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false]");
        await Set(6, y + 1, doorZ, "minecraft:oak_door[facing=north,half=upper,hinge=left,open=false]");
        await Set(9, y, doorZ, "minecraft:oak_door[facing=north,half=lower,hinge=left,open=true]");
        await Set(9, y + 1, doorZ, "minecraft:oak_door[facing=north,half=upper,hinge=left,open=true]");
        await Set(12, y, doorZ, "minecraft:iron_door[facing=east,half=lower,hinge=right,open=false]");
        await Set(12, y + 1, doorZ, "minecraft:iron_door[facing=east,half=upper,hinge=right,open=false]");
        Item("doors", 9, y, doorZ);
        await Set(20, y, doorZ, "minecraft:oak_trapdoor[facing=north,half=bottom,open=false]");
        await Set(22, y, doorZ, "minecraft:oak_trapdoor[facing=north,half=top,open=false]");
        await Set(24, y, doorZ, "minecraft:oak_trapdoor[facing=north,half=bottom,open=true]");
        await Set(26, y, doorZ, "minecraft:spruce_trapdoor[facing=east,half=bottom,open=true]");
        Item("trapdoors", 23, y, doorZ);
        // a ladder up a stone column, torches (standing, on a wall), a hanging lantern, candles
        await Set(32, y, doorZ, "minecraft:stone_bricks");
        await Set(32, y + 1, doorZ, "minecraft:stone_bricks");
        await Set(32, y + 2, doorZ, "minecraft:stone_bricks");
        await Set(32, y, doorZ - 1, "minecraft:ladder[facing=north]");
        await Set(32, y + 1, doorZ - 1, "minecraft:ladder[facing=north]");
        await Set(33, y + 1, doorZ, "minecraft:wall_torch[facing=east]");
        await Set(34, y, doorZ + 2, "minecraft:torch");
        await Set(36, y, doorZ + 2, "minecraft:soul_torch");
        await Set(38, y + 2, doorZ + 2, "minecraft:chain[axis=y]");
        await Set(38, y + 1, doorZ + 2, "minecraft:lantern[hanging=true]");
        await Set(40, y, doorZ + 2, "minecraft:lantern[hanging=false]");
        await Set(42, y, doorZ + 2, "minecraft:candle[candles=3,lit=true]");
        await Set(44, y, doorZ + 2, "minecraft:white_candle[candles=1,lit=false]");
        Item("ladder-torches", 34, y, doorZ + 1);
        // buttons, a lever, farmland, a cauldron, an anvil, a flower pot, a cactus
        await Set(50, y, doorZ, "minecraft:stone_bricks");
        await Set(50, y + 1, doorZ, "minecraft:stone_bricks");
        await Set(51, y + 1, doorZ, "minecraft:stone_button[face=wall,facing=east,powered=false]");
        await Set(49, y + 1, doorZ, "minecraft:lever[face=wall,facing=west,powered=true]");
        await Set(50, y + 2, doorZ, "minecraft:oak_button[face=ceiling,facing=north,powered=false]");
        await Cmd($"fill 54 {y} {doorZ} 57 {y} {doorZ} minecraft:farmland");
        await Set(60, y, doorZ, "minecraft:cauldron");
        await Set(62, y, doorZ, "minecraft:anvil[facing=east]");
        await Set(64, y, doorZ, "minecraft:flower_pot");
        await Set(66, y, doorZ, "minecraft:potted_poppy");
        await Set(68, y, doorZ, "minecraft:cactus");
        Item("small", 58, y, doorZ);

        // ---- things that smoke and flicker: lit campfires, fire on netherrack, soul fire, a redstone torch ----
        int smokeX = 20, smokeZ = 46;
        await Set(smokeX, y, smokeZ, "minecraft:campfire[facing=north,lit=true]");
        await Set(smokeX + 4, y, smokeZ, "minecraft:soul_campfire[facing=north,lit=true]");
        await Set(smokeX + 8, y, smokeZ, "minecraft:campfire[facing=north,lit=false]");
        Item("smoke-camp", smokeX + 4, y, smokeZ);
        await Set(smokeX + 14, y - 1, smokeZ, "minecraft:netherrack");
        await Set(smokeX + 14, y, smokeZ, "minecraft:fire");
        await Set(smokeX + 18, y - 1, smokeZ, "minecraft:soul_sand");
        await Set(smokeX + 18, y, smokeZ, "minecraft:soul_fire");
        await Set(smokeX + 22, y, smokeZ, "minecraft:redstone_torch[lit=true]");
        await Set(smokeX + 24, y, smokeZ, "minecraft:torch");
        await Set(smokeX + 26, y, smokeZ, "minecraft:soul_torch");
        Item("smoke-fire", smokeX + 18, y, smokeZ);

        // ---- blocks the game draws with models: chests, beds, shulker boxes, signs, banners (a second slab of floor south of the rest) ----
        await Cmd("forceload add -4 61 60 90");   // the chunks out there are not loaded by the spawn
        await Task.Delay(4000);
        await Cmd($"fill -4 {FloorY - 1} 61 60 {FloorY - 1} 84 minecraft:stone_bricks");
        await Cmd($"fill -4 {FloorY} 61 60 {FloorY + 8} 84 minecraft:air");
        int modelZ = 70;
        string[] fourWays = ["south", "west", "north", "east"];
        for (int i = 0; i < 4; i++)
        {
            await Set(2 + i * 2, y, modelZ, $"minecraft:chest[facing={fourWays[i]}]");
            await Set(2 + i * 2, y, modelZ + 3, $"minecraft:trapped_chest[facing={fourWays[i]}]");
            await Set(2 + i * 2, y, modelZ + 6, $"minecraft:ender_chest[facing={fourWays[i]}]");
        }
        // a double chest facing north: left and right halves side by side (x grows to the east: seen from the north the left half is on the east)
        await Set(12, y, modelZ, "minecraft:chest[facing=north,type=right]"); await Set(13, y, modelZ, "minecraft:chest[facing=north,type=left]");
        await Set(12, y, modelZ + 3, "minecraft:chest[facing=south,type=left]"); await Set(13, y, modelZ + 3, "minecraft:chest[facing=south,type=right]");
        Item("be-chests", 7, y, modelZ + 3);
        for (int i = 0; i < 4; i++)
        {
            // foot then head, the head toward the way it faces
            int hx = 22 + i * 4, hz = modelZ + 1;
            (int dx, int dz)[] toward = [(0, 1), (-1, 0), (0, -1), (1, 0)];
            await Set(hx, y, modelZ, $"minecraft:red_bed[facing={fourWays[i]},part=foot]");
            await Set(hx + toward[i].dx, y, modelZ + toward[i].dz, $"minecraft:red_bed[facing={fourWays[i]},part=head]");
        }
        await Set(22, y, modelZ + 6, "minecraft:blue_bed[facing=east,part=foot]"); await Set(23, y, modelZ + 6, "minecraft:blue_bed[facing=east,part=head]");
        Item("be-beds", 26, y, modelZ + 1);
        string[] boxKinds = ["shulker_box", "purple_shulker_box", "red_shulker_box", "lime_shulker_box", "white_shulker_box"];
        for (int i = 0; i < boxKinds.Length; i++) await Set(40 + i * 2, y, modelZ, $"minecraft:{boxKinds[i]}[facing=up]");
        await Set(40, y, modelZ + 3, "minecraft:shulker_box[facing=north]"); await Set(42, y, modelZ + 3, "minecraft:purple_shulker_box[facing=east]");
        await Set(44, y, modelZ + 3, "minecraft:red_shulker_box[facing=down]"); await Set(46, y, modelZ + 3, "minecraft:lime_shulker_box[facing=south]");
        Item("be-shulkers", 44, y, modelZ + 1);
        for (int i = 0; i < 4; i++) await Set(2 + i * 3, y, modelZ + 12, $"minecraft:oak_sign[rotation={i * 4}]");
        await Set(14, y, modelZ + 12, "minecraft:spruce_sign[rotation=2]"); await Set(16, y, modelZ + 12, "minecraft:birch_sign[rotation=6]");
        for (int i = 0; i < 4; i++)
        {
            (int dx, int dz)[] behind = [(0, -1), (1, 0), (0, 1), (-1, 0)];   // the wall is behind the sign: a sign facing south is on a north wall
            await Set(2 + i * 3 + behind[i].dx, y, modelZ + 16 + behind[i].dz, "minecraft:stone_bricks");
            await Set(2 + i * 3, y, modelZ + 16, $"minecraft:oak_wall_sign[facing={fourWays[i]}]");
        }
        // What the signs say (the first line is the way it faces, so a screenshot shows the writing is the right way round and not mirrored): the standing ones in red, the wall ones in black, one glowing
        string[] facing = ["South", "West", "North", "East"];
        for (int i = 0; i < 4; i++)
        {
            await Cmd($"data merge block {2 + i * 3} {y} {modelZ + 12} {{front_text:{{messages:['\"{facing[i]}\"','\"AubsCraft\"','\"sign {i}\"','\"\"'],color:'red'}}}}");
            await Cmd($"data merge block {2 + i * 3} {y} {modelZ + 16} {{front_text:{{messages:['\"{facing[i]}\"','\"on a wall\"','\"\"','\"\"'],color:'black'}}}}");
        }
        await Cmd($"data merge block 14 {y} {modelZ + 12} {{front_text:{{messages:['\"Glowing\"','\"blue\"','\"text\"','\"!\"'],color:'blue',has_glowing_text:1b}}}}");
        // Hanging signs under a stone beam, with words (south-facing and east-facing)
        await Cmd($"fill 20 {y + 2} {modelZ + 8} 26 {y + 2} {modelZ + 8} minecraft:stone_bricks");
        await Set(21, y + 1, modelZ + 8, "minecraft:oak_hanging_sign[attached=false,rotation=0]");
        await Set(24, y + 1, modelZ + 8, "minecraft:spruce_hanging_sign[attached=false,rotation=4]");
        await Cmd($"data merge block 21 {y + 1} {modelZ + 8} {{front_text:{{messages:['\"Hanging\"','\"sign\"','\"south\"','\"\"'],color:'black'}}}}");
        await Cmd($"data merge block 24 {y + 1} {modelZ + 8} {{front_text:{{messages:['\"Hanging\"','\"sign\"','\"west\"','\"\"'],color:'blue'}}}}");
        Item("be-hanging-signs", 22, y, modelZ + 6);
        Item("be-signs", 6, y, modelZ + 13);
        // Blocks that make their own light, in a row (a gate shoots them at midnight: they should be the only bright things)
        string[] lights = ["glowstone", "sea_lantern", "shroomlight", "magma_block", "jack_o_lantern[facing=south]", "torch", "ochre_froglight", "soul_lantern"];
        for (int i = 0; i < lights.Length; i++) await Set(30 + i * 2, y, 56, $"minecraft:{lights[i]}");
        Item("emissive", 36, y, 56);
        // A wall facing south, the backdrop of the gate that hangs paintings and item frames on it (entities come from the live feed, not the world files)
        await Cmd($"fill 0 {y} 88 26 {y + 4} 88 minecraft:stone_bricks");
        Item("be-hanging", 10, y, 90);
        for (int i = 0; i < 4; i++) await Set(24 + i * 3, y, modelZ + 12, $"minecraft:red_banner[rotation={i * 4}]");
        await Set(36, y, modelZ + 12, "minecraft:white_banner[rotation=0]"); await Set(38, y, modelZ + 12, "minecraft:blue_banner[rotation=8]");
        for (int i = 0; i < 4; i++)
        {
            (int dx, int dz)[] behind = [(0, -1), (1, 0), (0, 1), (-1, 0)];
            await Set(24 + i * 3 + behind[i].dx, y, modelZ + 16 + behind[i].dz, "minecraft:stone_bricks");
            await Set(24 + i * 3, y, modelZ + 16, $"minecraft:green_wall_banner[facing={fourWays[i]}]");
        }
        // Patterns on the standing banners and the wall ones: a creeper face, stripes, a circle, a flower
        string[] patterned = ["creeper", "stripe_top", "circle", "flower"];
        for (int i = 0; i < 4; i++)
        {
            await Cmd($"data merge block {24 + i * 3} {y} {modelZ + 12} {{patterns:[{{color:\"white\",pattern:\"minecraft:{patterned[i]}\"}},{{color:\"blue\",pattern:\"minecraft:border\"}}]}}");
            await Cmd($"data merge block {24 + i * 3} {y} {modelZ + 16} {{patterns:[{{color:\"yellow\",pattern:\"minecraft:{patterned[(i + 1) % 4]}\"}}]}}");
        }
        Item("be-banners", 28, y, modelZ + 13);
        // heads: skeleton, wither skeleton, zombie, creeper, player; floor ones turned a quarter each, and four on walls
        string[] heads = ["skeleton_skull", "wither_skeleton_skull", "zombie_head", "creeper_head", "player_head"];
        for (int i = 0; i < heads.Length; i++) await Set(44 + i * 2, y, modelZ + 12, $"minecraft:{heads[i]}[rotation={i * 3}]");
        for (int i = 0; i < 4; i++)
        {
            (int dx, int dz)[] behind = [(0, -1), (1, 0), (0, 1), (-1, 0)];
            await Set(44 + i * 3 + behind[i].dx, y, modelZ + 16 + behind[i].dz, "minecraft:stone_bricks");
            await Set(44 + i * 3, y, modelZ + 16, $"minecraft:{heads[i]}".Replace("_skull", "_wall_skull").Replace("_head", "_wall_head") + $"[facing={fourWays[i]}]");
        }
        Item("be-skulls", 48, y, modelZ + 13);

        await rcon.SendCommandAsync("save-all flush");
        await Task.Delay(3000);
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(outDir.TrimEnd('/'))!, "scene-items.txt"), items);
        await rcon.DisposeAsync();
        await paper.DisposeAsync();   // stops the server: the world is flushed and closed before it is copied

        // The saved world to where the viewer gate reads it
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        CopyDirectory(Path.Combine(paper.Dir, "world"), Path.Combine(outDir, "world"));
        Assert.That(Directory.GetFiles(Path.Combine(outDir, "world", "region"), "*.mca").Length, Is.GreaterThan(0));
        TestContext.Progress.WriteLine($"[scene] built: {items.Count} items -> {outDir}");
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
