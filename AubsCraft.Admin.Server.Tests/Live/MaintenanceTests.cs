using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// Backup, restore, new world and remove (ServerMaintenanceService) on REAL Paper servers behind a REAL Velocity
/// proxy: a marker block proves what a backup holds and what a restore brings back; a real client proves a
/// restored pre-proxy backup still joins through the proxy.
/// </summary>
[NonParallelizable]
public class MaintenanceTests
{
    private const int ProxyPort = 25800, MainPort = 25801, MainRcon = 25802, ExtraPort = 25803, ExtraRcon = 25804, ProxyRcon = 25805;
    private const string MainService = "minecraft-maintmain", ExtraService = "minecraft-maintextra", ProxyServiceName = "velocity-maint";

    /// <summary>"systemctl" for the test: starts and stops the real processes, tracks enable/disable.</summary>
    private sealed class Runner(string proxyDir) : IServiceRunner
    {
        public readonly Dictionary<string, PaperServer> Papers = [];
        public VelocityProcess? Proxy;
        public readonly HashSet<string> Disabled = [];
        public async Task StartAsync(string name, CancellationToken ct = default)
        {
            if (name == ProxyServiceName) Proxy = await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6));
            else if (name == MainService) Papers[name] = await PaperServer.StartAsync("maintmain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6), fresh: false);
            else if (name == ExtraService) Papers[name] = await PaperServer.StartAsync("maintextra", ExtraPort, ExtraRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6), fresh: false);
            else throw new InvalidOperationException("Unknown service " + name);
        }
        public async Task StopAsync(string name, CancellationToken ct = default)
        {
            if (name == ProxyServiceName) { if (Proxy != null) await Proxy.DisposeAsync(); Proxy = null; }
            else if (Papers.Remove(name, out var p)) await p.DisposeAsync();
        }
        public Task EnableAsync(string name, CancellationToken ct = default) { Disabled.Remove(name); return Task.CompletedTask; }
        public Task DisableAsync(string name, CancellationToken ct = default) { Disabled.Add(name); return Task.CompletedTask; }
    }

    private readonly AddonDownloader _downloader = new();
    private ServerRegistry _registry = null!;
    private Runner _runner = null!;
    private BackupService _backups = null!;
    private ProxyService _proxy = null!;
    private ServerMaintenanceService _maintenance = null!;
    private ServerDefinition _main = null!, _extra = null!;
    private ProxyDefinition _proxyDef = null!;
    private readonly List<string> _progress = [];
    private IProgress<string> Progress => new SyncProgress(_progress);

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); TestContext.Progress.WriteLine("  > " + value); }
    }

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var dir = TestUtil.NewTempDir();
        var proxyDir = Path.Combine(PaperServer.CacheRoot, "velocity-maint");
        foreach (var f in new[] { "velocity.toml", "forwarding.secret" }) { var p = Path.Combine(proxyDir, f); if (File.Exists(p)) File.Delete(p); }
        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json")), ("Backups:Path", Path.Combine(dir, "backups")));
        _registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        _registry.Remove(ServerRegistry.LegacyServerId);
        _proxy = new ProxyService(_downloader, TestUtil.Log<ProxyService>());
        _backups = new BackupService(config, TestUtil.Log<BackupService>());
        _runner = new Runner(proxyDir);
        _maintenance = new ServerMaintenanceService(_registry, _backups, _proxy, _runner, TestUtil.Log<ServerMaintenanceService>());

        // Two fresh Paper servers (first start writes their config), then both behind the proxy - as the cutover leaves them.
        var main = await PaperServer.StartAsync("maintmain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6));
        await main.DisposeAsync();
        var extra = await PaperServer.StartAsync("maintextra", ExtraPort, ExtraRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6));
        await extra.DisposeAsync();
        _main = main.ToDefinition("maintmain"); _main.ServiceName = MainService;
        _extra = extra.ToDefinition("maintextra"); _extra.ServiceName = ExtraService;
        _registry.Add(_main);
        _registry.Add(_extra);

        _proxyDef = new ProxyDefinition
        {
            Path = proxyDir, ServiceName = ProxyServiceName, BindHost = "127.0.0.1", Port = ProxyPort,
            BedrockPort = 19300, VoicePort = 24800, RconPort = ProxyRcon, OnlineMode = false,
            RconPassword = "maint-" + Guid.NewGuid().ToString("N")[..8],
        };
        await _proxy.InstallFilesAsync(_proxyDef);
        await (await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6))).DisposeAsync();
        _registry.SetProxy(_proxyDef);
        _proxy.ConfigureProxy(_proxyDef, _registry.All);
        _proxy.ConfigureBackend(_proxyDef, _main);
        _proxy.ConfigureBackend(_proxyDef, _extra);
        await _runner.StartAsync(MainService);
        await _runner.StartAsync(ExtraService);
        await _runner.StartAsync(ProxyServiceName);
        _maintenance.StartTimeout = TimeSpan.FromMinutes(6);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        foreach (var name in _runner.Papers.Keys.ToList()) await _runner.StopAsync(name);
        await _runner.StopAsync(ProxyServiceName);
    }

    private static async Task<string> RconAsync(ServerDefinition s, string command)
    {
        await using var rcon = new MinecraftRconClient("127.0.0.1", s.RconPort);
        Assert.That(await rcon.ConnectAsync(s.RconPassword), Is.True, $"RCON to {s.Id}");
        return await rcon.SendCommandAsync(command);
    }

    /// <summary>Puts a block at the marker spot (0, 100, 0), keeping its chunk loaded.</summary>
    private static async Task SetMarkerAsync(ServerDefinition s, string block)
    {
        await RconAsync(s, "forceload add 0 0");
        var r = await RconAsync(s, $"setblock 0 100 0 minecraft:{block}");
        Assert.That(r, Does.Contain("Changed the block").Or.Contain("Could not set"), "setblock: " + r);
    }

    private static async Task<bool> MarkerIsAsync(ServerDefinition s, string block)
    {
        await RconAsync(s, "forceload add 0 0");
        return (await RconAsync(s, $"execute if block 0 100 0 minecraft:{block}")).Contains("Test passed");
    }

    [Test, Order(1)]
    public async Task Backup_OfARunningServer_KeepsItRunning_AndRestoreBringsTheWorldBack()
    {
        await SetMarkerAsync(_main, "gold_block");
        var backup = await _maintenance.BackupAsync(_main.Id, null, Progress);

        // Still running, and saving was turned back on (a server left on save-off silently stops saving).
        Assert.That(await RconAsync(_main, "save-on"), Does.Contain("already"), "world saving must be back on after a backup");
        Assert.That(_backups.List(_main.Id).Select(b => b.FileName), Does.Contain(backup.FileName));

        await SetMarkerAsync(_main, "diamond_block");
        Assert.That(await MarkerIsAsync(_main, "diamond_block"), Is.True);

        await _maintenance.RestoreAsync(_main.Id, backup.FileName, Progress);
        Assert.That(await MarkerIsAsync(_main, "gold_block"), Is.True, "the restored world has the block from backup time");
    }

    [Test, Order(2)]
    public async Task Restore_OfAPreProxyBackup_StillJoinsThroughTheProxy()
    {
        // The real prod case: the cutover's "before-proxy" backup has the public port and online-mode on.
        await _runner.StopAsync(MainService);
        var props = Path.Combine(_main.Path, "server.properties");
        ConfigFiles.SetProperty(props, "online-mode", "true");
        ConfigFiles.SetProperty(props, "server-port", "25899");
        ConfigFiles.SetProperty(props, "rcon.password", "the-old-password");
        var old = await _maintenance.BackupAsync(_main.Id, "before-proxy", Progress);
        _proxy.ConfigureBackend(_proxyDef, _main);
        ConfigFiles.SetProperty(props, "rcon.password", _main.RconPassword);
        await _runner.StartAsync(MainService);

        await _maintenance.RestoreAsync(_main.Id, old.FileName, Progress);

        Assert.That(ConfigFiles.GetProperty(props, "server-port"), Is.EqualTo(_main.GamePort.ToString()));
        Assert.That(ConfigFiles.GetProperty(props, "online-mode"), Is.EqualTo("false"));
        await Task.Delay(3500); // Velocity login-ratelimit
        await using var bot = TestBot.Start("127.0.0.1", ProxyPort, "RestoreBot");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        await TestUtil.WaitUntilAsync(async () => (await RconAsync(_main, "list")).Contains("RestoreBot"), TimeSpan.FromSeconds(15),
            "the bot reached the restored server through the proxy: " + bot.Transcript);
    }

    [Test, Order(3)]
    public async Task NewWorld_ReplacesTheWorld_WithTheSeed_AndBacksUpTheOldOne()
    {
        await SetMarkerAsync(_extra, "gold_block");
        await RconAsync(_extra, "save-all flush");

        await _maintenance.ResetWorldAsync(_extra.Id, "424242", Progress);

        Assert.That(await RconAsync(_extra, "seed"), Does.Contain("424242"));
        Assert.That(await MarkerIsAsync(_extra, "gold_block"), Is.False, "the old world's marker is gone");
        var before = _backups.List(_extra.Id).SingleOrDefault(b => b.FileName.Contains("before-reset"));
        Assert.That(before, Is.Not.Null, "the old world was backed up first");

        // ...and that backup really holds the old world.
        await _maintenance.RestoreAsync(_extra.Id, before!.FileName, Progress);
        Assert.That(await MarkerIsAsync(_extra, "gold_block"), Is.True);
    }

    [Test, Order(4)]
    public void Remove_RefusesThePrimaryServer()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => _maintenance.RemoveAsync(_main.Id, Progress));
        Assert.That(ex!.Message, Does.Contain("cannot be removed"));
        Assert.That(_registry.Get(_main.Id), Is.Not.Null);
        Assert.That(Directory.Exists(_main.Path), Is.True);
    }

    [Test, Order(5)]
    public async Task Remove_TakesTheServerOutOfEverything_AndKeepsABackup()
    {
        Assert.That(File.ReadAllText(Path.Combine(_proxyDef.Path, "velocity.toml")), Does.Contain(_extra.Id));

        await _maintenance.RemoveAsync(_extra.Id, Progress);

        Assert.That(_registry.Get(_extra.Id), Is.Null, "registry");
        Assert.That(Directory.Exists(_extra.Path), Is.False, "folder");
        Assert.That(_runner.Papers.ContainsKey(ExtraService), Is.False, "stopped");
        Assert.That(_runner.Disabled, Does.Contain(ExtraService), "no longer starts at boot");
        Assert.That(File.ReadAllText(Path.Combine(_proxyDef.Path, "velocity.toml")), Does.Not.Contain(_extra.Id), "proxy config");
        Assert.That(_backups.List(_extra.Id).Any(b => b.FileName.Contains("removed")), Is.True, "final backup kept");
        // The proxy really reloaded: it no longer offers the server.
        await using var rcon = new MinecraftRconClient("127.0.0.1", ProxyRcon);
        Assert.That(await rcon.ConnectAsync(_proxyDef.RconPassword), Is.True);
        Assert.That(await rcon.SendCommandAsync("glist all"), Does.Not.Contain(_extra.Id));
    }
}
