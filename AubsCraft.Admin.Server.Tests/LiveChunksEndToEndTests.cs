using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Editing;
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

    // ---- editing the world: an admin's request -> the plugin applies it on the server's main thread -> the world, and its undo ----

    private async Task<(System.Net.HttpStatusCode Status, EditResult? Result, string Body)> PostEditAsync(string url, object? body, string? cookie = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = body == null ? null : JsonContent.Create(body) };
        req.Headers.Add("Cookie", cookie ?? _cookie);
        var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        EditResult? result = null;
        try { result = System.Text.Json.JsonSerializer.Deserialize<EditResult>(text, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); } catch (System.Text.Json.JsonException) { }
        return (resp.StatusCode, result, text);
    }

    private static object Fill(int[] from, int[] to, string block, string? only = null, bool hollow = false)
        => new { op = "fill", from, to, block, only, hollow };

    private static async Task<bool> IsBlockAsync(MinecraftRconClient rcon, int x, int y, int z, string block)
        => (await rcon.SendCommandAsync($"execute if block {x} {y} {z} {block}")).Contains("passed");

    [Test]
    public async Task AnAdminsFill_ChangesTheWorld_AndUndoAndRedoPutItBackAndForth()
    {
        await using var rcon = await TestServers.RconAsync(_paper);
        await WaitForPluginAsync();
        // The channel is up once the plugin has asked
        var status = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/world/edit/status?server=lc") { Headers = { { "Cookie", _cookie } } });
        for (int i = 0; i < 40 && !(await status.Content.ReadAsStringAsync()).Contains("true"); i++)
        {
            await Task.Delay(500);
            status = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/world/edit/status?server=lc") { Headers = { { "Cookie", _cookie } } });
        }
        Assert.That(await status.Content.ReadAsStringAsync(), Does.Contain("\"available\":true"), "the plugin's long poll is there");

        var (code, result, body) = await PostEditAsync("/api/world/edit?server=lc", new { label = "gold floor", ops = new[] { Fill([20, -60, 20], [23, -60, 23], "minecraft:gold_block") } });
        Assert.That(code, Is.EqualTo(System.Net.HttpStatusCode.OK), body);
        Assert.That(result!.Ok, Is.True, result.Error);
        Assert.That(result.Placed, Is.EqualTo(16));
        Assert.That(result.Applied, Is.True);
        Assert.That(await IsBlockAsync(rcon, 21, -60, 21, "minecraft:gold_block"), Is.True, "the block is in the world");
        Assert.That(await IsBlockAsync(rcon, 24, -60, 21, "minecraft:gold_block"), Is.False, "and only where it was asked");

        // The viewers are told, and the chunk the panel serves has it
        await Task.Delay(1500);
        var ids = await ChunkIdsAsync(1, 1);
        var names = await BlockNamesAsync();
        Assert.That(names[ids[(20 & 15) + (20 & 15) * 16 + 4 * 256]], Is.EqualTo("minecraft:gold_block"));

        // Undo: air again. Undo again (redo): gold again.
        var undo = await PostEditAsync($"/api/world/edit/undo?server=lc&edit={result.Edit}", null);
        Assert.That(undo.Result!.Ok, Is.True, undo.Body);
        Assert.That(undo.Result.Applied, Is.False);
        Assert.That(undo.Result.Placed, Is.EqualTo(16));
        Assert.That(await IsBlockAsync(rcon, 21, -60, 21, "minecraft:air"), Is.True, "undone");
        var redo = await PostEditAsync($"/api/world/edit/undo?server=lc&edit={result.Edit}", null);
        Assert.That(redo.Result!.Applied, Is.True);
        Assert.That(await IsBlockAsync(rcon, 21, -60, 21, "minecraft:gold_block"), Is.True, "redone");
        await PostEditAsync($"/api/world/edit/undo?server=lc&edit={result.Edit}", null);
        Assert.That(await IsBlockAsync(rcon, 21, -60, 21, "minecraft:air"), Is.True, "and gone again");

        // An edit that is not remembered says so
        var unknown = await PostEditAsync("/api/world/edit/undo?server=lc&edit=no-such-edit", null);
        Assert.That(unknown.Result!.Ok, Is.False);
        Assert.That(unknown.Result.Error, Does.Contain("no longer remembered"));
    }

    [Test]
    public async Task AFillOnlyWhereThereIsStone_AndAHollowBox_DoWhatTheyAreAskedTo()
    {
        await using var rcon = await TestServers.RconAsync(_paper);
        await WaitForPluginAsync();
        await rcon.SendCommandAsync("fill 40 -60 40 44 -60 40 minecraft:stone");
        // Replace only stone with glass in a wider row: the five stones change, the air beside them does not
        var only = await PostEditAsync("/api/world/edit?server=lc", new { ops = new[] { Fill([38, -60, 40], [46, -60, 40], "minecraft:glass", only: "minecraft:stone") } });
        Assert.That(only.Result!.Ok, Is.True, only.Body);
        Assert.That(only.Result.Placed, Is.EqualTo(5));
        Assert.That(only.Result.Skipped, Is.EqualTo(4));
        Assert.That(await IsBlockAsync(rcon, 42, -60, 40, "minecraft:glass"), Is.True);
        Assert.That(await IsBlockAsync(rcon, 38, -60, 40, "minecraft:air"), Is.True);

        // A 5 x 5 x 5 box with nothing inside: 125 - 27 blocks
        var hollow = await PostEditAsync("/api/world/edit?server=lc", new { ops = new[] { Fill([50, -59, 50], [54, -55, 54], "minecraft:stone", hollow: true) } });
        Assert.That(hollow.Result!.Placed, Is.EqualTo(98));
        Assert.That(await IsBlockAsync(rcon, 50, -57, 52, "minecraft:stone"), Is.True, "a wall");
        Assert.That(await IsBlockAsync(rcon, 52, -57, 52, "minecraft:air"), Is.True, "the middle is empty");
        Assert.That(await IsBlockAsync(rcon, 52, -55, 52, "minecraft:stone"), Is.True, "the roof");

        await PostEditAsync($"/api/world/edit/undo?server=lc&edit={hollow.Result.Edit}", null);
        await PostEditAsync($"/api/world/edit/undo?server=lc&edit={only.Result.Edit}", null);
        Assert.That(await IsBlockAsync(rcon, 42, -60, 40, "minecraft:stone"), Is.True, "the glass goes back to the stone that was there");
        await rcon.SendCommandAsync("fill 40 -60 40 44 -60 40 minecraft:air");
    }

    [Test]
    public async Task ASignThatIsOverwritten_ComesBackWithItsWriting()
    {
        await using var rcon = await TestServers.RconAsync(_paper);
        await WaitForPluginAsync();
        await rcon.SendCommandAsync("setblock 60 -60 60 minecraft:oak_sign[rotation=4]{front_text:{messages:['\"Hello\"','\"\"','\"\"','\"\"']}}");
        Assert.That(await rcon.SendCommandAsync("data get block 60 -60 60 front_text.messages[0]"), Does.Contain("Hello"));

        var over = await PostEditAsync("/api/world/edit?server=lc", new { label = "stone over a sign", ops = new[] { new { op = "set", at = new[] { 60, -60, 60 }, block = "minecraft:stone" } } });
        Assert.That(over.Result!.Ok, Is.True, over.Body);
        Assert.That(await IsBlockAsync(rcon, 60, -60, 60, "minecraft:stone"), Is.True);

        await PostEditAsync($"/api/world/edit/undo?server=lc&edit={over.Result.Edit}", null);
        Assert.That(await IsBlockAsync(rcon, 60, -60, 60, "minecraft:oak_sign"), Is.True, "the sign is back");
        Assert.That(await rcon.SendCommandAsync("data get block 60 -60 60 front_text.messages[0]"), Does.Contain("Hello"), "with what it said");
        await rcon.SendCommandAsync("setblock 60 -60 60 minecraft:air");
    }

    [Test]
    public async Task ASparseListOfBlocks_PlacesEachWhereItBelongs()
    {
        await using var rcon = await TestServers.RconAsync(_paper);
        await WaitForPluginAsync();
        // Three blocks from the anchor (56, -60, 56): (0,0,0) dirt, (2,0,0) stone, (0,1,-3) dirt - palette [dirt, stone]
        var data = new byte[24];
        void Record(int i, int dx, int dy, int dz, int p)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 8), (short)dx); BitConverter.TryWriteBytes(data.AsSpan(i * 8 + 2), (short)dy);
            BitConverter.TryWriteBytes(data.AsSpan(i * 8 + 4), (short)dz); BitConverter.TryWriteBytes(data.AsSpan(i * 8 + 6), (ushort)p);
        }
        Record(0, 0, 0, 0, 0); Record(1, 2, 0, 0, 1); Record(2, 0, 1, -3, 0);
        var result = await PostEditAsync("/api/world/edit?server=lc", new { ops = new object[] { new { op = "blocks", at = new[] { 56, -60, 56 }, palette = new[] { "minecraft:dirt", "minecraft:stone" }, data = Convert.ToBase64String(data) } } });
        Assert.That(result.Result!.Ok, Is.True, result.Body);
        Assert.That(result.Result.Placed, Is.EqualTo(3));
        Assert.That(await IsBlockAsync(rcon, 56, -60, 56, "minecraft:dirt"), Is.True);
        Assert.That(await IsBlockAsync(rcon, 58, -60, 56, "minecraft:stone"), Is.True);
        Assert.That(await IsBlockAsync(rcon, 56, -59, 53, "minecraft:dirt"), Is.True);
        await PostEditAsync($"/api/world/edit/undo?server=lc&edit={result.Result.Edit}", null);
        Assert.That(await IsBlockAsync(rcon, 58, -60, 56, "minecraft:air"), Is.True);
    }

    [Test]
    public async Task ABlockTheServerDoesNotKnow_IsRefusedWholesale_AndAFriendOrAStrangerCannotEdit()
    {
        await using var rcon = await TestServers.RconAsync(_paper);
        await WaitForPluginAsync();
        var unknown = await PostEditAsync("/api/world/edit?server=lc", new { ops = new[] { new { op = "set", at = new[] { 57, -60, 58 }, block = "minecraft:not_a_real_block" } } });
        Assert.That(unknown.Result!.Ok, Is.False);
        Assert.That(unknown.Result.Error, Does.Contain("Unknown block"));

        var bad = await PostEditAsync("/api/world/edit?server=lc", new { ops = new[] { new { op = "set", at = new[] { 57, 900, 58 }, block = "minecraft:stone" } } });
        Assert.That(bad.Status, Is.EqualTo(System.Net.HttpStatusCode.BadRequest), "the panel refuses a request out of the world before the server sees it");

        // A friend (the lowest role) is not allowed; a stranger is not even let in
        var auth = _factory.Services.GetRequiredService<AuthService>();
        await auth.CreateFriendAsync("buddy", "friend-password", "invite");
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { Username = "buddy", Password = "friend-password" });
        var friendCookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var asFriend = await PostEditAsync("/api/world/edit?server=lc", new { ops = new[] { new { op = "set", at = new[] { 57, -60, 58 }, block = "minecraft:stone" } } }, friendCookie);
        Assert.That(asFriend.Status, Is.EqualTo(System.Net.HttpStatusCode.Forbidden));
        var asStranger = await _http.PostAsJsonAsync("/api/world/edit?server=lc", new { ops = new[] { new { op = "set", at = new[] { 57, -60, 58 }, block = "minecraft:stone" } } });
        Assert.That((int)asStranger.StatusCode, Is.AnyOf(401, 302, 403));
        Assert.That(await IsBlockAsync(rcon, 57, -60, 58, "minecraft:air"), Is.True, "nothing was placed");

        // Made an Admin, the friend may
        await auth.SetUserRoleAsync("buddy", "Admin");
        login = await _http.PostAsJsonAsync("/api/auth/login", new { Username = "buddy", Password = "friend-password" });
        friendCookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var asAdmin = await PostEditAsync("/api/world/edit?server=lc", new { label = "an admin's block", ops = new[] { new { op = "set", at = new[] { 57, -60, 58 }, block = "minecraft:stone" } } }, friendCookie);
        Assert.That(asAdmin.Result!.Ok, Is.True, asAdmin.Body);
        await PostEditAsync($"/api/world/edit/undo?server=lc&edit={asAdmin.Result.Edit}", null);
    }
}
