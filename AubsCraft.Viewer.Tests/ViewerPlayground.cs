namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Not a gate: serves the build under test (and the fake plugin the editor talks to) to a headset on USB. Run it, then on the PC:
///   adb reverse tcp:PORT tcp:PORT, and open http://localhost:PORT/map in the headset's browser (the port and the owner's login are in the info file).
/// It runs until the stop file appears. Everything is read from the environment like the gates:
///   AUBS_VIEWER_PUBLISH (required), AUBS_VIEWER_WORLD (default the scene world), AUBS_QUEST_DIR (default D:/users/tj/Projects/_scratch/quest).
/// </summary>
[Explicit("serves a build to a headset until quest/stop.txt appears")]
[NonParallelizable]
public class ViewerPlayground
{
    [Test]
    public async Task ServeToTheQuest()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var dir = Environment.GetEnvironmentVariable("AUBS_QUEST_DIR") ?? "D:/users/tj/Projects/_scratch/quest";
        Directory.CreateDirectory(dir);
        var stop = Path.Combine(dir, "stop.txt");
        File.Delete(stop);
        await using var rig = await ViewerRig.StartAsync(publish!, world);
        var plugin = new FakePlugin(rig);
        plugin.Start();
        try
        {
            await plugin.PostWorldAsync();
            var port = new Uri(rig.BaseUrl).Port;
            File.WriteAllText(Path.Combine(dir, "serving.txt"), $"{port}\n{ViewerRig.OwnerName}\n{ViewerRig.OwnerPassword}\n");
            TestContext.Progress.WriteLine($"[playground] serving on port {port}");
            for (int i = 0; i < 3 * 3600 && !File.Exists(stop); i++) await Task.Delay(1000);
        }
        finally
        {
            await plugin.StopAsync();
            try { File.Delete(Path.Combine(dir, "serving.txt")); } catch { }
        }
    }
}
