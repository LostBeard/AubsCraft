using System.Drawing;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The world lit by its own light, over the SCENE world: a closed room (inside x 47..51, z 63..67, y -60..-58) with a torch in the middle, a torch and a glowstone in the open beside it.
/// By day the room is dark inside (no sky reaches it) where the outside is bright; at night a torch lights the ground round it warm; with the lighting turned off none of it shows.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerLightTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "light";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private async Task<(Bitmap bitmap, List<string> log)> ShotAsync(string name, int ticks, string cam, bool light)
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { lock (log) log.Add(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { lock (log) log.Add(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&dd=6&time={ticks}&light={(light ? 1 : 0)}&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            // The light of the columns near the camera comes a little after the columns
            for (int i = 0; i < 30; i++)
            {
                bool seen; lock (log) seen = log.Any(l => Regex.IsMatch(l, @"light [1-9]\d* columns"));
                if (seen || !light) break;
                await Task.Delay(1000);
            }
            await Task.Delay(3500);
            var file = Path.Combine(_outDir, name + ".png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
            return (new Bitmap(file), log);
        }
        finally { await page.CloseAsync(); }
    }

    private static double MeanLuma(Bitmap b, double fromY = 0, double toY = 1)
    {
        double sum = 0; long n = 0;
        for (int y = (int)(b.Height * fromY) + 6; y < (int)(b.Height * toY) - 6; y += 3)
            for (int x = 6; x < b.Width - 6; x += 3) { var p = b.GetPixel(x, y); sum += 0.3 * p.R + 0.59 * p.G + 0.11 * p.B; n++; }
        return sum / n;
    }

    private static int Warm(Bitmap b)
    {
        int n = 0;
        for (int y = b.Height / 3; y < b.Height - 6; y += 2)
            for (int x = 6; x < b.Width - 6; x += 2) { var p = b.GetPixel(x, y); if (p.R > 70 && p.R > p.G + 12 && p.G > p.B + 12) n++; }
        return n;
    }

    [Test]
    public async Task ByDay_ADarkRoomIsDarkInside_AndATorchLightsAnotherWarm()
    {
        // The dark room (no torch, inside x 63..67, z 63..67): from its corner across it, by noon
        var (lit, litLog) = await ShotAsync("dark-room-light", 6000, "63.6,-58.9,63.7,5,45", light: true);
        var (plain, _) = await ShotAsync("dark-room-plain", 6000, "63.6,-58.9,63.7,5,45", light: false);
        // The room with the torch in it
        var (torch, _) = await ShotAsync("torch-room-light", 6000, "47.6,-58.9,63.7,5,45", light: true);
        using (lit) using (plain) using (torch)
        {
            double a = MeanLuma(lit, 0.3, 1), b = MeanLuma(plain, 0.3, 1), c = MeanLuma(torch, 0.3, 1);
            TestContext.Progress.WriteLine($"[light] noon: dark room lit {a:F1}, plain {b:F1}; the torch room {c:F1}");
            Assert.That(a, Is.LessThan(b * 0.4), "with the light on, the room no sky reaches is much darker than the plain picture of it");
            Assert.That(c, Is.GreaterThan(a * 2), "and the room with a torch in it is far brighter than the dark one");
            Assert.That(litLog.Any(l => Regex.IsMatch(l, @"light [1-9]\d* columns")), Is.True, "the worker has the light of columns:\n" + string.Join("\n", litLog.Where(l => l.Contains("[Light]") || l.Contains("[Voxel]")).TakeLast(3)));
        }
    }

    [Test]
    public async Task AtNight_ATorchLightsItsSurroundingsWarm_OnlyWithLightOn()
    {
        // The torch in the open at (56, -60, 65), seen from the south side, by midnight
        var (lit, _) = await ShotAsync("torch-night-light", 18000, "56.5,-58.6,70.5,15,-90", light: true);
        var (plain, _) = await ShotAsync("torch-night-plain", 18000, "56.5,-58.6,70.5,15,-90", light: false);
        using (lit) using (plain)
        {
            int a = Warm(lit), b = Warm(plain);
            TestContext.Progress.WriteLine($"[light] midnight warm pixels near the torch: lit {a}, plain {b}");
            Assert.That(a, Is.GreaterThan(b + 150), "the torch's light colours the ground and walls round it");
        }
    }
}
