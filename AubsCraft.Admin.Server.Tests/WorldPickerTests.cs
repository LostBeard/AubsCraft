using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Which block a ray hits, and through which face: the basis of clicking in the world to break or place.</summary>
public class WorldPickerTests
{
    // A world of chunks made by hand: names at block positions, air everywhere else
    private sealed class World
    {
        private readonly Dictionary<(int, int), ChunkResult> _chunks = new();

        public World Set(int x, int y, int z, string name)
        {
            int cx = x >> 4, cz = z >> 4;
            if (!_chunks.TryGetValue((cx, cz), out var chunk))
                _chunks[(cx, cz)] = chunk = new ChunkResult(new ushort[98304], ["minecraft:air"]);
            int id = chunk.Palette.IndexOf(name);
            if (id < 0) { id = chunk.Palette.Count; chunk.Palette.Add(name); }
            chunk.Blocks[(x & 15) + (z & 15) * 16 + (y + 64) * 256] = (ushort)id;
            return this;
        }

        public ChunkResult? At(int cx, int cz) => _chunks.GetValueOrDefault((cx, cz));
    }

    private static WorldPicker.Hit? Cast(World w, double ox, double oy, double oz, double dx, double dy, double dz, double max = 64)
        => WorldPicker.Cast(w.At, ox, oy, oz, dx, dy, dz, max);

    [Test]
    public void ARayAlongX_HitsTheFirstSolidBlock_ThroughItsWestFace()
    {
        var w = new World().Set(5, 10, 3, "minecraft:stone").Set(8, 10, 3, "minecraft:dirt");
        var hit = Cast(w, 0.5, 10.5, 3.5, 1, 0, 0)!;
        Assert.That((hit.X, hit.Y, hit.Z), Is.EqualTo((5, 10, 3)));
        Assert.That((hit.NormalX, hit.NormalY, hit.NormalZ), Is.EqualTo((-1, 0, 0)), "entered from the west, so the block to place on that face is at x 4");
        Assert.That(hit.Block, Is.EqualTo("minecraft:stone"));
        Assert.That(hit.Distance, Is.EqualTo(4.5).Within(1e-9));
        Assert.That(hit.HitX, Is.EqualTo(5).Within(1e-9));
    }

    [Test]
    public void LookingDown_HitsTheTopFace_AndUp_TheBottom()
    {
        var w = new World().Set(2, 0, 2, "minecraft:grass_block[snowy=false]");
        var down = Cast(w, 2.5, 5.5, 2.5, 0, -1, 0)!;
        Assert.That((down.X, down.Y, down.Z, down.NormalY), Is.EqualTo((2, 0, 2, 1)));
        var up = Cast(w, 2.5, -5.5, 2.5, 0, 1, 0)!;
        Assert.That((up.Y, up.NormalY), Is.EqualTo((0, -1)));
    }

    [Test]
    public void ADiagonalRay_ReportsTheFaceItActuallyCrossed()
    {
        // From (0.5, 5.5, 0.5) toward (+1, -1, 0): it walks x and y alternately; the block at (3, 2, 0) is entered through its west or top face
        var w = new World().Set(3, 2, 0, "minecraft:stone");
        var hit = Cast(w, 0.5, 5.5, 0.5, 1, -1, 0)!;
        Assert.That((hit.X, hit.Y, hit.Z), Is.EqualTo((3, 2, 0)));
        Assert.That(Math.Abs(hit.NormalX) + Math.Abs(hit.NormalY) + Math.Abs(hit.NormalZ), Is.EqualTo(1));
        // Whichever face, the neighbour on it is empty and adjacent
        Assert.That(hit.HitX, Is.EqualTo(hit.NormalX != 0 ? 3 : hit.HitX));
    }

    [Test]
    public void WaterAndAirAreSeenThrough_Lava_AndThePlants_AreNot_AsFarAsPlants()
    {
        var w = new World().Set(1, 0, 0, "minecraft:water").Set(2, 0, 0, "minecraft:water[level=3]").Set(3, 0, 0, "minecraft:lava").Set(4, 0, 0, "minecraft:short_grass").Set(5, 0, 0, "minecraft:stone");
        var hit = Cast(w, 0.5, 0.5, 0.5, 1, 0, 0)!;
        Assert.That(hit.Block, Is.EqualTo("minecraft:short_grass"), "a plant can be picked (to break it or to place over it)");
        Assert.That(hit.X, Is.EqualTo(4));
    }

    [Test]
    public void ARayThatStartsInsideABlock_HitsItAtOnce_FromTheSideItWouldLeaveBackwards()
    {
        var w = new World().Set(0, 0, 0, "minecraft:stone");
        var hit = Cast(w, 0.5, 0.5, 0.5, 0, 0, 1)!;
        Assert.That((hit.X, hit.Y, hit.Z), Is.EqualTo((0, 0, 0)));
        Assert.That(hit.Distance, Is.Zero);
        Assert.That((hit.NormalX, hit.NormalY, hit.NormalZ), Is.EqualTo((0, 0, -1)));
    }

    [Test]
    public void NothingWithinReach_IsNull_AndAnUnloadedColumnIsAir()
    {
        var w = new World().Set(100, 0, 0, "minecraft:stone");
        Assert.That(Cast(w, 0.5, 0.5, 0.5, 1, 0, 0, max: 50), Is.Null, "too far");
        Assert.That(Cast(w, 0.5, 0.5, 0.5, 1, 0, 0, max: 120)!.X, Is.EqualTo(100));
        Assert.That(Cast(w, 0.5, 0.5, 0.5, -1, 0, 0), Is.Null, "the other way is unloaded: nothing");
        Assert.That(Cast(w, 0.5, 0.5, 0.5, 0, 0, 0), Is.Null, "no direction");
        Assert.That(Cast(w, 0.5, 0.5, 0.5, double.NaN, 0, 0), Is.Null);
    }

    [Test]
    public void RaysCrossChunkBorders_AndGoBelowZero()
    {
        var w = new World().Set(-17, 5, -33, "minecraft:stone");
        var hit = Cast(w, 0.5, 5.5, -32.5, -1, 0, 0)!;
        Assert.That((hit.X, hit.Y, hit.Z, hit.NormalX), Is.EqualTo((-17, 5, -33, 1)));
        // The same block approached along z through two chunks
        var along = Cast(w, -16.5, 5.5, 10.5, 0, 0, -1, 80)!;
        Assert.That((along.X, along.Z, along.NormalZ), Is.EqualTo((-17, -33, 1)));
    }

    [Test]
    public void ARayLeavingTheWorldVertically_StopsThere()
    {
        var w = new World();
        Assert.That(Cast(w, 0.5, 100.5, 0.5, 0, 1, 0), Is.Null);
        Assert.That(Cast(w, 0.5, -30.5, 0.5, 0, -1, 0), Is.Null);
        var bedrock = new World().Set(0, -64, 0, "minecraft:bedrock");
        Assert.That(Cast(bedrock, 0.5, 5.5, 0.5, 0, -1, 0, max: 100)!.Y, Is.EqualTo(-64));
    }

    [Test]
    public void TheReachIsClamped()
    {
        var w = new World().Set(300, 0, 0, "minecraft:stone");
        Assert.That(Cast(w, 0.5, 0.5, 0.5, 1, 0, 0, max: 10_000), Is.Null, "past MaxReach even when asked for more");
    }
}
