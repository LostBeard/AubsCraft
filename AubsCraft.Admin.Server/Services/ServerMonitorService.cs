using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Models;
using Microsoft.AspNetCore.SignalR;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Background service that polls every managed server over RCON every 3 seconds and pushes each one's
/// status (tagged with its server id) plus the server list to all connected SignalR clients.
/// Detects player join/leave by diffing each server's player list, and when a server comes online it
/// re-applies the network-wide whitelist and bans to it (it may have missed changes while it was down).
/// </summary>
public class ServerMonitorService : BackgroundService
{
    private readonly ServerManager _servers;
    private readonly IHubContext<ServerHub, IServerHubClient> _hub;
    private readonly ILogger<ServerMonitorService> _logger;
    private readonly ActivityLogService _activityLog;
    private readonly NetworkModerationService _moderation;

    public ServerMonitorService(
        ServerManager servers,
        ActivityLogService activityLog,
        NetworkModerationService moderation,
        IHubContext<ServerHub, IServerHubClient> hub,
        ILogger<ServerMonitorService> logger)
    {
        _servers = servers;
        _activityLog = activityLog;
        _moderation = moderation;
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
                // Servers are polled concurrently: a stopped server's RCON connect timeout must not delay the others.
                await Task.WhenAll(_servers.All.Select(s => PollServerAsync(s, stoppingToken)));
                await _hub.Clients.All.ReceiveServerList(_servers.GetSummaries());
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

    private async Task PollServerAsync(ServerInstance server, CancellationToken ct)
    {
        try
        {
            await PollAndPushAsync(server, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitor poll failed for server '{Server}'", server.Id);
        }
    }

    private async Task PollAndPushAsync(ServerInstance server, CancellationToken ct)
    {
        var rcon = server.Rcon;
        var wasConnected = server.LastStatus?.Connected == true;
        if (!rcon.IsConnected)
        {
            var connected = await rcon.ConnectAsync(ct);
            if (!connected)
            {
                var offline = new ServerStatusDto(false, 0, 0, [], 0, 0, 0, -1, server.Id);
                server.LastStatus = offline;
                await _hub.Clients.All.ReceiveServerStatus(offline);
                return;
            }
        }

        var players = await rcon.GetPlayersAsync(ct);
        var tps = await rcon.GetTpsAsync(ct);

        // Query time (don't let failure break the status push)
        int timeTicks = -1;
        try { timeTicks = (await rcon.QueryTimeAsync(ct)).Ticks; } catch { }

        // Push status
        var status = new ServerStatusDto(
            true,
            players.Online,
            players.Max,
            players.Players,
            tps.Tps1Min,
            tps.Tps5Min,
            tps.Tps15Min,
            timeTicks,
            server.Id);

        server.LastStatus = status;
        await _hub.Clients.All.ReceiveServerStatus(status);

        if (!wasConnected)
            await _moderation.SyncServerAsync(server);

        // Push TPS reading
        var tpsReading = new TpsReadingDto(DateTime.UtcNow, tps.Tps1Min, tps.Tps5Min, tps.Tps15Min, server.Id);
        server.AddTpsReading(tpsReading);
        await _hub.Clients.All.ReceiveTpsReading(tpsReading);

        // Detect joins/leaves
        var currentPlayers = new HashSet<string>(players.Players);
        var previous = server.PreviousPlayers;

        foreach (var player in currentPlayers.Except(previous))
        {
            var evt = new ActivityEventDto(DateTime.UtcNow, ActivityEventType.PlayerJoin, player, $"{player} joined the game", server.Id);
            await _hub.Clients.All.ReceiveActivityEvent(evt);
            _logger.LogInformation("Player joined '{Server}': {Player}", server.Id, player);
        }

        foreach (var player in previous.Except(currentPlayers))
        {
            var evt = new ActivityEventDto(DateTime.UtcNow, ActivityEventType.PlayerLeave, player, $"{player} left the game", server.Id);
            await _hub.Clients.All.ReceiveActivityEvent(evt);
            _logger.LogInformation("Player left '{Server}': {Player}", server.Id, player);
        }

        server.PreviousPlayers = currentPlayers;
    }

    private void OnActivityEvent(ActivityEventDto evt)
    {
        // Push activity events from log tailing to all SignalR clients
        _ = _hub.Clients.All.ReceiveActivityEvent(evt);

        // Also push chat messages on the dedicated channel
        if (evt.Type == ActivityEventType.Chat && evt.PlayerName != null)
        {
            _ = _hub.Clients.All.ReceiveChatMessage(
                new ChatMessageDto(evt.Timestamp, evt.PlayerName, evt.Details, evt.ServerId));
        }
    }
}
