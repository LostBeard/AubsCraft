using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server;

/// <summary>
/// Singleton service managing the RCON connection to the Minecraft server.
/// Handles connection lifecycle, reconnection, and provides thread-safe command execution.
/// </summary>
public class RconService : IAsyncDisposable
{
    private MinecraftRconClient? _client;
    private readonly ILogger<RconService> _logger;
    private readonly RconSettings _settings;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public bool IsConnected => _client?.IsConnected == true;

    public RconService(ILogger<RconService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _settings = configuration.GetSection("Rcon").Get<RconSettings>() ?? new RconSettings();
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_client?.IsConnected == true) return true;

            _client?.Dispose();
            _client = new MinecraftRconClient(_settings.Host, _settings.Port);
            _client.Disconnected += (_, _) => _logger.LogWarning("RCON disconnected from {Host}:{Port}", _settings.Host, _settings.Port);

            var result = await _client.ConnectAsync(_settings.Password, cancellationToken);
            if (result)
                _logger.LogInformation("RCON connected to {Host}:{Port}", _settings.Host, _settings.Port);
            else
                _logger.LogError("RCON auth failed for {Host}:{Port}", _settings.Host, _settings.Port);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RCON connection failed to {Host}:{Port}", _settings.Host, _settings.Port);
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client?.IsConnected != true)
            await ConnectAsync(cancellationToken);

        if (_client?.IsConnected != true)
            throw new InvalidOperationException("Not connected to RCON server");
    }

    public async Task<PlayerListResult> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.GetPlayersAsync(cancellationToken);
    }

    public async Task<List<string>> GetWhitelistAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.WhitelistListAsync(cancellationToken);
    }

    public async Task<string> WhitelistAddAsync(string playerName, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.WhitelistAddAsync(playerName, cancellationToken);
    }

    public async Task<string> WhitelistRemoveAsync(string playerName, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.WhitelistRemoveAsync(playerName, cancellationToken);
    }

    public async Task<string> KickAsync(string playerName, string? reason = null, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.KickAsync(playerName, reason, cancellationToken);
    }

    public async Task<string> BanAsync(string playerName, string? reason = null, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.BanAsync(playerName, reason, cancellationToken);
    }

    public async Task<string> PardonAsync(string playerName, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.PardonAsync(playerName, cancellationToken);
    }

    public async Task<List<string>> GetBanListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.BanListAsync(cancellationToken);
    }

    public async Task<string> SayAsync(string message, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.SayAsync(message, cancellationToken);
    }

    public async Task<string> SetTimeAsync(string time, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.SetTimeAsync(time, cancellationToken);
    }

    public async Task<string> SetWeatherAsync(string weather, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.SetWeatherAsync(weather, cancellationToken: cancellationToken);
    }

    public async Task<TpsResult> GetTpsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.GetTpsAsync(cancellationToken);
    }

    public async Task<TimeQueryResult> QueryTimeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.QueryTimeAsync(cancellationToken);
    }

    public async Task<string> SetGamemodeAsync(string playerName, string mode, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.SetGamemodeAsync(playerName, mode, cancellationToken);
    }

    public async Task<string> TeleportAsync(string playerName, string destination, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.TeleportAsync(playerName, destination, cancellationToken);
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await _client!.SendCommandAsync(command, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
            await _client.DisposeAsync();
        _connectLock.Dispose();
    }
}

public class RconSettings
{
    public string Host { get; set; } = "192.168.1.142";
    public int Port { get; set; } = 25575;
    public string Password { get; set; } = "";
}
