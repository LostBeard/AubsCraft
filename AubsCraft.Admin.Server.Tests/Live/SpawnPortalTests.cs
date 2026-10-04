using System.Globalization;
using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// SpawnPortalService on REAL servers behind a REAL proxy: a Paper server with Advanced Portals and a Fabric server
/// with ProxyPortal (created by ServerProvisioningService). The portals are built from the panel alone, never on a
/// built block, and a real client goes round trip through both without bouncing back.
/// </summary>
[NonParallelizable]
public class SpawnPortalTests
{
    private const int ProxyPort = 25820, MainPort = 25821, MainRcon = 25822, ProxyRcon = 25823;
    private const string MainService = "minecraft-spmain", ProxyServiceName = "velocity-sp";

    private sealed class Runner(string serversRoot, string proxyDir) : IServiceRunner
    {
        public readonly Dictionary<string, EnvFileProcess> Servers = [];
        public PaperServer? Main;
        public VelocityProcess? Proxy;
        public async Task StartAsync(string name, CancellationToken ct = default)
        {
            if (name == MainService) Main = await PaperServer.StartAsync("spmain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6), fresh: false);
            else if (name == ProxyServiceName) Proxy = await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6));
            else Servers[name] = await EnvFileProcess.StartAsync(Path.Combine(serversRoot, name["minecraft@".Length..]));
        }
        public async Task StopAsync(string name, CancellationToken ct = default)
        {
            if (name == MainService) { if (Main != null) await Main.DisposeAsync(); Main = null; }
            else if (name == ProxyServiceName) { if (Proxy != null) await Proxy.DisposeAsync(); Proxy = null; }
            else if (Servers.Remove(name, out var p)) await p.DisposeAsync();
        }
        public Task EnableAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisableAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly AddonDownloader _downloader = new();
    private ServerRegistry _registry = null!;
    private Runner _runner = null!;
    private SpawnPortalService _portals = null!;
    private ServerDefinition _main = null!, _fabric = null!;
    private PortalDefinition _toFabric = null!, _toMain = null!;
    private readonly List<(int x, int z)> _glass = [];

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var dir = TestUtil.NewTempDir();
        var serversRoot = Path.Combine(PaperServer.CacheRoot, "spawnportal-servers");
        if (Directory.Exists(serversRoot)) Directory.Delete(serversRoot, true);
        var proxyDir = Path.Combine(PaperServer.CacheRoot, "velocity-sp");
        foreach (var f in new[] { "velocity.toml", "forwarding.secret" }) { var p = Path.Combine(proxyDir, f); if (File.Exists(p)) File.Delete(p); }
        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json")), ("Servers:Root", serversRoot));
        _registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        _registry.Remove(ServerRegistry.LegacyServerId);
        var proxyService = new ProxyService(_downloader, TestUtil.Log<ProxyService>());
        _runner = new Runner(serversRoot, proxyDir);
        _portals = new SpawnPortalService(_registry, TestUtil.Loggers);

        var ap = await _downloader.ModrinthAsync("advanced-portals", "paper", PaperServer.GameVersion);
        var main = await PaperServer.StartAsync("spmain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6),
            beforeStart: d => _downloader.DownloadAsync(ap, Path.Combine(d, "plugins", ap.FileName)));
        await main.DisposeAsync();
        _main = main.ToDefinition("spmain");
        _main.ServiceName = MainService;
        _registry.Add(_main);

