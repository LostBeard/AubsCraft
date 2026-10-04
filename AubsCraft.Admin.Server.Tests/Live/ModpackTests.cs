using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The PC mod pack (ModpackService) for the Spooky preset's client mods, built from REAL Modrinth data, then checked the
/// way the Modrinth App installs it: every listed file is downloaded from its listed URL and must match the listed sha1,
/// sha512 and size; the Fabric loader it asks for must exist.
/// </summary>
public class ModpackTests
{
    private static readonly string[] SpookyClientMods = ["macaws-holidays", "mutant-monsters", "spooky-doors", "from-the-fog", "tense-ambience"];

    [Test]
    public async Task SpookyPack_ListsEveryModWithHashesThatMatchTheRealFiles()
    {
        // The latest stable Fabric loader for 1.21.5 (what the servers install), in a server folder's libraries.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AubsCraft-Tests/1.0");
        using var loaders = JsonDocument.Parse(await http.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader/1.21.5"));
        var loader = loaders.RootElement.EnumerateArray().First(l => l.GetProperty("loader").GetProperty("stable").GetBoolean())
            .GetProperty("loader").GetProperty("version").GetString()!;
        var dir = TestUtil.NewTempDir();
        Directory.CreateDirectory(Path.Combine(dir, "spooky", "libraries", "net", "fabricmc", "fabric-loader", loader));

        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json")));
        var registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        registry.Remove(ServerRegistry.LegacyServerId);
        registry.Add(new ServerDefinition { Id = "plain", Name = "Plain", Loader = ServerLoader.Paper, GameVersion = "1.21.5", Path = Path.Combine(dir, "plain") });
        registry.Add(new ServerDefinition
        {
            Id = "spooky", Name = "Spooky", Loader = ServerLoader.Fabric, GameVersion = "1.21.5", Path = Path.Combine(dir, "spooky"),
            ClientMods = [.. SpookyClientMods],
        });
        var packs = new ModpackService(registry, new AddonDownloader(), TestUtil.Log<ModpackService>());

        var available = packs.Available();
        Assert.That(available, Has.Count.EqualTo(1), "one pack: only Spooky needs client mods");
        Assert.That(available[0].Servers, Is.EqualTo(new[] { "Spooky" }));

        var bytes = await packs.GetPackAsync("1.21.5");
        Assert.That(bytes, Is.Not.Null);
        using var zip = new ZipArchive(new MemoryStream(bytes!));
        using var index = JsonDocument.Parse(zip.GetEntry("modrinth.index.json")!.Open());
        var root = index.RootElement;
        Assert.That(root.GetProperty("formatVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("game").GetString(), Is.EqualTo("minecraft"));
        Assert.That(root.GetProperty("dependencies").GetProperty("minecraft").GetString(), Is.EqualTo("1.21.5"));
        Assert.That(root.GetProperty("dependencies").GetProperty("fabric-loader").GetString(), Is.EqualTo(loader));

        var files = root.GetProperty("files").EnumerateArray().ToList();
        var names = files.Select(f => f.GetProperty("path").GetString()!).ToList();
        TestContext.Progress.WriteLine(string.Join("\n", names));
        foreach (var expected in new[] { "fabric-api", "voicechat", "spooky", "mutant", "holidays", "puzzles", "balm" })
            Assert.That(names, Has.Some.Contains(expected).IgnoreCase, $"the pack has {expected}");

        foreach (var f in files)
        {
            var path = f.GetProperty("path").GetString()!;
            Assert.That(path, Does.StartWith("mods/").And.EndWith(".jar"));
            var url = f.GetProperty("downloads")[0].GetString()!;
            Assert.That(new Uri(url).Host, Is.EqualTo("cdn.modrinth.com"), "the Modrinth App only downloads from allowed hosts");
            var data = await http.GetByteArrayAsync(url);
            Assert.That(data.LongLength, Is.EqualTo(f.GetProperty("fileSize").GetInt64()), path + " size");
            Assert.That(Convert.ToHexStringLower(SHA1.HashData(data)), Is.EqualTo(f.GetProperty("hashes").GetProperty("sha1").GetString()), path + " sha1");
            Assert.That(Convert.ToHexStringLower(SHA512.HashData(data)), Is.EqualTo(f.GetProperty("hashes").GetProperty("sha512").GetString()), path + " sha512");
        }
    }
}
