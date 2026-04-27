using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Hubs;

/// <summary>
/// Central SignalR hub for all client-server communication.
/// Server-to-client pushes happen via IServerHubClient.
/// Client-to-server commands are defined as hub methods below.
///
/// Methods are gated by role:
///   [Authorize(Roles = Roles.OwnerOrAdmin)] - dangerous ops (kick, ban, console, plugins, server control)
///   No method-level attribute - any logged-in user (Owner / Admin / Friend)
/// </summary>
[Authorize]
public class ServerHub : Hub<IServerHubClient>
{
    private readonly RconService _rcon;
    private readonly ServerMonitorService _monitor;
    private readonly ActivityLogService _activityLog;
    private readonly PluginService _plugins;
    private readonly PlayerStatsService _stats;
    private readonly ModrinthService _modrinth;
    private readonly ServerControlService _serverControl;
    private readonly AuthService _auth;
    private readonly InviteCodeService _invites;
    private readonly WhitelistAuditService _whitelistAudit;
    private readonly EmailNotificationService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServerHub> _logger;

    public ServerHub(RconService rcon, ServerMonitorService monitor, ActivityLogService activityLog,
        PluginService plugins, PlayerStatsService stats, ModrinthService modrinth,
        ServerControlService serverControl, AuthService auth, InviteCodeService invites,
        WhitelistAuditService whitelistAudit, EmailNotificationService email,
        IConfiguration configuration, ILogger<ServerHub> logger)
    {
        _rcon = rcon;
        _monitor = monitor;
        _activityLog = activityLog;
        _plugins = plugins;
        _stats = stats;
        _modrinth = modrinth;
        _serverControl = serverControl;
        _auth = auth;
        _invites = invites;
        _whitelistAudit = whitelistAudit;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    private string CurrentUsername => Context.User?.Identity?.Name ?? "(unknown)";
    private bool IsAdminOrOwner =>
        Context.User?.IsInRole(Roles.Owner) == true || Context.User?.IsInRole(Roles.Admin) == true;

    /// <summary>Returns the last known server status immediately.</summary>
    public ServerStatusDto? GetCurrentStatus() => _monitor.LastStatus;

    /// <summary>Returns recent activity events for initial page load.</summary>
    public List<ActivityEventDto> GetRecentActivity(int count = 100, string? typeFilter = null)
    {
        ActivityEventType? filter = null;
        if (typeFilter != null && Enum.TryParse<ActivityEventType>(typeFilter, true, out var parsed))
            filter = parsed;
        return _activityLog.GetRecent(count, filter);
    }

    // -- Whitelist (admin/owner only) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> WhitelistAdd(string playerName)
    {
        _logger.LogInformation("WhitelistAdd: {Player} by {User}", playerName, CurrentUsername);
        var result = await _whitelistAudit.AddOnBehalfAsync(playerName, "Java", CurrentUsername, isFriendCapped: false);
        return result.message;
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> WhitelistRemove(string playerName)
    {
        _logger.LogInformation("WhitelistRemove: {Player} by {User}", playerName, CurrentUsername);
        var result = await _whitelistAudit.RemoveAsync(playerName, "Java");
        return result.message;
    }

    public async Task<List<string>> GetWhitelist()
    {
        return await _rcon.GetWhitelistAsync();
    }

    // -- Self-whitelist (any logged-in user, capped for Friends) --

    public async Task<(bool success, string message)> AddOwnMcAccount(AddOwnMcAccountRequest req)
    {
        var user = _auth.GetUser(CurrentUsername);
        if (user == null)
            return (false, "User record missing - log out and back in.");

        var capped = user.Role == Roles.Friend;
        return await _whitelistAudit.AddOnBehalfAsync(req.McUsername, req.Platform, CurrentUsername, isFriendCapped: capped);
    }

    public List<WhitelistAuditEntryDto> GetMyMcAccounts()
    {
        return _whitelistAudit.ListByWebUser(CurrentUsername).Select(ToDto).ToList();
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public List<WhitelistAuditEntryDto> GetWhitelistAudit()
    {
        return _whitelistAudit.ListAll().Select(ToDto).ToList();
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<(bool success, string message)> RemoveAuditedAccount(string mcUsername, string platform)
    {
        return await _whitelistAudit.RemoveAsync(mcUsername, platform);
    }

    // -- Invite codes (admin/owner only) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<InviteCodeDto> CreateInviteCode(CreateInviteCodeRequest req)
    {
        var entry = await _invites.CreateAsync(req.Code, req.MaxUses, req.ExpiresInDays, req.Notes ?? "", CurrentUsername);
        return ToDto(entry);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public List<InviteCodeDto> ListInviteCodes()
    {
        return _invites.ListAll().Select(ToDto).ToList();
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<bool> RevokeInviteCode(string code)
    {
        return await _invites.RevokeAsync(code);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<bool> DeleteInviteCode(string code)
    {
        return await _invites.DeleteAsync(code);
    }

    // -- User management (owner only) --

    [Authorize(Roles = Roles.Owner)]
    public List<UserSummaryDto> ListUsers()
    {
        return _auth.GetAllUsers().Select(u => new UserSummaryDto(
            u.Username, u.Role, u.CreatedAt, u.CreatedViaInviteCode, u.LastLoginAt)).ToList();
    }

    [Authorize(Roles = Roles.Owner)]
    public async Task<bool> SetUserRole(string username, string role)
    {
        return await _auth.SetUserRoleAsync(username, role);
    }

    [Authorize(Roles = Roles.Owner)]
    public async Task<(bool success, string message)> DeleteUser(string username, bool revokeWhitelist)
    {
        try
        {
            var deleted = await _auth.DeleteUserAsync(username);
            if (!deleted) return (false, "User not found.");
            int revokedCount = 0;
            if (revokeWhitelist)
                revokedCount = await _whitelistAudit.RevokeAllByWebUserAsync(username);
            return (true,
                revokeWhitelist ? $"Deleted {username} and revoked {revokedCount} whitelist entries." : $"Deleted {username}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // -- Kick / Ban (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> KickPlayer(string playerName, string? reason = null)
    {
        _logger.LogInformation("Kick: {Player} Reason: {Reason} by {User}", playerName, reason ?? "(none)", CurrentUsername);
        return await _rcon.KickAsync(playerName, reason);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> BanPlayer(string playerName, string? reason = null)
    {
        _logger.LogInformation("Ban: {Player} Reason: {Reason} by {User}", playerName, reason ?? "(none)", CurrentUsername);
        return await _rcon.BanAsync(playerName, reason);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> PardonPlayer(string playerName)
    {
        _logger.LogInformation("Pardon: {Player} by {User}", playerName, CurrentUsername);
        return await _rcon.PardonAsync(playerName);
    }

    public async Task<List<string>> GetBanList()
    {
        return await _rcon.GetBanListAsync();
    }

    // -- Chat / Broadcast (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> Say(string message)
    {
        return await _rcon.SayAsync(message);
    }

    // -- World Control (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SetTime(string time)
    {
        _logger.LogInformation("SetTime: {Time} by {User}", time, CurrentUsername);
        return MinecraftText.StripColorCodes(await _rcon.SetTimeAsync(time));
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SetWeather(string weather)
    {
        _logger.LogInformation("SetWeather: {Weather} by {User}", weather, CurrentUsername);
        return MinecraftText.StripColorCodes(await _rcon.SetWeatherAsync(weather));
    }

    public async Task<WorldTimeWeatherDto> GetWorldTimeWeather()
    {
        var time = await _rcon.QueryTimeAsync();
        return new WorldTimeWeatherDto(time.Ticks, time.Formatted);
    }

    // -- Player Control (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SetGamemode(string playerName, string mode)
    {
        _logger.LogInformation("Gamemode: {Player} -> {Mode} by {User}", playerName, mode, CurrentUsername);
        return await _rcon.SetGamemodeAsync(playerName, mode);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> TeleportPlayer(string playerName, string destination)
    {
        _logger.LogInformation("Teleport: {Player} -> {Destination} by {User}", playerName, destination, CurrentUsername);
        return await _rcon.TeleportAsync(playerName, destination);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> GiveItem(string playerName, string item, int count = 1)
    {
        _logger.LogInformation("Give: {Player} {Item} x{Count} by {User}", playerName, item, count, CurrentUsername);
        return await _rcon.SendCommandAsync($"give {playerName} minecraft:{item} {count}");
    }

    // -- Server Admin (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SaveWorld()
    {
        return MinecraftText.StripColorCodes(await _rcon.SendCommandAsync("save-all"));
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SendCommand(string command)
    {
        _logger.LogInformation("Command: {Command} by {User}", command, CurrentUsername);
        return MinecraftText.StripColorCodes(await _rcon.SendCommandAsync(command));
    }

    // -- Plugins (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public List<PluginInfo> GetPlugins()
    {
        return _plugins.GetPlugins();
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public (bool success, string message) TogglePlugin(string fileName)
    {
        _logger.LogInformation("TogglePlugin: {FileName} by {User}", fileName, CurrentUsername);
        return _plugins.TogglePlugin(fileName);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<List<ModrinthSearchResult>> SearchPlugins(string query)
    {
        return await _modrinth.SearchAsync(query);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<List<ModrinthVersion>> GetPluginVersions(string projectId)
    {
        return await _modrinth.GetVersionsAsync(projectId);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<(bool success, string message)> InstallPlugin(string downloadUrl, string filename)
    {
        _logger.LogInformation("Installing plugin: {Filename} from {Url} by {User}", filename, downloadUrl, CurrentUsername);
        var result = await _modrinth.DownloadAsync(downloadUrl);
        if (result == null)
            return (false, "Download failed");

        var (data, _) = result.Value;
        var path = Path.Combine(_plugins.PluginsPath, filename);

        await File.WriteAllBytesAsync(path, data);
        _logger.LogInformation("Plugin installed: {Path} ({Size} bytes)", path, data.Length);
        return (true, $"Installed {filename} ({data.Length / 1024}KB). Restart the server to load it.");
    }

    // -- Server Control (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<(bool success, string message)> RestartServer()
    {
        _logger.LogInformation("Server restart requested by {User}", CurrentUsername);
        var (success, output) = await _serverControl.RestartAsync();
        if (success) _ = _email.NotifyServerLifecycleAsync("restarted", CurrentUsername);
        return (success, success ? "Server restarting..." : $"Restart failed: {output}");
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<(bool success, string message)> StopServer()
    {
        _logger.LogInformation("Server stop requested by {User}", CurrentUsername);
        var (success, output) = await _serverControl.StopAsync();
        if (success) _ = _email.NotifyServerLifecycleAsync("stopped", CurrentUsername);
        return (success, success ? "Server stopped" : $"Stop failed: {output}");
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<(bool success, string message)> StartServer()
    {
        _logger.LogInformation("Server start requested by {User}", CurrentUsername);
        var (success, output) = await _serverControl.StartAsync();
        if (success) _ = _email.NotifyServerLifecycleAsync("started", CurrentUsername);
        return (success, success ? "Server starting..." : $"Start failed: {output}");
    }

    // -- Player Positions (any logged-in user - read only) --

    public async Task<List<PlayerPositionDto>> GetPlayerPositions()
    {
        var positions = new List<PlayerPositionDto>();
        var status = _monitor.LastStatus;
        if (status == null || status.Players.Count == 0)
            return positions;

        foreach (var player in status.Players)
        {
            var pos = await _rcon.GetPlayerPositionAsync(player);
            if (pos != null)
                positions.Add(new PlayerPositionDto(pos.Name, pos.X, pos.Y, pos.Z));
        }
        return positions;
    }

    // -- Config --

    public BlueMapConfigDto GetBlueMapConfig()
    {
        return new BlueMapConfigDto(
            _configuration["BlueMap:Url"] ?? "",
            _configuration.GetValue<bool>("BlueMap:Enabled"));
    }

    // -- Player Stats (any logged-in user - read only) --

    public List<PlayerSummary> GetAllPlayers() => _stats.GetAllPlayers();
    public PlayerProfile? GetPlayerProfile(string uuid) => _stats.GetPlayerProfile(uuid);
    public WorldStats GetWorldStats() => _stats.GetWorldStats();

    // -- Helpers --

    private static InviteCodeDto ToDto(InviteCode c) => new(
        c.Code, c.MaxUses, c.UsesRemaining, c.ExpiresAt, c.Notes, c.CreatedBy, c.CreatedAt,
        c.Revoked, c.IsValid(DateTime.UtcNow),
        c.Redemptions.Select(r => new InviteRedemptionDto(r.Username, r.RedeemedAt)).ToList());

    private static WhitelistAuditEntryDto ToDto(WhitelistAuditEntry e) => new(
        e.McUsername, e.Platform, e.AddedByWebUser, e.AddedAt, e.AutoAdded, e.Confirmed);
}
