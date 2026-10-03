using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Services;

/// <summary>
/// SignalR client managing the real-time connection to the server hub, and which Minecraft server the
/// panel is looking at. Per-server calls go to the SELECTED server (SelectedServerId); per-server pushes
/// (status, TPS) are raised only for it. Activity and chat pushes from every server are raised, tagged
/// with ServerId, so the panel can show the whole network. The selection is remembered in localStorage.
/// </summary>
public class ServerHubClient : IAsyncDisposable
{
    private const string SelectedServerKey = "aubscraft.selectedServer";

    private HubConnection? _hub;
    private Task? _connectTask;
    private readonly NavigationManager _nav;
    private readonly SpawnJSRuntime _js;

    public ServerHubClient(NavigationManager nav, SpawnJSRuntime js)
    {
        _nav = nav;
        _js = js;
    }

    public HubConnectionState State => _hub?.State ?? HubConnectionState.Disconnected;
    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    // -- Server selection --

    /// <summary>Every managed server, in registry order (the first is the primary). Refreshed by the 3-second push.</summary>
    public List<ServerSummaryDto> Servers { get; private set; } = [];

    /// <summary>The server per-server calls target. Null until connected (or when no server is configured).</summary>
    public string? SelectedServerId { get; private set; }

    public ServerSummaryDto? SelectedServer => Servers.FirstOrDefault(s => s.Id == SelectedServerId);

    /// <summary>A server's display name (its id when unknown), for tagging network-wide events.</summary>
    public string ServerName(string? serverId) =>
        Servers.FirstOrDefault(s => s.Id == serverId)?.Name ?? serverId ?? "";

    /// <summary>True when more than one server exists, so events need a server tag.</summary>
    public bool IsMultiServer => Servers.Count > 1;

    public event Action? OnSelectedServerChanged;
    public event Action<List<ServerSummaryDto>>? OnServerListReceived;

    public void SelectServer(string serverId)
    {
        if (serverId == SelectedServerId || Servers.All(s => s.Id != serverId)) return;
        SelectedServerId = serverId;
        try
        {
            using var storage = _js.Get<Storage>("localStorage");
            storage.SetItem(SelectedServerKey, serverId);
        }
        catch (Exception ex)
        {
            // Storage can be unavailable (private window, blocked site data); the selection still works for this session.
            Console.WriteLine($"[ServerHubClient] Could not remember the selected server: {ex.Message}");
        }
        OnSelectedServerChanged?.Invoke();
    }

    private string? ReadRememberedServer()
    {
        try
        {
            using var storage = _js.Get<Storage>("localStorage");
            return storage.GetItem(SelectedServerKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ServerHubClient] Could not read the remembered server: {ex.Message}");
            return null;
        }
    }

    private void ApplyServerList(List<ServerSummaryDto> servers)
    {
        Servers = servers;
        // Keep the selection when it still exists; otherwise fall back to the primary (first) server.
        if (SelectedServerId == null || servers.All(s => s.Id != SelectedServerId))
        {
            var remembered = ReadRememberedServer();
            var next = servers.FirstOrDefault(s => s.Id == remembered)?.Id ?? servers.FirstOrDefault()?.Id;
            if (next != SelectedServerId)
            {
                SelectedServerId = next;
                OnSelectedServerChanged?.Invoke();
            }
        }
        OnServerListReceived?.Invoke(servers);
    }

    private bool IsSelected(string? serverId) => serverId != null && serverId == SelectedServerId;

    // -- Events (server pushes) --

