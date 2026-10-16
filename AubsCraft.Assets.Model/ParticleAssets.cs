using System.Text.Json.Serialization;

namespace AubsCraft.Assets.Model;

/// <summary>
/// The particle sprites, as the viewer reads them (written by AubsCraft.AssetTool from the game's own particle textures, never committed): one atlas of
/// <see cref="AtlasWidth"/> x <see cref="AtlasHeight"/> pixels. An animation (<c>big_smoke_0</c> ... <c>big_smoke_11</c>, all one size) is laid out as one
/// strip, frame after frame to the right, and is named without its number; a lone sprite is its own name.
/// </summary>
public sealed class ParticleAssets
{
    public string McVersion { get; set; } = "";
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    /// <summary>Sprite (an animation's name has no frame number: <c>big_smoke</c>) to its place: x, y, a frame's width, a frame's height, how many frames.</summary>
    public Dictionary<string, int[]> Sprites { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ParticleAssets))]
public partial class ParticleAssetsJson : JsonSerializerContext
{
}
