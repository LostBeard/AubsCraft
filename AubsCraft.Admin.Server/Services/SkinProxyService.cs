using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AubsCraft.Admin.Server.Services;

/// <summary>What fetching a skin came to.</summary>
public enum SkinResult { Ok, Refused, NotFound, Unavailable }

/// <summary>
/// Fetches a player's skin for the viewer. The browser cannot do it itself (the texture server's http address is mixed content on an https page, and
/// its CORS headers are not ours to rely on), and the panel must not become a way to make requests for anyone: only Mojang's texture server, only a
/// texture path, only a small PNG, remembered so a skin is fetched once however many viewers ask.
/// </summary>
public sealed partial class SkinProxyService
{
    public const string TextureHost = "textures.minecraft.net";
    /// <summary>A skin is a few KB; a 64 x 64 PNG that is bigger than this is not one.</summary>
    public const int MaxBytes = 64 * 1024;
    public const int MaxCached = 512;

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly HttpClient _http;
    private readonly ILogger<SkinProxyService> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    /// <summary>How many requests actually went to the texture server (not the cache).</summary>
    public int Fetches;

    public SkinProxyService(HttpClient http, ILogger<SkinProxyService> logger)
    {
        _http = http;
        _logger = logger;
    }

    // /texture/ then the hash the texture server names textures by: hex digits only
    [GeneratedRegex(@"^/texture/[0-9a-fA-F]{16,128}$")]
    private static partial Regex TexturePath();

    /// <summary>The address to fetch for a skin URL the plugin reported, or null when it is not a Mojang texture URL. The result is always https.</summary>
    public static Uri? Normalise(string? skinUrl)
    {
        if (string.IsNullOrWhiteSpace(skinUrl) || skinUrl.Length > 300) return null;
        if (!Uri.TryCreate(skinUrl, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.Equals(uri.Host, TextureHost, StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.IsDefaultPort) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        if (!TexturePath().IsMatch(uri.AbsolutePath)) return null;
        return new Uri($"https://{TextureHost}{uri.AbsolutePath.ToLowerInvariant()}");
    }

    public async Task<(SkinResult Result, byte[]? Png)> GetAsync(string? skinUrl, CancellationToken cancellation)
    {
        var uri = Normalise(skinUrl);
        if (uri == null) return (SkinResult.Refused, null);
        var key = uri.AbsolutePath;
        if (_cache.TryGetValue(key, out var hit)) return (SkinResult.Ok, hit);

        try
        {
            Interlocked.Increment(ref Fetches);
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (SkinResult.NotFound, null);
            if (!response.IsSuccessStatusCode) return (SkinResult.Unavailable, null);
            if (response.Content.Headers.ContentLength is > MaxBytes) return (SkinResult.Refused, null);

            await using var body = await response.Content.ReadAsStreamAsync(cancellation);
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = await body.ReadAsync(buffer, cancellation)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > MaxBytes) return (SkinResult.Refused, null);
            }
            var bytes = ms.ToArray();
            if (bytes.Length < PngMagic.Length || !bytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic)) return (SkinResult.Refused, null);

            if (_cache.TryAdd(key, bytes))
            {
                _order.Enqueue(key);
                while (_cache.Count > MaxCached && _order.TryDequeue(out var oldest)) _cache.TryRemove(oldest, out _);
            }
            return (SkinResult.Ok, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (!cancellation.IsCancellationRequested) _logger.LogDebug(ex, "Skin fetch for {Path} failed", key);
            return (SkinResult.Unavailable, null);
        }
    }
}
