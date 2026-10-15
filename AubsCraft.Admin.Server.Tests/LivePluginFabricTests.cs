using System.Diagnostics;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Admin.Server.Tests.Live;
using AubsCraft.Live.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The AubsCraft Live MOD on a REAL Fabric 1.21.5 server (Fabric Loader + Fabric API, the versions the production Spooky server runs), the panel on real
/// Kestrel: the panel installs the mod (jar into mods/, config into config/), the server loads it, frames arrive with the right creatures, a real
/// (bot) player appears. The same checks as the Paper plugin's, because the viewer cannot tell the two apart.
/// </summary>
[NonParallelizable]
public class LivePluginFabricTests
{
    private const int GamePort = 25861, RconPort = 25862, PanelPort = 25863;
    private const string Secret = "fabric-test-secret";
    private const string LoaderVersion = "0.16.14", InstallerVersion = "1.0.3", ApiVersion = "0.128.2+1.21.5";

    private sealed class AppFactory(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
        }
    }

    private AppFactory _factory = null!;
    private LiveFeedService _feed = null!;
    private MinecraftRconClient _rcon = null!;
    private Process _server = null!;
    private readonly List<string> _output = [];
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const string Id = "livefabric";

    private static async Task<string> DownloadAsync(string url, string file)
    {
        if (File.Exists(file)) return file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AubsCraft-Tests/1.0 (https://github.com/LostBeard)");
        var tmp = file + ".download";
        await using (var src = await http.GetStreamAsync(url))
        await using (var dst = File.Create(tmp)) await src.CopyToAsync(dst);
        File.Move(tmp, file, overwrite: true);
        return file;
    }

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var cache = PaperServer.CacheRoot;
        var launcher = await DownloadAsync($"https://meta.fabricmc.net/v2/versions/loader/1.21.5/{LoaderVersion}/{InstallerVersion}/server/jar",
            Path.Combine(cache, $"fabric-server-1.21.5-{LoaderVersion}.jar"));
        var api = await DownloadAsync($"https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/{Uri.EscapeDataString(ApiVersion)}/fabric-api-{Uri.EscapeDataString(ApiVersion)}.jar",
            Path.Combine(cache, $"fabric-api-{ApiVersion}.jar"));

        var dir = Path.Combine(cache, "servers", Id);
        Directory.CreateDirectory(dir);
        // A clean world and mods folder each run (the downloaded vanilla server and libraries stay)
        foreach (var d in new[] { "world", "mods", "config", "logs" }) if (Directory.Exists(Path.Combine(dir, d))) Directory.Delete(Path.Combine(dir, d), true);
        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        File.Copy(api, Path.Combine(dir, "mods", Path.GetFileName(api)), true);
        File.WriteAllText(Path.Combine(dir, "eula.txt"), "eula=true\n");
        var rconPassword = "test-" + Guid.NewGuid().ToString("N")[..12];
        File.WriteAllText(Path.Combine(dir, "server.properties"), string.Join('\n',
            "server-ip=127.0.0.1", $"server-port={GamePort}", "enable-rcon=true", $"rcon.port={RconPort}", $"rcon.password={rconPassword}", "online-mode=false",
            "max-players=4", "level-type=minecraft:flat", $"level-seed={PaperServer.Seed}", "level-name=world", "spawn-protection=0", "view-distance=4",
            "simulation-distance=4", "enable-query=false", ""));

        var definition = new ServerDefinition
        {
            Id = Id, Name = "Live fabric", Loader = ServerLoader.Fabric, GameVersion = "1.21.5", Path = dir, ServiceName = "test-livefabric",
            RconHost = "127.0.0.1", RconPort = RconPort, RconPassword = rconPassword, GamePort = GamePort, MemoryMb = 1024,
        };
        var tmp = TestUtil.NewTempDir();
        var registryPath = Path.Combine(tmp, "servers.json");
        File.WriteAllText(registryPath, System.Text.Json.JsonSerializer.Serialize(new ServerRegistryFile { Servers = [definition] }));
        Dictionary<string, string> Settings() => new()
        {
            ["Live:Secret"] = Secret,
            ["Live:PluginPanelUrl"] = $"http://127.0.0.1:{PanelPort}",
            ["Servers:RegistryPath"] = registryPath,
            ["ActivityLog:FilePath"] = Path.Combine(tmp, "activity-log.json"),
            ["Auth:UsersPath"] = Path.Combine(tmp, "users.json"),
            ["Auth:CredentialsPath"] = Path.Combine(tmp, "admin.json"),
            ["Auth:InviteCodesPath"] = Path.Combine(tmp, "invite-codes.json"),
            ["Auth:WhitelistAuditPath"] = Path.Combine(tmp, "whitelist-audit.json"),
            ["Moderation:BansPath"] = Path.Combine(tmp, "bans.json"),
        };
        _factory = new AppFactory(Settings());
        _factory.UseKestrel(PanelPort);
        _factory.StartServer();
        _feed = _factory.Services.GetRequiredService<LiveFeedService>();

        // The panel's own installer puts the mod in place, exactly as the Plugins page does
        var install = _factory.Services.GetRequiredService<LivePluginService>().Install(definition);
        Assert.That(install.Success && install.Changed, Is.True, install.Message);

        var psi = new ProcessStartInfo(await Jdk.JavaAsync(), ["-Xms512M", "-Xmx1G", "-jar", launcher, "--nogui"])
        {
            WorkingDirectory = dir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        _server = Process.Start(psi)!;
        KillOnExitJob.Add(_server);
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (_output) _output.Add(line);
            if (line.Contains("Done (") && line.Contains("For help")) _done.TrySetResult();
        }
        _server.OutputDataReceived += (_, e) => OnLine(e.Data);
        _server.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
        var finished = await Task.WhenAny(_done.Task, _server.WaitForExitAsync(), Task.Delay(TimeSpan.FromMinutes(6)));
        if (finished != _done.Task) throw new InvalidOperationException("Fabric did not finish starting:\n" + string.Join('\n', Tail(40)));

        _rcon = new MinecraftRconClient("127.0.0.1", RconPort);
        Assert.That(await _rcon.ConnectAsync(rconPassword), Is.True, "RCON");
    }

    private List<string> Tail(int n) { lock (_output) return _output.TakeLast(n).ToList(); }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_rcon != null) await _rcon.DisposeAsync();
        if (_factory != null) await _factory.DisposeAsync();
        if (_server is { HasExited: false })
        {
            try { await _server.StandardInput.WriteLineAsync("stop"); await _server.StandardInput.FlushAsync(); } catch (IOException) { }
            if (!_server.WaitForExit(60_000)) _server.Kill(entireProcessTree: true);
        }
        _server?.Dispose();
    }

    private LiveFrame? Latest()
    {
        using var sub = _feed.Subscribe(Id);
        return sub.Frames.TryRead(out var bytes) ? LiveFrameCodec.Parse(bytes) : null;
    }

    private async Task<LiveFrame> WaitForFrameAsync(Func<LiveFrame, bool> ok, string what, int seconds = 30)
    {
        LiveFrame? last = null;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            last = Latest();
            if (last != null && ok(last)) return last;
            await Task.Delay(250);
        }
        throw new AssertionException($"Timed out waiting for {what}. Last frame: {(last == null ? "none" : $"{last.Worlds.Sum(w => w.Entities.Count)} entities")}\n" + string.Join('\n', Tail(15)));
    }

    private static LiveEntity? Find(LiveFrame f, Func<LiveEntity, bool> match) => f.Worlds.SelectMany(w => w.Entities).Cast<LiveEntity?>().FirstOrDefault(e => match(e!.Value));

    [Test, Order(1)]
    public async Task TheMod_LoadsOnTheRealFabricServer_AndTheFramesArriveAtThePanel()
    {
        await _rcon.SendCommandAsync("gamerule doMobSpawning false");
        var frame = await WaitForFrameAsync(_ => true, "the first frame");
        Assert.That(Tail(5000).Any(l => l.Contains("Sending the world's activity to")), Is.True, "the server loaded the mod");
        Assert.That(frame.Worlds[0].Name, Is.EqualTo("overworld"), "the main world comes first");
        Assert.That(_feed.StatusOf(Id).Live, Is.True);
        Assert.That(frame.FullTime, Is.GreaterThan(0));
    }

    [Test, Order(2)]
    public async Task SummonedCreatures_ArriveWithTheirType_Place_Age_Colour_AndName()
    {
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon cow 8.5 100 8.5 {NoAI:1b,NoGravity:1b,Rotation:[90f,0f]}");
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon sheep 12.5 100 8.5 {NoAI:1b,NoGravity:1b,Color:6b,CustomName:'Dolly'}");
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon pig 16.5 100 8.5 {NoAI:1b,NoGravity:1b,Age:-24000}");
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon villager 20.5 100 8.5 {NoAI:1b,NoGravity:1b}");

        var frame = await WaitForFrameAsync(f => Find(f, e => e.Type == "pig") != null && Find(f, e => e.Type == "villager") != null, "the summoned creatures");
        var cow = Find(frame, e => e.Type == "cow") ?? throw new AssertionException("no cow");
        Assert.That(cow.X, Is.EqualTo(8.5f).Within(0.01f));
        Assert.That(cow.Y, Is.EqualTo(100f).Within(0.01f));
        Assert.That(cow.Yaw, Is.EqualTo(90f).Within(1.5f));
        var sheep = Find(frame, e => e.Type == "sheep") ?? throw new AssertionException("no sheep");
        Assert.That(sheep.Variant, Is.EqualTo("pink"));
        Assert.That(sheep.Name, Is.EqualTo("Dolly"));
        Assert.That((Find(frame, e => e.Type == "pig") ?? throw new AssertionException("no pig")).Baby, Is.True);
        Assert.That(Find(frame, e => e.Type == "villager")!.Value.Variant, Is.Not.Null.And.Not.Empty, "a villager reports its profession");
    }

    [Test, Order(3)]
    public async Task AMovedCreature_IsWhereItWent_AndADeadOneIsGone()
    {
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon chicken 30.5 100 30.5 {NoAI:1b,NoGravity:1b}");
        await WaitForFrameAsync(f => Find(f, e => e.Type == "chicken") != null, "the chicken");
        await _rcon.SendCommandAsync("tp @e[type=chicken,limit=1] 34.5 101 30.5");
        await WaitForFrameAsync(f => Find(f, e => e.Type == "chicken" && MathF.Abs(e.X - 34.5f) < 0.05f && MathF.Abs(e.Y - 101f) < 0.05f) != null, "the chicken at its new place");
        var killed = await _rcon.SendCommandAsync("kill @e[type=chicken]");
        await WaitForFrameAsync(f => Find(f, e => e.Type == "chicken") == null, "the chicken gone (kill said: " + killed + ")");
    }

    [Test, Order(4)]
    public async Task TheFrames_FollowTheWorldsClock_AndItsWeather()
    {
        await _rcon.SendCommandAsync("time set 13000");
        await WaitForFrameAsync(f => f.DayTime is >= 13000 and < 13200, "night", seconds: 15);
        await _rcon.SendCommandAsync("weather thunder");
        await WaitForFrameAsync(f => f.Raining && f.Thundering, "a thunderstorm", seconds: 15);
        await _rcon.SendCommandAsync("weather clear");
        await WaitForFrameAsync(f => !f.Raining && !f.Thundering, "clear weather", seconds: 15);
    }

    [Test, Order(5)]
    public async Task APlayer_IsInTheFrames_WithItsNameUuidAndMode()
    {
        await using var bot = TestBot.Start("127.0.0.1", GamePort, "FabricWatcher");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        var frame = await WaitForFrameAsync(f => f.Worlds.SelectMany(w => w.Players).Any(p => p.Name == "FabricWatcher"), "the player", seconds: 30);
        var world = frame.Worlds.First(w => w.Players.Any(p => p.Name == "FabricWatcher"));
        var player = world.Players.First(p => p.Name == "FabricWatcher");
        Assert.That(Guid.TryParse(player.Uuid, out _), Is.True, player.Uuid);
        Assert.That(player.Health, Is.GreaterThan(0.9f));
        Assert.That(player.GameMode, Is.InRange(0, 3));
        var entity = world.Entities.First(e => e.Id == player.EntityId);
        Assert.That(entity.IsPlayer && entity.Type == "player", Is.True);
    }

    [Test, Order(6)]
    public async Task TheFramesKeepComing_AtTheConfiguredRate()
    {
        long before = _feed.StatusOf(Id).Frames;
        await Task.Delay(3000);
        long got = _feed.StatusOf(Id).Frames - before;
        Assert.That(got, Is.InRange(8, 20), "frames in 3 seconds");
    }
}
