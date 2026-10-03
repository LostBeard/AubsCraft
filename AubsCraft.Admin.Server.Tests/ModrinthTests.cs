using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// Add-on search follows each server's loader and version. Runs against the REAL Modrinth API.
/// Lithium is a Fabric/NeoForge server-side mod; Simple Voice Chat ships both a Paper plugin and mods.
/// </summary>
public class ModrinthTests
{
    private static ServerDefinition Def(ServerLoader loader) =>
        new() { Id = "x", Name = "x", Loader = loader, GameVersion = "1.21.5", Path = "/x" };

    private static readonly ModrinthService Modrinth = new(TestUtil.Log<ModrinthService>());

    [Test]
    public async Task FabricServer_FindsFabricMods_PaperServerDoesNot()
    {
        var fabric = await Modrinth.SearchAsync(Def(ServerLoader.Fabric), "lithium");
        var paper = await Modrinth.SearchAsync(Def(ServerLoader.Paper), "lithium");
        Assert.That(fabric.Select(r => r.Slug), Does.Contain("lithium"));
        Assert.That(paper.Select(r => r.Slug), Does.Not.Contain("lithium"));
    }

    [Test]
    public async Task PaperServer_StillFindsPlugins()
    {
        var paper = await Modrinth.SearchAsync(Def(ServerLoader.Paper), "simple voice chat");
        Assert.That(paper.Select(r => r.Slug), Does.Contain("simple-voice-chat"));
    }

    [Test]
    public async Task Versions_AreFilteredToTheServersLoader()
    {
        // Simple Voice Chat publishes separate builds per loader; each server must only see its own.
        var fabric = await Modrinth.GetVersionsAsync(Def(ServerLoader.Fabric), "simple-voice-chat");
        var paper = await Modrinth.GetVersionsAsync(Def(ServerLoader.Paper), "simple-voice-chat");
        Assert.That(fabric, Is.Not.Empty);
        Assert.That(paper, Is.Not.Empty);
        Assert.That(fabric.SelectMany(v => v.Files).Select(f => f.Filename), Has.All.Contains("fabric"));
        Assert.That(paper.SelectMany(v => v.Files).Select(f => f.Filename), Has.All.Contains("bukkit"));
    }

    [Test]
    public async Task VanillaServer_HasNoAddons()
    {
        Assert.That(await Modrinth.SearchAsync(Def(ServerLoader.Vanilla), "lithium"), Is.Empty);
        Assert.That(ModrinthService.LoadersFor(ServerLoader.Vanilla), Is.Empty);
    }
}
