using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Where the sky is blocked over each block of a column, read from the chunk's own heightmap: what the viewer's rain and snow stop on.</summary>
public class RainHeightTests
{
    // The game's packing: 9 bits a value, seven to a long, none across two
    private static long[] Pack(Func<int, int> value)
    {
        var data = new long[37];
        for (int i = 0; i < 256; i++) data[i / 7] |= (long)value(i) << ((i % 7) * 9);
        return data;
    }

    private static NbtCompound Chunk(NbtCompound? maps)
    {
        var sections = new NbtList(NbtTagType.Compound, 0);
        var root = new NbtCompound { ["sections"] = sections };
        if (maps != null) root["Heightmaps"] = maps;
        return root;
    }

    [Test]
    public void TheHeightsComeOutAsPacked_ForEveryColumn_InTheGamesOrder()
    {
        var heights = RegionReader.RainHeights(Chunk(new NbtCompound { ["MOTION_BLOCKING"] = Pack(i => 64 + (i * 7) % 300) }))!;
        Assert.That(heights.Length, Is.EqualTo(256));
        for (int i = 0; i < 256; i++) Assert.That(heights[i], Is.EqualTo(64 + (i * 7) % 300), $"column {i}");
    }

    [Test]
    public void TheValuesAtTheEnds_AreNotLostToNeighbours()
    {
        // The largest value a column can have (the world is 384 high) and zero, side by side, over a whole long
        var heights = RegionReader.RainHeights(Chunk(new NbtCompound { ["MOTION_BLOCKING"] = Pack(i => i % 2 == 0 ? 384 : 0) }))!;
        for (int i = 0; i < 256; i++) Assert.That(heights[i], Is.EqualTo(i % 2 == 0 ? 384 : 0), $"column {i}");
    }

    [Test]
    public void AChunkWithoutAHeightmap_OrWithATooShortOne_HasNone()
    {
        Assert.That(RegionReader.RainHeights(Chunk(null)), Is.Null);
        Assert.That(RegionReader.RainHeights(Chunk(new NbtCompound { ["WORLD_SURFACE"] = Pack(_ => 70) })), Is.Null);
        Assert.That(RegionReader.RainHeights(Chunk(new NbtCompound { ["MOTION_BLOCKING"] = new long[10] })), Is.Null);
    }

    [Test]
    public void TheColumnResult_CarriesThem()
    {
        var maps = new NbtCompound { ["MOTION_BLOCKING"] = Pack(i => 100 + i) };
        var list = new NbtList(NbtTagType.Compound, 1);
        list.Add(new NbtCompound { ["Y"] = (byte)0, ["block_states"] = new NbtCompound { ["palette"] = new Func<NbtList>(() => { var p = new NbtList(NbtTagType.Compound, 1); p.Add(new NbtCompound { ["Name"] = "minecraft:stone" }); return p; })() } });
        var root = new NbtCompound { ["sections"] = list, ["Heightmaps"] = maps };
        var result = RegionReader.ParseChunkNbt(root);
        Assert.That(result.RainHeights, Is.Not.Null);
        Assert.That(result.RainHeights![255], Is.EqualTo(355));
    }
}
