using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The global block-id space: stable, append-only, persisted, and exact when a chunk is translated into it.</summary>
public class GlobalBlockRegistryTests
{
    private static GlobalBlockRegistry New(string dir) =>
        new(TestUtil.Config(("BlockRegistry:FilePath", Path.Combine(dir, "block-registry.json"))), TestUtil.Log<GlobalBlockRegistry>());

    [Test]
    public void AirIsIdZero_AndNamesGetTheNextIdOnce()
    {
        var reg = New(TestUtil.NewTempDir());
        Assert.That(reg.GetOrAdd("minecraft:air"), Is.EqualTo((ushort)0));
        var stone = reg.GetOrAdd("minecraft:stone");
        var dirt = reg.GetOrAdd("minecraft:dirt");
        Assert.That(new[] { stone, dirt }, Is.EqualTo(new ushort[] { 1, 2 }));
        Assert.That(reg.GetOrAdd("minecraft:stone"), Is.EqualTo(stone), "a name keeps its id");
        Assert.That(reg.Snapshot(), Is.EqualTo(new[] { "minecraft:air", "minecraft:stone", "minecraft:dirt" }));
        Assert.That(reg.Count, Is.EqualTo(3));
    }

    [Test]
    public void IdsSurviveARestart_AndNewNamesAppend()
    {
        var dir = TestUtil.NewTempDir();
        var first = New(dir);
        first.GetOrAdd("minecraft:stone");
        first.GetOrAdd("minecraft:dirt");

        var second = New(dir);
        Assert.That(second.GetOrAdd("minecraft:dirt"), Is.EqualTo((ushort)2), "the id a client cached still means dirt");
        Assert.That(second.GetOrAdd("minecraft:sand"), Is.EqualTo((ushort)3));
        Assert.That(New(dir).Snapshot(), Is.EqualTo(new[] { "minecraft:air", "minecraft:stone", "minecraft:dirt", "minecraft:sand" }));
    }

    [Test]
    public void ChunkIdsAreTranslatedThroughTheChunksOwnPalette()
    {
        var reg = New(TestUtil.NewTempDir());
        reg.GetOrAdd("minecraft:granite"); // global 1: so a chunk's palette index never equals its global id by accident

        // Chunk A palette: air, dirt, stone. Chunk B palette: air, stone, dirt (the same names, other indices).
        var a = new ChunkResult(new ushort[] { 0, 1, 2, 2, 1 }, ["minecraft:air", "minecraft:dirt", "minecraft:stone"]);
        var b = new ChunkResult(new ushort[] { 0, 1, 2, 2, 1 }, ["minecraft:air", "minecraft:stone", "minecraft:dirt"]);
        var ga = reg.ToGlobalIds(a);
        var gb = reg.ToGlobalIds(b);

        var names = reg.Snapshot();
        Assert.That(ga.Select(i => names[i]), Is.EqualTo(new[] { "minecraft:air", "minecraft:dirt", "minecraft:stone", "minecraft:stone", "minecraft:dirt" }));
        Assert.That(gb.Select(i => names[i]), Is.EqualTo(new[] { "minecraft:air", "minecraft:stone", "minecraft:dirt", "minecraft:dirt", "minecraft:stone" }));
        Assert.That(a.Blocks, Is.EqualTo(new ushort[] { 0, 1, 2, 2, 1 }), "the cached chunk is not modified");
    }

    [Test]
    public void ParallelRegistration_NeverHandsOutTheSameIdTwice()
    {
        var reg = New(TestUtil.NewTempDir());
        var names = Enumerable.Range(0, 300).Select(i => $"minecraft:block_{i}").ToArray();
        var ids = new ushort[names.Length * 4];
        Parallel.For(0, ids.Length, i => ids[i] = reg.GetOrAdd(names[i % names.Length]));

        for (int i = 0; i < ids.Length; i++)
            Assert.That(ids[i], Is.EqualTo(ids[i % names.Length]), "one name, one id");
        Assert.That(ids.Distinct().Count(), Is.EqualTo(names.Length));
        Assert.That(reg.Count, Is.EqualTo(names.Length + 1));
    }

    [Test]
    public void FullRegistry_RefusesRatherThanReusingAnId()
    {
        var reg = New(TestUtil.NewTempDir());
        for (int i = 1; i < GlobalBlockRegistry.MaxBlocks; i++) reg.GetOrAdd($"x:b{i}");
        Assert.That(reg.Count, Is.EqualTo(GlobalBlockRegistry.MaxBlocks));
        Assert.Throws<InvalidOperationException>(() => reg.GetOrAdd("x:one_too_many"));
        Assert.That(reg.GetOrAdd("x:b7"), Is.EqualTo((ushort)7), "known names still resolve");
    }

    [Test]
    public void AFailedSave_NeverFailsTheRegistration_AndTheNextNewNamePersistsEverything()
    {
        // The folder does not exist: every save fails (as it would while a scanner holds the file, or on a full disk)
        var dir = Path.Combine(TestUtil.NewTempDir(), "not-yet");
        var reg = new GlobalBlockRegistry(TestUtil.Config(("BlockRegistry:FilePath", Path.Combine(dir, "block-registry.json"))),
            TestUtil.Log<GlobalBlockRegistry>());

        ushort stone = 0;
        Assert.DoesNotThrow(() => stone = reg.GetOrAdd("minecraft:stone"), "an id is handed out even when it cannot be saved yet");
        Assert.That(stone, Is.EqualTo((ushort)1));
        Assert.That(reg.HasUnsavedNames, Is.True);
        Assert.That(reg.GetOrAdd("minecraft:stone"), Is.EqualTo((ushort)1), "a known name still resolves");

        Directory.CreateDirectory(dir);
        reg.GetOrAdd("minecraft:dirt"); // a new name saves the whole registry, the earlier unsaved one too
        Assert.That(reg.HasUnsavedNames, Is.False);
        var reloaded = new GlobalBlockRegistry(TestUtil.Config(("BlockRegistry:FilePath", Path.Combine(dir, "block-registry.json"))),
            TestUtil.Log<GlobalBlockRegistry>());
        Assert.That(reloaded.Snapshot(), Is.EqualTo(new[] { "minecraft:air", "minecraft:stone", "minecraft:dirt" }));
    }

    [Test]
    public void CorruptRegistryFile_StopsLoudly()
    {
        var dir = TestUtil.NewTempDir();
        // id 0 must be air: a registry that disagrees would silently turn every cached chunk's air into stone.
        File.WriteAllText(Path.Combine(dir, "block-registry.json"), """["minecraft:stone","minecraft:air"]""");
        Assert.Throws<InvalidDataException>(() => New(dir));
    }
}
