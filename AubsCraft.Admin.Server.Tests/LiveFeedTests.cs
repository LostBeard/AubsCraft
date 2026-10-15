using System.Net;
using System.Net.Http.Headers;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The panel's side of the live activity: the shared secret, frames accepted only when they parse, fan-out to viewers that lose the oldest
/// frames when slow, the plugin installer, and the endpoints (loopback + secret to send, a login to watch).
/// </summary>
public class LiveFeedTests
{
    private static IConfiguration Config(params (string, string?)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    private static LiveFeedService Feed(string secret = "s3cret") =>
        new(Config(("Live:Secret", secret)), NullLogger<LiveFeedService>.Instance);

    /// <summary>A valid frame; <paramref name="fullTime"/> tells frames apart.</summary>
    private static byte[] FrameBytes(long fullTime, int cows = 2) => LiveFrameCodec.Write(new LiveFrame
    {
        FullTime = fullTime, DayTime = 6000, Raining = false, Thundering = false,
        Worlds =
        [
            new LiveWorld
            {
                Name = "world",
                Entities = Enumerable.Range(1, cows).Select(i => new LiveEntity(i, "cow", i, 64, i, 0, 0, 0, LiveFlags.OnGround, 0, 0, 0, null, null)).ToArray(),
                Players = [new LivePlayer(1, "01234567-89ab-cdef-fedc-ba9876543210", "Steve", "http://x/s", false, 0, "air", 1f)],
            },
        ],
    });

    // ---- the secret ----

    [Test]
    public void TheSecret_MustMatchExactly()
    {
        var feed = Feed("abc123");
        Assert.That(feed.VerifySecret("abc123"), Is.True);
        Assert.That(feed.VerifySecret("abc124"), Is.False);
        Assert.That(feed.VerifySecret("abc12"), Is.False, "a prefix");
        Assert.That(feed.VerifySecret("abc1234"), Is.False, "longer");
        Assert.That(feed.VerifySecret("ABC123"), Is.False, "case matters");
        Assert.That(feed.VerifySecret(""), Is.False);
        Assert.That(feed.VerifySecret(null), Is.False);
    }

    [Test]
    public void WithoutAConfiguredSecret_OneIsCreated_AndKept()
    {
        var path = Path.Combine(TestUtil.NewTempDir(), "live-secret.txt");
        var first = new LiveFeedService(Config(("Live:SecretPath", path)), NullLogger<LiveFeedService>.Instance);
        Assert.That(first.Secret.Length, Is.GreaterThanOrEqualTo(32), "long enough to not be guessed");
        Assert.That(File.ReadAllText(path), Is.EqualTo(first.Secret));
        var second = new LiveFeedService(Config(("Live:SecretPath", path)), NullLogger<LiveFeedService>.Instance);
        Assert.That(second.Secret, Is.EqualTo(first.Secret), "the plugins already installed keep working after a panel restart");
    }

    [Test]
    public void TwoPanels_DoNotShareASecret()
    {
        var a = new LiveFeedService(Config(("Live:SecretPath", Path.Combine(TestUtil.NewTempDir(), "s.txt"))), NullLogger<LiveFeedService>.Instance);
        var b = new LiveFeedService(Config(("Live:SecretPath", Path.Combine(TestUtil.NewTempDir(), "s.txt"))), NullLogger<LiveFeedService>.Instance);
        Assert.That(a.Secret, Is.Not.EqualTo(b.Secret));
    }

    // ---- ingest ----

    [Test]
    public void AValidFrame_IsAccepted_AndCounted()
    {
        var feed = Feed();
        Assert.That(feed.Ingest("s1", FrameBytes(100, cows: 3)), Is.Null);
        var status = feed.StatusOf("s1");
        Assert.That(status.Live, Is.True);
        Assert.That(status.Frames, Is.EqualTo(1));
        Assert.That(status.Entities, Is.EqualTo(3));
        Assert.That(status.Players, Is.EqualTo(1));
        Assert.That(status.LastFullTime, Is.EqualTo(100));
        Assert.That(feed.StatusOf("other").Live, Is.False, "feeds are per server");
    }

    [Test]
    public void ABadFrame_IsRefused_AndNeverRelayed()
    {
        var feed = Feed();
        using var sub = feed.Subscribe("s1");
        var good = FrameBytes(100);
        Assert.That(feed.Ingest("s1", []), Is.Not.Null, "empty");
        Assert.That(feed.Ingest("s1", "not a frame at all"u8.ToArray()), Is.Not.Null, "garbage");
        Assert.That(feed.Ingest("s1", good[..(good.Length / 2)]), Is.Not.Null, "truncated");
        Assert.That(feed.Ingest("s1", new byte[LiveFeedService.MaxFrameBytes + 1]), Is.Not.Null, "too big");
        Assert.That(feed.StatusOf("s1").Frames, Is.EqualTo(0));
        Assert.That(sub.Frames.TryRead(out _), Is.False, "nothing reached the viewer");
    }

    // ---- fan-out ----

    [Test]
    public void ANewViewer_GetsTheLatestFrameAtOnce_ThenTheNewOnes()
    {
        var feed = Feed();
        feed.Ingest("s1", FrameBytes(100));
        feed.Ingest("s1", FrameBytes(104));
        using var sub = feed.Subscribe("s1");
        Assert.That(sub.Frames.TryRead(out var first), Is.True);
        Assert.That(LiveFrameCodec.Parse(first!).FullTime, Is.EqualTo(104), "the latest, not the first");
        Assert.That(sub.Frames.TryRead(out _), Is.False);
        feed.Ingest("s1", FrameBytes(108));
        Assert.That(sub.Frames.TryRead(out var next), Is.True);
        Assert.That(LiveFrameCodec.Parse(next!).FullTime, Is.EqualTo(108));
    }

    [Test]
    public void Viewers_OnlyGetTheirOwnServersFrames()
    {
        var feed = Feed();
        using var a = feed.Subscribe("a");
        using var b = feed.Subscribe("b");
        feed.Ingest("a", FrameBytes(1));
        Assert.That(a.Frames.TryRead(out _), Is.True);
        Assert.That(b.Frames.TryRead(out _), Is.False);
    }

    [Test]
    public void EveryViewer_GetsEveryFrame()
    {
        var feed = Feed();
        using var one = feed.Subscribe("s1");
        using var two = feed.Subscribe("s1");
        feed.Ingest("s1", FrameBytes(100));
        Assert.That(one.Frames.TryRead(out _), Is.True);
        Assert.That(two.Frames.TryRead(out _), Is.True);
        Assert.That(feed.SubscriberCount("s1"), Is.EqualTo(2));
    }

    [Test]
    public void ADisposedSubscription_GetsNothingMore_AndIsForgotten()
    {
        var feed = Feed();
        var sub = feed.Subscribe("s1");
        sub.Dispose();
        Assert.That(feed.SubscriberCount("s1"), Is.EqualTo(0));
        feed.Ingest("s1", FrameBytes(100));
        Assert.That(sub.Frames.TryRead(out _), Is.False);
    }

    [Test]
    public void ASlowViewer_LosesTheOldestFrames_NotTheNewest_AndNeverBlocksTheFeed()
    {
        var feed = Feed();
        using var slow = feed.Subscribe("s1");
        for (int i = 0; i < 30; i++) feed.Ingest("s1", FrameBytes(1000 + i)); // never read
        var seen = new List<long>();
        while (slow.Frames.TryRead(out var f)) seen.Add(LiveFrameCodec.Parse(f).FullTime);
        Assert.That(seen.Count, Is.LessThanOrEqualTo(8), "bounded: a stuck browser does not grow the panel");
        Assert.That(seen[^1], Is.EqualTo(1029), "the newest survived");
        Assert.That(seen, Is.Ordered);
        Assert.That(feed.StatusOf("s1").Frames, Is.EqualTo(30), "the feed itself took every frame");
    }

    // ---- the plugin installer ----

    private static (LivePluginService Plugin, LiveFeedService Feed, ServerDefinition Server) PluginFor(ServerLoader loader = ServerLoader.Paper)
    {
        var feed = Feed("plugin-secret");
        var plugin = new LivePluginService(feed, Config(("Live:PluginPanelUrl", "http://127.0.0.1:5999")), NullLogger<LivePluginService>.Instance);
        var server = new ServerDefinition { Id = "creative", Name = "Creative", Loader = loader, Path = TestUtil.NewTempDir() };
        return (plugin, feed, server);
    }

    [Test]
    public void TheShippedJar_IsInTheBuild()
    {
        Assert.That(File.Exists(LivePluginService.ShippedJar), Is.True, LivePluginService.ShippedJar);
        var head = File.ReadAllBytes(LivePluginService.ShippedJar).AsSpan(0, 2).ToArray();
        Assert.That(head, Is.EqualTo("PK"u8.ToArray()), "a jar is a zip");
    }

    [Test]
    public void Install_PutsTheJarAndItsConfigInThePluginsFolder()
    {
        var (plugin, feed, server) = PluginFor();
        Assert.That(plugin.Inspect(server).JarInstalled, Is.False);

        var result = plugin.Install(server);
        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Changed, Is.True);
        var jar = Path.Combine(server.Path, "plugins", "aubscraft-live.jar");
        Assert.That(File.ReadAllBytes(jar), Is.EqualTo(File.ReadAllBytes(LivePluginService.ShippedJar)));
        var config = File.ReadAllText(Path.Combine(server.Path, "plugins", "AubsCraftLive", "config.yml"));
        Assert.That(config, Does.Contain("server-id: \"creative\""));
        Assert.That(config, Does.Contain("panel-url: \"http://127.0.0.1:5999\""));
        Assert.That(config, Does.Contain($"secret: \"{feed.Secret}\""));

        var state = plugin.Inspect(server);
        Assert.That(state, Is.EqualTo(state with { Supported = true, JarInstalled = true, JarUpToDate = true, ConfigWritten = true, Live = false }));
        Assert.That(state.Message, Does.Contain("Restart"));
    }

