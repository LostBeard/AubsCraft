using System.Text;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.BlockEntities;
using Microsoft.Extensions.Logging.Abstractions;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>A chunk the running server posted (FORMAT.md, "chunk update"): read into the same chunk the region reader makes, served instead of the file's, and announced to the viewers.</summary>
public class LiveChunkTests
{
    // The plugin's encoder, written out again: the same layout, so these tests say what the bytes mean
    private sealed class Body
    {
        private readonly MemoryStream _ms = new();
        public Body U8(int v) { _ms.WriteByte((byte)v); return this; }
        public Body U16(int v) { _ms.WriteByte((byte)v); _ms.WriteByte((byte)(v >> 8)); return this; }
        public Body Str(string s) { var b = Encoding.UTF8.GetBytes(s); U16(b.Length); _ms.Write(b); return this; }
        public Body Raw(byte[] b) { _ms.Write(b); return this; }
        public byte[] ToArray() => _ms.ToArray();
    }

    // top layers, with the palette's blocks at the given (index, palette id) places; everything else is palette 0
    private static byte[] Chunk(string[] palette, int top, (int x, int y, int z, int id)[] placed, int border = 0, byte? lightAt = null,
        Action<Body>? signs = null, Action<Body>? banners = null, int signCount = 0, int bannerCount = 0)
    {
        var b = new Body().U8(1).U8(border).U16(top).U16(palette.Length);
        foreach (var p in palette) b.Str(p);
        var ids = new int[256 * top];
        foreach (var (x, y, z, id) in placed) ids[x + z * 16 + y * 256] = id;
        foreach (var id in ids) b.U16(id);
        var light = new byte[256 * top];
        System.Array.Fill(light, lightAt ?? (byte)0xF0);
        b.Raw(light);
        for (int i = 0; i < 256; i++) b.U16(70);    // the rain stops at 70 above the bottom: y 6
        b.U16(signCount); signs?.Invoke(b);
        b.U16(bannerCount); banners?.Invoke(b);
        return b.ToArray();
    }

    [Test]
    public void TheBlocksAreTheChunksBlocks_InTheOrderTheViewerReads()
    {
        var body = Chunk(["minecraft:air", "minecraft:stone", "minecraft:oak_planks"], 70,
            [(1, 3, 2, 1), (15, 69, 0, 2)]);
        var parsed = LiveChunkReader.Parse(body, null);
        var chunk = parsed.Chunk;
        Assert.That(chunk.Blocks.Length, Is.EqualTo(98304));
        Assert.That(chunk.Palette[chunk.Blocks[1 + 2 * 16 + 3 * 256]], Is.EqualTo("minecraft:stone"));
        Assert.That(chunk.Palette[chunk.Blocks[15 + 0 * 16 + 69 * 256]], Is.EqualTo("minecraft:oak_planks"));
        Assert.That(chunk.Blocks[0], Is.EqualTo(0), "air is id 0 of the chunk's palette, as in a chunk read from the file");
        Assert.That(chunk.Palette[0], Is.EqualTo("minecraft:air"));
        Assert.That(chunk.Blocks.Skip(256 * 70).All(b => b == 0), Is.True, "above what was sent is air");
        Assert.That(chunk.RainHeights![17], Is.EqualTo(70));
    }

    [Test]
    public void TheBlockDataStrings_BecomeTheViewersNames()
    {
        // With the table of which properties matter, a log's axis is part of its name and the rest is dropped
        var before = BlockStateNamer.Current;
        BlockStateNamer.Current = new BlockStateNamer(new() { ["minecraft:oak_log"] = ["axis"] });
        try
        {
            var body = Chunk(["minecraft:air", "minecraft:oak_log[axis=x]", "minecraft:oak_log[axis=y]", "minecraft:water[level=0]"], 70,
                [(0, 0, 0, 1), (1, 0, 0, 2), (2, 0, 0, 3)]);
            var chunk = LiveChunkReader.Parse(body, null).Chunk;
            Assert.That(chunk.Palette[chunk.Blocks[0]], Is.EqualTo("minecraft:oak_log[axis=x]"));
            Assert.That(chunk.Palette[chunk.Blocks[1]], Is.EqualTo("minecraft:oak_log[axis=y]"));
            Assert.That(chunk.Palette[chunk.Blocks[2]], Is.EqualTo("minecraft:water"), "a block with no property the picture needs is its name");
        }
        finally { BlockStateNamer.Current = before; }
    }

