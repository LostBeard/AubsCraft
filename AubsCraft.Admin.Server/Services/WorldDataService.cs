using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Provides access to ONE server's Minecraft world data by reading region files from the server filesystem.
/// Keeps the chunks last asked for in memory, up to <see cref="MaxCachedChunks"/>, and every chunk's heightmap (the world stream reads every chunk there is; a parsed chunk is 200 KB and a heightmap 3).
/// </summary>
public sealed class WorldDataService
{
    private readonly ServerDefinition _server;
    private readonly ILogger<WorldDataService> _logger;
    private sealed class Cached(ChunkResult chunk, long used)
    {
        public readonly ChunkResult Chunk = chunk;
        public long Used = used;
    }

    private readonly ConcurrentDictionary<(int, int), Cached> _chunkCache = new();
    private readonly ConcurrentDictionary<(int, int), HeightmapResult> _heightmaps = new();
    // What each chunk the panel has read last looked like, for one that has left the cache: a chunk saved again unchanged sends no viewer after it
    private readonly ConcurrentDictionary<(int, int), long> _contentHashes = new();
    private long _useClock;
    private int _trimming;

    /// <summary>
    /// The most parsed chunks held. A world of 15,000 chunks all held was 3 GB in the panel (the world stream reads every one for its heightmap); a viewer shows about 800 columns in full.
    /// </summary>
    public int MaxCachedChunks { get; set; } = 2048;

    /// <summary>Parsed chunks held now.</summary>
    public int CachedChunkCount => _chunkCache.Count;

    private void Keep((int, int) key, ChunkResult chunk)
    {
        _chunkCache[key] = new Cached(chunk, Interlocked.Increment(ref _useClock));
        _contentHashes[key] = ChunkDiff.ContentHash(chunk);
        if (_chunkCache.Count <= MaxCachedChunks || Interlocked.Exchange(ref _trimming, 1) != 0) return;
        try
        {
            // The longest unused go, a tenth at a time so this is not done for every chunk read. What the running server sent stays: it is newer than the file
            int target = MaxCachedChunks - MaxCachedChunks / 10;
            var oldest = _chunkCache.Where(kv => !_liveLight.ContainsKey(kv.Key)).OrderBy(kv => kv.Value.Used).Select(kv => kv.Key).Take(Math.Max(_chunkCache.Count - target, 0)).ToList();
            foreach (var old in oldest) _chunkCache.TryRemove(old, out _);
        }
        finally { Volatile.Write(ref _trimming, 0); }
    }

    // Whatever was known of a chunk's look goes when the chunk changes; its hash stays until the new look is known, to tell whether it did
    private void Forget((int, int) key)
    {
        _chunkCache.TryRemove(key, out _);
        _heightmaps.TryRemove(key, out _);
    }
    private static readonly Regex RegionFilePattern = new(@"r\.(-?\d+)\.(-?\d+)\.mca", RegexOptions.Compiled);

    public WorldDataService(ServerDefinition server, ILogger<WorldDataService> logger)
    {
        _server = server;
        _logger = logger;
    }

    /// <summary>The overworld folder (server.properties level-name), resolved per call so a world reset is picked up.</summary>
    private string _worldPath => _server.WorldPath;

    /// <summary>
    /// Lists all region coordinates that exist in the world.
    /// Each region is 32x32 chunks (512x512 blocks).
    /// </summary>
    public List<RegionInfo> GetRegions()
    {
        var regionDir = Path.Combine(_worldPath, "region");
        if (!Directory.Exists(regionDir))
            return [];

        var regions = new List<RegionInfo>();
        foreach (var file in Directory.GetFiles(regionDir, "r.*.mca"))
        {
            var match = RegionFilePattern.Match(Path.GetFileName(file));
            if (match.Success)
            {
                var rx = int.Parse(match.Groups[1].Value);
                var rz = int.Parse(match.Groups[2].Value);
                var info = new FileInfo(file);
                regions.Add(new RegionInfo(rx, rz, info.Length));
            }
        }
        return regions.OrderBy(r => r.X).ThenBy(r => r.Z).ToList();
    }

