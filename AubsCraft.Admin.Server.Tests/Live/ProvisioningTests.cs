using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// Creating servers behind the proxy with the real ServerProvisioningService: a Paper main server behind a
/// real Velocity 4.2.0 (as on the VM after the cutover), then new servers created from nothing. A real client
/// /server's into a new Fabric server through the proxy; Aubri's spooky mod set loads; a failed create leaves
/// nothing behind. New servers are started the way minecraft@.service starts them (aubscraft.env).
/// </summary>
[NonParallelizable]
public class ProvisioningTests
{
    private const int ProxyPort = 25750, MainPort = 25751, MainRcon = 25752, ProxyRcon = 25753;
    private const string MainService = "minecraft-provmain", ProxyServiceName = "velocity-prov";

    /// <summary>The spooky set proposed for Aubri's server (Fabric 1.21.5).</summary>
    public static readonly string[] SpookyMods =
    [
        "from-the-fog", "server-side-horror", "sleepless-datapack", "qraftys-halloween-villages", "flower-mimics",
        "macaws-holidays", "mutant-monsters", "spooky-doors",
    ];

    private sealed class Runner(string serversRoot, string proxyDir) : IServiceRunner
    {
        public readonly Dictionary<string, EnvFileProcess> Servers = [];
        public PaperServer? Main;
        public VelocityProcess? Proxy;

        public async Task StartAsync(string name, CancellationToken ct = default)
        {
            if (name == MainService) Main = await PaperServer.StartAsync("provmain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6), fresh: false);
            else if (name == ProxyServiceName) Proxy = await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6));
            else if (name.StartsWith("minecraft@")) Servers[name] = await EnvFileProcess.StartAsync(Path.Combine(serversRoot, name["minecraft@".Length..]));
            else throw new ArgumentException(name);
        }

        public async Task StopAsync(string name, CancellationToken ct = default)
        {
            if (name == MainService) { if (Main != null) await Main.DisposeAsync(); Main = null; }
            else if (name == ProxyServiceName) { if (Proxy != null) await Proxy.DisposeAsync(); Proxy = null; }
            else if (Servers.Remove(name, out var p)) await p.DisposeAsync();
        }

        public Task EnableAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisableAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly AddonDownloader _downloader = new();
    private ServerRegistry _registry = null!;
    private ProxyService _proxyService = null!;
    private ServerProvisioningService _provisioning = null!;
    private Runner _runner = null!;
    private string _serversRoot = "";

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var dir = TestUtil.NewTempDir();
        _serversRoot = Path.Combine(PaperServer.CacheRoot, "prov-servers");
        if (Directory.Exists(_serversRoot)) Directory.Delete(_serversRoot, true);
        var proxyDir = Path.Combine(PaperServer.CacheRoot, "velocity-prov");
        // A new proxy each run - including ViaVersion's config, whose saved server versions would otherwise hide a
        // missing "velocity-servers" entry (a fresh one assumes default: 4 = Minecraft 1.7.2).
        foreach (var f in new[] { "velocity.toml", "forwarding.secret", Path.Combine("plugins", "viaversion", "config.yml") })
        { var p = Path.Combine(proxyDir, f); if (File.Exists(p)) File.Delete(p); }

        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json")), ("Servers:Root", _serversRoot));
        _registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        _registry.Remove(ServerRegistry.LegacyServerId);
        _proxyService = new ProxyService(_downloader, TestUtil.Log<ProxyService>());
        _runner = new Runner(_serversRoot, proxyDir);
        var software = new ServerSoftwareService(_downloader, config, TestUtil.Log<ServerSoftwareService>()) { JavaPath = await Jdk.JavaAsync(), Java21Path = await Jdk.Java21Async() };
        _provisioning = new ServerProvisioningService(_registry, software, _downloader, _proxyService, _runner, config, TestUtil.Log<ServerProvisioningService>());

