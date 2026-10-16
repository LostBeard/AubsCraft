using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The big generated assets and the columns' block ids cross the network packed to a browser that asks for it, and arrive whole: the bytes after unpacking are the bytes of the file.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerCompressionTests
{
    private ViewerRig _rig = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private async Task<(IAPIResponse response, byte[] body)> GetAsync(string path, string? acceptEncoding)
    {
        var headers = new Dictionary<string, string>();
        if (acceptEncoding != null) headers["Accept-Encoding"] = acceptEncoding;
        var response = await _rig.Context.APIRequest.GetAsync(_rig.BaseUrl + path, new APIRequestContextOptions { Headers = headers });
        return (response, await response.BodyAsync());
    }

    [Test]
    public async Task TheAssets_AreSentPacked_WhenAsked_AndWhole()
    {
        var file = Path.Combine(Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH")!, "wwwroot", "assets", "mc-entity-atlas.rgba");
        var (packed, packedBody) = await GetAsync("/assets/mc-entity-atlas.rgba", "br, gzip");
        Assert.That(packed.Ok, Is.True);
        Assert.That(packed.Headers.TryGetValue("content-encoding", out var encoding) ? encoding : null, Is.AnyOf("br", "gzip"), "packed");
        var (plain, plainBody) = await GetAsync("/assets/mc-entity-atlas.rgba", "identity");
        Assert.That(plain.Headers.ContainsKey("content-encoding"), Is.False, "and plain to one that does not ask");
        Assert.That(plainBody.Length, Is.EqualTo(new FileInfo(file).Length));
        Assert.That(packedBody, Is.EqualTo(plainBody), "the same bytes once unpacked");
    }

    [Test]
    public async Task TheColumnsBlockIds_AreSentPacked_AndWhole_WithTheirHeaders()
    {
        var (packed, packedBody) = await GetAsync("/api/world/chunk-ids/0/5", "br, gzip");
        Assert.That(packed.Ok, Is.True, $"status {packed.Status}");
        Assert.That(packed.Headers.TryGetValue("content-encoding", out var encoding) ? encoding : null, Is.AnyOf("br", "gzip"));
        var (plain, plainBody) = await GetAsync("/api/world/chunk-ids/0/5", "identity");
        Assert.That(plainBody.Length, Is.EqualTo(98304 * 2), "a column is 16 x 384 x 16 ids of two bytes");
        Assert.That(packedBody, Is.EqualTo(plainBody));
        foreach (var name in new[] { "x-block-registry-count", "x-biomes", "x-rainheights" })
            Assert.That(packed.Headers.ContainsKey(name), Is.True, $"{name} still comes with it");
    }

    [Test]
    public async Task TheColumnsLight_IsSentPacked_AndWhole()
    {
        var (packed, packedBody) = await GetAsync("/api/world/chunk-light/1/3", "br, gzip");
        Assert.That(packed.Ok, Is.True, $"status {packed.Status}");
        Assert.That(packed.Headers.TryGetValue("content-encoding", out var encoding) ? encoding : null, Is.AnyOf("br", "gzip"));
        Assert.That(packedBody.Length, Is.EqualTo(98304), "a byte for each block of the column");
        // The scene's row of light blocks is in this column: glowstone at x 30, z 56, y -60 gives 15 in its own cell
        int index = (30 & 15) + (56 & 15) * 16 + (-60 + 64) * 256;
        Assert.That(packedBody[index] & 15, Is.EqualTo(15));
        var (missing, _) = await GetAsync("/api/world/chunk-light/9999/9999", "identity");
        Assert.That(missing.Status, Is.EqualTo(404));
    }
}
