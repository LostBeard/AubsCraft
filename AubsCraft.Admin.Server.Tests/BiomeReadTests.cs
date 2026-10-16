using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Biomes;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The biome of each 4 x 4 block cell of a column, read from the chunk's own data and carried to the viewer (the far tier's frame and the column response's header).</summary>
public class BiomeReadTests
{
    private static NbtList Names(params string[] names)
    {
        var list = new NbtList(NbtTagType.String, names.Length);
        foreach (var n in names) list.Add(n);
        return list;
    }

    // A section's biomes: 64 cells (y, z, x) by the given function, packed the way the game packs them (ceil(log2 n) bits, at least one, aligned within a long)
    private static NbtCompound Biomes(string[] palette, Func<int, int, int, int>? cell = null)
    {
        var c = new NbtCompound { ["palette"] = Names(palette) };
        if (palette.Length > 1)
        {
            int bits = Math.Max(1, (int)Math.Ceiling(Math.Log2(palette.Length)));
            int perLong = 64 / bits;
            var data = new long[(64 + perLong - 1) / perLong];
            for (int y = 0; y < 4; y++)
                for (int z = 0; z < 4; z++)
                    for (int x = 0; x < 4; x++)
                    {
                        int index = (y * 4 + z) * 4 + x;
                        data[index / perLong] |= (long)cell!(x, y, z) << ((index % perLong) * bits);
                    }
            c["data"] = data;
        }
        return c;
    }

    private static NbtCompound Section(sbyte y, NbtCompound biomes, string block = "minecraft:stone")
    {
        var list = new NbtList(NbtTagType.Compound, 1);
        list.Add(new NbtCompound { ["Name"] = block });
        return new NbtCompound { ["Y"] = (byte)y, ["block_states"] = new NbtCompound { ["palette"] = list }, ["biomes"] = biomes };
    }

    private static NbtCompound Chunk(params NbtCompound[] sections)
    {
        var list = new NbtList(NbtTagType.Compound, sections.Length);
        foreach (var s in sections) list.Add(s);
        return new NbtCompound { ["sections"] = list };
    }

    [Test]
    public void AOneBiomeSection_IsThatBiomeEverywhere()
    {
        var cells = RegionReader.BiomeCells(Section(0, Biomes(["minecraft:desert"])))!;
        Assert.That(cells, Is.EqualTo(Enumerable.Repeat(BiomeData.IdOf("minecraft:desert"), 16).ToArray()));
    }

    [Test]
    public void ASectionWithSeveralBiomes_GivesEachCellItsOwn_AtTheSurfaceLayer_ByXAndZ()
    {
        string[] palette = ["minecraft:plains", "minecraft:swamp", "minecraft:river", "minecraft:beach", "minecraft:forest"];   // 5 -> 3 bits
        // Layer 1 (the layer read): swamp where x is 2 or 3, river where z is 3, else plains; every other layer is forest, so a wrong layer shows
        var section = Section(0, Biomes(palette, (x, y, z) => y != 1 ? 4 : z == 3 ? 2 : x >= 2 ? 1 : 0));
        var cells = RegionReader.BiomeCells(section)!;
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                string expect = j == 3 ? "minecraft:river" : i >= 2 ? "minecraft:swamp" : "minecraft:plains";
                Assert.That(cells[j * 4 + i], Is.EqualTo(BiomeData.IdOf(expect)), $"cell x{i} z{j}");
            }
    }

    [Test]
    public void ABiomeTheTableDoesNotKnow_IsUnknown_AndASectionWithoutBiomes_IsNone()
    {
        var cells = RegionReader.BiomeCells(Section(0, Biomes(["mymod:glowing_glade"])))!;
        Assert.That(cells.Distinct(), Is.EqualTo(new[] { BiomeData.Unknown }));
        var bare = new NbtCompound { ["Y"] = (byte)0 };
        Assert.That(RegionReader.BiomeCells(bare), Is.Null);
    }

    [Test]
    public void TheColumnsBiomes_AreThoseOfItsHighestSectionWithBlocks()
    {
        var low = Section(0, Biomes(["minecraft:swamp"]));
        var high = Section(3, Biomes(["minecraft:desert"]));
        var airAbove = Section(8, Biomes(["minecraft:jungle"]), "minecraft:air");
        var chunk = RegionReader.ParseChunkNbt(Chunk(low, high, airAbove));
        Assert.That(chunk.Biomes, Is.Not.Null);
        Assert.That(chunk.Biomes!.Distinct(), Is.EqualTo(new[] { BiomeData.IdOf("minecraft:desert") }), "the empty section above does not count, the lower ones do not either");
        // Order of the sections in the file does not matter
        var reversed = RegionReader.ParseChunkNbt(Chunk(airAbove, high, low));
        Assert.That(reversed.Biomes, Is.EqualTo(chunk.Biomes));
    }

    [Test]
    public void AChunkWithNoBiomeData_HasNone()
    {
        var list = new NbtList(NbtTagType.Compound, 1);
        list.Add(new NbtCompound { ["Name"] = "minecraft:stone" });
        var section = new NbtCompound { ["Y"] = (byte)0, ["block_states"] = new NbtCompound { ["palette"] = list } };
        Assert.That(RegionReader.ParseChunkNbt(Chunk(section)).Biomes, Is.Null);
    }

    [Test]
    public void TheFarFrame_CarriesTheBiomes_AndPlainsWhenThereAreNone()
    {
        var reg = new GlobalBlockRegistry(TestUtil.Config(("BlockRegistry:FilePath", Path.Combine(TestUtil.NewTempDir(), "block-registry.json"))), TestUtil.Log<GlobalBlockRegistry>());
        var biomes = Enumerable.Range(0, 16).Select(i => (byte)(i * 3)).ToArray();
        var hm = new HeightmapResult(new int[256], new ushort[256], Enumerable.Repeat(-64, 256).ToArray(), new ushort[256], ["minecraft:air"], biomes);
        var frame = HeightmapWire.BuildIdsFrame(0, 0, hm, reg);
        Assert.That(frame.Length, Is.EqualTo(HeightmapWire.FrameBytes));
        Assert.That(HeightmapWire.FrameBytes, Is.EqualTo(12 + 3072 + 16));
        Assert.That(frame.AsSpan(HeightmapWire.BiomeOffset, 16).ToArray(), Is.EqualTo(biomes));
        // The layout the GPU reads is where it was
        Assert.That(HeightmapWire.DataOffset, Is.EqualTo(12));
        var none = HeightmapWire.BuildIdsFrame(0, 0, hm with { Biomes = null }, reg);
        Assert.That(none.AsSpan(HeightmapWire.BiomeOffset, 16).ToArray(), Is.EqualTo(Enumerable.Repeat(BiomeData.Plains, 16).ToArray()));
    }
}