        // The VM after the cutover: Paper main server behind Velocity.
        var main = await PaperServer.StartAsync("provmain", MainPort, MainRcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6));
        await main.DisposeAsync();
        // A whitelisted player, written as the server writes it ("whitelist add" by name cannot resolve on an
        // offline-mode test server).
        File.WriteAllText(Path.Combine(main.Dir, "whitelist.json"),
            """[{"uuid":"6b1f3d2a-4c5e-4f60-9a7b-8c9d0e1f2a3b","name":"WhitelistedFriend"}]""");
        var mainDef = main.ToDefinition("main");
        mainDef.ServiceName = MainService;
        _registry.Add(mainDef);
        var proxy = new ProxyDefinition
        {
            Path = proxyDir, ServiceName = ProxyServiceName, BindHost = "127.0.0.1", Port = ProxyPort,
            BedrockPort = 19250, VoicePort = 24750, RconPort = ProxyRcon, OnlineMode = false,
            RconPassword = "prov-" + Guid.NewGuid().ToString("N")[..8],
        };
        await _proxyService.InstallFilesAsync(proxy);
        await (await VelocityProcess.StartAsync(proxyDir, TimeSpan.FromMinutes(6))).DisposeAsync();
        _registry.SetProxy(proxy);
        _proxyService.ConfigureProxy(proxy, _registry.All);
        _proxyService.ConfigureBackend(proxy, mainDef);
        await _runner.StartAsync(MainService);
        await _runner.StartAsync(ProxyServiceName);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        foreach (var name in _runner.Servers.Keys.ToList()) await _runner.StopAsync(name);
        await _runner.StopAsync(ProxyServiceName);
        await _runner.StopAsync(MainService);
    }

    private static async Task<List<string>> PlayersOn(ServerDefinition s)
    {
        await using var rcon = new MinecraftRconClient("127.0.0.1", s.RconPort);
        Assert.That(await rcon.ConnectAsync(s.RconPassword), Is.True);
        return (await rcon.GetPlayersAsync()).Players;
    }

    [Test, Order(1)]
    public async Task NewFabricServer_IsReachableThroughTheProxy()
    {
        var steps = new List<string>();
        var def = await _provisioning.CreateAsync(new ServerProvisioningService.CreateServerRequest(
            "fabric-one", "Fabric One", ServerLoader.Fabric, "1.21.5", 2048, []), new Progress<string>(steps.Add));
        TestContext.Progress.WriteLine(string.Join("\n", steps));

        Assert.That(_registry.Get("fabric-one"), Is.Not.Null);
        Assert.That(File.ReadAllText(Path.Combine(_registry.Proxy!.Path, "velocity.toml")), Does.Contain($"fabric-one = \"127.0.0.1:{def.GamePort}\""));
        Assert.That(File.ReadAllText(Path.Combine(def.Path, "whitelist.json")), Does.Contain("WhitelistedFriend"), "whitelist copied from the main server");
        var mods = Directory.GetFiles(Path.Combine(def.Path, "mods")).Select(Path.GetFileName).ToList();
        foreach (var m in new[] { "fabric-api", "FabricProxy-Lite", "voicechat", "vivecraft" })
            Assert.That(mods, Has.Some.Contains(m).IgnoreCase, string.Join(", ", mods));

        await using (var bot = TestBot.Start("127.0.0.1", ProxyPort, "ProvBot"))
        {
            await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
            await bot.ChatAsync("/server fabric-one");
            await TestUtil.WaitUntilAsync(async () => (await PlayersOn(def)).Contains("ProvBot"), TimeSpan.FromSeconds(45),
                "the bot reached the new server: " + bot.Transcript);
        }
        await Task.Delay(3500); // Velocity login-ratelimit
        await using (var sneaky = TestBot.Start("127.0.0.1", def.GamePort, "Sneaky"))
        {
            var e = await sneaky.WaitForAsync(x => TestBot.Kind(x) is "spawn" or "kicked" or "end", TimeSpan.FromSeconds(30));
            Assert.That(e != null && TestBot.Kind(e.Value) != "spawn", "joined the new server without the proxy: " + sneaky.Transcript);
        }
        var output = _runner.Servers[def.ServiceName].Output;
        Assert.That(output.Any(l => l.Contains($"Voice chat server started at 127.0.0.1:{def.VoicePort}")), Is.True,
            string.Join("\n", output.Where(l => l.Contains("oice"))));

        // The FIRST start too (its log is rotated to logs/*.log.gz): voice must already use the server's own
        // port - the default 24454 is the proxy's public voice port and fails to bind on the VM.
        var firstStart = Directory.GetFiles(Path.Combine(def.Path, "logs"), "*.log.gz").Order().First();
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(firstStart), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        Assert.That(reader.ReadToEnd(), Does.Contain($"Voice chat server started at 127.0.0.1:{def.VoicePort}"), "first start: " + firstStart);
    }

    /// <summary>
    /// Forge (on Java 21 - its Mixin cannot read Java 25 classes) and NeoForge (on the VM's Java 25): created with their
    /// add-ons (Proxy-Compatible-Forge's mixins, Simple Voice Chat), started, and joined through the proxy by a real
    /// client within a minute of being created - with no class-version or mixin errors.
    /// </summary>
    [Order(5)]
    [TestCase(ServerLoader.Forge, "forge-one")]
    [TestCase(ServerLoader.NeoForge, "neoforge-one")]
    public async Task ForgeFamilyServer_Runs_AndIsReachableThroughTheProxy(ServerLoader loader, string id)
    {
        var steps = new List<string>();
        var def = await _provisioning.CreateAsync(new ServerProvisioningService.CreateServerRequest(
            id, id, loader, "1.21.5", 2048, []), new Progress<string>(steps.Add));
        TestContext.Progress.WriteLine(string.Join("\n", steps));
        var env = File.ReadAllText(Path.Combine(def.Path, "aubscraft.env"));
        Assert.That(env.Contains("JAVA="), Is.EqualTo(loader == ServerLoader.Forge), "only Forge names its own Java (21): " + env);
        try
        {
            await Task.Delay(3500); // Velocity login-ratelimit
            await using (var bot = TestBot.Start("127.0.0.1", ProxyPort, "Forge" + (loader == ServerLoader.Forge ? "F" : "N")))
            {
                await bot.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
                await bot.ChatAsync("/server " + id);
                await TestUtil.WaitUntilAsync(async () => (await PlayersOn(def)).Count > 0, TimeSpan.FromSeconds(60),
                    $"the bot reached the {loader} server: " + bot.Transcript);
            }
            var output = _runner.Servers[def.ServiceName].Output;
            var bad = output.Where(l => l.Contains("UnsupportedClassVersionError") || l.Contains("Unsupported class file major version")
                                        || l.Contains("MixinApplyError") || l.Contains("Mixin apply failed")).ToList();
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }
        finally { await _runner.StopAsync(def.ServiceName); }
    }

    [Test, Order(2)]
    public async Task TheSpookySet_LoadsTogether()
    {
        var steps = new List<string>();
        var def = await _provisioning.CreateAsync(new ServerProvisioningService.CreateServerRequest(
            "spooky", "Spooky", ServerLoader.Fabric, "1.21.5", 3072, SpookyMods), new Progress<string>(steps.Add));
        TestContext.Progress.WriteLine(string.Join("\n", steps));

        var output = _runner.Servers[def.ServiceName].Output;
        // Fabric lists every loaded mod ("\t- <mod id> <version>") at start; every requested project and its
        // dependencies must be among them. Mod ids, from that list: From The Fog = watching.
        var loaded = output.TakeWhile(l => !l.Contains("Done (")).Where(l => l.TrimStart().StartsWith("- "))
            .Select(l => l.Trim()[2..].Split(' ')[0]).ToHashSet();
        foreach (var id in new[] { "watching", "serversidehorror", "mr_sleepless_datapack", "mr_qraftys_halloweenvillages", "flowermimics",
                     "mcwholidays", "mutantmonsters", "spookydoors", "deimos", "collective", "puzzleslib", "forgeconfigapiport", "balm",
                     "fabricproxy-lite", "voicechat", "floodgate", "vivecraft" })
            Assert.That(loaded, Does.Contain(id), "not loaded: " + id);

        // No errors except these known upstream quirks of the mods themselves (verified 2026-10-03, Fabric 1.21.5):
        //  - From The Fog ships a function file whose name says "THIS IS NOT A BUG IT IS INTENTIONAL";
        //  - Sleepless' two optional compat functions (tc_freeze / tc_unfreeze, for another mod) use a gamerule
        //    1.21.5 does not have, so just those two are skipped.
        var known = new[] { "THIS IS NOT A BUG IT IS INTENTIONAL", "sleepless:util/compat/tc_freeze", "sleepless:util/compat/tc_unfreeze" };
        var errors = output.Where(l => l.Contains("/ERROR]") && !known.Any(l.Contains)).ToList();
        Assert.That(errors, Is.Empty, string.Join("\n", errors.Take(10)));
    }

    [Test, Order(3)]
    public async Task AFailedCreate_LeavesNothingBehind()
    {
        var toml = File.ReadAllText(Path.Combine(_registry.Proxy!.Path, "velocity.toml"));
        var before = _registry.All.Count;
        Assert.CatchAsync(() => _provisioning.CreateAsync(new ServerProvisioningService.CreateServerRequest(
            "broken", "Broken", ServerLoader.Fabric, "1.21.5", 1024, ["this-mod-does-not-exist-aubscraft"])));
        Assert.That(_registry.All.Count, Is.EqualTo(before));
        Assert.That(Directory.Exists(Path.Combine(_serversRoot, "broken")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(_registry.Proxy.Path, "velocity.toml")), Is.EqualTo(toml));
    }
}
