using System.Security.Cryptography;
using System.Text.Json;

namespace AubsCraft.Assets;

/// <summary>
/// Finds the client jar of a Minecraft version: an explicit path, the launcher's copy, a cached download, or a download from Mojang's version
/// manifest (verified against the SHA-1 the manifest gives). The jar is Mojang's: it stays in a cache folder, never in the repository.
/// </summary>
public static class JarLocator
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>Where downloaded jars are kept: %LOCALAPPDATA%\AubsCraft\mc-client (or ~/.cache/aubscraft/mc-client).</summary>
    public static string DefaultCacheDirectory()
    {
        var root = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(root, "AubsCraft", "mc-client");
    }

    /// <summary>The path of the version's client jar; downloads it when no copy is found. Throws when it cannot be had.</summary>
    public static async Task<string> FindOrDownloadAsync(string version, string? explicitPath = null, string? cacheDirectory = null, Action<string>? log = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (!File.Exists(explicitPath)) throw new FileNotFoundException($"The client jar {explicitPath} does not exist");
            return explicitPath;
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            var launcher = Path.Combine(appData, ".minecraft", "versions", version, version + ".jar");
            if (File.Exists(launcher)) { log?.Invoke($"using the launcher's {launcher}"); return launcher; }
        }

        cacheDirectory ??= DefaultCacheDirectory();
        var cached = Path.Combine(cacheDirectory, version + ".jar");
        if (File.Exists(cached)) { log?.Invoke($"using the cached {cached}"); return cached; }

        Directory.CreateDirectory(cacheDirectory);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        log?.Invoke($"looking up {version} in Mojang's version manifest");
        using var manifest = JsonDocument.Parse(await http.GetStringAsync(ManifestUrl));
        string? versionUrl = null;
        foreach (var v in manifest.RootElement.GetProperty("versions").EnumerateArray())
            if (v.GetProperty("id").GetString() == version) { versionUrl = v.GetProperty("url").GetString(); break; }
        if (versionUrl == null) throw new InvalidOperationException($"Mojang's manifest has no version {version}");

        using var meta = JsonDocument.Parse(await http.GetStringAsync(versionUrl));
        var client = meta.RootElement.GetProperty("downloads").GetProperty("client");
        var url = client.GetProperty("url").GetString()!;
        var sha1 = client.GetProperty("sha1").GetString()!;

        log?.Invoke($"downloading {url}");
        var bytes = await http.GetByteArrayAsync(url);
        var actual = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The downloaded client jar has SHA-1 {actual}, the manifest says {sha1}");
        var temp = cached + ".part";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, cached, overwrite: true);
        log?.Invoke($"cached as {cached} ({bytes.Length / 1024 / 1024} MB)");
        return cached;
    }
}
