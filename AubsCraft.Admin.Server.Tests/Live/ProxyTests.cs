using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The whole Velocity setup, for real: ProxyService downloads Velocity 4.2.0 + every proxy plugin (checksums
/// verified), configures the proxy and two REAL Paper 1.21.5 servers (with Floodgate and Simple Voice Chat
/// installed, as on the VM), and a REAL Minecraft client (mineflayer) plays through it: joins via the proxy,
/// lands on the first server, switches with /server, cannot bypass the proxy, and the server list reloads
/// live. Java 25 for everything (= production).
/// </summary>
[NonParallelizable]
public class ProxyTests
{
    private const int ProxyPort = 25670, BedrockPort = 19232, VoicePort = 24470, ProxyRcon = 25676;
    private static readonly (string id, int port, int rcon, int voice, int max) A = ("proxy-a", 25671, 25681, 24471, 5);
    private static readonly (string id, int port, int rcon, int voice, int max) B = ("proxy-b", 25672, 25682, 24472, 6);

    private readonly AddonDownloader _downloader = new();
    private ProxyService _proxyService = null!;
    private ProxyDefinition _proxy = null!;
    private PaperServer _a = null!, _b = null!;
    private ServerDefinition _defA = null!, _defB = null!;
    private VelocityProcess _velocity = null!;
    private readonly TimeSpan _start = TimeSpan.FromMinutes(6);

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _proxyService = new ProxyService(_downloader, TestUtil.Log<ProxyService>());

        // Backend plugins the VM's server runs that the proxy must cooperate with.
        var floodgate = await _downloader.GeyserMcAsync("floodgate", "spigot");
        var voice = await _downloader.ModrinthAsync("simple-voice-chat", "paper", PaperServer.GameVersion);
        async Task AddPlugins(string dir)
        {
            await _downloader.DownloadAsync(floodgate, Path.Combine(dir, "plugins", floodgate.FileName));
            await _downloader.DownloadAsync(voice, Path.Combine(dir, "plugins", voice.FileName));
        }

        // 1. First start of each server generates its config (paper-global.yml, floodgate/, voicechat/).
        _a = await PaperServer.StartAsync(A.id, A.port, A.rcon, A.max, "minecraft:flat", "world", _start, beforeStart: AddPlugins);
        _b = await PaperServer.StartAsync(B.id, B.port, B.rcon, B.max, "minecraft:flat", "world", _start, beforeStart: AddPlugins);
        await _a.DisposeAsync();
        await _b.DisposeAsync();

        // 2. Proxy: install (verified downloads), first start (generates config + Floodgate key), configure.
        var proxyDir = Path.Combine(PaperServer.CacheRoot, "velocity");
        if (File.Exists(Path.Combine(proxyDir, "velocity.toml"))) File.Delete(Path.Combine(proxyDir, "velocity.toml"));
        _proxy = new ProxyDefinition
        {
            Path = proxyDir,
            BindHost = "127.0.0.1",
            Port = ProxyPort,
            BedrockPort = BedrockPort,
            VoicePort = VoicePort,
            RconPort = ProxyRcon,
            RconPassword = "proxy-" + Guid.NewGuid().ToString("N")[..10],
            OnlineMode = false, // the test client cannot log in to Mojang
        };
        await _proxyService.InstallFilesAsync(_proxy);
        await (await VelocityProcess.StartAsync(proxyDir, _start)).DisposeAsync();

        _defA = _a.ToDefinition(A.id);
        _defA.VoicePort = A.voice;
        _defB = _b.ToDefinition(B.id);
        _defB.VoicePort = B.voice;
        _proxyService.ConfigureProxy(_proxy, [_defA, _defB]);
        _proxyService.ConfigureBackend(_proxy, _defA);
        _proxyService.ConfigureBackend(_proxy, _defB);

        // 3. Everything up as it will run on the VM.
        _a = await PaperServer.StartAsync(A.id, A.port, A.rcon, A.max, "minecraft:flat", "world", _start, fresh: false);
        _b = await PaperServer.StartAsync(B.id, B.port, B.rcon, B.max, "minecraft:flat", "world", _start, fresh: false);
        _velocity = await VelocityProcess.StartAsync(proxyDir, _start);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_velocity != null) await _velocity.DisposeAsync();
        if (_a != null) await _a.DisposeAsync();
        if (_b != null) await _b.DisposeAsync();
    }

    private DateTime _lastConnect = DateTime.MinValue;

    /// <summary>
    /// Starts a bot, at least 3.5 s after the previous one: Velocity's login-ratelimit (3000 ms per IP) resets
    /// a second connection from the same address sooner - correct proxy behaviour, and every bot here is 127.0.0.1.
    /// </summary>
    private async Task<TestBot> ConnectAsync(int port, string name)
    {
        var wait = _lastConnect + TimeSpan.FromSeconds(3.5) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        _lastConnect = DateTime.UtcNow;
        return TestBot.Start("127.0.0.1", port, name);
    }

    private static async Task<List<string>> PlayersOn(PaperServer s)
    {
        await using var rcon = await TestServers.RconAsync(s);
        return (await rcon.GetPlayersAsync()).Players;
    }

    [Test, Order(1)]
    public async Task Proxy_StartsEveryPluginWithoutErrors()
    {
        await _velocity.WaitForLineAsync("Started Geyser on UDP port " + BedrockPort, TimeSpan.FromSeconds(60));
        var output = _velocity.Output;
        foreach (var plugin in new[] { "floodgate", "geyser", "velocircon", "viaversion", "viabackwards", "viarewind", "voicechat", "vvivecraftvelocityextensions" })
            Assert.That(output.Any(l => l.Contains("Loaded plugin " + plugin + " ")), Is.True, "plugin not loaded: " + plugin);
        // It binds the proxy's own address (0.0.0.0 on the VM, 127.0.0.1 here).
        Assert.That(output.Any(l => l.Contains($"Voice chat proxy server started at {_proxy.BindHost}:{VoicePort}")), Is.True,
            string.Join('\n', output.Where(l => l.Contains("oice"))));
        Assert.That(output.Where(l => l.Contains(" ERROR]") || l.Contains("Exception")), Is.Empty);
    }

    [Test, Order(2)]
    public void Backends_AreConfiguredForTheProxy()
    {
        foreach (var (s, d) in new[] { (_a, _defA), (_b, _defB) })
        {
            var props = Path.Combine(s.Dir, "server.properties");
            Assert.That(ConfigFiles.GetProperty(props, "online-mode"), Is.EqualTo("false"));
            Assert.That(ConfigFiles.GetProperty(props, "server-ip"), Is.EqualTo("127.0.0.1"));
            // Paper re-saves the file on start (dropping the quotes): compare values, not formatting.
            var global = File.ReadAllLines(Path.Combine(s.Dir, "config", "paper-global.yml"));
            var velocity = global.SkipWhile(l => l != "  velocity:").Skip(1).TakeWhile(l => l.StartsWith("    ")).ToList();
            var secret = File.ReadAllText(Path.Combine(_proxy.Path, "forwarding.secret")).Trim();
            Assert.That(velocity, Does.Contain("    enabled: true"));
            Assert.That(velocity.Select(l => l.Trim()), Has.Some.Matches<string>(l => l == $"secret: {secret}" || l == $"secret: '{secret}'"),
                string.Join(" | ", velocity));
            Assert.That(File.ReadAllBytes(Path.Combine(s.Dir, "plugins", "floodgate", "key.pem")),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(_proxy.Path, "plugins", "floodgate", "key.pem"))), "Floodgate key copied");
            // The server's voice chat listens on its own internal port, on this machine only.
            Assert.That(s.OutputTail(400).Any(l => l.Contains($"Voice chat server started at 127.0.0.1:{d.VoicePort}")), Is.True,
                string.Join('\n', s.OutputTail(400).Where(l => l.Contains("oice"))));
        }
    }

    [Test, Order(3)]
    public async Task APlayer_JoinsThroughTheProxy_LandsOnTheFirstServer_AndSwitches()
    {
        await using var bot = await ConnectAsync(ProxyPort, "ProxyBot1");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        await TestUtil.WaitUntilAsync(async () => (await PlayersOn(_a)).Contains("ProxyBot1"), TimeSpan.FromSeconds(15), "bot is on proxy-a");
        Assert.That(await PlayersOn(_b), Does.Not.Contain("ProxyBot1"));

        await bot.ChatAsync("/server " + B.id);
        await TestUtil.WaitUntilAsync(async () => (await PlayersOn(_b)).Contains("ProxyBot1"), TimeSpan.FromSeconds(30),
            "bot moved to proxy-b: " + bot.Transcript);
        await TestUtil.WaitUntilAsync(async () => !(await PlayersOn(_a)).Contains("ProxyBot1"), TimeSpan.FromSeconds(15), "bot left proxy-a");
    }

    [Test, Order(4)]
    public async Task ConnectingStraightToAServer_IsRefused()
    {
        // online-mode=false on the server would let anyone in as anyone - modern forwarding must reject them.
        await using var bot = await ConnectAsync(A.port, "Sneaky");
        var e = await bot.WaitForAsync(x => TestBot.Kind(x) is "spawn" or "kicked" or "end", TimeSpan.FromSeconds(30));
        Assert.That(e, Is.Not.Null, bot.Transcript);
        Assert.That(TestBot.Kind(e!.Value), Is.Not.EqualTo("spawn"), "a client got onto the server without the proxy: " + bot.Transcript);
        Assert.That(await PlayersOn(_a), Does.Not.Contain("Sneaky"));
    }

    [Test, Order(5)]
    public async Task TheServerList_ReloadsWithoutRestartingTheProxy()
    {
        // Drop proxy-b, reload: /server proxy-b must fail. Add it back, reload: it works again.
        _proxyService.ConfigureProxy(_proxy, [_defA]);
        var reload = await _proxyService.ReloadAsync(_proxy);
        Assert.That(reload, Does.Contain("reload").IgnoreCase, reload);

        await using (var bot = await ConnectAsync(ProxyPort, "ProxyBot2"))
        {
            await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
            await bot.ChatAsync("/server " + B.id);
            await Task.Delay(3000);
            Assert.That(await PlayersOn(_b), Does.Not.Contain("ProxyBot2"), "proxy-b is still reachable after it was removed");
        }

        _proxyService.ConfigureProxy(_proxy, [_defA, _defB]);
        await _proxyService.ReloadAsync(_proxy);
        await using (var bot = await ConnectAsync(ProxyPort, "ProxyBot3"))
        {
            await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
            await bot.ChatAsync("/server " + B.id);
            await TestUtil.WaitUntilAsync(async () => (await PlayersOn(_b)).Contains("ProxyBot3"), TimeSpan.FromSeconds(30),
                "proxy-b reachable again after reload: " + bot.Transcript);
        }
    }

    [Test, Order(6)]
    public async Task ProxyRcon_ListsPlayersAcrossServers()
    {
        await using var bot = await ConnectAsync(ProxyPort, "ProxyBot4");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        // "glist" alone prints only the total; "glist all" lists every server's players.
        var glist = "";
        await TestUtil.WaitUntilAsync(async () => (glist = await _proxyService.CommandAsync(_proxy, "glist all")).Contains("ProxyBot4"),
            TimeSpan.FromSeconds(15), "glist all shows the player - last output: " + glist);
        Assert.That(glist, Does.Contain(A.id), glist);
    }
}
