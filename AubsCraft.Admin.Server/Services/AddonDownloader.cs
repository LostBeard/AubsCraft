using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AubsCraft.Admin.Server.Services;

/// <summary>A downloadable jar with the checksum its source publishes (or a pinned one).</summary>
/// <remarks>Sha1 and Size are filled for Modrinth files (a Modrinth modpack lists both).</remarks>
public record AddonArtifact(string Name, string FileName, string Url, string HashAlgorithm, string Hash, string Version,
    string? ProjectId = null, IReadOnlyList<string>? RequiredProjectIds = null, string? Sha1 = null, long Size = 0);

/// <summary>
/// Resolves server software and add-ons to exact downloads WITH checksums, and downloads them verified:
/// PaperMC (Paper, Velocity) via fill.papermc.io, GeyserMC (Geyser, Floodgate) via download.geysermc.org,
/// Modrinth projects (Via*, Simple Voice Chat, Velocircon, Vivecraft extension, ...) by loader and version.
/// A file whose hash does not match is deleted and the download fails - nothing unverified is installed.
/// </summary>
public class AddonDownloader
{
    private readonly HttpClient _http;

    public AddonDownloader(HttpClient? http = null)
    {
        // No client-wide timeout: each attempt gets its own (ApiTimeout / DownloadTimeout) so a hung request
        // is retried instead of failing the whole setup.
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AubsCraft-Admin/1.0 (https://github.com/LostBeard)");
    }

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The latest build of a PaperMC project version (e.g. "velocity", "4.2.0" or "paper", "1.21.5").</summary>
    public async Task<AddonArtifact> PaperMcAsync(string project, string version, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync(
            $"https://fill.papermc.io/v3/projects/{project}/versions/{version}/builds/latest", ct));
        var dl = doc.RootElement.GetProperty("downloads").GetProperty("server:default");
        return new AddonArtifact(project, dl.GetProperty("name").GetString()!, dl.GetProperty("url").GetString()!,
            "sha256", dl.GetProperty("checksums").GetProperty("sha256").GetString()!,
            $"{version}-{doc.RootElement.GetProperty("id").GetInt32()}");
    }

