using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// Where the map opens on a server's world: level.dat spawn, else BlueMap's start position, else the saved
/// chunk nearest the origin. Uses a REAL Paper world (bravo); the spawn is set with /setworldspawn so the
/// expected answer comes from the server, not from the code under test.
/// </summary>
[NonParallelizable]
public class SpawnTests
{
    private static WorldDataService World(ServerDefinition def) => new(def, TestUtil.Log<WorldDataService>());

    [Test]
    public async Task Spawn_ComesFromLevelDat()
    {
        await using (var rcon = await TestServers.RconAsync(TestServers.Bravo))
        {
            var answer = await rcon.SendCommandAsync("setworldspawn 123 90 -456");
            Assert.That(answer, Does.Contain("123").And.Contain("-456"), answer);
            await rcon.SendCommandAsync("save-all flush");
        }

        var spawn = World(TestServers.Bravo.ToDefinition("bravo")).GetSpawn();
        Assert.That((spawn.X, spawn.Z, spawn.Known), Is.EqualTo((123, -456, true)));
    }

    /// <summary>A copy of bravo's server folder with only the world's region files (no level.dat, no BlueMap).</summary>
    private static ServerDefinition CopyOfRegionsOnly()
    {
        var dir = TestUtil.NewTempDir();
        var regions = Path.Combine(dir, "world", "region");
        Directory.CreateDirectory(regions);
        foreach (var f in Directory.GetFiles(Path.Combine(TestServers.Bravo.Dir, TestServers.Bravo.LevelName, "region"), "*.mca"))
        {
            // Shared read: bravo is running and holds its region files open.
            using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = File.Create(Path.Combine(regions, Path.GetFileName(f)));
            src.CopyTo(dst);
        }
        return new ServerDefinition { Id = "copy", Name = "copy", Path = dir };
    }

    [Test]
    public async Task WithoutLevelDat_BlueMapStartPosIsUsed()
    {
        await using (var rcon = await TestServers.RconAsync(TestServers.Bravo))
            await rcon.SendCommandAsync("save-all flush");
        var def = CopyOfRegionsOnly();
        var settings = Path.Combine(def.Path, "bluemap", "web", "maps", "world");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "settings.json"), """{"name":"world","startPos":[-700,321],"skyColor":[0.5,0.7,1,1]}""");

        var spawn = World(def).GetSpawn();
        Assert.That((spawn.X, spawn.Z, spawn.Known), Is.EqualTo((-700, 321, true)));
    }

    [Test]
    public async Task WithNeither_TheSavedChunkNearestTheOriginIsUsed()
    {
        await using (var rcon = await TestServers.RconAsync(TestServers.Bravo))
            await rcon.SendCommandAsync("save-all flush");
        var def = CopyOfRegionsOnly();
        var world = World(def);
        var chunks = world.GetPopulatedChunks();
        Assert.That(chunks, Is.Not.Empty);
        static bool HasBlocks(ChunkResult c) => c.Palette.Any(n => !n.EndsWith("air"));
        // The expected answer: nearest chunk that has blocks (region files also hold empty proto chunks).
        var nearest = chunks.OrderBy(c => (long)c.X * c.X + (long)c.Z * c.Z)
            .First(c => world.GetChunk(c.X, c.Z) is { } ch && HasBlocks(ch));

        var spawn = world.GetSpawn();
        Assert.That((spawn.X, spawn.Z, spawn.Known), Is.EqualTo((nearest.X * 16 + 8, nearest.Z * 16 + 8, true)));
        Assert.That(HasBlocks(world.GetChunk(spawn.X >> 4, spawn.Z >> 4)!), Is.True, "the map opens over terrain");
    }

    [Test]
    public void AnEmptyWorld_IsUnknown()
    {
        var def = new ServerDefinition { Id = "empty", Name = "empty", Path = TestUtil.NewTempDir() };
        Assert.That(World(def).GetSpawn().Known, Is.False);
    }
}
