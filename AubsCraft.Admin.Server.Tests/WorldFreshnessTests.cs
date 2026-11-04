using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The panel holds what it has read of a world, and the game goes on writing the files. TJ's report of 2026-11-04: whole columns of blocks missing from the view, with the region files holding every
/// one of them complete. A chunk the game had only begun to generate was read as air and kept as air for as long as the panel ran.
/// </summary>
public class WorldFreshnessTests
{
    private const uint T0 = 1_790_000_000;
    private const string Stone = "minecraft:stone", Gold = "minecraft:gold_block", Air = "minecraft:air";

    private static readonly (sbyte, string)[] StoneFloor = [(-4, Stone)];
    private static readonly (sbyte, string)[] GoldFloor = [(-4, Gold)];
    private static readonly (sbyte, string)[] AllAir = [(-4, Air), (0, Air)];

    private sealed class World
    {
        public readonly string Dir = TestUtil.NewTempDir();
        public readonly WorldDataService Data;
        public readonly List<(int Cx, int Cz, int Border)> Notes = new();

        public World() => Data = new WorldDataService(new ServerDefinition { Id = "t", Name = "t", Path = Dir }, TestUtil.Log<WorldDataService>());

        public string Region(int rx = 0, int rz = 0) => Path.Combine(Dir, "world", "region", $"r.{rx}.{rz}.mca");
        public void Write(params RegionChunk[] chunks) => RegionFileWriter.Write(Region(), chunks);
        public void Scan() => Data.ScanForChanges(batch => Notes.AddRange(batch));
        public string TopBlockOf(int cx, int cz)
        {
            var c = Data.GetChunk(cx, cz);
            return c == null ? "(none)" : c.Palette[c.Blocks[0]];
        }
    }

    [Test]
    public void AGenerationStub_IsNotTerrain_AndBecomesTerrainWhenTheGameFinishesIt()
    {
        var w = new World();
        w.Write(new RegionChunk(1, 1, "minecraft:structure_starts", AllAir, T0));
        w.Scan();
        Assert.That(w.Data.GetChunk(1, 1), Is.Null, "a stub is not served as an empty column");
        Assert.That(w.Data.GetHeightmap(1, 1), Is.Null, "nor does it go down the world stream");

        w.Write(new RegionChunk(1, 1, "minecraft:full", StoneFloor, T0 + 60));
        w.Scan();
        Assert.That(w.TopBlockOf(1, 1), Is.EqualTo(Stone), "the finished chunk is what the panel serves now");
        Assert.That(w.Notes, Is.EqualTo(new[] { (1, 1, 15) }), "and the viewers are told, all sides: its neighbours have walls toward it to lose");
    }

    [Test]
    public void AFullChunkWithNoBlocks_IsStillAChunk()
    {
        var w = new World();
        w.Write(new RegionChunk(1, 1, "minecraft:full", AllAir, T0));
        Assert.That(w.Data.GetChunk(1, 1), Is.Not.Null, "a void world's finished chunk is really empty");
    }

    [Test]
    public void AChunkThePanelHolds_IsReadAgainWhenTheFileChanges()
    {
        var w = new World();
        w.Write(new RegionChunk(2, 3, "minecraft:full", StoneFloor, T0));
        w.Scan();
        Assert.That(w.TopBlockOf(2, 3), Is.EqualTo(Stone));
        w.Write(new RegionChunk(2, 3, "minecraft:full", GoldFloor, T0 + 60));
        w.Scan();
        Assert.That(w.TopBlockOf(2, 3), Is.EqualTo(Gold));
        Assert.That(w.Notes, Is.EqualTo(new[] { (2, 3, 15) }));
    }

    [Test]
    public void AChunkSavedAgainUnchanged_SendsNoViewerAfterIt()
    {
        var w = new World();
        w.Write(new RegionChunk(2, 3, "minecraft:full", StoneFloor, T0));
        w.Scan();
        w.Data.GetChunk(2, 3);
        w.Write(new RegionChunk(2, 3, "minecraft:full", StoneFloor, T0 + 60));
        w.Scan();
        Assert.That(w.Notes, Is.Empty);
    }

    [Test]
    public void ChunksNewToARegion_AndANewRegion_AreReported()
    {
        var w = new World();
        w.Write(new RegionChunk(0, 0, "minecraft:full", StoneFloor, T0));
        w.Scan();
        w.Write(new RegionChunk(0, 0, "minecraft:full", StoneFloor, T0), new RegionChunk(5, 6, "minecraft:full", GoldFloor, T0 + 60));
        RegionFileWriter.Write(w.Region(-1, 2), [new RegionChunk(31, 0, "minecraft:full", GoldFloor, T0 + 60)]);
        w.Scan();
        Assert.That(w.Notes, Is.EquivalentTo(new[] { (5, 6, 15), (-1, 64, 15) }));
        Assert.That(w.TopBlockOf(5, 6), Is.EqualTo(Gold));
        Assert.That(w.TopBlockOf(-1, 64), Is.EqualTo(Gold));
    }

