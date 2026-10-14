using System.Text.Json;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// One append-only list of block names for the whole server: a block's id is its index, forever. A chunk read from a region
/// file carries its own palette (its ids mean different blocks in different chunks), so before this the client had to remap
/// every chunk's ids. With one global id space the chunk's bytes go to the GPU as they arrive.
/// Id 0 is always "minecraft:air". Persisted, so an id handed to a client stays valid across restarts (a client keeps a
/// block-name table and its cached chunks).
/// </summary>
public sealed class GlobalBlockRegistry
{
    /// <summary>The ids are 12 bits on the GPU (<c>PackedQuad</c> type field), so the registry cannot hold more.</summary>
    public const int MaxBlocks = 4096;

    public const string Air = "minecraft:air";

    private readonly object _lock = new();
    private readonly List<string> _names = [Air];
    private readonly Dictionary<string, ushort> _ids = new(StringComparer.Ordinal) { [Air] = 0 };
    private readonly string _filePath;
    private readonly ILogger<GlobalBlockRegistry> _logger;

    public GlobalBlockRegistry(IConfiguration configuration, ILogger<GlobalBlockRegistry> logger)
    {
        _logger = logger;
        _filePath = configuration.GetValue<string>("BlockRegistry:FilePath") ?? "block-registry.json";
        Load();
    }

    /// <summary>How many names are registered (ids 0 .. Count-1 are valid). Only ever grows.</summary>
    public int Count
    {
        get { lock (_lock) return _names.Count; }
    }

    /// <summary>The names so far, index = id.</summary>
    public string[] Snapshot()
    {
        lock (_lock) return _names.ToArray();
    }

    /// <summary>The id of a block name, registering it (and saving) the first time it is seen.</summary>
    public ushort GetOrAdd(string name)
    {
        lock (_lock)
        {
            if (_ids.TryGetValue(name, out var id)) return id;
            if (_names.Count >= MaxBlocks)
                throw new InvalidOperationException($"Block registry is full ({MaxBlocks}); cannot register '{name}'.");
            id = (ushort)_names.Count;
            _names.Add(name);
            _ids[name] = id;
            Save();
            return id;
        }
    }

    /// <summary>
    /// A chunk's block ids in the global id space: <c>result[i]</c> = the global id of <c>chunk.Blocks[i]</c>'s block name.
    /// Same layout as the chunk (x + z*16 + y*256, 384 high). A fresh array; the cached chunk is untouched.
    /// </summary>
    public ushort[] ToGlobalIds(ChunkResult chunk)
    {
        var map = new ushort[chunk.Palette.Count];
        for (int i = 0; i < map.Length; i++) map[i] = GetOrAdd(chunk.Palette[i]);
        var src = chunk.Blocks;
        var dst = new ushort[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = map[src[i]];
        return dst;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_filePath));
            if (saved == null || saved.Length == 0) return;
            if (saved[0] != Air) throw new InvalidDataException($"id 0 is '{saved[0]}', must be '{Air}'");
            if (saved.Length > MaxBlocks) throw new InvalidDataException($"{saved.Length} names, max {MaxBlocks}");
            _names.Clear();
            _ids.Clear();
            foreach (var n in saved)
            {
                if (!_ids.TryAdd(n, (ushort)_names.Count)) throw new InvalidDataException($"duplicate name '{n}'");
                _names.Add(n);
            }
        }
        catch (Exception ex)
        {
            // A registry we cannot trust must not hand out ids that disagree with what clients cached: stop loudly.
            _logger.LogError(ex, "Block registry {Path} is unreadable", _filePath);
            throw;
        }
    }

    // Caller holds _lock. Write-then-move so a crash mid-write never leaves a truncated registry.
    private void Save()
    {
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_names));
        File.Move(tmp, _filePath, overwrite: true);
    }
}