    [Test]
    public void ABlockDataString_IsSplitIntoItsNameAndProperties()
    {
        var (name, props) = LiveChunkReader.ParseBlockData("minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=false]");
        Assert.That(name, Is.EqualTo("minecraft:oak_stairs"));
        Assert.That(props!["facing"], Is.EqualTo("north"));
        Assert.That(props["half"], Is.EqualTo("bottom"));
        Assert.That(props.Count, Is.EqualTo(4));
        Assert.That(LiveChunkReader.ParseBlockData("minecraft:stone"), Is.EqualTo(("minecraft:stone", (Dictionary<string, object>?)null)));
    }

    [Test]
    public void TheLightIsKept_AndAboveWhatWasSentIsOpenSky()
    {
        var parsed = LiveChunkReader.Parse(Chunk(["minecraft:air", "minecraft:stone"], 40, [(0, 0, 0, 1)], lightAt: 0x3C), null);
        Assert.That(parsed.Light.Length, Is.EqualTo(98304));
        Assert.That(parsed.Light[5 + 5 * 256], Is.EqualTo(0x3C));
        Assert.That(parsed.Light[256 * 40 - 1], Is.EqualTo(0x3C));
        Assert.That(parsed.Light[256 * 40], Is.EqualTo(0xF0), "open sky above");
        Assert.That(parsed.Light[98303], Is.EqualTo(0xF0));
    }

    [Test]
    public void ABedAndAChest_AreBlockEntities_WhereTheyStand()
    {
        var body = Chunk(["minecraft:air", "minecraft:chest[facing=north,type=single,waterlogged=false]", "minecraft:red_bed[facing=east,part=head,occupied=false]"], 80,
            [(4, 70, 5, 1), (9, 71, 12, 2)]);
        var spots = LiveChunkReader.Parse(body, null).Chunk.BlockEntities!;
        Assert.That(spots.Count, Is.EqualTo(2));
        var chest = spots.Single(s => s.Kind == BlockEntityKind.Chest);
        Assert.That((chest.X, chest.Y, chest.Z), Is.EqualTo((4, 70 - 64, 5)), "the world's y, as the file reader gives it");
        Assert.That(chest.Orient, Is.EqualTo(8), "facing north");
        var bed = spots.Single(s => s.Kind == BlockEntityKind.Bed);
        Assert.That((bed.X, bed.Y, bed.Z), Is.EqualTo((9, 71 - 64, 12)));
        Assert.That(bed.Part, Is.EqualTo(1), "the head");
    }

    [Test]
    public void SignTextsAndBannerPatterns_ComeThrough()
    {
        var body = Chunk(["minecraft:air"], 10, [],
            signs: b => b.U8(3).U16(unchecked((ushort)-5)).U8(7).Str("Hello").Str("world").Str("").Str("").Str("red").U8(1),
            banners: b => b.U8(2).U16(65).U8(2).U8(2).Str("stripe_top").Str("blue").Str("creeper").Str("white"),
            signCount: 1, bannerCount: 1);
        var chunk = LiveChunkReader.Parse(body, null).Chunk;
        var sign = chunk.SignTexts!.Single();
        Assert.That((sign.X, sign.Y, sign.Z), Is.EqualTo((3, -5, 7)), "y is signed");
        Assert.That(sign.Lines, Is.EqualTo(new[] { "Hello", "world", "", "" }));
        Assert.That(sign.Color, Is.EqualTo((byte)Array.IndexOf(BlockEntityKinds.Dyes, "red")));
        Assert.That(sign.Glowing, Is.True);
        var banner = chunk.Banners!.Single();
        Assert.That((banner.X, banner.Y, banner.Z), Is.EqualTo((2, 65, 2)));
        Assert.That(banner.Layers.Length, Is.EqualTo(2));
        Assert.That(banner.Layers[0], Is.EqualTo(((byte)BannerPatterns.IndexOf("stripe_top"), (byte)Array.IndexOf(BlockEntityKinds.Dyes, "blue"))));
    }

    [Test]
    public void ABlankSign_IsLeftOut()
    {
        var body = Chunk(["minecraft:air"], 10, [], signs: b => b.U8(1).U16(2).U8(3).Str("").Str("  ").Str("").Str("").Str("black").U8(0), signCount: 1);
        Assert.That(LiveChunkReader.Parse(body, null).Chunk.SignTexts, Is.Null);
    }

