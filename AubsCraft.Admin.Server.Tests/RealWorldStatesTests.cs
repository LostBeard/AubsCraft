using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The real world's block states, read through the namer: how many distinct ids the registry would need. The world is the production one (the M: share, or AUBS_VIEWER_WORLD);
/// the test is skipped when it is not there. A sample of chunks from every region file, not all of them.
/// </summary>
public class RealWorldStatesTests
{
    [Test]
    public void TheRealWorld_FitsTheRegistry_WithItsStates()
    {
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var regions = Path.Combine(world, "world", "region");
        if (!Directory.Exists(regions)) Assert.Ignore("No world at " + regions);
        var table = Path.Combine(AppContext.BaseDirectory, "ServerData", "mc-state-props.json");
        var saved = BlockStateNamer.Current;
        BlockStateNamer.Current = BlockStateNamer.Load(table);
        try
        {
            Assert.That(BlockStateNamer.Current.BlockCount, Is.GreaterThan(100), "the generated table is next to the tests");
            var keys = new HashSet<string>();
            var names = new HashSet<string>();
            int chunks = 0;
            foreach (var file in Directory.GetFiles(regions, "r.*.mca"))
            {
                var present = RegionReader.ListChunks(file);
                foreach (var (lx, lz) in present.Where((_, i) => i % 6 == 0).Take(120))
                {
                    var chunk = RegionReader.ReadChunk(file, lx, lz);
                    if (chunk == null) continue;
                    chunks++;
                    foreach (var key in chunk.Palette) { keys.Add(key); names.Add(BlockStateNamer.NameOf(key)); }
                }
            }
            TestContext.Progress.WriteLine($"[states] {chunks} chunks sampled: {names.Count} block names, {keys.Count} distinct ids with states");
            Assert.That(chunks, Is.GreaterThan(0));
            Assert.That(keys.Count, Is.LessThan(2048), "ids with states must stay below the seabed offset");
            Assert.That(keys.Count(k => k.Contains('[')), Is.GreaterThan(0), "a real world has logs, rails or furnaces in it");
        }
        finally { BlockStateNamer.Current = saved; }
    }
}
