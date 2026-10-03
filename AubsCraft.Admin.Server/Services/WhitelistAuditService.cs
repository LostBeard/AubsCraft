using System.Text.Json;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Tracks who added which Minecraft account to the whitelist via the web admin.
/// Lets admins audit Friend self-adds and revoke them in bulk if a Friend goes rogue.
/// The whitelist is NETWORK-WIDE: adds/removes go to every reachable server, and NetworkModerationService
/// re-applies these entries to a server when it comes online (it may have been stopped during the change).
/// </summary>
public class WhitelistAuditService
{
    private readonly string _path;
    private readonly ServerManager _servers;
    private readonly EmailNotificationService _email;
    private readonly ILogger<WhitelistAuditService> _logger;
    private WhitelistAuditFile? _cached;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public const int MaxAccountsPerFriend = 5;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public WhitelistAuditService(IConfiguration configuration, ServerManager servers, EmailNotificationService email, ILogger<WhitelistAuditService> logger)
    {
        _logger = logger;
        _servers = servers;
        _email = email;
        _path = configuration.GetValue<string>("Auth:WhitelistAuditPath") ?? "whitelist-audit.json";
    }

    public List<WhitelistAuditEntry> ListAll()
    {
        return LoadCached().Entries
            .OrderByDescending(e => e.AddedAt)
            .ToList();
    }

