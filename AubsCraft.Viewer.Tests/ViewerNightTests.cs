using System.Drawing;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The scene at midnight, in the real viewer: nothing lights the world by block light, so the ground is dark, but the blocks that make their own light (glowstone, sea lanterns, shroomlights,
/// magma, torches) are drawn at full brightness. A row of them is at x 30..44, z 56 of the scene.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerNightTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "night";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    [Test]
    public async Task AtMidnight_TheLightBlocksGlow_AndTheGroundIsDark()
    {
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            // From the south of the row, a little above, looking north
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=18000&dd=6&cam=37.0,-57.6,62.0,8,-90", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            await Task.Delay(2500);
            var file = Path.Combine(_outDir, "night-lights.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
            using var bmp = new Bitmap(file);
            int bright = 0, dark = 0;
            // Below the horizon only (the sky has stars): the lower half of the frame
            for (int y = bmp.Height / 2 + 20; y < bmp.Height - 6; y += 2)
                for (int x = 6; x < bmp.Width - 6; x += 2)
                {
                    var p = bmp.GetPixel(x, y);
                    int lum = (p.R * 3 + p.G * 6 + p.B) / 10;
                    if (lum > 130) bright++; else if (lum < 50) dark++;
                }
            TestContext.Progress.WriteLine($"[night] bright {bright}, dark {dark}");
            Assert.That(bright, Is.GreaterThan(150), "the blocks that make light are bright");
            Assert.That(dark, Is.GreaterThan(bright * 5), "and the rest of the world is dark");
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally { await page.CloseAsync(); }
    }
}
