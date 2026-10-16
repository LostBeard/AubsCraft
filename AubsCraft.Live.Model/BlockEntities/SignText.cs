using System.Text;

namespace AubsCraft.Live.Model.BlockEntities;

/// <summary>The writing on the front of a sign: where it is in its column, the four lines, the dye it is written in (an index of <see cref="BlockEntityKinds.Dyes"/>) and whether it glows.</summary>
public sealed record SignText(int X, int Y, int Z, string[] Lines, byte Color, bool Glowing)
{
    /// <summary>The text with nothing in it (all four lines blank) draws nothing.</summary>
    public bool IsBlank => Lines.All(string.IsNullOrWhiteSpace);

    /// <summary>What two signs have to share to share a picture: the lines, the dye and the glow.</summary>
    public string Key => $"{Color}{(Glowing ? "g" : "n")}|{string.Join("|", Lines)}";
}

/// <summary>
/// The signs' text of a column as the server sends it (the <c>X-SignText</c> header, base64): a count, then for each sign x and z (0..15, a byte each), y (16 bits, little-endian, the world's),
/// the dye, the glow flag, and four lines of a length byte and that many UTF-8 bytes. A line is cut at <see cref="MaxLineBytes"/>, a column's signs at <see cref="MaxPerColumn"/>.
/// </summary>
public static class SignTextCodec
{
    public const int MaxPerColumn = 120;
    public const int MaxLineBytes = 60;

    public static string Encode(IReadOnlyList<SignText> signs)
    {
        using var stream = new MemoryStream();
        int count = Math.Min(signs.Count, MaxPerColumn);
        stream.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
        {
            var s = signs[i];
            stream.WriteByte((byte)(s.X & 15));
            stream.WriteByte((byte)(s.Z & 15));
            stream.WriteByte((byte)(s.Y & 0xFF));
            stream.WriteByte((byte)((s.Y >> 8) & 0xFF));
            stream.WriteByte(s.Color);
            stream.WriteByte(s.Glowing ? (byte)1 : (byte)0);
            for (int line = 0; line < 4; line++)
            {
                var bytes = Encoding.UTF8.GetBytes(line < s.Lines.Length ? s.Lines[line] : "");
                int length = Math.Min(bytes.Length, MaxLineBytes);
                // Never cut a character in half: back up to where one begins
                while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80) length--;
                stream.WriteByte((byte)length);
                stream.Write(bytes, 0, length);
            }
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    public static List<SignText> Decode(string? header)
    {
        var result = new List<SignText>();
        if (string.IsNullOrEmpty(header)) return result;
        byte[] data;
        try { data = Convert.FromBase64String(header); }
        catch (FormatException) { return result; }
        if (data.Length < 1) return result;
        int count = data[0], at = 1;
        for (int i = 0; i < count; i++)
        {
            if (at + 6 > data.Length) break;
            int x = data[at], z = data[at + 1];
            int y = (short)(data[at + 2] | (data[at + 3] << 8));
            byte color = data[at + 4];
            bool glow = data[at + 5] != 0;
            at += 6;
            var lines = new string[4];
            bool ok = true;
            for (int line = 0; line < 4; line++)
            {
                if (at >= data.Length) { ok = false; break; }
                int length = data[at++];
                if (at + length > data.Length) { ok = false; break; }
                lines[line] = Encoding.UTF8.GetString(data, at, length);
                at += length;
            }
            if (!ok) break;
            result.Add(new SignText(x, y, z, lines, color, glow));
        }
        return result;
    }

    /// <summary>The colour a sign's writing is drawn in (r, g, b: 0..255): the dye's, darkened to two fifths as the game does, or the dye's own when it glows (glowing black is a warm white).</summary>
    public static (byte r, byte g, byte b) TextColour(byte dye, bool glowing)
    {
        var (r, g, b) = Dye[dye % 16];
        if (glowing) return dye == 15 ? ((byte)0xF0, (byte)0xEB, (byte)0xCC) : (r, g, b);
        return ((byte)(r * 4 / 10), (byte)(g * 4 / 10), (byte)(b * 4 / 10));
    }

    // The game's text colours of the dyes, in the order of BlockEntityKinds.Dyes
    private static readonly (byte r, byte g, byte b)[] Dye =
    {
        (0xF9, 0xFF, 0xFE), (0xF9, 0x80, 0x1D), (0xC7, 0x4E, 0xBD), (0x3A, 0xB3, 0xDA), (0xFE, 0xD8, 0x3D), (0x80, 0xC7, 0x1F), (0xF3, 0x8B, 0xAA), (0x47, 0x4F, 0x52),
        (0x9D, 0x9D, 0x97), (0x16, 0x9C, 0x9C), (0x89, 0x32, 0xB8), (0x3C, 0x44, 0xAA), (0x83, 0x54, 0x32), (0x5E, 0x7C, 0x16), (0xB0, 0x2E, 0x26), (0x1D, 0x1D, 0x21),
    };
}
