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

    // TJ 2026-11-04: "the moon has a large black border around it". The game's moon texture is opaque with a black background: the game adds it to the sky, so black is nothing. Mixed over the sky, it was a black square.
    [Test]
    public async Task TheMoon_HasNoBlackSquareAroundIt()
    {
        // Straight up at midnight: the moon is overhead, opposite the sun. No clouds to tell apart (the scene's clear sky at the height the rig stands)
        var (bmp, _) = await ShotAsync("moon", 18000, "0,120,0,89,0");
        int w = bmp.Width, h = bmp.Height;
        double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        // The moon: the 32 px square with the most bright pixels (the stars are single pixels)
        int bestX = 0, bestY = 0, most = 0;
        for (int y = 0; y + 32 < h; y += 4)
            for (int x = 0; x + 32 < w; x += 4)
            {
                int n = 0;
                for (int py = y; py < y + 32; py += 2)
                    for (int px = x; px < x + 32; px += 2)
                        if (Luma(bmp.GetPixel(px, py)) > 100) n++;
                if (n > most) { most = n; bestX = x; bestY = y; }
            }
        Assert.That(most, Is.GreaterThan(40), "the moon is in the view");
        int cx = bestX + 16, cy = bestY + 16;
        // The night sky is deep blue: its blue channel says the most. The middle value of a small patch, so a star in it does not count
        double Blue(int x, int y)
        {
            var values = new List<int>();
            for (int py = y - 6; py <= y + 6; py++)
                for (int px = x - 6; px <= x + 6; px++)
                    if (px >= 0 && py >= 0 && px < w && py < h) values.Add(bmp.GetPixel(px, py).B);
            values.Sort();
            return values[values.Count / 2];
        }
        // The corners of the square the moon's picture fills (about 130 px across here), and the sky just outside them
        const int Corner = 55, Outside = 90;
        double inside = new[] { Blue(cx - Corner, cy - Corner), Blue(cx + Corner, cy - Corner), Blue(cx - Corner, cy + Corner), Blue(cx + Corner, cy + Corner) }.Average();
        double sky = new[] { Blue(cx - Outside, cy - Outside), Blue(cx + Outside, cy - Outside), Blue(cx - Outside, cy + Outside), Blue(cx + Outside, cy + Outside) }.Average();
        TestContext.Progress.WriteLine($"[sky] the moon at {cx},{cy}; blue in the corners of its square {inside:F1}, in the sky outside it {sky:F1}");
        Assert.That(sky, Is.GreaterThan(5), "a sky bright enough for a black square to show");
        Assert.That(inside, Is.GreaterThan(sky * 0.75), "no black square round the moon");
    }

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
