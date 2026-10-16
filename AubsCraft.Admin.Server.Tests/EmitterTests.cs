using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Particles;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Which blocks give off particles, how a column's emitters travel in a header, and the walk that finds them.</summary>
public class EmitterTests
{
    [TestCase("minecraft:campfire[facing=north,lit=true]", EmitterKind.Campfire)]
    [TestCase("minecraft:campfire[facing=north,lit=false]", EmitterKind.None)]
    [TestCase("minecraft:soul_campfire[facing=east,lit=true]", EmitterKind.SoulCampfire)]
    [TestCase("minecraft:furnace[facing=north,lit=true]", EmitterKind.Furnace)]
    [TestCase("minecraft:furnace[facing=north,lit=false]", EmitterKind.None)]
    [TestCase("minecraft:blast_furnace[facing=south,lit=true]", EmitterKind.Furnace)]
    [TestCase("minecraft:smoker[facing=west,lit=true]", EmitterKind.Furnace)]
    [TestCase("minecraft:fire[east=false,north=true,south=false,up=false,west=false]", EmitterKind.Fire)]
    [TestCase("minecraft:soul_fire", EmitterKind.SoulFire)]
    [TestCase("minecraft:torch", EmitterKind.Torch)]
    [TestCase("minecraft:wall_torch[facing=north]", EmitterKind.Torch)]
    [TestCase("minecraft:soul_torch", EmitterKind.SoulTorch)]
    [TestCase("minecraft:soul_wall_torch[facing=east]", EmitterKind.SoulTorch)]
    [TestCase("minecraft:redstone_torch[lit=true]", EmitterKind.RedstoneTorch)]
    [TestCase("minecraft:redstone_torch[lit=false]", EmitterKind.None)]
    [TestCase("minecraft:redstone_wall_torch[facing=south,lit=true]", EmitterKind.RedstoneTorch)]
    [TestCase("minecraft:candle[candles=2,lit=true]", EmitterKind.Candle)]
    [TestCase("minecraft:white_candle[candles=1,lit=false]", EmitterKind.None)]
    [TestCase("minecraft:candle_cake[lit=true]", EmitterKind.Candle)]
    [TestCase("minecraft:stone", EmitterKind.None)]
    [TestCase("minecraft:lantern", EmitterKind.None)]
    [TestCase("minecraft:air", EmitterKind.None)]
    [TestCase("othermod:campfire[lit=true]", EmitterKind.None)]
    public void TheBlockNamesThatGiveOffParticles(string key, EmitterKind expected) => Assert.That(EmitterClassifier.Classify(key), Is.EqualTo(expected));

    [Test]
    public void ASpotSurvivesTheHeader_AtAnyHeight()
    {
        var spots = new List<EmitterSpot>
        {
            new(0, -64, 0, EmitterKind.Torch), new(15, 319, 15, EmitterKind.Campfire), new(7, 62, 3, EmitterKind.Fire), new(1, 0, 14, EmitterKind.Candle),
        };
        Assert.That(EmitterCodec.Decode(EmitterCodec.Encode(spots)), Is.EqualTo(spots));
    }

    [Test]
    public void ADamagedHeader_IsNoEmitters_NotAnException()
    {
        Assert.That(EmitterCodec.Decode(null), Is.Empty);
        Assert.That(EmitterCodec.Decode(""), Is.Empty);
        Assert.That(EmitterCodec.Decode("not base64!!"), Is.Empty);
        Assert.That(EmitterCodec.Decode(Convert.ToBase64String(new byte[] { 1, 2, 3 })), Is.Empty, "not a whole number of spots");
        Assert.That(EmitterCodec.Decode(Convert.ToBase64String(new byte[] { 0x12, 10, 0, 200 })), Is.Empty, "an unknown kind is dropped");
    }

    [Test]
    public void ATorchFarm_IsCutAtTheLimit()
    {
        var spots = Enumerable.Range(0, 1000).Select(i => new EmitterSpot(i & 15, i % 300, (i >> 4) & 15, EmitterKind.Torch)).ToList();
        Assert.That(EmitterCodec.Decode(EmitterCodec.Encode(spots)).Count, Is.EqualTo(EmitterCodec.MaxPerColumn));
    }

    private static ChunkResult ChunkWith(params (int x, int y, int z, string key)[] blocks)
    {
        var palette = new List<string> { "minecraft:air" };
        var data = new ushort[16 * 16 * 384];
        foreach (var (x, y, z, key) in blocks)
        {
            int index = palette.IndexOf(key);
            if (index < 0) { palette.Add(key); index = palette.Count - 1; }
            data[x + z * 16 + (y + 64) * 256] = (ushort)index;
        }
        return new ChunkResult(data, palette);
    }

    [Test]
    public void TheScan_FindsEveryEmitter_AtItsOwnPlace_AndOnlyThose()
    {
        var chunk = ChunkWith((3, 70, 5, "minecraft:campfire[facing=north,lit=true]"), (9, -60, 12, "minecraft:torch"), (4, 70, 5, "minecraft:stone"),
            (15, 300, 0, "minecraft:furnace[facing=north,lit=true]"), (2, 71, 2, "minecraft:campfire[facing=north,lit=false]"));
        var spots = EmitterScan.Scan(chunk);
        Assert.That(spots, Is.EquivalentTo(new[]
        {
            new EmitterSpot(3, 70, 5, EmitterKind.Campfire), new EmitterSpot(9, -60, 12, EmitterKind.Torch), new EmitterSpot(15, 300, 0, EmitterKind.Furnace),
        }));
        Assert.That(spots[0].Y, Is.LessThan(spots[^1].Y).Or.EqualTo(spots[^1].Y), "lowest first");
    }

    [Test]
    public void AColumnWithoutEmitters_ScansToNothing()
        => Assert.That(EmitterScan.Scan(ChunkWith((1, 70, 1, "minecraft:stone"), (2, 70, 2, "minecraft:oak_planks"))), Is.Empty);
}
