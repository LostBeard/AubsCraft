using AubsCraft.Assets;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model.Biomes;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The colours of grass, leaves and water in every biome, against the colours the game is known to show, from the game's own colour maps.</summary>
public class BiomeColorTests
{
    private static async Task<BiomeAssets> RealAsync()
    {
        using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync("1.21.5"));
        return new BiomeColorBuilder(jar, "1.21.5").Build();
    }

    private static int Rgb(byte[] colours, int id) => (colours[id * 3] << 16) | (colours[id * 3 + 1] << 8) | colours[id * 3 + 2];

    [TestCase("minecraft:plains", 0x91BD59, 0x77AB2F, 0x3F76E4)]
    [TestCase("minecraft:forest", 0x79C05A, 0x59AE30, 0x3F76E4)]
    [TestCase("minecraft:desert", 0xBFB755, 0xAEA42A, 0x3F76E4)]
    [TestCase("minecraft:jungle", 0x59C93C, 0x30BB0B, 0x3F76E4)]
    [TestCase("minecraft:taiga", 0x86B783, 0x68A464, 0x3F76E4)]
    [TestCase("minecraft:snowy_plains", 0x80B497, 0x60A17B, 0x3F76E4)]
    [TestCase("minecraft:savanna", 0xBFB755, 0xAEA42A, 0x3F76E4)]
    [TestCase("minecraft:badlands", 0x90814D, 0x9E814D, 0x3F76E4)]
    [TestCase("minecraft:dark_forest", 0x507A32, 0x59AE30, 0x3F76E4)]
    [TestCase("minecraft:swamp", 0x6A7039, 0x6A7039, 0x617B64)]
    [TestCase("minecraft:warm_ocean", 0x8EB971, 0x71A74D, 0x43D5EE)]
    [TestCase("minecraft:lukewarm_ocean", 0x8EB971, 0x71A74D, 0x45ADF2)]
    [TestCase("minecraft:cold_ocean", 0x8EB971, 0x71A74D, 0x3D57D6)]
    [TestCase("minecraft:frozen_ocean", 0x80B497, 0x60A17B, 0x3938C9)]
    public async Task ABiome_HasTheColoursTheGameShows(string name, int grass, int foliage, int water)
    {
        var assets = await RealAsync();
        int id = Array.IndexOf(assets.Names, name);
        Assert.That(id, Is.GreaterThanOrEqualTo(0), name);
        Assert.That(Rgb(assets.Grass, id), Is.EqualTo(grass), $"{name} grass: {Rgb(assets.Grass, id):X6}");
        Assert.That(Rgb(assets.Foliage, id), Is.EqualTo(foliage), $"{name} foliage: {Rgb(assets.Foliage, id):X6}");
        Assert.That(Rgb(assets.Water, id), Is.EqualTo(water), $"{name} water: {Rgb(assets.Water, id):X6}");
    }

    [Test]
    public async Task EveryBiome_HasEveryColour_AndTheTableIsSound()
    {
        var assets = await RealAsync();
        Assert.That(assets.Names, Is.Unique);
        Assert.That(assets.Names.Length, Is.EqualTo(BiomeData.All.Length));
        foreach (var arr in new[] { assets.Grass, assets.Foliage, assets.Water }) Assert.That(arr.Length, Is.EqualTo(assets.Names.Length * 3));
        for (int i = 0; i < assets.Names.Length; i++)
            foreach (var arr in new[] { assets.Grass, assets.Foliage, assets.Water })
                Assert.That(Rgb(arr, i), Is.Not.EqualTo(0), assets.Names[i]);
        Assert.That(assets.Names[assets.PlainsId], Is.EqualTo("minecraft:plains"));
        Assert.That(BiomeData.IdOf("minecraft:plains"), Is.EqualTo((byte)assets.PlainsId));
        Assert.That(BiomeData.IdOf("mymod:strange_land"), Is.EqualTo(BiomeData.Unknown));
        Assert.That(BiomeData.All.Length, Is.LessThan(BiomeData.Unknown), "ids fit a byte below the unknown marker");
    }

    [Test]
    public void TheLookup_FollowsTemperatureAcrossAndRainfallDown()
    {
        // A 256 x 256 map: red grows with the column (colder = left... the map's own x is 255 at temperature 0), green with the row
        var map = new byte[256 * 256 * 4];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++) { int i = (y * 256 + x) * 4; map[i] = (byte)x; map[i + 1] = (byte)y; map[i + 2] = 7; map[i + 3] = 255; }
        // temperature 1, rainfall 0: x = 0, y = 255
        Assert.That(BiomeColors.Lookup(map, 1f, 0f), Is.EqualTo((0 << 16) | (255 << 8) | 7));
        // temperature 0 clamps rain to 0: x = 255, y = 255
        Assert.That(BiomeColors.Lookup(map, 0f, 1f), Is.EqualTo((255 << 16) | (255 << 8) | 7));
        // temperature 1, rain 1: top left
        Assert.That(BiomeColors.Lookup(map, 1f, 1f), Is.EqualTo((0 << 16) | (0 << 8) | 7));
        // Out of range climates clamp (a desert is 2.0)
        Assert.That(BiomeColors.Lookup(map, 2f, 0f), Is.EqualTo(BiomeColors.Lookup(map, 1f, 0f)));
        Assert.That(BiomeColors.Lookup(map, -3f, 5f), Is.EqualTo(BiomeColors.Lookup(map, 0f, 1f)));
    }

    [Test]
    public void ABiomeThatFixesItsColour_IgnoresTheMap_AndTheModifiersApplyToWhatTheyShould()
    {
        var map = new byte[256 * 256 * 4];
        Array.Fill(map, (byte)100);
        var fixedGrass = new BiomeInfo("test:fixed", 0.5f, 0.5f, 0x112233, 0xAABBCC, 0x445566, GrassModifier.None);
        Assert.That(BiomeColors.Grass(fixedGrass, map), Is.EqualTo(0xAABBCC));
        Assert.That(BiomeColors.Foliage(fixedGrass, map), Is.EqualTo(0x445566));
        Assert.That(BiomeColors.Water(fixedGrass), Is.EqualTo(0x112233));
        var dark = fixedGrass with { Modifier = GrassModifier.DarkForest };
        Assert.That(BiomeColors.Grass(dark, map), Is.EqualTo(((0xAABBCC & 0xFEFEFE) + 0x28340A) >> 1), "the dark forest darkens whatever grass it has");
        Assert.That(BiomeColors.Foliage(dark, map), Is.EqualTo(0x445566), "and leaves it alone");
        var swamp = fixedGrass with { Modifier = GrassModifier.Swamp };
        Assert.That(BiomeColors.Grass(swamp, map), Is.EqualTo(0x6A7039));
    }
}
