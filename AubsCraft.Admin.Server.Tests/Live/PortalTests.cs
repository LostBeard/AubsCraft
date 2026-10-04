using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// Portal server switching through the real proxy, both directions: Advanced Portals on a Paper server
/// (bungee:&lt;server&gt; tag - Velocity's BungeeCord channel) and ProxyPortal on a Fabric server created by
/// ServerProvisioningService. A real client steps into each portal (RCON tp) and must arrive on the other server.
/// </summary>
[NonParallelizable]
public class PortalTests
{
    private const int ProxyPort = 25780, MainPort = 25781, MainRcon = 25782, ProxyRcon = 25783;
    private const string MainService = "minecraft-portalmain", ProxyServiceName = "velocity-portal";

    private sealed class Runner(string serversRoot, string proxyDir) : IServiceRunner
    {
        public readonly Dictionary<string, EnvFileProcess> Servers = [];
        public PaperServer? Main;
        public VelocityProcess? Proxy;
        public async Task StartAsync(string name, CancellationToken ct = default)
        {
            if (name == MainService) Main = await PaperServer.StartAsync("portalmain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6), fresh: false);
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
    private ServerDefinition _main = null!, _fabric = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var dir = TestUtil.NewTempDir();
        var serversRoot = Path.Combine(PaperServer.CacheRoot, "portal-servers");
        if (Directory.Exists(serversRoot)) Directory.Delete(serversRoot, true);
        var proxyDir = Path.Combine(PaperServer.CacheRoot, "velocity-portal");
        foreach (var f in new[] { "velocity.toml", "forwarding.secret" }) { var p = Path.Combine(proxyDir, f); if (File.Exists(p)) File.Delete(p); }
        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json")), ("Servers:Root", serversRoot));
        _registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        _registry.Remove(ServerRegistry.LegacyServerId);
        var proxyService = new ProxyService(_downloader, TestUtil.Log<ProxyService>());
        _runner = new Runner(serversRoot, proxyDir);

        // Paper main server with Advanced Portals (as it would be installed from the Plugins page).
        var ap = await _downloader.ModrinthAsync("advanced-portals", "paper", PaperServer.GameVersion);
        var main = await PaperServer.StartAsync("portalmain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6),
            beforeStart: d => _downloader.DownloadAsync(ap, Path.Combine(d, "plugins", ap.FileName)));
        await main.DisposeAsync();
        _main = main.ToDefinition("main");
        _main.ServiceName = MainService;
        _registry.Add(_main);

        var proxy = new ProxyDefinition
        {
            Path = proxyDir, ServiceName = ProxyServiceName, BindHost = "127.0.0.1", Port = ProxyPort,
            BedrockPort = 19280, VoicePort = 24780, RconPort = ProxyRcon, OnlineMode = false,
            RconPassword = "portal-" + Guid.NewGuid().ToString("N")[..8],
        };
        await proxyService.InstallFilesAsync(proxy);
        await (await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6))).DisposeAsync();
        _registry.SetProxy(proxy);
        proxyService.ConfigureProxy(proxy, _registry.All);
        proxyService.ConfigureBackend(proxy, _main);
        await _runner.StartAsync(MainService);
        await _runner.StartAsync(ProxyServiceName);

        // A Fabric server with ProxyPortal, created exactly as the Servers page creates one.
        var software = new ServerSoftwareService(_downloader, config, TestUtil.Log<ServerSoftwareService>()) { JavaPath = await Jdk.JavaAsync(), Java21Path = await Jdk.Java21Async() };
        var provisioning = new ServerProvisioningService(_registry, software, _downloader, proxyService, _runner, config, TestUtil.Log<ServerProvisioningService>());
        _fabric = await provisioning.CreateAsync(new ServerProvisioningService.CreateServerRequest(
            "fabricp", "Fabric P", ServerLoader.Fabric, "1.21.5", 2048, ["proxyportal"]));
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

    [Test, Order(1)]
    public async Task AdvancedPortals_OnPaper_SendsThePlayerToAnotherServer()
    {
        // A portal far from spawn (nobody stands in it by accident): a 3x3x3 box around (1000, 200, 1000),
        // triggered by air (no portal blocks needed for the test), going to the Fabric server.
        var portals = Path.Combine(_runner.Main!.Dir, "plugins", "AdvancedPortals", "portals");
        Directory.CreateDirectory(portals);
        File.WriteAllText(Path.Combine(portals, "tofabric.yaml"), $"""
            maxLoc:
              posX: 1001
              posY: 201
              posZ: 1001
              worldName: world
            minLoc:
              posX: 999
              posY: 199
              posZ: 999
              worldName: world
            args:
              name:
              - tofabric
              bungee:
              - {_fabric.Id}
              triggerblock:
              - AIR
            """);
        TestContext.Progress.WriteLine("reload: " + await RconAsync(_main, "portal reload"));

        await using var bot = TestBot.Start("127.0.0.1", ProxyPort, "PortalBot");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        await TestUtil.WaitUntilAsync(() => IsOn(_main, "PortalBot"), TimeSpan.FromSeconds(15), "bot on the main server");
        // Advanced Portals ignores portals for joinCooldown (5 s) after a player joins, so arrivals cannot bounce back.
        await Task.Delay(6000);
        await RconAsync(_main, "tp PortalBot 1000 200 1000");
        await TestUtil.WaitUntilAsync(() => IsOn(_fabric, "PortalBot"), TimeSpan.FromSeconds(30),
            "the Advanced Portals portal sent the bot to the Fabric server: " + bot.Transcript);
    }

    [Test, Order(2)]
    public async Task ProxyPortal_OnFabric_SendsThePlayerBack()
    {
        var create = await RconAsync(_fabric, "portal create tomain 2999 199 2999 3001 201 3001 main");
        TestContext.Progress.WriteLine("create: " + create);

        await Task.Delay(3500); // Velocity login-ratelimit since the previous test's bot
        await using var bot = TestBot.Start("127.0.0.1", ProxyPort, "PortalBot2");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        await TestUtil.WaitUntilAsync(() => IsOn(_main, "PortalBot2"), TimeSpan.FromSeconds(15), "bot on the main server");
        await bot.ChatAsync("/server " + _fabric.Id);
        await TestUtil.WaitUntilAsync(() => IsOn(_fabric, "PortalBot2"), TimeSpan.FromSeconds(30), "bot on the Fabric server");
        await RconAsync(_fabric, "tp PortalBot2 3000 200 3000");
        await TestUtil.WaitUntilAsync(() => IsOn(_main, "PortalBot2"), TimeSpan.FromSeconds(30),
            "the ProxyPortal portal sent the bot back to the main server: " + bot.Transcript);
    }
}
