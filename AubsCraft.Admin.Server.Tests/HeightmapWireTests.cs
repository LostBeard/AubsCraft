using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The far tier's heightmap frame in global block ids: exact bytes, in the layout SpawnDev.VoxelEngine's HeightmapLayout reads.</summary>
public class HeightmapWireTests
{
    private static GlobalBlockRegistry NewRegistry() =>
        new(TestUtil.Config(("BlockRegistry:FilePath", Path.Combine(TestUtil.NewTempDir(), "block-registry.json"))), TestUtil.Log<GlobalBlockRegistry>());

    private static HeightmapResult Map(List<string> palette, Action<int[], ushort[], int[], ushort[]> fill)
    {
        var heights = new int[256];
        var ids = new ushort[256];
        var seabedHeights = Enumerable.Repeat(-64, 256).ToArray();
        var seabedIds = new ushort[256];
        fill(heights, ids, seabedHeights, seabedIds);
        return new HeightmapResult(heights, ids, seabedHeights, seabedIds, palette);
    }

    [Test]
    public void IdsFrame_HasTheExactLayout_AndTheChunksOwnPaletteIsTranslated()
    {
        var reg = NewRegistry();
        reg.GetOrAdd("minecraft:granite"); // global 1, so palette indices differ from global ids
        var hm = Map(["minecraft:air", "minecraft:stone", "minecraft:water", "minecraft:sand"], (h, id, sh, sid) =>
        {
            h[0] = 70; id[0] = 1;                       // stone at 70
            h[1] = 62; id[1] = 2; sh[1] = 50; sid[1] = 3; // water at 62 over sand at 50
            h[255] = -64; id[255] = 3;                  // sand at the world bottom
        });

        var frame = HeightmapWire.BuildIdsFrame(-3, 7, hm, reg);
        var names = reg.Snapshot();

        Assert.That(frame, Has.Length.EqualTo(HeightmapWire.FrameBytes));
        Assert.That(HeightmapWire.DataBytes, Is.EqualTo(3072), "the layout VoxelEngine reads");
        Assert.That(BitConverter.ToInt32(frame, 0), Is.EqualTo(-3));
        Assert.That(BitConverter.ToInt32(frame, 4), Is.EqualTo(7));
        Assert.That(BitConverter.ToInt32(frame, 8), Is.EqualTo(reg.Count), "the count covers every id in the frame");

        int heights = HeightmapWire.DataOffset;
        int types = heights + 256 * 4;
        int seabedHeights = types + 256 * 2;
        int seabedTypes = seabedHeights + 256 * 4;
        string TypeName(int cell) => names[BitConverter.ToUInt16(frame, types + cell * 2)];
        string SeabedName(int cell) => names[BitConverter.ToUInt16(frame, seabedTypes + cell * 2)];

        Assert.That(BitConverter.ToInt32(frame, heights + 0 * 4), Is.EqualTo(70));
        Assert.That(TypeName(0), Is.EqualTo("minecraft:stone"));
        Assert.That(BitConverter.ToInt32(frame, heights + 1 * 4), Is.EqualTo(62));
        Assert.That(TypeName(1), Is.EqualTo("minecraft:water"));
        Assert.That(BitConverter.ToInt32(frame, seabedHeights + 1 * 4), Is.EqualTo(50));
        Assert.That(SeabedName(1), Is.EqualTo("minecraft:sand"));
        Assert.That(BitConverter.ToInt32(frame, heights + 255 * 4), Is.EqualTo(-64));
        Assert.That(TypeName(255), Is.EqualTo("minecraft:sand"));
        Assert.That(BitConverter.ToUInt16(frame, types + 2 * 2), Is.EqualTo((ushort)0), "no top block = id 0");
        Assert.That(BitConverter.ToInt32(frame, seabedHeights + 0 * 4), Is.EqualTo(-64), "no seabed = -64");
        Assert.That(BitConverter.ToUInt16(frame, seabedTypes + 0 * 2), Is.EqualTo((ushort)0), "no seabed = id 0");
    }

    [Test]
    public void TwoChunksWithDifferentPalettes_AgreeOnGlobalIds()
    {
        var reg = NewRegistry();
        var a = Map(["minecraft:air", "minecraft:dirt", "minecraft:stone"], (h, id, _, _) => { h[5] = 1; id[5] = 1; });
        var b = Map(["minecraft:air", "minecraft:stone", "minecraft:dirt"], (h, id, _, _) => { h[5] = 1; id[5] = 2; });
        int typesOffset = HeightmapWire.DataOffset + 256 * 4;
        var fa = HeightmapWire.BuildIdsFrame(0, 0, a, reg);
        var fb = HeightmapWire.BuildIdsFrame(1, 0, b, reg);
        Assert.That(BitConverter.ToUInt16(fa, typesOffset + 5 * 2), Is.EqualTo(BitConverter.ToUInt16(fb, typesOffset + 5 * 2)),
            "dirt is dirt in both frames, though its palette index differs");
    }
}
