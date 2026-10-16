using AubsCraft.Admin.Server.Services;
using AubsCraft.Assets;
using AubsCraft.Assets.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// Block states in the viewer: the generator describes the states that look different (a log lying down, a furnace facing east, a rail curving, a crop grown) and turns models
/// the way the game does; the server names blocks with exactly those properties; the registry then holds states, not just names.
/// </summary>
public class BlockStateTests
{
    private static NbtCompound Props(params (string, string)[] values)
    {
        var c = new NbtCompound();
        foreach (var (k, v) in values) c[k] = v;
        return c;
    }

    private static BlockStateNamer Namer() => new(new()
    {
        ["minecraft:oak_log"] = ["axis"],
        ["minecraft:furnace"] = ["facing", "lit"],
        ["minecraft:rail"] = ["shape"],
    });

    // ---- the server's names ----

    [Test]
    public void ABlockIsNamedWithTheProperties_ThatChangeItsLook_AndNoOthers()
    {
        var n = Namer();
        Assert.That(n.Key("minecraft:oak_log", Props(("axis", "x"))), Is.EqualTo("minecraft:oak_log[axis=x]"));
        Assert.That(n.Key("minecraft:furnace", Props(("facing", "east"), ("lit", "true"))), Is.EqualTo("minecraft:furnace[facing=east,lit=true]"));
        Assert.That(n.Key("minecraft:furnace", Props(("lit", "true"), ("facing", "east"))), Is.EqualTo("minecraft:furnace[facing=east,lit=true]"), "in the generator's order, whatever the chunk's");
        Assert.That(n.Key("minecraft:rail", Props(("shape", "north_south"), ("waterlogged", "false"))), Is.EqualTo("minecraft:rail[shape=north_south]"), "waterlogged does not change the picture");
    }

    [Test]
    public void ABlockWithNoPropertiesThatMatter_IsJustItsName()
    {
        var n = Namer();
        Assert.That(n.Key("minecraft:stone", null), Is.EqualTo("minecraft:stone"));
        Assert.That(n.Key("minecraft:stone", Props(("foo", "bar"))), Is.EqualTo("minecraft:stone"), "a block the table does not list");
        Assert.That(n.Key("minecraft:oak_log", Props(("persistent", "true"))), Is.EqualTo("minecraft:oak_log"), "none of the listed properties present");
        Assert.That(n.Key("minecraft:oak_log", null), Is.EqualTo("minecraft:oak_log"));
        Assert.That(new BlockStateNamer(new()).Key("minecraft:oak_log", Props(("axis", "x"))), Is.EqualTo("minecraft:oak_log"), "no table: names only, as before states");
    }

    [Test]
    public void APartlyKnownState_KeepsWhatItHas()
    {
        Assert.That(Namer().Key("minecraft:furnace", Props(("facing", "north"))), Is.EqualTo("minecraft:furnace[facing=north]"));
    }

    [Test]
    public void NameOf_StripsTheStates()
    {
        Assert.That(BlockStateNamer.NameOf("minecraft:oak_log[axis=x]"), Is.EqualTo("minecraft:oak_log"));
        Assert.That(BlockStateNamer.NameOf("minecraft:stone"), Is.EqualTo("minecraft:stone"));
        Assert.That(BlockStateNamer.NameOf("mymod:thing[a=b,c=d]"), Is.EqualTo("mymod:thing"));
    }

    [Test]
    public void TheTable_LoadsFromTheGeneratorsFile_AndAMissingOneMeansNamesOnly()
    {
        var path = Path.Combine(TestUtil.NewTempDir(), "mc-state-props.json");
        File.WriteAllText(path, """{ "minecraft:oak_log": ["axis"] }""");
        var loaded = BlockStateNamer.Load(path);
        Assert.That(loaded.BlockCount, Is.EqualTo(1));
        Assert.That(loaded.Key("minecraft:oak_log", Props(("axis", "z"))), Is.EqualTo("minecraft:oak_log[axis=z]"));
        Assert.That(BlockStateNamer.Load(Path.Combine(TestUtil.NewTempDir(), "nope.json")).BlockCount, Is.EqualTo(0));
        File.WriteAllText(path, "not json");
        Assert.That(BlockStateNamer.Load(path).BlockCount, Is.EqualTo(0), "a broken file is no table, not a crash");
    }