    [Test]
    public void Install_Twice_ChangesNothingTheSecondTime()
    {
        var (plugin, _, server) = PluginFor();
        plugin.Install(server);
        var again = plugin.Install(server);
        Assert.That(again.Success, Is.True);
        Assert.That(again.Changed, Is.False, "no restart needed for nothing");
    }

    [Test]
    public void AnOlderJar_IsReported_AndReplaced()
    {
        var (plugin, _, server) = PluginFor();
        plugin.Install(server);
        var jar = Path.Combine(server.Path, "plugins", "aubscraft-live.jar");
        File.WriteAllBytes(jar, "PK old version"u8.ToArray());
        Assert.That(plugin.Inspect(server).JarUpToDate, Is.False);
        Assert.That(plugin.Install(server).Changed, Is.True);
        Assert.That(plugin.Inspect(server).JarUpToDate, Is.True);
    }

    [Test]
    public void ATamperedConfig_IsReported_AndRewritten()
    {
        var (plugin, _, server) = PluginFor();
        plugin.Install(server);
        var cfg = Path.Combine(server.Path, "plugins", "AubsCraftLive", "config.yml");
        File.WriteAllText(cfg, "secret: \"wrong\"\n");
        Assert.That(plugin.Inspect(server).ConfigWritten, Is.False);
        Assert.That(plugin.Install(server).Changed, Is.True);
        Assert.That(plugin.Inspect(server).ConfigWritten, Is.True);
    }

