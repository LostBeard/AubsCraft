using System.Text.Json;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Log tailing and the activity log keep events apart per server.</summary>
public class ActivityTests
{
    private static ActivityLogService NewActivityLog(string dir) =>
        new(TestUtil.Config(("ActivityLog:FilePath", Path.Combine(dir, "activity-log.json"))), TestUtil.Log<ActivityLogService>());

    [Test]
    public async Task LogTailer_TagsEveryEventWithItsServer_AndTailsNewLines()
    {
        var dir = TestUtil.NewTempDir();
        var activity = NewActivityLog(dir);
        var logA = Path.Combine(dir, "a.log");
        var logB = Path.Combine(dir, "b.log");
        // Real Paper 1.21.5 line format.
        File.WriteAllText(logA, "[14:05:36] [Server thread/INFO]: SpudArt joined the game\n");
        File.WriteAllText(logB, "[14:05:40] [Server thread/INFO]: <Aubs> hi from bravo\n");

        using var a = new LogTailer("alpha", logA, activity, TestUtil.Log<LogTailer>());
        using var b = new LogTailer("bravo", logB, activity, TestUtil.Log<LogTailer>());
        await a.PollAsync(); // first poll = history
        await b.PollAsync();

        File.AppendAllText(logA, "[14:06:00] [Server thread/INFO]: SpudArt left the game\n");
        await a.PollAsync();

        var all = activity.GetRecent(100);
        Assert.That(all.Select(e => (e.ServerId, e.Type)), Is.EquivalentTo(new[]
        {
            ("alpha", ActivityEventType.PlayerJoin),
            ("bravo", ActivityEventType.Chat),
            ("alpha", ActivityEventType.PlayerLeave),
        }));
        Assert.That(activity.GetRecent(100, null, "bravo").Single().Details, Is.EqualTo("hi from bravo"));
        Assert.That(activity.GetRecent(100, null, "alpha"), Has.Count.EqualTo(2));
    }

    [Test]
    public void SavedEventsWithoutAServer_LoadAsTheOriginalServer()
    {
        var dir = TestUtil.NewTempDir();
        // activity-log.json as written before multi-server support: no ServerId field at all.
        File.WriteAllText(Path.Combine(dir, "activity-log.json"),
            """[{"Timestamp":"2026-10-01T12:00:00Z","Type":0,"PlayerName":"SpudArt","Details":"SpudArt joined the game"}]""");

        var activity = NewActivityLog(dir);
        var evt = activity.GetRecent(10).Single();
        Assert.That(evt.ServerId, Is.EqualTo(ServerRegistry.LegacyServerId));

        // And the id is written back on the next flush.
        activity.FlushToFile();
        var saved = JsonSerializer.Deserialize<List<ActivityEventDto>>(File.ReadAllText(Path.Combine(dir, "activity-log.json")))!;
        Assert.That(saved.Single().ServerId, Is.EqualTo(ServerRegistry.LegacyServerId));
    }
}
