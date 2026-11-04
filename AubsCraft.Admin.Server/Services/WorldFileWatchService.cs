namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Keeps the panel, and the viewers, up with the region files of every server. The game saves chunks and generates new ones all the time, whether it has our plugin or our mod or neither; the panel
/// reads a chunk once and holds it. Before this nothing ever told it (or a viewer) that a chunk it held had changed on disk: ground a player had just explored stayed a hole until the panel restarted.
/// Every few seconds each world is compared with what was last seen (<see cref="WorldDataService.ScanForChanges"/>: a region file's time and size, then only the header of one that moved) and the
/// viewers are sent the chunks that changed, by the same note as the plugin's chunk updates.
/// </summary>
public sealed class WorldFileWatchService : BackgroundService
{
    private readonly ServerManager _servers;
    private readonly LiveFeedService _live;
    private readonly ILogger<WorldFileWatchService> _logger;
    private readonly TimeSpan _every;

    public WorldFileWatchService(ServerManager servers, LiveFeedService live, IConfiguration configuration, ILogger<WorldFileWatchService> logger)
    {
        _servers = servers;
        _live = live;
        _logger = logger;
        _every = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("World:WatchSeconds", 5)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the host's start-up path: a world on a slow drive must not hold the panel's start
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var server in _servers.All)
            {
                if (stoppingToken.IsCancellationRequested) return;
                try
                {
                    bool watched = _live.SubscriberCount(server.Id) > 0;
                    server.World.ScanForChanges(watched ? batch => _live.Broadcast(server.Id, LiveChunkReader.ChangeMessage(batch)) : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Could not look over the world of '{Id}': {Message}", server.Id, ex.Message);
                }
            }
            try { await Task.Delay(_every, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
