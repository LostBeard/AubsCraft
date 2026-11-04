using System.Text.Json.Serialization;

namespace AubsCraft.Assets.Model;

/// <summary>
/// The pictures of the game's own HUD, as the viewer reads them (written by AubsCraft.AssetTool from the client jar, never committed): one atlas of <see cref="AtlasWidth"/> x
/// <see cref="AtlasHeight"/> pixels. <see cref="Sprites"/> maps a sprite's name as the jar has it under gui/sprites/hud ("heart/full", "food_empty", "hotbar") to x, y, width, height.
/// <see cref="Items"/> maps an item's name to the x, y of its icon, a square of <see cref="IconSize"/> pixels: a flat item at twice its size, a block as the small isometric cube a hotbar shows.
/// Items whose icons came out the same share one place.
/// </summary>
public sealed class GuiAssets
{
    public const int IconSize = 32;

    public string McVersion { get; set; } = "";
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    public Dictionary<string, int[]> Sprites { get; set; } = new();
    public Dictionary<string, int[]> Items { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuiAssets))]
public partial class GuiAssetsJson : JsonSerializerContext
{
}