    // ---- a chunk read through the namer ----

    private static NbtCompound Section(sbyte y, params (string name, (string, string)[] props)[] palette)
    {
        var list = new NbtList(NbtTagType.Compound, palette.Length);
        foreach (var (name, props) in palette)
        {
            var entry = new NbtCompound { ["Name"] = name };
            if (props.Length > 0) entry["Properties"] = Props(props);
            list.Add(entry);
        }
        var blockStates = new NbtCompound { ["palette"] = list };
        if (palette.Length > 1)
        {
            // 4 bits a block, 16 to a long, 256 longs: block i uses palette index (i % palette.Length)
            var data = new long[256];
            for (int i = 0; i < 4096; i++) data[i / 16] |= (long)(i % palette.Length) << ((i % 16) * 4);
            blockStates["data"] = data;
        }
        return new NbtCompound { ["Y"] = (byte)y, ["block_states"] = blockStates };
    }

    private static NbtCompound Chunk(params NbtCompound[] sections)
    {
        var list = new NbtList(NbtTagType.Compound, sections.Length);
        foreach (var s in sections) list.Add(s);
        return new NbtCompound { ["sections"] = list };
    }

    [Test]
    public void AChunksPalette_HoldsEachStateOfALog_AsItsOwnEntry()
    {
        var saved = BlockStateNamer.Current;
        BlockStateNamer.Current = Namer();
        try
        {
            var chunk = RegionReader.ParseChunkNbt(Chunk(Section(0,
                ("minecraft:air", []), ("minecraft:oak_log", [("axis", "x")]), ("minecraft:oak_log", [("axis", "y")]), ("minecraft:stone", [("waterlogged", "false")]))));
            Assert.That(chunk.Palette, Is.EquivalentTo(new[] { "minecraft:air", "minecraft:oak_log[axis=x]", "minecraft:oak_log[axis=y]", "minecraft:stone" }));
            Assert.That(chunk.Palette.Distinct().Count(), Is.EqualTo(chunk.Palette.Count));
            // The blocks point at the right entries: block i of the section has palette index i % 4
            int x = chunk.Palette.IndexOf("minecraft:oak_log[axis=x]");
            Assert.That(chunk.Blocks[1 + 64 * 256], Is.EqualTo(x), "the second block of the section (section 0 starts at y 64 of the array) is the log with axis x");
        }
        finally { BlockStateNamer.Current = saved; }
    }

    [Test]
    public void TwoStatesOfOneBlock_ThatLookTheSame_ShareOneEntry()
    {
        var saved = BlockStateNamer.Current;
        BlockStateNamer.Current = Namer();
        try
        {
            var chunk = RegionReader.ParseChunkNbt(Chunk(Section(0,
                ("minecraft:air", []), ("minecraft:rail", [("shape", "north_south"), ("waterlogged", "false")]), ("minecraft:rail", [("shape", "north_south"), ("waterlogged", "true")]))));
            Assert.That(chunk.Palette.Count(p => p.StartsWith("minecraft:rail")), Is.EqualTo(1), "waterlogged is not part of the name");
        }
        finally { BlockStateNamer.Current = saved; }
    }

    [Test]
    public void WithNoTable_AChunkIsNamedAsBefore()
    {
        var saved = BlockStateNamer.Current;
        BlockStateNamer.Current = new BlockStateNamer(new());
        try
        {
            var chunk = RegionReader.ParseChunkNbt(Chunk(Section(0, ("minecraft:air", []), ("minecraft:oak_log", [("axis", "x")]), ("minecraft:oak_log", [("axis", "y")]))));
            Assert.That(chunk.Palette, Is.EqualTo(new[] { "minecraft:air", "minecraft:oak_log" }));
        }
        finally { BlockStateNamer.Current = saved; }
    }

