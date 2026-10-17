using System.Drawing;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Soft corner shadows (VoxelEngine's ambient occlusion, baked into the quads by the column mesher) in the real viewer, over the SCENE world: the same view with `ao=1` and `ao=0`. The soft one
/// is darker where blocks meet (the feet of walls, the corners under trees) and never brighter, and the page logs no error.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerAoTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "ao";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private async Task<Bitmap> ShotAsync(string name, string link, string cam)
    {
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&dd=8&time=6000&" + link + "&cam=" + cam, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 20, quietSeconds: 4, maxSeconds: 60);
            var file = Path.Combine(_outDir, name + ".png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
            return new Bitmap(file);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static int Luma(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;

    [Test]
    public async Task SoftShading_DarkensWhereBlocksMeet_AndNeverLightensAnything()
    {
        // Looking at the scene's platform from a little way off and above: walls, pillars, steps and trees stand on the ground
        const string cam = "-10,-52,30,-20,0";
        using var flat = await ShotAsync("flat", "ao=0", cam);
        using var soft = await ShotAsync("soft", "ao=1", cam);
        Assert.That(soft.Size, Is.EqualTo(flat.Size));

        // Below the horizon only: the sky's clouds drift by the viewer's clock, so the sky is never the same picture twice
        int from = (int)(flat.Height * 0.45), darker = 0, lighter = 0, total = 0;
        long lumaFlat = 0, lumaSoft = 0;
        for (int y = from; y < flat.Height; y += 1)
            for (int x = 0; x < flat.Width; x += 1)
            {
                int a = Luma(flat.GetPixel(x, y)), b = Luma(soft.GetPixel(x, y));
                lumaFlat += a; lumaSoft += b; total++;
                if (b < a - 12) darker++;
                if (b > a + 12) lighter++;
            }
        TestContext.Progress.WriteLine($"[ao] ground pixels {total}: {darker} darker, {lighter} lighter; mean luma {lumaFlat / (double)total:F1} -> {lumaSoft / (double)total:F1}");
        Assert.That(darker, Is.GreaterThan(total / 200), "the corners and the feet of walls are darker with soft shading");
        Assert.That(lighter, Is.LessThan(total / 1000), "nothing is lighter with it");
        Assert.That(lumaSoft, Is.LessThan(lumaFlat), "the picture is a little darker for it");
    }
}
