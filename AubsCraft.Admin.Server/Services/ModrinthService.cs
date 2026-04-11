using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Client for the Modrinth API. Searches and downloads Minecraft plugins.
/// https://docs.modrinth.com/
/// </summary>
public class ModrinthService
{
    private readonly HttpClient _http;
    private readonly ILogger<ModrinthService> _logger;
    private readonly string _gameVersion;

    public ModrinthService(IConfiguration configuration, ILogger<ModrinthService> logger)
    {
        _logger = logger;
        _gameVersion = configuration.GetValue<string>("Minecraft:GameVersion") ?? "1.21.5";
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.modrinth.com/v2/"),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "AubsCraft-Admin/1.0 (https://github.com/LostBeard)");
    }

    /// <summary>
    /// Search for plugins on Modrinth.
    /// </summary>
    public async Task<List<ModrinthSearchResult>> SearchAsync(string query, int limit = 20)
    {
        try
        {
            var facets = $"[[\"categories:bukkit\"],[\"categories:paper\"],[\"project_type:mod\"]]";
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
    /// Get versions for a project that are compatible with the server.
    /// </summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId)
    {
        try
        {
            var url = $"project/{projectId}/version?game_versions=[\"{_gameVersion}\"]&loaders=[\"bukkit\",\"paper\",\"spigot\"]";
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
