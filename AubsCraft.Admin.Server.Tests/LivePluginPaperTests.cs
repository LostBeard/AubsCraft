using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Admin.Server.Tests.Live;
using AubsCraft.Live.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The AubsCraft Live plugin on a REAL Paper 1.21.5 server, the panel on real Kestrel, and nothing faked between them: the panel installs the plugin into
/// the server's plugins folder (jar and config), the server loads it, the plugin samples the world's entities on the main thread and posts frames over
/// loopback with the secret, the panel accepts them, and a viewer's subscription gets them. RCON summons creatures with known positions, colours, ages and
/// names; the frames must show exactly those.
/// </summary>
[NonParallelizable]
public class LivePluginPaperTests
{
    private const int GamePort = 25851, RconPort = 25852, PanelPort = 25853;
    private const string Secret = "paper-test-secret";

    private sealed class AppFactory(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
        }
    }

    private AppFactory _factory = null!;
    private PaperServer _paper = null!;
    private LiveFeedService _feed = null!;
    private MinecraftRconClient _rcon = null!;
    private string _id = "livetest";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var dir = TestUtil.NewTempDir();
        var serverDir = Path.Combine(PaperServer.CacheRoot, "servers", "livetest");
        var definition = new ServerDefinition
        {
            Id = _id, Name = "Live test", Loader = ServerLoader.Paper, GameVersion = PaperServer.GameVersion, Path = serverDir,
            ServiceName = "test-livetest", RconHost = "127.0.0.1", RconPort = RconPort, GamePort = GamePort, MemoryMb = 1024,
        };
        // RCON's password is chosen when the server's files are written: the registry is written after the server starts, so start the panel after
        _paper = await PaperServer.StartAsync("livetest", GamePort, RconPort, 4, "minecraft:flat", "world", TimeSpan.FromMinutes(6), beforeStart: async folder =>
        {
            // Before java starts: the panel's own installer puts the plugin in place, exactly as the Plugins page does
            using var host = new AppFactory(new()
            {
                ["Live:Secret"] = Secret,
                ["Live:PluginPanelUrl"] = $"http://127.0.0.1:{PanelPort}",
                ["Servers:RegistryPath"] = Path.Combine(dir, "unused-servers.json"),
                ["Minecraft:ServerPath"] = folder,
                ["ActivityLog:FilePath"] = Path.Combine(dir, "activity-log.json"),
                ["Auth:UsersPath"] = Path.Combine(dir, "users.json"),
                ["Auth:CredentialsPath"] = Path.Combine(dir, "admin.json"),
                ["Auth:InviteCodesPath"] = Path.Combine(dir, "invite-codes.json"),
                ["Auth:WhitelistAuditPath"] = Path.Combine(dir, "whitelist-audit.json"),
                ["Moderation:BansPath"] = Path.Combine(dir, "bans.json"),
            });
            var plugin = host.Services.GetRequiredService<LivePluginService>();
            var result = plugin.Install(definition);
            Assert.That(result.Success && result.Changed, Is.True, result.Message);
            await Task.CompletedTask;
        });

        definition.RconPassword = _paper.RconPassword;
        var registryPath = Path.Combine(dir, "servers.json");
        File.WriteAllText(registryPath, System.Text.Json.JsonSerializer.Serialize(new ServerRegistryFile { Servers = [definition] }));
        _factory = new AppFactory(new()
        {
            ["Live:Secret"] = Secret,
            ["Live:PluginPanelUrl"] = $"http://127.0.0.1:{PanelPort}",
            ["Servers:RegistryPath"] = registryPath,
            ["ActivityLog:FilePath"] = Path.Combine(dir, "activity-log.json"),
            ["Auth:UsersPath"] = Path.Combine(dir, "users.json"),
            ["Auth:CredentialsPath"] = Path.Combine(dir, "admin.json"),
            ["Auth:InviteCodesPath"] = Path.Combine(dir, "invite-codes.json"),
            ["Auth:WhitelistAuditPath"] = Path.Combine(dir, "whitelist-audit.json"),
            ["Moderation:BansPath"] = Path.Combine(dir, "bans.json"),
        });
        _factory.UseKestrel(PanelPort);
        _factory.StartServer();
        _feed = _factory.Services.GetRequiredService<LiveFeedService>();

        _rcon = new MinecraftRconClient("127.0.0.1", RconPort);
        Assert.That(await _rcon.ConnectAsync(_paper.RconPassword), Is.True, "RCON");
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_rcon != null) await _rcon.DisposeAsync();
        if (_factory != null) await _factory.DisposeAsync();
        if (_paper != null) await _paper.DisposeAsync();
    }

    /// <summary>The newest frame the panel holds for the server, parsed.</summary>
    private LiveFrame? Latest()
    {
        using var sub = _feed.Subscribe(_id);
        return sub.Frames.TryRead(out var bytes) ? LiveFrameCodec.Parse(bytes) : null;
    }

    private async Task<LiveFrame> WaitForFrameAsync(Func<LiveFrame, bool> ok, string what, int seconds = 30)
    {
        LiveFrame? last = null;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            last = Latest();
            if (last != null && ok(last)) return last;
            await Task.Delay(250);
        }
        throw new AssertionException($"Timed out waiting for {what}. Last frame: {(last == null ? "none" : $"{last.Worlds.Sum(w => w.Entities.Count)} entities")}\n" +
                                     string.Join('\n', _paper.OutputTail(15)));
    }

    private static LiveEntity? Find(LiveFrame f, Func<LiveEntity, bool> match) => f.Worlds.SelectMany(w => w.Entities).Cast<LiveEntity?>().FirstOrDefault(e => match(e!.Value));

    [Test, Order(1)]
    public async Task ThePlugin_LoadsOnTheRealServer_AndTheFramesArriveAtThePanel()
    {
        await _rcon.SendCommandAsync("gamerule doMobSpawning false");
        await _rcon.SendCommandAsync("kill @e[type=!player]");
        var frame = await WaitForFrameAsync(_ => true, "the first frame");
        Assert.That(_paper.OutputTail(400).Any(l => l.Contains("AubsCraft Live") || l.Contains("AubsCraftLive")), Is.True, "the server loaded the plugin");
        Assert.That(frame.Worlds[0].Name, Is.EqualTo("world"));
        Assert.That(_feed.StatusOf(_id).Live, Is.True);
        Assert.That(frame.FullTime, Is.GreaterThan(0));
        Assert.That(frame.DayTime, Is.InRange(0, 24000));
    }

    [Test, Order(2)]
    public async Task SummonedCreatures_ArriveWithTheirType_Place_Age_Colour_AndName()
    {
        // Frozen in place (no AI, no gravity) so the positions are exact
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon cow 8.5 100 8.5 {NoAI:1b,NoGravity:1b,Rotation:[90f,0f]}");
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon sheep 12.5 100 8.5 {NoAI:1b,NoGravity:1b,Color:6b,CustomName:'Dolly'}");
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon pig 16.5 100 8.5 {NoAI:1b,NoGravity:1b,Age:-24000}");
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon villager 20.5 100 8.5 {NoAI:1b,NoGravity:1b}");

        var frame = await WaitForFrameAsync(f => Find(f, e => e.Type == "pig") != null && Find(f, e => e.Type == "villager") != null, "the summoned creatures");
        var cow = Find(frame, e => e.Type == "cow") ?? throw new AssertionException("no cow");
        Assert.That(cow.X, Is.EqualTo(8.5f).Within(0.01f));
        Assert.That(cow.Y, Is.EqualTo(100f).Within(0.01f));
        Assert.That(cow.Z, Is.EqualTo(8.5f).Within(0.01f));
        Assert.That(cow.Yaw, Is.EqualTo(90f).Within(1.5f), "the yaw is Minecraft's degrees");
        Assert.That(cow.Baby, Is.False);

        var sheep = Find(frame, e => e.Type == "sheep") ?? throw new AssertionException("no sheep");
        Assert.That(sheep.Variant, Is.EqualTo("pink"), "dye 6 is pink");
        Assert.That(sheep.Name, Is.EqualTo("Dolly"));
        Assert.That(sheep.X, Is.EqualTo(12.5f).Within(0.01f));

        var pig = Find(frame, e => e.Type == "pig") ?? throw new AssertionException("no pig");
        Assert.That(pig.Baby, Is.True, "Age -24000 is a baby");

        var villager = Find(frame, e => e.Type == "villager") ?? throw new AssertionException("no villager");
        Assert.That(villager.Variant, Is.Not.Null.And.Not.Empty, "a villager reports its profession");
    }

    [Test, Order(3)]
    public async Task AMovedCreature_IsWhereItWent_AndADeadOneIsGone()
    {
        await _rcon.SendCommandAsync("execute in minecraft:overworld run summon chicken 30.5 100 30.5 {NoAI:1b,NoGravity:1b}");
        await WaitForFrameAsync(f => Find(f, e => e.Type == "chicken") != null, "the chicken");

        await _rcon.SendCommandAsync("tp @e[type=chicken,limit=1] 34.5 101 30.5");
        var moved = await WaitForFrameAsync(f => Find(f, e => e.Type == "chicken" && MathF.Abs(e.X - 34.5f) < 0.05f) != null, "the chicken at its new place");
        var chicken = Find(moved, e => e.Type == "chicken")!.Value;
        Assert.That(chicken.Y, Is.EqualTo(101f).Within(0.05f));

        await _rcon.SendCommandAsync("kill @e[type=chicken]");
        await WaitForFrameAsync(f => Find(f, e => e.Type == "chicken") == null, "the chicken gone");
    }

    [Test, Order(4)]
    public async Task TheFrames_FollowTheWorldsClock_AndItsWeather()
    {
        await _rcon.SendCommandAsync("time set 13000");
        var night = await WaitForFrameAsync(f => f.DayTime is >= 13000 and < 13200, "night", seconds: 15);
        await _rcon.SendCommandAsync("weather thunder");
        await WaitForFrameAsync(f => f.Raining && f.Thundering, "a thunderstorm", seconds: 15);
        await _rcon.SendCommandAsync("weather clear");
        await WaitForFrameAsync(f => !f.Raining && !f.Thundering, "clear weather", seconds: 15);
        Assert.That(night.FullTime, Is.GreaterThan(0));
    }

    [Test, Order(5)]
    public async Task TheFramesKeepComing_AtTheConfiguredRate()
    {
        long before = _feed.StatusOf(_id).Frames;
        await Task.Delay(3000);
        long got = _feed.StatusOf(_id).Frames - before;
        // Every 4 ticks is 5 a second: 15 in 3 s; a loaded machine may drop some (one in flight at a time), never most
        Assert.That(got, Is.GreaterThanOrEqualTo(8), "frames in 3 seconds");
        Assert.That(got, Is.LessThanOrEqualTo(20), "no faster than the configured rate");
    }

    [Test, Order(6)]
    public async Task APlayer_IsInTheFrames_WithItsNameUuidAndModeAndItMoves()
    {
        await using var bot = TestBot.Start("127.0.0.1", GamePort, "LiveWatcher");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        var frame = await WaitForFrameAsync(f => f.Worlds.SelectMany(w => w.Players).Any(p => p.Name == "LiveWatcher"), "the player", seconds: 30);
        var world = frame.Worlds.First(w => w.Players.Any(p => p.Name == "LiveWatcher"));
        var player = world.Players.First(p => p.Name == "LiveWatcher");
        Assert.That(Guid.TryParse(player.Uuid, out _), Is.True, player.Uuid);
        Assert.That(player.Health, Is.GreaterThan(0.9f), "a fresh player has full health");
        Assert.That(player.GameMode, Is.InRange(0, 3));
        var entity = world.Entities.First(e => e.Id == player.EntityId);
        Assert.That(entity.IsPlayer, Is.True);
        Assert.That(entity.Type, Is.EqualTo("player"));

        // Walking changes where it is
        float x0 = entity.X, z0 = entity.Z;
        await bot.WalkAsync(TimeSpan.FromSeconds(2));
        var later = await WaitForFrameAsync(f => f.Worlds.SelectMany(w => w.Entities).Any(e => e.Id == player.EntityId && (MathF.Abs(e.X - x0) > 0.5f || MathF.Abs(e.Z - z0) > 0.5f)),
            "the player somewhere else", seconds: 10);
        Assert.That(later.Worlds.SelectMany(w => w.Players).Any(p => p.Name == "LiveWatcher"), Is.True);
    }
}
