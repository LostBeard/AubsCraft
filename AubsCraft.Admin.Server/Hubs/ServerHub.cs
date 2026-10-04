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
/// Per-server methods take the target server's id as their FIRST argument (see ServerManager); the
/// whitelist and bans are network-wide (WhitelistAuditService / NetworkModerationService).
///
/// Methods are gated by role:
///   [Authorize(Roles = Roles.OwnerOrAdmin)] - dangerous ops (kick, ban, console, plugins, server control)
///   No method-level attribute - any logged-in user (Owner / Admin / Friend)
/// </summary>
[Authorize]
public class ServerHub : Hub<IServerHubClient>
{
    private readonly ServerManager _servers;
    private readonly NetworkModerationService _moderation;
    private readonly HostCapacityService _capacity;
    private readonly ProxyOperationsService _proxyOps;
    private readonly ServerOperationsService _serverOps;
    private readonly AutoBackupService _autoBackup;
    private readonly ServerSettingsService _settings;
    private readonly ServerSoftwareService _software;
    private readonly ActivityLogService _activityLog;
    private readonly ModrinthService _modrinth;
    private readonly AuthService _auth;
    private readonly InviteCodeService _invites;
    private readonly WhitelistAuditService _whitelistAudit;
    private readonly EmailNotificationService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServerHub> _logger;

