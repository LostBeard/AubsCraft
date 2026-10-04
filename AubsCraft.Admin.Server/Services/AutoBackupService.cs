using System.Globalization;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Nightly backups: at Backups:DailyAt (default 04:00) in Backups:TimeZone (default America/New_York), every server with
/// AutoBackup on is backed up (label "auto") through ServerOperationsService - one operation at a time, so it waits for
/// a manual operation to finish - the same way "Back up now" does it (a running server keeps running). Then old
/// nightly backups are pruned: the newest KeepDaily (7) are kept, plus the newest of each of the last KeepWeekly (4)
/// weeks. ONLY "auto" backups are ever deleted - manual, before-reset, removed and before-proxy backups stay.
/// A night is skipped (with a warning) when the disk has less free space than twice the server's last backup.
/// </summary>
public class AutoBackupService : BackgroundService
{
    public const string Label = "auto";

    private readonly ServerRegistry _registry;
    private readonly BackupService _backups;
    private readonly ServerMaintenanceService _maintenance;
    private readonly ServerOperationsService _operations;
    private readonly ILogger<AutoBackupService> _logger;

    public TimeOnly DailyAt { get; }
    public TimeZoneInfo TimeZone { get; }
    public int KeepDaily { get; }
    public int KeepWeekly { get; }

    public AutoBackupService(IConfiguration config, ServerRegistry registry, BackupService backups, ServerMaintenanceService maintenance,
        ServerOperationsService operations, ILogger<AutoBackupService> logger)
    {
        _registry = registry;
        _backups = backups;
        _maintenance = maintenance;
        _operations = operations;
        _logger = logger;
        DailyAt = TimeOnly.Parse(config.GetValue<string>("Backups:DailyAt") ?? "04:00", CultureInfo.InvariantCulture);
        TimeZone = FindZone(config.GetValue<string>("Backups:TimeZone") ?? "America/New_York");
        KeepDaily = config.GetValue("Backups:KeepDaily", 7);
        KeepWeekly = config.GetValue("Backups:KeepWeekly", 4);
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    /// <summary>What the Servers page shows.</summary>
    public string ScheduleText =>
        $"Every night at {DailyAt.ToString("h:mm tt", CultureInfo.InvariantCulture)} ({TimeZone.StandardName}); keeps the last {KeepDaily} nights and one a week for {KeepWeekly} weeks.";

    /// <summary>The next run after <paramref name="utcNow"/>, in UTC.</summary>
    public DateTime NextRunUtc(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZone);
        var next = local.Date + DailyAt.ToTimeSpan();
        if (next <= local) next = next.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), TimeZone);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NextRunUtc(DateTime.UtcNow);
            _logger.LogInformation("Next nightly backup at {Next:u}", next);
            try { await Task.Delay(next - DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) { return; }
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>Backs up every server with AutoBackup on, then prunes its old nightly backups.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        foreach (var def in _registry.All.Where(s => s.AutoBackup).ToList())
        {
            var last = _backups.List(def.Id).FirstOrDefault();
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_backups.Root))!).AvailableFreeSpace;
            if (last != null && free < last.SizeBytes * 2)
            {
                _logger.LogWarning("Skipped the nightly backup of {Server}: {Free:N0} bytes free, its last backup was {Size:N0}", def.Id, free, last.SizeBytes);
                continue;
            }
            // One operation at a time: wait (up to 2 hours) for a manual operation to finish.
            var end = DateTime.UtcNow.AddHours(2);
            bool? ok = null;
            while (ok == null && DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                ok = await _operations.RunScheduledAsync($"Nightly backup of {def.Name}", p => _maintenance.BackupAsync(def.Id, Label, p, ct));
                if (ok == null) await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            if (ok != true) { _logger.LogWarning("The nightly backup of {Server} did not run (ok={Ok})", def.Id, ok); continue; }
            foreach (var old in SelectToPrune(_backups.List(def.Id), KeepDaily, KeepWeekly))
            {
                File.Delete(old.Path);
                _logger.LogInformation("Pruned old nightly backup {File}", old.FileName);
            }
        }
    }

    public static bool IsAuto(BackupService.BackupInfo b) => b.FileName.EndsWith("-" + Label + ".tar.gz", StringComparison.Ordinal);

    /// <summary>
    /// The nightly backups to delete: all but the newest <paramref name="keepDaily"/>, and the newest of each of the most
    /// recent <paramref name="keepWeekly"/> weeks. Backups that are not nightly are never selected.
    /// </summary>
    public static List<BackupService.BackupInfo> SelectToPrune(IEnumerable<BackupService.BackupInfo> backups, int keepDaily, int keepWeekly)
    {
        var autos = backups.Where(IsAuto).OrderByDescending(b => b.CreatedUtc).ToList();
        var keep = autos.Take(keepDaily).ToHashSet();
        foreach (var week in autos.GroupBy(b => (ISOWeek.GetYear(b.CreatedUtc), ISOWeek.GetWeekOfYear(b.CreatedUtc))).Take(keepWeekly))
            keep.Add(week.First());
        return autos.Where(b => !keep.Contains(b)).ToList();
    }
}
