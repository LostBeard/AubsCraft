using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Models;
using Microsoft.AspNetCore.SignalR;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Runs long server operations (create, for now) in the background with their progress pushed live to every
/// connected admin (ReceiveOperationProgress, operation "create-server"). One at a time.
/// </summary>
public class ServerOperationsService
{
    private readonly ServerProvisioningService _provisioning;
    private readonly ServerMaintenanceService _maintenance;
    private readonly BackupService _backups;
    private readonly SpawnPortalService _portals;
    private readonly ServerRegistry _registry;
    private readonly HostCapacityService _capacity;
    private readonly IHubContext<ServerHub, IServerHubClient> _hub;
    private readonly ILogger<ServerOperationsService> _logger;
    private readonly List<string> _log = [];
    private int _running;

    public ServerOperationsService(ServerProvisioningService provisioning, ServerMaintenanceService maintenance, BackupService backups,
        SpawnPortalService portals, ServerRegistry registry, HostCapacityService capacity, IHubContext<ServerHub, IServerHubClient> hub, ILogger<ServerOperationsService> logger)
    {
        _provisioning = provisioning;
        _maintenance = maintenance;
        _backups = backups;
        _portals = portals;
        _registry = registry;
        _capacity = capacity;
        _hub = hub;
        _logger = logger;
    }

    public List<string> Log { get { lock (_log) return _log.ToList(); } }

    /// <summary>Starts creating a server in the background. Returns at once with an error or "Creating ...".</summary>
    public HubResult StartCreate(CreateServerDto dto, string requestedBy)
    {
        if (!Enum.TryParse<ServerLoader>(dto.Loader, true, out var loader))
            return new HubResult(false, $"Unknown server type '{dto.Loader}'.");
        if (!ServerRegistry.IsValidId(dto.Id))
            return new HubResult(false, "The id must be 1-32 lowercase letters, digits or hyphens (it is what players type after /server).");
        if (dto.MemoryMb < 512) return new HubResult(false, "Give it at least 512 MB.");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return new HubResult(false, "Another server is being created.");

        lock (_log) _log.Clear();
        _logger.LogWarning("Creating server '{Id}' ({Loader} {Version}) for {User}", dto.Id, loader, dto.GameVersion, requestedBy);
        var request = new ServerProvisioningService.CreateServerRequest(dto.Id, string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name.Trim(),
            loader, dto.GameVersion, dto.MemoryMb, Clean(dto.ServerMods), string.IsNullOrWhiteSpace(dto.Seed) ? null : dto.Seed, Clean(dto.ClientMods));
        _ = Task.Run(async () =>
        {
            try
            {
                await _provisioning.CreateAsync(request, new Progress<string>(line => _ = PushAsync(line, false, false)));
                await PushAsync("Finished.", true, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Creating '{Id}' failed", dto.Id);
                await PushAsync("Create failed: " + ex.Message, true, true);
            }
            finally { Volatile.Write(ref _running, 0); }
        });
        return new HubResult(true, $"Creating {dto.Id}...");
    }

    // -- Backup / restore / reset / remove (operation "server-op") --

    public List<BackupDto> ListBackups(string serverId) =>
        _backups.List(serverId).Select(b => new BackupDto(b.FileName, b.SizeBytes, b.CreatedUtc)).ToList();

    public HubResult DeleteBackup(string serverId, string fileName)
    {
        var b = _backups.List(serverId).FirstOrDefault(x => x.FileName == fileName);
        if (b == null) return new HubResult(false, "No such backup.");
        File.Delete(b.Path);
        return new HubResult(true, $"Deleted {fileName}.");
    }

    public HubResult StartBackup(string serverId, string user) =>
        Run(serverId, $"Backing up {serverId}", user, p => _maintenance.BackupAsync(serverId, null, p));

    public HubResult StartRestore(string serverId, string file, string user) =>
        Run(serverId, $"Restoring {serverId} from {file}", user, p => _maintenance.RestoreAsync(serverId, file, p));

    public HubResult StartReset(string serverId, string? seed, string user) =>
        Run(serverId, $"New world for {serverId}", user, p => _maintenance.ResetWorldAsync(serverId, seed, p));

    public HubResult StartSpawnPortal(string serverId, string targetId, string user) =>
        Run(serverId, $"Building a portal on {serverId} to {targetId}", user, p => _portals.BuildAtSpawnAsync(serverId, targetId, p));

    public List<PortalDto> ListPortals(string serverId) =>
        (_registry.Get(serverId)?.Portals ?? []).Select(p => new PortalDto(p.Name, p.Target, p.X, p.Y, p.Z)).ToList();

    public HubResult StartRemove(string serverId, string user) =>
        Run(serverId, $"Removing {serverId}", user, p => _maintenance.RemoveAsync(serverId, p));

    /// <summary>Runs one maintenance operation in the background (one at a time, shared with create).</summary>
    private HubResult Run(string serverId, string title, string user, Func<IProgress<string>, Task> op)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return new HubResult(false, "Another server operation is running.");
        lock (_log) _log.Clear();
        _logger.LogWarning("{Title} (by {User})", title, user);
        _ = Task.Run(async () =>
        {
            try
            {
                await PushAsync(title, "server-op", false, false);
                await op(new Progress<string>(line => _ = PushAsync(line, "server-op", false, false)));
                await PushAsync("Finished.", "server-op", true, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Title} failed", title);
                await PushAsync("Failed: " + ex.Message, "server-op", true, true);
            }
            finally { Volatile.Write(ref _running, 0); }
        });
        return new HubResult(true, title + "...");
    }

    /// <summary>Modrinth project names typed one per line (or comma separated), without blanks or duplicates.</summary>
    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).SelectMany(i => i.Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Select(i => i.Trim().ToLowerInvariant()).Where(i => i.Length > 0).Distinct().ToList();

    private Task PushAsync(string line, bool done, bool failed) => PushAsync(line, "create-server", done, failed);

    private Task PushAsync(string line, string operation, bool done, bool failed)
    {
        lock (_log) _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        return _hub.Clients.All.ReceiveOperationProgress(new OperationProgressDto(operation, line, done, failed));
    }
}

public record BackupDto(string FileName, long SizeBytes, DateTime CreatedUtc);

public record PortalDto(string Name, string Target, int X, int Y, int Z);

/// <summary>The Add server form.</summary>
public record CreateServerDto(string Id, string Name, string Loader, string GameVersion, int MemoryMb,
    List<string> ServerMods, List<string> ClientMods, string? Seed);