    [Test]
    public void TheRegistry_GivesEachStateItsOwnId()
    {
        var dir = TestUtil.NewTempDir();
        var registry = new GlobalBlockRegistry(TestUtil.Config(("BlockRegistry:FilePath", Path.Combine(dir, "reg.json"))), TestUtil.Log<GlobalBlockRegistry>());
        var chunk = new ChunkResult(new ushort[] { 0, 1, 2, 3 }, ["minecraft:air", "minecraft:oak_log[axis=x]", "minecraft:oak_log[axis=y]", "minecraft:stone"]);
        var ids = registry.ToGlobalIds(chunk);
        Assert.That(ids.Distinct().Count(), Is.EqualTo(4));
        Assert.That(registry.Snapshot(), Does.Contain("minecraft:oak_log[axis=x]").And.Contain("minecraft:oak_log[axis=y]"));
    }

    // ---- turning models the way the game does ----

    [Test]
    public void ATurnAboutY_MovesTheSidesRoundAndTurnsTheTopAndBottomTextures()
    {
        // y: 90 turns clockwise seen from above: the north face now looks east (dest 0 = +X comes from north = 3)
        Assert.That(BlockAssetBuilder.SourceFace(0, 0, 90), Is.EqualTo((3, 0)));
        Assert.That(BlockAssetBuilder.SourceFace(2, 0, 90), Is.EqualTo((0, 0)), "east now looks south");
        Assert.That(BlockAssetBuilder.SourceFace(1, 0, 90), Is.EqualTo((2, 0)), "south now looks west");
        Assert.That(BlockAssetBuilder.SourceFace(3, 0, 90), Is.EqualTo((1, 0)), "west now looks north");
        Assert.That(BlockAssetBuilder.SourceFace(4, 0, 90), Is.EqualTo((4, 90)), "the top texture turns clockwise with it");
        Assert.That(BlockAssetBuilder.SourceFace(5, 0, 90), Is.EqualTo((5, 270)), "the bottom, seen from below, the other way");
        Assert.That(BlockAssetBuilder.SourceFace(4, 0, 180), Is.EqualTo((4, 180)));
        Assert.That(BlockAssetBuilder.SourceFace(4, 0, 270), Is.EqualTo((4, 270)));
        Assert.That(BlockAssetBuilder.SourceFace(0, 0, 0), Is.EqualTo((0, 0)), "no turn, no change");
    }

    [Test]
    public void ATurnAboutX_TipsTheModelOver()
    {
        // x: 90 takes north to down (a dispenser facing down), down to south, south to up, up to north
        Assert.That(BlockAssetBuilder.SourceFace(5, 90, 0).source, Is.EqualTo(3), "the face looking down was north");
        Assert.That(BlockAssetBuilder.SourceFace(2, 90, 0).source, Is.EqualTo(5), "south was down");
        Assert.That(BlockAssetBuilder.SourceFace(4, 90, 0).source, Is.EqualTo(2), "up was south");
        Assert.That(BlockAssetBuilder.SourceFace(3, 90, 0).source, Is.EqualTo(4), "north was up");
        // x: 270 is the other way
        Assert.That(BlockAssetBuilder.SourceFace(4, 270, 0).source, Is.EqualTo(3), "up was north");
        // Upside down (x: 180): the top is the bottom and the sides are upside down
        Assert.That(BlockAssetBuilder.SourceFace(4, 180, 0).source, Is.EqualTo(5));
        Assert.That(BlockAssetBuilder.SourceFace(0, 180, 0), Is.EqualTo((0, 180)), "the east side, upside down");
        Assert.That(BlockAssetBuilder.SourceFace(0, 90, 0), Is.EqualTo((0, 90)), "tipped forward, the east side is turned a quarter");
    }

    [Test]
    public void EveryTurn_IsAPermutationOfTheFaces()
    {
        for (int x = 0; x < 360; x += 90)
            for (int y = 0; y < 360; y += 90)
            {
                var sources = Enumerable.Range(0, 6).Select(d => BlockAssetBuilder.SourceFace(d, x, y).source).ToList();
                Assert.That(sources.Distinct().Count(), Is.EqualTo(6), $"x {x}, y {y}");
                foreach (var d in Enumerable.Range(0, 6)) Assert.That(BlockAssetBuilder.SourceFace(d, x, y).uvRotation % 90, Is.EqualTo(0));
            }
    }