    /// <summary>Status of the SELECTED server.</summary>
    public event Action<ServerStatusDto>? OnServerStatusReceived;
    /// <summary>Status of ANY server (each carries ServerId).</summary>
    public event Action<ServerStatusDto>? OnAnyServerStatusReceived;
    /// <summary>Activity from every server (each carries ServerId).</summary>
    public event Action<ActivityEventDto>? OnActivityEventReceived;
    /// <summary>Chat from every server (each carries ServerId).</summary>
    public event Action<ChatMessageDto>? OnChatMessageReceived;
    /// <summary>TPS readings of the SELECTED server.</summary>
    public event Action<TpsReadingDto>? OnTpsReadingReceived;
    /// <summary>Progress lines from a long operation (the proxy cutover).</summary>
    public event Action<OperationProgressDto>? OnOperationProgress;
    public event Action<HubConnectionState>? OnStateChanged;
    public event Action<string>? OnError;

    // -- Safe invocation wrapper --

    private async Task<T> SafeInvokeAsync<T>(string method, T fallback, params object?[] args)
    {
        try
        {
            // Use InvokeCoreAsync (the explicit object?[] overload). InvokeAsync's
            // single-arg instance overload outranks the array extension in overload
            // resolution, which would wrap our args array as a single argument and
            // break server-side argument binding.
            return await _hub!.InvokeCoreAsync<T>(method, args, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HubException)
        {
            OnError?.Invoke($"Connection error: {ex.Message}");
            return fallback;
        }
    }

    private async Task<T> SafeInvokeAsync<T>(string method, T fallback, CancellationToken ct, params object?[] args)
    {
        try
        {
            return await _hub!.InvokeCoreAsync<T>(method, args, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HubException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException)
                OnError?.Invoke($"Connection error: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// Connects (once) and loads the server list. Every caller awaits the SAME connect: returning early while
    /// another component's connect was still starting let its first calls run unconnected (empty fallbacks).
    /// </summary>
    public Task ConnectAsync() => _connectTask ??= ConnectCoreAsync();

    private async Task ConnectCoreAsync()
    {
        try
        {
            await StartHubAsync();
        }
        catch
        {
            // Let the next ConnectAsync try again instead of replaying this failure forever.
            var failed = _hub;
            _hub = null;
            _connectTask = null;
            if (failed != null) await failed.DisposeAsync();
            throw;
        }
    }

    private async Task StartHubAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(_nav.ToAbsoluteUri("/hubs/server"))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<ServerStatusDto>("ReceiveServerStatus", status =>
        {
            OnAnyServerStatusReceived?.Invoke(status);
            if (IsSelected(status.ServerId)) OnServerStatusReceived?.Invoke(status);
        });

        _hub.On<ActivityEventDto>("ReceiveActivityEvent", evt =>
            OnActivityEventReceived?.Invoke(evt));

        _hub.On<ChatMessageDto>("ReceiveChatMessage", msg =>
            OnChatMessageReceived?.Invoke(msg));

        _hub.On<TpsReadingDto>("ReceiveTpsReading", reading =>
        {
            if (IsSelected(reading.ServerId)) OnTpsReadingReceived?.Invoke(reading);
        });

        _hub.On<List<ServerSummaryDto>>("ReceiveServerList", ApplyServerList);

        _hub.On<OperationProgressDto>("ReceiveOperationProgress", p => OnOperationProgress?.Invoke(p));

        _hub.Reconnecting += _ => { OnStateChanged?.Invoke(HubConnectionState.Reconnecting); return Task.CompletedTask; };
        _hub.Reconnected += _ => { OnStateChanged?.Invoke(HubConnectionState.Connected); return Task.CompletedTask; };
        _hub.Closed += _ => { OnStateChanged?.Invoke(HubConnectionState.Disconnected); return Task.CompletedTask; };

        await _hub.StartAsync();
        OnStateChanged?.Invoke(HubConnectionState.Connected);
        ApplyServerList(await SafeInvokeAsync<List<ServerSummaryDto>>("GetServers", []));
    }

    /// <summary>
    /// Closes the connection (on logout). The connection was authorized by the login cookie and stays
    /// authorized for as long as it is open, so leaving it up after logout would keep hub access alive.
    /// </summary>
    public async Task DisconnectAsync()
    {
        var hub = _hub;
        _hub = null;
        _connectTask = null;
        Servers = [];
        SelectedServerId = null;
        if (hub != null) await hub.DisposeAsync();
        OnStateChanged?.Invoke(HubConnectionState.Disconnected);
    }

    /// <summary>The selected server's id for a per-server call. "" (rejected by the hub as unknown) when none is selected.</summary>
    private string Sid => SelectedServerId ?? "";

    // -- Hub invocations (client calls server) --

    public Task<string> WhitelistAddAsync(string playerName)
        => SafeInvokeAsync("WhitelistAdd", "", playerName);

    public Task<string> WhitelistRemoveAsync(string playerName)
        => SafeInvokeAsync("WhitelistRemove", "", playerName);

    public Task<List<string>> GetWhitelistAsync()
        => SafeInvokeAsync<List<string>>("GetWhitelist", [], Sid);

    public Task<List<ActivityEventDto>> GetRecentActivityAsync(int count = 100, string? typeFilter = null)
        => SafeInvokeAsync<List<ActivityEventDto>>("GetRecentActivity", [], count, typeFilter, null);

    public Task<string> KickAsync(string playerName, string? reason = null)
        => SafeInvokeAsync("KickPlayer", "", Sid, playerName, reason);

    public Task<string> BanAsync(string playerName, string? reason = null)
        => SafeInvokeAsync("BanPlayer", "", playerName, reason);

    public Task<string> PardonAsync(string playerName)
        => SafeInvokeAsync("PardonPlayer", "", playerName);

    public Task<List<string>> GetBanListAsync()
        => SafeInvokeAsync<List<string>>("GetBanList", [], Sid);

    public Task<string> SayAsync(string message)
        => SafeInvokeAsync("Say", "", Sid, message);

    public Task<string> SetTimeAsync(string time)
        => SafeInvokeAsync("SetTime", "", Sid, time);

    public Task<string> SetWeatherAsync(string weather)
        => SafeInvokeAsync("SetWeather", "", Sid, weather);

    /// <summary>The selected server's recent TPS readings (for the dashboard graph on page load).</summary>
    public Task<List<TpsReadingDto>> GetTpsHistoryAsync()
        => SafeInvokeAsync<List<TpsReadingDto>>("GetTpsHistory", [], Sid);

    public Task<ServerStatusDto?> GetCurrentStatusAsync()
        => SafeInvokeAsync<ServerStatusDto?>("GetCurrentStatus", null, Sid);

    public Task<string> SetGamemodeAsync(string playerName, string mode)
        => SafeInvokeAsync("SetGamemode", "", Sid, playerName, mode);

    public Task<string> TeleportPlayerAsync(string playerName, string destination)
        => SafeInvokeAsync("TeleportPlayer", "", Sid, playerName, destination);

    public Task<string> GiveItemAsync(string playerName, string item, int count = 1)
        => SafeInvokeAsync("GiveItem", "", Sid, playerName, item, count);

    public Task<string> SaveWorldAsync()
        => SafeInvokeAsync("SaveWorld", "", Sid);

    public Task<string> SendCommandAsync(string command)
        => SafeInvokeAsync("SendCommand", "", Sid, command);

    public Task<WorldTimeWeatherDto> GetWorldTimeWeatherAsync()
        => SafeInvokeAsync("GetWorldTimeWeather", new WorldTimeWeatherDto(0, "..."), Sid);

    public Task<List<PluginInfoDto>> GetPluginsAsync()
        => SafeInvokeAsync<List<PluginInfoDto>>("GetPlugins", [], Sid);

    public Task<ToggleResultDto> TogglePluginAsync(string fileName)
        => SafeInvokeAsync("TogglePlugin", new ToggleResultDto(false, "Connection lost"), Sid, fileName);

    public Task<List<PlayerSummaryDto>> GetAllPlayersAsync()
        => SafeInvokeAsync<List<PlayerSummaryDto>>("GetAllPlayers", [], Sid);

    public Task<PlayerProfileDto?> GetPlayerProfileAsync(string uuid)
        => SafeInvokeAsync<PlayerProfileDto?>("GetPlayerProfile", null, Sid, uuid);

    public Task<WorldStatsDto> GetWorldStatsAsync()
        => SafeInvokeAsync("GetWorldStats", new WorldStatsDto(), Sid);

    // -- Plugin Browser --

    public Task<List<ModrinthSearchResultDto>> SearchPluginsAsync(string query)
        => SafeInvokeAsync<List<ModrinthSearchResultDto>>("SearchPlugins", [], Sid, query);

    public Task<List<ModrinthVersionDto>> GetPluginVersionsAsync(string projectId)
        => SafeInvokeAsync<List<ModrinthVersionDto>>("GetPluginVersions", [], Sid, projectId);

    public Task<ToggleResultDto> InstallPluginAsync(string downloadUrl, string filename)
        => SafeInvokeAsync("InstallPlugin", new ToggleResultDto(false, "Connection lost"), Sid, downloadUrl, filename);

    // -- Server Control --

    public Task<ToggleResultDto> RestartServerAsync()
        => SafeInvokeAsync("RestartServer", new ToggleResultDto(false, "Connection lost"), Sid);

    public Task<ToggleResultDto> StopServerAsync()
        => SafeInvokeAsync("StopServer", new ToggleResultDto(false, "Connection lost"), Sid);

    /// <summary>What starting the selected server would ask of the machine (warnings, never a block).</summary>
    public Task<HostCapacityDto?> GetStartCapacityAsync()
        => SafeInvokeAsync<HostCapacityDto?>("GetStartCapacity", null, Sid);

    public Task<ToggleResultDto> StartServerAsync()
        => SafeInvokeAsync("StartServer", new ToggleResultDto(false, "Connection lost"), Sid);

    // Map data streaming removed - replaced by binary WebSocket + binary HTTP endpoints
    // Heightmaps: binary WebSocket at /api/world/ws
    // Full chunks: binary HTTP at /api/world/chunk/{x}/{z}

    public Task<List<PlayerPositionDto>> GetPlayerPositionsAsync()
        => SafeInvokeAsync<List<PlayerPositionDto>>("GetPlayerPositions", [], Sid);

    // -- Creating servers (owner) --

    public Task<List<string>> GetGameVersionsAsync(string loader)
        => SafeInvokeAsync<List<string>>("GetGameVersions", [], loader);

    public Task<ToggleResultDto> CreateServerAsync(CreateServerDto request)
        => SafeInvokeAsync("CreateServer", new ToggleResultDto(false, "Connection lost"), request);

    public Task<List<string>> GetCreateLogAsync()
        => SafeInvokeAsync<List<string>>("GetCreateLog", []);

    // -- Backup / restore / new world / remove (owner) - progress arrives as OnOperationProgress ("server-op") --

    public Task<List<BackupDto>> ListBackupsAsync(string serverId)
        => SafeInvokeAsync<List<BackupDto>>("ListBackups", [], serverId);

    public Task<ToggleResultDto> BackupServerAsync(string serverId)
        => SafeInvokeAsync("BackupServer", new ToggleResultDto(false, "Connection lost"), serverId);

    public Task<ToggleResultDto> RestoreBackupAsync(string serverId, string fileName)
        => SafeInvokeAsync("RestoreBackup", new ToggleResultDto(false, "Connection lost"), serverId, fileName);

    public Task<ToggleResultDto> DeleteBackupAsync(string serverId, string fileName)
        => SafeInvokeAsync("DeleteBackup", new ToggleResultDto(false, "Connection lost"), serverId, fileName);

    public Task<ToggleResultDto> ResetWorldAsync(string serverId, string? seed)
        => SafeInvokeAsync("ResetWorld", new ToggleResultDto(false, "Connection lost"), serverId, seed);

    public Task<ToggleResultDto> RemoveServerAsync(string serverId)
        => SafeInvokeAsync("RemoveServer", new ToggleResultDto(false, "Connection lost"), serverId);

    public Task<HostCapacityDto?> GetCreateCapacityAsync(int memoryMb)
        => SafeInvokeAsync<HostCapacityDto?>("GetCreateCapacity", null, memoryMb);

    // -- Velocity proxy (owner) --

    public Task<ProxyStatusDto?> GetProxyStatusAsync()
        => SafeInvokeAsync<ProxyStatusDto?>("GetProxyStatus", null);

    public Task<CutoverPreviewDto?> GetCutoverPreviewAsync(string serverId)
        => SafeInvokeAsync<CutoverPreviewDto?>("GetCutoverPreview", null, serverId);

    public Task<ToggleResultDto> StartProxyCutoverAsync(string serverId, int newGamePort, int voicePort)
        => SafeInvokeAsync("StartProxyCutover", new ToggleResultDto(false, "Connection lost"), serverId, newGamePort, voicePort);

    // -- Config --

    public Task<BlueMapConfigDto> GetBlueMapConfigAsync()
        => SafeInvokeAsync("GetBlueMapConfig", new BlueMapConfigDto("", false));

    public Task<WorldSpawnDto> GetWorldSpawnAsync()
        => SafeInvokeAsync("GetWorldSpawn", new WorldSpawnDto(0, 0, false), Sid);

    // -- Self-whitelist (any logged-in user) --

    public Task<ToggleResultDto> AddOwnMcAccountAsync(string mcUsername, string platform)
        => SafeInvokeAsync("AddOwnMcAccount",
            new ToggleResultDto(false, "Connection lost"),
            new AddOwnMcAccountRequestDto(mcUsername, platform));

    public Task<List<WhitelistAuditEntryDto>> GetMyMcAccountsAsync()
        => SafeInvokeAsync<List<WhitelistAuditEntryDto>>("GetMyMcAccounts", []);

    // -- Whitelist audit (admin/owner) --

    public Task<List<WhitelistAuditEntryDto>> GetWhitelistAuditAsync()
        => SafeInvokeAsync<List<WhitelistAuditEntryDto>>("GetWhitelistAudit", []);

    public Task<ToggleResultDto> RemoveAuditedAccountAsync(string mcUsername, string platform)
        => SafeInvokeAsync("RemoveAuditedAccount", new ToggleResultDto(false, "Connection lost"),
            mcUsername, platform);

    // -- Invite codes (admin/owner) --

    public Task<InviteCodeDto?> CreateInviteCodeAsync(string? code, int maxUses, int? expiresInDays, string notes)
        => SafeInvokeAsync<InviteCodeDto?>("CreateInviteCode", null,
            new CreateInviteCodeRequestDto(code, maxUses, expiresInDays, notes));

    public Task<List<InviteCodeDto>> ListInviteCodesAsync()
        => SafeInvokeAsync<List<InviteCodeDto>>("ListInviteCodes", []);

    public Task<bool> RevokeInviteCodeAsync(string code)
        => SafeInvokeAsync("RevokeInviteCode", false, code);

    public Task<bool> DeleteInviteCodeAsync(string code)
        => SafeInvokeAsync("DeleteInviteCode", false, code);

    // -- User management (owner) --

    public Task<List<UserSummaryDto>> ListUsersAsync()
        => SafeInvokeAsync<List<UserSummaryDto>>("ListUsers", []);

    public Task<bool> SetUserRoleAsync(string username, string role)
        => SafeInvokeAsync("SetUserRole", false, username, role);

    public Task<ToggleResultDto> DeleteUserAsync(string username, bool revokeWhitelist)
        => SafeInvokeAsync("DeleteUser", new ToggleResultDto(false, "Connection lost"),
            username, revokeWhitelist);

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
            await _hub.DisposeAsync();
    }
}

// -- DTOs (matching server-side models) --

public record WorldTimeWeatherDto(
    int TimeTicks,
    string TimeFormatted);

public record BlueMapConfigDto(
    string Url,
    bool Enabled);

public record WorldSpawnDto(
    int X,
    int Z,
    bool Known);

// HeightmapStreamDto and ChunkStreamDto removed - binary WebSocket + binary HTTP replaced SignalR streaming

public record PlayerPositionDto(
    string Name,
    float X,
    float Y,
    float Z);

public record ServerStatusDto(
    bool Connected,
    int Online,
    int Max,
    List<string> Players,
    double Tps1Min,
    double Tps5Min,
    double Tps15Min,
    int TimeTicks = -1,
    string? ServerId = null);

public enum ActivityEventType
{
    PlayerJoin, PlayerLeave, Chat, Death, Advancement,
    WhitelistRejection, AdminAction, ServerMessage,
}

public record ActivityEventDto(
    DateTime Timestamp,
    ActivityEventType Type,
    string? PlayerName,
    string Details,
    string? ServerId = null);

public record ChatMessageDto(
    DateTime Timestamp,
    string PlayerName,
    string Message,
    string? ServerId = null);

public record TpsReadingDto(
    DateTime Timestamp,
    double Tps1Min,
    double Tps5Min,
    double Tps15Min,
    string? ServerId = null);

public class PluginInfoDto
{
    public string FileName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Authors { get; set; } = "";
    public string Website { get; set; } = "";
    public bool Enabled { get; set; }
    public long FileSize { get; set; }
    public string? Error { get; set; }
}

// Matches the server's HubResult record (named Success/Message properties). The hub no longer
// returns ValueTuples, which System.Text.Json/SignalR could not serialize (they blanked out).
public record ToggleResultDto(bool Success, string Message);

public class ModrinthSearchResultDto
{
    // The hub sends these snake_case (the server DTO uses [JsonPropertyName]); without matching
    // attributes here, ProjectId/IconUrl deserialize to empty - which broke plugin install
    // (GetPluginVersions was called with an empty project id -> "No compatible version found").
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public long Downloads { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
}

public class ModrinthVersionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "";
    [JsonPropertyName("date_published")] public DateTime DatePublished { get; set; }
    public List<ModrinthFileDto> Files { get; set; } = [];
}

public class ModrinthFileDto
{
    public string Url { get; set; } = "";
    public string Filename { get; set; } = "";
    public long Size { get; set; }
    public bool Primary { get; set; }
}

public class PlayerSummaryDto
{
    public string UUID { get; set; } = "";
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTime? LastLogin { get; set; }
    public DateTime? LastLogout { get; set; }
    public long PlayTimeTicks { get; set; }
    public string PlayTimeFormatted { get; set; } = "";
    public string Platform { get; set; } = "Java";
    public string? DeviceOS { get; set; }
    public bool IsVR { get; set; }
}

public class PlayerProfileDto
{
    public string UUID { get; set; } = "";
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public bool GodMode { get; set; }
    public DateTime? LastLogin { get; set; }
    public DateTime? LastLogout { get; set; }
    public DateTime? FirstSeen { get; set; }
    public string Platform { get; set; } = "Java";
    public string? DeviceOS { get; set; }
    public bool IsVR { get; set; }
    public string? ClientVersion { get; set; }
    public long PlayTimeTicks { get; set; }
    public string PlayTimeFormatted { get; set; } = "";
    public long Deaths { get; set; }
    public long MobKills { get; set; }
    public long DamageTaken { get; set; }
    public long DamageDealt { get; set; }
    public long Jumps { get; set; }
    public long SleepInBed { get; set; }
    public string TotalDistanceFormatted { get; set; } = "";
    public string WalkDistanceFormatted { get; set; } = "";
    public string SprintDistanceFormatted { get; set; } = "";
    public string FlyDistanceFormatted { get; set; } = "";
    public string SwimDistanceFormatted { get; set; } = "";
    public long BlocksPlaced { get; set; }
    public long BlocksBroken { get; set; }
    public long SessionCount { get; set; }
    public long ChatMessages { get; set; }
    public int AdvancementsCompleted { get; set; }
    public List<string> Advancements { get; set; } = [];
    public long ClaimBlocksAccrued { get; set; }
    public Dictionary<string, long> KilledMobs { get; set; } = [];
    public Dictionary<string, long> KilledByMobs { get; set; } = [];
    public Dictionary<string, long> BlocksMined { get; set; } = [];
}

public class WorldStatsDto
{
    public int TotalPlayers { get; set; }
    public string TotalPlayTimeFormatted { get; set; } = "";
    public long TotalDeaths { get; set; }
    public long TotalMobKills { get; set; }
    public long TotalJumps { get; set; }
    public string TotalDistanceFormatted { get; set; } = "";
    public long TotalBlocksPlaced { get; set; }
    public long TotalBlocksBroken { get; set; }
    public long TotalChatMessages { get; set; }
    public long TotalSessions { get; set; }
    public int PluginCount { get; set; }
}

// -- Auth / invite / whitelist DTOs (mirror server Models/AuthModels.cs) --

public record AddOwnMcAccountRequestDto(string McUsername, string Platform);

public record CreateInviteCodeRequestDto(
    string? Code,
    int MaxUses,
    int? ExpiresInDays,
    string Notes);

public record InviteRedemptionDto(string Username, DateTime RedeemedAt);

public record InviteCodeDto(
    string Code,
    int MaxUses,
    int UsesRemaining,
    DateTime? ExpiresAt,
    string Notes,
    string CreatedBy,
    DateTime CreatedAt,
    bool Revoked,
    bool IsValid,
    List<InviteRedemptionDto> Redemptions);

public record WhitelistAuditEntryDto(
    string McUsername,
    string Platform,
    string AddedByWebUser,
    DateTime AddedAt,
    bool AutoAdded,
    bool Confirmed);

public record UserSummaryDto(
    string Username,
    string Role,
    DateTime CreatedAt,
    string? CreatedViaInviteCode,
    DateTime? LastLoginAt);

public record PublicStatusDto(
    bool Connected,
    int Online,
    int Max,
    List<string> Players,
    List<PublicServerStatusDto>? Servers = null);

public record PublicServerStatusDto(
    string Id,
    string Name,
    bool Connected,
    int Online,
    int Max,
    List<string> Players);

/// <summary>The Add server form (mirrors Server/Services/ServerOperationsService.cs).</summary>
public record CreateServerDto(string Id, string Name, string Loader, string GameVersion, int MemoryMb,
    List<string> ServerMods, List<string> ClientMods, string? Seed);

// -- Proxy DTOs (mirror Server/Services/ProxyOperationsService.cs) --
public record PrerequisiteDto(string What, bool Ok, string Fix);
public record CutoverPreviewDto(string ServerId, string ServerName, int PublicPort, int BedrockPort, int VoicePort,
    int NewGamePort, int NewVoicePort, List<PrerequisiteDto> Prerequisites, List<string> Log);
public record ProxyStatusDto(bool Online, int Port, int BedrockPort, int VoicePort, string Version, string? Players);
public record OperationProgressDto(string Operation, string Message, bool Done, bool Failed);

public record BackupDto(string FileName, long SizeBytes, DateTime CreatedUtc);

/// <summary>Mirrors the server's HostCapacityDto.</summary>
public record HostCapacityDto(long TotalMemoryMb, int Cores, int RunningServers, long NeededMemoryMb, List<string> Warnings);

/// <summary>One managed Minecraft server (mirrors the server's ServerSummaryDto).</summary>
public record ServerSummaryDto(
    string Id,
    string Name,
    string Loader,
    string GameVersion,
    int GamePort,
    int MemoryMb,
    bool IsPrimary,
    bool Connected,
    int Online,
    int Max);
