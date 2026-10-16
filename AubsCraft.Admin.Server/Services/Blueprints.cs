using System.IO.Compression;
using System.Text;
using AubsCraft.Live.Model.Editing;

namespace AubsCraft.Admin.Server.Services;

/// <summary>A saved blueprint, in short: what the list shows and the ghost needs.</summary>
public sealed record BlueprintInfo(string Name, int Width, int Height, int Depth, int Blocks, string Author, DateTime CreatedUtc);

/// <summary>
/// A piece of the world kept to be put down again: a box of blocks (width = east-west, height, depth = north-south), each a palette entry (a block as the viewer names it, e.g.
/// <c>minecraft:oak_stairs[facing=north,half=bottom]</c>; entry 0 is air), laid out (y * Depth + z) * Width + x. Taken from the chunks the panel holds (so what is just built, and not yet saved by the
/// server, is in it), and pasted as an edit - turned, mirrored, or not.
/// </summary>
public sealed class Blueprint
{
    public const int MaxBlocks = 4_000_000;
    private static readonly byte[] Magic = "ABP1"u8.ToArray();

    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; }
    public string[] Palette { get; init; } = ["minecraft:air"];
    public ushort[] Blocks { get; init; } = [];

    /// <summary>Blocks that are not air.</summary>
    public int Count
    {
        get
        {
            var air = AirIds();
            int n = 0;
            foreach (var b in Blocks) if (!air[b]) n++;
            return n;
        }
    }

    public BlueprintInfo Info => new(Name, Width, Height, Depth, Count, Author, CreatedUtc);

    private bool[] AirIds()
    {
        var air = new bool[Palette.Length];
        for (int i = 0; i < Palette.Length; i++) air[i] = IsAir(Palette[i]);
        return air;
    }

    public static bool IsAir(string block) => BlockStateNamer.NameOf(block) is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air";

    /// <summary>
    /// The blocks of a box of the world (any two corners), from the chunks <paramref name="chunkAt"/> gives (null: not there, air). With <paramref name="trim"/> the box shrinks to the blocks that
    /// are not air - what a person selects is a rough box round a thing, and the thing is what is kept.
    /// </summary>
    public static Blueprint FromWorld(Func<int, int, ChunkResult?> chunkAt, int x1, int y1, int z1, int x2, int y2, int z2, string name, string author, bool trim = true)
    {
        int minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2), minY = Math.Max(Math.Min(y1, y2), WorldPicker.MinY), maxY = Math.Min(Math.Max(y1, y2), WorldPicker.MaxY), minZ = Math.Min(z1, z2), maxZ = Math.Max(z1, z2);
        long volume = (long)(maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);
        if (volume <= 0 || volume > MaxBlocks) throw new ArgumentException($"A blueprint holds at most {MaxBlocks:N0} blocks (this box has {volume:N0})");
        int w = maxX - minX + 1, h = maxY - minY + 1, d = maxZ - minZ + 1;

        var palette = new List<string> { "minecraft:air" };
        var index = new Dictionary<string, ushort> { ["minecraft:air"] = 0 };
        var blocks = new ushort[w * h * d];
        ChunkResult? chunk = null;
        int cachedCx = int.MinValue, cachedCz = int.MinValue;
        // The chunk's own palette ids to the blueprint's, as they come
        var map = new Dictionary<(int, int, ushort), ushort>();
        for (int y = 0; y < h; y++)
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                {
                    int wx = minX + x, wy = minY + y, wz = minZ + z;
                    int cx = wx >> 4, cz = wz >> 4;
                    if (cx != cachedCx || cz != cachedCz) { cachedCx = cx; cachedCz = cz; chunk = chunkAt(cx, cz); }
                    if (chunk == null) continue;
                    ushort local = chunk.Blocks[(wx & 15) + (wz & 15) * 16 + (wy - WorldPicker.MinY) * 256];
                    if (local == 0) continue;
                    if (!map.TryGetValue((cx, cz, local), out var id))
                    {
                        var block = chunk.Palette[local];
                        if (!index.TryGetValue(block, out id)) { id = (ushort)palette.Count; palette.Add(block); index[block] = id; }
                        map[(cx, cz, local)] = id;
                    }
                    blocks[(y * d + z) * w + x] = id;
                }

        var blueprint = new Blueprint { Name = name, Author = author, Width = w, Height = h, Depth = d, Palette = palette.ToArray(), Blocks = blocks };
        return trim ? blueprint.Trimmed() : blueprint;
    }

    /// <summary>The same blocks in the smallest box that holds the ones that are not air (an all-air blueprint is a single air block).</summary>
    public Blueprint Trimmed()
    {
        var air = AirIds();
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, y1 = -1, z1 = -1;
        for (int y = 0; y < Height; y++)
            for (int z = 0; z < Depth; z++)
                for (int x = 0; x < Width; x++)
                {
                    if (air[Blocks[(y * Depth + z) * Width + x]]) continue;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                    z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
                }
        if (x1 < 0) return new Blueprint { Name = Name, Author = Author, CreatedUtc = CreatedUtc, Width = 1, Height = 1, Depth = 1, Palette = ["minecraft:air"], Blocks = [0] };
        if (x0 == 0 && y0 == 0 && z0 == 0 && x1 == Width - 1 && y1 == Height - 1 && z1 == Depth - 1) return this;
        int w = x1 - x0 + 1, h = y1 - y0 + 1, d = z1 - z0 + 1;
        var blocks = new ushort[w * h * d];
        for (int y = 0; y < h; y++)
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                    blocks[(y * d + z) * w + x] = Blocks[((y + y0) * Depth + z + z0) * Width + x + x0];
        return new Blueprint { Name = Name, Author = Author, CreatedUtc = CreatedUtc, Width = w, Height = h, Depth = d, Palette = Palette, Blocks = blocks };
    }

    /// <summary>The size once turned (a quarter turn swaps width and depth).</summary>
    public (int Width, int Height, int Depth) SizeAfter(int quarterTurns) => ((quarterTurns % 2 + 2) % 2 == 0) ? (Width, Height, Depth) : (Depth, Height, Width);

    /// <summary>
    /// The edit that puts the blueprint down with its lowest north-west corner at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), mirrored and then turned. Air is left out unless
    /// <paramref name="withAir"/> (then the pasted box is cleared where the blueprint has air).
    /// </summary>
    public EditRequest ToPaste(int x, int y, int z, int quarterTurns = 0, bool mirrorX = false, bool mirrorZ = false, bool withAir = false, string? label = null)
    {
        var palette = new string[Palette.Length];
        for (int i = 0; i < palette.Length; i++) palette[i] = BlockTransform.Transform(Palette[i], quarterTurns, mirrorX, mirrorZ);
        var air = AirIds();
        var records = new List<byte>(Blocks.Length * 8);
        int newW = Width, newD = Depth;
        for (int by = 0; by < Height; by++)
            for (int bz = 0; bz < Depth; bz++)
                for (int bx = 0; bx < Width; bx++)
                {
                    ushort id = Blocks[(by * Depth + bz) * Width + bx];
                    if (air[id] && !withAir) continue;
                    var place = BlockTransform.Place(bx, bz, Width, Depth, quarterTurns, mirrorX, mirrorZ);
                    newW = place.Width; newD = place.Depth;
                    records.AddRange(BitConverter.GetBytes((short)place.X));
                    records.AddRange(BitConverter.GetBytes((short)by));
                    records.AddRange(BitConverter.GetBytes((short)place.Z));
                    records.AddRange(BitConverter.GetBytes(id));
                }
        var op = new EditOpDto { Op = "blocks", At = [x, y, z], Palette = palette, Data = Convert.ToBase64String(records.ToArray()) };
        if (records.Count == 0) throw new ArgumentException("There is nothing in that blueprint to put down");
        return new EditRequest { Label = label ?? "paste " + Name, Ops = [op] };
    }

    // ---- the file ----

    public byte[] ToBytes()
    {
        using var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(Name); w.Write(Author); w.Write(CreatedUtc.Ticks);
            w.Write((ushort)Width); w.Write((ushort)Height); w.Write((ushort)Depth);
            w.Write((ushort)Palette.Length);
            foreach (var p in Palette) w.Write(p);
            foreach (var b in Blocks) w.Write(b);
        }
        return raw.ToArray();
    }

    public static Blueprint FromBytes(byte[] bytes)
    {
        try
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var r = new BinaryReader(gz, Encoding.UTF8);
            if (!r.ReadBytes(4).SequenceEqual(Magic)) throw new InvalidDataException("Not a blueprint");
            string name = r.ReadString(), author = r.ReadString();
            var created = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            int w = r.ReadUInt16(), h = r.ReadUInt16(), d = r.ReadUInt16();
            if ((long)w * h * d > MaxBlocks) throw new InvalidDataException("A blueprint that large");
            var palette = new string[r.ReadUInt16()];
            for (int i = 0; i < palette.Length; i++) palette[i] = r.ReadString();
            if (palette.Length == 0) throw new InvalidDataException("No palette");
            var blocks = new ushort[w * h * d];
            for (int i = 0; i < blocks.Length; i++)
            {
                blocks[i] = r.ReadUInt16();
                if (blocks[i] >= palette.Length) throw new InvalidDataException("A block outside the palette");
            }
            return new Blueprint { Name = name, Author = author, CreatedUtc = created, Width = w, Height = h, Depth = d, Palette = palette, Blocks = blocks };
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException)
        {
            throw new InvalidDataException("Not a blueprint file: " + ex.Message, ex);
        }
    }
}

