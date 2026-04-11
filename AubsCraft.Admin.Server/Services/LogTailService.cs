using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Background service that tails the Minecraft server log file.
/// Uses FileSystemWatcher for notifications with a timer-based fallback.
/// Parses each new line for player events and feeds ActivityLogService.
/// </summary>
public class LogTailService : BackgroundService
{
    private readonly ActivityLogService _activityLog;
    private readonly ILogger<LogTailService> _logger;
    private readonly string _logPath;

    private long _lastPosition;
    private long _lastFileSize;
    private FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _readLock = new(1, 1);

    public LogTailService(
        ActivityLogService activityLog,
        IConfiguration configuration,
        ILogger<LogTailService> logger)
    {
        _activityLog = activityLog;
        _logger = logger;
        _logPath = configuration.GetValue<string>("Minecraft:LogPath") ?? "latest.log";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LogTailService starting, watching: {Path}", _logPath);

        // Wait for app to start
        await Task.Delay(3000, stoppingToken);

        // Read recent history on startup (last portion of the log)
        if (File.Exists(_logPath))
        {
            await LoadRecentHistoryAsync();
        }

        // Set up FileSystemWatcher
        TrySetupWatcher();

        // Fallback timer in case FSW misses events
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReadNewLinesAsync();
        }

        _logger.LogInformation("LogTailService stopped");
    }

    private void TrySetupWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            var fileName = Path.GetFileName(_logPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName)) return;

            _watcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += async (_, _) => await ReadNewLinesAsync();
            _logger.LogInformation("FileSystemWatcher active for {Path}", _logPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileSystemWatcher failed, relying on timer-based polling");
        }
    }

    private async Task ReadNewLinesAsync()
    {
        if (!await _readLock.WaitAsync(0)) return; // Skip if already reading
        try
        {
            if (!File.Exists(_logPath)) return;

            var info = new FileInfo(_logPath);

            // Log rotation detected (file shrunk)
            if (info.Length < _lastFileSize)
            {
                _logger.LogInformation("Log rotation detected, resetting position");
                _lastPosition = 0;
            }
            _lastFileSize = info.Length;

            if (info.Length <= _lastPosition) return; // No new data

            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_lastPosition, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var evt = LogLineParser.Parse(line);
                if (evt != null)
                {
                    _activityLog.AddEvent(evt);
                }
            }

            _lastPosition = fs.Position;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Error reading log file, will retry");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in log tail");
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>
    /// Reads the entire current log file on startup to populate history.
    /// Sets _lastPosition to the end so ongoing tailing only gets new lines.
    /// </summary>
    private async Task LoadRecentHistoryAsync()
    {
        try
        {
            var info = new FileInfo(_logPath);
            _lastFileSize = info.Length;

            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            var eventCount = 0;
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var evt = LogLineParser.Parse(line);
                if (evt != null)
                {
                    _activityLog.AddEvent(evt);
                    eventCount++;
                }
            }

            _lastPosition = fs.Position;
            _logger.LogInformation("Loaded {Count} events from log history, position {Position}", eventCount, _lastPosition);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load log history, starting fresh");
            if (File.Exists(_logPath))
            {
                var info = new FileInfo(_logPath);
                _lastPosition = info.Length;
                _lastFileSize = info.Length;
            }
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _readLock.Dispose();
        base.Dispose();
    }
}
