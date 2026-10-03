using System.Text.Json;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Resolves and caches the assets the /quest installer streams onto a headset: the QuestCraft APK
/// (from QuestCraft's GitHub releases) and the required client mods (from Modrinth). Upstream files
/// are fetched server-side and cached on disk so the browser fetches them same-origin (no CORS) and
/// can stream them straight to the device over WebUSB.
/// </summary>
public class QuestAssetService
{
    readonly IHttpClientFactory _httpFactory;
    readonly ILogger<QuestAssetService> _log;
    readonly string _cacheDir;
    readonly ServerRegistry _registry;
    readonly AddonDownloader _downloader;

    // Mods QCXR installs and updates itself (instances.json / mods.json, slug filenames). A second copy of any of
    // them under another name crashes Fabric, so server client mods never push these - matched by Modrinth
    // project id, because dependencies are listed by id.
    static readonly string[] QcxrManagedSlugs = ["fabric-api", "vivecraft", "sodium", "cloth-config", "simple-voice-chat"];

    // Server client mods in the current manifest (id -> verified artifact): downloaded through AddonDownloader.
    Dictionary<string, AddonArtifact> _serverClientArtifacts = new(StringComparer.OrdinalIgnoreCase);

    // The Minecraft/Fabric version QuestCraft (QCXR) runs - mods must match this exactly.
    // Verified on-device: QCXR 6.0.0 runs MC 1.21.5 + Fabric, matching the Paper 1.21.5 server directly.
    const string ModGameVersion = "1.21.5";

    // Required client mods. TargetFilename is the filename to write into QCXR's instance mods folder -
    // it must match QCXR's slug-based name (e.g. "Simple-Voice-Chat.jar") so we OVERWRITE QCXR's
    // bundled copy (keeping the headset version matched to the server) instead of leaving a duplicate
    // jar, which would crash Fabric. For mods QCXR doesn't ship, any stable filename works.
    static readonly (string Id, string Slug, string Name, string TargetFilename)[] RequiredMods =
    {
        ("simple-voice-chat", "simple-voice-chat", "Simple Voice Chat", "Simple-Voice-Chat.jar"),
    };

    QuestManifest? _cachedManifest;
    DateTimeOffset _manifestFetchedAt;
    readonly TimeSpan _manifestTtl = TimeSpan.FromMinutes(30);
    readonly SemaphoreSlim _gate = new(1, 1);

    public QuestAssetService(IHttpClientFactory httpFactory, IWebHostEnvironment env, ServerRegistry registry, AddonDownloader downloader,
        ILogger<QuestAssetService> log)
    {
        _httpFactory = httpFactory;
        _registry = registry;
        _downloader = downloader;
        _log = log;
        _cacheDir = Path.Combine(env.ContentRootPath, "quest-cache");
        Directory.CreateDirectory(_cacheDir);
    }

    HttpClient NewClient()
    {
        var c = _httpFactory.CreateClient("quest");
        // GitHub + Modrinth both require a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AubsCraft-QuestInstaller/1.0 (+https://spawndev.com)");
        c.Timeout = TimeSpan.FromMinutes(10);
        return c;
    }

    /// <summary>Resolves (and caches for a while) the manifest of what the installer should offer.</summary>
    public async Task<QuestManifest> GetManifestAsync(CancellationToken ct)
    {
        if (_cachedManifest != null && DateTimeOffset.UtcNow - _manifestFetchedAt < _manifestTtl)
            return _cachedManifest;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cachedManifest != null && DateTimeOffset.UtcNow - _manifestFetchedAt < _manifestTtl)
                return _cachedManifest;

            using var http = NewClient();
            var questCraft = await ResolveQuestCraftAsync(http, ct);
            var mods = new List<QuestModInfo>();
            foreach (var m in RequiredMods)
            {
                var info = await ResolveModAsync(http, m.Id, m.Slug, m.Name, m.TargetFilename, "Voice chat on every server", ct);
                if (info != null) mods.Add(info);
            }
            mods.AddRange(await ResolveServerClientModsAsync(http, ct));

