using System.Text.Json;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The server registry (servers.json): seeding from the single-server config, persistence, validation.</summary>
public class ServerRegistryTests
{
    private static ServerRegistry Open(string path, params (string, string?)[] extra) =>
        new(TestUtil.Config([("Servers:RegistryPath", path), .. extra]), TestUtil.Log<ServerRegistry>());

    [Test]
    public void FirstRun_SeedsOneServerFromTheSingleServerConfig_AndPersistsIt()
    {
        var path = Path.Combine(TestUtil.NewTempDir(), "servers.json");
        // The exact keys and shape of the production appsettings.json on the VM.
        var registry = Open(path,
            ("Minecraft:ServerPath", "/opt/minecraft/server"),
            ("Minecraft:GameVersion", "1.21.5"),
            ("Minecraft:ServiceName", "minecraft"),
            ("Rcon:Host", "127.0.0.1"),
            ("Rcon:Port", "25575"),
            ("Rcon:Password", "secret"));

        var s = registry.All.Single();
        Assert.Multiple(() =>
        {
            Assert.That(s.Id, Is.EqualTo(ServerRegistry.LegacyServerId));
            Assert.That(s.Name, Is.EqualTo("AubsCraft"));
            Assert.That(s.Loader, Is.EqualTo(ServerLoader.Paper));
            Assert.That(s.Path, Is.EqualTo("/opt/minecraft/server"));
            Assert.That(s.ServiceName, Is.EqualTo("minecraft"));
            Assert.That(s.RconPort, Is.EqualTo(25575));
            Assert.That(s.RconPassword, Is.EqualTo("secret"));
            Assert.That(registry.Primary?.Id, Is.EqualTo("aubscraft"));
            Assert.That(File.Exists(path), "servers.json written on first run");
            Assert.That(File.Exists(path + ".tmp"), Is.False, "no temp file left behind");
        });

        // The file now wins over the legacy config: a second open does not re-seed.
        var reopened = Open(path, ("Minecraft:ServerPath", "/somewhere/else"));
        Assert.That(reopened.All.Single().Path, Is.EqualTo("/opt/minecraft/server"));
        // Loader serializes by name (hand-editable).
        Assert.That(File.ReadAllText(path), Does.Contain("\"Loader\": \"Paper\""));
    }

    [Test]
    public void AddUpdateRemove_PersistAndRaiseChanged()
    {
        var path = Path.Combine(TestUtil.NewTempDir(), "servers.json");
        var registry = Open(path);
        int changes = 0;
        registry.Changed += () => changes++;

        registry.Add(new ServerDefinition { Id = "creative", Name = "Creative", Loader = ServerLoader.Fabric, Path = "/opt/minecraft/servers/creative" });
        var updated = registry.Get("creative")!;
        registry.Update(new ServerDefinition { Id = "creative", Name = "Creative World", Loader = ServerLoader.Fabric, Path = updated.Path });
        Assert.That(Open(path).Get("creative")?.Name, Is.EqualTo("Creative World"));
        Assert.That(Open(path).All.Select(s => s.Id), Is.EqualTo(new[] { "aubscraft", "creative" }), "order kept: primary first");

        Assert.That(registry.Remove("creative"), Is.True);
        Assert.That(registry.Remove("creative"), Is.False);
        Assert.That(Open(path).All.Select(s => s.Id), Is.EqualTo(new[] { "aubscraft" }));
        Assert.That(changes, Is.EqualTo(3));
    }

    [TestCase("Creative")]       // uppercase
    [TestCase("-creative")]      // leading hyphen
    [TestCase("creative-")]      // trailing hyphen
    [TestCase("my server")]      // space
    [TestCase("../etc")]         // path
    [TestCase("")]
    [TestCase("a23456789012345678901234567890123")] // 33 chars
    public void InvalidIds_AreRejected(string id)
    {
        var registry = Open(Path.Combine(TestUtil.NewTempDir(), "servers.json"));
        Assert.Throws<ArgumentException>(() => registry.Add(new ServerDefinition { Id = id, Name = "x", Path = "/x" }));
    }

    [TestCase("creative")]
    [TestCase("a")]
    [TestCase("survival-2")]
    [TestCase("a2345678901234567890123456789012")] // 32 chars
    public void ValidIds_AreAccepted(string id) => Assert.That(ServerRegistry.IsValidId(id), Is.True);

    [Test]
    public void DuplicateId_IsRejected()
    {
        var registry = Open(Path.Combine(TestUtil.NewTempDir(), "servers.json"));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Add(new ServerDefinition { Id = "aubscraft", Name = "again", Path = "/x" }));
    }

    [Test]
    public void ARegistryFileWithAnInvalidId_FailsLoudly()
    {
        var path = Path.Combine(TestUtil.NewTempDir(), "servers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServerRegistryFile
        {
            Servers = [new ServerDefinition { Id = "Bad Id", Name = "x", Path = "/x" }],
        }));
        Assert.Throws<ArgumentException>(() => Open(path));
    }

    [Test]
    public void WorldPath_FollowsServerPropertiesLevelName()
    {
        var dir = TestUtil.NewTempDir();
        var def = new ServerDefinition { Id = "x", Name = "x", Path = dir };
        Assert.That(def.WorldPath, Is.EqualTo(Path.Combine(dir, "world")), "no server.properties: default 'world'");

        File.WriteAllText(Path.Combine(dir, "server.properties"), "motd=hi\nlevel-name=my_world\n");
        Assert.That(def.WorldPath, Is.EqualTo(Path.Combine(dir, "my_world")));
    }

    [Test]
    public void AddonsPath_DependsOnTheLoader()
    {
        string P(ServerLoader l) => new ServerDefinition { Path = "/srv/x", Loader = l }.AddonsPath ?? "(none)";
        Assert.Multiple(() =>
        {
            Assert.That(P(ServerLoader.Paper), Is.EqualTo(Path.Combine("/srv/x", "plugins")));
            Assert.That(P(ServerLoader.Fabric), Is.EqualTo(Path.Combine("/srv/x", "mods")));
            Assert.That(P(ServerLoader.Forge), Is.EqualTo(Path.Combine("/srv/x", "mods")));
            Assert.That(P(ServerLoader.NeoForge), Is.EqualTo(Path.Combine("/srv/x", "mods")));
            Assert.That(P(ServerLoader.Vanilla), Is.EqualTo("(none)"));
        });
    }
}
