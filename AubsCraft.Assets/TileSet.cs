using System.Text.Json;
using AubsCraft.Assets.Model;
using StbImageSharp;


namespace AubsCraft.Assets;

/// <summary>Pixel operations on square RGBA8 tiles.</summary>
public static class TileImages
{
    /// <summary>Rotate a tile clockwise by a multiple of 90 degrees.</summary>
    public static byte[] Rotate(byte[] rgba, int size, int degrees)
    {
        int turns = ((degrees % 360) + 360) % 360 / 90;
        var src = rgba;
        for (int t = 0; t < turns; t++)
        {
            var dst = new byte[src.Length];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    // clockwise: the pixel at (x, y) goes to (size - 1 - y, x)
                    Array.Copy(src, (y * size + x) * 4, dst, (x * size + (size - 1 - y)) * 4, 4);
            src = dst;
        }
        return src;
    }

    /// <summary>The (fw x fh) rectangle at (x0, y0) of a w-wide image, scaled to size x size with nearest neighbour.</summary>
    public static byte[] Crop(byte[] image, int w, int x0, int y0, int fw, int fh, int size)
    {
        var tile = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx = x0 + x * fw / size, sy = y0 + y * fh / size;
                Array.Copy(image, (sy * w + sx) * 4, tile, (y * size + x) * 4, 4);
            }
        return tile;
    }

    /// <summary>
    /// <paramref name="overlay"/> over <paramref name="baseTile"/> (source over), the overlay's colour multiplied by <paramref name="tint"/> first:
    /// the grass fringe on the side of a grass block is a grey texture the game tints.
    /// </summary>
    public static byte[] Composite(byte[] baseTile, byte[] overlay, (int r, int g, int b) tint)
    {
        var result = (byte[])baseTile.Clone();
        for (int i = 0; i < result.Length; i += 4)
        {
            int a = overlay[i + 3];
            if (a == 0) continue;
            int r = overlay[i] * tint.r / 255, g = overlay[i + 1] * tint.g / 255, b = overlay[i + 2] * tint.b / 255;
            int inv = 255 - a;
            result[i] = (byte)((r * a + baseTile[i] * inv) / 255);
            result[i + 1] = (byte)((g * a + baseTile[i + 1] * inv) / 255);
            result[i + 2] = (byte)((b * a + baseTile[i + 2] * inv) / 255);
            result[i + 3] = (byte)Math.Max(baseTile[i + 3], a);
        }
        return result;
    }

    /// <summary>The smallest and largest alpha of a tile.</summary>
    public static (int min, int max) AlphaRange(byte[] rgba)
    {
        int min = 255, max = 0;
        for (int i = 3; i < rgba.Length; i += 4) { min = Math.Min(min, rgba[i]); max = Math.Max(max, rgba[i]); }
        return (min, max);
    }
}

/// <summary>
/// The tiles of an atlas as they are collected: block textures by id (with a rotation baked in when a model turns a face), composites, and the
/// frames of animated textures (consecutive tiles, with a per-tick timeline). Tile numbers are final once handed out.
/// </summary>
public sealed class TileSet
{
    private readonly ClientJar _jar;
    private readonly List<byte[]> _tiles = new();
    private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
    private readonly List<TileAnimation> _animations = new();

    public TileSet(ClientJar jar, int tileSize = 16)
    {
        _jar = jar;
        TileSize = tileSize;
    }

    public int TileSize { get; }
    public int Count => _tiles.Count;
    public IReadOnlyList<TileAnimation> Animations => _animations;
    public byte[] Pixels(int tile) => _tiles[tile];

