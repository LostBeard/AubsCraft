using AubsCraft.Assets;
using AubsCraft.Assets.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>A client jar built in memory: models, blockstates and solid-colour textures, so the generator is tested without Mojang's files.</summary>
internal sealed class FakeJar
{
    private readonly Dictionary<string, byte[]> _files = new();

    public FakeJar Json(string path, string json) { _files[path] = System.Text.Encoding.UTF8.GetBytes(json); return this; }
    public FakeJar Model(string id, string json) => Json($"assets/minecraft/models/{id}.json", json);
    public FakeJar State(string block, string json) => Json($"assets/minecraft/blockstates/{block}.json", json);
    public FakeJar Meta(string textureId, string json) => Json($"assets/minecraft/textures/{textureId}.png.mcmeta", json);

    /// <summary>A texture of w x h pixels, each pixel given by <paramref name="pixel"/> (x, y).</summary>
    public FakeJar Texture(string textureId, int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> pixel)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int i = (y * w + x) * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
            }
        using var ms = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(rgba, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
        _files[$"assets/minecraft/textures/{textureId}.png"] = ms.ToArray();
        return this;
    }

    public FakeJar Solid(string textureId, byte r, byte g, byte b, byte a = 255, int size = 16) => Texture(textureId, size, size, (_, _) => (r, g, b, a));

    /// <summary>The vanilla parents most blocks use.</summary>
    public FakeJar WithStandardParents()
    {
        Model("block/block", "{}");
        Model("block/cube", """
            { "elements": [ { "from": [0,0,0], "to": [16,16,16], "faces": {
                "down":  { "texture": "#down",  "cullface": "down" },  "up":    { "texture": "#up",    "cullface": "up" },
                "north": { "texture": "#north", "cullface": "north" }, "south": { "texture": "#south", "cullface": "south" },
                "west":  { "texture": "#west",  "cullface": "west" },  "east":  { "texture": "#east",  "cullface": "east" } } } ] }
            """);
        Model("block/cube_all", """{ "parent": "block/cube", "textures": { "particle": "#all", "down": "#all", "up": "#all", "north": "#all", "east": "#all", "south": "#all", "west": "#all" } }""");
        Model("block/cube_column", """{ "parent": "block/cube", "textures": { "particle": "#side", "down": "#end", "up": "#end", "north": "#side", "east": "#side", "south": "#side", "west": "#side" } }""");
        Model("block/cube_bottom_top", """{ "parent": "block/cube", "textures": { "particle": "#side", "down": "#bottom", "up": "#top", "north": "#side", "east": "#side", "south": "#side", "west": "#side" } }""");
        Model("block/orientable", """{ "parent": "block/cube", "textures": { "particle": "#front", "down": "#top", "up": "#top", "north": "#front", "east": "#side", "south": "#side", "west": "#side" } }""");
        Model("block/cross", """
            { "textures": { "particle": "#cross" }, "elements": [
              { "from": [0.8,0,8], "to": [15.2,16,8], "faces": { "north": { "texture": "#cross" }, "south": { "texture": "#cross" } } },
              { "from": [8,0,0.8], "to": [8,16,15.2], "faces": { "east": { "texture": "#cross" }, "west": { "texture": "#cross" } } } ] }
            """);
        Model("block/tinted_cross", """
            { "textures": { "particle": "#cross" }, "elements": [
              { "from": [0.8,0,8], "to": [15.2,16,8], "faces": { "north": { "texture": "#cross", "tintindex": 0 }, "south": { "texture": "#cross", "tintindex": 0 } } },
              { "from": [8,0,0.8], "to": [8,16,15.2], "faces": { "east": { "texture": "#cross", "tintindex": 0 }, "west": { "texture": "#cross", "tintindex": 0 } } } ] }
            """);
        return this;
    }

    /// <summary>A block whose only state uses cube_all of the texture of the same name.</summary>
    public FakeJar SimpleCube(string block, byte r, byte g, byte b, byte a = 255)
    {
        Texture_($"block/{block}", r, g, b, a);
        Model($"block/{block}", $$"""{ "parent": "minecraft:block/cube_all", "textures": { "all": "minecraft:block/{{block}}" } }""");
        return State(block, $$"""{ "variants": { "": { "model": "minecraft:block/{{block}}" } } }""");
    }

    private void Texture_(string id, byte r, byte g, byte b, byte a) => Solid(id, r, g, b, a);

    public ClientJar Build() => ClientJar.FromFiles(_files);
}

