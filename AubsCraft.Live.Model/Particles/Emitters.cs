namespace AubsCraft.Live.Model.Particles;

/// <summary>What a block gives off: the viewer maps each kind to the particles it shows (smoke, flames, sparks).</summary>
public enum EmitterKind : byte
{
    None = 0,
    Campfire = 1,
    SoulCampfire = 2,
    Furnace = 3,
    Fire = 4,
    SoulFire = 5,
    Torch = 6,
    SoulTorch = 7,
    RedstoneTorch = 8,
    Candle = 9,
}

/// <summary>A block that gives off particles, in its column: x and z 0..15 within the chunk, y the block's world height.</summary>
public readonly record struct EmitterSpot(int X, int Y, int Z, EmitterKind Kind);

/// <summary>Which blocks give off particles, from the block names the server and the viewer share (<c>minecraft:campfire[facing=north,lit=true]</c>).</summary>
public static class EmitterClassifier
{
    public static EmitterKind Classify(string key)
    {
        int bracket = key.IndexOf('[');
        var name = bracket < 0 ? key : key[..bracket];
        if (name.Length < 11 || !name.StartsWith("minecraft:", StringComparison.Ordinal)) return EmitterKind.None;
        bool Lit(bool whenAbsent)
        {
            int i = key.IndexOf("lit=", StringComparison.Ordinal);
            if (i < 0) return whenAbsent;
            return key.AsSpan(i + 4).StartsWith("true", StringComparison.Ordinal);
        }
        switch (name)
        {
            case "minecraft:campfire": return Lit(true) ? EmitterKind.Campfire : EmitterKind.None;
            case "minecraft:soul_campfire": return Lit(true) ? EmitterKind.SoulCampfire : EmitterKind.None;
            case "minecraft:furnace":
            case "minecraft:blast_furnace":
            case "minecraft:smoker": return Lit(false) ? EmitterKind.Furnace : EmitterKind.None;
            case "minecraft:fire": return EmitterKind.Fire;
            case "minecraft:soul_fire": return EmitterKind.SoulFire;
            case "minecraft:torch":
            case "minecraft:wall_torch": return EmitterKind.Torch;
            case "minecraft:soul_torch":
            case "minecraft:soul_wall_torch": return EmitterKind.SoulTorch;
            case "minecraft:redstone_torch":
            case "minecraft:redstone_wall_torch": return Lit(true) ? EmitterKind.RedstoneTorch : EmitterKind.None;
            case "minecraft:candle_cake": return Lit(false) ? EmitterKind.Candle : EmitterKind.None;
        }
        // Every candle colour: minecraft:white_candle ... minecraft:candle
        if (name.EndsWith("candle", StringComparison.Ordinal)) return Lit(false) ? EmitterKind.Candle : EmitterKind.None;
        return EmitterKind.None;
    }
}

/// <summary>
/// The emitters of one column as a short text for a response header (<c>X-Emitters</c>): base64 of four bytes each - x and z in one byte, the world y + 64 in two (little endian), the kind.
/// A column is cut at <see cref="MaxPerColumn"/> (a torch farm must not make a header that is too big to send).
/// </summary>
public static class EmitterCodec
{
    public const int MaxPerColumn = 400;
    public const int BaseY = -64;

    public static string Encode(IReadOnlyList<EmitterSpot> spots)
    {
        int n = Math.Min(spots.Count, MaxPerColumn);
        var bytes = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            var s = spots[i];
            int y = s.Y - BaseY;
            bytes[i * 4] = (byte)((s.X & 15) << 4 | (s.Z & 15));
            bytes[i * 4 + 1] = (byte)(y & 255);
            bytes[i * 4 + 2] = (byte)((y >> 8) & 255);
            bytes[i * 4 + 3] = (byte)s.Kind;
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The spots of <paramref name="text"/>; anything that is not a whole number of them (a damaged header) is none at all.</summary>
    public static List<EmitterSpot> Decode(string? text)
    {
        var spots = new List<EmitterSpot>();
        if (string.IsNullOrEmpty(text)) return spots;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException) { return spots; }
        if (bytes.Length % 4 != 0) return spots;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            var kind = (EmitterKind)bytes[i + 3];
            if (kind == EmitterKind.None || !Enum.IsDefined(kind)) continue;
            spots.Add(new EmitterSpot(bytes[i] >> 4, (bytes[i + 1] | bytes[i + 2] << 8) + BaseY, bytes[i] & 15, kind));
        }
        return spots;
    }
}
