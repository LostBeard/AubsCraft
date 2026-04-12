using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Provides access to Minecraft world data by reading region files from the server filesystem.
/// Caches parsed chunk data in memory.
/// </summary>
public sealed class WorldDataService
{
    private readonly string _worldPath;
    private readonly ILogger<WorldDataService> _logger;
    private readonly ConcurrentDictionary<(int, int), ChunkResult> _chunkCache = new();
    private static readonly Regex RegionFilePattern = new(@"r\.(-?\d+)\.(-?\d+)\.mca", RegexOptions.Compiled);

    public WorldDataService(IConfiguration configuration, ILogger<WorldDataService> logger)
    {
        _worldPath = Path.Combine(
            configuration["Minecraft:ServerPath"] ?? "/opt/minecraft/server",
            "world");
        _logger = logger;
    }

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

        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int col = x + z * 16;
            // Scan from top (383) down to find first non-air block
            for (int y = 383; y >= 0; y--)
            {
                var blockId = chunk.Blocks[x + z * 16 + y * 256];
                if (blockId != 0)
                {
                    heights[col] = y - 64; // Convert to Minecraft Y
                    blockIds[col] = blockId;
                    break;
                }
            }
        }

        return new HeightmapResult(heights, blockIds, chunk.Palette);
    }

    /// <summary>
    /// Clears the chunk cache (call after world save or reload).
    /// </summary>
    public void ClearCache()
    {
        _chunkCache.Clear();
        _logger.LogInformation("World data cache cleared ({Count} entries)", _chunkCache.Count);
    }
}

public record RegionInfo(int X, int Z, long FileSize);
public record ChunkCoord(int X, int Z);
public record HeightmapResult(int[] Heights, ushort[] BlockIds, List<string> Palette);
