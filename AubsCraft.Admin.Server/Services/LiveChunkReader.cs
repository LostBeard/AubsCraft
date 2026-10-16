using System.Text;
using AubsCraft.Live.Model.BlockEntities;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// A chunk as the AubsCraft Live plugin posts it while the world changes (FORMAT.md, "chunk update"): the blocks as strings of block data, the light, how high the rain falls, the sign texts and the
/// banner patterns. All numbers little-endian. It becomes the same <see cref="ChunkResult"/> the region reader makes from the file, so every endpoint that serves chunks serves it unchanged.
/// <code>
/// u8 version (1) | u8 border mask | u16 top (layers sent, from the world's bottom) | u16 palette count | palette: (u16 length, UTF-8 block data string)...
/// | u16 blocks[256 * top] (palette index, x + z*16 + y*256) | u8 light[256 * top] (sky&lt;&lt;4 | block) | u16 rain[256] (height of the highest blocking block's top above the bottom)
/// | u16 sign count, each: u8 x, i16 y, u8 z, 4 strings, dye string, u8 glow | u16 banner count, each: u8 x, i16 y, u8 z, u8 layers, (pattern string, dye string)...
/// </code>
/// The border mask says which neighbours the change touches (1 -X, 2 +X, 4 -Z, 8 +Z): their faces toward this chunk may have changed.
/// </summary>
public static class LiveChunkReader
{
    public const int MaxBytes = 8 * 1024 * 1024;
    private const int Layer = 256, TotalHeight = 384;

    public sealed record Parsed(ChunkResult Chunk, byte[] Light, int BorderMask);

    /// <summary>The chunk of a post. <paramref name="previous"/> (the chunk as it was) lends what the post does not carry: the biomes. Throws <see cref="InvalidDataException"/> for anything that is not exactly the format.</summary>
    public static Parsed Parse(byte[] body, ChunkResult? previous)
    {
        try { return ParseCore(body, previous); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or EndOfStreamException or OverflowException)
        {
            throw new InvalidDataException("Not a chunk update: " + ex.Message, ex);
        }
    }

