using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The whole admin app (real Program: DI, hosted monitor + log tailing, endpoints, SignalR hub) on REAL Kestrel
/// over two REAL Paper servers, driven the way the browser drives it: anonymous public status, owner setup +
/// cookie login, hub calls with a server id over SignalR's normal transport, and the per-server world HTTP +
/// WebSocket endpoints over real sockets. (TestServer's in-memory WebSocket drops frames still queued when the
/// handler returns, which real Kestrel delivers - so these tests use Kestrel.)
/// </summary>
[NonParallelizable]
public class AppEndToEndTests
{
    private AppFactory _factory = null!;
    private HttpClient _http = null!;
    private string _cookie = "";
    private HubConnection _hub = null!;
    private Uri _base = null!;

    /// <summary>
    /// The app with every state file redirected to a temp folder. A subclass (not WithWebHostBuilder): the
    /// factory WithWebHostBuilder returns hands host creation back to its parent, so UseKestrel on it is lost.
    /// </summary>
    private sealed class AppFactory(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
            // The app's warnings and errors go to the test output (a failing endpoint logs its exception here).
            builder.ConfigureLogging(l => l.AddSimpleConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
        }
    }

    [OneTimeSetUp]
    public async Task StartAppAsync()
    {
        var dir = TestUtil.NewTempDir();
        var registryPath = Path.Combine(dir, "servers.json");
        // servers.json as the panel will hold it: alpha is the primary (first) server.
        System.IO.File.WriteAllText(registryPath, System.Text.Json.JsonSerializer.Serialize(new ServerRegistryFile
        {
            Servers = [TestServers.Alpha.ToDefinition("alpha"), TestServers.Bravo.ToDefinition("bravo")],
        }));

        _factory = new AppFactory(new()
        {
            ["Servers:RegistryPath"] = registryPath,
            ["ActivityLog:FilePath"] = Path.Combine(dir, "activity-log.json"),
            ["Auth:UsersPath"] = Path.Combine(dir, "users.json"),
            ["Auth:CredentialsPath"] = Path.Combine(dir, "admin.json"),
            ["Auth:InviteCodesPath"] = Path.Combine(dir, "invite-codes.json"),
            ["Auth:WhitelistAuditPath"] = Path.Combine(dir, "whitelist-audit.json"),
            ["Moderation:BansPath"] = Path.Combine(dir, "bans.json"),
        });
        _factory.UseKestrel(0); // any free port
        _factory.StartServer();
        // The address Kestrel actually bound.
        var addresses = _factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses;
        _base = new Uri(addresses.First().TrimEnd('/') + "/");
        _http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = _base };

