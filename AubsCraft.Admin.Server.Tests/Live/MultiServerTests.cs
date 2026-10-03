using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using Microsoft.Extensions.Configuration;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The multi-server services against two REAL Paper servers (see TestServers): every per-server operation
/// reaches the right server, and the whitelist and bans reach all of them.
/// </summary>
[NonParallelizable]
public class MultiServerTests
{
    private string _dir = "";
    private IConfiguration _config = null!;
    private ServerRegistry _registry = null!;
    private ServerManager _manager = null!;
    private ActivityLogService _activity = null!;
    private WhitelistAuditService _whitelist = null!;
    private NetworkModerationService _moderation = null!;

    /// <summary>Builds the services over a registry holding the given servers (alpha / bravo).</summary>
    private void Build(params (string id, PaperServer server)[] servers)
    {
        _dir = TestUtil.NewTempDir();
        _config = TestUtil.Config(
            ("Servers:RegistryPath", Path.Combine(_dir, "servers.json")),
            ("ActivityLog:FilePath", Path.Combine(_dir, "activity-log.json")),
            ("Auth:WhitelistAuditPath", Path.Combine(_dir, "whitelist-audit.json")),
            ("Moderation:BansPath", Path.Combine(_dir, "bans.json")));
        _registry = new ServerRegistry(_config, TestUtil.Log<ServerRegistry>());
        // Replace the seeded legacy server with the test servers.
        _registry.Remove(ServerRegistry.LegacyServerId);
        foreach (var (id, server) in servers) _registry.Add(server.ToDefinition(id));
        _activity = new ActivityLogService(_config, TestUtil.Log<ActivityLogService>());
        _manager = new ServerManager(_registry, _activity, _config, TestUtil.Loggers);
        var email = new EmailNotificationService(_config, TestUtil.Log<EmailNotificationService>(), _activity);
        _whitelist = new WhitelistAuditService(_config, _manager, email, TestUtil.Log<WhitelistAuditService>());
        _moderation = new NetworkModerationService(_manager, _whitelist, _config, TestUtil.Log<NetworkModerationService>());
    }

    [SetUp]
    public async Task ResetAsync()
    {
        await TestServers.ResetListsAsync(TestServers.Alpha);
        await TestServers.ResetListsAsync(TestServers.Bravo);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (_manager != null) await _manager.DisposeAsync();
    }

    private static async Task<List<string>> WhitelistOf(PaperServer s)
    {
        await using var rcon = await TestServers.RconAsync(s);
        return await rcon.WhitelistListAsync();
    }

    private static async Task<List<string>> BansOf(PaperServer s)
    {
        await using var rcon = await TestServers.RconAsync(s);
        return await rcon.BanListAsync();
    }

    [Test]
    public async Task EachInstance_TalksToItsOwnServer()
    {
        Build(("alpha", TestServers.Alpha), ("bravo", TestServers.Bravo));
        var alpha = await _manager.Get("alpha")!.Rcon.GetPlayersAsync();
        var bravo = await _manager.Get("bravo")!.Rcon.GetPlayersAsync();
        Assert.That(alpha.Max, Is.EqualTo(7), "alpha is capped at 7");
        Assert.That(bravo.Max, Is.EqualTo(9), "bravo is capped at 9");
        Assert.That(_manager.Get(null)?.Id, Is.EqualTo("alpha"), "no id = the primary (first) server");
        Assert.That(_manager.Get("nope"), Is.Null);
    }

    [Test]
    public async Task EachInstance_ReadsItsOwnWorld()
    {
        Build(("alpha", TestServers.Alpha), ("bravo", TestServers.Bravo));
        // Make both servers write their spawn chunks to region files.
        foreach (var s in new[] { TestServers.Alpha, TestServers.Bravo })
        {
            await using var rcon = await TestServers.RconAsync(s);
            await rcon.SendCommandAsync("save-all flush");
        }

        var alpha = _manager.Get("alpha")!.World;
        var bravo = _manager.Get("bravo")!.World;
        await TestUtil.WaitUntilAsync(() => Task.FromResult(alpha.GetPopulatedChunks().Count > 0 && bravo.GetPopulatedChunks().Count > 0),
            TimeSpan.FromSeconds(30), "both worlds have saved chunks");

        // Bravo's overworld folder is renamed by level-name: the reader must follow it.
        Assert.That(Directory.Exists(Path.Combine(TestServers.Bravo.Dir, "bravo_world", "region")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(TestServers.Bravo.Dir, "world")), Is.False);

        // The spawn chunks saved first; any saved chunk of each world will do.
        var a0 = alpha.GetPopulatedChunks()[0];
        var b0 = bravo.GetPopulatedChunks()[0];
        var flat = alpha.GetChunk(a0.X, a0.Z)!.Palette;
        var normal = bravo.GetChunk(b0.X, b0.Z)!.Palette;
        // A flat world is bedrock + dirt + grass; a normal world has stone/deepslate underground.
        Assert.That(flat, Does.Contain("minecraft:bedrock"));
        Assert.That(flat, Does.Not.Contain("minecraft:stone").And.Not.Contain("minecraft:deepslate"));
        Assert.That(normal.Any(n => n is "minecraft:stone" or "minecraft:deepslate"), Is.True,
            $"bravo's chunk ({b0.X},{b0.Z}) palette: " + string.Join(", ", normal));
    }

