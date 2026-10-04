using System.Security.Cryptography;
using System.Text.Json;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// AddonManagerService against REAL Modrinth on a Fabric 1.21.5 server folder: install with required libraries and the
/// client-mod list, identification by hash, refusing to remove what is needed, removing, and updating an old build.
/// </summary>
[NonParallelizable]
public class AddonManagerTests
{
    private string _dir = "";
    private ServerRegistry _registry = null!;
    private AddonManagerService _addons = null!;
    private readonly AddonDownloader _downloader = new();
    private string Mods => Path.Combine(_dir, "srv", "mods");

    [SetUp]
    public void SetUp()
    {
        _dir = TestUtil.NewTempDir();
        Directory.CreateDirectory(Mods);
        var config = TestUtil.Config(("Servers:RegistryPath", Path.Combine(_dir, "servers.json")));
        _registry = new ServerRegistry(config, TestUtil.Log<ServerRegistry>());
        _registry.Remove(ServerRegistry.LegacyServerId);
        _registry.Add(new ServerDefinition { Id = "fab", Name = "Fab", Loader = ServerLoader.Fabric, GameVersion = "1.21.5", Path = Path.Combine(_dir, "srv") });
        _addons = new AddonManagerService(_registry, _downloader, new ProxyService(_downloader, TestUtil.Log<ProxyService>()), TestUtil.Log<AddonManagerService>());
    }

    [Test]
    public async Task Install_BringsItsLibraries_AndAddsAClientModToTheList_ThenRemoveRespectsWhoNeedsWhat()
    {
        var msg = await _addons.InstallAsync("fab", "spooky-doors");
        TestContext.Progress.WriteLine(msg);
        Assert.That(msg, Does.Contain("Installed").And.Contain("balm"));
        Assert.That(_registry.Get("fab")!.ClientMods, Does.Contain("spooky-doors"), "players need Spooky Doors too");

        var list = await _addons.ListAsync("fab");
        var doors = list.Single(a => a.Slug == "spooky-doors");
        var balm = list.Single(a => a.Slug == "balm");
        Assert.That(doors.ClientNeeded, Is.True);
        Assert.That(balm.RequiredBy, Does.Contain(doors.Name), "Balm is needed by Spooky Doors");
        foreach (var a in list)
            Assert.That(a.ProjectId, Is.Not.Null, a.FileName + " identified on Modrinth by its hash");

        var refused = Assert.ThrowsAsync<InvalidOperationException>(() => _addons.RemoveAsync("fab", balm.FileName));
        Assert.That(refused!.Message, Does.Contain("needed by"));
        Assert.That(File.Exists(Path.Combine(Mods, balm.FileName)), Is.True);

        var removed = await _addons.RemoveAsync("fab", doors.FileName);
        Assert.That(removed, Does.Contain("Removed"));
        Assert.That(File.Exists(Path.Combine(Mods, doors.FileName)), Is.False);
        Assert.That(_registry.Get("fab")!.ClientMods, Does.Not.Contain("spooky-doors"), "no longer needed on players' games");
        Assert.That((await _addons.ListAsync("fab")).Single(a => a.Slug == "balm").RequiredBy, Is.Empty, "now Balm can go");
    }

    [Test]
    public async Task TheProxyForwardingModCannotBeRemoved()
    {
        await _addons.InstallAsync("fab", "fabricproxy-lite");
        var proxyMod = (await _addons.ListAsync("fab")).Single(a => a.Slug == "fabricproxy-lite");
        Assert.That(proxyMod.Protected, Is.True);
        Assert.ThrowsAsync<InvalidOperationException>(() => _addons.RemoveAsync("fab", proxyMod.FileName));
        Assert.That(File.Exists(Path.Combine(Mods, proxyMod.FileName)), Is.True);
    }

    [Test]
    public async Task AnOldBuild_IsOfferedAnUpdate_AndUpdatedToTheNewest()
    {
        // An older Simple Voice Chat build for Fabric 1.21.5, put in place as if installed long ago.
        using var versions = JsonDocument.Parse(await _downloader.ModrinthGetAsync(
            "https://api.modrinth.com/v2/project/simple-voice-chat/version?loaders=%5B%22fabric%22%5D&game_versions=%5B%221.21.5%22%5D"));
        var all = versions.RootElement.EnumerateArray().ToList();
        Assert.That(all.Count, Is.GreaterThan(1), "needs at least two builds to test an update");
        var old = AddonDownloader.FromModrinthVersion(all[^1], "simple-voice-chat");
        await _downloader.DownloadAsync(old, Path.Combine(Mods, old.FileName));

        var listed = (await _addons.ListAsync("fab", checkUpdates: true)).Single(a => a.Slug == "simple-voice-chat");
        Assert.That(listed.Version, Is.EqualTo(old.Version));
        Assert.That(listed.UpdateVersion, Is.Not.Null.And.Not.EqualTo(old.Version), "an update is offered");

        var msg = await _addons.UpdateAsync("fab", old.FileName);
        Assert.That(msg, Does.Contain("Updated"));
        Assert.That(File.Exists(Path.Combine(Mods, old.FileName)), Is.False, "the old build is gone");
        var now = (await _addons.ListAsync("fab", checkUpdates: true)).Single(a => a.Slug == "simple-voice-chat");
        Assert.That(now.Version, Is.EqualTo(listed.UpdateVersion));
        Assert.That(now.UpdateVersion, Is.Null, "up to date now");
        Assert.That(Directory.GetFiles(Mods, "*voicechat*").Length, Is.EqualTo(1), "one copy, never two (two copies crash Fabric)");
    }
}
