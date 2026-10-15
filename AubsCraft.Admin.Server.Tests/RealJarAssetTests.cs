using AubsCraft.Assets;
using AubsCraft.Assets.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The generator on a REAL client jar (the version the servers run): what a synthetic jar cannot show - that every one of a game's ~1,100 blocks
/// resolves, every tile number is in range, and the output does not change between runs. The jar is found in the launcher folder or the cache,
/// or downloaded once from Mojang (checked against Mojang's SHA-1): offline with no cached jar this test fails, as it should.
/// </summary>
public class RealJarAssetTests
{
    private const string Version = "1.21.5";

    private static BuildResult? _result;
    private static byte[]? _json;

    private static async Task<(BuildResult result, byte[] json)> BuildOnceAsync()
    {
        if (_result == null)
        {
            var jarPath = await JarLocator.FindOrDownloadAsync(Version);
            using var jar = ClientJar.Open(jarPath);
            _result = new BlockAssetBuilder(jar, Version).Build();
            _json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_result.Assets, BlockAssetsJson.Default.BlockAssets);
        }
        return (_result, _json!);
    }

    [Test]
    public async Task EveryBlock_HasSixFaces_AllTilesAreInRange_AndEveryAnimationIsWellFormed()
    {
        var (r, _) = await BuildOnceAsync();
        var a = r.Assets;
        Assert.That(a.Blocks.Count, Is.GreaterThan(1000));
        Assert.That(a.TileCount, Is.InRange(800, 4000));
        Assert.That(r.AtlasRgba.Length, Is.EqualTo(r.AtlasWidth * r.AtlasHeight * 4));
        Assert.That(r.AtlasWidth, Is.EqualTo(a.TilesPerRow * a.TileSize));
        foreach (var (name, b) in a.Blocks)
        {
            Assert.That(b.Faces, Has.Length.EqualTo(6), name);
            foreach (var f in b.Faces) Assert.That(f, Is.InRange(-1, a.TileCount - 1), name);
            Assert.That(b.TintFaces, Is.InRange(0, 63), name);
            Assert.That(b.TintColor, Has.Length.EqualTo(3), name);
            Assert.That(new[] { BlockKinds.None, BlockKinds.Cube, BlockKinds.Cross, BlockKinds.Water, BlockKinds.Shape }, Does.Contain(b.Kind), name);
            Assert.That(new[] { BlockRenders.Opaque, BlockRenders.Cutout, BlockRenders.Translucent }, Does.Contain(b.Render), name);
            if (b.Kind != BlockKinds.None) Assert.That(b.Faces.Any(f => f >= 0) || name == "minecraft:heavy_core", Is.True, name + " has no texture");
        }
        Assert.That(a.Animations.Select(x => x.Tile).Distinct().Count(), Is.EqualTo(a.Animations.Count), "a tile is animated once");
        foreach (var anim in a.Animations)
        {
            Assert.That(anim.Timeline, Is.Not.Empty);
            Assert.That(anim.Tile, Is.InRange(0, a.TileCount - 1));
            Assert.That(anim.Timeline.All(t => t >= 0 && t < a.TileCount), Is.True, $"animation of tile {anim.Tile}");
            Assert.That(anim.Timeline.Distinct().Count(), Is.GreaterThan(1), $"animation of tile {anim.Tile} shows one frame");
        }
    }

    [Test]
    public async Task KnownBlocks_AreWhatTheyLookLikeInTheGame()
    {
        var (r, _) = await BuildOnceAsync();
        var b = r.Assets.Blocks;
        BlockAsset Get(string n) => b["minecraft:" + n];

        Assert.That(Get("stone").Kind, Is.EqualTo(BlockKinds.Cube));
        Assert.That(Get("stone").Render, Is.EqualTo(BlockRenders.Opaque));
        Assert.That(Get("stone").Faces.Distinct().Count(), Is.EqualTo(1));

        var grass = Get("grass_block");
        Assert.That(grass.Tint, Is.EqualTo(BlockTints.Grass));
        Assert.That(grass.TintFaces, Is.EqualTo(1 << 4), "only the top takes the biome colour at draw time");
        Assert.That(grass.Faces[0], Is.Not.EqualTo(grass.Faces[4]), "side and top differ");
        Assert.That(grass.Faces[5], Is.EqualTo(Get("dirt").Faces[0]), "the bottom of grass is dirt");
        // The plains grass colour (the game's 0x91BD59)
        Assert.That(grass.TintColor, Is.EqualTo(new[] { 145, 189, 89 }).Within(3));

        var log = Get("oak_log");
        Assert.That(log.Faces[0], Is.EqualTo(log.Faces[3]), "bark on every side");
        Assert.That(log.Faces[4], Is.EqualTo(log.Faces[5]), "rings top and bottom");
        Assert.That(log.Faces[0], Is.Not.EqualTo(log.Faces[4]));

        Assert.That(Get("furnace").Faces[3], Is.Not.EqualTo(Get("furnace").Faces[2]), "the furnace front is on the north side, the south is plain");
        Assert.That(Get("oak_leaves").Render, Is.EqualTo(BlockRenders.Cutout));
        Assert.That(Get("oak_leaves").Tint, Is.EqualTo(BlockTints.Foliage));
        Assert.That(Get("birch_leaves").Tint, Is.EqualTo(BlockTints.Fixed));
        Assert.That(Get("glass").Render, Is.EqualTo(BlockRenders.Cutout));
        Assert.That(Get("short_grass").Kind, Is.EqualTo(BlockKinds.Cross));
        Assert.That(Get("poppy").Kind, Is.EqualTo(BlockKinds.Cross));
        Assert.That(Get("air").Kind, Is.EqualTo(BlockKinds.None));
        Assert.That(Get("oak_stairs").Kind, Is.EqualTo(BlockKinds.Shape));

        var water = Get("water");
        Assert.That(water.Kind, Is.EqualTo(BlockKinds.Water));
        Assert.That(r.Assets.Animations.Any(a => a.Tile == water.Faces[0]), Is.True, "water animates");
        Assert.That(r.Assets.Animations.Any(a => a.Tile == Get("lava").Faces[0]), Is.True, "lava animates");
        Assert.That(r.Assets.Animations.Any(a => a.Tile == Get("magma_block").Faces[0]), Is.True, "magma animates");
        Assert.That(r.Assets.Animations.Any(a => a.Tile == Get("sea_lantern").Faces[0]), Is.True, "the sea lantern animates");

        Assert.That(b.Values.Count(x => x.Kind == BlockKinds.Cube), Is.GreaterThan(300));
        Assert.That(b.Values.Count(x => x.Kind == BlockKinds.Cross), Is.GreaterThan(40));
    }

    [Test]
    public async Task TheOutput_IsTheSameEveryTime()
    {
        var (first, json) = await BuildOnceAsync();
        using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync(Version));
        var second = new BlockAssetBuilder(jar, Version).Build();
        Assert.That(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(second.Assets, BlockAssetsJson.Default.BlockAssets), Is.EqualTo(json));
        Assert.That(second.AtlasRgba, Is.EqualTo(first.AtlasRgba));
        Assert.That(second.Report, Is.EqualTo(first.Report));
    }

    [Test]
    public async Task TheThinBlocksOfTheRealGame_AreFound_AndNoTallOneIs()
    {
        var (r, _) = await BuildOnceAsync();
        var thin = r.Assets.Blocks.Where(b => b.Value.Thickness > 0).ToDictionary(b => b.Key, b => b.Value.Thickness);
        TestContext.Progress.WriteLine($"[assets] {thin.Count} thin blocks: " + string.Join(", ", thin.Select(t => $"{t.Key.Replace("minecraft:", "")}={t.Value}").Take(80)));
        foreach (var name in new[] { "minecraft:rail", "minecraft:powered_rail", "minecraft:detector_rail", "minecraft:activator_rail", "minecraft:white_carpet", "minecraft:moss_carpet",
                     "minecraft:stone_pressure_plate", "minecraft:oak_pressure_plate", "minecraft:snow", "minecraft:lily_pad" })
            Assert.That(thin.ContainsKey(name), Is.True, name + " is a thin block");
        foreach (var name in new[] { "minecraft:oak_slab", "minecraft:oak_stairs", "minecraft:oak_fence", "minecraft:ladder", "minecraft:torch", "minecraft:glass_pane", "minecraft:oak_door", "minecraft:chest" })
            Assert.That(thin.ContainsKey(name), Is.False, name + " is not a layer on the floor");
        foreach (var (name, t) in thin) Assert.That(t, Is.InRange(1, 6), name);
    }

    [Test]
    public async Task TheStatesOfTheRealGame_AreFewEnoughForTheRegistry_AndTheCommonOnesAreThere()
    {
        var (r, _) = await BuildOnceAsync();
        var a = r.Assets;
        int stateEntries = a.Blocks.Keys.Count(k => k.Contains('['));
        TestContext.Progress.WriteLine($"[assets] {a.StateProps.Count} blocks with states, {stateEntries} state entries");
        // The registry holds 4,096 ids and the seabed variants of 2,048 of them: every state the game has must fit with room for a world's plain names
        Assert.That(stateEntries, Is.InRange(100, 1000));
        foreach (var name in a.StateProps.Keys) Assert.That(a.Blocks.ContainsKey(name), Is.True, name + " keeps its default asset");
        foreach (var key in a.Blocks.Keys.Where(k => k.Contains('[')))
        {
            var name = key[..key.IndexOf('[')];
            Assert.That(a.StateProps.ContainsKey(name), Is.True, key);
            var props = key[(key.IndexOf('[') + 1)..^1].Split(',').Select(p => p[..p.IndexOf('=')]).ToArray();
            Assert.That(props, Is.EqualTo(a.StateProps[name]), key + ": the key has exactly the properties of its block");
        }
        // What makes the picture right in a real world
        Assert.That(a.StateProps["minecraft:oak_log"], Is.EqualTo(new[] { "axis" }));
        Assert.That(a.StateProps["minecraft:rail"], Is.EqualTo(new[] { "shape" }));
        Assert.That(a.StateProps["minecraft:furnace"], Is.EquivalentTo(new[] { "facing", "lit" }));
        Assert.That(a.StateProps["minecraft:wheat"], Is.EqualTo(new[] { "age" }));
        Assert.That(a.Blocks.ContainsKey("minecraft:oak_log[axis=z]"), Is.True);
        Assert.That(a.Blocks.ContainsKey("minecraft:rail[shape=south_east]"), Is.True);
        // Not stateful: blocks whose states do not change the picture
        Assert.That(a.StateProps.ContainsKey("minecraft:stone"), Is.False);
        Assert.That(a.StateProps.ContainsKey("minecraft:oak_leaves"), Is.False, "leaves distance/persistent do not change the picture");
        // A lying log has its ends on the ends of its axis: tile of +Y of an axis=x log is a side, of +X an end
        var upright = a.Blocks["minecraft:oak_log[axis=y]"].Faces;
        var alongX = a.Blocks["minecraft:oak_log[axis=x]"].Faces;
        Assert.That(alongX[0], Is.Not.EqualTo(alongX[2]), "its east end is not its south side");
        Assert.That(upright[4], Is.Not.EqualTo(upright[0]), "upright: top is an end, sides are bark");
    }
}
