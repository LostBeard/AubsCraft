using System.Text.Json;
using AubsCraft.Assets;
using AubsCraft.Assets.Model;

// usage: AubsCraft.AssetTool --version 1.21.5 --out <dir> [--jar <client.jar>] [--cache <dir>]
string? version = null, outDir = null, jarPath = null, cache = null;
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--version": version = Next(); break;
        case "--out": outDir = Next(); break;
        case "--jar": jarPath = Next(); break;
        case "--cache": cache = Next(); break;
        default: Console.Error.WriteLine($"Unknown argument {args[i]}"); return 2;
    }
}
if (version == null || outDir == null)
{
    Console.Error.WriteLine("usage: AubsCraft.AssetTool --version 1.21.5 --out <dir> [--jar <client.jar>] [--cache <dir>]");
    return 2;
}

try
{
    var jar = await JarLocator.FindOrDownloadAsync(version, jarPath, cache, m => Console.WriteLine("[assets] " + m));
    using var client = ClientJar.Open(jar);
    var result = new BlockAssetBuilder(client, version).Build();

    Directory.CreateDirectory(outDir);
    // Written to temporary names and moved: a build that is interrupted must not leave half a file for the next one to trust
    void Write(string name, byte[] bytes)
    {
        var path = Path.Combine(outDir, name);
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, overwrite: true);
    }
    Write("mc-blocks.json", JsonSerializer.SerializeToUtf8Bytes(result.Assets, BlockAssetsJson.Default.BlockAssets));
    // Which properties of which blocks the viewer can tell apart: the server reads this to name block states (a log lying down is another id than a standing one)
    Write("mc-state-props.json", JsonSerializer.SerializeToUtf8Bytes(result.Assets.StateProps.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value)));
    Write("mc-atlas.rgba", result.AtlasRgba);
    Write("mc-atlas.png", TileSet.EncodePng(result.AtlasRgba, result.AtlasWidth, result.AtlasHeight));
    // The creatures' textures (the skin slots below them are filled by the viewer): a missing texture is reported, not fatal
    var entities = new EntityAtlasBuilder(client, version).Build(AubsCraft.Live.Model.Entities.EntityModels.TextureNames());
    Write("mc-entities.json", JsonSerializer.SerializeToUtf8Bytes(entities.Assets, EntityAssetsJson.Default.EntityAssets));
    Write("mc-entity-atlas.rgba", entities.StaticRgba);
    // The particle sprites (smoke, flames, sparks): every texture of the jar's particle folder, animations laid out as strips
    var particles = new ParticleAtlasBuilder(client, version).Build();
    Write("mc-particles.json", JsonSerializer.SerializeToUtf8Bytes(particles.Assets, ParticleAssetsJson.Default.ParticleAssets));
    Write("mc-particle-atlas.rgba", particles.Rgba);
    // What the biomes do to grass, leaves and water (the colour maps and the biome table)
    var biomes = new BiomeColorBuilder(client, version).Build();
    Write("mc-biomes.json", JsonSerializer.SerializeToUtf8Bytes(biomes, BiomeAssetsJson.Default.BiomeAssets));
    Write("mc-assets-report.txt", System.Text.Encoding.UTF8.GetBytes(result.Report + "\n" + entities.Report + "\n" + particles.Report + $"\nBiome colours: {biomes.Names.Length} biomes\n"));
    Console.WriteLine("[assets] " + result.Report.Split('\n')[1].Trim());
    Console.WriteLine("[assets] " + entities.Report.Split('\n')[1].Trim());
    Console.WriteLine("[assets] " + particles.Report.Split('\n')[1].Trim());
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("[assets] FAILED: " + ex.Message);
    return 1;
}