    // ---- the generator's states, on a small jar ----

    private static BuildResult BuildJar(FakeJar jar)
    {
        using var client = jar.Build();
        return new BlockAssetBuilder(client, "test").Build();
    }

    private static byte[] TileOf(BuildResult r, int tile)
    {
        var a = r.Assets;
        var px = new byte[a.TileSize * a.TileSize * 4];
        int ox = (tile % a.TilesPerRow) * a.TileSize, oy = (tile / a.TilesPerRow) * a.TileSize;
        for (int y = 0; y < a.TileSize; y++) Array.Copy(r.AtlasRgba, ((oy + y) * r.AtlasWidth + ox) * 4, px, y * a.TileSize * 4, a.TileSize * 4);
        return px;
    }

    private static (byte r, byte g, byte b) Colour(BuildResult r, int tile) => (TileOf(r, tile)[0], TileOf(r, tile)[1], TileOf(r, tile)[2]);

    private static FakeJar LogJar()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/log_side", 100, 60, 20).Solid("block/log_end", 200, 170, 100);
        jar.Model("block/log", """{ "parent": "block/cube", "textures": { "particle": "#side", "down": "#end", "up": "#end", "north": "#side", "east": "#side", "south": "#side", "west": "#side", "side": "block/log_side", "end": "block/log_end" } }""");
        jar.State("log", """
            { "variants": {
              "axis=x": { "model": "block/log", "x": 90, "y": 90 },
              "axis=y": { "model": "block/log" },
              "axis=z": { "model": "block/log", "x": 90 } } }
            """);
        return jar;
    }

    [Test]
    public void ALyingLog_HasItsEndsOnTheEndsOfItsAxis()
    {
        var r = BuildJar(LogJar());
        Assert.That(r.Assets.StateProps["minecraft:log"], Is.EqualTo(new[] { "axis" }));
        var side = ((byte)100, (byte)60, (byte)20);
        var end = ((byte)200, (byte)170, (byte)100);
        // Faces: +X -X +Z -Z +Y -Y
        var up = r.Assets.Blocks["minecraft:log[axis=y]"].Faces.Select(t => Colour(r, t)).ToArray();
        Assert.That(up, Is.EqualTo(new[] { side, side, side, side, end, end }), "upright: ends up and down");
        var z = r.Assets.Blocks["minecraft:log[axis=z]"].Faces.Select(t => Colour(r, t)).ToArray();
        Assert.That(z, Is.EqualTo(new[] { side, side, end, end, side, side }), "along z: ends north and south");
        var x = r.Assets.Blocks["minecraft:log[axis=x]"].Faces.Select(t => Colour(r, t)).ToArray();
        Assert.That(x, Is.EqualTo(new[] { end, end, side, side, side, side }), "along x: ends east and west");
        // The plain name is the default state, upright
        Assert.That(r.Assets.Blocks["minecraft:log"].Faces.Select(t => Colour(r, t)).ToArray(), Is.EqualTo(up));
    }

    [Test]
    public void AFurnaceFacing_TurnsItsFrontToThatSide()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/f_front", 255, 0, 0).Solid("block/f_side", 0, 255, 0).Solid("block/f_top", 0, 0, 255);
        jar.Model("block/f", """{ "parent": "block/orientable", "textures": { "front": "block/f_front", "side": "block/f_side", "top": "block/f_top" } }""");
        jar.State("f", """
            { "variants": {
              "facing=east": { "model": "block/f", "y": 90 }, "facing=north": { "model": "block/f" },
              "facing=south": { "model": "block/f", "y": 180 }, "facing=west": { "model": "block/f", "y": 270 } } }
            """);
        var r = BuildJar(jar);
        var front = ((byte)255, (byte)0, (byte)0);
        int FrontSide(string facing) => Array.FindIndex(r.Assets.Blocks[$"minecraft:f[facing={facing}]"].Faces.Select(t => Colour(r, t)).ToArray(), c => c == front);
        // Faces: 0 +X east, 1 -X west, 2 +Z south, 3 -Z north
        Assert.That(FrontSide("north"), Is.EqualTo(3));
        Assert.That(FrontSide("east"), Is.EqualTo(0));
        Assert.That(FrontSide("south"), Is.EqualTo(2));
        Assert.That(FrontSide("west"), Is.EqualTo(1));
    }

    [Test]
    public void APropertyThatChangesNothing_IsNotPartOfTheKey()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/log_side", 100, 60, 20).Solid("block/log_end", 200, 170, 100);
        jar.Model("block/log", """{ "parent": "block/cube", "textures": { "particle": "#side", "down": "#end", "up": "#end", "north": "#side", "east": "#side", "south": "#side", "west": "#side", "side": "block/log_side", "end": "block/log_end" } }""");
        jar.State("thing", """
            { "variants": {
              "axis=y,lit=false": { "model": "block/log" }, "axis=y,lit=true": { "model": "block/log" },
              "axis=z,lit=false": { "model": "block/log", "x": 90 }, "axis=z,lit=true": { "model": "block/log", "x": 90 } } }
            """);
        var r = BuildJar(jar);
        Assert.That(r.Assets.StateProps["minecraft:thing"], Is.EqualTo(new[] { "axis" }), "lit makes no difference to the picture");
        Assert.That(r.Assets.Blocks.Keys.Where(k => k.StartsWith("minecraft:thing[")), Is.EquivalentTo(new[] { "minecraft:thing[axis=y]", "minecraft:thing[axis=z]" }));
    }

    [Test]
    public void ABlockWhoseStatesAllLookTheSame_HasNoStates()
    {
        var jar = new FakeJar().WithStandardParents().SimpleCube("plain", 10, 20, 30);
        jar.State("plain", """{ "variants": { "a=1": { "model": "minecraft:block/plain" }, "a=2": { "model": "minecraft:block/plain" } } }""");
        var r = BuildJar(jar);
        Assert.That(r.Assets.StateProps.ContainsKey("minecraft:plain"), Is.False);
        Assert.That(r.Assets.Blocks.Keys.Any(k => k.StartsWith("minecraft:plain[")), Is.False);
    }

    [Test]
    public void StairsAndOtherShapes_HaveShapeRules_AndTheirStatesAreNamedByWhatChoosesTheModel()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/planks", 160, 120, 70);
        jar.Model("block/stairs", """
            { "textures": { "t": "block/planks" }, "elements": [ { "from": [0,0,0], "to": [16,8,16], "faces": { "up": { "texture": "#t" }, "north": { "texture": "#t" } } },
                                                                 { "from": [0,8,0], "to": [8,16,16], "faces": { "up": { "texture": "#t" }, "north": { "texture": "#t" } } } ] }
            """);
        jar.Solid("block/planks_b", 20, 20, 200);
        jar.Model("block/stairs_b", """
            { "textures": { "t": "block/planks_b" }, "elements": [ { "from": [0,0,0], "to": [16,8,16], "faces": { "up": { "texture": "#t" }, "north": { "texture": "#t" } } },
                                                                   { "from": [0,8,0], "to": [8,16,16], "faces": { "up": { "texture": "#t" }, "north": { "texture": "#t" } } } ] }
            """);
        // Stairs are boxes now: the blockstate's rules are kept (not one cube asset a state), and the property that chooses the model names the state
        jar.State("stairs", """{ "variants": { "half=bottom": { "model": "block/stairs" }, "half=top": { "model": "block/stairs_b" } } }""");
        var r = BuildJar(jar);
        Assert.That(r.Assets.Shapes.ContainsKey("minecraft:stairs"), Is.True);
        Assert.That(r.Assets.StateProps["minecraft:stairs"], Is.EqualTo(new[] { "half" }));
        Assert.That(r.Assets.Blocks.Keys.Any(k => k.StartsWith("minecraft:stairs[")), Is.False, "the viewer works a state's boxes out from the rules, no asset a state");
    }

    [Test]
    public void ARail_IsAFlatLayer_WithItsTextureTurnedAndItsCornerTexture()
    {
        var jar = new FakeJar().WithStandardParents();
        // A straight rail texture with a red mark in its top-left pixel; a corner one all blue
        jar.Texture("block/rail", 16, 16, (x, y) => x == 0 && y == 0 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)90, (byte)90, (byte)90, (byte)255));
        jar.Solid("block/rail_corner", 0, 0, 255);
        jar.Model("block/rail_flat", """{ "textures": { "rail": "block/rail" }, "elements": [ { "from": [0,1,0], "to": [16,1,16], "faces": { "up": { "uv": [0,0,16,16], "texture": "#rail" }, "down": { "texture": "#rail" } } } ] }""");
        jar.Model("block/rail_curved", """{ "textures": { "rail": "block/rail_corner" }, "elements": [ { "from": [0,1,0], "to": [16,1,16], "faces": { "up": { "uv": [0,0,16,16], "texture": "#rail" }, "down": { "texture": "#rail" } } } ] }""");
        jar.State("rail", """
            { "variants": {
              "shape=north_south": { "model": "block/rail_flat" }, "shape=east_west": { "model": "block/rail_flat", "y": 90 },
              "shape=south_east": { "model": "block/rail_curved" } } }
            """);
        var r = BuildJar(jar);
        Assert.That(r.Assets.StateProps["minecraft:rail"], Is.EqualTo(new[] { "shape" }));
        foreach (var key in new[] { "north_south", "east_west", "south_east" })
        {
            var a = r.Assets.Blocks[$"minecraft:rail[shape={key}]"];
            Assert.That(a.Kind, Is.EqualTo(BlockKinds.Shape), key);
            Assert.That(a.Thickness, Is.EqualTo(1), key);
        }
        // The straight rail's mark is at the top-left going north-south and has turned a quarter clockwise (to the top-right) going east-west
        var ns = TileOf(r, r.Assets.Blocks["minecraft:rail[shape=north_south]"].Faces[4]);
        var ew = TileOf(r, r.Assets.Blocks["minecraft:rail[shape=east_west]"].Faces[4]);
        Assert.That(ns[0], Is.EqualTo(255), "mark top-left");
        Assert.That(ew[(15) * 4], Is.EqualTo(255), "mark top-right after a quarter turn");
        Assert.That(ew[0], Is.EqualTo(90), "and gone from the top-left");
        // The curve uses the corner texture
        Assert.That(Colour(r, r.Assets.Blocks["minecraft:rail[shape=south_east]"].Faces[4]), Is.EqualTo(((byte)0, (byte)0, (byte)255)));
    }

    [Test]
    public void ACropAtEachAge_IsItsOwnPlantPicture()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/crop0", 10, 200, 10).Solid("block/crop1", 200, 200, 10);
        jar.Model("block/crop_a", """{ "parent": "block/cross", "textures": { "cross": "block/crop0" } }""");
        jar.Model("block/crop_b", """{ "parent": "block/cross", "textures": { "cross": "block/crop1" } }""");
        jar.State("wheat", """{ "variants": { "age=0": { "model": "block/crop_a" }, "age=1": { "model": "block/crop_b" } } }""");
        var r = BuildJar(jar);
        Assert.That(r.Assets.StateProps["minecraft:wheat"], Is.EqualTo(new[] { "age" }));
        var young = r.Assets.Blocks["minecraft:wheat[age=0]"];
        var grown = r.Assets.Blocks["minecraft:wheat[age=1]"];
        Assert.That(young.Kind, Is.EqualTo(BlockKinds.Cross));
        Assert.That(Colour(r, young.Faces[0]), Is.EqualTo(((byte)10, (byte)200, (byte)10)));
        Assert.That(Colour(r, grown.Faces[0]), Is.EqualTo(((byte)200, (byte)200, (byte)10)));
    }
}