    /// <summary>
    /// Gets parsed chunk data for a specific chunk coordinate.
    /// Returns null if the chunk doesn't exist.
    /// </summary>
    public ChunkResult? GetChunk(int chunkX, int chunkZ)
    {
        if (_chunkCache.TryGetValue((chunkX, chunkZ), out var cached))
        {
            cached.Used = Interlocked.Increment(ref _useClock);
            return cached.Chunk;
        }
        // Known to be a stub, and the file has not changed since (ScanForChanges forgets it when it does)
        if (_stubs.ContainsKey((chunkX, chunkZ)))
            return null;

        var regionPath = RegionPathOf(chunkX, chunkZ);
        if (!File.Exists(regionPath))
            return null;

        try
        {
            long changesBefore = Volatile.Read(ref _changesApplied);
            var result = RegionReader.ReadChunk(regionPath, chunkX & 31, chunkZ & 31);
            // A change was applied while this read went on: what was read may be older than it, so it is handed out but not kept
            if (Volatile.Read(ref _changesApplied) != changesBefore) return result is { GenerationStub: true } ? null : result;
            _unreadable.TryRemove((chunkX, chunkZ), out _);
            // A chunk the game has only begun to generate is not terrain: it is not served, so a viewer never takes it for air and it never
            // sits in the cache as an empty column after the game has finished it (TJ 2026-11-04: whole columns missing from finished terrain)
            if (result is { GenerationStub: true })
            {
                _stubs[(chunkX, chunkZ)] = 0;
                return null;
            }
            if (result != null)
                Keep((chunkX, chunkZ), result);
            return result;
        }
        catch (Exception ex)
        {
            // Often only the moment: the game was writing that part of the file. ScanForChanges tries it again and tells the viewers: without that the
            // chunk was skipped for good, in the world stream too, and a hole stayed there until the panel restarted.
            _unreadable[(chunkX, chunkZ)] = 0;
            _logger.LogWarning("Could not read chunk ({ChunkX}, {ChunkZ}) yet: {Message}", chunkX, chunkZ, ex.Message);
            return null;
        }
    }

    private string RegionPathOf(int chunkX, int chunkZ) => Path.Combine(_worldPath, "region", $"r.{chunkX >> 5}.{chunkZ >> 5}.mca");

    // ---- the files change under the panel: the running server saves chunks, and generates new ones, all the time ----

    private sealed record RegionSeen(DateTime WriteTime, long Length, DateTime CheckedAt, byte[] Header);

    /// <summary>How long a region file whose time and size say nothing changed goes without its header being looked at: a network drive's file times can lag.</summary>
    private static readonly TimeSpan HeaderCheckEvery = TimeSpan.FromSeconds(60);
    /// <summary>The coarsest clock a file's time may come from (FAT's is 2 s; a network drive's is its own).</summary>
    private static readonly TimeSpan WriteTimeTick = TimeSpan.FromSeconds(2);

    private readonly Dictionary<(int Rx, int Rz), RegionSeen> _regionsSeen = new();
    private readonly ConcurrentDictionary<(int, int), byte> _stubs = new();
    private readonly ConcurrentDictionary<(int, int), byte> _unreadable = new();
    private bool _firstScanDone;
    private long _changesApplied;
    private readonly object _scanLock = new();

