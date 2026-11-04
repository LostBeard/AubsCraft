using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AubsCraft.Assets.Model;
using StbImageSharp;

namespace AubsCraft.Assets;

/// <summary>The packed HUD pictures and what describes them.</summary>
public sealed record GuiAtlasResult(GuiAssets Assets, byte[] Rgba, string Report);

/// <summary>
/// Packs what a player's HUD is made of into one atlas: the game's own sprites (hearts, hunger, armor, air, the hotbar and its selection, the experience bar) and an icon for every item. An
/// item's icon comes from <see cref="ItemAtlasBuilder"/>'s pictures: a flat item is its sprite, a block the isometric cube a hotbar shows it as, drawn here from the top and side of its cube,
/// shaded as the game shades them.
/// </summary>
public sealed class GuiAtlasBuilder
{
    public const int AtlasWidth = 1024;

    /// <summary>The sprites of gui/sprites/hud the viewer's player bars use. A jar without one of them is an error: the bars would come out with holes in them.</summary>
    public static readonly string[] SpriteNames =
    [
        "heart/container", "heart/full", "heart/half", "heart/absorbing_full", "heart/absorbing_half",
        "food_empty", "food_half", "food_full",
        "armor_empty", "armor_half", "armor_full",
        "air", "air_empty",
        "hotbar", "hotbar_selection",
        "experience_bar_background", "experience_bar_progress",
    ];

    private readonly ClientJar _jar;
    private readonly string _version;

    public GuiAtlasBuilder(ClientJar jar, string version)
    {
        _jar = jar;
        _version = version;
    }

    public GuiAtlasResult Build(ItemAssets items, byte[] itemRgba)
    {
        // Row-packed sprites, tallest first
        var sprites = new List<(string Name, int W, int H, byte[] Rgba)>();
        foreach (var name in SpriteNames)
        {
            var path = $"assets/minecraft/textures/gui/sprites/hud/{name}.png";
            if (!_jar.Exists(path)) throw new InvalidOperationException($"The client jar has no {path}");
            var image = ImageResult.FromMemory(_jar.ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
            sprites.Add((name, image.Width, image.Height, image.Data));
        }

        var placedSprites = new Dictionary<string, int[]>(StringComparer.Ordinal);
        int x = 0, y = 0, rowHeight = 0;
        foreach (var s in sprites.OrderByDescending(s => s.H).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            if (x + s.W > AtlasWidth) { y += rowHeight; x = 0; rowHeight = 0; }
            placedSprites[s.Name] = [x, y, s.W, s.H];
            rowHeight = Math.Max(rowHeight, s.H);
            x += s.W + 1;   // a pixel between sprites: a scaled quad's edge must not sample the neighbour
        }
        int iconsTop = y + rowHeight + 1;

        // The icons, the same ones sharing a place
        const int icon = GuiAssets.IconSize;
        int columns = AtlasWidth / icon;
        var byHash = new Dictionary<string, (int Index, byte[] Rgba)>(StringComparer.Ordinal);
        var iconOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, place) in items.Items.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            byte[]? picture = place[2] switch
            {
                ItemAssets.KindSprite => FlatIcon(itemRgba, items.AtlasWidth, place[0], place[1]),
                ItemAssets.KindCube => CubeIcon(itemRgba, items.AtlasWidth, place[0], place[1]),
                _ => null,   // a painting or an item frame's picture is not an item's icon
            };
            if (picture == null) continue;
            var hash = Convert.ToHexString(SHA256.HashData(picture));
            if (!byHash.TryGetValue(hash, out var known))
            {
                known = (byHash.Count, picture);
                byHash[hash] = known;
            }
            iconOf[name] = known.Index;
        }

