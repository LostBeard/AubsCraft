using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Everything the panel holds for ONE Minecraft server: its RCON connection, systemd control, add-ons,
/// stats, world reader, log tailer and the monitor's last status. Built by ServerManager from a
/// ServerDefinition; rebuilt when the definition changes.
/// </summary>
public sealed class ServerInstance : IAsyncDisposable
{
    public ServerDefinition Definition { get; }
    public string Id => Definition.Id;
    public RconService Rcon { get; }
    public ServerControlService Control { get; }
    /// <summary>Null for a server with no add-on folder (Vanilla).</summary>
    public PluginService? Plugins { get; }
    public PlayerStatsService Stats { get; }
    public WorldDataService World { get; }
    public LogTailer LogTail { get; }

    // -- Monitor state (written by ServerMonitorService) --
    public ServerStatusDto? LastStatus { get; internal set; }
    internal HashSet<string> PreviousPlayers { get; set; } = [];
    private readonly List<TpsReadingDto> _tpsHistory = [];
    public const int MaxTpsHistory = 200;

    public ServerInstance(ServerDefinition definition, ActivityLogService activityLog, bool enableSqlite, ILoggerFactory loggers)
    {
        Definition = definition;
        Rcon = new RconService(
            new RconSettings { Host = definition.RconHost, Port = definition.RconPort, Password = definition.RconPassword },
            loggers.CreateLogger<RconService>());
        Control = new ServerControlService(definition.ServiceName, loggers.CreateLogger<ServerControlService>());
        Plugins = definition.AddonsPath is { } addons ? new PluginService(addons, loggers.CreateLogger<PluginService>()) : null;
        Stats = new PlayerStatsService(definition, Rcon, enableSqlite, loggers.CreateLogger<PlayerStatsService>());
        World = new WorldDataService(definition, loggers.CreateLogger<WorldDataService>());
        LogTail = new LogTailer(definition.Id, definition.LogPath, activityLog, loggers.CreateLogger<LogTailer>());
    }

    public List<TpsReadingDto> TpsHistory
    {
        get { lock (_tpsHistory) return _tpsHistory.ToList(); }
    }

    internal void AddTpsReading(TpsReadingDto reading)
    {
        lock (_tpsHistory)
        {
            _tpsHistory.Add(reading);
            while (_tpsHistory.Count > MaxTpsHistory)
                _tpsHistory.RemoveAt(0);
        }
    }

    /// <summary>
    /// The names on the server's whitelist, read from its whitelist.json: exact, and available while the server
    /// is stopped. The server rewrites the file on every whitelist change.
    /// </summary>
    public List<string> ReadWhitelist() => ReadNames("whitelist.json");

    /// <summary>
    /// The banned player names, from the server's banned-players.json. Over RCON the "banlist" entries arrive
    /// concatenated and cannot always be split (see MinecraftRconClient.BanListAsync); the file is exact.
    /// </summary>
    public List<string> ReadBannedPlayers() => ReadNames("banned-players.json");

    private List<string> ReadNames(string file)
    {
        var path = System.IO.Path.Combine(Definition.Path, file);
        if (!File.Exists(path)) return [];
        // Shared read: the server may be rewriting the file right now.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = System.Text.Json.JsonDocument.Parse(fs);
        return doc.RootElement.EnumerateArray()
            .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null)
            .OfType<string>()
            .ToList();
    }

    public ServerSummaryDto ToSummary(bool isPrimary) => new(
        Definition.Id,
        Definition.Name,
        Definition.Loader.ToString(),
        Definition.GameVersion,
        Definition.GamePort,
        Definition.MemoryMb,
        isPrimary,
        LastStatus?.Connected == true,
        LastStatus?.Online ?? 0,
        LastStatus?.Max ?? 0);

    public async ValueTask DisposeAsync()
    {
        LogTail.Dispose();
        await Rcon.DisposeAsync();
    }
}