            _cachedManifest = new QuestManifest(
                QuestCraft: questCraft,
                Mods: mods,
                // QCXR 6.0.0 uses an instance-based layout (verified on-device):
                // /sdcard/Android/data/<package>/files/instances/<instance>/mods. The client fills in
                // {package} (detected) and {instance} (the instance folder name, e.g. "1.21.5").
                ModsDirTemplate: "/sdcard/Android/data/{package}/files/instances/{instance}/mods",
                // Hints the client uses to find the installed QuestCraft package via `pm list packages`
                // (the QCXR rebrand uses com.qcxr.qcxr; older builds used com.neofetch.questcraft).
                PackageHints: new[] { "qcxr", "questcraft", "pojav", "neofetch" },
                QuestCraftManaged: QcxrManagedSlugs);
            _manifestFetchedAt = DateTimeOffset.UtcNow;
            return _cachedManifest;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The client mods every Fabric server on QuestCraft's Minecraft version needs (ServerDefinition.ClientMods),
    /// with their required dependencies, minus what QCXR manages itself. Each is written as
    /// aubscraft-{projectId}.jar: a stable name, so an update overwrites the old jar instead of leaving a duplicate.
    /// </summary>
    async Task<List<QuestModInfo>> ResolveServerClientModsAsync(HttpClient http, CancellationToken ct)
    {
        var questServers = _registry.All
            .Where(s => s.Loader is AubsCraft.Admin.Server.Models.ServerLoader.Fabric && s.GameVersion == ModGameVersion).ToList();
        var slugs = questServers.SelectMany(s => s.ClientMods).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var artifacts = new Dictionary<string, AddonArtifact>(StringComparer.OrdinalIgnoreCase);
        if (slugs.Count > 0)
        {
            var managed = new HashSet<string>(QcxrManagedSlugs, StringComparer.OrdinalIgnoreCase);
            foreach (var slug in QcxrManagedSlugs)
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.modrinth.com/v2/project/{slug}", ct));
                managed.Add(doc.RootElement.GetProperty("id").GetString()!);
            }
            foreach (var a in await _downloader.ModrinthWithDependenciesAsync(slugs, "fabric", ModGameVersion, managed, ct))
                artifacts[a.ProjectId ?? a.Name] = a;
        }
        _serverClientArtifacts = artifacts;
        // Who needs it: the servers that list it, or (a dependency) the mods that need it.
        string For(AddonArtifact a)
        {
            var names = questServers.Where(s => s.ClientMods.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).Select(s => s.Name).ToList();
            return names.Count > 0 ? string.Join(", ", names) : "Needed by another mod";
        }
        return artifacts.Select(kv => new QuestModInfo(kv.Key, kv.Value.Name, kv.Value.Version, kv.Value.FileName,
            $"aubscraft-{kv.Key}.jar", kv.Value.Url, 0, kv.Value.HashAlgorithm == "sha512" ? kv.Value.Hash : null, For(kv.Value))).ToList();
    }

    async Task<QuestCraftInfo> ResolveQuestCraftAsync(HttpClient http, CancellationToken ct)
    {
        // Latest QuestCraft release; pick the .apk asset.
        var json = await http.GetStringAsync("https://api.github.com/repos/QuestCraftPlusPlus/QuestCraft/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.GetProperty("tag_name").GetString() ?? "unknown";
        string? url = null, filename = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                filename = name;
                url = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        if (url == null || filename == null)
            throw new InvalidOperationException("QuestCraft latest release has no .apk asset.");
        return new QuestCraftInfo("questcraft", version, filename, url);
    }

    async Task<QuestModInfo?> ResolveModAsync(HttpClient http, string id, string slug, string name, string targetFilename, string forWhat, CancellationToken ct)
    {
        // Modrinth versions for this project, filtered to Fabric + the target MC version. Pick newest.
        var url = $"https://api.modrinth.com/v2/project/{slug}/version?loaders=%5B%22fabric%22%5D&game_versions=%5B%22{ModGameVersion}%22%5D";
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        JsonElement? newest = null;
        DateTimeOffset newestDate = DateTimeOffset.MinValue;
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var date = v.TryGetProperty("date_published", out var dp) && dp.GetDateTimeOffset() is var d ? d : DateTimeOffset.MinValue;
            if (newest == null || date > newestDate) { newest = v.Clone(); newestDate = date; }
        }
        if (newest == null) { _log.LogWarning("No Modrinth fabric {Version} version for {Slug}", ModGameVersion, slug); return null; }

        var version = newest.Value.GetProperty("version_number").GetString() ?? "unknown";
        // The primary file (or the first one).
        JsonElement? file = null;
        foreach (var f in newest.Value.GetProperty("files").EnumerateArray())
        {
            if (file == null) file = f;
            if (f.TryGetProperty("primary", out var p) && p.GetBoolean()) { file = f; break; }
        }
        if (file == null) return null;
        var filename = file.Value.GetProperty("filename").GetString()!;
        var downloadUrl = file.Value.GetProperty("url").GetString()!;
        long size = file.Value.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
        var sha512 = file.Value.TryGetProperty("hashes", out var h) && h.TryGetProperty("sha512", out var h5) ? h5.GetString() : null;
        return new QuestModInfo(id, name, version, filename, targetFilename, downloadUrl, size, sha512, forWhat);
    }

    /// <summary>
    /// Opens the asset with the given id (the QuestCraft id or a mod id), downloading and caching it
    /// to disk on first request. Returns a readable stream plus its filename, content type and length.
    /// </summary>
    public async Task<(Stream Stream, string Filename, string ContentType, long Length)?> OpenAssetAsync(string id, CancellationToken ct)
    {
        var manifest = await GetManifestAsync(ct);
        string upstreamUrl, filename, contentType;
        if (string.Equals(id, manifest.QuestCraft.Id, StringComparison.OrdinalIgnoreCase))
        {
            upstreamUrl = manifest.QuestCraft.DownloadUrl;
            filename = manifest.QuestCraft.Filename;
            contentType = "application/vnd.android.package-archive";
        }
        else
        {
            var mod = manifest.Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (mod == null) return null;
            upstreamUrl = mod.DownloadUrl;
            filename = mod.Filename;
            contentType = "application/java-archive";
        }

        var cachePath = Path.Combine(_cacheDir, filename);
        if (_serverClientArtifacts.TryGetValue(id, out var verified))
        {
            // Server client mods: checksum-verified download (kept in the cache once verified).
            if (!AddonDownloader.IsCurrent(verified, cachePath))
                await _downloader.DownloadAsync(verified, cachePath, ct);
        }
        else if (!File.Exists(cachePath))
        {
            await DownloadToCacheAsync(upstreamUrl, cachePath, ct);
        }

        var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return (stream, filename, contentType, stream.Length);
    }

    async Task DownloadToCacheAsync(string url, string cachePath, CancellationToken ct)
    {
        _log.LogInformation("Downloading quest asset from {Url}", url);
        using var http = NewClient();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var tmp = cachePath + ".tmp";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await src.CopyToAsync(dst, ct);
        }
        File.Move(tmp, cachePath, overwrite: true);
    }
}

/// <summary>The /quest manifest: what to install and where the mods go.</summary>
public record QuestManifest(
    QuestCraftInfo QuestCraft,
    IReadOnlyList<QuestModInfo> Mods,
    string ModsDirTemplate,
    IReadOnlyList<string> PackageHints,
    IReadOnlyList<string> QuestCraftManaged);

/// <summary>The QuestCraft APK to install.</summary>
public record QuestCraftInfo(string Id, string Version, string Filename, string DownloadUrl);

/// <summary>A required client mod jar to push to the mods folder. TargetFilename is the on-device
/// filename (QCXR slug name) the jar is written as, so it overwrites QCXR's bundled copy. Sha512 (Modrinth's) lets the
/// page tell whether the headset's copy is current; For says which server(s) need it.</summary>
public record QuestModInfo(string Id, string Name, string Version, string Filename, string TargetFilename, string DownloadUrl, long Size,
    string? Sha512 = null, string For = "");
