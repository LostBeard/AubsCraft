using System.Collections.Concurrent;
using System.Text.Json;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Stores activity events in a ring buffer and persists to JSON file.
/// Events are pushed to SignalR via the EventAdded event.
/// </summary>
public class ActivityLogService : IDisposable
{
    private readonly ConcurrentQueue<ActivityEventDto> _events = new();
    private readonly int _maxEvents;
    private readonly string _filePath;
    private readonly ILogger<ActivityLogService> _logger;
    private readonly Timer _flushTimer;

    public event Action<ActivityEventDto>? EventAdded;

    public ActivityLogService(IConfiguration configuration, ILogger<ActivityLogService> logger)
    {
        _logger = logger;
        _maxEvents = configuration.GetValue("ActivityLog:MaxEvents", 1000);
        _filePath = configuration.GetValue<string>("ActivityLog:FilePath") ?? "activity-log.json";

        LoadFromFile();

        // Flush to disk every 5 minutes
        _flushTimer = new Timer(_ => FlushToFile(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public void AddEvent(ActivityEventDto evt)
    {
        _events.Enqueue(evt);

        // Trim to max
        while (_events.Count > _maxEvents)
            _events.TryDequeue(out _);

        EventAdded?.Invoke(evt);
    }

    public List<ActivityEventDto> GetRecent(int count, ActivityEventType? filter = null)
    {
        var query = _events.AsEnumerable().Reverse();
        if (filter.HasValue)
            query = query.Where(e => e.Type == filter.Value);
        return query.Take(count).Reverse().ToList();
    }

    public void FlushToFile()
    {
        try
        {
            var events = _events.ToArray();
            var json = JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush activity log to {Path}", _filePath);
        }
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var events = JsonSerializer.Deserialize<List<ActivityEventDto>>(json);
            if (events != null)
            {
                foreach (var evt in events)
                    _events.Enqueue(evt);
                _logger.LogInformation("Loaded {Count} activity events from {Path}", events.Count, _filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load activity log from {Path}", _filePath);
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        FlushToFile();
    }
}
