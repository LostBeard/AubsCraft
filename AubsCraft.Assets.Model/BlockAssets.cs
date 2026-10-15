using System.Text.Json.Serialization;

namespace AubsCraft.Assets.Model;

/// <summary>
/// The look of every block of a Minecraft version, generated from that version's client jar (see AubsCraft.Assets): which atlas tile each face
/// of a block uses, how it is tinted, how it is drawn, and which tiles animate. Loaded by the viewer next to the atlas image (mc-atlas.rgba).
/// </summary>
public sealed class BlockAssets
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    /// <summary>The Minecraft version the assets come from ("1.21.5").</summary>
    public string GameVersion { get; set; } = "";
    /// <summary>Side of an atlas tile in pixels (16 in vanilla).</summary>
    public int TileSize { get; set; } = 16;
    public int TilesPerRow { get; set; }
    public int TileCount { get; set; }
    /// <summary>Block name ("minecraft:stone") to how it looks, for every block the version has.</summary>
    public Dictionary<string, BlockAsset> Blocks { get; set; } = new();
    /// <summary>
    /// The block states that look different, as extra entries of <see cref="Blocks"/> keyed <c>minecraft:name[prop=value,...]</c>, and per block name the properties those
    /// keys use (in key order). The server names a block with these properties only, so a chunk's ids carry exactly the states the viewer can tell apart.
    /// </summary>
    public Dictionary<string, string[]> StateProps { get; set; } = new();
    /// <summary>Tiles that play through other tiles (water, lava, fire, portals...).</summary>
    public List<TileAnimation> Animations { get; set; } = new();

    public int AtlasWidth => TilesPerRow * TileSize;
    public int AtlasHeight => TileCount == 0 ? 0 : ((TileCount + TilesPerRow - 1) / TilesPerRow) * TileSize;
}

/// <summary>How one block name is drawn (the look of its default state: block properties are not part of the name).</summary>
public sealed class BlockAsset
{
    /// <summary>"none" (air and other invisible blocks), "cube", "cross" (plants: two crossed planes), "water" (alpha blended), or "shape" (not a full cube: drawn as a cube for now).</summary>
    public string Kind { get; set; } = BlockKinds.Cube;
    /// <summary>The atlas tile of each face, in the order +X, -X, +Z, -Z, +Y, -Y; -1 = no texture. A cross uses the first.</summary>
    public int[] Faces { get; set; } = new int[6] { -1, -1, -1, -1, -1, -1 };
    /// <summary>Bit f set: face f is multiplied by the tint colour (grass top, leaves...). Faces with a tint baked into their tile are not set.</summary>
    public int TintFaces { get; set; }
    /// <summary>Which colour the tint is: "none", "grass", "foliage", "water" (a biome colour map), or "fixed" (the same everywhere).</summary>
    public string Tint { get; set; } = BlockTints.None;
    /// <summary>The tint colour (r, g, b 0-255) to use until a biome says otherwise: the plains colour of a map tint, the colour of a fixed one.</summary>
    public int[] TintColor { get; set; } = new int[3] { 255, 255, 255 };
    /// <summary>"opaque", "cutout" (some texels fully transparent: leaves, plants) or "translucent" (some texels partly transparent: glass, ice).</summary>
    public string Render { get; set; } = BlockRenders.Opaque;
    /// <summary>
    /// 1..6 for a shape whose model lies within that many sixteenths of the floor (a carpet, rail, pressure plate, snow layer, closed trapdoor): the viewer draws it
    /// as a cube squashed to that height. 0 for everything else (slabs, 8/16, are left as cubes: whether one is the bottom, the top or a double is a block state the registry does not carry yet).
    /// </summary>
    public int Thickness { get; set; }
}

/// <summary>A tile that shows <c>Timeline[tick % Timeline.Length]</c>, 20 ticks a second, where it is used.</summary>
public sealed class TileAnimation
{
    public int Tile { get; set; }
    public int[] Timeline { get; set; } = Array.Empty<int>();
}

public static class BlockKinds
{
    public const string None = "none", Cube = "cube", Cross = "cross", Water = "water", Shape = "shape";
}

public static class BlockTints
{
    public const string None = "none", Grass = "grass", Foliage = "foliage", Water = "water", Fixed = "fixed";
}

public static class BlockRenders
{
    public const string Opaque = "opaque", Cutout = "cutout", Translucent = "translucent";
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BlockAssets))]
public partial class BlockAssetsJson : JsonSerializerContext
{
}
