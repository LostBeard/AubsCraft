using System.Text;
using AubsCraft.Assets.Model;
using StbImageSharp;

namespace AubsCraft.Assets;

/// <summary>The packed entity textures and what describes them.</summary>
public sealed record EntityAtlasResult(EntityAssets Assets, byte[] StaticRgba, string Report);

/// <summary>
/// Packs creature textures from the client jar into one atlas (see <see cref="EntityAssets"/>): any mix of sizes, shelf-packed tallest first so the result is
/// the same every time, nothing between neighbours (the shader fetches exact texels). Textures the jar does not have are reported, not fatal: a creature
/// whose texture is missing draws as nothing visible, and the report says which.
/// </summary>
public sealed class EntityAtlasBuilder
{
    public const int AtlasWidth = 1024, StaticLimit = 1792, SkinLimit = 2304, SkinSize = 64;
    /// <summary>Under the skins, a band of slots for the writing on signs: each 192 x 48 (the writing on the left half), 5 across and 16 down.</summary>
    public const int TextWidth = 192, TextHeight = 48, TextColumns = AtlasWidth / TextWidth, TextRows = 16;
    public const int AtlasHeight = SkinLimit + TextRows * TextHeight;
    public const string White = "white";

    private readonly ClientJar _jar;
    private readonly string _version;

    public EntityAtlasBuilder(ClientJar jar, string version)
    {
        _jar = jar;
        _version = version;
    }

    public EntityAtlasResult Build(IEnumerable<string> textureNames)
    {
        var report = new StringBuilder();
        var images = new List<(string name, int w, int h, byte[] rgba)>();
        var missing = new List<string>();
        foreach (var name in textureNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (name == White)
            {
                var px = new byte[16 * 16 * 4];
                Array.Fill(px, (byte)255);
                images.Add((name, 16, 16, px));
                continue;
            }
            var path = $"assets/minecraft/textures/entity/{name}.png";
            if (!_jar.Exists(path)) { missing.Add(name); continue; }
            var image = ImageResult.FromMemory(_jar.ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
            images.Add((name, image.Width, image.Height, image.Data));
        }

        // Shelf packing, tallest first: a row is as high as its first (tallest) texture
        var placed = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var ordered = images.OrderByDescending(i => i.h).ThenByDescending(i => i.w).ThenBy(i => i.name, StringComparer.Ordinal).ToList();
        int x = 0, y = 0, rowHeight = 0;
        foreach (var img in ordered)
        {
            if (img.w > AtlasWidth) throw new InvalidOperationException($"{img.name} is {img.w} px wide: wider than the {AtlasWidth} px atlas");
            if (x + img.w > AtlasWidth) { y += rowHeight; x = 0; rowHeight = 0; }
            if (rowHeight == 0) rowHeight = img.h;
            if (y + img.h > StaticLimit)
                throw new InvalidOperationException($"The entity textures do not fit the {AtlasWidth}x{StaticLimit} area above the skin slots (at {img.name})");
            placed[img.name] = new[] { x, y, img.w, img.h };
            x += img.w;
        }
        int staticHeight = y + rowHeight;

        var rgba = new byte[AtlasWidth * staticHeight * 4];
        foreach (var img in images)
        {
            var at = placed[img.name];
            for (int row = 0; row < img.h; row++)
                Buffer.BlockCopy(img.rgba, row * img.w * 4, rgba, ((at[1] + row) * AtlasWidth + at[0]) * 4, img.w * 4);
        }

        var assets = new EntityAssets
        {
            McVersion = _version,
            AtlasWidth = AtlasWidth,
            AtlasHeight = AtlasHeight,
            StaticHeight = staticHeight,
            SkinY = StaticLimit,
            SkinSize = SkinSize,
            SkinColumns = AtlasWidth / SkinSize,
            SkinRows = (SkinLimit - StaticLimit) / SkinSize,
            TextY = SkinLimit,
            TextWidth = TextWidth,
            TextHeight = TextHeight,
            TextColumns = TextColumns,
            TextRows = TextRows,
            Textures = placed,
        };
        report.AppendLine($"Entity assets for Minecraft {_version}");
        report.AppendLine($"  {placed.Count} textures in {AtlasWidth}x{staticHeight} px, {assets.SkinColumns * assets.SkinRows} skin slots of {SkinSize} px below");
        foreach (var m in missing) report.AppendLine($"  MISSING texture: {m}");
        return new EntityAtlasResult(assets, rgba, report.ToString());
    }
}