    [Test]
    public void TheSecretChanging_MakesTheConfigStale()
    {
        var (plugin, _, server) = PluginFor();
        plugin.Install(server);
        var other = new LivePluginService(Feed("a-different-secret"), Config(("Live:PluginPanelUrl", "http://127.0.0.1:5999")), NullLogger<LivePluginService>.Instance);
        Assert.That(other.Inspect(server).ConfigWritten, Is.False);
    }

    [Test]
    public void Uninstall_RemovesBoth()
    {
        var (plugin, _, server) = PluginFor();
        plugin.Install(server);
        var result = plugin.Uninstall(server);
        Assert.That(result.Success && result.Changed, Is.True);
        Assert.That(File.Exists(Path.Combine(server.Path, "plugins", "aubscraft-live.jar")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(server.Path, "plugins", "AubsCraftLive")), Is.False);
        Assert.That(plugin.Uninstall(server).Changed, Is.False, "nothing left to remove");
    }

    [Test]
    public void AServerThatIsNeitherPaperNorFabric_IsNotSupported_AndNothingIsWritten()
    {
        foreach (var loader in new[] { ServerLoader.Vanilla, ServerLoader.Forge, ServerLoader.NeoForge })
        {
            var (plugin, _, server) = PluginFor(loader);
            Assert.That(plugin.Inspect(server).Supported, Is.False, loader.ToString());
            var result = plugin.Install(server);
            Assert.That(result.Success, Is.False);
            Assert.That(Directory.GetFileSystemEntries(server.Path), Is.Empty, loader + ": nothing written");
        }
    }

