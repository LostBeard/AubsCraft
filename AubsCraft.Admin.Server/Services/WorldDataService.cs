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
    /// Clears the chunk cache (call after world save or reload).
    /// </summary>
    public void ClearCache()
    {
        _chunkCache.Clear();
        _logger.LogInformation("World data cache cleared ({Count} entries)", _chunkCache.Count);
    }
}

public record RegionInfo(int X, int Z, long FileSize);
