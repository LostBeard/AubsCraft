using AubsCraft.Admin.Server.Models;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Moves a running single server behind a new Velocity proxy - the one-time switch from "players connect to
/// the Minecraft server" to "players connect to the proxy, which sends them to a server". The proxy takes over
/// the server's public port, so players keep the same address; the server moves to a localhost-only port.
///
/// Downtime is from step 2 to step 6. Everything that can be done with the server up (downloads) is done first.
/// Any failure after the server stops is rolled back: proxy stopped, the pre-cutover backup restored (config,
/// plugins and world exactly as they were), the registry reverted, the server started again.
/// </summary>
public class ProxyCutoverService
{
    private readonly ServerRegistry _registry;
    private readonly ProxyService _proxy;
    private readonly BackupService _backups;
    private readonly IServiceRunner _runner;
    private readonly ILogger<ProxyCutoverService> _logger;

    public ProxyCutoverService(ServerRegistry registry, ProxyService proxy, BackupService backups, IServiceRunner runner,
        ILogger<ProxyCutoverService> logger)
    {
        _registry = registry;
        _proxy = proxy;
        _backups = backups;
        _runner = runner;
        _logger = logger;
    }

    /// <summary>What to switch: the server, the proxy to put in front of it, and the server's new internal ports.</summary>
    public record CutoverPlan(string ServerId, ProxyDefinition Proxy, int NewGamePort, int VoicePort);

    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public async Task CutoverAsync(CutoverPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Step(string s) { _logger.LogInformation("Cutover: {Step}", s); progress?.Report(s); }

        if (_registry.Proxy != null) throw new InvalidOperationException("A proxy is already set up.");
        var original = _registry.Get(plan.ServerId) ?? throw new InvalidOperationException($"No server '{plan.ServerId}'.");
        if (original.Loader != ServerLoader.Paper) throw new NotSupportedException("The cutover moves a Paper server.");
        if (plan.NewGamePort == plan.Proxy.Port) throw new ArgumentException("The server needs a port other than the proxy's.");
        if (string.IsNullOrEmpty(plan.Proxy.RconPassword)) plan.Proxy.RconPassword = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        Step("1/6 Downloading Velocity and the proxy plugins (the server is still up)");
        await _proxy.InstallFilesAsync(plan.Proxy, ct);

        Step($"2/6 Stopping {original.Name}");
        await _runner.StopAsync(original.ServiceName, ct);

        // From here on the server is DOWN: every failure must bring it back. Until the backup exists nothing
        // has been changed, so recovery is just starting it again (a failed backup once left it stopped).
        BackupService.BackupInfo backup;
        try
        {
            await WaitUntilAsync(async () => !await RconUpAsync(original.RconHost, original.RconPort, original.RconPassword, ct), "the server to stop", ct);
            Step("3/6 Backing up the server");
            backup = await _backups.CreateAsync(original, "before-proxy", ct);
        }
        catch (Exception ex)
        {
            Step($"FAILED ({ex.Message}) - nothing was changed, starting {original.Name} again");
            await StartAndWaitAsync(original);
            Step($"{original.Name} is running as before");
            throw;
        }

        var moved = Clone(original);
        moved.GamePort = plan.NewGamePort;
        moved.VoicePort = plan.VoicePort;
        moved.RconHost = "127.0.0.1";
        var proxyStarted = false;
        try
        {
            Step("4/6 First start of the proxy (it writes its config and keys)");
            var logStart = DateTime.UtcNow;
            await _runner.StartAsync(plan.Proxy.ServiceName, ct);
            proxyStarted = true;
            await WaitUntilAsync(() => Task.FromResult(ProxyFinishedStarting(plan.Proxy, logStart)), "the proxy's first start", ct);
            await _runner.StopAsync(plan.Proxy.ServiceName, ct);
            proxyStarted = false;

            Step("5/6 Configuring the proxy and the server");
            _registry.Update(moved);
            _registry.SetProxy(plan.Proxy);
            _proxy.ConfigureProxy(plan.Proxy, _registry.All);
            _proxy.ConfigureBackend(plan.Proxy, moved);

            Step($"6/6 Starting {moved.Name} and the proxy");
            await _runner.StartAsync(moved.ServiceName, ct);
            await WaitUntilAsync(() => RconUpAsync(moved.RconHost, moved.RconPort, moved.RconPassword, ct), "the server to start", ct);
            await _runner.StartAsync(plan.Proxy.ServiceName, ct);
            proxyStarted = true;
            await WaitUntilAsync(() => RconUpAsync("127.0.0.1", plan.Proxy.RconPort, plan.Proxy.RconPassword, ct), "the proxy to start", ct);
            // Start at boot too: without this a VM restart left the proxy down (2026-10-03) and nobody could connect.
            await _runner.EnableAsync(plan.Proxy.ServiceName, ct);
            Step("Done: players now connect through the proxy");
        }
        catch (Exception ex)
        {
            Step($"FAILED ({ex.Message}) - rolling back to the backup");
            await RollBackAsync(original, plan.Proxy, backup.FileName, proxyStarted);
            Step("Rolled back: the server is running as before");
            throw;
        }
    }

    private async Task RollBackAsync(ServerDefinition original, ProxyDefinition proxy, string backupFile, bool proxyStarted)
    {
        // No cancellation here: a half-done rollback is worse than a slow one.
        if (proxyStarted)
        {
            try { await _runner.StopAsync(proxy.ServiceName); }
            catch (Exception ex) { _logger.LogError(ex, "Rollback: could not stop the proxy"); }
        }
        try { await _runner.StopAsync(original.ServiceName); }
        catch (Exception ex) { _logger.LogWarning(ex, "Rollback: stopping the server failed (it may not be running)"); }
        await _backups.RestoreAsync(original, backupFile);
        _registry.SetProxy(null);
        _registry.Update(original);
        await StartAndWaitAsync(original);
    }

    /// <summary>Starts a server and waits for its RCON. No cancellation: this is the recovery path.</summary>
    private async Task StartAndWaitAsync(ServerDefinition server)
    {
        await _runner.StartAsync(server.ServiceName);
        await WaitUntilAsync(() => RconUpAsync(server.RconHost, server.RconPort, server.RconPassword, default), $"{server.Name} to start", default);
    }

    private static ServerDefinition Clone(ServerDefinition d) =>
        System.Text.Json.JsonSerializer.Deserialize<ServerDefinition>(System.Text.Json.JsonSerializer.Serialize(d))!;

    /// <summary>The first start is done when every generated file exists and the log written since <paramref name="since"/> says Done.</summary>
    private static bool ProxyFinishedStarting(ProxyDefinition proxy, DateTime since)
    {
        if (ProxyService.GeneratedFiles(proxy).Any(f => !File.Exists(f))) return false;
        var log = Path.Combine(proxy.Path, "logs", "latest.log");
        if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < since) return false;
        using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        // The log FILE format differs from the console: "[main/INFO] [com.velocitypowered.proxy.Velocity]: Done (1.8s)!".
        return reader.ReadToEnd().Contains("[com.velocitypowered.proxy.Velocity]: Done (");
    }

    private static async Task<bool> RconUpAsync(string host, int port, string password, CancellationToken ct)
    {
        try
        {
            await using var rcon = new MinecraftRconClient(host, port);
            return await rcon.ConnectAsync(password, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what, CancellationToken ct)
    {
        var end = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < end)
        {
            if (await condition()) return;
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}