    [Test]
    public void Fabric_Install_PutsTheModInMods_AndItsConfigInConfig()
    {
        var (plugin, feed, server) = PluginFor(ServerLoader.Fabric);
        Assert.That(plugin.Inspect(server).Supported, Is.True);
        var result = plugin.Install(server);
        Assert.That(result.Success && result.Changed, Is.True, result.Message);
        var jar = Path.Combine(server.Path, "mods", "aubscraft-live-fabric.jar");
        Assert.That(File.ReadAllBytes(jar), Is.EqualTo(File.ReadAllBytes(LivePluginService.ShippedFabricJar)));
        var config = File.ReadAllText(Path.Combine(server.Path, "config", "aubscraft-live.properties"));
        Assert.That(config, Does.Contain("server-id=creative").And.Contain("panel-url=http://127.0.0.1:5999").And.Contain($"secret={feed.Secret}"));
        Assert.That(Directory.Exists(Path.Combine(server.Path, "plugins")), Is.False, "a Fabric server has no plugins folder");
        var state = plugin.Inspect(server);
        Assert.That(state.JarInstalled && state.JarUpToDate && state.ConfigWritten, Is.True);
        Assert.That(plugin.Install(server).Changed, Is.False, "installing again changes nothing");

        File.WriteAllText(Path.Combine(server.Path, "config", "aubscraft-live.properties"), "secret=wrong" + Environment.NewLine);
        Assert.That(plugin.Inspect(server).ConfigWritten, Is.False);
        Assert.That(plugin.Install(server).Changed, Is.True);

        var removed = plugin.Uninstall(server);
        Assert.That(removed.Success && removed.Changed, Is.True);
        Assert.That(File.Exists(jar), Is.False);
        Assert.That(File.Exists(Path.Combine(server.Path, "config", "aubscraft-live.properties")), Is.False);
        Assert.That(plugin.Uninstall(server).Changed, Is.False);
    }

    [Test]
    public void TheFabricMod_IsInTheBuild_AndIsAJar()
    {
        Assert.That(File.Exists(LivePluginService.ShippedFabricJar), Is.True, LivePluginService.ShippedFabricJar);
        Assert.That(File.ReadAllBytes(LivePluginService.ShippedFabricJar).AsSpan(0, 2).ToArray(), Is.EqualTo("PK"u8.ToArray()));
    }

    [Test]
    public void TheState_SaysLive_OnceFramesArrive()
    {
        var (plugin, feed, server) = PluginFor();
        plugin.Install(server);
        feed.Ingest("creative", FrameBytes(100));
        var state = plugin.Inspect(server);
        Assert.That(state.Live, Is.True);
        Assert.That(state.Message, Does.StartWith("Live"));
    }

    // ---- the endpoints (the real app on real Kestrel) ----

