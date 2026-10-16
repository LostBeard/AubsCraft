using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The sky in the real viewer, over the SCENE world (void at the camera, so the whole frame above the horizon is sky): the gradient from the horizon's colour to the zenith's, the sun
/// and the moon, stars, clouds. Checked by what the worker reports (the sky images loaded) and by the picture, at noon, dusk and midnight. The hour is fixed by `time=`.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerSkyLookTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "skylook";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private async Task<(Bitmap Bitmap, List<string> Log)> ShotAsync(string name, int ticks, string cam)
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&dd=6&time=" + ticks.ToString(CultureInfo.InvariantCulture) + "&cam=" + cam, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            for (int i = 0; i < 30 && !log.Any(l => l.Contains("[Sky] Sky assets:")); i++) await Task.Delay(1000);
            await Task.Delay(3000);
            var file = Path.Combine(_outDir, name + ".png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
            Assert.That(log.Any(l => Regex.IsMatch(l, @"\[Sky\] Sky assets: sun 32x32, moon 32x32, clouds 256x256")), Is.True, "the sky images loaded:\n" + string.Join("\n", log.Where(l => l.Contains("Sky"))));
            return (new Bitmap(file), log);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static Color At(Bitmap b, double fx, double fy) => b.GetPixel((int)(b.Width * fx), (int)(b.Height * fy));

    [Test]
    public async Task AtNoon_TheSkyIsAGradient_BlueOverhead_PaleAtTheHorizon()
    {
        // Looking up 10 degrees: the top of the frame is well up the sky, the bottom is at and below the horizon (the fog colour)
        var (bmp, _) = await ShotAsync("noon", 6000, "0,120,0,10,0");
        using var _ = bmp;
        // The clouds are near white; the sky between them is what has the gradient
        Color Sky(double from, double to)
        {
            long r = 0, g = 0, bl = 0, n = 0;
            for (int y = (int)(bmp.Height * from); y < (int)(bmp.Height * to); y += 2)
                for (int x = 6; x < bmp.Width - 6; x += 2)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.R > 200) continue;
                    r += p.R; g += p.G; bl += p.B; n++;
                }
            Assert.That(n, Is.GreaterThan(500), "enough cloud-free sky to measure");
            return Color.FromArgb((int)(r / n), (int)(g / n), (int)(bl / n));
        }
        var top = Sky(0.02, 0.12);
        var low = Sky(0.8, 0.97);
        TestContext.Progress.WriteLine($"[skylook] noon top {top} bottom {low}");
        Assert.That(top.B, Is.GreaterThan(top.R + 60), "blue overhead");
        Assert.That(low.R, Is.GreaterThan(top.R + 10), "paler (more red) toward the horizon");
    }

    [Test]
    public async Task AtMidnight_ItIsDark_WithStars()
    {
        var (bmp, _) = await ShotAsync("midnight", 18000, "0,120,0,40,0");
        using var _ = bmp;
        int bright = 0, dark = 0;
        for (int y = 6; y < bmp.Height - 6; y += 2)
            for (int x = 6; x < bmp.Width - 6; x += 2)
            {
                var p = bmp.GetPixel(x, y);
                int max = Math.Max(p.R, Math.Max(p.G, p.B));
                if (max > 120) bright++; else if (max < 40) dark++;
            }
        TestContext.Progress.WriteLine($"[skylook] midnight bright {bright} dark {dark}");
        Assert.That(dark, Is.GreaterThan(bright * 20), "mostly dark");
        Assert.That(bright, Is.GreaterThan(5), "stars (and the moon) show");
    }

    [Test]
    public async Task TheSun_IsSeenWhereItIs_AtDusk()
    {
        // At time 12000 the sun is low in the west (-z side per the game's sun arc); look along the horizon both ways: one view has a bright disc, the other does not
        double bestBright = 0;
        foreach (var yaw in new[] { 0, 90, 180, 270 })
        {
            var (bmp, _) = await ShotAsync($"dusk-{yaw}", 12000, $"0,120,0,2,{yaw}");
            using (bmp)
            {
                int white = 0;
                for (int y = 6; y < bmp.Height - 6; y += 2)
                    for (int x = 6; x < bmp.Width - 6; x += 2)
                    {
                        var p = bmp.GetPixel(x, y);
                        if (p.R > 245 && p.G > 235 && p.B > 200) white++;
                    }
                TestContext.Progress.WriteLine($"[skylook] dusk yaw {yaw}: near-white samples {white}");
                bestBright = Math.Max(bestBright, white);
            }
        }
        Assert.That(bestBright, Is.GreaterThan(30), "the sun's square is in view from one of the four sides");
    }
}