    [Test]
    public async Task Whitelist_IsAppliedToEveryServer()
    {
        Build(("alpha", TestServers.Alpha), ("bravo", TestServers.Bravo));
        var (ok, message) = await _whitelist.AddOnBehalfAsync("NetPlayerOne", "Java", "tester", isFriendCapped: false);
        Assert.That(ok, Is.True, message);
        Assert.That(await WhitelistOf(TestServers.Alpha), Does.Contain("NetPlayerOne"));
        Assert.That(await WhitelistOf(TestServers.Bravo), Does.Contain("NetPlayerOne"));

        (ok, message) = await _whitelist.RemoveAsync("NetPlayerOne", "Java");
        Assert.That(ok, Is.True, message);
        Assert.That(await WhitelistOf(TestServers.Alpha), Does.Not.Contain("NetPlayerOne"));
        Assert.That(await WhitelistOf(TestServers.Bravo), Does.Not.Contain("NetPlayerOne"));
    }

    [Test]
    public async Task Bans_AreAppliedToEveryServer()
    {
        Build(("alpha", TestServers.Alpha), ("bravo", TestServers.Bravo));
        await _moderation.BanAsync("Griefer", "test ban", "tester");
        Assert.That(await BansOf(TestServers.Alpha), Does.Contain("Griefer"));
        Assert.That(await BansOf(TestServers.Bravo), Does.Contain("Griefer"));
        Assert.That(_moderation.ListBans().Single().PlayerName, Is.EqualTo("Griefer"));

        await _moderation.PardonAsync("Griefer");
        Assert.That(await BansOf(TestServers.Alpha), Does.Not.Contain("Griefer"));
        Assert.That(await BansOf(TestServers.Bravo), Does.Not.Contain("Griefer"));
        Assert.That(_moderation.ListBans(), Is.Empty);
    }

    [Test]
    public async Task AServerThatMissedChanges_CatchesUpOnSync()
    {
        // Only alpha exists while the changes are made (bravo stands in for a server that was stopped).
        Build(("alpha", TestServers.Alpha));
        await _whitelist.AddOnBehalfAsync("LatePlayer", "Java", "tester", isFriendCapped: false);
        await _moderation.BanAsync("LateGriefer", null, "tester");
        Assert.That(await WhitelistOf(TestServers.Bravo), Does.Not.Contain("LatePlayer"));
        Assert.That(await BansOf(TestServers.Bravo), Does.Not.Contain("LateGriefer"));

        // Bravo joins the network: the monitor syncs a server when it comes online.
        _registry.Add(TestServers.Bravo.ToDefinition("bravo"));
        var bravo = _manager.Get("bravo")!;
        Assert.That(await bravo.Rcon.ConnectAsync(), Is.True);
        await _moderation.SyncServerAsync(bravo);

        Assert.That(await WhitelistOf(TestServers.Bravo), Does.Contain("LatePlayer"));
        Assert.That(await BansOf(TestServers.Bravo), Does.Contain("LateGriefer"));
    }

    [Test]
    public async Task WhitelistAdd_WhenEveryServerIsOffline_IsRecordedForLater()
    {
        // A server definition whose RCON port has nothing listening = a stopped server.
        Build();
        var stopped = TestServers.Alpha.ToDefinition("stopped");
        stopped.RconPort = 25699;
        _registry.Add(stopped);

        var (ok, message) = await _whitelist.AddOnBehalfAsync("OfflineAdd", "Java", "tester", isFriendCapped: false);
        Assert.That(ok, Is.True);
        Assert.That(message, Does.Contain("offline"));
        Assert.That(_whitelist.ListAll().Select(e => e.McUsername), Does.Contain("OfflineAdd"));
    }

    [Test]
    public async Task UpdatingADefinition_RebuildsItsInstance_RemovingDisposesIt()
    {
        Build(("alpha", TestServers.Alpha), ("bravo", TestServers.Bravo));
        var before = _manager.Get("bravo")!;
        Assert.That(await before.Rcon.ConnectAsync(), Is.True);

        var changed = TestServers.Bravo.ToDefinition("bravo");
        changed.Name = "Bravo Renamed";
        _registry.Update(changed);
        var after = _manager.Get("bravo")!;
        Assert.That(after, Is.Not.SameAs(before));
        Assert.That(after.Definition.Name, Is.EqualTo("Bravo Renamed"));
        Assert.That(_manager.Get("alpha"), Is.Not.Null);

        _registry.Remove("bravo");
        Assert.That(_manager.Get("bravo"), Is.Null);
        Assert.That(_manager.All.Select(i => i.Id), Is.EqualTo(new[] { "alpha" }));
    }
}
