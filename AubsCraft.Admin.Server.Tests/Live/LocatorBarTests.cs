using AubsCraft.Admin.Server.Services;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// "Find each other" on AubsCraft (a Paper server): the Locator Bar plugin (Modrinth player-locator-bar, Paper
/// 1.16.5-1.21.5) shows other players on the action bar - plain Minecraft, so Java, QuestCraft and Bedrock (Geyser)
/// all see it with nothing installed. Two real clients 40 blocks apart: one must see the other on its bar.
/// </summary>
[NonParallelizable]
public class LocatorBarTests
{
    private const int Port = 25840, Rcon = 25841;

    [Test]
    public async Task APlayerSeesAnotherOnTheActionBar()
    {
        var downloader = new AddonDownloader();
        var plugin = await downloader.ModrinthAsync("player-locator-bar", "paper", null);
        TestContext.Progress.WriteLine($"Locator Bar {plugin.Version}: {plugin.FileName}");
        var paper = await PaperServer.StartAsync("locator", Port, Rcon, 10, "minecraft:normal", "world", TimeSpan.FromMinutes(6),
            beforeStart: d => downloader.DownloadAsync(plugin, Path.Combine(d, "plugins", plugin.FileName)));
        try
        {
            var def = paper.ToDefinition("locator");
            async Task<string> Cmd(string c)
            {
                await using var rcon = new MinecraftRconClient("127.0.0.1", Rcon);
                Assert.That(await rcon.ConnectAsync(def.RconPassword), Is.True);
                return await rcon.SendCommandAsync(c);
            }

            await using var seeker = TestBot.Start("127.0.0.1", Port, "Seeker");
            await seeker.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
            await using var hider = TestBot.Start("127.0.0.1", Port, "Hider");
            await hider.WaitForSpawnAsync(TimeSpan.FromSeconds(60));
            await Cmd("gamemode creative Seeker");
            await Cmd("gamemode creative Hider");
            // Control: the client reports a plain action-bar message (so a silence below is the plugin's, not the bot's).
            await Cmd("title Seeker actionbar {\"text\":\"control\"}");
            Assert.That(await seeker.WaitForAsync(e => TestBot.Kind(e) == "actionbar" && e.GetProperty("text").GetString()!.Contains("control"),
                TimeSpan.FromSeconds(10)), Is.Not.Null, "the bot sees action-bar messages: " + seeker.Transcript);

            // Like the 1.21.6 bar it copies, it shows players within a 120-degree field of view. The test client keeps
            // facing south (yaw 0; it overrides a teleport's facing), so the hider stands 40 blocks south.
            await Cmd("tp Seeker 0 120 0");
            await Cmd("tp Hider 0 120 40");

            var bar = await seeker.WaitForAsync(e => TestBot.Kind(e) == "actionbar" && e.GetProperty("text").GetString()!.Contains('⬤'), TimeSpan.FromSeconds(20));
            Assert.That(bar, Is.Not.Null, "the seeker's action bar shows the hider: " + seeker.Transcript);
            TestContext.Progress.WriteLine("bar: " + bar!.Value.GetProperty("text").GetString());
        }
        finally { await paper.DisposeAsync(); }
    }
}