/// <summary>
/// The blueprints of each server, as files (<c>{Blueprints:Path}/{serverId}/{name}.abp</c>), and each admin's clipboard (kept in memory: the last copy, for pasting). A name is plain words; it is also what
/// the file is called, with anything else turned to an underscore, so no name can point outside the folder.
/// </summary>
public sealed class BlueprintStore
{
    public const int MaxNameLength = 64;
    public const string ClipboardName = "~clipboard";

    private readonly string _root;
    private readonly ILogger<BlueprintStore> _logger;
    private readonly Dictionary<(string server, string user), Blueprint> _clipboards = new();
    private readonly object _lock = new();

    public BlueprintStore(IConfiguration configuration, ILogger<BlueprintStore> logger)
    {
        _root = Path.GetFullPath(configuration.GetValue<string>("Blueprints:Path") ?? "blueprints");
        _logger = logger;
    }

    /// <summary>A name as it may be kept: trimmed, letters, digits, spaces and a few marks; null when nothing usable is left.</summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '\'' or '(' or ')') sb.Append(c);
        var clean = sb.ToString().Trim().Trim('.');
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    private static string ServerFolder(string serverId) => new string(serverId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    private static string FileName(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_').ToArray()).Trim().ToLowerInvariant() + ".abp";

    private string PathOf(string serverId, string name) => Path.Combine(_root, ServerFolder(serverId), FileName(name));

    public List<BlueprintInfo> List(string serverId)
    {
        var folder = Path.Combine(_root, ServerFolder(serverId));
        var list = new List<BlueprintInfo>();
        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.GetFiles(folder, "*.abp"))
        {
            try { list.Add(Blueprint.FromBytes(File.ReadAllBytes(file)).Info); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { _logger.LogWarning("Skipping unreadable blueprint {File}: {Message}", file, ex.Message); }
        }
        return list.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Blueprint? Load(string serverId, string name)
    {
        var clean = CleanName(name);
        if (clean == null) return null;
        var path = PathOf(serverId, clean);
        if (!File.Exists(path)) return null;
        try { return Blueprint.FromBytes(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { _logger.LogWarning("Unreadable blueprint {Path}: {Message}", path, ex.Message); return null; }
    }

    /// <summary>Keep a blueprint under its name (replacing one of that name).</summary>
    public void Save(string serverId, Blueprint blueprint)
    {
        var path = PathOf(serverId, blueprint.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, blueprint.ToBytes());
        File.Move(temp, path, overwrite: true);
    }

    public bool Delete(string serverId, string name)
    {
        var clean = CleanName(name);
        if (clean == null) return false;
        var path = PathOf(serverId, clean);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Give a blueprint a new name (false when there is none of the old name, or one of the new).</summary>
    public bool Rename(string serverId, string name, string newName)
    {
        var old = Load(serverId, name);
        var clean = CleanName(newName);
        if (old == null || clean == null || Load(serverId, clean) != null) return false;
        Save(serverId, new Blueprint { Name = clean, Author = old.Author, CreatedUtc = old.CreatedUtc, Width = old.Width, Height = old.Height, Depth = old.Depth, Palette = old.Palette, Blocks = old.Blocks });
        Delete(serverId, name);
        return true;
    }

    // ---- the clipboard ----

    public void SetClipboard(string serverId, string user, Blueprint blueprint)
    {
        lock (_lock) _clipboards[(serverId, user)] = blueprint;
    }

    public Blueprint? Clipboard(string serverId, string user)
    {
        lock (_lock) return _clipboards.GetValueOrDefault((serverId, user));
    }
}