    /// <summary>
    /// Compare the region files with what was last seen and bring what the panel holds up to date: a chunk the game wrote since is read again. <paramref name="onChanged"/> gets, in batches, the chunks whose
    /// blocks are not what the viewers were last given (with the sides that differ: 1 -X, 2 +X, 4 -Z, 8 +Z). The first call only learns how things are. Runs on one thread at a time; the
    /// viewers' requests go on while it does.
    /// </summary>
    /// <param name="onChanged">Null when nobody is watching: what changed is only forgotten, and read when it is next asked for.</param>
    public void ScanForChanges(Action<IReadOnlyList<(int Cx, int Cz, int Border)>>? onChanged)
    {
        lock (_scanLock)
        {
            var regionDir = Path.Combine(_worldPath, "region");
            if (!Directory.Exists(regionDir)) return;
            var changed = new List<(int Cx, int Cz, int Border)>();
            void Flush()
            {
                if (changed.Count == 0) return;
                onChanged?.Invoke(changed.ToArray());
                changed.Clear();
            }

            var now = DateTime.UtcNow;
            foreach (var file in Directory.GetFiles(regionDir, "r.*.mca"))
            {
                var match = RegionFilePattern.Match(Path.GetFileName(file));
                if (!match.Success) continue;
                var key = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                try
                {
                    var info = new FileInfo(file);
                    _regionsSeen.TryGetValue(key, out var seen);
                    // Same time and size says nothing changed only if the header was read well after that time: a write in the same tick of the clock as our read leaves both the same
                    bool settledWhenRead = seen != null && seen.CheckedAt - seen.WriteTime > WriteTimeTick;
                    if (settledWhenRead && seen!.WriteTime == info.LastWriteTimeUtc && seen.Length == info.Length && now - seen.CheckedAt < HeaderCheckEvery) continue;
                    var header = RegionReader.ReadHeader(file);
                    if (header == null) continue;   // being created: next time
                    for (int i = 0; i < 1024; i++)
                    {
                        var place = header.AsSpan(i * 4, 4);
                        var stamp = header.AsSpan(4096 + i * 4, 4);
                        if (seen != null ? place.SequenceEqual(seen.Header.AsSpan(i * 4, 4)) && stamp.SequenceEqual(seen.Header.AsSpan(4096 + i * 4, 4))
                                         : !_firstScanDone ? true : place.SequenceEqual(new byte[4])) continue;
                        int cx = key.Item1 * 32 + (i & 31), cz = key.Item2 * 32 + (i >> 5);
                        FileChunkChanged(cx, cz, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(stamp), file, changed, onChanged != null);
                        if (changed.Count >= 256) Flush();
                    }
                    _regionsSeen[key] = new RegionSeen(info.LastWriteTimeUtc, info.Length, now, header);
                }
                catch (IOException) { /* the game has it at this moment: next time */ }
                catch (UnauthorizedAccessException) { }
            }

            // What could not be read when it was asked for (the world stream skipped it, so no viewer has it)
            foreach (var (cx, cz) in _unreadable.Keys.ToArray())
                try
                {
                    var path = RegionPathOf(cx, cz);
                    var chunk = File.Exists(path) ? RegionReader.ReadChunk(path, cx & 31, cz & 31) : null;
                    _unreadable.TryRemove((cx, cz), out _);
                    if (chunk is { GenerationStub: true }) { _stubs[(cx, cz)] = 0; continue; }
                    if (chunk == null) continue;
                    Keep((cx, cz), chunk);
                    changed.Add((cx, cz, 15));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug("Chunk ({ChunkX}, {ChunkZ}) still cannot be read: {Message}", cx, cz, ex.Message);
                }

            Flush();
            _firstScanDone = true;
        }
    }

    // The file's copy of a chunk is new: what the panel holds of it goes, unless what the running server sent is newer still
    private void FileChunkChanged(int cx, int cz, uint stamp, string regionPath, List<(int Cx, int Cz, int Border)> changed, bool watched)
    {
        var key = (cx, cz);
        Interlocked.Increment(ref _changesApplied);
        _stubs.TryRemove(key, out _);
        if (_liveLight.TryGetValue(key, out var live))
        {
            if (DateTimeOffset.FromUnixTimeSeconds(stamp) < new DateTimeOffset(live.At).AddSeconds(-1)) return;
            _liveLight.TryRemove(key, out _);   // the file has caught up
        }
        _chunkCache.TryGetValue(key, out var held);
        var old = held?.Chunk;
        _heightmaps.TryRemove(key, out _);
        if (!watched)
        {
            Forget(key);
            return;
        }

        ChunkResult? fresh;
        try { fresh = RegionReader.ReadChunk(regionPath, cx & 31, cz & 31); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Forget(key);
            _unreadable[key] = 0;
            _logger.LogDebug("Chunk ({ChunkX}, {ChunkZ}) changed and cannot be read yet: {Message}", cx, cz, ex.Message);
            return;
        }
        if (fresh == null || fresh.GenerationStub)
        {
            if (fresh != null) _stubs[key] = 0;
            Forget(key);
            if (old != null || _contentHashes.TryRemove(key, out _)) changed.Add((cx, cz, 15));
            return;
        }
        // Saved again with nothing changed that a viewer shows (the game rewrites a chunk for its creatures, its clock...): no viewer is sent after it
        int border = old != null ? ChunkDiff.BorderOfChange(old, fresh)
            : _contentHashes.TryGetValue(key, out var known) && known == ChunkDiff.ContentHash(fresh) ? -1
            : 15;   // new, or out of the cache and changed: which sides is not known any more
        Keep(key, fresh);
        if (border >= 0) changed.Add((cx, cz, border));
    }

    /// <summary>
    /// The light of a chunk column (<see cref="RegionReader.LightOf"/>), read from the region file when asked (it is not kept: a column's light is a hundred kilobytes and only the columns
    /// near a camera are asked for). Null when the chunk is not there or its light was never worked out.
    /// </summary>
    public byte[]? GetChunkLight(int chunkX, int chunkZ)
    {
        if (_liveLight.TryGetValue((chunkX, chunkZ), out var live)) return live.Light;
        var regionPath = Path.Combine(_worldPath, "region", $"r.{chunkX >> 5}.{chunkZ >> 5}.mca");
        if (!File.Exists(regionPath)) return null;
        try { return RegionReader.ReadChunkLight(regionPath, chunkX & 31, chunkZ & 31); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the light of chunk ({ChunkX}, {ChunkZ})", chunkX, chunkZ);
            return null;
        }
    }