    public List<WhitelistAuditEntry> ListByWebUser(string webUsername)
    {
        return LoadCached().Entries
            .Where(e => e.AddedByWebUser.Equals(webUsername, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.AddedAt)
            .ToList();
    }

    public int CountByWebUser(string webUsername)
    {
        return LoadCached().Entries
            .Count(e => e.AddedByWebUser.Equals(webUsername, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds a Minecraft account to the server whitelist on behalf of a web user.
    /// Routes Java to /whitelist add and Bedrock to /fwhitelist add (Floodgate).
    /// </summary>
    public async Task<(bool success, string message)> AddOnBehalfAsync(
        string mcUsername, string platform, string addedByWebUser, bool isFriendCapped)
    {
        if (string.IsNullOrWhiteSpace(mcUsername))
            return (false, "Username is required.");
        mcUsername = mcUsername.Trim();
        platform = string.Equals(platform, "Bedrock", StringComparison.OrdinalIgnoreCase) ? "Bedrock" : "Java";

        await _ioLock.WaitAsync();
        try
        {
            var file = LoadFromDisk();

            if (file.Entries.Any(e => e.McUsername.Equals(mcUsername, StringComparison.OrdinalIgnoreCase)
                                       && e.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"{mcUsername} is already in the whitelist audit log.");
            }

            if (isFriendCapped)
            {
                var existing = file.Entries.Count(e => e.AddedByWebUser.Equals(addedByWebUser, StringComparison.OrdinalIgnoreCase));
                if (existing >= MaxAccountsPerFriend)
                    return (false, $"You've reached the limit of {MaxAccountsPerFriend} Minecraft accounts. Ask an admin if you need more.");
            }

            var results = await _servers.RunOnAllAsync(rcon => AddCommand(rcon, mcUsername, platform));
            if (results.Count > 0 && results.All(r => r.Ok && IsRejected(r.Response)))
                return (false, string.Join("; ", results.Select(r => r.Response)));
            var rconResponse = Summarize(results);

            var entry = new WhitelistAuditEntry
            {
                McUsername = mcUsername,
                Platform = platform,
                AddedByWebUser = addedByWebUser,
                AddedAt = DateTime.UtcNow,
                AutoAdded = false,
                Confirmed = false,
            };
            file.Entries.Add(entry);
            await SaveAsync(file);
            _cached = file;

            _logger.LogInformation("Whitelist add: {Mc} ({Platform}) by {Web}. RCON: {Resp}",
                mcUsername, platform, addedByWebUser, rconResponse);
            _ = _email.NotifyWhitelistAddAsync(mcUsername, platform, addedByWebUser);
            return (true, rconResponse);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Removes a Minecraft account from the whitelist (server + audit log).
    /// </summary>
    public async Task<(bool success, string message)> RemoveAsync(string mcUsername, string platform)
    {
        if (string.IsNullOrWhiteSpace(mcUsername)) return (false, "Username is required.");
        platform = string.Equals(platform, "Bedrock", StringComparison.OrdinalIgnoreCase) ? "Bedrock" : "Java";

        await _ioLock.WaitAsync();
        try
        {
            var file = LoadFromDisk();
            var results = await _servers.RunOnAllAsync(rcon => RemoveCommand(rcon, mcUsername, platform));
            var rconResponse = Summarize(results);

            file.Entries.RemoveAll(e =>
                e.McUsername.Equals(mcUsername, StringComparison.OrdinalIgnoreCase)
                && e.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase));
            await SaveAsync(file);
            _cached = file;

            _logger.LogInformation("Whitelist remove: {Mc} ({Platform}). RCON: {Resp}",
                mcUsername, platform, rconResponse);
            return (true, rconResponse);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Removes ALL whitelist entries added by a single web user (used when nuking a Friend account).
    /// </summary>
    public async Task<int> RevokeAllByWebUserAsync(string webUsername)
    {
        await _ioLock.WaitAsync();
        try
        {
            var file = LoadFromDisk();
            var toRemove = file.Entries
                .Where(e => e.AddedByWebUser.Equals(webUsername, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var entry in toRemove)
            {
                var results = await _servers.RunOnAllAsync(rcon => RemoveCommand(rcon, entry.McUsername, entry.Platform));
                foreach (var r in results.Where(r => !r.Ok))
                    _logger.LogWarning("Revoke-all for {Web}: removing {Mc} on '{Server}' failed: {Response}", webUsername, entry.McUsername, r.ServerId, r.Response);
            }

            file.Entries.RemoveAll(e => e.AddedByWebUser.Equals(webUsername, StringComparison.OrdinalIgnoreCase));
            await SaveAsync(file);
            _cached = file;
            _logger.LogInformation("Revoked {Count} whitelist entries for web user {Web}", toRemove.Count, webUsername);
            return toRemove.Count;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Java accounts go through /whitelist, Bedrock accounts through Floodgate's /fwhitelist.</summary>
    internal static Task<string> AddCommand(RconService rcon, string mcUsername, string platform) =>
        string.Equals(platform, "Bedrock", StringComparison.OrdinalIgnoreCase)
            ? rcon.SendCommandAsync($"fwhitelist add {mcUsername}")
            : rcon.WhitelistAddAsync(mcUsername);

    internal static Task<string> RemoveCommand(RconService rcon, string mcUsername, string platform) =>
        string.Equals(platform, "Bedrock", StringComparison.OrdinalIgnoreCase)
            ? rcon.SendCommandAsync($"fwhitelist remove {mcUsername}")
            : rcon.WhitelistRemoveAsync(mcUsername);

    /// <summary>Vanilla's answer when the name is not a real account: then nothing is recorded.</summary>
    private static bool IsRejected(string response) =>
        response.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    /// <summary>One line per server: what it answered, or that it will get the change when it next starts.</summary>
    internal static string Summarize(List<ServerCommandResult> results)
    {
        if (results.Count == 0) return "No servers are configured.";
        if (results.Count == 1)
            return results[0].Ok ? results[0].Response : "The server is offline: the change is applied when it starts.";
        return string.Join("; ", results.Select(r =>
            r.Ok ? $"{r.ServerName}: {r.Response}" : $"{r.ServerName}: offline, applied when it starts"));
    }

    private WhitelistAuditFile LoadCached()
    {
        if (_cached != null) return _cached;
        _cached = LoadFromDisk();
        return _cached;
    }

    private WhitelistAuditFile LoadFromDisk()
    {
        if (!File.Exists(_path)) return new WhitelistAuditFile();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<WhitelistAuditFile>(json) ?? new WhitelistAuditFile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read whitelist-audit.json");
            return new WhitelistAuditFile();
        }
    }

    private async Task SaveAsync(WhitelistAuditFile file)
    {
        var json = JsonSerializer.Serialize(file, JsonOpts);
        await File.WriteAllTextAsync(_path, json);
    }
}
