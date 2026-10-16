using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Biomes;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// Which biomes the real world has and where (a chunk of each, spread apart), read through the same parser the viewer's server uses, written to AUBS_BIOMES_OUT (default a file in the temp folder)
/// for the viewer gate to shoot. Skipped when the world is not there. Not an assertion about the world: a tool the gate uses.
/// </summary>
public class RealWorldBiomesTests
{
    [Test]
    public void ListTheBiomesOfTheRealWorld()
    {
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var regions = Path.Combine(world, "world", "region");
        if (!Directory.Exists(regions)) Assert.Ignore("No world at " + regions);
        var counts = new Dictionary<byte, int>();
        var where = new Dictionary<byte, List<(int cx, int cz)>>();
        foreach (var file in Directory.GetFiles(regions, "r.*.mca"))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('.');
            int rx = int.Parse(parts[1]), rz = int.Parse(parts[2]);
            foreach (var (lx, lz) in RegionReader.ListChunks(file))
            {
                ChunkResult? chunk;
                try { chunk = RegionReader.ReadChunk(file, lx, lz); } catch { continue; }
                if (chunk?.Biomes == null) continue;
                foreach (var id in chunk.Biomes.Distinct())
                {
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                    int cx = rx * 32 + lx, cz = rz * 32 + lz;
                    // The chunks where this biome fills the whole chunk (its 16 cells) are the places to shoot; keep a few, apart
                    if (chunk.Biomes.All(b => b == id) && (!where.TryGetValue(id, out var list) || list.Count < 4 && list.All(p => Math.Abs(p.cx - cx) > 6 || Math.Abs(p.cz - cz) > 6)))
                        (where.TryGetValue(id, out var l2) ? l2 : where[id] = new List<(int, int)>()).Add((cx, cz));
                }
            }
        }
        var lines = counts.OrderByDescending(c => c.Value).Select(c =>
            $"{(c.Key == BiomeData.Unknown ? "unknown" : BiomeData.All[c.Key].Name)}|{c.Value}|{string.Join(';', where.GetValueOrDefault(c.Key)?.Select(p => $"{p.cx},{p.cz}") ?? Enumerable.Empty<string>())}").ToList();
        foreach (var l in lines) TestContext.Progress.WriteLine("[biomes] " + l);
        File.WriteAllLines(Environment.GetEnvironmentVariable("AUBS_BIOMES_OUT") ?? Path.Combine(Path.GetTempPath(), "aubs-biomes.txt"), lines);
        Assert.That(counts, Is.Not.Empty, "the world has biome data");
    }
}
