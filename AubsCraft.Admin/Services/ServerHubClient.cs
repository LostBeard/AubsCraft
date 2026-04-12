using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace AubsCraft.Admin.Services;

/// <summary>
/// SignalR client managing the real-time connection to the server hub.
/// Replaces the HTTP-based RconApiClient with push-based updates.
/// </summary>
public class ServerHubClient : IAsyncDisposable
{
    private HubConnection? _hub;
    private readonly NavigationManager _nav;

    public ServerHubClient(NavigationManager nav)
    {
        _nav = nav;
    }

    public HubConnectionState State => _hub?.State ?? HubConnectionState.Disconnected;
    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    // -- Events (server pushes) --

    public event Action<ServerStatusDto>? OnServerStatusReceived;
    public event Action<ActivityEventDto>? OnActivityEventReceived;
    public event Action<ChatMessageDto>? OnChatMessageReceived;
    public event Action<TpsReadingDto>? OnTpsReadingReceived;
    public event Action<HubConnectionState>? OnStateChanged;
    public event Action<string>? OnError;

    // -- Safe invocation wrapper --

    private async Task<T> SafeInvokeAsync<T>(string method, T fallback, params object?[] args)
    {
        try
        {
            // Use the non-params overload with CancellationToken to prevent
            // double-wrapping of the args array. The params overload wraps our
            // object?[] in another array, breaking server-side deserialization.
            return await _hub!.InvokeAsync<T>(method, args, CancellationToken.None);
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
            return await _hub!.InvokeAsync<T>(method, args, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HubException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException)
                OnError?.Invoke($"Connection error: {ex.Message}");
            return fallback;
        }
    }

    public async Task ConnectAsync()
    {
        if (_hub != null) return;

        _hub = new HubConnectionBuilder()
            .WithUrl(_nav.ToAbsoluteUri("/hubs/server"))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<ServerStatusDto>("ReceiveServerStatus", status =>
            OnServerStatusReceived?.Invoke(status));

        _hub.On<ActivityEventDto>("ReceiveActivityEvent", evt =>
            OnActivityEventReceived?.Invoke(evt));

        _hub.On<ChatMessageDto>("ReceiveChatMessage", msg =>
            OnChatMessageReceived?.Invoke(msg));

        _hub.On<TpsReadingDto>("ReceiveTpsReading", reading =>
            OnTpsReadingReceived?.Invoke(reading));

        _hub.Reconnecting += _ => { OnStateChanged?.Invoke(HubConnectionState.Reconnecting); return Task.CompletedTask; };
        _hub.Reconnected += _ => { OnStateChanged?.Invoke(HubConnectionState.Connected); return Task.CompletedTask; };
        _hub.Closed += _ => { OnStateChanged?.Invoke(HubConnectionState.Disconnected); return Task.CompletedTask; };

        await _hub.StartAsync();
        OnStateChanged?.Invoke(HubConnectionState.Connected);
    }

    // -- Hub invocations (client calls server) --

    public Task<string> WhitelistAddAsync(string playerName)
        => SafeInvokeAsync("WhitelistAdd", "", playerName);

    public Task<string> WhitelistRemoveAsync(string playerName)
        => SafeInvokeAsync("WhitelistRemove", "", playerName);

    public Task<List<string>> GetWhitelistAsync()
        => SafeInvokeAsync<List<string>>("GetWhitelist", []);

    public Task<List<ActivityEventDto>> GetRecentActivityAsync(int count = 100, string? typeFilter = null)
        => SafeInvokeAsync<List<ActivityEventDto>>("GetRecentActivity", [], count, typeFilter);

    public Task<string> KickAsync(string playerName, string? reason = null)
        => SafeInvokeAsync("KickPlayer", "", playerName, reason);

    public Task<string> BanAsync(string playerName, string? reason = null)
        => SafeInvokeAsync("BanPlayer", "", playerName, reason);

    public Task<string> PardonAsync(string playerName)
        => SafeInvokeAsync("PardonPlayer", "", playerName);

    public Task<List<string>> GetBanListAsync()
        => SafeInvokeAsync<List<string>>("GetBanList", []);

    public Task<string> SayAsync(string message)
        => SafeInvokeAsync("Say", "", message);

    public Task<string> SetTimeAsync(string time)
        => SafeInvokeAsync("SetTime", "", time);

    public Task<string> SetWeatherAsync(string weather)
        => SafeInvokeAsync("SetWeather", "", weather);