    /// <summary>The latest GeyserMC build of a project ("geyser", "floodgate") for a platform ("velocity", "spigot", ...).</summary>
    public async Task<AddonArtifact> GeyserMcAsync(string project, string platform, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync(
            $"https://download.geysermc.org/v2/projects/{project}/versions/latest/builds/latest", ct));
        var root = doc.RootElement;
        var version = root.GetProperty("version").GetString()!;
        var build = root.GetProperty("build").GetInt32();
        var dl = root.GetProperty("downloads").GetProperty(platform);
        return new AddonArtifact(project, dl.GetProperty("name").GetString()!,
            $"https://download.geysermc.org/v2/projects/{project}/versions/{version}/builds/{build}/downloads/{platform}",
            "sha256", dl.GetProperty("sha256").GetString()!, $"{version}-b{build}");
    }

    /// <summary>
    /// The newest RELEASE of a Modrinth project for a loader ("velocity", "paper", "fabric", ...), optionally for
    /// one Minecraft version; falls back to the newest of any type only when the project has no release.
    /// </summary>
    public async Task<AddonArtifact> ModrinthAsync(string slug, string loader, string? gameVersion = null, CancellationToken ct = default)
    {
        var url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(slug)}/version?loaders={Uri.EscapeDataString($"[\"{loader}\"]")}";
        if (gameVersion != null) url += $"&game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}";
        using var doc = JsonDocument.Parse(await GetStringAsync(url, ct));
        var versions = doc.RootElement.EnumerateArray().ToList();
        var pick = versions.FirstOrDefault(v => v.GetProperty("version_type").GetString() == "release");
        if (pick.ValueKind == JsonValueKind.Undefined) pick = versions.FirstOrDefault();
        if (pick.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Modrinth has no {loader} build of '{slug}'{(gameVersion != null ? " for " + gameVersion : "")}.");
        return FromModrinthVersion(pick, slug);
    }

    /// <summary>A Modrinth version object (from /project/x/version, /version_files, ...) as a verified artifact: its primary file.</summary>
    public static AddonArtifact FromModrinthVersion(JsonElement version, string name)
    {
        var file = version.GetProperty("files").EnumerateArray().FirstOrDefault(f => f.GetProperty("primary").GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined) file = version.GetProperty("files")[0];
        return new AddonArtifact(name, file.GetProperty("filename").GetString()!, file.GetProperty("url").GetString()!,
            "sha512", file.GetProperty("hashes").GetProperty("sha512").GetString()!, version.GetProperty("version_number").GetString()!,
            version.GetProperty("project_id").GetString(), RequiredProjectIds(version),
            file.GetProperty("hashes").TryGetProperty("sha1", out var sha1) ? sha1.GetString() : null,
            file.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
    }

    /// <summary>The project ids a Modrinth version REQUIRES.</summary>
    public static List<string> RequiredProjectIds(JsonElement version) =>
        version.GetProperty("dependencies").EnumerateArray()
            .Where(d => d.GetProperty("dependency_type").GetString() == "required"
                        && d.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String)
            .Select(d => d.GetProperty("project_id").GetString()!)
            .ToList();

    /// <summary>GET a Modrinth API URL (with the retries).</summary>
    public Task<string> ModrinthGetAsync(string url, CancellationToken ct = default) => GetStringAsync(url, ct);

    /// <summary>POST JSON to a Modrinth API URL (with the retries), e.g. /v2/version_files to identify jars by hash.</summary>
    public Task<string> ModrinthPostAsync(string url, object body, CancellationToken ct = default) =>
        WithRetryAsync(ApiTimeout, async attemptCt =>
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, attemptCt);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(attemptCt);
        }, ct);

    /// <summary>
    /// A Modrinth project plus everything it REQUIRES (recursively), for one loader and Minecraft version,
    /// each once (by project id). <paramref name="alreadyHave"/> are project ids or slugs to skip (e.g. installed).
    /// A required dependency with no build for this loader/version fails the whole resolution: installing the
    /// mod without it would crash the server on start.
    /// </summary>
    public async Task<List<AddonArtifact>> ModrinthWithDependenciesAsync(IEnumerable<string> slugs, string loader, string? gameVersion,
        ISet<string>? alreadyHave = null, CancellationToken ct = default)
    {
        var result = new List<AddonArtifact>();
        var seen = new HashSet<string>(alreadyHave ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(slugs);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (seen.Contains(next)) continue;
            var a = await ModrinthAsync(next, loader, gameVersion, ct);
            if (a.ProjectId == next)
            {
                // Reached as a dependency (by id): name it by its slug, which people recognise.
                using var project = JsonDocument.Parse(await GetStringAsync($"https://api.modrinth.com/v2/project/{next}", ct));
                a = a with { Name = project.RootElement.GetProperty("slug").GetString() ?? next };
            }
            seen.Add(next);
            if (a.ProjectId != null && !seen.Add(a.ProjectId) && next != a.ProjectId) continue; // reached by another name
            result.Add(a);
            foreach (var dep in a.RequiredProjectIds ?? []) queue.Enqueue(dep);
        }
        return result;
    }

    /// <summary>
    /// Vivecraft Velocity Extensions 1.0 (Techjar, 2022 - the only release; it registers and forwards the
    /// vivecraft:data channel). GitHub publishes no digest for it, so the hash is pinned here.
    /// </summary>
    public static AddonArtifact VivecraftVelocityExtensions { get; } = new(
        "vivecraft-velocity-extensions", "Vivecraft_Velocity_Extensions-1.0.jar",
        "https://github.com/Techjar/Vivecraft_Velocity_Extensions/releases/download/1.0/Vivecraft_Velocity_Extensions-1.0.jar",
        "sha256", "60ba1b26cebfc63972b58e7ca00a648d2a4355623a2bc7e9e76a42654f821a08", "1.0");

    /// <summary>Downloads to <paramref name="path"/> through a temp file, verifying the hash before it is moved into place.</summary>
    public async Task DownloadAsync(AddonArtifact a, string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".download";
        try
        {
            await WithRetryAsync(DownloadTimeout, async attemptCt =>
            {
                using var response = await _http.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead, attemptCt);
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(attemptCt);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, attemptCt);
                return true;
            }, ct);
            var actual = Hash(tmp, a.HashAlgorithm);
            if (!actual.Equals(a.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{a.FileName}: {a.HashAlgorithm} mismatch (expected {a.Hash}, got {actual}).");
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>GET a string, retrying transient failures (see WithRetryAsync).</summary>
    private Task<string> GetStringAsync(string url, CancellationToken ct) =>
        WithRetryAsync(ApiTimeout, async attemptCt =>
        {
            using var response = await _http.GetAsync(url, attemptCt);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(attemptCt);
        }, ct);

    /// <summary>
    /// Runs a request with a per-attempt timeout and retries it on 5xx, network errors and timeouts (Modrinth
    /// answered one lookup 502 and later let one hang for 100 s; both came back fine on the next try): 4 tries,
    /// 1/2/4 s apart. A 4xx is the request's fault and fails at once; the caller cancelling stops at once.
    /// </summary>
    private static async Task<T> WithRetryAsync<T>(TimeSpan timeout, Func<CancellationToken, Task<T>> request, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeout);
            try
            {
                return await request(attemptCts.Token);
            }
            catch (HttpRequestException ex) when (attempt < 4 && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
            {
            }
            catch (OperationCanceledException) when (attempt < 4 && !ct.IsCancellationRequested)
            {
                // This attempt's timeout, not the caller's cancellation.
            }
            catch (IOException) when (attempt < 4 && !ct.IsCancellationRequested)
            {
                // Connection dropped mid-body.
            }
            await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), ct);
        }
    }

    /// <summary>True when the file exists and matches the artifact's hash (skip re-downloading).</summary>
    public static bool IsCurrent(AddonArtifact a, string path) =>
        File.Exists(path) && Hash(path, a.HashAlgorithm).Equals(a.Hash, StringComparison.OrdinalIgnoreCase);

    private static string Hash(string path, string algorithm)
    {
        using var s = File.OpenRead(path);
        return algorithm switch
        {
            "sha256" => Convert.ToHexStringLower(SHA256.HashData(s)),
            "sha512" => Convert.ToHexStringLower(SHA512.HashData(s)),
            _ => throw new ArgumentException($"Unknown hash algorithm {algorithm}"),
        };
    }
}
