using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>A tool: prints the blocks of a box of the scene world (AUBS_SCENE_BOX = "x0,y0,z0,x1,y1,z1"), through the namer, to see what the server really saved.</summary>
public class SceneInspectTests
{
    [Test]
    public void PrintTheBox()
    {
        var box = Environment.GetEnvironmentVariable("AUBS_SCENE_BOX");
        if (box == null) Assert.Ignore("AUBS_SCENE_BOX not set");
        var v = box!.Split(',').Select(int.Parse).ToArray();
        var saved = BlockStateNamer.Current;
        BlockStateNamer.Current = BlockStateNamer.Load(Path.Combine(AppContext.BaseDirectory, "ServerData", "mc-state-props.json"));
        try
        {
            var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
            var chunks = new Dictionary<(int, int), ChunkResult?>();
            for (int x = v[0]; x <= v[3]; x++)
                for (int z = v[2]; z <= v[5]; z++)
                    for (int y = v[1]; y <= v[4]; y++)
                    {
                        int cx = x >> 4, cz = z >> 4;
                        if (!chunks.TryGetValue((cx, cz), out var chunk))
                        {
                            var file = Path.Combine(world, "world", "region", $"r.{cx >> 5}.{cz >> 5}.mca");
                            chunks[(cx, cz)] = chunk = File.Exists(file) ? RegionReader.ReadChunk(file, cx & 31, cz & 31) : null;
                        }
                        if (chunk == null) continue;
                        var name = chunk.Palette[chunk.Blocks[(x & 15) + (z & 15) * 16 + (y + 64) * 256]];
                        if (name != "minecraft:air") TestContext.Progress.WriteLine($"[box] {x},{y},{z} {name}");
                    }
        }
        finally { BlockStateNamer.Current = saved; }
    }
}
