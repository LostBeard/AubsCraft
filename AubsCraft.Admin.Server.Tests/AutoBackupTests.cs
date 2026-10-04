using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// Nightly backups (AutoBackupService): the schedule, what is pruned (only nightly backups, keeping 7 days + 4 weeks),
/// and a real night's run over a real server folder - a new "auto" backup, old nightly ones pruned, other backups kept.
/// </summary>
public class AutoBackupTests
{
    private static BackupService.BackupInfo B(string name, DateTime created) => new("s", name, "/x/" + name, 1, created);

    [Test]
    public void Prune_KeepsSevenNightsAndOnePerWeek_AndNeverTouchesOtherBackups()
    {
        var start = new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc); // a Sunday
        var backups = new List<BackupService.BackupInfo>();
        for (var day = 0; day < 40; day++) backups.Add(B($"s-{day:D2}-auto.tar.gz", start.AddDays(-day)));
        backups.Add(B("s-manual.tar.gz", start.AddDays(-100)));
        backups.Add(B("s-x-before-reset.tar.gz", start.AddDays(-90)));
        backups.Add(B("s-x-before-proxy.tar.gz", start.AddDays(-80)));

        var prune = AutoBackupService.SelectToPrune(backups, keepDaily: 7, keepWeekly: 4).Select(b => b.FileName).ToHashSet();
        var kept = backups.Where(b => !prune.Contains(b.FileName)).ToList();

        Assert.That(prune.All(n => n.EndsWith("-auto.tar.gz")), Is.True, "only nightly backups are ever pruned");
        Assert.That(kept.Select(b => b.FileName), Does.Contain("s-manual.tar.gz").And.Contain("s-x-before-reset.tar.gz").And.Contain("s-x-before-proxy.tar.gz"));
        var keptAuto = kept.Where(AutoBackupService.IsAuto).OrderByDescending(b => b.CreatedUtc).ToList();
        Assert.That(keptAuto.Take(7).Select(b => b.CreatedUtc), Is.EqualTo(Enumerable.Range(0, 7).Select(d => start.AddDays(-d))), "the last 7 nights");
        Assert.That(keptAuto.Min(b => b.CreatedUtc), Is.GreaterThan(start.AddDays(-28)), "nothing older than 4 weeks");
        Assert.That(keptAuto.Count, Is.LessThanOrEqualTo(7 + 4));
        Assert.That(keptAuto.Count, Is.GreaterThan(7), "weekly backups beyond the 7 nights");
    }

    [Test]
    public void NextRun_IsFourInTheMorningEastern_InUtc()
    {
        var svc = Create(TestUtil.NewTempDir(), out _);
        // 2026-10-04 is EDT (UTC-4): 04:00 local = 08:00 UTC.
        Assert.That(svc.NextRunUtc(new DateTime(2026, 10, 4, 7, 0, 0, DateTimeKind.Utc)), Is.EqualTo(new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc)));
        Assert.That(svc.NextRunUtc(new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc)), Is.EqualTo(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc)));
        // 2026-12-15 is EST (UTC-5): 04:00 local = 09:00 UTC.
        Assert.That(svc.NextRunUtc(new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc)), Is.EqualTo(new DateTime(2026, 12, 15, 9, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task ANight_BacksUpEachServer_AndPrunesOnlyOldNightlyBackups()
    {
        var dir = TestUtil.NewTempDir();
        var svc = Create(dir, out var registry);
        var on = new ServerDefinition { Id = "on", Name = "On", Path = Path.Combine(dir, "on"), RconHost = "127.0.0.1", RconPort = 1, RconPassword = "x" };
        var off = new ServerDefinition { Id = "off", Name = "Off", Path = Path.Combine(dir, "off"), RconHost = "127.0.0.1", RconPort = 1, RconPassword = "x", AutoBackup = false };
        foreach (var s in new[] { on, off })
        {
            Directory.CreateDirectory(Path.Combine(s.Path, "world"));
            File.WriteAllText(Path.Combine(s.Path, "world", "level.dat"), "data");
            registry.Add(s);
        }
        // Old backups: 40 nightly ones (one a day - more than the 4 kept weeks, so the weekly rule cannot keep the
        // manual one by accident) and a manual one, all older than tonight.
        var backupDir = Path.Combine(dir, "backups", "on");
        Directory.CreateDirectory(backupDir);
        for (var day = 1; day <= 40; day++)
        {
            var f = Path.Combine(backupDir, $"on-old{day:D2}-auto.tar.gz");
            File.WriteAllText(f, "old");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-day));
        }
        var manual = Path.Combine(backupDir, "on-old-manual.tar.gz");
        File.WriteAllText(manual, "keep me");
        File.SetLastWriteTimeUtc(manual, DateTime.UtcNow.AddDays(-60));

        await svc.RunOnceAsync();

        var files = Directory.GetFiles(backupDir).Select(Path.GetFileName).ToList();
        Assert.That(files.Count(f => f!.EndsWith("-auto.tar.gz") && !f.Contains("old")), Is.EqualTo(1), "tonight's backup: " + string.Join(", ", files));
        Assert.That(files, Does.Contain("on-old-manual.tar.gz"), "a manual backup is never pruned");
        Assert.That(files.Count(f => f!.EndsWith("-auto.tar.gz")), Is.InRange(8, 11), "7 nights + weekly ones: " + string.Join(", ", files));
        Assert.That(Directory.Exists(Path.Combine(dir, "backups", "off")), Is.False, "AutoBackup off: no backup");
    }

    private static AutoBackupService Create(string dir, out ServerRegistry registry)
    {
        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json")), ("Backups:Path", Path.Combine(dir, "backups")));
        registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        registry.Remove(ServerRegistry.LegacyServerId);
        var backups = new BackupService(config, TestUtil.Log<BackupService>());
        var proxy = new ProxyService(new AddonDownloader(), TestUtil.Log<ProxyService>());
        var maintenance = new ServerMaintenanceService(registry, backups, proxy, new NoRunner(), TestUtil.Log<ServerMaintenanceService>());
        var portals = new SpawnPortalService(registry, TestUtil.Loggers);
        var ops = new ServerOperationsService(null!, maintenance, backups, portals, registry, null!, new NoHub(), TestUtil.Log<ServerOperationsService>());
        return new AutoBackupService(config, registry, backups, maintenance, ops, TestUtil.Log<AutoBackupService>());
    }

    private sealed class NoRunner : IServiceRunner
    {
        public Task StartAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnableAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisableAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>No admins connected: progress goes nowhere.</summary>
    private sealed class NoHub : IHubContext<ServerHub, IServerHubClient>
    {
        public IHubClients<IServerHubClient> Clients { get; } = new NoClients();
        public IGroupManager Groups => throw new NotSupportedException();
        private sealed class NoClients : IHubClients<IServerHubClient>
        {
            private static readonly IServerHubClient Nobody = System.Reflection.DispatchProxy.Create<IServerHubClient, NoCalls>();
            public IServerHubClient All => Nobody;
            public IServerHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Nobody;
            public IServerHubClient Client(string connectionId) => Nobody;
            public IServerHubClient Clients(IReadOnlyList<string> connectionIds) => Nobody;
            public IServerHubClient Group(string groupName) => Nobody;
            public IServerHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Nobody;
            public IServerHubClient Groups(IReadOnlyList<string> groupNames) => Nobody;
            public IServerHubClient User(string userId) => Nobody;
            public IServerHubClient Users(IReadOnlyList<string> userIds) => Nobody;
        }
    }

    public class NoCalls : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => Task.CompletedTask;
    }
}
