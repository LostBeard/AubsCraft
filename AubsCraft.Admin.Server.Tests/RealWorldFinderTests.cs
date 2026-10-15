using System.Text.Json;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// Finds where the real world has blocks of given states, by reading EVERY chunk of every region file through the namer, and writes the first few places of each kind to
/// AUBS_FINDER_OUT (default: a file in the temp folder) for the viewer gate to shoot. Skipped when the world is not there. Not an assertion about the world, a tool the gate uses.
/// </summary>
public class RealWorldFinderTests
{
    [Test]
    public void FindTheStatefulBlocks_AcrossTheWholeWorld()
    {
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var regions = Path.Combine(world, "world", "region");
        if (!Directory.Exists(regions)) Assert.Ignore("No world at " + regions);
        var saved = BlockStateNamer.Current;
        BlockStateNamer.Current = BlockStateNamer.Load(Path.Combine(AppContext.BaseDirectory, "ServerData", "mc-state-props.json"));
        try
        {
            var kinds = new (string label, Func<string, bool> wanted)[]
            {
                ("rail-curve", n => n.Contains("rail[") && (n.Contains("=south_east") || n.Contains("=south_west") || n.Contains("=north_east") || n.Contains("=north_west"))),
                ("rail-slope", n => n.Contains("rail[") && n.Contains("ascending")),
                ("rail-straight", n => n.Contains("rail[") && (n.Contains("=north_south") || n.Contains("=east_west"))),
                ("log-lying", n => (n.Contains("_log[") || n.Contains("_wood[") || n.Contains("_stem[") || n.Contains("_hyphae[")) && (n.Contains("axis=x") || n.Contains("axis=z"))),
                ("furnace-lit", n => (n.Contains("furnace[") || n.Contains("smoker[")) && n.Contains("lit=true")),
                ("furnace", n => n.Contains("furnace[") || n.Contains("smoker[")),
                ("dispenser", n => n.Contains("dispenser[") || n.Contains("dropper[") || n.Contains("observer[")),
                ("pumpkin", n => n.Contains("pumpkin[") || n.Contains("jack_o_lantern[")),
                ("glazed", n => n.Contains("glazed_terracotta[")),
                ("pillar", n => n.Contains("quartz_pillar[") || n.Contains("purpur_pillar[") || n.Contains("hay_block[") || n.Contains("bone_block[")),
                ("crop-young", n => (n.StartsWith("minecraft:wheat[") || n.StartsWith("minecraft:carrots[") || n.StartsWith("minecraft:potatoes[")) && !n.EndsWith("age=7]")),
                ("snowy-grass", n => n.Contains("grass_block[") && n.Contains("snowy=true")),
            };
            var found = kinds.ToDictionary(k => k.label, _ => new List<(int x, int y, int z, string name)>());
            int chunks = 0;
            foreach (var file in Directory.GetFiles(regions, "r.*.mca"))
            {
                var parts = Path.GetFileNameWithoutExtension(file).Split('.');
                int rx = int.Parse(parts[1]), rz = int.Parse(parts[2]);
                foreach (var (lx, lz) in RegionReader.ListChunks(file))
                {
                    ChunkResult? chunk;
                    try { chunk = RegionReader.ReadChunk(file, lx, lz); } catch { continue; }
                    if (chunk == null) continue;
                    chunks++;
                    foreach (var (label, wanted) in kinds)
                    {
                        if (found[label].Count >= 6) continue;
                        var ids = new HashSet<int>(Enumerable.Range(0, chunk.Palette.Count).Where(i => wanted(chunk.Palette[i])));
                        if (ids.Count == 0) continue;
                        for (int i = 0; i < chunk.Blocks.Length && found[label].Count < 6; i++)
                        {
                            if (!ids.Contains(chunk.Blocks[i])) continue;
                            int x = i % 16, z = (i / 16) % 16, y = i / 256 - 64;
                            int wx = (rx * 32 + lx) * 16 + x, wz = (rz * 32 + lz) * 16 + z;
                            // Spread them out: not within 24 blocks of one already found
                            if (found[label].Any(f => Math.Abs(f.x - wx) < 24 && Math.Abs(f.z - wz) < 24)) continue;
                            found[label].Add((wx, y, wz, chunk.Palette[chunk.Blocks[i]]));
                        }
                    }
                }
            }
            var outFile = Environment.GetEnvironmentVariable("AUBS_FINDER_OUT") ?? Path.Combine(Path.GetTempPath(), "aubs-states-locations.json");
            File.WriteAllText(outFile, JsonSerializer.Serialize(found.ToDictionary(k => k.Key, k => k.Value.Select(v => new { v.x, v.y, v.z, v.name }).ToList()), new JsonSerializerOptions { WriteIndented = true }));
            TestContext.Progress.WriteLine($"[finder] {chunks} chunks read -> {outFile}: " + string.Join(", ", found.Select(f => $"{f.Key} {f.Value.Count}")));
            Assert.That(chunks, Is.GreaterThan(100));
        }
        finally { BlockStateNamer.Current = saved; }
    }
}