public class AssetGeneratorTests
{
    private static BuildResult Build(FakeJar jar)
    {
        using var client = jar.Build();
        return new BlockAssetBuilder(client, "test").Build();
    }

    private static byte[] Tile(BuildResult r, int tile)
    {
        var a = r.Assets;
        var px = new byte[a.TileSize * a.TileSize * 4];
        int ox = (tile % a.TilesPerRow) * a.TileSize, oy = (tile / a.TilesPerRow) * a.TileSize;
        for (int y = 0; y < a.TileSize; y++)
            Array.Copy(r.AtlasRgba, ((oy + y) * r.AtlasWidth + ox) * 4, px, y * a.TileSize * 4, a.TileSize * 4);
        return px;
    }

    private static (byte r, byte g, byte b, byte a) PixelOf(byte[] tile, int size, int x, int y)
    {
        int i = (y * size + x) * 4;
        return (tile[i], tile[i + 1], tile[i + 2], tile[i + 3]);
    }

    [Test]
    public void ACubeAll_BlockHasTheSameTileOnEverySide_AndItsPixelsAreInTheAtlas()
    {
        var r = Build(new FakeJar().WithStandardParents().SimpleCube("stone", 120, 121, 122));
        var stone = r.Assets.Blocks["minecraft:stone"];
        Assert.That(stone.Kind, Is.EqualTo(BlockKinds.Cube));
        Assert.That(stone.Faces.Distinct().Count(), Is.EqualTo(1));
        Assert.That(stone.Faces[0], Is.GreaterThanOrEqualTo(0));
        Assert.That(PixelOf(Tile(r, stone.Faces[0]), 16, 3, 9), Is.EqualTo(((byte)120, (byte)121, (byte)122, (byte)255)));
        Assert.That(stone.Render, Is.EqualTo(BlockRenders.Opaque));
        Assert.That(stone.Tint, Is.EqualTo(BlockTints.None));
    }

    [Test]
    public void ATextureUsedByTwoBlocks_IsOneTile()
    {
        var jar = new FakeJar().WithStandardParents().SimpleCube("stone", 1, 2, 3);
        jar.Model("block/stone_twin", """{ "parent": "block/cube_all", "textures": { "all": "block/stone" } }""");
        jar.State("stone_twin", """{ "variants": { "": { "model": "block/stone_twin" } } }""");
        var r = Build(jar);
        Assert.That(r.Assets.Blocks["minecraft:stone_twin"].Faces[0], Is.EqualTo(r.Assets.Blocks["minecraft:stone"].Faces[0]));
        Assert.That(r.Assets.TileCount, Is.EqualTo(1));
    }

