using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The /quest manifest (QuestAssetService, REAL Modrinth and GitHub): it follows the servers' client-mod lists at once.
/// It used to be cached for 30 minutes regardless, so a mod added on the Mods page was missing from the Quest setup
/// for up to half an hour.
/// </summary>
public class QuestManifestTests
{
    private sealed class Http : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
    }

    [Test]
    public async Task AClientModAddedToAServer_IsInTheManifestAtOnce()
    {
        var dir = TestUtil.NewTempDir();
        var registry = new ServerRegistry(TestUtil.Config(("Servers:RegistryPath", Path.Combine(dir, "servers.json"))), TestUtil.Log<ServerRegistry>());
        registry.Remove(ServerRegistry.LegacyServerId);
        var def = new ServerDefinition { Id = "s", Name = "S", Loader = ServerLoader.Fabric, GameVersion = "1.21.5", Path = Path.Combine(dir, "s"), ClientMods = ["spooky-doors"] };
        registry.Add(def);
        var quest = new QuestAssetService(new Http(), new Env(dir), registry, new AddonDownloader(), TestUtil.Log<QuestAssetService>());

        var first = await quest.GetManifestAsync(CancellationToken.None);
        Assert.That(first.Mods.Select(m => m.Name), Does.Contain("spooky-doors").And.Not.Contain("mutant-monsters"));

        def.ClientMods.Add("mutant-monsters");
        registry.Update(def);
        var second = await quest.GetManifestAsync(CancellationToken.None);
        Assert.That(second.Mods.Select(m => m.Name), Does.Contain("mutant-monsters"), "added on the server, offered to headsets now");

        Assert.That(await quest.GetManifestAsync(CancellationToken.None), Is.SameAs(second), "unchanged lists still use the cache");
    }
}
