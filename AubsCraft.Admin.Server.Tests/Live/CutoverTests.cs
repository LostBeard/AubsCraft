using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// A rehearsal of the production cutover with the real ProxyCutoverService: a Paper server set up like the
/// VM's today (public port, its own Geyser-Spigot on UDP 19132, Floodgate, ViaVersion family, Simple Voice
/// Chat), with a marker block in its world, is moved behind a new Velocity proxy that takes over its port.
/// A real client must then join on the SAME address and find the same world; and a cutover that fails
/// part-way must roll back to the server exactly as it was.
/// </summary>
[NonParallelizable]
public class CutoverTests
{
    private const int PublicPort = 25690, Rcon = 25695, InternalPort = 25691, InternalVoice = 24491;
    private const int ProxyRcon = 25696;
    private const string ServerId = "cutover", ProxyService_ = "velocity-test", ServerService = "minecraft-test";

    /// <summary>Runs the "services" as local processes: the cutover code drives them exactly as it drives systemd.</summary>
    private sealed class LocalRunner(string proxyDir) : IServiceRunner
    {
        public PaperServer? Server;
        public VelocityProcess? Proxy;
        public int ProxyStarts;
        public int FailProxyStartNumber = -1; // to force a failure

        public async Task StartAsync(string serviceName, CancellationToken ct = default)
        {
            if (serviceName == ServerService)
                Server = await PaperServer.StartAsync(ServerId, 0, 0, 8, "minecraft:flat", "world", TimeSpan.FromMinutes(6), fresh: false);
            else
            {
                ProxyStarts++;
                if (ProxyStarts == FailProxyStartNumber) throw new InvalidOperationException("forced proxy start failure (test)");
                Proxy = await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6));
            }
        }

        public async Task StopAsync(string serviceName, CancellationToken ct = default)
        {
            if (serviceName == ServerService) { if (Server != null) await Server.DisposeAsync(); Server = null; }
            else { if (Proxy != null) await Proxy.DisposeAsync(); Proxy = null; }
        }

        public readonly List<string> Enabled = [];
        public Task EnableAsync(string serviceName, CancellationToken ct = default) { Enabled.Add(serviceName); return Task.CompletedTask; }
        public Task DisableAsync(string serviceName, CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly AddonDownloader _downloader = new();
    private string _dir = "";
    private ServerRegistry _registry = null!;
    private BackupService _backups = null!;
    private LocalRunner _runner = null!;

    private ProxyDefinition NewProxy(string proxyDir) => new()
    {
        Path = proxyDir,
        ServiceName = ProxyService_,
        BindHost = "127.0.0.1",
        Port = PublicPort,         // the proxy takes over the server's public port
        BedrockPort = 19132,       // and its Bedrock port: the server's own Geyser must be gone, or this collides
        VoicePort = 24490,
        RconPort = ProxyRcon,
        OnlineMode = false,
    };

    /// <summary>A server like the VM's: public port, its own Geyser/Floodgate/Via/voice, and a marker block.</summary>
    private async Task<ServerDefinition> StartProductionLikeServerAsync()
    {
        var plugins = new List<AddonArtifact>
        {
            await _downloader.GeyserMcAsync("geyser", "spigot"),
            await _downloader.GeyserMcAsync("floodgate", "spigot"),
            await _downloader.ModrinthAsync("viaversion", "paper", PaperServer.GameVersion),
            await _downloader.ModrinthAsync("viabackwards", "paper", PaperServer.GameVersion),
            await _downloader.ModrinthAsync("simple-voice-chat", "paper", PaperServer.GameVersion),
        };
        var server = await PaperServer.StartAsync(ServerId, PublicPort, Rcon, 8, "minecraft:flat", "world", TimeSpan.FromMinutes(6),
            beforeStart: async dir =>
            {
                foreach (var a in plugins) await _downloader.DownloadAsync(a, Path.Combine(dir, "plugins", a.FileName));
            });
        await using (var rcon = await TestServers.RconAsync(server))
        {
            Assert.That(await rcon.SendCommandAsync("setblock 3 -50 3 minecraft:gold_block"), Does.Contain("Changed").IgnoreCase);
            await rcon.SendCommandAsync("save-all flush");
        }
        var def = server.ToDefinition(ServerId);
        def.ServiceName = ServerService;
        _runner.Server = server;
        return def;
    }

    private void Build(string proxyDir, string? backupsPath = null)
    {
        _dir = TestUtil.NewTempDir();
        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(_dir, "servers.json")),
            ("Backups:Path", backupsPath ?? Path.Combine(_dir, "backups")));
        _registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        _registry.Remove(ServerRegistry.LegacyServerId);
        _backups = new BackupService(config, TestUtil.Log<BackupService>());
        _runner = new LocalRunner(proxyDir);
    }

    private ProxyCutoverService Cutover() =>
        new(_registry, new ProxyService(_downloader, TestUtil.Log<ProxyService>()), _backups, _runner, TestUtil.Log<ProxyCutoverService>());

    private static string FreshProxyDir(string name)
    {
        var dir = Path.Combine(PaperServer.CacheRoot, name);
        // Keep downloaded jars (verified, re-used); drop config so this is a true first start.
        if (Directory.Exists(dir))
            foreach (var e in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Path.GetFileName(e) == "plugins")
                {
                    foreach (var p in Directory.EnumerateFileSystemEntries(e))
                        if (Directory.Exists(p)) Directory.Delete(p, true); // plugin data folders
                    continue;
                }
                if (Path.GetFileName(e) == "velocity.jar") continue;
                if (Directory.Exists(e)) Directory.Delete(e, true); else File.Delete(e);
            }
        return dir;
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (_runner?.Proxy != null) await _runner.Proxy.DisposeAsync();
        if (_runner?.Server != null) await _runner.Server.DisposeAsync();
    }

    private static async Task<string> RconAsync(int port, string password, string command)
    {
        await using var rcon = new MinecraftRconClient("127.0.0.1", port);
        Assert.That(await rcon.ConnectAsync(password), Is.True);
        return await rcon.SendCommandAsync(command);
    }

    [Test]
    public async Task Cutover_KeepsTheAddress_AndTheWorld()
    {
        var proxyDir = FreshProxyDir("velocity-cutover");
        Build(proxyDir);
        var def = await StartProductionLikeServerAsync();
        _registry.Add(def);
        var proxy = NewProxy(proxyDir);

        var steps = new List<string>();
        await Cutover().CutoverAsync(new ProxyCutoverService.CutoverPlan(ServerId, proxy, InternalPort, InternalVoice),
            new Progress<string>(steps.Add));

        // Registry: proxy recorded, server moved to its internal port.
        Assert.That(_registry.Proxy?.Port, Is.EqualTo(PublicPort));
        Assert.That(_registry.Get(ServerId)!.GamePort, Is.EqualTo(InternalPort));
        Assert.That(_backups.List(ServerId).Single().FileName, Does.Contain("before-proxy"));

        // A player connects to the SAME address as before and lands on the same world.
        await using (var bot = TestBot.Start("127.0.0.1", PublicPort, "CutoverBot"))
        {
            await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
            var list = await RconAsync(Rcon, def.RconPassword, "list");
            Assert.That(list, Does.Contain("CutoverBot"), list);
        }
        var marker = await RconAsync(Rcon, def.RconPassword, "execute if block 3 -50 3 minecraft:gold_block");
        Assert.That(marker, Does.Contain("passed").IgnoreCase, "the world's marker block survived: " + marker);

        // The proxy holds Bedrock now: its Geyser bound 19132, so the server's own Geyser was really disabled.
        await _runner.Proxy!.WaitForLineAsync("Started Geyser on UDP port 19132", TimeSpan.FromSeconds(60));
        Assert.That(Directory.GetFiles(Path.Combine(_runner.Server!.Dir, "plugins"), "Geyser-Spigot.jar.disabled"), Has.Length.EqualTo(1));
        Assert.That(_runner.Proxy.Output.Where(l => l.Contains(" ERROR]")), Is.Empty);

        // The server is no longer reachable directly.
        await using (var sneaky = TestBot.Start("127.0.0.1", InternalPort, "Sneaky"))
        {
            var e = await sneaky.WaitForAsync(x => TestBot.Kind(x) is "spawn" or "kicked" or "end", TimeSpan.FromSeconds(30));
            Assert.That(e != null && TestBot.Kind(e.Value) != "spawn", "joined without the proxy: " + sneaky.Transcript);
        }
        Assert.That(steps.Last(), Does.StartWith("Done"), string.Join(" | ", steps));
        Assert.That(_runner.Enabled, Does.Contain(ProxyService_), "the proxy starts at boot (a VM restart once left it down)");
    }

    [Test]
    public async Task ABackupFailure_LeavesTheServerRunningAsItWas()
    {
        // What happened on the VM: the backup step failed (permissions) after the server was stopped, and the
        // server stayed down. Here the backup folder is a FILE, so creating the backup fails with a real IO error.
        var blocker = Path.Combine(TestUtil.NewTempDir(), "not-a-folder");
        File.WriteAllText(blocker, "");
        var proxyDir = FreshProxyDir("velocity-cutover-backupfail");
        Build(proxyDir, backupsPath: blocker);
        var def = await StartProductionLikeServerAsync();
        _registry.Add(def);

        var steps = new List<string>();
        Assert.CatchAsync(() => Cutover().CutoverAsync(
            new ProxyCutoverService.CutoverPlan(ServerId, NewProxy(proxyDir), InternalPort, InternalVoice), new Progress<string>(steps.Add)));

        Assert.That(_runner.Server, Is.Not.Null, "the server was started again");
        Assert.That(_runner.ProxyStarts, Is.Zero, "the proxy was never started");
        Assert.That(_registry.Proxy, Is.Null);
        Assert.That(_registry.Get(ServerId)!.GamePort, Is.EqualTo(PublicPort));
        await using (var bot = TestBot.Start("127.0.0.1", PublicPort, "BackupFailBot"))
            await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        Assert.That(steps, Has.Some.Contains("is running as before"), string.Join(" | ", steps));
    }

    [Test]
    public async Task AFailedCutover_RollsBackToTheServerAsItWas()
    {
        var proxyDir = FreshProxyDir("velocity-cutover-fail");
        Build(proxyDir);
        var def = await StartProductionLikeServerAsync();
        _registry.Add(def);
        _runner.FailProxyStartNumber = 2; // the first start (config generation) works, the real start fails

        var steps = new List<string>();
        Assert.ThrowsAsync<InvalidOperationException>(() => Cutover().CutoverAsync(
            new ProxyCutoverService.CutoverPlan(ServerId, NewProxy(proxyDir), InternalPort, InternalVoice), new Progress<string>(steps.Add)));

        // Registry, config and plugins as before; players connect straight to the server again.
        Assert.That(_registry.Proxy, Is.Null);
        Assert.That(_registry.Get(ServerId)!.GamePort, Is.EqualTo(PublicPort));
        var props = Path.Combine(_runner.Server!.Dir, "server.properties");
        Assert.That(ConfigFiles.GetProperty(props, "server-port"), Is.EqualTo(PublicPort.ToString()));
        Assert.That(File.Exists(Path.Combine(_runner.Server.Dir, "plugins", "Geyser-Spigot.jar")), Is.True, "Geyser re-enabled");
        await using (var bot = TestBot.Start("127.0.0.1", PublicPort, "RollbackBot"))
            await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        var marker = await RconAsync(Rcon, def.RconPassword, "execute if block 3 -50 3 minecraft:gold_block");
        Assert.That(marker, Does.Contain("passed").IgnoreCase, marker);
        Assert.That(steps, Has.Some.StartsWith("Rolled back"), string.Join(" | ", steps));
    }
}
