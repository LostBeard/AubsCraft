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
    private readonly HostCapacityService _capacity;
    private readonly IHubContext<ServerHub, IServerHubClient> _hub;
    private readonly ILogger<ServerOperationsService> _logger;
    private readonly List<string> _log = [];
    private int _running;

    public ServerOperationsService(ServerProvisioningService provisioning, HostCapacityService capacity,
        IHubContext<ServerHub, IServerHubClient> hub, ILogger<ServerOperationsService> logger)
    {
        _provisioning = provisioning;
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

    /// <summary>Modrinth project names typed one per line (or comma separated), without blanks or duplicates.</summary>
    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).SelectMany(i => i.Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Select(i => i.Trim().ToLowerInvariant()).Where(i => i.Length > 0).Distinct().ToList();

    private Task PushAsync(string line, bool done, bool failed)
    {
        lock (_log) _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        return _hub.Clients.All.ReceiveOperationProgress(new OperationProgressDto("create-server", line, done, failed));
    }
}

/// <summary>The Add server form.</summary>
public record CreateServerDto(string Id, string Name, string Loader, string GameVersion, int MemoryMb,
    List<string> ServerMods, List<string> ClientMods, string? Seed);