    // ---- chunks the running server has sent (they are newer than the files) ----

    /// <summary>The most live chunks kept: a chunk is 300 KB with its light. The oldest go back to being read from the file, which the server has saved by then.</summary>
    public const int MaxLiveChunks = 256;
    private sealed record LiveChunk(byte[] Light, DateTime At);
    private readonly ConcurrentDictionary<(int, int), LiveChunk> _liveLight = new();

    /// <summary>A chunk as the running server has it now: served instead of the region file's until it is dropped.</summary>
    public void SetLiveChunk(int chunkX, int chunkZ, ChunkResult chunk, byte[] light)
    {
        _liveLight[(chunkX, chunkZ)] = new LiveChunk(light, DateTime.UtcNow);
        _heightmaps.TryRemove((chunkX, chunkZ), out _);
        Keep((chunkX, chunkZ), chunk);
        while (_liveLight.Count > MaxLiveChunks)
        {
            var oldest = _liveLight.MinBy(kv => kv.Value.At).Key;
            if (!_liveLight.TryRemove(oldest, out _)) break;
            Forget(oldest);   // the next read parses the file
        }
    }

    /// <summary>How many chunks the running server has sent that are held now.</summary>
    public int LiveChunkCount => _liveLight.Count;

    /// <summary>
    /// Lists all populated chunk coordinates across all regions.
    /// </summary>
    public List<ChunkCoord> GetPopulatedChunks()
    {
        var regionDir = Path.Combine(_worldPath, "region");
        if (!Directory.Exists(regionDir)) return [];

        var coords = new List<ChunkCoord>();
        foreach (var file in Directory.GetFiles(regionDir, "r.*.mca"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                Path.GetFileName(file), @"r\.(-?\d+)\.(-?\d+)\.mca");
            if (!match.Success) continue;

            var rx = int.Parse(match.Groups[1].Value);
            var rz = int.Parse(match.Groups[2].Value);

            try
            {
                var chunks = RegionReader.ListChunks(file);
                foreach (var (lx, lz) in chunks)
                    coords.Add(new ChunkCoord(rx * 32 + lx, rz * 32 + lz));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list chunks in {File}", file);
            }
        }
        return coords;
    }

    /// <summary>
    /// Gets a lightweight heightmap for a chunk - just the top block ID and Y per column.
    /// Returns 256 entries (16x16), each with the block ID and height of the topmost non-air block.
    /// </summary>
    public HeightmapResult? GetHeightmap(int chunkX, int chunkZ)
    {
        if (_heightmaps.TryGetValue((chunkX, chunkZ), out var known)) return known;
        long changesBefore = Volatile.Read(ref _changesApplied);
        var chunk = GetChunk(chunkX, chunkZ);
        if (chunk == null) return null;

        var heights = new int[256];
        var blockIds = new ushort[256];
        // Seabed: for water columns, the first solid block below the water surface
        var seabedHeights = new int[256];
        var seabedBlockIds = new ushort[256];

        // Pre-identify water and plant palette entries for fast lookup
        var waterIds = new HashSet<ushort>();
        var plantIds = new HashSet<ushort>();
        for (int i = 0; i < chunk.Palette.Count; i++)
        {
            var name = BlockStateNamer.NameOf(chunk.Palette[i]);
            if (name is "minecraft:water" or "minecraft:flowing_water")
                waterIds.Add((ushort)i);
            if (IsPlantBlock(name))
                plantIds.Add((ushort)i);
        }

        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int col = x + z * 16;
            seabedHeights[col] = -64; // default: no seabed

            // Scan from top (383) down to find first non-air, non-plant block
            for (int y = 383; y >= 0; y--)
            {
                var blockId = chunk.Blocks[x + z * 16 + y * 256];
                if (blockId != 0 && !plantIds.Contains(blockId))
                {
                    heights[col] = y - 64; // Convert to Minecraft Y
                    blockIds[col] = blockId;

                    // If top block is water, scan down for the seabed (first solid block)
                    if (waterIds.Contains(blockId))
                    {
                        for (int sy = y - 1; sy >= 0; sy--)
                        {
                            var sbId = chunk.Blocks[x + z * 16 + sy * 256];
                            if (sbId != 0 && !waterIds.Contains(sbId))
                            {
                                seabedHeights[col] = sy - 64;
                                seabedBlockIds[col] = sbId;
                                break;
                            }
                        }
                    }
                    break;
                }
            }
        }

