using System.Text.Json.Serialization;

namespace AubsCraft.Assets.Model;

/// <summary>
/// The colour of grass, leaves and water in every biome, as the viewer reads it (written by AubsCraft.AssetTool from the game's colour maps and the biome table, never committed). A biome's id is its
/// index in <see cref="Names"/> (the same order the server numbers them in); <see cref="Grass"/>, <see cref="Foliage"/> and <see cref="Water"/> hold three bytes (r, g, b) a biome, one after another.
/// An id past the end (a biome the table does not know) is drawn as plains.
/// </summary>
public sealed class BiomeAssets
{
    public string McVersion { get; set; } = "";
    public string[] Names { get; set; } = System.Array.Empty<string>();
    public byte[] Grass { get; set; } = System.Array.Empty<byte>();
    public byte[] Foliage { get; set; } = System.Array.Empty<byte>();
    public byte[] Water { get; set; } = System.Array.Empty<byte>();
    /// <summary>The id of plains, the colour of anything unknown.</summary>
    public int PlainsId { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BiomeAssets))]
public partial class BiomeAssetsJson : JsonSerializerContext
{
}
