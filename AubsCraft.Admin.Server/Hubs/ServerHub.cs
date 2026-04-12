using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using Microsoft.AspNetCore.SignalR;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Hubs;

/// <summary>
/// Central SignalR hub for all client-server communication.
/// Server-to-client pushes happen via IServerHubClient.
/// Client-to-server commands are defined as hub methods below.
/// </summary>
public class ServerHub : Hub<IServerHubClient>
{
    private readonly RconService _rcon;
    private readonly ServerMonitorService _monitor;
    private readonly ActivityLogService _activityLog;
    private readonly PluginService _plugins;
    private readonly PlayerStatsService _stats;
    private readonly ModrinthService _modrinth;
    private readonly ServerControlService _serverControl;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServerHub> _logger;

    public ServerHub(RconService rcon, ServerMonitorService monitor, ActivityLogService activityLog,
        PluginService plugins, PlayerStatsService stats, ModrinthService modrinth,
        ServerControlService serverControl, IConfiguration configuration, ILogger<ServerHub> logger)
    {
        _rcon = rcon;
        _monitor = monitor;
        _activityLog = activityLog;
        _plugins = plugins;
        _stats = stats;
        _modrinth = modrinth;
        _serverControl = serverControl;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Returns the last known server status immediately.
    /// </summary>
    public ServerStatusDto? GetCurrentStatus()
    {
        return _monitor.LastStatus;
    }

    /// <summary>
    /// Returns recent activity events for initial page load.
    /// </summary>
    public List<ActivityEventDto> GetRecentActivity(int count = 100, string? typeFilter = null)
    {
        ActivityEventType? filter = null;
        if (typeFilter != null && Enum.TryParse<ActivityEventType>(typeFilter, true, out var parsed))
            filter = parsed;
        return _activityLog.GetRecent(count, filter);
    }

    // -- Whitelist --

    public async Task<string> WhitelistAdd(string playerName)
    {
        _logger.LogInformation("WhitelistAdd: {Player}", playerName);
        return await _rcon.WhitelistAddAsync(playerName);
    }

    public async Task<string> WhitelistRemove(string playerName)
    {
        _logger.LogInformation("WhitelistRemove: {Player}", playerName);
        return await _rcon.WhitelistRemoveAsync(playerName);
    }

    public async Task<List<string>> GetWhitelist()
    {
        return await _rcon.GetWhitelistAsync();
    }

    // -- Kick / Ban --

    public async Task<string> KickPlayer(string playerName, string? reason = null)
    {
        _logger.LogInformation("Kick: {Player} Reason: {Reason}", playerName, reason ?? "(none)");
        return await _rcon.KickAsync(playerName, reason);
    }

    public async Task<string> BanPlayer(string playerName, string? reason = null)
    {
        _logger.LogInformation("Ban: {Player} Reason: {Reason}", playerName, reason ?? "(none)");
        return await _rcon.BanAsync(playerName, reason);
    }

    public async Task<string> PardonPlayer(string playerName)
    {
        _logger.LogInformation("Pardon: {Player}", playerName);
        return await _rcon.PardonAsync(playerName);
    }

    public async Task<List<string>> GetBanList()
    {
        return await _rcon.GetBanListAsync();
    }

    // -- Chat / Broadcast --

    public async Task<string> Say(string message)
    {
        return await _rcon.SayAsync(message);
    }

    // -- World Control --

    public async Task<string> SetTime(string time)
    {
        _logger.LogInformation("SetTime: {Time}", time);
        return MinecraftText.StripColorCodes(await _rcon.SetTimeAsync(time));
    }

    public async Task<string> SetWeather(string weather)
    {
        _logger.LogInformation("SetWeather: {Weather}", weather);
        return MinecraftText.StripColorCodes(await _rcon.SetWeatherAsync(weather));
    }

    public async Task<WorldTimeWeatherDto> GetWorldTimeWeather()
    {
        var time = await _rcon.QueryTimeAsync();
        return new WorldTimeWeatherDto(time.Ticks, time.Formatted);
    }

    // -- Player Control --

    public async Task<string> SetGamemode(string playerName, string mode)
    {
        _logger.LogInformation("Gamemode: {Player} -> {Mode}", playerName, mode);
        return await _rcon.SetGamemodeAsync(playerName, mode);
    }

    public async Task<string> TeleportPlayer(string playerName, string destination)
    {
        _logger.LogInformation("Teleport: {Player} -> {Destination}", playerName, destination);
        return await _rcon.TeleportAsync(playerName, destination);
    }

    public async Task<string> GiveItem(string playerName, string item, int count = 1)
    {
        _logger.LogInformation("Give: {Player} {Item} x{Count}", playerName, item, count);
        return await _rcon.SendCommandAsync($"give {playerName} minecraft:{item} {count}");
    }

    // -- Server Admin --

    public async Task<string> SaveWorld()
    {
        return MinecraftText.StripColorCodes(await _rcon.SendCommandAsync("save-all"));
    }

    public async Task<string> SendCommand(string command)
    {
        _logger.LogInformation("Command: {Command}", command);
        return MinecraftText.StripColorCodes(await _rcon.SendCommandAsync(command));
    }

    // -- Plugins --

    public List<PluginInfo> GetPlugins()
    {
        return _plugins.GetPlugins();
    }

    public (bool success, string message) TogglePlugin(string fileName)
    {
        _logger.LogInformation("TogglePlugin: {FileName}", fileName);
        return _plugins.TogglePlugin(fileName);
    }

    // -- Plugin Browser (Modrinth) --

    public async Task<List<ModrinthSearchResult>> SearchPlugins(string query)
    {
        return await _modrinth.SearchAsync(query);
    }

    public async Task<List<ModrinthVersion>> GetPluginVersions(string projectId)
    {
        return await _modrinth.GetVersionsAsync(projectId);
    }

    public async Task<(bool success, string message)> InstallPlugin(string downloadUrl, string filename)
    {
        _logger.LogInformation("Installing plugin: {Filename} from {Url}", filename, downloadUrl);
        var result = await _modrinth.DownloadAsync(downloadUrl);
        if (result == null)
            return (false, "Download failed");

        var (data, _) = result.Value;
        var path = Path.Combine(_plugins.PluginsPath, filename);

        await File.WriteAllBytesAsync(path, data);
        _logger.LogInformation("Plugin installed: {Path} ({Size} bytes)", path, data.Length);
        return (true, $"Installed {filename} ({data.Length / 1024}KB). Restart the server to load it.");
    }

    // -- Server Control --

    public async Task<(bool success, string message)> RestartServer()
    {
        _logger.LogInformation("Server restart requested");
        var (success, output) = await _serverControl.RestartAsync();
        return (success, success ? "Server restarting..." : $"Restart failed: {output}");
    }

    public async Task<(bool success, string message)> StopServer()
    {
        _logger.LogInformation("Server stop requested");
        var (success, output) = await _serverControl.StopAsync();
        return (success, success ? "Server stopped" : $"Stop failed: {output}");
    }

    public async Task<(bool success, string message)> StartServer()
    {
        _logger.LogInformation("Server start requested");
        var (success, output) = await _serverControl.StartAsync();
        return (success, success ? "Server starting..." : $"Start failed: {output}");
    }

    // World data streaming removed - replaced by binary WebSocket endpoint in Program.cs
    // Heightmaps: binary WebSocket at /api/world/ws
    // Full chunks: binary HTTP at /api/world/chunk/{x}/{z}

    // -- Player Positions (for 3D map) --

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

    // -- Player Stats --

    public List<PlayerSummary> GetAllPlayers()
    {
        return _stats.GetAllPlayers();
    }

    public PlayerProfile? GetPlayerProfile(string uuid)
    {
        return _stats.GetPlayerProfile(uuid);
    }

    public WorldStats GetWorldStats()
    {
        return _stats.GetWorldStats();
    }
}
