using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Provides access to ONE server's Minecraft world data by reading region files from the server filesystem.
/// Caches parsed chunk data in memory.
/// </summary>
public sealed class WorldDataService
{
    private readonly ServerDefinition _server;
    private readonly ILogger<WorldDataService> _logger;
    private readonly ConcurrentDictionary<(int, int), ChunkResult> _chunkCache = new();
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
            return cached;

        var regionX = chunkX >> 5; // divide by 32
        var regionZ = chunkZ >> 5;
        var localX = chunkX & 31;
        var localZ = chunkZ & 31;

        var regionPath = Path.Combine(_worldPath, "region", $"r.{regionX}.{regionZ}.mca");
        if (!File.Exists(regionPath))
            return null;

        try
        {
            var result = RegionReader.ReadChunk(regionPath, localX, localZ);
            if (result != null)
                _chunkCache[(chunkX, chunkZ)] = result;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read chunk ({ChunkX}, {ChunkZ})", chunkX, chunkZ);
            return null;
        }
    }

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
            var name = chunk.Palette[i];
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

        return new HeightmapResult(heights, blockIds, seabedHeights, seabedBlockIds, chunk.Palette);
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
            if (GetChunk(c.X, c.Z) is { } chunk && chunk.Palette.Any(n => n is not ("minecraft:air" or "minecraft:cave_air" or "minecraft:void_air")))
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
        _chunkCache.Clear();
        _logger.LogInformation("World data cache cleared ({Count} entries)", _chunkCache.Count);
    }
}

public record RegionInfo(int X, int Z, long FileSize);
public record ChunkCoord(int X, int Z);
public record HeightmapResult(
    int[] Heights, ushort[] BlockIds,
    int[] SeabedHeights, ushort[] SeabedBlockIds,
    List<string> Palette);