    [Test]
    public void TheBiomesComeFromTheChunkThatWasThere()
    {
        var before = new ChunkResult(new ushort[98304], ["minecraft:air"], Biomes: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        var chunk = LiveChunkReader.Parse(Chunk(["minecraft:air"], 4, []), before).Chunk;
        Assert.That(chunk.Biomes, Is.EqualTo(before.Biomes));
    }

    [Test]
    public void TheBorderMaskIsKept()
    {
        Assert.That(LiveChunkReader.Parse(Chunk(["minecraft:air"], 4, [], border: 5), null).BorderMask, Is.EqualTo(5));
    }

    [Test]
    public void ThingsThatAreNotAChunkUpdate_AreRefused()
    {
        var good = Chunk(["minecraft:air", "minecraft:stone"], 8, [(0, 0, 0, 1)]);
        Assert.DoesNotThrow(() => LiveChunkReader.Parse(good, null));
        Assert.Throws<InvalidDataException>(() => LiveChunkReader.Parse([], null), "empty");
        Assert.Throws<InvalidDataException>(() => LiveChunkReader.Parse(good[..100], null), "cut short");
        var wrongVersion = (byte[])good.Clone(); wrongVersion[0] = 9;
        Assert.Throws<InvalidDataException>(() => LiveChunkReader.Parse(wrongVersion, null), "version");
        Assert.Throws<InvalidDataException>(() => LiveChunkReader.Parse(Chunk(["minecraft:air"], 8, [(0, 0, 0, 5)]), null), "a palette index beyond the palette");
        var tall = new Body().U8(1).U8(0).U16(400).U16(1).Str("minecraft:air").ToArray();
        Assert.Throws<InvalidDataException>(() => LiveChunkReader.Parse(tall, null), "more layers than a world has");
    }

    [Test]
    public void TheChangeMessage_NamesTheChunksAndTheirBorders()
    {
        var m = LiveChunkReader.ChangeMessage([(3, -7, 5), (-100000, 100000, 0)]);
        Assert.That(Encoding.ASCII.GetString(m, 0, 4), Is.EqualTo("ACHG"));
        Assert.That(BitConverter.ToUInt16(m, 4), Is.EqualTo(2));
        Assert.That((BitConverter.ToInt32(m, 6), BitConverter.ToInt32(m, 10), m[14]), Is.EqualTo((3, -7, (byte)5)));
        Assert.That((BitConverter.ToInt32(m, 15), BitConverter.ToInt32(m, 19), m[23]), Is.EqualTo((-100000, 100000, (byte)0)));
        Assert.That(m.Length, Is.EqualTo(24));
    }

    [Test]
    public void ALiveChunk_IsServedInsteadOfTheFile_AndTheOldestGoBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aubs-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var world = new WorldDataService(new ServerDefinition { Id = "t", Path = folder }, NullLogger<WorldDataService>.Instance);
            Assert.That(world.GetChunk(1, 1), Is.Null, "no file, no chunk");
            var parsed = LiveChunkReader.Parse(Chunk(["minecraft:air", "minecraft:stone"], 8, [(0, 0, 0, 1)], lightAt: 0x55), null);
            world.SetLiveChunk(1, 1, parsed.Chunk, parsed.Light);
            Assert.That(world.GetChunk(1, 1), Is.SameAs(parsed.Chunk));
            Assert.That(world.GetChunkLight(1, 1)![0], Is.EqualTo(0x55));

            // Past the limit the oldest go; the newest stay
            for (int i = 0; i < WorldDataService.MaxLiveChunks + 10; i++)
            {
                world.SetLiveChunk(100 + i, 0, parsed.Chunk, parsed.Light);
                Thread.Sleep(0);
            }
            Assert.That(world.LiveChunkCount, Is.EqualTo(WorldDataService.MaxLiveChunks));
            Assert.That(world.GetChunk(1, 1), Is.Null, "the oldest went back to the (missing) file");
            Assert.That(world.GetChunk(100 + WorldDataService.MaxLiveChunks + 9, 0), Is.Not.Null, "the newest is there");
        }
        finally { Directory.Delete(folder, true); }
    }
}
