using System.Text.Json.Serialization;

namespace AubsCraft.Assets.Model;

/// <summary>
/// The entity texture atlas, as the viewer reads it (written by AubsCraft.AssetTool from the game's own creature textures, never committed). The atlas
/// is <see cref="AtlasWidth"/> x <see cref="AtlasHeight"/> pixels: the generated textures are packed at the top (<see cref="StaticHeight"/> rows, shipped as
/// pixels), and below them is a grid of <see cref="SkinSize"/> square slots the viewer fills with players' skins as they arrive.
/// </summary>
public sealed class EntityAssets
{
    public string McVersion { get; set; } = "";
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    /// <summary>Rows of the atlas the generated pixels cover (the file holds AtlasWidth x StaticHeight pixels).</summary>
    public int StaticHeight { get; set; }
    /// <summary>Where the skin slots start (y), their side, and how many there are across and down.</summary>
    public int SkinY { get; set; }
    public int SkinSize { get; set; } = 64;
    public int SkinColumns { get; set; }
    public int SkinRows { get; set; }
    /// <summary>Where the sign-text slots start (y), their size (a sign's writing takes the left half of a slot, the right half stays clear), and how many there are across and down.</summary>
    public int TextY { get; set; }
    public int TextWidth { get; set; } = 192;
    public int TextHeight { get; set; } = 48;
    public int TextColumns { get; set; }
    public int TextRows { get; set; }
    /// <summary>Texture name (a path under textures/entity, no extension; "white" is a plain white tile) to its place: x, y, width, height in atlas pixels.</summary>
    public Dictionary<string, int[]> Textures { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EntityAssets))]
public partial class EntityAssetsJson : JsonSerializerContext
{
}
