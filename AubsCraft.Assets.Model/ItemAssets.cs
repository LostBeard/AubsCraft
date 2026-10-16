using System.Text.Json.Serialization;

namespace AubsCraft.Assets.Model;

/// <summary>
/// The pictures of dropped items, as the viewer reads them (written by AubsCraft.AssetTool from the game's own item models and textures, never committed): one atlas of
/// <see cref="AtlasWidth"/> x <see cref="AtlasHeight"/> pixels. Each item has the unfolded box the entity renderer draws: a flat item (a sword, an apple) is a 16 x 16 x 1 box whose
/// two big faces are its sprite (<see cref="SpriteWidth"/> x <see cref="SpriteHeight"/> pixels, the box's cross unfolding); a block is a 16 x 16 x 16 cube (<see cref="CubeWidth"/> x
/// <see cref="CubeHeight"/>). <see cref="Items"/> maps an item's name to x, y (where its unfolding starts in the atlas) and the kind (0 sprite, 1 cube). Items whose pictures came out
/// the same share one place. A flat item has a fourth number: 1 when the game holds it as a tool (a sword, a pickaxe), 0 otherwise. A painting (<c>painting/kebab</c>) or an item frame (<c>frame/item_frame</c>) is a box one pixel thick of the picture's own size: its entry is x, y, 2, width, height
/// of the picture in pixels (its unfolding is 2 x width + 2 by height + 1).
/// </summary>
public sealed class ItemAssets
{
    public const int KindSprite = 0, KindCube = 1, KindHanging = 2;
    public const int SpriteWidth = 34, SpriteHeight = 17, CubeWidth = 64, CubeHeight = 32;

    public string McVersion { get; set; } = "";
    public int AtlasWidth { get; set; }
    public int AtlasHeight { get; set; }
    public Dictionary<string, int[]> Items { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ItemAssets))]
public partial class ItemAssetsJson : JsonSerializerContext
{
}
