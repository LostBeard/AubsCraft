using System.Text.Json.Serialization;

namespace AubsCraft.Assets.Model;

/// <summary>
/// The images of the sky, as the viewer reads them (written by AubsCraft.AssetTool from the game's own environment textures, never committed): one atlas of <see cref="AtlasWidth"/> x
/// <see cref="AtlasHeight"/> pixels holding the sun, the eight phases of the moon and the cloud map. Each rect is x, y, width, height in atlas pixels; the moon's rect is the first
/// phase's (a frame's size), the others follow to the right, four to a row.
/// </summary>
public sealed class SkyAssets
{
    public string McVersion { get; set; } = "";
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    public int[] Sun { get; set; } = new int[4];
    public int[] Moon { get; set; } = new int[4];
    public int[] Clouds { get; set; } = new int[4];
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SkyAssets))]
public partial class SkyAssetsJson : JsonSerializerContext
{
}
