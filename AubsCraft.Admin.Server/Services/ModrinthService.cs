using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Client for the Modrinth API. Searches and downloads add-ons for a server: plugins for Paper, mods for
/// Fabric / Forge / NeoForge, matched to that server's loader and Minecraft version.
/// https://docs.modrinth.com/
/// </summary>
public class ModrinthService
{
    private readonly HttpClient _http;
    private readonly ILogger<ModrinthService> _logger;

    public ModrinthService(ILogger<ModrinthService> logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/"),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "AubsCraft-Admin/1.0 (https://github.com/LostBeard)");
    }

    /// <summary>The Modrinth loader names whose builds run on a server of this type.</summary>
    public static string[] LoadersFor(ServerLoader loader) => loader switch
    {
        ServerLoader.Paper => ["bukkit", "paper", "spigot", "purpur", "folia"],
        ServerLoader.Fabric => ["fabric"],
        ServerLoader.Forge => ["forge"],
        ServerLoader.NeoForge => ["neoforge"],
        _ => [],
    };

    /// <summary>
    /// Modrinth search facets for add-ons that run on this server. The OUTER array is AND, INNER arrays are
    /// OR: "built for ANY of this server's loaders". For the mod loaders, also AND "runs on a server"
    /// (client-only mods are useless there) and "built for this exact Minecraft version" (mods, unlike
    /// Paper plugins, break across versions). The old plugin form ANDed bukkit+paper+mod, which matched
    /// almost nothing (e.g. Simple Voice Chat).
    /// </summary>
    public static string FacetsFor(ServerDefinition server)
    {
        var loaders = "[" + string.Join(",", LoadersFor(server.Loader).Select(l => $"\"categories:{l}\"")) + "]";
        if (server.Loader == ServerLoader.Paper) return "[" + loaders + "]";
        return "[" + loaders + ",[\"server_side:required\",\"server_side:optional\"],[\"versions:" + server.GameVersion + "\"]]";
    }

    /// <summary>
    /// Search Modrinth for add-ons that run on this server. Empty for a server with no add-on loader (Vanilla).
    /// </summary>
    public async Task<List<ModrinthSearchResult>> SearchAsync(ServerDefinition server, string query, int limit = 20)
    {
        if (LoadersFor(server.Loader).Length == 0) return [];
        try
        {
            var facets = FacetsFor(server);
            var url = $"search?query={Uri.EscapeDataString(query)}&limit={limit}&facets={Uri.EscapeDataString(facets)}";
            var response = await _http.GetFromJsonAsync<ModrinthSearchResponse>(url);
            return response?.Hits ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modrinth search failed for: {Query}", query);
            return [];
        }
    }

    /// <summary>
    /// Get versions of a project that run on this server (its loader and Minecraft version).
    /// </summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(ServerDefinition server, string projectId)
    {
        if (LoadersFor(server.Loader).Length == 0) return [];
        try
        {
            // The bracket/quote JSON array params MUST be URL-encoded - unencoded, Modrinth returns
            // nothing (which surfaced as "No compatible version found" on install).
            var gameVersions = Uri.EscapeDataString($"[\"{server.GameVersion}\"]");
            var loaders = Uri.EscapeDataString("[" + string.Join(",", LoadersFor(server.Loader).Select(l => $"\"{l}\"")) + "]");
            var url = $"project/{Uri.EscapeDataString(projectId)}/version?game_versions={gameVersions}&loaders={loaders}";
            return await _http.GetFromJsonAsync<List<ModrinthVersion>>(url) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get versions for: {ProjectId}", projectId);
            return [];
        }
    }

    /// <summary>
    /// Download a plugin jar from Modrinth.
    /// </summary>
    public async Task<(byte[] data, string filename)?> DownloadAsync(string url)
    {
        try
        {
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync();
            var filename = url.Split('/').Last();
            return (data, filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download: {Url}", url);
            return null;
        }
    }
}

public class ModrinthSearchResponse
{
    [JsonPropertyName("hits")]
    public List<ModrinthSearchResult> Hits { get; set; } = [];

    [JsonPropertyName("total_hits")]
    public int TotalHits { get; set; }
}

public class ModrinthSearchResult
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; set; }
}

public class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = "";

    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = ""; // release, beta, alpha

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("date_published")]
    public DateTime DatePublished { get; set; }

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = [];
}

public class ModrinthFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}