        var proxy = new ProxyDefinition
        {
            Path = proxyDir, ServiceName = ProxyServiceName, BindHost = "127.0.0.1", Port = ProxyPort,
            BedrockPort = 19320, VoicePort = 24820, RconPort = ProxyRcon, OnlineMode = false,
            RconPassword = "sp-" + Guid.NewGuid().ToString("N")[..8],
        };
        await proxyService.InstallFilesAsync(proxy);
        await (await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6))).DisposeAsync();
        _registry.SetProxy(proxy);
        proxyService.ConfigureProxy(proxy, _registry.All);
        proxyService.ConfigureBackend(proxy, _main);
        await _runner.StartAsync(MainService);
        await _runner.StartAsync(ProxyServiceName);

        var software = new ServerSoftwareService(_downloader, config, TestUtil.Log<ServerSoftwareService>()) { JavaPath = await Jdk.JavaAsync() };
        var provisioning = new ServerProvisioningService(_registry, software, _downloader, proxyService, _runner, config, TestUtil.Log<ServerProvisioningService>());
        _fabric = await provisioning.CreateAsync(new ServerProvisioningService.CreateServerRequest(
            "spfabric", "SP Fabric", ServerLoader.Fabric, "1.21.5", 2048, ["proxyportal"]));
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        foreach (var name in _runner.Servers.Keys.ToList()) await _runner.StopAsync(name);
        await _runner.StopAsync(ProxyServiceName);
        await _runner.StopAsync(MainService);
    }

    private static async Task<string> RconAsync(ServerDefinition s, string command)
    {
        await using var rcon = new MinecraftRconClient("127.0.0.1", s.RconPort);
        Assert.That(await rcon.ConnectAsync(s.RconPassword), Is.True);
        return await rcon.SendCommandAsync(command);
    }

    private static async Task<bool> IsOn(ServerDefinition s, string player)
    {
        await using var rcon = new MinecraftRconClient("127.0.0.1", s.RconPort);
        return await rcon.ConnectAsync(s.RconPassword) && (await rcon.GetPlayersAsync()).Players.Contains(player);
    }

    private static async Task<(double x, double y, double z)> PositionAsync(ServerDefinition s, string player)
    {
        var r = await RconAsync(s, $"data get entity {player} Pos");
        var n = Regex.Matches(r, @"(-?\d+(?:\.\d+)?)d").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.That(n, Has.Count.EqualTo(3), r);
        return (n[0], n[1], n[2]);
    }

    /// <summary>The middle of the lit inside of a portal (where a player stepping in stands).</summary>
    /// (Computed as numbers: "{-2}.5" as text would be -2.5, the block NEXT to x = -2.)
    private static string Center(PortalDefinition p) => p.Axis == "x"
        ? string.Create(CultureInfo.InvariantCulture, $"{p.X + 2.0} {p.Y + 1} {p.Z + 0.5}")
        : string.Create(CultureInfo.InvariantCulture, $"{p.X + 0.5} {p.Y + 1} {p.Z + 2.0}");

    [Test, Order(1)]
    public async Task BuildsBothPortals_NearSpawn_WithoutReplacingABuiltBlock()
    {
        // "Built blocks": a glass block on the ground in every column 6-9 blocks from spawn. The portal must go
        // further out and leave every one of them.
        var spawn = new WorldDataService(_main, TestUtil.Log<WorldDataService>()).GetSpawn();
        Assert.That(spawn.Known, Is.True);
        await RconAsync(_main, $"forceload add {spawn.X - 12} {spawn.Z - 12} {spawn.X + 12} {spawn.Z + 12}");
        for (var dx = -9; dx <= 9; dx++)
            for (var dz = -9; dz <= 9; dz++)
            {
                var d = Math.Sqrt(dx * dx + dz * dz);
                if (d < 6 || d > 9) continue;
                _glass.Add((spawn.X + dx, spawn.Z + dz));
                await RconAsync(_main, $"execute positioned {spawn.X + dx} 0 {spawn.Z + dz} positioned over motion_blocking_no_leaves run setblock ~ ~ ~ minecraft:glass");
            }
        await RconAsync(_main, $"forceload remove {spawn.X - 12} {spawn.Z - 12} {spawn.X + 12} {spawn.Z + 12}");

        var progress = new Progress<string>(l => TestContext.Progress.WriteLine("  > " + l));
        _toFabric = await _portals.BuildAtSpawnAsync(_main.Id, _fabric.Id, progress);
        _toMain = await _portals.BuildAtSpawnAsync(_fabric.Id, _main.Id, progress);

        // Every column the frame and its walk-in rows stand on is outside the ring (frames run along X or along Z).
        var columns = from along in Enumerable.Range(0, 4)
                      from across in Enumerable.Range(-1, 3)
                      select _toFabric.Axis == "x" ? (x: _toFabric.X + along, z: _toFabric.Z + across) : (x: _toFabric.X + across, z: _toFabric.Z + along);
        var nearest = columns.Min(c => Math.Sqrt(Math.Pow(c.x - spawn.X, 2) + Math.Pow(c.z - spawn.Z, 2)));
        Assert.That(nearest, Is.GreaterThan(9), $"outside the glass ring (portal at {_toFabric.X} {_toFabric.Z} axis {_toFabric.Axis}, spawn {spawn.X} {spawn.Z})");
        await RconAsync(_main, $"forceload add {spawn.X - 12} {spawn.Z - 12} {spawn.X + 12} {spawn.Z + 12}");
        foreach (var (x, z) in _glass)
            Assert.That(await RconAsync(_main, $"execute positioned {x} 0 {z} positioned over motion_blocking_no_leaves if block ~ ~-1 ~ minecraft:glass"),
                Does.Contain("Test passed"), $"glass at {x} {z} untouched");
        await RconAsync(_main, $"forceload remove {spawn.X - 12} {spawn.Z - 12} {spawn.X + 12} {spawn.Z + 12}");

        foreach (var (server, portal) in new[] { (_main, _toFabric), (_fabric, _toMain) })
        {
            await RconAsync(server, $"forceload add {portal.X} {portal.Z}");
            Assert.That(await RconAsync(server, $"execute if block {SpawnPortalService.Inside(portal)} minecraft:nether_portal"), Does.Contain("Test passed"), server.Id + " portal lit");
            await RconAsync(server, $"forceload remove {portal.X} {portal.Z}");
        }
        Assert.That(_registry.Get(_main.Id)!.Portals.Single().Target, Is.EqualTo(_fabric.Id));
        Assert.That(await RconAsync(_main, "datapack list enabled"), Does.Contain(SpawnPortalService.DatapackName));
        Assert.That(await RconAsync(_fabric, "datapack list enabled"), Does.Contain(SpawnPortalService.DatapackName));
        Assert.ThrowsAsync<InvalidOperationException>(() => _portals.BuildAtSpawnAsync(_main.Id, _fabric.Id), "one portal per destination");
    }

    [Test, Order(2)]
    public async Task RoundTrip_ThroughBothPortals_WithoutBouncingBack()
    {
        const string bot = "SpawnBot";
        await using var client = TestBot.Start("127.0.0.1", ProxyPort, bot);
        await client.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        await TestUtil.WaitUntilAsync(() => IsOn(_main, bot), TimeSpan.FromSeconds(15), "bot on the main server");
        await Task.Delay(6000); // Advanced Portals ignores portals for 5 s after a join

        // Main -> Fabric (first visit: arrives at Fabric's spawn).
        await RconAsync(_main, $"tp {bot} {Center(_toFabric)}");
        await TestUtil.WaitUntilAsync(() => IsOn(_fabric, bot), TimeSpan.FromSeconds(30), "Advanced Portals sent the bot to Fabric: " + client.Transcript);
        await Task.Delay(3000);

        // Fabric -> Main: back where it left Main - INSIDE the main portal. It must be moved out, not sent back.
        await RconAsync(_fabric, $"tp {bot} {Center(_toMain)}");
        await TestUtil.WaitUntilAsync(() => IsOn(_main, bot), TimeSpan.FromSeconds(30), "ProxyPortal sent the bot to Main: " + client.Transcript);
        await Task.Delay(9000);
        Assert.That(await IsOn(_main, bot), Is.True, "stayed on Main (no bounce back to Fabric)");
        var p = await PositionAsync(_main, bot);
        Assert.That(Math.Abs(p.x - _toFabric.FrontX) + Math.Abs(p.z - _toFabric.FrontZ), Is.LessThan(1.0), $"in front of the main portal: {p}");
        // A player walks off before going back in (Advanced Portals re-arms on real movement, not on a teleport).
        await client.WalkAsync(TimeSpan.FromMilliseconds(800));

        // Main -> Fabric again: back where it left Fabric - INSIDE the Fabric portal, which ProxyPortal fires at once.
        await RconAsync(_main, $"tp {bot} {Center(_toFabric)}");
        await TestUtil.WaitUntilAsync(() => IsOn(_fabric, bot), TimeSpan.FromSeconds(30), "back to Fabric: " + client.Transcript);
        await Task.Delay(9000);
        Assert.That(await IsOn(_fabric, bot), Is.True, "stayed on Fabric (no bounce back to Main)");
        var q = await PositionAsync(_fabric, bot);
        Assert.That(Math.Abs(q.x - _toMain.FrontX) + Math.Abs(q.z - _toMain.FrontZ), Is.LessThan(1.0), $"in front of the Fabric portal: {q}");
    }
}