    public ServerHub(ServerManager servers, NetworkModerationService moderation, HostCapacityService capacity,
        ProxyOperationsService proxyOps, ServerOperationsService serverOps, AutoBackupService autoBackup, ServerSettingsService settings, ServerSoftwareService software, ActivityLogService activityLog,
        ModrinthService modrinth, AuthService auth, InviteCodeService invites,
        WhitelistAuditService whitelistAudit, EmailNotificationService email,
        IConfiguration configuration, ILogger<ServerHub> logger)
    {
        _servers = servers;
        _moderation = moderation;
        _capacity = capacity;
        _proxyOps = proxyOps;
        _serverOps = serverOps;
        _autoBackup = autoBackup;
        _settings = settings;
        _software = software;
        _activityLog = activityLog;
        _modrinth = modrinth;
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

    /// <summary>The server a per-server call targets. A HubException's message reaches the caller.</summary>
    private ServerInstance Server(string serverId) =>
        _servers.Get(serverId) ?? throw new HubException($"Unknown server '{serverId}'.");

    private PluginService Addons(string serverId) =>
        Server(serverId).Plugins ?? throw new HubException("This server has no add-on folder (Vanilla).");

    // -- Servers --

    /// <summary>Every managed server with its live state, in registry order (the first is the primary).</summary>
    public List<ServerSummaryDto> GetServers() => _servers.GetSummaries();

    /// <summary>Returns one server's last known status immediately.</summary>
    public ServerStatusDto? GetCurrentStatus(string serverId) => Server(serverId).LastStatus;

    /// <summary>One server's recent TPS readings, for the dashboard graph on page load.</summary>
    public List<TpsReadingDto> GetTpsHistory(string serverId) => Server(serverId).TpsHistory;

    /// <summary>Returns recent activity events for initial page load. serverId null = every server.</summary>
    public List<ActivityEventDto> GetRecentActivity(int count = 100, string? typeFilter = null, string? serverId = null)
    {
        ActivityEventType? filter = null;
        if (typeFilter != null && Enum.TryParse<ActivityEventType>(typeFilter, true, out var parsed))
            filter = parsed;
        return _activityLog.GetRecent(count, filter, serverId);
    }

    // -- Whitelist (admin/owner only) - network-wide --

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

    /// <summary>One server's actual whitelist (its whitelist.json - also available while it is stopped).</summary>
    public List<string> GetWhitelist(string serverId)
    {
        return Server(serverId).ReadWhitelist();
    }

    // -- Self-whitelist (any logged-in user, capped for Friends) --

    public async Task<HubResult> AddOwnMcAccount(AddOwnMcAccountRequest req)
    {
        var user = _auth.GetUser(CurrentUsername);
        if (user == null)
            return new HubResult(false, "User record missing - log out and back in.");

        var capped = user.Role == Roles.Friend;
        var (success, message) = await _whitelistAudit.AddOnBehalfAsync(req.McUsername, req.Platform, CurrentUsername, isFriendCapped: capped);
        return new HubResult(success, message);
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
    public async Task<HubResult> RemoveAuditedAccount(string mcUsername, string platform)
    {
        var (success, message) = await _whitelistAudit.RemoveAsync(mcUsername, platform);
        return new HubResult(success, message);
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
    public async Task<HubResult> DeleteUser(string username, bool revokeWhitelist)
    {
        try
        {
            var deleted = await _auth.DeleteUserAsync(username);
            if (!deleted) return new HubResult(false, "User not found.");
            int revokedCount = 0;
            if (revokeWhitelist)
                revokedCount = await _whitelistAudit.RevokeAllByWebUserAsync(username);
            return new HubResult(true,
                revokeWhitelist ? $"Deleted {username} and revoked {revokedCount} whitelist entries." : $"Deleted {username}.");
        }
        catch (Exception ex)
        {
            return new HubResult(false, ex.Message);
        }
    }

    // -- Kick (one server) / Ban (network-wide) - admin/owner --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> KickPlayer(string serverId, string playerName, string? reason = null)
    {
        _logger.LogInformation("Kick: {Player} on '{Server}' Reason: {Reason} by {User}", playerName, serverId, reason ?? "(none)", CurrentUsername);
        return await Server(serverId).Rcon.KickAsync(playerName, reason);
    }

    /// <summary>Bans on EVERY server; servers that are down get the ban when they start.</summary>
    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> BanPlayer(string playerName, string? reason = null)
    {
        _logger.LogInformation("Ban: {Player} Reason: {Reason} by {User}", playerName, reason ?? "(none)", CurrentUsername);
        return await _moderation.BanAsync(playerName, reason, CurrentUsername);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> PardonPlayer(string playerName)
    {
        _logger.LogInformation("Pardon: {Player} by {User}", playerName, CurrentUsername);
        return await _moderation.PardonAsync(playerName);
    }

    /// <summary>One server's actual ban list (its banned-players.json - exact, unlike RCON's banlist output).</summary>
    public List<string> GetBanList(string serverId)
    {
        return Server(serverId).ReadBannedPlayers();
    }

    // -- Chat / Broadcast (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> Say(string serverId, string message)
    {
        return await Server(serverId).Rcon.SayAsync(message);
    }

    // -- World Control (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SetTime(string serverId, string time)
    {
        _logger.LogInformation("SetTime: {Time} on '{Server}' by {User}", time, serverId, CurrentUsername);
        return MinecraftText.StripColorCodes(await Server(serverId).Rcon.SetTimeAsync(time));
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SetWeather(string serverId, string weather)
    {
        _logger.LogInformation("SetWeather: {Weather} on '{Server}' by {User}", weather, serverId, CurrentUsername);
        return MinecraftText.StripColorCodes(await Server(serverId).Rcon.SetWeatherAsync(weather));
    }

    public async Task<WorldTimeWeatherDto> GetWorldTimeWeather(string serverId)
    {
        var time = await Server(serverId).Rcon.QueryTimeAsync();
        return new WorldTimeWeatherDto(time.Ticks, time.Formatted);
    }

    // -- Player Control (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SetGamemode(string serverId, string playerName, string mode)
    {
        _logger.LogInformation("Gamemode: {Player} -> {Mode} on '{Server}' by {User}", playerName, mode, serverId, CurrentUsername);
        return await Server(serverId).Rcon.SetGamemodeAsync(playerName, mode);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> TeleportPlayer(string serverId, string playerName, string destination)
    {
        _logger.LogInformation("Teleport: {Player} -> {Destination} on '{Server}' by {User}", playerName, destination, serverId, CurrentUsername);
        return await Server(serverId).Rcon.TeleportAsync(playerName, destination);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> GiveItem(string serverId, string playerName, string item, int count = 1)
    {
        _logger.LogInformation("Give: {Player} {Item} x{Count} on '{Server}' by {User}", playerName, item, count, serverId, CurrentUsername);
        return await Server(serverId).Rcon.SendCommandAsync($"give {playerName} minecraft:{item} {count}");
    }

    // -- Server Admin (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SaveWorld(string serverId)
    {
        return MinecraftText.StripColorCodes(await Server(serverId).Rcon.SendCommandAsync("save-all"));
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<string> SendCommand(string serverId, string command)
    {
        _logger.LogInformation("Command on '{Server}': {Command} by {User}", serverId, command, CurrentUsername);
        return MinecraftText.StripColorCodes(await Server(serverId).Rcon.SendCommandAsync(command));
    }

    // -- Plugins / mods (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public List<PluginInfo> GetPlugins(string serverId)
    {
        return Server(serverId).Plugins?.GetPlugins() ?? [];
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public HubResult TogglePlugin(string serverId, string fileName)
    {
        _logger.LogInformation("TogglePlugin: {FileName} on '{Server}' by {User}", fileName, serverId, CurrentUsername);
        var (success, message) = Addons(serverId).TogglePlugin(fileName);
        return new HubResult(success, message);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<List<ModrinthSearchResult>> SearchPlugins(string serverId, string query)
    {
        return await _modrinth.SearchAsync(Server(serverId).Definition, query);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<List<ModrinthVersion>> GetPluginVersions(string serverId, string projectId)
    {
        return await _modrinth.GetVersionsAsync(Server(serverId).Definition, projectId);
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<HubResult> InstallPlugin(string serverId, string downloadUrl, string filename)
    {
        var addons = Addons(serverId);
        _logger.LogInformation("Installing add-on on '{Server}': {Filename} from {Url} by {User}", serverId, filename, downloadUrl, CurrentUsername);
        var result = await _modrinth.DownloadAsync(downloadUrl);
        if (result == null)
            return new HubResult(false, "Download failed");

        var (data, _) = result.Value;
        // Replaces any existing copy of the same plugin (matched by plugin.yml name) so an update
        // doesn't leave a duplicate older jar.
        var (success, message) = addons.InstallPlugin(data, filename);
        return new HubResult(success, message);
    }

    // -- Server Control (admin/owner) --

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<HubResult> RestartServer(string serverId)
    {
        var server = Server(serverId);
        _logger.LogInformation("Server '{Server}' restart requested by {User}", serverId, CurrentUsername);
        var (success, output) = await server.Control.RestartAsync();
        if (success) _ = _email.NotifyServerLifecycleAsync($"{server.Definition.Name}: restarted", CurrentUsername);
        return new HubResult(success, success ? $"{server.Definition.Name} restarting..." : $"Restart failed: {output}");
    }

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<HubResult> StopServer(string serverId)
    {
        var server = Server(serverId);
        _logger.LogInformation("Server '{Server}' stop requested by {User}", serverId, CurrentUsername);
        var (success, output) = await server.Control.StopAsync();
        if (success) _ = _email.NotifyServerLifecycleAsync($"{server.Definition.Name}: stopped", CurrentUsername);
        return new HubResult(success, success ? $"{server.Definition.Name} stopped" : $"Stop failed: {output}");
    }

    /// <summary>
    /// What starting this server would ask of the machine, with warnings when it probably cannot carry it.
    /// Never a hard limit: the UI shows the warnings and lets the admin start anyway.
    /// </summary>
    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public HostCapacityDto GetStartCapacity(string serverId) => _capacity.Evaluate(Server(serverId));

    [Authorize(Roles = Roles.OwnerOrAdmin)]
    public async Task<HubResult> StartServer(string serverId)
    {
        var server = Server(serverId);
        _logger.LogInformation("Server '{Server}' start requested by {User}", serverId, CurrentUsername);
        var (success, output) = await server.Control.StartAsync();
        if (success) _ = _email.NotifyServerLifecycleAsync($"{server.Definition.Name}: started", CurrentUsername);
        return new HubResult(success, success ? $"{server.Definition.Name} starting..." : $"Start failed: {output}");
    }

    // -- Player Positions (any logged-in user - read only) --

    public async Task<List<PlayerPositionDto>> GetPlayerPositions(string serverId)
    {
        var server = Server(serverId);
        var positions = new List<PlayerPositionDto>();
        var status = server.LastStatus;
        if (status == null || status.Players.Count == 0)
            return positions;

        foreach (var player in status.Players)
        {
            var pos = await server.Rcon.GetPlayerPositionAsync(player);
            if (pos != null)
                positions.Add(new PlayerPositionDto(pos.Name, pos.X, pos.Y, pos.Z));
        }
        return positions;
    }

    // -- Velocity proxy (owner only: it changes how every player connects) --

    [Authorize(Roles = Roles.Owner)]
    public Task<ProxyStatusDto?> GetProxyStatus() => _proxyOps.StatusAsync();

    /// <summary>What the cutover would do for this server, the machine's readiness, and the last run's log.</summary>
    [Authorize(Roles = Roles.Owner)]
    public CutoverPreviewDto GetCutoverPreview(string serverId) => _proxyOps.Preview(serverId);

    /// <summary>Starts the cutover in the background; progress arrives as ReceiveOperationProgress.</summary>
    [Authorize(Roles = Roles.Owner)]
    public HubResult StartProxyCutover(string serverId, int newGamePort, int voicePort)
    {
        Server(serverId);
        var message = _proxyOps.StartCutover(serverId, newGamePort, voicePort, CurrentUsername);
        return new HubResult(message == "Cutover started.", message);
    }

    // -- Creating servers (owner) --

    /// <summary>Minecraft versions a server type can be created with, newest first.</summary>
    [Authorize(Roles = Roles.Owner)]
    public async Task<List<string>> GetGameVersions(string loader) =>
        Enum.TryParse<ServerLoader>(loader, true, out var l) ? await _software.GameVersionsAsync(l) : [];

    /// <summary>Starts creating a server in the background; progress arrives as ReceiveOperationProgress ("create-server").</summary>
    [Authorize(Roles = Roles.Owner)]
    public HubResult CreateServer(CreateServerDto request) => _serverOps.StartCreate(request, CurrentUsername);

    // -- Backup / restore / reset / remove (owner) - progress as ReceiveOperationProgress ("server-op") --

    [Authorize(Roles = Roles.Owner)]
    public List<BackupDto> ListBackups(string serverId) { Server(serverId); return _serverOps.ListBackups(serverId); }

    [Authorize(Roles = Roles.Owner)]
    public HubResult BackupServer(string serverId) { Server(serverId); return _serverOps.StartBackup(serverId, CurrentUsername); }

    [Authorize(Roles = Roles.Owner)]
    public HubResult RestoreBackup(string serverId, string fileName) { Server(serverId); return _serverOps.StartRestore(serverId, fileName, CurrentUsername); }

    [Authorize(Roles = Roles.Owner)]
    public HubResult DeleteBackup(string serverId, string fileName) { Server(serverId); return _serverOps.DeleteBackup(serverId, fileName); }

    [Authorize(Roles = Roles.Owner)]
    public HubResult ResetWorld(string serverId, string? seed) { Server(serverId); return _serverOps.StartReset(serverId, seed, CurrentUsername); }

    [Authorize(Roles = Roles.Owner)]
    public ServerSettingsDto GetServerSettings(string serverId) { Server(serverId); return _settings.Read(serverId); }

    [Authorize(Roles = Roles.Owner)]
    public async Task<HubResult> SaveServerSettings(string serverId, ServerSettingsDto settings)
    {
        Server(serverId);
        try { return new HubResult(true, await _settings.SaveAsync(serverId, settings)); }
        catch (ArgumentException ex) { return new HubResult(false, ex.Message); }
    }

    [Authorize(Roles = Roles.Owner)]
    public AutoBackupDto GetAutoBackup(string serverId) =>
        new(Server(serverId).Definition.AutoBackup, _autoBackup.ScheduleText);

    [Authorize(Roles = Roles.Owner)]
    public HubResult SetAutoBackup(string serverId, bool on) { Server(serverId); return _serverOps.SetAutoBackup(serverId, on); }

    [Authorize(Roles = Roles.Owner)]
    public List<PortalDto> ListPortals(string serverId) { Server(serverId); return _serverOps.ListPortals(serverId); }

    [Authorize(Roles = Roles.Owner)]
    public HubResult BuildSpawnPortal(string serverId, string targetId) { Server(serverId); Server(targetId); return _serverOps.StartSpawnPortal(serverId, targetId, CurrentUsername); }

    [Authorize(Roles = Roles.Owner)]
    public HubResult RemoveServer(string serverId) { Server(serverId); return _serverOps.StartRemove(serverId, CurrentUsername); }

    /// <summary>The log of the last create (for a page opened while one runs).</summary>
    [Authorize(Roles = Roles.Owner)]
    public List<string> GetCreateLog() => _serverOps.Log;

    /// <summary>What starting one more server of this size would ask of the machine (before creating it).</summary>
    [Authorize(Roles = Roles.Owner)]
    public HostCapacityDto GetCreateCapacity(int memoryMb) =>
        HostCapacityService.Evaluate(HostCapacityService.TotalMemoryMb(), Environment.ProcessorCount,
            _servers.All.Where(s => s.LastStatus?.Connected == true).Select(s => s.Definition).ToList(),
            new ServerDefinition { Id = "(new)", MemoryMb = memoryMb });

    // -- Config --

    public BlueMapConfigDto GetBlueMapConfig()
    {
        return new BlueMapConfigDto(
            _configuration["BlueMap:Url"] ?? "",
            _configuration.GetValue<bool>("BlueMap:Enabled"));
    }

    /// <summary>Where a first-time map visitor starts on this server's world (see WorldDataService.GetSpawn).</summary>
    public WorldSpawnDto GetWorldSpawn(string serverId) => Server(serverId).World.GetSpawn();

    // -- Player Stats (any logged-in user - read only) --

    public List<PlayerSummary> GetAllPlayers(string serverId) => Server(serverId).Stats.GetAllPlayers();
    public PlayerProfile? GetPlayerProfile(string serverId, string uuid) => Server(serverId).Stats.GetPlayerProfile(uuid);
    public WorldStats GetWorldStats(string serverId) => Server(serverId).Stats.GetWorldStats();

    // -- Helpers --

    private static InviteCodeDto ToDto(InviteCode c) => new(
        c.Code, c.MaxUses, c.UsesRemaining, c.ExpiresAt, c.Notes, c.CreatedBy, c.CreatedAt,
        c.Revoked, c.IsValid(DateTime.UtcNow),
        c.Redemptions.Select(r => new InviteRedemptionDto(r.Username, r.RedeemedAt)).ToList());

    private static WhitelistAuditEntryDto ToDto(WhitelistAuditEntry e) => new(
        e.McUsername, e.Platform, e.AddedByWebUser, e.AddedAt, e.AutoAdded, e.Confirmed);
}

/// <summary>
/// A success/message result for hub methods. A named record (not a ValueTuple) - System.Text.Json,
/// and therefore SignalR's JsonHubProtocol, does not serialize ValueTuples (their Item1/Item2 are
/// fields, dropped by default), which silently blanked every result. Serializes as {Success,Message}.
/// </summary>
public record HubResult(bool Success, string Message);

public record AutoBackupDto(bool On, string Schedule);
