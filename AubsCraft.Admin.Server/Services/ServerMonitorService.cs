using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Models;
using Microsoft.AspNetCore.SignalR;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Background service that polls RCON every 3 seconds and pushes
/// status updates to all connected SignalR clients.
/// Detects player join/leave by diffing the player list.
/// </summary>
public class ServerMonitorService : BackgroundService
{
    private readonly RconService _rcon;
    private readonly IHubContext<ServerHub, IServerHubClient> _hub;
    private readonly ILogger<ServerMonitorService> _logger;

    private HashSet<string> _previousPlayers = [];
    private readonly List<TpsReadingDto> _tpsHistory = [];
    private const int MaxTpsHistory = 200;

    public IReadOnlyList<TpsReadingDto> TpsHistory => _tpsHistory;
    public ServerStatusDto? LastStatus { get; private set; }

    private readonly ActivityLogService _activityLog;

    public ServerMonitorService(
        RconService rcon,
        ActivityLogService activityLog,
        IHubContext<ServerHub, IServerHubClient> hub,
        ILogger<ServerMonitorService> logger)
    {
        _rcon = rcon;
        _activityLog = activityLog;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ServerMonitorService starting");

        // Subscribe to activity events from log tailing and push via SignalR
        _activityLog.EventAdded += OnActivityEvent;

        // Wait a moment for the app to fully start
        await Task.Delay(2000, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollAndPushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitor poll failed");
            }
        }

        _logger.LogInformation("ServerMonitorService stopped");
    }

    private async Task PollAndPushAsync(CancellationToken ct)
    {
        if (!_rcon.IsConnected)
        {
            var connected = await _rcon.ConnectAsync(ct);
            if (!connected)
            {
                await _hub.Clients.All.ReceiveServerStatus(new ServerStatusDto(
                    false, 0, 0, [], 0, 0, 0));
                return;
            }
        }

        var players = await _rcon.GetPlayersAsync(ct);
        var tps = await _rcon.GetTpsAsync(ct);

        // Push status
        var status = new ServerStatusDto(
            true,
            players.Online,
            players.Max,
            players.Players,
            tps.Tps1Min,
            tps.Tps5Min,
            tps.Tps15Min);

        LastStatus = status;
        await _hub.Clients.All.ReceiveServerStatus(status);

        // Push TPS reading
        var tpsReading = new TpsReadingDto(DateTime.UtcNow, tps.Tps1Min, tps.Tps5Min, tps.Tps15Min);
        lock (_tpsHistory)
        {
            _tpsHistory.Add(tpsReading);
            while (_tpsHistory.Count > MaxTpsHistory)
                _tpsHistory.RemoveAt(0);
        }
        await _hub.Clients.All.ReceiveTpsReading(tpsReading);

        // Detect joins/leaves
        var currentPlayers = new HashSet<string>(players.Players);

        var joined = currentPlayers.Except(_previousPlayers);
        var left = _previousPlayers.Except(currentPlayers);

        foreach (var player in joined)
        {
            var evt = new ActivityEventDto(DateTime.UtcNow, ActivityEventType.PlayerJoin, player, $"{player} joined the game");
            await _hub.Clients.All.ReceiveActivityEvent(evt);
            _logger.LogInformation("Player joined: {Player}", player);
        }

        foreach (var player in left)
        {
            var evt = new ActivityEventDto(DateTime.UtcNow, ActivityEventType.PlayerLeave, player, $"{player} left the game");
            await _hub.Clients.All.ReceiveActivityEvent(evt);
            _logger.LogInformation("Player left: {Player}", player);
        }

        _previousPlayers = currentPlayers;
    }

    private void OnActivityEvent(ActivityEventDto evt)
    {
        // Push activity events from log tailing to all SignalR clients
        _ = _hub.Clients.All.ReceiveActivityEvent(evt);

        // Also push chat messages on the dedicated channel
        if (evt.Type == ActivityEventType.Chat && evt.PlayerName != null)
        {
            _ = _hub.Clients.All.ReceiveChatMessage(
                new ChatMessageDto(evt.Timestamp, evt.PlayerName, evt.Details));
        }
    }
}