    private sealed class AppFactory(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
        }
    }

    private static (AppFactory Factory, HttpClient Http, string ServerId) StartApp()
    {
        var dir = TestUtil.NewTempDir();
        var factory = new AppFactory(new()
        {
            ["Servers:RegistryPath"] = Path.Combine(dir, "servers.json"),
            ["Minecraft:ServerPath"] = Path.Combine(dir, "mc"),
            ["ActivityLog:FilePath"] = Path.Combine(dir, "activity-log.json"),
            ["Auth:UsersPath"] = Path.Combine(dir, "users.json"),
            ["Auth:CredentialsPath"] = Path.Combine(dir, "admin.json"),
            ["Auth:InviteCodesPath"] = Path.Combine(dir, "invite-codes.json"),
            ["Auth:WhitelistAuditPath"] = Path.Combine(dir, "whitelist-audit.json"),
            ["Moderation:BansPath"] = Path.Combine(dir, "bans.json"),
            ["Live:Secret"] = "endpoint-secret",
        });
        factory.UseKestrel(0);
        factory.StartServer();
        var address = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
        var id = factory.Services.GetRequiredService<ServerManager>().All[0].Id;
        return (factory, http, id);
    }

    private static HttpRequestMessage Post(string url, byte[] body, string? secret = "endpoint-secret", string? forwardedFor = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (secret != null) req.Headers.Add("X-Live-Secret", secret);
        if (forwardedFor != null) req.Headers.Add("X-Forwarded-For", forwardedFor);
        return req;
    }

    [Test]
    [NonParallelizable]
    public async Task Ingest_AcceptsFromThePlugin_AndRefusesEverythingElse()
    {
        var (factory, http, id) = StartApp();
        await using var _ = factory;
        using var __ = http;
        var feed = factory.Services.GetRequiredService<LiveFeedService>();

        async Task<HttpStatusCode> Send(HttpRequestMessage req) { using var r = await http.SendAsync(req); return r.StatusCode; }

        Assert.That(await Send(Post($"api/live/ingest/{id}", FrameBytes(100), secret: null)), Is.EqualTo(HttpStatusCode.Unauthorized), "no secret");
        Assert.That(await Send(Post($"api/live/ingest/{id}", FrameBytes(100), secret: "wrong")), Is.EqualTo(HttpStatusCode.Unauthorized), "wrong secret");
        Assert.That(await Send(Post($"api/live/ingest/{id}", FrameBytes(100), forwardedFor: "203.0.113.9")), Is.EqualTo(HttpStatusCode.Forbidden),
            "came through the reverse proxy: the world, not the plugin");
        Assert.That(await Send(Post("api/live/ingest/no-such-server", FrameBytes(100))), Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await Send(Post($"api/live/ingest/{id}", "garbage"u8.ToArray())), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await Send(Post($"api/live/ingest/{id}", new byte[LiveFeedService.MaxFrameBytes + 1024])), Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(feed.StatusOf(id).Frames, Is.EqualTo(0), "none of that got in");

        Assert.That(await Send(Post($"api/live/ingest/{id}", FrameBytes(100))), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(feed.StatusOf(id).Frames, Is.EqualTo(1));
        Assert.That(feed.StatusOf(id).Live, Is.True);
    }

    [Test]
    public void OnlyALoopbackPeerWithoutForwardingHeaders_IsThisMachine()
    {
        var none = new Microsoft.AspNetCore.Http.HeaderDictionary();
        var forwarded = new Microsoft.AspNetCore.Http.HeaderDictionary { ["X-Forwarded-For"] = "203.0.113.9" };
        Assert.That(LiveEndpoints.CameFromThisMachine(IPAddress.Loopback, none), Is.True);
        Assert.That(LiveEndpoints.CameFromThisMachine(IPAddress.IPv6Loopback, none), Is.True);
        Assert.That(LiveEndpoints.CameFromThisMachine(IPAddress.Loopback.MapToIPv6(), none), Is.True, "127.0.0.1 as seen by a dual-stack listener");
        Assert.That(LiveEndpoints.CameFromThisMachine(IPAddress.Parse("203.0.113.9"), none), Is.False, "the network");
        Assert.That(LiveEndpoints.CameFromThisMachine(IPAddress.Parse("192.168.1.20"), none), Is.False, "even the LAN");
        Assert.That(LiveEndpoints.CameFromThisMachine(null, none), Is.False, "unknown peer");
        Assert.That(LiveEndpoints.CameFromThisMachine(IPAddress.Loopback, forwarded), Is.False, "the reverse proxy's own loopback hop");
    }

    [Test]
    [NonParallelizable]
    public async Task AnIngestBody_WithNoLength_IsStillCapped()
    {
        var (factory, http, id) = StartApp();
        await using var _ = factory;
        using var __ = http;
        // Chunked: no Content-Length to refuse up front, so the cap has to hold while reading
        var big = new MemoryStream(new byte[LiveFeedService.MaxFrameBytes + 4096]);
        var req = new HttpRequestMessage(HttpMethod.Post, $"api/live/ingest/{id}") { Content = new StreamContent(big) };
        req.Headers.Add("X-Live-Secret", "endpoint-secret");
        req.Headers.TransferEncodingChunked = true;
        using var resp = await http.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    [NonParallelizable]
    public async Task WatchingTheFeed_NeedsALogin()
    {
        var (factory, http, id) = StartApp();
        await using var _ = factory;
        using var __ = http;
        using var status = await http.GetAsync("api/live/status");
        Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "the entity positions of a private server are not public");
        using var ws = await http.GetAsync($"api/live/ws?server={id}");
        Assert.That(ws.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var skin = await http.GetAsync("api/live/skin?url=http://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef");
        Assert.That(skin.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "the panel does not fetch for the world");
    }
}