    public Task<ServerStatusDto?> GetCurrentStatusAsync()
        => SafeInvokeAsync<ServerStatusDto?>("GetCurrentStatus", null);

    public Task<string> SetGamemodeAsync(string playerName, string mode)
        => SafeInvokeAsync("SetGamemode", "", playerName, mode);

    public Task<string> TeleportPlayerAsync(string playerName, string destination)
        => SafeInvokeAsync("TeleportPlayer", "", playerName, destination);

    public Task<string> GiveItemAsync(string playerName, string item, int count = 1)
        => SafeInvokeAsync("GiveItem", "", playerName, item, count);

    public Task<string> SaveWorldAsync()
        => SafeInvokeAsync("SaveWorld", "");

    public Task<string> SendCommandAsync(string command)
        => SafeInvokeAsync("SendCommand", "", command);

    public Task<WorldTimeWeatherDto> GetWorldTimeWeatherAsync()
        => SafeInvokeAsync("GetWorldTimeWeather", new WorldTimeWeatherDto(0, "..."));

    public Task<List<PluginInfoDto>> GetPluginsAsync()
        => SafeInvokeAsync<List<PluginInfoDto>>("GetPlugins", []);

    public Task<ToggleResultDto> TogglePluginAsync(string fileName)
        => SafeInvokeAsync("TogglePlugin", new ToggleResultDto(false, "Connection lost"), fileName);

    public Task<List<PlayerSummaryDto>> GetAllPlayersAsync()
        => SafeInvokeAsync<List<PlayerSummaryDto>>("GetAllPlayers", []);

    public Task<PlayerProfileDto?> GetPlayerProfileAsync(string uuid)
        => SafeInvokeAsync<PlayerProfileDto?>("GetPlayerProfile", null, uuid);

    public Task<WorldStatsDto> GetWorldStatsAsync()
        => SafeInvokeAsync("GetWorldStats", new WorldStatsDto());

    // -- Plugin Browser --

    public Task<List<ModrinthSearchResultDto>> SearchPluginsAsync(string query)
        => SafeInvokeAsync<List<ModrinthSearchResultDto>>("SearchPlugins", [], query);

    public Task<List<ModrinthVersionDto>> GetPluginVersionsAsync(string projectId)
        => SafeInvokeAsync<List<ModrinthVersionDto>>("GetPluginVersions", [], projectId);

    public Task<ToggleResultDto> InstallPluginAsync(string downloadUrl, string filename)
        => SafeInvokeAsync("InstallPlugin", new ToggleResultDto(false, "Connection lost"), downloadUrl, filename);

    // -- Server Control --

    public Task<ToggleResultDto> RestartServerAsync()
        => SafeInvokeAsync("RestartServer", new ToggleResultDto(false, "Connection lost"));

    public Task<ToggleResultDto> StopServerAsync()
        => SafeInvokeAsync("StopServer", new ToggleResultDto(false, "Connection lost"));

    public Task<ToggleResultDto> StartServerAsync()
        => SafeInvokeAsync("StartServer", new ToggleResultDto(false, "Connection lost"));

    // Map data streaming removed - replaced by binary WebSocket + binary HTTP endpoints
    // Heightmaps: binary WebSocket at /api/world/ws
    // Full chunks: binary HTTP at /api/world/chunk/{x}/{z}

    public Task<List<PlayerPositionDto>> GetPlayerPositionsAsync()
        => SafeInvokeAsync<List<PlayerPositionDto>>("GetPlayerPositions", []);

    // -- Config --

    public Task<BlueMapConfigDto> GetBlueMapConfigAsync()
        => SafeInvokeAsync("GetBlueMapConfig", new BlueMapConfigDto("", false));

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
    double Tps15Min);

public enum ActivityEventType
{
    PlayerJoin, PlayerLeave, Chat, Death, Advancement,
    WhitelistRejection, AdminAction, ServerMessage,
}

public record ActivityEventDto(
    DateTime Timestamp,
    ActivityEventType Type,
    string? PlayerName,
    string Details);

public record ChatMessageDto(
    DateTime Timestamp,
    string PlayerName,
    string Message);

public record TpsReadingDto(
    DateTime Timestamp,
    double Tps1Min,
    double Tps5Min,
    double Tps15Min);

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

public record ToggleResultDto(bool Success, string Message);

public class ModrinthSearchResultDto
{
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public long Downloads { get; set; }
    public string? IconUrl { get; set; }
}

public class ModrinthVersionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string VersionType { get; set; } = "";
    public DateTime DatePublished { get; set; }
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