        var setup = await _http.PostAsJsonAsync("/api/auth/setup", new { Username = "owner", Password = "test-password" });
        Assert.That(setup.IsSuccessStatusCode, Is.True, await setup.Content.ReadAsStringAsync());
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { Username = "owner", Password = "test-password" });
        Assert.That(login.IsSuccessStatusCode, Is.True, await login.Content.ReadAsStringAsync());
        _cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_base, "hubs/server"), o => o.Headers["Cookie"] = _cookie)
            .Build();
        await _hub.StartAsync();
    }

    [OneTimeTearDown]
    public async Task StopAppAsync()
    {
        if (_hub != null) await _hub.DisposeAsync();
        _http?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
    }

    private HttpRequestMessage Authed(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Cookie", _cookie);
        return req;
    }

    [Test, Order(1)]
    public async Task PublicStatus_SumsTheNetwork_AndListsEachServer()
    {
        PublicStatusDto? status = null;
        await TestUtil.WaitUntilAsync(async () =>
        {
            status = await _http.GetFromJsonAsync<PublicStatusDto>("/api/public/status");
            return status?.Servers?.Count == 2 && status.Servers.All(s => s.Connected);
        }, TimeSpan.FromSeconds(30), "the monitor reports both servers online");

        Assert.That(status!.Connected, Is.True);
        Assert.That(status.Max, Is.EqualTo(7 + 9));
        Assert.That(status.Servers!.Select(s => (s.Id, s.Max)), Is.EqualTo(new[] { ("alpha", 7), ("bravo", 9) }));
    }

    [Test, Order(2)]
    public async Task Hub_ListsServers_AndRoutesEachCallToTheNamedServer()
    {
        var servers = await _hub.InvokeAsync<List<ServerSummaryDto>>("GetServers");
        Assert.That(servers.Select(s => s.Id), Is.EqualTo(new[] { "alpha", "bravo" }));
        Assert.That(servers[0].IsPrimary, Is.True);
        Assert.That(servers[1].IsPrimary, Is.False);
        Assert.That(servers.Select(s => s.Loader), Has.All.EqualTo("Paper"));

        var bravo = await _hub.InvokeAsync<ServerStatusDto?>("GetCurrentStatus", "bravo");
        Assert.That(bravo?.Max, Is.EqualTo(9));
        Assert.That(bravo?.ServerId, Is.EqualTo("bravo"));

        // A console command lands on the named server only.
        var alphaList = await _hub.InvokeAsync<string>("SendCommand", "alpha", "list");
        var bravoList = await _hub.InvokeAsync<string>("SendCommand", "bravo", "list");
        Assert.That(alphaList, Does.Contain("max of 7"));
        Assert.That(bravoList, Does.Contain("max of 9"));
    }

    [Test, Order(3)]
    public void Hub_RejectsAnUnknownServer()
    {
        var ex = Assert.ThrowsAsync<HubException>(() => _hub.InvokeAsync<string>("SendCommand", "nope", "list"));
        Assert.That(ex!.Message, Does.Contain("Unknown server 'nope'"));
    }

    [Test, Order(4)]
    public async Task Hub_PushesTheServerListAndTaggedStatuses()
    {
        var lists = new TaskCompletionSource<List<ServerSummaryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusIds = new HashSet<string>();
        var bothStatuses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var a = _hub.On<List<ServerSummaryDto>>("ReceiveServerList", l => lists.TrySetResult(l));
        using var b = _hub.On<ServerStatusDto>("ReceiveServerStatus", s =>
        {
            lock (statusIds)
            {
                if (s.ServerId != null) statusIds.Add(s.ServerId);
                if (statusIds.Count == 2) bothStatuses.TrySetResult();
            }
        });

        var list = await lists.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await bothStatuses.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(list.Select(s => (s.Id, s.Connected, s.Max)), Is.EqualTo(new[] { ("alpha", true, 7), ("bravo", true, 9) }));
        Assert.That(statusIds, Is.EquivalentTo(new[] { "alpha", "bravo" }));
    }

    [Test, Order(5)]
    public async Task Hub_WhitelistAdd_ReachesEveryServer()
    {
        await TestServers.ResetListsAsync(TestServers.Alpha);
        await TestServers.ResetListsAsync(TestServers.Bravo);
        await _hub.InvokeAsync<string>("WhitelistAdd", "HubPlayer");
        Assert.That(await _hub.InvokeAsync<List<string>>("GetWhitelist", "alpha"), Does.Contain("HubPlayer"));
        Assert.That(await _hub.InvokeAsync<List<string>>("GetWhitelist", "bravo"), Does.Contain("HubPlayer"));
    }

    [Test, Order(6)]
    public async Task WorldEndpoints_ServeTheNamedServersWorld()
    {
        foreach (var s in new[] { TestServers.Alpha, TestServers.Bravo })
        {
            await using var rcon = await TestServers.RconAsync(s);
            await rcon.SendCommandAsync("save-all flush");
        }
        await _http.SendAsync(Authed(HttpMethod.Post, "/api/world/cache/clear?server=alpha"));
        await _http.SendAsync(Authed(HttpMethod.Post, "/api/world/cache/clear?server=bravo"));

        var alpha = await ReadChunkPaletteAsync("alpha");
        var bravo = await ReadChunkPaletteAsync("bravo");
        var primary = await ReadChunkPaletteAsync(null);
        Assert.That(alpha, Does.Not.Contain("minecraft:stone"), "alpha is a flat world");
        Assert.That(bravo.Any(n => n is "minecraft:stone" or "minecraft:deepslate"), Is.True, "bravo is a normal world");
        Assert.That(primary, Is.EqualTo(alpha), "no ?server= = the primary server");

        var missing = await _http.SendAsync(Authed(HttpMethod.Get, "/api/world/chunk/0/0?server=nope"));
        Assert.That((int)missing.StatusCode, Is.EqualTo(404));
    }

    /// <summary>
    /// The palette of the first chunk /api/world/chunks lists for the server, from GET /api/world/chunk/{x}/{z}
    /// ([int32 count][int32 len + utf8]...). Both requests carry the same ?server= (none = the primary).
    /// </summary>
    private async Task<List<string>> ReadChunkPaletteAsync(string? server)
    {
        var query = server == null ? "" : $"?server={server}";
        var listResp = await _http.SendAsync(Authed(HttpMethod.Get, "/api/world/chunks" + query));
        Assert.That(listResp.IsSuccessStatusCode, Is.True, $"/api/world/chunks{query}: {(int)listResp.StatusCode}");
        var chunks = await listResp.Content.ReadFromJsonAsync<List<ChunkCoord>>();
        Assert.That(chunks, Is.Not.Empty, $"/api/world/chunks{query} listed no chunks");
        var url = $"/api/world/chunk/{chunks![0].X}/{chunks[0].Z}{query}";
        var resp = await _http.SendAsync(Authed(HttpMethod.Get, url));
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"{url}: {(int)resp.StatusCode}");
        return ParsePalette(await resp.Content.ReadAsByteArrayAsync(), 0);
    }

    private static List<string> ParsePalette(byte[] data, int offset)
    {
        var count = BitConverter.ToInt32(data, offset);
        offset += 4;
        var palette = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var len = BitConverter.ToInt32(data, offset);
            palette.Add(Encoding.UTF8.GetString(data, offset + 4, len));
            offset += 4 + len;
        }
        return palette;
    }

    [Test, Order(7)]
    public async Task WorldWebSocket_StreamsTheNamedServersHeightmaps()
    {
        // Saved chunks only: a fresh world's region files list nothing until the server flushes them.
        await using (var rcon = await TestServers.RconAsync(TestServers.Bravo))
            await rcon.SendCommandAsync("save-all flush");
        await _http.SendAsync(Authed(HttpMethod.Post, "/api/world/cache/clear?server=bravo"));

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Cookie", _cookie);
        await ws.ConnectAsync(new UriBuilder(new Uri(_base, "api/world/ws?server=bravo")) { Scheme = "ws" }.Uri, CancellationToken.None);

        // Frame: [int32 cx][int32 cz][palette...][heights...]
        var buf = new byte[1 << 20];
        var total = 0;
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, total, buf.Length - total), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            total += r.Count;
        } while (!r.EndOfMessage);

        Assert.That(r.MessageType, Is.EqualTo(WebSocketMessageType.Binary));
        var cx = BitConverter.ToInt32(buf, 0);
        var cz = BitConverter.ToInt32(buf, 4);
        var palette = ParsePalette(buf, 8);
        // Every bravo chunk near spawn is normal terrain: it has stone or deepslate somewhere; alpha's never do.
        Assert.That(palette.Any(n => n is "minecraft:stone" or "minecraft:deepslate"), Is.True,
            $"chunk ({cx},{cz}) palette: {string.Join(", ", palette)}");
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
