using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// ServerSettingsService: the files it writes (server.properties, aubscraft.env, the registry), what it refuses, and on
/// a REAL Paper server which settings apply at once (difficulty, over RCON) and which on the next start (max players).
/// </summary>
[NonParallelizable]
public class ServerSettingsTests
{
    private const int Port = 25830, Rcon = 25831;

    private static (ServerSettingsService svc, ServerRegistry registry) Create(string dir)
    {
        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json")));
        var registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        registry.Remove(ServerRegistry.LegacyServerId);
        return (new ServerSettingsService(registry, new ProxyService(new AddonDownloader(), TestUtil.Log<ProxyService>()), TestUtil.Log<ServerSettingsService>()), registry);
    }

    [Test]
    public void Memory_RewritesOnlyTheHeapInAubscraftEnv()
    {
        var env = Path.Combine(TestUtil.NewTempDir(), "aubscraft.env");
        File.WriteAllText(env, "JAVA_OPTS=-Xms1024M -Xmx3072M -XX:+UseG1GC -XX:MaxGCPauseMillis=200\nLAUNCH=-jar fabric-server-launch.jar\n");
        ServerSettingsService.WriteHeap(env, 4096);
        Assert.That(File.ReadAllText(env), Is.EqualTo("JAVA_OPTS=-Xms1024M -Xmx4096M -XX:+UseG1GC -XX:MaxGCPauseMillis=200\nLAUNCH=-jar fabric-server-launch.jar\n"));
        ServerSettingsService.WriteHeap(env, 768);
        Assert.That(File.ReadAllLines(env)[0], Does.StartWith("JAVA_OPTS=-Xms768M -Xmx768M "), "-Xms never above -Xmx");
    }

    [Test]
    public async Task Save_RefusesBadValues_AndMemoryOfAServerWithoutAubscraftEnv()
    {
        var dir = TestUtil.NewTempDir();
        var (svc, registry) = Create(dir);
        var path = Path.Combine(dir, "s");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "server.properties"), "difficulty=easy\nmax-players=10\n");
        registry.Add(new ServerDefinition { Id = "s", Name = "S", Path = path, MemoryMb = 2048, RconHost = "127.0.0.1", RconPort = 1, RconPassword = "x" });
        var ok = svc.Read("s");
        Assert.That(ok.MemoryEditable, Is.False, "no aubscraft.env");

        Assert.ThrowsAsync<ArgumentException>(() => svc.SaveAsync("s", ok with { MaxPlayers = 0 }));
        Assert.ThrowsAsync<ArgumentException>(() => svc.SaveAsync("s", ok with { Difficulty = "nightmare" }));
        Assert.ThrowsAsync<ArgumentException>(() => svc.SaveAsync("s", ok with { ViewDistance = 99 }));
        Assert.ThrowsAsync<ArgumentException>(() => svc.SaveAsync("s", ok with { MemoryMb = 4096 }));
        Assert.That(ConfigFiles.GetProperty(Path.Combine(path, "server.properties"), "max-players"), Is.EqualTo("10"), "nothing written");

        var msg = await svc.SaveAsync("s", ok with { Name = "Renamed", Pvp = false, AllowFlight = true });
        Assert.That(registry.Get("s")!.Name, Is.EqualTo("Renamed"));
        Assert.That(ConfigFiles.GetProperty(Path.Combine(path, "server.properties"), "pvp"), Is.EqualTo("false"));
        Assert.That(ConfigFiles.GetProperty(Path.Combine(path, "server.properties"), "allow-flight"), Is.EqualTo("true"));
        Assert.That(msg, Does.Contain("Restart Renamed to apply").And.Contain("PvP").And.Contain("allow flight"));
    }

    [Test]
    public async Task OnARealServer_DifficultyAppliesNow_MaxPlayersOnRestart()
    {
        var dir = TestUtil.NewTempDir();
        var (svc, registry) = Create(dir);
        var paper = await PaperServer.StartAsync("settings", Port, Rcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6));
        try
        {
            var def = paper.ToDefinition("settings");
            registry.Add(def);
            var before = svc.Read("settings");
            var msg = await svc.SaveAsync("settings", before with { Difficulty = "hard", MaxPlayers = 7 });
            Assert.That(msg, Does.Contain("Applied now: difficulty").And.Contain("max players"));

            await using (var rcon = new MinecraftRconClient("127.0.0.1", Rcon))
            {
                Assert.That(await rcon.ConnectAsync(def.RconPassword), Is.True);
                Assert.That(await rcon.SendCommandAsync("difficulty"), Does.Contain("Hard"), "difficulty applied without a restart");
                Assert.That((await rcon.GetPlayersAsync()).Max, Is.EqualTo(10), "max players waits for the restart");
            }
            await paper.DisposeAsync();
            paper = await PaperServer.StartAsync("settings", Port, Rcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6), fresh: false);
            await using (var rcon = new MinecraftRconClient("127.0.0.1", Rcon))
            {
                Assert.That(await rcon.ConnectAsync(def.RconPassword), Is.True);
                Assert.That((await rcon.GetPlayersAsync()).Max, Is.EqualTo(7), "max players after the restart");
                Assert.That(await rcon.SendCommandAsync("difficulty"), Does.Contain("Hard"), "and difficulty stays (it was saved too)");
            }
        }
        finally { await paper.DisposeAsync(); }
    }
}