    [Test]
    public void ANewStub_IsNotReported()
    {
        var w = new World();
        w.Write(new RegionChunk(0, 0, "minecraft:full", StoneFloor, T0));
        w.Scan();
        w.Write(new RegionChunk(0, 0, "minecraft:full", StoneFloor, T0), new RegionChunk(5, 6, "minecraft:biomes", AllAir, T0 + 60));
        w.Scan();
        Assert.That(w.Notes, Is.Empty);
    }

    [Test]
    public void AChunkThatCouldNotBeRead_IsTriedAgain_AndReported()
    {
        var w = new World();
        w.Write(new RegionChunk(4, 4, "minecraft:full", StoneFloor, T0, Corrupt: true));
        w.Scan();
        Assert.That(w.Data.GetChunk(4, 4), Is.Null, "caught mid-write");
        // The same place and time in the header: the game finished the write it had begun, so nothing in the header says so
        w.Write(new RegionChunk(4, 4, "minecraft:full", StoneFloor, T0));
        w.Scan();
        Assert.That(w.TopBlockOf(4, 4), Is.EqualTo(Stone));
        Assert.That(w.Notes, Is.EqualTo(new[] { (4, 4, 15) }));
    }

    [Test]
    public void WhatTheRunningServerSent_IsNotOverwrittenByAnOlderFile()
    {
        var w = new World();
        w.Write(new RegionChunk(2, 3, "minecraft:full", StoneFloor, T0));
        w.Scan();
        var live = w.Data.GetChunk(2, 3)! with { Palette = ["minecraft:air", Gold] };
        var blocks = new ushort[live.Blocks.Length];
        Array.Fill(blocks, (ushort)1);
        w.Data.SetLiveChunk(2, 3, live with { Blocks = blocks }, new byte[RegionReader.LightBytes]);

        // The game wrote the chunk an hour before the live update reached the panel
        uint older = (uint)DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        w.Write(new RegionChunk(2, 3, "minecraft:full", StoneFloor, older));
        w.Scan();
        Assert.That(w.TopBlockOf(2, 3), Is.EqualTo(Gold), "the live copy stands");
        Assert.That(w.Data.LiveChunkCount, Is.EqualTo(1));

        // Now the file has caught up
        uint newer = (uint)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        w.Write(new RegionChunk(2, 3, "minecraft:full", StoneFloor, newer));
        w.Scan();
        Assert.That(w.TopBlockOf(2, 3), Is.EqualTo(Stone));
        Assert.That(w.Data.LiveChunkCount, Is.EqualTo(0));
    }

    [Test]
    public void TheCache_StaysWithinItsBound_AndTheChunkInUseStays()
    {
        var w = new World();
        w.Data.MaxCachedChunks = 20;
        var chunks = new List<RegionChunk>();
        for (int z = 0; z < 8; z++)
            for (int x = 0; x < 8; x++)
                chunks.Add(new RegionChunk(x, z, "minecraft:full", StoneFloor, T0));
        w.Write(chunks.ToArray());
        w.Scan();
        for (int z = 0; z < 8; z++)
            for (int x = 0; x < 8; x++)
            {
                Assert.That(w.Data.GetChunk(x, z), Is.Not.Null);
                Assert.That(w.Data.GetChunk(0, 0), Is.Not.Null);   // in use all along
                Assert.That(w.Data.CachedChunkCount, Is.LessThanOrEqualTo(20 + 1));
            }
        // An evicted chunk is read again, whole
        Assert.That(w.TopBlockOf(3, 3), Is.EqualTo(Stone));
    }

    [Test]
    public void AChunkOutOfTheCache_SavedUnchanged_SendsNoViewerAfterIt_AndChangedDoes()
    {
        var w = new World();
        w.Data.MaxCachedChunks = 3;
        var all = new List<RegionChunk>();
        for (int x = 0; x < 8; x++) all.Add(new RegionChunk(x, 0, "minecraft:full", StoneFloor, T0));
        w.Write(all.ToArray());
        w.Scan();
        for (int x = 0; x < 8; x++) w.Data.GetChunk(x, 0);
        Assert.That(w.Data.CachedChunkCount, Is.LessThanOrEqualTo(4), "(0, 0) has left the cache");

        all[0] = new RegionChunk(0, 0, "minecraft:full", StoneFloor, T0 + 60);
        w.Write(all.ToArray());
        w.Scan();
        Assert.That(w.Notes, Is.Empty, "saved again with the same blocks");

        for (int x = 1; x < 8; x++) w.Data.GetChunk(x, 0);   // (0, 0) out again
        all[0] = new RegionChunk(0, 0, "minecraft:full", GoldFloor, T0 + 120);
        w.Write(all.ToArray());
        w.Scan();
        Assert.That(w.Notes, Is.EqualTo(new[] { (0, 0, 15) }), "changed: which sides is not known any more, so all");
    }

