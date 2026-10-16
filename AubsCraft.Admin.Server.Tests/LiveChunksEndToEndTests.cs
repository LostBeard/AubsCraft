using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Admin.Server.Tests.Live;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The world as it is NOW, end to end: the real AubsCraft Live plugin in a REAL Paper server, the real panel, a block changed on the server. The panel must say the chunk changed (the message the viewers
/// get on the live socket), then serve the new block from <c>/api/world/chunk-ids</c> (the region file has not been written yet), and take it away again when the block is removed.
/// </summary>
[NonParallelizable]
public class LiveChunksEndToEndTests
{
    private const string Secret = "chunks-e2e-secret";

    private sealed class AppFactory(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
            builder.ConfigureLogging(l => l.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        }
    }

    private PaperServer _paper = null!;
    private AppFactory _factory = null!;
    private HttpClient _http = null!;
    private Uri _base = null!;
    private string _cookie = "";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        int panelPort = FreePort();
        var panelUrl = $"http://127.0.0.1:{panelPort}";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Live:Secret"] = Secret, ["Live:PluginPanelUrl"] = panelUrl }).Build();
        var plugins = new LivePluginService(new LiveFeedService(config, NullLogger<LiveFeedService>.Instance), config, NullLogger<LivePluginService>.Instance);

        // The plugin goes into the server's folder before it starts, pointed at the panel that is about to be there
        _paper = await PaperServer.StartAsync("chunks", 25603, 25613, 2, "minecraft:flat", "world", TimeSpan.FromMinutes(6), beforeStart: dir =>
        {
            var result = plugins.Install(new ServerDefinition { Id = "lc", Name = "chunks", Path = dir, Loader = ServerLoader.Paper });
            Assert.That(result.Success, Is.True, result.Message);
            return Task.CompletedTask;
        });

        var temp = TestUtil.NewTempDir();
        var registryPath = Path.Combine(temp, "servers.json");
        File.WriteAllText(registryPath, System.Text.Json.JsonSerializer.Serialize(new ServerRegistryFile { Servers = [_paper.ToDefinition("lc")] }));
        _factory = new AppFactory(new()
        {
            ["Servers:RegistryPath"] = registryPath,
            ["ActivityLog:FilePath"] = Path.Combine(temp, "activity-log.json"),
            ["Auth:UsersPath"] = Path.Combine(temp, "users.json"),
            ["Auth:CredentialsPath"] = Path.Combine(temp, "admin.json"),
            ["Auth:InviteCodesPath"] = Path.Combine(temp, "invite-codes.json"),
            ["Auth:WhitelistAuditPath"] = Path.Combine(temp, "whitelist-audit.json"),
            ["Moderation:BansPath"] = Path.Combine(temp, "bans.json"),
            ["Live:Secret"] = Secret,
            ["Blocks:RegistryPath"] = Path.Combine(temp, "blocks.json"),
        });
        _factory.UseKestrel(panelPort);
        _factory.StartServer();
        _base = new Uri(panelUrl + "/");
        _http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = _base };
        Assert.That((await _http.PostAsJsonAsync("/api/auth/setup", new { Username = "owner", Password = "test-password" })).IsSuccessStatusCode, Is.True);
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { Username = "owner", Password = "test-password" });
        Assert.That(login.IsSuccessStatusCode, Is.True);
        _cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        _http?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
        if (_paper != null) await _paper.DisposeAsync();
    }


    // The plugin has found the panel: its frames are arriving
    private async Task WaitForPluginAsync()
    {
        for (int i = 0; i < 120; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/live/status");
            req.Headers.Add("Cookie", _cookie);
            var body = await (await _http.SendAsync(req)).Content.ReadAsStringAsync();
            if (body.Contains("\"live\":true", StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("The plugin never reached the panel");
    }
    private async Task<ushort[]> ChunkIdsAsync(int cx, int cz)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/world/chunk-ids/{cx}/{cz}?server=lc");
        req.Headers.Add("Cookie", _cookie);
        var bytes = await (await _http.SendAsync(req)).Content.ReadAsByteArrayAsync();
        Assert.That(bytes.Length, Is.EqualTo(98304 * 2));
        var ids = new ushort[98304];
        Buffer.BlockCopy(bytes, 0, ids, 0, bytes.Length);
        return ids;
    }

    private async Task<string[]> BlockNamesAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/world/blocks");
        req.Headers.Add("Cookie", _cookie);
        return (await (await _http.SendAsync(req)).Content.ReadFromJsonAsync<string[]>())!;
    }

    // Waits for a message about a chunk on the live socket (ACHG: a count, then x, z, border); returns its border mask
    private static async Task<int> NextChangeAsync(ClientWebSocket ws, int cx, int cz, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        var buffer = new byte[1 << 20];
        while (DateTime.UtcNow < deadline)
        {
            int total = 0;
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None).WaitAsync(deadline - DateTime.UtcNow);
                total += r.Count;
            } while (!r.EndOfMessage);
            if (total < 6 || buffer[0] != 'A' || buffer[1] != 'C' || buffer[2] != 'H' || buffer[3] != 'G') continue;   // a frame of creatures
            int n = BitConverter.ToUInt16(buffer, 4);
            for (int i = 0; i < n; i++)
            {
                int at = 6 + i * 9;
                if (BitConverter.ToInt32(buffer, at) == cx && BitConverter.ToInt32(buffer, at + 4) == cz) return buffer[at + 8];
            }
        }
        throw new TimeoutException($"No change message for chunk ({cx}, {cz})");
    }

    [Test]
    public async Task ABlockChangedOnTheServer_IsInTheChunkTheViewerFetches_BeforeTheFileHasIt()
    {
        await using var rcon = await TestServers.RconAsync(_paper);
        await WaitForPluginAsync();
        // The flat world's surface is y -61 (grass); the chunk holds (5, -60, 5)
        const int x = 5, y = -60, z = 5;
        int index = x + z * 16 + (y + 64) * 256;

        // Let the plugin and the panel find each other (the plugin starts posting once the server is up), then open the live socket like a viewer
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Cookie", _cookie);
        await ws.ConnectAsync(new UriBuilder(new Uri(_base, "api/live/ws?server=lc")) { Scheme = "ws" }.Uri, CancellationToken.None);

        await rcon.SendCommandAsync("save-all flush");   // a fresh world has no region files until it is saved
        await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/world/cache/clear?server=lc") { Headers = { { "Cookie", _cookie } } });
        var before = await ChunkIdsAsync(0, 0);
        var names = await BlockNamesAsync();
        Assert.That(names[before[index]], Is.EqualTo("minecraft:air"), "nothing there yet");

        await rcon.SendCommandAsync($"setblock {x} {y} {z} minecraft:diamond_block");
        int border = await NextChangeAsync(ws, 0, 0, TimeSpan.FromSeconds(30));
        TestContext.Progress.WriteLine($"[chunks] change message, border mask {border}");

        var after = await ChunkIdsAsync(0, 0);
        names = await BlockNamesAsync();
        Assert.That(names[after[index]], Is.EqualTo("minecraft:diamond_block"), "the viewer's next fetch has the new block");
        Assert.That(after.Where((id, i) => id != before[i]).Count(), Is.GreaterThanOrEqualTo(1));
        // Everything else in the chunk is as it was (the flat world's layers)
        Assert.That(names[after[0]], Is.EqualTo(names[before[0]]));
        Assert.That(names[after[4 + 4 * 256 + 0 * 16]], Is.EqualTo(names[before[4 + 4 * 256 + 0 * 16]]));
    }

    [Test]
    public async Task ARemovedBlockIsGone_ALightIsLit_AndABlockOnTheEdgeSaysSo()
    {
        await using var rcon = await TestServers.RconAsync(_paper);
        await WaitForPluginAsync();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Cookie", _cookie);
        await ws.ConnectAsync(new UriBuilder(new Uri(_base, "api/live/ws?server=lc")) { Scheme = "ws" }.Uri, CancellationToken.None);

        // A sea lantern: a block that gives off light 15, in the middle of the chunk
        await rcon.SendCommandAsync("setblock 8 -60 8 minecraft:sea_lantern");
        await NextChangeAsync(ws, 0, 0, TimeSpan.FromSeconds(30));
        int lantern = 8 + 8 * 16 + (-60 + 64) * 256;
        var names = await BlockNamesAsync();
        Assert.That(names[(await ChunkIdsAsync(0, 0))[lantern]], Is.EqualTo("minecraft:sea_lantern"));
        var lightReq = new HttpRequestMessage(HttpMethod.Get, "/api/world/chunk-light/0/0?server=lc");
        lightReq.Headers.Add("Cookie", _cookie);
        var light = await (await _http.SendAsync(lightReq)).Content.ReadAsByteArrayAsync();
        Assert.That(light.Length, Is.EqualTo(98304));
        Assert.That(light[lantern] & 15, Is.EqualTo(15), "the lantern's own cell holds its light");
        Assert.That(light[lantern + 1] & 15, Is.GreaterThanOrEqualTo(13), "and the air beside it nearly as much");

        // Taken away again
        await rcon.SendCommandAsync("setblock 8 -60 8 minecraft:air");
        await NextChangeAsync(ws, 0, 0, TimeSpan.FromSeconds(30));
        names = await BlockNamesAsync();
        Assert.That(names[(await ChunkIdsAsync(0, 0))[lantern]], Is.EqualTo("minecraft:air"));

        // A block on the chunk's west edge: the neighbour toward -X must be meshed again too
        await rcon.SendCommandAsync("setblock 0 -60 8 minecraft:stone");
        int border = await NextChangeAsync(ws, 0, 0, TimeSpan.FromSeconds(30));
        Assert.That(border & 1, Is.EqualTo(1), "x = 0 touches the -X neighbour");
        await rcon.SendCommandAsync("setblock 0 -60 8 minecraft:air");
        await NextChangeAsync(ws, 0, 0, TimeSpan.FromSeconds(30));

        // A fill across two chunks tells both
        await rcon.SendCommandAsync("fill 14 -60 14 17 -60 17 minecraft:stone");
        await NextChangeAsync(ws, 0, 0, TimeSpan.FromSeconds(30));
        await NextChangeAsync(ws, 1, 1, TimeSpan.FromSeconds(30));
        await rcon.SendCommandAsync("fill 14 -60 14 17 -60 17 minecraft:air");
    }
}