        var heightmap = new HeightmapResult(heights, blockIds, seabedHeights, seabedBlockIds, chunk.Palette, chunk.Biomes);
        // Not kept if a change came while it was worked out: it may be from before that
        if (Volatile.Read(ref _changesApplied) == changesBefore) _heightmaps[(chunkX, chunkZ)] = heightmap;
        return heightmap;
    }

    /// <summary>
    /// Where a first-time map visitor should start (X, Z), so the camera opens over the world and not over
    /// (0, 0), which may be empty: a world's spawn can be hundreds of blocks away. In order:
    ///   1. the spawn in level.dat (Data.SpawnX/SpawnZ; newer versions: Data.spawn.pos) - when readable
    ///      (the original server's level.dat is owner-only, 0600 minecraft);
    ///   2. BlueMap's map settings startPos (BlueMap takes it from the spawn) - when BlueMap is installed;
    ///   3. the centre of the generated chunk nearest the origin - every world with any terrain has one.
    /// Known = false only for a world with no saved chunks at all.
    /// </summary>
    public WorldSpawnDto GetSpawn()
    {
        try
        {
            var levelDat = Path.Combine(_worldPath, "level.dat");
            if (File.Exists(levelDat))
            {
                using var fs = new FileStream(levelDat, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var data = NbtReader.ReadGzip(fs).GetCompound("Data");
                if (data != null && data.ContainsKey("SpawnX"))
                    return new WorldSpawnDto(data.GetInt("SpawnX"), data.GetInt("SpawnZ"), true);
                if (data?.GetCompound("spawn") is { } spawn && spawn.Get<int[]>("pos") is { Length: >= 3 } pos)
                    return new WorldSpawnDto(pos[0], pos[2], true);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidCastException)
        {
            _logger.LogWarning(ex, "Could not read the spawn from level.dat");
        }

        try
        {
            var settings = Path.Combine(_server.Path, "bluemap", "web", "maps", "world", "settings.json");
            if (File.Exists(settings))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("startPos", out var p) && p.GetArrayLength() >= 2)
                    return new WorldSpawnDto(p[0].GetInt32(), p[1].GetInt32(), true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Could not read the spawn from BlueMap settings");
        }

        // Nearest first, skipping chunks with no blocks: a region file also stores ungenerated "proto" chunks
        // around the generated area, and opening the map over one shows only sky.
        foreach (var c in GetPopulatedChunks().OrderBy(c => (long)c.X * c.X + (long)c.Z * c.Z))
        {
            if (GetChunk(c.X, c.Z) is { } chunk && chunk.Palette.Any(n => BlockStateNamer.NameOf(n) is not ("minecraft:air" or "minecraft:cave_air" or "minecraft:void_air")))
                return new WorldSpawnDto(c.X * 16 + 8, c.Z * 16 + 8, true);
        }
        return new WorldSpawnDto(0, 0, false);
    }

    private static bool IsPlantBlock(string name) => name is
        "minecraft:short_grass" or "minecraft:tall_grass" or "minecraft:grass" or
        "minecraft:fern" or "minecraft:large_fern" or
        "minecraft:dandelion" or "minecraft:poppy" or "minecraft:cornflower" or
        "minecraft:azure_bluet" or "minecraft:orange_tulip" or "minecraft:red_tulip" or
        "minecraft:pink_tulip" or "minecraft:white_tulip" or "minecraft:oxeye_daisy" or
        "minecraft:lily_of_the_valley" or "minecraft:rose_bush" or "minecraft:lilac" or
        "minecraft:peony" or "minecraft:sunflower" or "minecraft:wildflowers" or
        "minecraft:dead_bush" or "minecraft:sweet_berry_bush" or
        "minecraft:sugar_cane" or "minecraft:bamboo";

    public void ClearCache()
    {
        int count = _chunkCache.Count;
        _chunkCache.Clear();
        _heightmaps.Clear();
        _logger.LogInformation("World data cache cleared ({Count} entries)", count);
    }
}

public record RegionInfo(int X, int Z, long FileSize);
public record ChunkCoord(int X, int Z);
public record HeightmapResult(
    int[] Heights, ushort[] BlockIds,
    int[] SeabedHeights, ushort[] SeabedBlockIds,
    List<string> Palette, byte[]? Biomes = null);