    [Test]
    public void AHeightmap_FollowsTheChunk()
    {
        var w = new World();
        w.Write(new RegionChunk(2, 3, "minecraft:full", StoneFloor, T0));
        w.Scan();
        var first = w.Data.GetHeightmap(2, 3)!;
        Assert.That(w.Data.GetHeightmap(2, 3), Is.SameAs(first), "kept");
        Assert.That(first.Palette[first.BlockIds[0]], Is.EqualTo(Stone));
        w.Write(new RegionChunk(2, 3, "minecraft:full", GoldFloor, T0 + 60));
        w.Scan();
        var second = w.Data.GetHeightmap(2, 3)!;
        Assert.That(second.Palette[second.BlockIds[0]], Is.EqualTo(Gold), "and new when the chunk is");
    }

    [Test]
    public void ASign_IsTheSameSignThoughItsLinesAreAnotherArray()
    {
        ushort[] Blocks() => new ushort[16 * 16 * 384];
        ChunkResult With(string line) => new(Blocks(), ["minecraft:air"], SignTexts: [new AubsCraft.Live.Model.BlockEntities.SignText(1, 2, 3, [line, "", "", ""], 0, false)]);
        Assert.That(ChunkDiff.BorderOfChange(With("hello"), With("hello")), Is.EqualTo(-1));
        Assert.That(ChunkDiff.BorderOfChange(With("hello"), With("bye")), Is.EqualTo(0), "new words: its inside changed");
        Assert.That(ChunkDiff.ContentHash(With("hello")), Is.EqualTo(ChunkDiff.ContentHash(With("hello"))));
        Assert.That(ChunkDiff.ContentHash(With("hello")), Is.Not.EqualTo(ChunkDiff.ContentHash(With("bye"))));
    }

    [Test]
    public void ChunkDiff_NamesTheSidesThatChanged_ThroughDifferentPalettes()
    {
        const int W = 16;
        ushort[] Floor(ushort id)
        {
            var b = new ushort[W * W * 384];
            for (int i = 0; i < W * W; i++) b[i] = id;
            return b;
        }
        var a = new ChunkResult(Floor(1), ["minecraft:air", Stone]);
        var sameOtherPalette = new ChunkResult(Floor(2), ["minecraft:air", Gold, Stone]);
        Assert.That(ChunkDiff.BorderOfChange(a, sameOtherPalette), Is.EqualTo(-1), "the same blocks under other palette numbers");

        var inside = Floor(1); inside[5 + 5 * W + 3 * W * W] = 1;
        Assert.That(ChunkDiff.BorderOfChange(a, new ChunkResult(inside, ["minecraft:air", Stone])), Is.EqualTo(0));
        var east = Floor(1); east[15 + 7 * W + 3 * W * W] = 1;
        Assert.That(ChunkDiff.BorderOfChange(a, new ChunkResult(east, ["minecraft:air", Stone])), Is.EqualTo(2));
        var north = Floor(1); north[7 + 0 * W + 3 * W * W] = 1;
        Assert.That(ChunkDiff.BorderOfChange(a, new ChunkResult(north, ["minecraft:air", Stone])), Is.EqualTo(4));
    }

    [Test]
    public void ANoteThatChunksChanged_IsNotLostToAViewerThatFellBehind()
    {
        var live = new LiveFeedService(TestUtil.Config(("Live:Secret", "s")), TestUtil.Log<LiveFeedService>());
        using var sub = live.Subscribe("t");
        var note = LiveChunkReader.ChangeMessage([(7, 8, 15)]);
        live.Broadcast("t", note);
        var frame = AubsCraft.Live.Model.LiveFrameCodec.Write(new AubsCraft.Live.Model.LiveFrame { FullTime = 1, Worlds = [] });
        for (int i = 0; i < 200; i++) Assert.That(live.Ingest("t", frame), Is.Null);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = sub.ReadAllAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Assert.That(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult(), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(note), "first, ahead of the frames, though 200 frames came after it");
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
