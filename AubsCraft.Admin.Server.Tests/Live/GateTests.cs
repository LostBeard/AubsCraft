using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The AubsCraft Gate proxy plugin (ProxyGate/, installed by ProxyService) on a REAL Velocity with two REAL Paper
/// servers, one of them Java-only (it has client mods). Modded Java players (client brand "fabric", as QuestCraft and
/// the PC pack report) switch to it; an unmodded PC player (brand "vanilla") and a Bedrock player are refused with a
/// message saying what to do, and stay where they are. A test client cannot connect through Geyser, so the Bedrock player is a
/// normal client named in the plugin's test switch (-Daubscraft.gate.testBedrockNames) - production uses
/// Floodgate's own isFloodgatePlayer.
/// </summary>
[NonParallelizable]
public class GateTests
{
    private const int ProxyPort = 25810, MainPort = 25811, MainRcon = 25812, ModdedPort = 25813, ModdedRcon = 25814, ProxyRcon = 25815;
    private const string BedrockName = "GateBedrock";

    private PaperServer _mainServer = null!, _moddedServer = null!;
    private VelocityProcess _proxyProcess = null!;
    private ServerDefinition _main = null!, _modded = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var dir = TestUtil.NewTempDir();
        var proxyDir = Path.Combine(PaperServer.CacheRoot, "velocity-gate");
        foreach (var f in new[] { "velocity.toml", "forwarding.secret" }) { var p = Path.Combine(proxyDir, f); if (File.Exists(p)) File.Delete(p); }
        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json")));
        var registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        registry.Remove(ServerRegistry.LegacyServerId);
        var proxyService = new ProxyService(new AddonDownloader(), TestUtil.Log<ProxyService>());

        var main = await PaperServer.StartAsync("gatemain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6));
        await main.DisposeAsync();
        var modded = await PaperServer.StartAsync("gatemodded", ModdedPort, ModdedRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6));
        await modded.DisposeAsync();
        _main = main.ToDefinition("gatemain");
        _modded = modded.ToDefinition("gatemodded");
        _modded.Name = "Gate Modded";
        _modded.ClientMods = ["from-the-fog"]; // client mods = Bedrock cannot play it
        registry.Add(_main);
        registry.Add(_modded);

        var proxy = new ProxyDefinition
        {
            Path = proxyDir, ServiceName = "velocity-gate", BindHost = "127.0.0.1", Port = ProxyPort,
            BedrockPort = 19310, VoicePort = 24810, RconPort = ProxyRcon, OnlineMode = false,
            RconPassword = "gate-" + Guid.NewGuid().ToString("N")[..8],
        };
        await proxyService.InstallFilesAsync(proxy);
        Assert.That(File.Exists(Path.Combine(proxyDir, "plugins", "aubscraft-gate.jar")), Is.True, "InstallFilesAsync installs the gate");
        await (await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6))).DisposeAsync();
        registry.SetProxy(proxy);
        proxyService.ConfigureProxy(proxy, registry.All);
        proxyService.ConfigureBackend(proxy, _main);
        proxyService.ConfigureBackend(proxy, _modded);
        Assert.That(File.ReadAllText(Path.Combine(proxyDir, "plugins", "aubscraft-gate", "java-only.txt")),
            Does.Contain("gatemodded\tGate Modded").And.Not.Contain("gatemain"), "only servers with client mods are Java-only");

        _mainServer = await PaperServer.StartAsync("gatemain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6), fresh: false);
        _moddedServer = await PaperServer.StartAsync("gatemodded", ModdedPort, ModdedRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6), fresh: false);
        _proxyProcess = await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6), "-Daubscraft.gate.testBedrockNames=" + BedrockName);
        Assert.That(_proxyProcess.Output, Has.Some.Contains("aubscraft-gate"), "Velocity loaded the gate plugin");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_proxyProcess != null) await _proxyProcess.DisposeAsync();
        if (_mainServer != null) await _mainServer.DisposeAsync();
        if (_moddedServer != null) await _moddedServer.DisposeAsync();
    }

    private static async Task<bool> IsOn(ServerDefinition s, string player)
    {
        await using var rcon = new MinecraftRconClient("127.0.0.1", s.RconPort);
        return await rcon.ConnectAsync(s.RconPassword) && (await rcon.GetPlayersAsync()).Players.Contains(player);
    }

    [Test, Order(1)]
    public async Task ModdedJavaPlayer_SwitchesToTheJavaOnlyServer()
    {
        await using var bot = TestBot.Start("127.0.0.1", ProxyPort, "GateJava", brand: "fabric");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        await TestUtil.WaitUntilAsync(() => IsOn(_main, "GateJava"), TimeSpan.FromSeconds(15), "bot on the main server");
        await bot.ChatAsync("/server " + _modded.Id);
        await TestUtil.WaitUntilAsync(() => IsOn(_modded, "GateJava"), TimeSpan.FromSeconds(30), "a Java player reaches the Java-only server: " + bot.Transcript);
    }

    [Test, Order(2)]
    public async Task UnmoddedPcPlayer_IsKeptOff_AndToldWhereToGetThePack()
    {
        await Task.Delay(3500); // Velocity login-ratelimit since the previous bot
        await using var bot = TestBot.Start("127.0.0.1", ProxyPort, "GateVanilla");
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        await TestUtil.WaitUntilAsync(() => IsOn(_main, "GateVanilla"), TimeSpan.FromSeconds(15), "bot on the main server");
        await bot.ChatAsync("/server " + _modded.Id);

        var message = await bot.WaitForAsync(e => TestBot.Kind(e) == "message" && e.GetProperty("text").GetString()!.Contains("needs mods on your game"),
            TimeSpan.FromSeconds(15));
        Assert.That(message, Is.Not.Null, "the player is told why: " + bot.Transcript);
        Assert.That(message!.Value.GetProperty("text").GetString(), Does.Contain(ProxyService.DefaultPublicUrl + "/pc"));
        await Task.Delay(3000);
        Assert.That(await IsOn(_modded, "GateVanilla"), Is.False, "not moved to the modded server");
        Assert.That(await IsOn(_main, "GateVanilla"), Is.True, "still on the main server, still connected");
    }

    [Test, Order(3)]
    public async Task BedrockPlayer_IsKeptOffTheJavaOnlyServer_WithAMessage()
    {
        await Task.Delay(3500); // Velocity login-ratelimit since the previous bot
        await using var bot = TestBot.Start("127.0.0.1", ProxyPort, BedrockName);
        await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
        await TestUtil.WaitUntilAsync(() => IsOn(_main, BedrockName), TimeSpan.FromSeconds(15), "bot on the main server");
        await bot.ChatAsync("/server " + _modded.Id);

        var message = await bot.WaitForAsync(e => TestBot.Kind(e) == "message" && e.GetProperty("text").GetString()!.Contains("needs Java Edition"),
            TimeSpan.FromSeconds(15));
        Assert.That(message, Is.Not.Null, "the Bedrock player is told why: " + bot.Transcript);
        Assert.That(message!.Value.GetProperty("text").GetString(), Does.Contain("Gate Modded"));
        await Task.Delay(3000);
        Assert.That(await IsOn(_modded, BedrockName), Is.False, "not moved to the Java-only server");
        Assert.That(await IsOn(_main, BedrockName), Is.True, "still on the main server, still connected");
        Assert.That(_proxyProcess.Output, Has.Some.Contains($"Kept Bedrock player {BedrockName} off Java-only server {_modded.Id}"));
    }
}
