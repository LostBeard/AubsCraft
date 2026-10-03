using AubsCraft.Admin.Server.Models;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Owns one ServerInstance per server in the ServerRegistry and keeps them in step with it: a server added
/// to the registry gets an instance, a removed one is disposed, a changed definition is rebuilt.
/// </summary>
public sealed class ServerManager : IAsyncDisposable
{
    private readonly ServerRegistry _registry;
    private readonly ActivityLogService _activityLog;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<ServerManager> _logger;
    private readonly bool _enableSqlite;
    private readonly Lock _lock = new();
    private Dictionary<string, ServerInstance> _instances = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after instances were added, removed or rebuilt.</summary>
    public event Action? ServersChanged;

    public ServerManager(ServerRegistry registry, ActivityLogService activityLog, IConfiguration configuration, ILoggerFactory loggers)
    {
        _registry = registry;
        _activityLog = activityLog;
        _loggers = loggers;
        _logger = loggers.CreateLogger<ServerManager>();
        // SQLite over SSHFS breaks CoreProtect's WAL checkpoints - only enable when running locally
        _enableSqlite = configuration.GetValue("Minecraft:EnableSqliteQueries", !OperatingSystem.IsWindows());
        Sync();
        _registry.Changed += Sync;
    }

    /// <summary>All instances, in registry order (the first is the primary server).</summary>
    public IReadOnlyList<ServerInstance> All
    {
        get
        {
            lock (_lock)
                return _registry.All.Select(d => _instances.GetValueOrDefault(d.Id)).OfType<ServerInstance>().ToList();
        }
    }

    public ServerInstance? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Primary;
        lock (_lock) return _instances.GetValueOrDefault(id);
    }

    public ServerInstance? Primary => _registry.Primary is { } p ? Get(p.Id) : null;

    public bool IsPrimary(ServerInstance instance) =>
        string.Equals(_registry.Primary?.Id, instance.Id, StringComparison.OrdinalIgnoreCase);

    public List<ServerSummaryDto> GetSummaries()
    {
        var primaryId = _registry.Primary?.Id;
        return All.Select(i => i.ToSummary(string.Equals(i.Id, primaryId, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>
    /// Runs an RCON operation on every server that is reachable right now (a stopped server's connect is
    /// refused immediately on localhost). Returns one result per server that was tried.
    /// </summary>
    public async Task<List<ServerCommandResult>> RunOnAllAsync(Func<RconService, Task<string>> op, CancellationToken ct = default)
    {
        var tasks = All.Select(async server =>
        {
            try
            {
                if (!server.Rcon.IsConnected && !await server.Rcon.ConnectAsync(ct))
                    return new ServerCommandResult(server.Id, server.Definition.Name, false, "offline");
                var response = await op(server.Rcon);
                return new ServerCommandResult(server.Id, server.Definition.Name, true, MinecraftText.StripColorCodes(response));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ServerCommandResult(server.Id, server.Definition.Name, false, ex.Message);
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private void Sync()
    {
        var stale = new List<ServerInstance>();
        lock (_lock)
        {
            var next = new Dictionary<string, ServerInstance>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in _registry.All)
            {
                // Reuse an instance only when it was built from this exact definition object; the registry
                // replaces the object on Update, so a changed definition gets a fresh instance.
                if (_instances.TryGetValue(def.Id, out var existing) && ReferenceEquals(existing.Definition, def))
                {
                    next[def.Id] = existing;
                    continue;
                }
                if (existing != null) stale.Add(existing);
                next[def.Id] = new ServerInstance(def, _activityLog, _enableSqlite, _loggers);
                _logger.LogInformation("Server '{Id}' ({Loader} {Version}) at {Path}", def.Id, def.Loader, def.GameVersion, def.Path);
            }
            stale.AddRange(_instances.Where(kv => !next.ContainsKey(kv.Key)).Select(kv => kv.Value));
            _instances = next;
        }
        foreach (var s in stale)
            _ = s.DisposeAsync().AsTask();
        ServersChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _registry.Changed -= Sync;
        List<ServerInstance> all;
        lock (_lock) all = _instances.Values.ToList();
        foreach (var i in all) await i.DisposeAsync();
    }
}

/// <summary>What one server answered to a network-wide command. Ok = false with "offline" when its RCON was unreachable.</summary>
public record ServerCommandResult(string ServerId, string ServerName, bool Ok, string Response);