    [Test]
    public void ALog_HasBarkOnTheSides_AndRingsOnTopAndBottom_InTheUprightState()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/oak_log", 100, 60, 20).Solid("block/oak_log_top", 200, 170, 100);
        jar.Model("block/oak_log", """{ "parent": "block/cube_column", "textures": { "end": "block/oak_log_top", "side": "block/oak_log" } }""");
        jar.Model("block/oak_log_horizontal", """{ "parent": "block/cube_column", "textures": { "end": "block/oak_log_top", "side": "block/oak_log" } }""");
        // Mojang lists the lying states after the upright one, but the picker must not depend on that: put axis=x FIRST
        jar.State("oak_log", """{ "variants": { "axis=x": { "model": "block/oak_log_horizontal", "x": 90, "y": 90 }, "axis=y": { "model": "block/oak_log" }, "axis=z": { "model": "block/oak_log_horizontal", "x": 90 } } }""");
        var r = Build(jar);
        var f = r.Assets.Blocks["minecraft:oak_log"].Faces;
        Assert.That(PixelOf(Tile(r, f[0]), 16, 0, 0).r, Is.EqualTo(100), "east is bark");
        Assert.That(PixelOf(Tile(r, f[3]), 16, 0, 0).r, Is.EqualTo(100), "north is bark");
        Assert.That(PixelOf(Tile(r, f[4]), 16, 0, 0).r, Is.EqualTo(200), "top is rings");
        Assert.That(PixelOf(Tile(r, f[5]), 16, 0, 0).r, Is.EqualTo(200), "bottom is rings");
    }

    [Test]
    public void AFurnace_KeepsItsFrontFaceOnTheNorthSide_InTheNorthState()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/furnace_front", 255, 0, 0).Solid("block/furnace_side", 0, 255, 0).Solid("block/furnace_top", 0, 0, 255);
        jar.Model("block/furnace", """{ "parent": "block/orientable", "textures": { "top": "block/furnace_top", "front": "block/furnace_front", "side": "block/furnace_side" } }""");
        // Alphabetical, as in the jar: east comes first and is turned a quarter
        jar.State("furnace", """{ "variants": { "facing=east,lit=false": { "model": "block/furnace", "y": 90 }, "facing=north,lit=false": { "model": "block/furnace" }, "facing=south,lit=false": { "model": "block/furnace", "y": 180 }, "facing=west,lit=false": { "model": "block/furnace", "y": 270 } } }""");
        var f = Build(jar).Assets.Blocks["minecraft:furnace"].Faces;
        // Faces: +X -X +Z -Z +Y -Y; the front is the red one and sits on -Z (north)
        var r = Build(jar);
        Assert.That(PixelOf(Tile(r, f[3]), 16, 1, 1).r, Is.EqualTo(255), "north is the front");
        Assert.That(PixelOf(Tile(r, f[0]), 16, 1, 1).g, Is.EqualTo(255), "east is a side");
    }

    [Test]
    public void ATurnedState_MovesTheFrontToWhereItLooks()
    {
        // A block whose only state faces east (y = 90): its front, modelled on north, must end up on +X (east)
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/front", 255, 0, 0).Solid("block/side", 0, 255, 0).Solid("block/top", 0, 0, 255);
        jar.Model("block/m", """{ "parent": "block/orientable", "textures": { "top": "block/top", "front": "block/front", "side": "block/side" } }""");
        jar.State("turned", """{ "variants": { "facing=east": { "model": "block/m", "y": 90 } } }""");
        var r = Build(jar);
        var f = r.Assets.Blocks["minecraft:turned"].Faces;
        Assert.That(PixelOf(Tile(r, f[0]), 16, 1, 1).r, Is.EqualTo(255), "the front looks east");
        Assert.That(PixelOf(Tile(r, f[3]), 16, 1, 1).g, Is.EqualTo(255), "north is a side now");
    }

    [Test]
    public void AGrassBlock_GetsItsOverlayCompositedIntoTheSides_WithThePlainsTint_AndItsTopIsTinted()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/dirt", 130, 90, 50).Solid("block/grass_top", 150, 150, 150);
        jar.Solid("block/grass_side", 130, 90, 50);
        // The overlay: a grey fringe in the top row only
        jar.Texture("block/grass_side_overlay", 16, 16, (x, y) => y == 0 ? ((byte)200, (byte)200, (byte)200, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        jar.Texture("colormap/grass", 256, 256, (x, y) => (x == 50 && y == 173) ? ((byte)100, (byte)200, (byte)50, (byte)255) : ((byte)1, (byte)1, (byte)1, (byte)255));
        jar.Model("block/grass_block", """
            { "parent": "block/cube_bottom_top", "textures": { "bottom": "block/dirt", "top": "block/grass_top", "side": "block/grass_side", "overlay": "block/grass_side_overlay" },
              "elements": [
                { "from": [0,0,0], "to": [16,16,16], "faces": { "down": { "texture": "#bottom" }, "up": { "texture": "#top", "tintindex": 0 }, "north": { "texture": "#side" }, "south": { "texture": "#side" }, "west": { "texture": "#side" }, "east": { "texture": "#side" } } },
                { "from": [0,0,0], "to": [16,16,16], "faces": { "north": { "texture": "#overlay", "tintindex": 0 }, "south": { "texture": "#overlay", "tintindex": 0 }, "west": { "texture": "#overlay", "tintindex": 0 }, "east": { "texture": "#overlay", "tintindex": 0 } } } ] }
            """);
        jar.State("grass_block", """{ "variants": { "snowy=false": { "model": "block/grass_block" } } }""");
        var r = Build(jar);
        var g = r.Assets.Blocks["minecraft:grass_block"];
        Assert.That(g.Tint, Is.EqualTo(BlockTints.Grass));
        Assert.That(g.TintColor, Is.EqualTo(new[] { 100, 200, 50 }), "the plains colour comes from the colour map at (0.8, 0.4)");
        Assert.That(g.TintFaces, Is.EqualTo(1 << 4), "only the top is tinted at draw time; the sides' tint is baked in");
        var side = Tile(r, g.Faces[0]);
        // fringe row: overlay grey (200) x tint (100,200,50) / 255 = (78, 156, 39); the rest is the dirt side
        var (pr, pg, pb, _) = PixelOf(side, 16, 5, 0);
        Assert.That((pr, pg, pb), Is.EqualTo((78, 156, 39)));
        Assert.That(PixelOf(side, 16, 5, 8), Is.EqualTo(((byte)130, (byte)90, (byte)50, (byte)255)), "below the fringe it is the dirt");
        Assert.That(PixelOf(Tile(r, g.Faces[5]), 16, 0, 0).r, Is.EqualTo(130), "the bottom is dirt");
    }

    [Test]
    public void APlant_IsACross_WithItsTexture_AndTintedWhenTheModelTintsIt()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/poppy", 255, 0, 0).Solid("block/short_grass", 50, 200, 50);
        jar.Model("block/poppy", """{ "parent": "block/cross", "textures": { "cross": "block/poppy" } }""");
        jar.Model("block/short_grass", """{ "parent": "block/tinted_cross", "textures": { "cross": "block/short_grass" } }""");
        jar.State("poppy", """{ "variants": { "": { "model": "block/poppy" } } }""");
        jar.State("short_grass", """{ "variants": { "": { "model": "block/short_grass" } } }""");
        var r = Build(jar);
        var poppy = r.Assets.Blocks["minecraft:poppy"];
        Assert.That(poppy.Kind, Is.EqualTo(BlockKinds.Cross));
        Assert.That(poppy.Tint, Is.EqualTo(BlockTints.None));
        Assert.That(PixelOf(Tile(r, poppy.Faces[0]), 16, 0, 0).r, Is.EqualTo(255));
        var grass = r.Assets.Blocks["minecraft:short_grass"];
        Assert.That(grass.Kind, Is.EqualTo(BlockKinds.Cross));
        Assert.That(grass.Tint, Is.EqualTo(BlockTints.Grass));
        Assert.That(grass.TintFaces, Is.Not.Zero);
    }

    [Test]
    public void LeavesAndGlass_AreCutout_IceIsTranslucent_AndFixedTintsAreFixed()
    {
        var jar = new FakeJar().WithStandardParents();
        // holes (alpha 0) in some pixels
        jar.Texture("block/oak_leaves", 16, 16, (x, y) => (x + y) % 5 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)90, (byte)90, (byte)90, (byte)255));
        jar.Texture("block/birch_leaves", 16, 16, (x, y) => ((byte)90, (byte)90, (byte)90, (byte)255));
        jar.Solid("block/ice", 150, 190, 250, 160);
        foreach (var n in new[] { "oak_leaves", "birch_leaves", "ice" })
        {
            // leaves tint every face (tintindex 0); ice does not
            string tint = n.EndsWith("leaves") ? ", \"tintindex\": 0" : "";
            string face = "{ \"texture\": \"#all\"" + tint + " }";
            jar.Model("block/" + n, "{ \"textures\": { \"all\": \"block/" + n + "\" }, \"elements\": [ { \"from\": [0,0,0], \"to\": [16,16,16], \"faces\": { "
                + "\"down\": " + face + ", \"up\": " + face + ", \"north\": " + face + ", \"south\": " + face + ", \"west\": " + face + ", \"east\": " + face + " } } ] }");
            jar.State(n, "{ \"variants\": { \"\": { \"model\": \"block/" + n + "\" } } }");
        }
        var b = Build(jar).Assets.Blocks;
        Assert.That(b["minecraft:oak_leaves"].Render, Is.EqualTo(BlockRenders.Cutout));
        Assert.That(b["minecraft:oak_leaves"].Tint, Is.EqualTo(BlockTints.Foliage));
        Assert.That(b["minecraft:oak_leaves"].TintFaces, Is.EqualTo(0b111111));
        Assert.That(b["minecraft:birch_leaves"].Tint, Is.EqualTo(BlockTints.Fixed));
        Assert.That(b["minecraft:birch_leaves"].TintColor, Is.EqualTo(new[] { 128, 167, 85 }));
        Assert.That(b["minecraft:birch_leaves"].Render, Is.EqualTo(BlockRenders.Cutout), "leaves are always at least cutout, whatever their texels");
        Assert.That(b["minecraft:ice"].Render, Is.EqualTo(BlockRenders.Translucent));
    }

    [Test]
    public void AnAnimatedTexture_GetsConsecutiveFrameTiles_AndATimelinePerTick()
    {
        var jar = new FakeJar().WithStandardParents();
        // 3 frames stacked: red, green, blue
        jar.Texture("block/magma", 16, 48, (x, y) => y < 16 ? ((byte)255, (byte)0, (byte)0, (byte)255) : y < 32 ? ((byte)0, (byte)255, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255));
        jar.Meta("block/magma", """{ "animation": { "frametime": 2 } }""");
        jar.Model("block/magma_block", """{ "parent": "block/cube_all", "textures": { "all": "block/magma" } }""");
        jar.State("magma_block", """{ "variants": { "": { "model": "block/magma_block" } } }""");
        var r = Build(jar);
        int t = r.Assets.Blocks["minecraft:magma_block"].Faces[0];
        Assert.That(r.Assets.Animations, Has.Count.EqualTo(1));
        var a = r.Assets.Animations[0];
        Assert.That(a.Tile, Is.EqualTo(t));
        Assert.That(a.Timeline, Is.EqualTo(new[] { t, t, t + 1, t + 1, t + 2, t + 2 }), "each frame for 2 ticks");
        Assert.That(PixelOf(Tile(r, t), 16, 0, 0).r, Is.EqualTo(255));
        Assert.That(PixelOf(Tile(r, t + 1), 16, 0, 0).g, Is.EqualTo(255));
        Assert.That(PixelOf(Tile(r, t + 2), 16, 0, 0).b, Is.EqualTo(255));
    }

    [Test]
    public void AnAnimationWithAFrameList_PlaysThemInThatOrder_WithTheirOwnTimes()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Texture("block/fire", 16, 32, (x, y) => y < 16 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)255, (byte)0, (byte)255));
        jar.Meta("block/fire", """{ "animation": { "frametime": 3, "frames": [ 1, { "index": 0, "time": 5 }, 1 ] } }""");
        jar.Model("block/fire_block", """{ "parent": "block/cube_all", "textures": { "all": "block/fire" } }""");
        jar.State("fire_block", """{ "variants": { "": { "model": "block/fire_block" } } }""");
        var r = Build(jar);
        int t = r.Assets.Blocks["minecraft:fire_block"].Faces[0];
        Assert.That(r.Assets.Animations[0].Timeline, Is.EqualTo(new[] { t + 1, t + 1, t + 1, t, t, t, t, t, t + 1, t + 1, t + 1 }));
    }

    [Test]
    public void ATextureWithoutAnimationData_IsOneTile_EvenWhenTheImageIsTaller()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Texture("block/tall", 16, 32, (x, y) => y < 16 ? ((byte)9, (byte)9, (byte)9, (byte)255) : ((byte)200, (byte)200, (byte)200, (byte)255));
        jar.Model("block/t", """{ "parent": "block/cube_all", "textures": { "all": "block/tall" } }""");
        jar.State("t", """{ "variants": { "": { "model": "block/t" } } }""");
        var r = Build(jar);
        Assert.That(r.Assets.TileCount, Is.EqualTo(1));
        Assert.That(r.Assets.Animations, Is.Empty);
        Assert.That(PixelOf(Tile(r, 0), 16, 0, 0).r, Is.EqualTo(9), "the top square");
    }

    [Test]
    public void AFaceRotation_IsBakedIntoItsTile()
    {
        var jar = new FakeJar().WithStandardParents();
        // an arrow: the top-left pixel is red, everything else dark
        jar.Texture("block/arrow", 16, 16, (x, y) => (x == 0 && y == 0) ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)10, (byte)10, (byte)10, (byte)255));
        jar.Model("block/rot", """
            { "textures": { "t": "block/arrow" }, "elements": [ { "from": [0,0,0], "to": [16,16,16], "faces": {
            "down": { "texture": "#t" }, "up": { "texture": "#t", "rotation": 90 }, "north": { "texture": "#t" }, "south": { "texture": "#t" }, "west": { "texture": "#t" }, "east": { "texture": "#t" } } } ] }
            """);
        jar.State("rot", """{ "variants": { "": { "model": "block/rot" } } }""");
        var r = Build(jar);
        var f = r.Assets.Blocks["minecraft:rot"].Faces;
        Assert.That(f[4], Is.Not.EqualTo(f[0]), "the turned face is another tile");
        // a clockwise quarter turn takes the top-left pixel to the top-right
        Assert.That(PixelOf(Tile(r, f[4]), 16, 15, 0).r, Is.EqualTo(255));
        Assert.That(PixelOf(Tile(r, f[0]), 16, 0, 0).r, Is.EqualTo(255));
    }

    [Test]
    public void ANonCubeModel_IsAShape_DrawnWithItsTexture_AndNothingIsLost()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/planks", 160, 120, 70);
        jar.Model("block/slab", """
            { "textures": { "t": "block/planks" }, "elements": [ { "from": [0,0,0], "to": [16,8,16], "faces": {
            "down": { "texture": "#t" }, "up": { "texture": "#t" }, "north": { "texture": "#t" }, "south": { "texture": "#t" }, "west": { "texture": "#t" }, "east": { "texture": "#t" } } } ] }
            """);
        jar.State("oak_slab", """{ "variants": { "type=bottom": { "model": "block/slab" }, "type=top": { "model": "block/slab" } } }""");
        var r = Build(jar);
        var slab = r.Assets.Blocks["minecraft:oak_slab"];
        Assert.That(slab.Kind, Is.EqualTo(BlockKinds.Shape));
        Assert.That(PixelOf(Tile(r, slab.Faces[0]), 16, 2, 2).r, Is.EqualTo(160));
    }

    private static BlockAsset ShapeWithElements(params (double y0, double y1)[] elements)
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/t", 100, 100, 100);
        var els = string.Join(",", elements.Select(e => $$"""{ "from": [0,{{e.y0}},0], "to": [16,{{e.y1}},16], "faces": { "up": { "texture": "#t" }, "north": { "texture": "#t" } } }"""));
        jar.Model("block/m", $$"""{ "textures": { "t": "block/t" }, "elements": [ {{els}} ] }""");
        jar.State("thing", """{ "variants": { "": { "model": "block/m" } } }""");
        return Build(jar).Assets.Blocks["minecraft:thing"];
    }

    [Test]
    public void AFlatShape_HasItsThickness_AndATallOrHalfHighOneHasNone()
    {
        Assert.That(ShapeWithElements((0, 1)).Thickness, Is.EqualTo(1), "a carpet");
        Assert.That(ShapeWithElements((0, 0)).Thickness, Is.EqualTo(1), "a rail is a plane at the floor: one layer");
        Assert.That(ShapeWithElements((0, 2)).Thickness, Is.EqualTo(2), "a snow layer");
        Assert.That(ShapeWithElements((0, 2.5)).Thickness, Is.EqualTo(3), "rounded up: never thinner than the model");
        Assert.That(ShapeWithElements((0, 6)).Thickness, Is.EqualTo(6), "a daylight sensor");
        Assert.That(ShapeWithElements((0, 1), (0, 3)).Thickness, Is.EqualTo(3), "the highest element decides");
        Assert.That(ShapeWithElements((0, 8)).Thickness, Is.EqualTo(0), "a slab is half a block: left a cube until the registry knows its state");
        Assert.That(ShapeWithElements((0, 16)).Thickness, Is.EqualTo(0), "stairs and fences reach the top");
        Assert.That(ShapeWithElements((0, 1), (0, 16)).Thickness, Is.EqualTo(0), "one tall element makes it tall");
        Assert.That(ShapeWithElements((14, 16)).Thickness, Is.EqualTo(0), "something hanging at the top is not a layer on the floor");
        Assert.That(ShapeWithElements((3, 5)).Thickness, Is.EqualTo(0), "a floating piece is not a layer on the floor either");
    }

    [Test]
    public void Water_IsAnAnimatedTintedFluid_AndAirIsNothing()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Texture("block/water_still", 16, 32, (x, y) => y < 16 ? ((byte)10, (byte)10, (byte)200, (byte)180) : ((byte)20, (byte)20, (byte)220, (byte)180));
        jar.Meta("block/water_still", """{ "animation": { "frametime": 2 } }""");
        var r = Build(jar);
        var water = r.Assets.Blocks["minecraft:water"];
        Assert.That(water.Kind, Is.EqualTo(BlockKinds.Water));
        Assert.That(water.Tint, Is.EqualTo(BlockTints.Water));
        Assert.That(water.TintColor, Is.EqualTo(new[] { 63, 118, 228 }));
        Assert.That(r.Assets.Animations.Any(a => a.Tile == water.Faces[0]), Is.True);
        Assert.That(r.Assets.Blocks["minecraft:air"].Kind, Is.EqualTo(BlockKinds.None));
        Assert.That(r.Assets.Blocks["minecraft:flowing_water"].Faces, Is.EqualTo(water.Faces));
    }

    [Test]
    public void TheAtlas_PutsEveryTileWhereItsNumberSays_AndTheJsonRoundTrips()
    {
        var jar = new FakeJar().WithStandardParents();
        var rng = new Random(5);
        for (int i = 0; i < 40; i++) jar.SimpleCube($"b{i:D2}", (byte)(i * 6), (byte)(255 - i * 6), (byte)(i * 3));
        var r = Build(jar);
        Assert.That(r.Assets.TileCount, Is.EqualTo(40));
        Assert.That(r.Assets.TilesPerRow, Is.EqualTo(7), "ceil(sqrt(40)) wide");
        Assert.That(r.AtlasWidth, Is.EqualTo(7 * 16));
        Assert.That(r.AtlasHeight, Is.EqualTo(6 * 16));
        for (int i = 0; i < 40; i++)
        {
            int tile = r.Assets.Blocks[$"minecraft:b{i:D2}"].Faces[0];
            Assert.That(PixelOf(Tile(r, tile), 16, 7, 7).r, Is.EqualTo((byte)(i * 6)), $"block {i}");
        }
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(r.Assets, BlockAssetsJson.Default.BlockAssets);
        var back = System.Text.Json.JsonSerializer.Deserialize(bytes, BlockAssetsJson.Default.BlockAssets)!;
        Assert.That(back.TileCount, Is.EqualTo(40));
        Assert.That(back.Blocks["minecraft:b07"].Faces, Is.EqualTo(r.Assets.Blocks["minecraft:b07"].Faces));
        Assert.That(System.Text.Encoding.UTF8.GetString(bytes), Does.Contain("\"tilesPerRow\":7"), "camelCase names");
    }

    [Test]
    public void ABigTexture_IsScaledDownToTheTileSize_NotCropped()
    {
        var jar = new FakeJar().WithStandardParents();
        // 32x32: left half red, right half green
        jar.Texture("block/hd", 32, 32, (x, y) => x < 16 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)255, (byte)0, (byte)255));
        jar.Model("block/hd_block", """{ "parent": "block/cube_all", "textures": { "all": "block/hd" } }""");
        jar.State("hd_block", """{ "variants": { "": { "model": "block/hd_block" } } }""");
        var r = Build(jar);
        var tile = Tile(r, r.Assets.Blocks["minecraft:hd_block"].Faces[0]);
        Assert.That(PixelOf(tile, 16, 3, 3).r, Is.EqualTo(255));
        Assert.That(PixelOf(tile, 16, 12, 3).g, Is.EqualTo(255), "the right half survived the scaling");
    }

    [Test]
    public void AModelWithAParentThatLoopsBack_IsRejected_NotLoopedForever()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Model("block/a", """{ "parent": "block/b" }""").Model("block/b", """{ "parent": "block/a" }""");
        jar.State("loop", """{ "variants": { "": { "model": "block/a" } } }""");
        using var client = jar.Build();
        Assert.Throws<InvalidDataException>(() => new BlockAssetBuilder(client, "test").Build());
    }

    [Test]
    public void TheChildModel_OverridesTheParentsTextures_AndReplacesItsElements()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Model("block/base", """{ "parent": "block/cube_all", "textures": { "all": "block/one" } }""");
        jar.Model("block/child", """{ "parent": "block/base", "textures": { "all": "block/two" } }""");
        using var client = jar.Build();
        var model = new ModelResolver(client).Resolve("minecraft:block/child")!;
        Assert.That(model.ResolveTexture("#north"), Is.EqualTo("block/two"));
        Assert.That(model.Ancestors, Is.EqualTo(new[] { "block/base", "block/cube_all", "block/cube" }));
        Assert.That(model.Elements, Has.Count.EqualTo(1));
        Assert.That(model.Elements[0].IsFullCube, Is.True);
        Assert.That(model.ResolveTexture("#missing"), Is.Null);
    }

    [Test]
    public void TheStatePicker_PrefersNoProperties_ThenThePlainestVariant()
    {
        var plain = BlockStateFile.Parse("""{ "variants": { "": { "model": "m" } } }""").DefaultModel()!;
        Assert.That(plain.Model, Is.EqualTo("m"));
        var picked = BlockStateFile.Parse("""
            { "variants": { "facing=east,lit=true": { "model": "a" }, "facing=south,lit=false": { "model": "b" }, "facing=north,lit=true": { "model": "c" }, "facing=north,lit=false": { "model": "d" } } }
            """).DefaultModel()!;
        Assert.That(picked.Model, Is.EqualTo("d"), "north and not lit");
        var weighted = BlockStateFile.Parse("""{ "variants": { "": [ { "model": "first", "weight": 1 }, { "model": "second", "weight": 3 } ] } }""").DefaultModel()!;
        Assert.That(weighted.Model, Is.EqualTo("first"));
        var multipart = BlockStateFile.Parse("""{ "multipart": [ { "apply": { "model": "post" } }, { "when": { "north": "true" }, "apply": { "model": "side", "y": 90 } } ] }""").DefaultModel()!;
        Assert.That(multipart.Model, Is.EqualTo("post"));
    }

    [Test]
    public void TheColourMap_IsLookedUpTheGameWay()
    {
        var jar = new FakeJar();
        // The colour at (x, y) encodes its position
        jar.Texture("colormap/grass", 256, 256, (x, y) => ((byte)x, (byte)y, (byte)7, (byte)255));
        using var client = jar.Build();
        var map = Colormap.TryLoad(client, "assets/minecraft/textures/colormap/grass.png")!;
        // Plains (temperature 0.8, downfall 0.4) with the game's own double arithmetic: x = (int)((1 - 0.8) * 255) = 50 (1 - 0.8 is a hair
        // under 0.2), y = (int)((1 - 0.4 * 0.8) * 255) = 173
        Assert.That(map.Sample(0.8, 0.4), Is.EqualTo((50, 173, 7)));
        Assert.That(map.Sample(1.0, 1.0), Is.EqualTo((0, 0, 7)), "hot and wet: the top left");
        Assert.That(map.Sample(0.0, 0.0), Is.EqualTo((255, 255, 7)), "cold and dry: the bottom right");
        Assert.That(map.Sample(5, 5), Is.EqualTo(map.Sample(1, 1)), "clamped");
    }

    [Test]
    public async Task AJarThatIsNamedExplicitly_IsUsed_AndOneThatIsMissingIsAnError()
    {
        var path = Path.Combine(Path.GetTempPath(), "aubs-assets-test-" + Guid.NewGuid().ToString("N") + ".jar");
        File.WriteAllBytes(path, new byte[] { 1 });
        try
        {
            Assert.That(await JarLocator.FindOrDownloadAsync("1.0", path), Is.EqualTo(path));
        }
        finally { File.Delete(path); }
        Assert.ThrowsAsync<FileNotFoundException>(async () => await JarLocator.FindOrDownloadAsync("1.0", path));
    }
}
