using System.Text.Json;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Network-wide bans, and keeping every server's whitelist and ban list in step. Players move between
/// servers through the proxy, so a ban or whitelist entry applies to ALL servers: changes go to every
/// reachable server immediately, and SyncServerAsync re-applies the full lists to a server when it comes
/// online (ServerMonitorService calls it), so a server that was stopped during a change catches up.
/// Bans are persisted in bans.json (Moderation:BansPath).
/// </summary>
public class NetworkModerationService
{
    private readonly ServerManager _servers;
    private readonly WhitelistAuditService _whitelist;
    private readonly ILogger<NetworkModerationService> _logger;
    private readonly string _bansPath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public NetworkModerationService(ServerManager servers, WhitelistAuditService whitelist, IConfiguration configuration,
        ILogger<NetworkModerationService> logger)
    {
        _servers = servers;
        _whitelist = whitelist;
        _logger = logger;
        _bansPath = configuration.GetValue<string>("Moderation:BansPath") ?? "bans.json";
    }

    public List<NetworkBan> ListBans() => Load();

    /// <summary>Bans a player on every server (now on reachable ones, at next start on the rest).</summary>
    public async Task<string> BanAsync(string playerName, string? reason, string bannedBy)
    {
        await _ioLock.WaitAsync();
        try
        {
            var bans = Load();
            bans.RemoveAll(b => b.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));
            bans.Add(new NetworkBan { PlayerName = playerName, Reason = reason, BannedBy = bannedBy, BannedAt = DateTime.UtcNow });
            Save(bans);
        }
        finally { _ioLock.Release(); }

        var results = await _servers.RunOnAllAsync(rcon => rcon.BanAsync(playerName, reason));
        return WhitelistAuditService.Summarize(results);
    }

    /// <summary>Lifts a ban on every server.</summary>
    public async Task<string> PardonAsync(string playerName)
    {
        await _ioLock.WaitAsync();
        try
        {
            var bans = Load();
            if (bans.RemoveAll(b => b.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase)) > 0)
                Save(bans);
        }
        finally { _ioLock.Release(); }

        var results = await _servers.RunOnAllAsync(rcon => rcon.PardonAsync(playerName));
        return WhitelistAuditService.Summarize(results);
    }

    /// <summary>
    /// Re-applies the network whitelist and bans to one server. Every command is idempotent ("already
    /// whitelisted" / "nothing changed"), so running it on each (re)connect is safe.
    /// </summary>
    public async Task SyncServerAsync(ServerInstance server)
    {
        var whitelist = _whitelist.ListAll();
        var bans = Load();
        if (whitelist.Count == 0 && bans.Count == 0) return;

        int failed = 0;
        foreach (var entry in whitelist)
        {
            try { await WhitelistAuditService.AddCommand(server.Rcon, entry.McUsername, entry.Platform); }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Whitelist sync of {Player} to '{Server}' failed", entry.McUsername, server.Id);
            }
        }
        foreach (var ban in bans)
        {
            try { await server.Rcon.BanAsync(ban.PlayerName, ban.Reason); }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Ban sync of {Player} to '{Server}' failed", ban.PlayerName, server.Id);
            }
        }
        _logger.LogInformation("Synced {Whitelist} whitelist entries and {Bans} bans to '{Server}' ({Failed} failed)",
            whitelist.Count, bans.Count, server.Id, failed);
    }

    private List<NetworkBan> Load()
    {
        if (!File.Exists(_bansPath)) return [];
        return JsonSerializer.Deserialize<List<NetworkBan>>(File.ReadAllText(_bansPath)) ?? [];
    }

    private void Save(List<NetworkBan> bans)
    {
        var tmp = _bansPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(bans, JsonOpts));
        File.Move(tmp, _bansPath, overwrite: true);
    }
}

public class NetworkBan
{
    public string PlayerName { get; set; } = "";
    public string? Reason { get; set; }
    public string BannedBy { get; set; } = "";
    public DateTime BannedAt { get; set; }
}
