namespace AubsCraft.Live.Model.BlockEntities;

/// <summary>The blocks the game draws with a model of their own (a block entity renderer), not with a block's cube faces: the viewer draws them as boxes, like the creatures.</summary>
public enum BlockEntityKind : byte
{
    None = 0,
    Chest = 1,
    TrappedChest = 2,
    EnderChest = 3,
    Bed = 4,
    ShulkerBox = 5,
    Sign = 6,
    HangingSign = 7,
    Banner = 8,
    Skull = 9,
}

/// <summary>
/// A block that is drawn by a model, in its column: x and z 0..15 within the chunk, y the world height. <see cref="Orient"/> is how it faces in sixteenths of a turn from south
/// (0 south, 4 west, 8 north, 12 east; a sign, banner or head standing on the ground can sit between), or 16 up / 17 down for a shulker box. <see cref="Variant"/> and <see cref="Part"/>
/// depend on the kind: see <see cref="BlockEntityKinds"/> (a hanging sign's part: 0 hangs by two chains, 1 on a wall, 2 by a V of chains from the block above).
/// </summary>
public readonly record struct BlockEntitySpot(int X, int Y, int Z, BlockEntityKind Kind, byte Orient, byte Variant, byte Part);

/// <summary>Which blocks are block entities, what they are made of (a colour, a wood, a skull type), and how they face, from a block's name and its properties.</summary>
public static class BlockEntityKinds
{
    /// <summary>The dye colours in the game's order (the index is a bed's, banner's or shulker box's <see cref="BlockEntitySpot.Variant"/>).</summary>
    public static readonly string[] Dyes =
        ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];

    /// <summary>The signs' woods (a sign's <see cref="BlockEntitySpot.Variant"/>).</summary>
    public static readonly string[] Woods =
        ["oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry", "pale_oak", "bamboo", "crimson", "warped"];

    /// <summary>The heads' kinds (a skull's <see cref="BlockEntitySpot.Variant"/>).</summary>
    public static readonly string[] Skulls = ["skeleton", "wither_skeleton", "zombie", "creeper", "piglin", "dragon", "player"];

    /// <summary>The shulker box without a dye.</summary>
    public const byte Undyed = 16;

    public const byte FacingUp = 16, FacingDown = 17;

    /// <summary>True for the blocks that are drawn by a model, by name alone (the viewer draws no cube for them).</summary>
    public static bool IsBlockEntityBlock(string name) => Classify(name, null) != null;

    /// <summary>What a block is and how it faces: null when it is no block entity. <paramref name="props"/> are the block's properties as the chunk stores them (strings).</summary>
    public static BlockEntitySpot? Classify(string name, IReadOnlyDictionary<string, object>? props)
    {
        int colon = name.IndexOf(':');
        if (colon < 0 || name[..colon] != "minecraft") return null;
        var n = name[(colon + 1)..];
        string Prop(string key) => props != null && props.TryGetValue(key, out var v) && v is string s ? s : "";
        byte Facing() => Prop("facing") switch { "south" => 0, "west" => 4, "north" => 8, "east" => 12, "up" => FacingUp, "down" => FacingDown, _ => 0 };
        byte Rotation() => byte.TryParse(Prop("rotation"), out var r) ? (byte)(r & 15) : (byte)0;
        BlockEntitySpot At(BlockEntityKind kind, byte orient, byte variant = 0, byte part = 0) => new(0, 0, 0, kind, orient, variant, part);

        switch (n)
        {
            case "chest": return At(BlockEntityKind.Chest, Facing(), Prop("type") switch { "left" => 1, "right" => 2, _ => 0 });
            case "trapped_chest": return At(BlockEntityKind.TrappedChest, Facing(), Prop("type") switch { "left" => 1, "right" => 2, _ => 0 });
            case "ender_chest": return At(BlockEntityKind.EnderChest, Facing());
            case "shulker_box": return At(BlockEntityKind.ShulkerBox, props == null ? FacingUp : Facing(), Undyed);
        }
        if (n.EndsWith("_shulker_box", StringComparison.Ordinal) && DyeOf(n[..^12]) is { } shulker) return At(BlockEntityKind.ShulkerBox, props == null ? FacingUp : Facing(), shulker);
        if (n.EndsWith("_bed", StringComparison.Ordinal) && DyeOf(n[..^4]) is { } bed) return At(BlockEntityKind.Bed, Facing(), bed, (byte)(Prop("part") == "head" ? 1 : 0));
        if (n.EndsWith("_wall_banner", StringComparison.Ordinal) && DyeOf(n[..^12]) is { } wallBanner) return At(BlockEntityKind.Banner, Facing(), wallBanner, 1);
        if (n.EndsWith("_banner", StringComparison.Ordinal) && DyeOf(n[..^7]) is { } banner) return At(BlockEntityKind.Banner, Rotation(), banner, 0);
        if (n.EndsWith("_wall_hanging_sign", StringComparison.Ordinal) && WoodOf(n[..^18]) is { } wallHanging) return At(BlockEntityKind.HangingSign, Facing(), wallHanging, 1);
        if (n.EndsWith("_hanging_sign", StringComparison.Ordinal) && WoodOf(n[..^13]) is { } hanging) return At(BlockEntityKind.HangingSign, Rotation(), hanging, (byte)(Prop("attached") == "true" ? 2 : 0));
        if (n.EndsWith("_wall_sign", StringComparison.Ordinal) && WoodOf(n[..^10]) is { } wallSign) return At(BlockEntityKind.Sign, Facing(), wallSign, 1);
        if (n.EndsWith("_sign", StringComparison.Ordinal) && WoodOf(n[..^5]) is { } sign) return At(BlockEntityKind.Sign, Rotation(), sign, 0);
        // Heads: skeleton_skull, wither_skeleton_skull, zombie_head, creeper_head, piglin_head, dragon_head, player_head, and the wall ones
        foreach (var (suffix, kind) in new[] { ("_wall_skull", 0), ("_wall_head", 0), ("_skull", 1), ("_head", 1) })
        {
            if (!n.EndsWith(suffix, StringComparison.Ordinal)) continue;
            int skull = Array.IndexOf(Skulls, n[..^suffix.Length]);
            if (skull < 0) continue;
            return At(BlockEntityKind.Skull, kind == 0 ? Facing() : Rotation(), (byte)skull, (byte)(kind == 0 ? 1 : 0));
        }
        return null;
    }

    private static byte? DyeOf(string colour) { int i = Array.IndexOf(Dyes, colour); return i < 0 ? null : (byte)i; }
    private static byte? WoodOf(string wood) { int i = Array.IndexOf(Woods, wood); return i < 0 ? null : (byte)i; }
}