    /// <summary>The tile of a texture ("block/stone"), turned clockwise by <paramref name="rotation"/> degrees; -1 when the jar has no such texture. An animated texture gets all its frames as consecutive tiles and its first is returned.</summary>
    public int GetTexture(string textureId, int rotation = 0)
    {
        rotation = ((rotation % 360) + 360) % 360;
        var key = $"{textureId}@{rotation}";
        if (_byKey.TryGetValue(key, out var existing)) return existing;

        var path = $"assets/minecraft/textures/{textureId}.png";
        if (!_jar.Exists(path)) return _byKey[key] = -1;

        var image = ImageResult.FromMemory(_jar.ReadBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        int w = image.Width, h = image.Height;

        // The frame size and order of an animated texture; a plain texture is one square frame (the top one when the image is taller)
        int fw = w, fh = w;
        bool animated = false;
        int defaultTicks = 1;
        List<(int index, int ticks)>? frames = null;
        if (_jar.Exists(path + ".mcmeta"))
        {
            using var meta = JsonDocument.Parse(_jar.ReadText(path + ".mcmeta"));
            if (meta.RootElement.TryGetProperty("animation", out var anim))
            {
                animated = true;
                if (anim.TryGetProperty("width", out var aw)) fw = aw.GetInt32();
                fh = anim.TryGetProperty("height", out var ah) ? ah.GetInt32() : fw;
                if (anim.TryGetProperty("frametime", out var ft)) defaultTicks = Math.Max(1, ft.GetInt32());
                if (anim.TryGetProperty("frames", out var list))
                {
                    frames = new();
                    foreach (var f in list.EnumerateArray())
                        frames.Add(f.ValueKind == JsonValueKind.Number
                            ? (f.GetInt32(), defaultTicks)
                            : (f.GetProperty("index").GetInt32(), f.TryGetProperty("time", out var tm) ? Math.Max(1, tm.GetInt32()) : defaultTicks));
                }
                // no frame list: every frame in order, each for the default time (filled in below once the frame count is known)
            }
            else { fh = Math.Min(w, h); }
        }
        else { fh = Math.Min(w, h); }

        // Only an animated texture is a strip of frames; a plain one is its top square, however tall the image
        int cols = animated ? Math.Max(1, w / fw) : 1, rows = animated ? Math.Max(1, h / fh) : 1;
        int frameCount = cols * rows;
        frames ??= Enumerable.Range(0, frameCount).Select(i => (i, defaultTicks)).ToList();

        int first = _tiles.Count;
        for (int i = 0; i < frameCount; i++)
        {
            var tile = TileImages.Crop(image.Data, w, (i % cols) * fw, (i / cols) * fh, fw, fh, TileSize);
            _tiles.Add(rotation == 0 ? tile : TileImages.Rotate(tile, TileSize, rotation));
        }

        if (frameCount > 1 && frames.Count > 0)
        {
            var timeline = new List<int>();
            foreach (var (index, ticks) in frames)
                for (int t = 0; t < ticks; t++) timeline.Add(first + Math.Clamp(index, 0, frameCount - 1));
            _animations.Add(new TileAnimation { Tile = first, Timeline = timeline.ToArray() });
        }
        return _byKey[key] = first;
    }

    /// <summary>A tile made by the generator (a composite), added once per key.</summary>
    public int Add(string key, Func<byte[]> make)
    {
        if (_byKey.TryGetValue(key, out var existing)) return existing;
        _tiles.Add(make());
        return _byKey[key] = _tiles.Count - 1;
    }

    /// <summary>The atlas image (RGBA8): the tiles in a near-square grid, row-major from the top-left.</summary>
    public (byte[] rgba, int width, int height, int tilesPerRow) BuildAtlas()
    {
        int n = Math.Max(_tiles.Count, 1);
        int perRow = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (n + perRow - 1) / perRow;
        int width = perRow * TileSize, height = rows * TileSize;
        var atlas = new byte[width * height * 4];
        for (int t = 0; t < _tiles.Count; t++)
        {
            int ox = (t % perRow) * TileSize, oy = (t / perRow) * TileSize;
            for (int y = 0; y < TileSize; y++)
                Array.Copy(_tiles[t], y * TileSize * 4, atlas, ((oy + y) * width + ox) * 4, TileSize * 4);
        }
        return (atlas, width, height, perRow);
    }

    /// <summary>The atlas as a PNG (for looking at it).</summary>
    public static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }
}
