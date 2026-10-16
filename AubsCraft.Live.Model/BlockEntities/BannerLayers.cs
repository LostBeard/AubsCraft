namespace AubsCraft.Live.Model.BlockEntities;

/// <summary>
/// The patterns of a banner: layer after layer, each a pattern of the game's (its mask in <c>textures/entity/banner</c>) in a dye, laid over the base colour in order.
/// </summary>
public sealed record BannerLayers(int X, int Y, int Z, (byte Pattern, byte Color)[] Layers);

/// <summary>The game's banner patterns by number (the order is fixed here, the names are the files of textures/entity/banner), and the names a world file gives them.</summary>
public static class BannerPatterns
{
    public static readonly string[] Names =
    [
        "base", "border", "bricks", "circle", "creeper", "cross", "curly_border", "diagonal_left", "diagonal_right", "diagonal_up_left", "diagonal_up_right", "flow", "flower", "globe", "gradient",
        "gradient_up", "guster", "half_horizontal", "half_horizontal_bottom", "half_vertical", "half_vertical_right", "mojang", "piglin", "rhombus", "skull", "small_stripes", "square_bottom_left",
        "square_bottom_right", "square_top_left", "square_top_right", "straight_cross", "stripe_bottom", "stripe_center", "stripe_downleft", "stripe_downright", "stripe_left", "stripe_middle",
        "stripe_right", "stripe_top", "triangle_bottom", "triangle_top", "triangles_bottom", "triangles_top",
    ];

    /// <summary>The number of a pattern ("minecraft:stripe_top" or "stripe_top"), or -1 for one the viewer does not know (a data pack's).</summary>
    public static int IndexOf(string id)
    {
        if (id.StartsWith("minecraft:", StringComparison.Ordinal)) id = id[10..];
        return Array.IndexOf(Names, id);
    }

    /// <summary>The texture of a pattern's layer (a path under textures/entity).</summary>
    public static string TextureOf(int pattern) => $"banner/{Names[pattern]}";

    /// <summary>The textures the layers can wear, for the asset tool to pack.</summary>
    public static IEnumerable<string> TextureNames() => Names.Select((_, i) => TextureOf(i));
}

/// <summary>
/// The banners' patterns of a column as the server sends them (the <c>X-BannerPatterns</c> header, base64): a count, then for each banner x and z (a byte each), y (16 bits, little-endian, the world's),
/// the number of layers (at most <see cref="MaxLayers"/>, the game's own limit being six on a survival banner, more by command) and a pattern and a dye byte for each.
/// </summary>
public static class BannerLayersCodec
{
    public const int MaxPerColumn = 80;
    public const int MaxLayers = 16;

    public static string Encode(IReadOnlyList<BannerLayers> banners)
    {
        using var stream = new MemoryStream();
        int count = Math.Min(banners.Count, MaxPerColumn);
        stream.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
        {
            var b = banners[i];
            stream.WriteByte((byte)(b.X & 15));
            stream.WriteByte((byte)(b.Z & 15));
            stream.WriteByte((byte)(b.Y & 0xFF));
            stream.WriteByte((byte)((b.Y >> 8) & 0xFF));
            int layers = Math.Min(b.Layers.Length, MaxLayers);
            stream.WriteByte((byte)layers);
            for (int l = 0; l < layers; l++) { stream.WriteByte(b.Layers[l].Pattern); stream.WriteByte(b.Layers[l].Color); }
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    public static List<BannerLayers> Decode(string? header)
    {
        var result = new List<BannerLayers>();
        if (string.IsNullOrEmpty(header)) return result;
        byte[] data;
        try { data = Convert.FromBase64String(header); }
        catch (FormatException) { return result; }
        if (data.Length < 1) return result;
        int count = data[0], at = 1;
        for (int i = 0; i < count; i++)
        {
            if (at + 5 > data.Length) break;
            int x = data[at], z = data[at + 1];
            int y = (short)(data[at + 2] | (data[at + 3] << 8));
            int layers = data[at + 4];
            at += 5;
            if (layers > MaxLayers || at + layers * 2 > data.Length) break;
            var list = new (byte, byte)[layers];
            for (int l = 0; l < layers; l++) { list[l] = (data[at], data[at + 1]); at += 2; }
            result.Add(new BannerLayers(x, y, z, list));
        }
        return result;
    }
}
