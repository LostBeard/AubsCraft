using AubsCraft.Admin.Rendering;
using AubsCraft.Live.Model.Biomes;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>What falls where: the biome decides rain, snow or nothing, the column's heights say where it stops, and the window follows the camera.</summary>
public class WeatherMapTests
{
    private static ushort[] Flat(ushort h) => Enumerable.Repeat(h, 256).ToArray();

    private static uint Cell(uint[] cells, int x0, int z0, int x, int z) => cells[(z - z0) * WeatherMap.Side + (x - x0)];

    [Test]
    public void TheBiomeDecides_TooHotForRain_ColdEnoughForSnow_RainElsewhere()
    {
        Assert.That(WeatherMap.KindOf(BiomeData.IdOf("minecraft:desert")), Is.EqualTo(WeatherPipeline.KindNone));
        Assert.That(WeatherMap.KindOf(BiomeData.IdOf("minecraft:savanna")), Is.EqualTo(WeatherPipeline.KindNone));
        Assert.That(WeatherMap.KindOf(BiomeData.IdOf("minecraft:badlands")), Is.EqualTo(WeatherPipeline.KindNone));
        Assert.That(WeatherMap.KindOf(BiomeData.IdOf("minecraft:snowy_plains")), Is.EqualTo(WeatherPipeline.KindSnow));
        Assert.That(WeatherMap.KindOf(BiomeData.IdOf("minecraft:frozen_peaks")), Is.EqualTo(WeatherPipeline.KindSnow));
        Assert.That(WeatherMap.KindOf(BiomeData.IdOf("minecraft:plains")), Is.EqualTo(WeatherPipeline.KindRain));
        Assert.That(WeatherMap.KindOf(BiomeData.IdOf("minecraft:jungle")), Is.EqualTo(WeatherPipeline.KindRain));
        Assert.That(WeatherMap.KindOf(BiomeData.IdOf("minecraft:taiga")), Is.EqualTo(WeatherPipeline.KindRain));
        Assert.That(WeatherMap.KindOf(BiomeData.Unknown), Is.EqualTo(WeatherPipeline.KindRain), "an unknown biome is plains");
    }

    [Test]
    public void TheWindowHoldsEachColumnsHeightAndKind_ByWorldPosition()
    {
        var map = new WeatherMap();
        var heights = new ushort[256];
        for (int i = 0; i < 256; i++) heights[i] = (ushort)(100 + i);
        // The column (1, -1): world x 16..31, z -16..-1; biomes: desert in the first 4 x 4 cell, plains elsewhere
        var biomes = Enumerable.Repeat(BiomeData.IdOf("minecraft:plains"), 16).ToArray();
        biomes[0] = BiomeData.IdOf("minecraft:desert");
        map.SetColumn(1, -1, heights, biomes);
        Assert.That(map.Build(20f, -8f, out int x0, out int z0, out var cells), Is.True);
        Assert.That(x0, Is.EqualTo(20 - WeatherMap.Side / 2));
        Assert.That(z0, Is.EqualTo(-8 - WeatherMap.Side / 2));
        // World (17, -3): local (1, 13): height index 13 * 16 + 1; biome cell (13 >> 2) * 4 + (1 >> 2) = 12: plains
        uint inRain = Cell(cells, x0, z0, 17, -3);
        Assert.That(inRain & 0xFFFF, Is.EqualTo(100 + 13 * 16 + 1));
        Assert.That(inRain >> 16, Is.EqualTo(WeatherPipeline.KindRain));
        // World (16, -16): local (0, 0): in the desert cell
        uint inDesert = Cell(cells, x0, z0, 16, -16);
        Assert.That(inDesert >> 16, Is.EqualTo(WeatherPipeline.KindNone));
        // A column that is not loaded: nothing
        Assert.That(Cell(cells, x0, z0, 9, -8), Is.Zero);
    }

    [Test]
    public void ABuildIsOnlyDoneWhenSomethingChanged_AColumnComingOrGoingOrTheCameraCrossingABlock()
    {
        var map = new WeatherMap();
        map.SetColumn(0, 0, Flat(90), null);
        Assert.That(map.Build(8.2f, 8.2f, out _, out _, out _), Is.True);
        Assert.That(map.Build(8.9f, 8.9f, out _, out _, out _), Is.False, "the same block");
        Assert.That(map.Build(9.1f, 8.9f, out _, out _, out var moved), Is.True, "a block over");
        map.SetColumn(1, 0, Flat(95), null);
        Assert.That(map.Build(9.1f, 8.9f, out _, out _, out _), Is.True, "a column arrived");
        map.RemoveColumn(1, 0);
        Assert.That(map.Build(9.1f, 8.9f, out _, out _, out _), Is.True, "a column left");
        Assert.That(map.Build(9.1f, 8.9f, out _, out _, out _), Is.False);
        // A column without biomes is rain (plains)
        map.Build(8f, 8f, out int x0, out int z0, out var cells);
        Assert.That(Cell(cells, x0, z0, 8, 8), Is.EqualTo((WeatherPipeline.KindRain << 16) | 90u));
    }

    [Test]
    public void AColumnWithTheWrongNumberOfHeights_IsIgnored()
    {
        var map = new WeatherMap();
        map.SetColumn(0, 0, new ushort[10], null);
        Assert.That(map.ColumnCount, Is.Zero);
    }
}