/// <summary>
/// The block entities of one column as a short text for a response header (<c>X-BlockEntities</c>): base64 of seven bytes each - x and z in one byte, the world y + 64 in two
/// (little endian), the kind, the orientation, the variant, the part. A column is cut at <see cref="MaxPerColumn"/> (a storeroom of chests must not make a header too big to send).
/// </summary>
public static class BlockEntityCodec
{
    public const int MaxPerColumn = 500;
    public const int BaseY = -64;
    private const int Bytes = 7;

    public static string Encode(IReadOnlyList<BlockEntitySpot> spots)
    {
        int n = Math.Min(spots.Count, MaxPerColumn);
        var bytes = new byte[n * Bytes];
        for (int i = 0; i < n; i++)
        {
            var s = spots[i];
            int y = s.Y - BaseY, o = i * Bytes;
            bytes[o] = (byte)(((s.X & 15) << 4) | (s.Z & 15));
            bytes[o + 1] = (byte)(y & 255);
            bytes[o + 2] = (byte)((y >> 8) & 255);
            bytes[o + 3] = (byte)s.Kind;
            bytes[o + 4] = s.Orient;
            bytes[o + 5] = s.Variant;
            bytes[o + 6] = s.Part;
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The spots of <paramref name="text"/>; anything that is not a whole number of them (a damaged header) is none at all.</summary>
    public static List<BlockEntitySpot> Decode(string? text)
    {
        var spots = new List<BlockEntitySpot>();
        if (string.IsNullOrEmpty(text)) return spots;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException) { return spots; }
        if (bytes.Length % Bytes != 0) return spots;
        for (int o = 0; o < bytes.Length; o += Bytes)
        {
            var kind = (BlockEntityKind)bytes[o + 3];
            if (kind == BlockEntityKind.None || !Enum.IsDefined(kind)) continue;
            spots.Add(new BlockEntitySpot(bytes[o] >> 4, (bytes[o + 1] | bytes[o + 2] << 8) + BaseY, bytes[o] & 15, kind, bytes[o + 4], bytes[o + 5], bytes[o + 6]));
        }
        return spots;
    }
}
