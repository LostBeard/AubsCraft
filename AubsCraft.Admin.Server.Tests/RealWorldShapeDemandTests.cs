using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// A tool: how many block ids would the real world need if the shaped blocks (stairs, slabs, fences, doors, ...) were named by every property that changes their shape?
/// The global registry holds 12-bit ids (2,048 for blocks, the rest for seabed variants), so this decides whether shapes can be states of the ids or need a channel of their own.
/// Skipped when the world is not there.
/// </summary>
public class RealWorldShapeDemandTests
{
    [Test]
    public void CountTheShapedStates_AcrossTheWholeWorld()
    {
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var regions = Path.Combine(world, "world", "region");
        if (!Directory.Exists(regions)) Assert.Ignore("No world at " + regions);

        string[]? PropsFor(string name)
        {
            string n = name.StartsWith("minecraft:") ? name[10..] : name;
            if (n.EndsWith("_stairs")) return ["facing", "half", "shape"];
            if (n.EndsWith("_slab")) return ["type"];
            if (n.EndsWith("_fence_gate")) return ["facing", "open", "in_wall"];
            if (n.EndsWith("_fence")) return ["north", "east", "south", "west"];
            if (n.EndsWith("_wall") && !n.Contains("sign") && !n.Contains("banner") && !n.Contains("torch") && !n.Contains("head")) return ["north", "east", "south", "west", "up"];
            if (n.EndsWith("_pane") || n == "iron_bars") return ["north", "east", "south", "west"];
            if (n.EndsWith("_door")) return ["facing", "half", "hinge", "open"];
            if (n.EndsWith("_trapdoor")) return ["facing", "half", "open"];
            if (n is "ladder" or "wall_torch" or "redstone_wall_torch" or "soul_wall_torch") return ["facing"];
            if (n.EndsWith("_button") || n == "lever") return ["face", "facing"];
            if (n.EndsWith("_bed")) return ["facing", "part"];
            if (n is "chest" or "trapped_chest") return ["facing", "type"];
            if (n is "lantern" or "soul_lantern") return ["hanging"];
            if (n == "chain") return ["axis"];
            if (n.EndsWith("candle")) return ["candles", "lit"];
            if (n.EndsWith("_pressure_plate")) return ["powered"];
            if (n is "anvil" or "chipped_anvil" or "damaged_anvil" or "grindstone" or "lectern" or "campfire" or "soul_campfire" or "bell") return ["facing"];
            return null;
        }

        var saved = BlockStateNamer.Current;
        var table = new Dictionary<string, string[]>();
        var all = new HashSet<string>();
        var keyed = new SortedDictionary<string, HashSet<string>>();
        var plain = new HashSet<string>();
        int chunks = 0;
        try
        {
            // A namer that names every block by the wide property lists above (built as names turn up)
            foreach (var file in Directory.GetFiles(regions, "r.*.mca"))
            {
                foreach (var (lx, lz) in RegionReader.ListChunks(file))
                {
                    ChunkResult? chunk;
                    try
                    {
                        // First pass of this chunk: learn the names, so the namer knows them for the second read
                        BlockStateNamer.Current = new BlockStateNamer(new());
                        chunk = RegionReader.ReadChunk(file, lx, lz);
                        if (chunk == null) continue;
                        foreach (var name in chunk.Palette.Select(BlockStateNamer.NameOf).Distinct())
                            if (!table.ContainsKey(name) && PropsFor(name) is { } p) table[name] = p;
                        BlockStateNamer.Current = new BlockStateNamer(table);
                        chunk = RegionReader.ReadChunk(file, lx, lz);
                    }
                    catch { continue; }
                    if (chunk == null) continue;
                    chunks++;
                    foreach (var key in chunk.Palette)
                    {
                        all.Add(key);
                        var name = BlockStateNamer.NameOf(key);
                        if (PropsFor(name) == null) { plain.Add(key); continue; }
                        string kind = name.StartsWith("minecraft:") ? name[10..] : name;
                        kind = kind.Contains('_') ? kind[(kind.LastIndexOf('_') + 1)..] : kind;
                        if (!keyed.TryGetValue(kind, out var set)) keyed[kind] = set = new();
                        set.Add(key);
                    }
                }
            }
            int shaped = keyed.Values.Sum(s => s.Count);
            TestContext.Progress.WriteLine($"[shapes] {chunks} chunks; {all.Count} distinct keys, of which {shaped} shaped (by kind: {string.Join(", ", keyed.Select(k => $"{k.Key} {k.Value.Count}"))}); {plain.Count} others");
            Assert.That(chunks, Is.GreaterThan(100));
        }
        finally { BlockStateNamer.Current = saved; }
    }
}
