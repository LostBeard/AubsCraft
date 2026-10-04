using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// The PC mod pack for the servers that need client mods: a Modrinth pack (.mrpack) per Minecraft version holding
/// Fabric, Fabric API, Simple Voice Chat and every such server's client mods with their dependencies. The Modrinth
/// App (or Prism Launcher) imports it and downloads each mod itself from Modrinth's CDN, checking the sha1/sha512
/// listed here. One pack covers every modded server of that version, and the plain servers too.
/// See https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack
/// </summary>
public class ModpackService
{
    private readonly ServerRegistry _registry;
    private readonly AddonDownloader _downloader;
    private readonly ILogger<ModpackService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Key, byte[] Pack, DateTime Built)> _cache = [];

    /// <summary>Always in the pack: the mod API almost every mod needs, and voice chat (every AubsCraft server runs it).</summary>
    public static readonly string[] BaseMods = ["fabric-api", "simple-voice-chat"];

    public ModpackService(ServerRegistry registry, AddonDownloader downloader, ILogger<ModpackService> logger)
    {
        _registry = registry;
        _downloader = downloader;
        _logger = logger;
    }

    /// <summary>The servers each pack is for, by Minecraft version: Fabric servers with client mods.</summary>
    public List<ModpackInfo> Available() => ModdedServers()
        .GroupBy(s => s.GameVersion)
        .Select(g => new ModpackInfo(g.Key, FileName(g.Key), g.Select(s => s.Name).ToList(),
            g.SelectMany(s => s.ClientMods).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList()))
        .OrderByDescending(p => p.GameVersion).ToList();

    private IEnumerable<ServerDefinition> ModdedServers() =>
        _registry.All.Where(s => s.Loader is ServerLoader.Fabric or ServerLoader.Vanilla && s.ClientMods.Count > 0);

    public static string FileName(string gameVersion) => $"AubsCraft-{gameVersion}.mrpack";

    /// <summary>The pack for one Minecraft version (cached for 30 minutes, rebuilt at once when the mod lists change).</summary>
    public async Task<byte[]?> GetPackAsync(string gameVersion, CancellationToken ct = default)
    {
        var servers = ModdedServers().Where(s => s.GameVersion == gameVersion).ToList();
        if (servers.Count == 0) return null;
        var slugs = BaseMods.Concat(servers.SelectMany(s => s.ClientMods)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var loader = FabricLoaderVersion(servers);
        var key = string.Join(",", slugs.Order()) + "|" + loader;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(gameVersion, out var c) && c.Key == key && DateTime.UtcNow - c.Built < TimeSpan.FromMinutes(30))
                return c.Pack;
            var mods = await _downloader.ModrinthWithDependenciesAsync(slugs, "fabric", gameVersion, null, ct);
            var pack = Build(gameVersion, loader, servers.Select(s => s.Name).ToList(), mods);
            _cache[gameVersion] = (key, pack, DateTime.UtcNow);
            _logger.LogInformation("Built the {Version} PC pack: {Count} mods, Fabric loader {Loader}", gameVersion, mods.Count, loader);
            return pack;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The Fabric loader the servers run (newest of them, from libraries/net/fabricmc/fabric-loader/{version}): the client
    /// should run the same one.
    /// </summary>
    public static string FabricLoaderVersion(IEnumerable<ServerDefinition> servers)
    {
        var versions = servers
            .Select(s => Path.Combine(s.Path, "libraries", "net", "fabricmc", "fabric-loader"))
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(v => Version.TryParse(v.Split('+')[0], out _))
            .OrderByDescending(v => Version.Parse(v.Split('+')[0]))
            .ToList();
        return versions.FirstOrDefault() ?? throw new InvalidOperationException("Could not find the servers' Fabric loader version.");
    }

    public static byte[] Build(string gameVersion, string fabricLoader, IReadOnlyList<string> serverNames, IReadOnlyList<AddonArtifact> mods)
    {
        var files = new JsonArray();
        foreach (var m in mods.OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase))
        {
            if (m.HashAlgorithm != "sha512" || m.Sha1 == null || m.Size <= 0)
                throw new InvalidOperationException($"{m.Name} is missing the hashes or size a Modrinth pack needs.");
            files.Add(new JsonObject
            {
                ["path"] = "mods/" + m.FileName,
                ["hashes"] = new JsonObject { ["sha1"] = m.Sha1, ["sha512"] = m.Hash },
                ["env"] = new JsonObject { ["client"] = "required", ["server"] = "unsupported" },
                ["downloads"] = new JsonArray(m.Url),
                ["fileSize"] = m.Size,
            });
        }
        var index = new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = DateTime.UtcNow.ToString("yyyy.MM.dd.HHmm"),
            ["name"] = $"AubsCraft {gameVersion}",
            ["summary"] = $"Mods for {string.Join(", ", serverNames)} on mc.spawndev.com",
            ["files"] = files,
            ["dependencies"] = new JsonObject { ["minecraft"] = gameVersion, ["fabric-loader"] = fabricLoader },
        };
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            using var w = entry.Open();
            JsonSerializer.Serialize(w, index, new JsonSerializerOptions { WriteIndented = true });
        }
        return ms.ToArray();
    }
}

/// <summary>A PC pack offered on the Set up PC page.</summary>
public record ModpackInfo(string GameVersion, string FileName, List<string> Servers, List<string> Mods);
