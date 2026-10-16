using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The light of a chunk column: what the game stored (two nibble arrays a section), laid out like the block ids, one byte a block.</summary>
public class LightTests
{
    private static byte[] Nibbles(Func<int, int> level)
    {
        var data = new byte[2048];
        for (int i = 0; i < 4096; i++) data[i >> 1] |= (byte)((level(i) & 15) << ((i & 1) * 4));
        return data;
    }

    private static NbtCompound Section(sbyte y, byte[]? sky, byte[]? block, string blockName = "minecraft:stone")
    {
        var palette = new NbtList(NbtTagType.Compound, 1);
        palette.Add(new NbtCompound { ["Name"] = blockName });
        var section = new NbtCompound { ["Y"] = (byte)y, ["block_states"] = new NbtCompound { ["palette"] = palette } };
        if (sky != null) section["SkyLight"] = sky;
        if (block != null) section["BlockLight"] = block;
        return section;
    }

    private static NbtCompound Chunk(byte? lightOn, params NbtCompound[] sections)
    {
        var list = new NbtList(NbtTagType.Compound, sections.Length);
        foreach (var s in sections) list.Add(s);
        var root = new NbtCompound { ["sections"] = list };
        if (lightOn != null) root["isLightOn"] = lightOn.Value;
        return root;
    }

    // The byte of block (x, y local to the section, z) in section index n (y -4 is 0)
    private static int At(byte[] light, int sectionIndex, int x, int y, int z) => light[sectionIndex * 4096 + y * 256 + z * 16 + x];

    [Test]
    public void TheNibbles_AreUnpackedInTheOrderOfTheBlocks_SkyHighBlockLow()
    {
        // Sky light = x, block light = y of the section, so a mix-up of the axes or the halves shows
        var sky = Nibbles(i => i & 15);
        var block = Nibbles(i => (i >> 8) & 15);
        var light = RegionReader.LightOf(Chunk(1, Section(-4, sky, block)))!;
        Assert.That(light.Length, Is.EqualTo(RegionReader.LightBytes));
        Assert.That(light.Length, Is.EqualTo(98304));
        foreach (var (x, y, z) in new[] { (0, 0, 0), (1, 0, 0), (15, 3, 7), (7, 15, 15), (8, 9, 2), (0, 15, 0) })
            Assert.That(At(light, 0, x, y, z), Is.EqualTo((x << 4) | y), $"({x},{y},{z})");
    }

    [Test]
    public void TheSectionsAreStackedFromTheBottom_AndSkyFillsWhatIsAboveTheTop()
    {
        var dark = Nibbles(_ => 0);
        var lit = Nibbles(_ => 9);
        var light = RegionReader.LightOf(Chunk(1, Section(-4, dark, lit), Section(-3, dark, dark), Section(-2, lit, dark)))!;
        Assert.That(At(light, 0, 3, 3, 3), Is.EqualTo(0x09), "the lowest section: block light 9");
        Assert.That(At(light, 1, 3, 3, 3), Is.EqualTo(0x00));
        Assert.That(At(light, 2, 3, 3, 3), Is.EqualTo(0x90), "sky light 9");
        Assert.That(At(light, 3, 0, 0, 0), Is.EqualTo(0xF0), "above the chunk's top section: open sky");
        Assert.That(At(light, 23, 15, 15, 15), Is.EqualTo(0xF0));
    }

    [Test]
    public void ASectionWithNoSkyArray_IsOpenAirWhenItIsEmptyAir_ElseDark()
    {
        var light = RegionReader.LightOf(Chunk(1, Section(-4, null, null, "minecraft:stone"), Section(-3, null, null, "minecraft:air"), Section(-2, null, null, "minecraft:stone")))!;
        Assert.That(At(light, 0, 5, 5, 5), Is.EqualTo(0x00), "solid and no sky array: dark");
        Assert.That(At(light, 1, 5, 5, 5), Is.EqualTo(0xF0), "air and no sky array: sky");
    }

    [Test]
    public void AChunkWhoseLightWasNeverWorkedOut_HasNone_AndOneWithNoSectionsNeither()
    {
        Assert.That(RegionReader.LightOf(Chunk(0, Section(-4, null, null))), Is.Null, "flagged unlit and no light arrays: never lit");
        Assert.That(RegionReader.LightOf(Chunk(0, Section(-4, Nibbles(_ => 5), null))), Is.Not.Null, "flagged 0 but with arrays: Paper's Starlight, which saves its light so");
        Assert.That(RegionReader.LightOf(Chunk(1)), Is.Null);
        Assert.That(RegionReader.LightOf(Chunk(null, Section(-4, Nibbles(_ => 5), null))), Is.Not.Null, "no flag: take it as lit");
    }

    [Test]
    public void TheSceneWorld_HasLightWhereItShould()
    {
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var regions = Path.Combine(world, "world", "region");
        if (!Directory.Exists(regions)) Assert.Ignore("No world at " + regions);
        byte[]? Light(int cx, int cz)
        {
            var file = Path.Combine(regions, $"r.{cx >> 5}.{cz >> 5}.mca");
            return File.Exists(file) ? RegionReader.ReadChunkLight(file, cx & 31, cz & 31) : null;
        }
        // The scene's row of light blocks: glowstone at x 30, z 56, y -60 (chunk 1, 3)
        var row = Light(1, 3);
        if (row == null) Assert.Ignore("The scene has no light data (an old build of it)");
        const int y = -60 + 64;
        int At2(byte[] l, int lx, int ly, int lz) => l[lx + lz * 16 + ly * 256];
        Assert.That(At2(row, 30 & 15, y, 56 & 15) & 15, Is.EqualTo(15), "the glowstone gives light 15 in its own cell");
        Assert.That(At2(row, 30 & 15, y + 1, 56 & 15) & 15, Is.InRange(13, 14), "the cell above it, one step away");
        Assert.That(At2(row, 30 & 15, y + 8, 56 & 15) >> 4, Is.EqualTo(15), "open sky overhead");
        Assert.That(At2(row, 30 & 15, y + 8, 56 & 15) & 15, Is.LessThanOrEqualTo(7), "and a long way from the glowstone it is dim (or none)");
        // The closed room (inside x 47..51, z 63..67, y -60..-58, a torch in the middle at 49, 65): the sky does not reach in
        var room = Light(3, 4);
        if (room == null) Assert.Ignore("The scene has no room (an old build of it)");
        Assert.That(At2(room, 49 & 15, y, 65 & 15) & 15, Is.InRange(13, 14), "the torch lights its own cell (14)");
        // The corner (51, 63) is in the chunk to the north (z 48..63)
        var north = Light(3, 3)!;
        Assert.That(At2(north, 51 & 15, y + 1, 63 & 15) >> 4, Is.EqualTo(0), "the corner of the room under the roof has no sky light");
        Assert.That(At2(north, 51 & 15, y + 1, 63 & 15) & 15, Is.InRange(8, 12), "and some of the torch's: it is two to four steps away");
        // Outside the room, level with it, the sky is open
        Assert.That(At2(room, 54 & 15, y + 1, 70 & 15) >> 4, Is.EqualTo(15), "outside it is open sky");
    }
}
