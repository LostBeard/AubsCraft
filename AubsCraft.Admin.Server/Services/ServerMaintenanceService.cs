using AubsCraft.Admin.Server.Models;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Backup, restore, reset world and remove for one server. Backups of a RUNNING server do not stop it: world
/// saving is paused (save-off), everything is flushed (save-all flush), the folder is archived, saving resumes
/// (save-on) - so region files are never copied mid-write. Restore, reset and remove stop the server; reset and
/// remove always take a backup first, so both can be undone from the backup list.
/// </summary>
public class ServerMaintenanceService
{
    private readonly ServerRegistry _registry;
    private readonly BackupService _backups;
    private readonly ProxyService _proxy;
    private readonly IServiceRunner _runner;
    private readonly ILogger<ServerMaintenanceService> _logger;

    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(8);

    public ServerMaintenanceService(ServerRegistry registry, BackupService backups, ProxyService proxy, IServiceRunner runner,
        ILogger<ServerMaintenanceService> logger)
    {
        _registry = registry;
        _backups = backups;
        _proxy = proxy;
        _runner = runner;
        _logger = logger;
    }

    private ServerDefinition Get(string id) => _registry.Get(id) ?? throw new InvalidOperationException($"No server '{id}'.");

    /// <summary>Backs a server up, running or not.</summary>
    public async Task<BackupService.BackupInfo> BackupAsync(string serverId, string? label = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var def = Get(serverId);
        await using var rcon = await TryRconAsync(def, ct);
        if (rcon == null)
        {
            progress?.Report($"{def.Name} is not running: backing up its files");
            return await _backups.CreateAsync(def, label, ct);
        }
        progress?.Report($"Pausing world saving on {def.Name} and flushing it to disk");
        await rcon.SendCommandAsync("save-off", ct);
        try
        {
            await rcon.SendCommandAsync("save-all flush", ct);
            progress?.Report("Writing the backup");
            return await _backups.CreateAsync(def, label, ct);
        }
        finally
        {
            // Always resume saving, even if the backup failed: a server left on save-off loses progress.
            await rcon.SendCommandAsync("save-on", CancellationToken.None);
            progress?.Report("World saving resumed");
        }
    }

    /// <summary>Restores a backup: stop, replace the files, start.</summary>
    public async Task RestoreAsync(string serverId, string backupFile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var def = Get(serverId);
        if (!_backups.List(serverId).Any(b => b.FileName == backupFile))
            throw new InvalidOperationException($"No backup '{backupFile}' for {def.Name}.");
        progress?.Report($"Stopping {def.Name}");
        await StopAsync(def, ct);
        try
        {
            progress?.Report($"Restoring {backupFile}");
            await _backups.RestoreAsync(def, backupFile, ct);
        }
        finally
        {
            progress?.Report($"Starting {def.Name}");
            await StartAsync(def);
        }
        progress?.Report($"{def.Name} is running from the backup");
    }

    /// <summary>
    /// A new world: stop, back up (label "before-reset"), delete the world folders, optionally set a new seed,
    /// start. Everything else (mods, settings, whitelist) stays.
    /// </summary>
    public async Task ResetWorldAsync(string serverId, string? newSeed, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var def = Get(serverId);
        progress?.Report($"Stopping {def.Name}");
        await StopAsync(def, ct);
        try
        {
            progress?.Report("Backing up the old world first");
            await _backups.CreateAsync(def, "before-reset", ct);
            var world = def.WorldPath;
            // Paper keeps the Nether and End as separate folders; Fabric/Forge keep them inside the world folder.
            foreach (var dir in new[] { world, world + "_nether", world + "_the_end" })
            {
                if (!Directory.Exists(dir)) continue;
                Directory.Delete(dir, recursive: true);
                progress?.Report($"Deleted {Path.GetFileName(dir)}");
            }
            var props = Path.Combine(def.Path, "server.properties");
            ConfigFiles.SetProperty(props, "level-seed", newSeed?.Trim() ?? "");
        }
        finally
        {
            progress?.Report($"Starting {def.Name} (it generates the new world)");
            await StartAsync(def);
        }
        progress?.Report($"{def.Name} has a new world");
    }

    /// <summary>
    /// Removes a server: stop, stop starting at boot, back up (label "removed" - kept, so it can be restored
    /// onto a new server), take it out of the proxy, delete its folder, forget it. The primary server (where
    /// players arrive) cannot be removed.
    /// </summary>
    public async Task RemoveAsync(string serverId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var def = Get(serverId);
        if (string.Equals(_registry.Primary?.Id, def.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{def.Name} is where players arrive; it cannot be removed.");
        progress?.Report($"Stopping {def.Name}");
        await StopAsync(def, ct);
        await _runner.DisableAsync(def.ServiceName, ct);
        progress?.Report("Backing it up (kept in the backup list)");
        await _backups.CreateAsync(def, "removed", ct);

        _registry.Remove(def.Id);
        if (_registry.Proxy is { } proxy)
        {
            progress?.Report("Taking it out of the proxy");
            _proxy.ConfigureProxy(proxy, _registry.All);
            await _proxy.ReloadAsync(proxy, ct);
        }
        progress?.Report("Deleting its folder");
        for (int i = 0; ; i++)
        {
            try { Directory.Delete(def.Path, recursive: true); break; }
            catch (IOException) when (i < 5) { await Task.Delay(1000, ct); } // a just-stopped JVM can hold files briefly
        }
        progress?.Report($"{def.Name} removed");
    }

    private async Task StopAsync(ServerDefinition def, CancellationToken ct)
    {
        await _runner.StopAsync(def.ServiceName, ct);
        await WaitUntilAsync(async () => await TryRconAsync(def, ct) is not { } r || await Dispose(r), $"{def.Name} to stop", ct);
    }

    private async Task StartAsync(ServerDefinition def)
    {
        await _runner.StartAsync(def.ServiceName);
        await WaitUntilAsync(async () => await TryRconAsync(def, default) is { } r && await Dispose(r, true), $"{def.Name} to start", default);
    }

    private static async Task<bool> Dispose(MinecraftRconClient r, bool result = false)
    {
        await r.DisposeAsync();
        return result;
    }

    /// <summary>A connected RCON client, or null when the server is not answering.</summary>
    private static async Task<MinecraftRconClient?> TryRconAsync(ServerDefinition def, CancellationToken ct)
    {
        var rcon = new MinecraftRconClient(def.RconHost, def.RconPort);
        try
        {
            if (await rcon.ConnectAsync(def.RconPassword, ct)) return rcon;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        await rcon.DisposeAsync();
        return null;
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