        int rows = (byHash.Count + columns - 1) / columns;
        int height = iconsTop + rows * icon;
        var rgba = new byte[AtlasWidth * height * 4];
        foreach (var s in sprites)
        {
            var at = placedSprites[s.Name];
            for (int row = 0; row < s.H; row++)
                Buffer.BlockCopy(s.Rgba, row * s.W * 4, rgba, ((at[1] + row) * AtlasWidth + at[0]) * 4, s.W * 4);
        }
        foreach (var (_, (index, picture)) in byHash)
        {
            int ix = index % columns * icon, iy = iconsTop + index / columns * icon;
            for (int row = 0; row < icon; row++)
                Buffer.BlockCopy(picture, row * icon * 4, rgba, ((iy + row) * AtlasWidth + ix) * 4, icon * 4);
        }

        var assets = new GuiAssets { McVersion = _version, AtlasWidth = AtlasWidth, AtlasHeight = height, Sprites = placedSprites };
        foreach (var (name, index) in iconOf) assets.Items[name] = [index % columns * icon, iconsTop + index / columns * icon];

        var report = new StringBuilder();
        report.AppendLine($"HUD assets for Minecraft {_version}");
        report.AppendLine($"  {placedSprites.Count} sprites, {iconOf.Count} item icons ({byHash.Count} different) in {AtlasWidth}x{height} px");
        return new GuiAtlasResult(assets, rgba, report.ToString());
    }

    // A flat item: its sprite at twice its size (the unfolding's front is at 1, 1)
    private static byte[] FlatIcon(byte[] atlas, int atlasWidth, int x, int y)
    {
        const int icon = GuiAssets.IconSize;
        var pixels = new byte[icon * icon * 4];
        for (int py = 0; py < icon; py++)
            for (int px = 0; px < icon; px++)
                Buffer.BlockCopy(atlas, ((y + 1 + py / 2) * atlasWidth + x + 1 + px / 2) * 4, pixels, (py * icon + px) * 4, 4);
        return pixels;
    }

    // Where the three faces of the icon's cube lie in the icon (0..1), as the game's isometric item view has them: each is a parallelogram, origin and two edges
    private static readonly (Vector2 Origin, Vector2 U, Vector2 V, float Shade)[] Faces =
    [
        (new Vector2(0.5f, 0.07f), new Vector2(0.43f, 0.215f), new Vector2(-0.43f, 0.215f), 1f),      // top: u along the back right edge, v along the back left
        (new Vector2(0.07f, 0.285f), new Vector2(0.43f, 0.215f), new Vector2(0f, 0.43f), 0.78f),      // left
        (new Vector2(0.5f, 0.5f), new Vector2(0.43f, -0.215f), new Vector2(0f, 0.43f), 0.58f),        // right
    ];

    // A block: the cube's top (the unfolding's 16, 0) and side (16, 16), drawn pixel by pixel into the three faces
    private static byte[] CubeIcon(byte[] atlas, int atlasWidth, int x, int y)
    {
        const int icon = GuiAssets.IconSize;
        var pixels = new byte[icon * icon * 4];
        for (int py = 0; py < icon; py++)
            for (int px = 0; px < icon; px++)
            {
                var p = new Vector2((px + 0.5f) / icon, (py + 0.5f) / icon);
                for (int face = 0; face < Faces.Length; face++)
                {
                    var (origin, eu, ev, shade) = Faces[face];
                    var d = p - origin;
                    float det = eu.X * ev.Y - eu.Y * ev.X;
                    float u = (d.X * ev.Y - d.Y * ev.X) / det, v = (eu.X * d.Y - eu.Y * d.X) / det;
                    if (u < 0f || u >= 1f || v < 0f || v >= 1f) continue;
                    int tx = x + 16 + Math.Min((int)(u * 16f), 15), ty = y + (face == 0 ? 0 : 16) + Math.Min((int)(v * 16f), 15);
                    int from = (ty * atlasWidth + tx) * 4, to = (py * icon + px) * 4;
                    if (atlas[from + 3] == 0) continue;   // glass, leaves: the next face shows through
                    pixels[to] = (byte)(atlas[from] * shade);
                    pixels[to + 1] = (byte)(atlas[from + 1] * shade);
                    pixels[to + 2] = (byte)(atlas[from + 2] * shade);
                    pixels[to + 3] = atlas[from + 3];
                    break;
                }
            }
        return pixels;
    }
}