    private static Parsed ParseCore(byte[] body, ChunkResult? previous)
    {
        var r = new Reader(body);
        if (r.U8() != 1) throw new InvalidDataException("Unknown chunk update version");
        int border = r.U8();
        int top = r.U16();
        if (top > TotalHeight) throw new InvalidDataException("Too many layers");
        int paletteCount = r.U16();
        if (paletteCount == 0 && top > 0) throw new InvalidDataException("No palette");

        // The plugin's block data strings -> the viewer's names; id 0 of a chunk's palette is air, as in the file reader
        var palette = new List<string> { "minecraft:air" };
        var local = new Dictionary<string, ushort> { ["minecraft:air"] = 0 };
        var map = new ushort[paletteCount];
        BlockEntitySpot?[]? entityOf = null;
        for (int i = 0; i < paletteCount; i++)
        {
            var (name, props) = ParseBlockData(r.String());
            if (BlockEntityKinds.Classify(name, props) is { } spot)
            {
                entityOf ??= new BlockEntitySpot?[paletteCount];
                entityOf[i] = spot;
            }
            var key = BlockStateNamer.Current.Key(name, props);
            if (!local.TryGetValue(key, out var id))
            {
                id = (ushort)palette.Count;
                palette.Add(key);
                local[key] = id;
            }
            map[i] = id;
        }

        var blocks = new ushort[Layer * TotalHeight];
        List<BlockEntitySpot>? blockEntities = null;
        for (int i = 0; i < Layer * top; i++)
        {
            int p = r.U16();
            if (p >= paletteCount) throw new InvalidDataException("Palette index out of range");
            blocks[i] = map[p];
            if (entityOf?[p] is { } found && (blockEntities?.Count ?? 0) < BlockEntityCodec.MaxPerColumn)
                (blockEntities ??= new()).Add(found with { X = i & 15, Y = (i >> 8) - 64, Z = (i >> 4) & 15 });
        }

        // Above what was sent is open air under open sky
        var light = new byte[Layer * TotalHeight];
        r.Bytes(light, Layer * top);
        if (top < TotalHeight) System.Array.Fill(light, (byte)0xF0, Layer * top, Layer * (TotalHeight - top));

        var rain = new ushort[256];
        for (int i = 0; i < 256; i++) rain[i] = (ushort)r.U16();

        List<SignText>? signs = null;
        int signCount = r.U16();
        for (int i = 0; i < signCount; i++)
        {
            int x = r.U8(), y = (short)r.U16(), z = r.U8();
            var lines = new string[4];
            for (int l = 0; l < 4; l++) lines[l] = r.String();
            int dye = System.Array.IndexOf(BlockEntityKinds.Dyes, r.String());
            bool glow = r.U8() != 0;
            var sign = new SignText(x & 15, y, z & 15, lines, (byte)(dye < 0 ? 15 : dye), glow);
            if (sign.IsBlank || (signs?.Count ?? 0) >= SignTextCodec.MaxPerColumn) continue;
            (signs ??= new()).Add(sign);
        }

        List<BannerLayers>? banners = null;
        int bannerCount = r.U16();
        for (int i = 0; i < bannerCount; i++)
        {
            int x = r.U8(), y = (short)r.U16(), z = r.U8();
            int n = r.U8();
            var layers = new List<(byte, byte)>();
            for (int l = 0; l < n; l++)
            {
                int pattern = BannerPatterns.IndexOf(r.String());
                int color = System.Array.IndexOf(BlockEntityKinds.Dyes, r.String());
                if (pattern < 0 || color < 0 || layers.Count >= BannerLayersCodec.MaxLayers) continue;
                layers.Add(((byte)pattern, (byte)color));
            }
            if (layers.Count == 0 || (banners?.Count ?? 0) >= BannerLayersCodec.MaxPerColumn) continue;
            (banners ??= new()).Add(new BannerLayers(x & 15, y, z & 15, layers.ToArray()));
        }

        return new Parsed(new ChunkResult(blocks, palette, previous?.Biomes, blockEntities, rain, signs, banners), light, border);
    }

    /// <summary>"minecraft:oak_stairs[facing=north,half=bottom]" -> the name and its properties.</summary>
    public static (string Name, Dictionary<string, object>? Properties) ParseBlockData(string data)
    {
        int open = data.IndexOf('[');
        if (open < 0) return (data, null);
        var name = data[..open];
        var props = new Dictionary<string, object>();
        foreach (var pair in data[(open + 1)..].TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) props[pair[..eq]] = pair[(eq + 1)..];
        }
        return (name, props);
    }

    /// <summary>The message the viewers get when chunks change: "ACHG", a count (u16), and for each chunk x and z (i32 each) and the border mask (u8).</summary>
    public static byte[] ChangeMessage(IReadOnlyList<(int Cx, int Cz, int Border)> chunks)
    {
        var bytes = new byte[6 + chunks.Count * 9];
        "ACHG"u8.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (ushort)chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            int at = 6 + i * 9;
            BitConverter.TryWriteBytes(bytes.AsSpan(at), chunks[i].Cx);
            BitConverter.TryWriteBytes(bytes.AsSpan(at + 4), chunks[i].Cz);
            bytes[at + 8] = (byte)chunks[i].Border;
        }
        return bytes;
    }

    private sealed class Reader(byte[] data)
    {
        private int _at;
        public int U8() => data[_at++];
        public int U16() { int v = data[_at] | (data[_at + 1] << 8); _at += 2; return v; }
        public string String()
        {
            int n = U16();
            if (_at + n > data.Length) throw new EndOfStreamException("string runs past the end");
            var s = Encoding.UTF8.GetString(data, _at, n);
            _at += n;
            return s;
        }
        public void Bytes(byte[] into, int count)
        {
            if (_at + count > data.Length) throw new EndOfStreamException("light runs past the end");
            Buffer.BlockCopy(data, _at, into, 0, count);
            _at += count;
        }
    }
}
