using System.Drawing;
using System.Globalization;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The sky is the fog colour at any hour. Terrain past the fog (450 blocks) is not drawn any more, so what is behind it - the cleared frame -
/// must be exactly what fogged terrain looks like: at noon the old sky blue, at midnight dark, never daytime blue behind a night horizon.
/// A time= link fixes the hour (Minecraft ticks), so the gate does not depend on the clock of the world it runs against.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
/// </summary>
[NonParallelizable]
public class ViewerSkyTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "sky";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    // Looking up and out from a height: the top of the frame is cleared sky (nothing is drawn there)
    private Task<Bitmap> ShotAsync(string name, int ticks) => ShotAsync(name, ticks, "&cam=0,120,0,12,0");

    private async Task<Bitmap> ShotAsync(string name, int ticks, string camAndOptions)
    {
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            string url = _rig.BaseUrl + "/map?hud=0&time=" + ticks.ToString(CultureInfo.InvariantCulture) + camAndOptions;
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            // Terrain streaming in below the horizon: a short wait, the sky does not depend on it
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 150, quietSeconds: 6, maxSeconds: 90);
            var file = Path.Combine(_outDir, name + ".png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
            Assert.That(errors, Is.Empty, "The page logged errors");
            return new Bitmap(file);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    // Pixels within 8 of the sky colour (166, 204, 242) below the horizon: what the fog has swallowed
    private static int SkyLike(Bitmap bmp)
    {
        int n = 0;
        for (int y = bmp.Height / 2; y < bmp.Height - 6; y += 3)
            for (int x = 6; x < bmp.Width - 6; x += 3)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.R - 166) <= 8 && Math.Abs(p.G - 204) <= 8 && Math.Abs(p.B - 242) <= 8) n++;
            }
        return n;
    }

    [Test]
    public async Task AShorterDrawDistance_BringsTheFogIn_SoTheEdgeOfTheWorldIsNeverSeen()
    {
        // The same view at the full distance and at 14 chunks (a weak GPU settles somewhere like that). The fog follows the draw distance,
        // so the short one has swallowed more of the land: more of the frame below the horizon is fog colour, and there is no ring of
        // unfogged terrain where the drawing stops (look at the saved shots: a hard edge would be a visible line)
        string view = "&cam=0,95,0,-4,0";
        using var far = await ShotAsync("dd30", ticks: 6000, camAndOptions: view + "&dd=30");
        using var near = await ShotAsync("dd14", ticks: 6000, camAndOptions: view + "&dd=14");
        int farSky = SkyLike(far), nearSky = SkyLike(near);
        TestContext.Progress.WriteLine($"[sky] fog-coloured samples below the horizon: dd=30 {farSky}, dd=14 {nearSky}");
        Assert.That(nearSky, Is.GreaterThan(farSky + 200), "a 14-chunk draw distance should show clearly more fog than 30");
    }

    [Test]
    public async Task AtNoon_TheSkyIsBlueOverhead_AndTheFogColourAtTheHorizon()
    {
        using var bmp = await ShotAsync("noon", ticks: 6000);
        // The top of the frame is up the sky: the zenith's blue, deeper than the fog colour (166, 204, 242) it fades to at the horizon; clouds (near white) are skipped
        long r = 0, n = 0;
        for (int y = 6; y < 40; y += 2)
            for (int x = 6; x < bmp.Width - 6; x += 2)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 200) continue;
                r += p.R; n++;
            }
        Assert.That(n, Is.GreaterThan(200));
        Assert.That(r / n, Is.LessThan(155), "deeper blue overhead than the fog colour");
    }

    [Test]
    public async Task AtMidnight_TheSkyIsDark_AndNoDaytimeBlueShowsAnywhereAboveTheTerrain()
    {
        using var bmp = await ShotAsync("midnight", ticks: 18000);
        var top = bmp.GetPixel(bmp.Width / 2, 6);
        // The sky at midnight is nearly black (a star or a cloud may be under this one pixel, so the bound is loose)
        Assert.That(top.R, Is.LessThan(60));
        Assert.That(top.G, Is.LessThan(60));
        Assert.That(top.B, Is.LessThan(80));
        // The sky blue of the old constant clear colour is (166, 204, 242): not one pixel of the upper half is anywhere near it
        int dayBlue = 0;
        // Inside a 6 px margin: the canvas screenshot has a white 1 px edge (rounded corners)
        for (int y = 6; y < bmp.Height / 2; y += 3)
            for (int x = 6; x < bmp.Width - 6; x += 3)
            {
                var p = bmp.GetPixel(x, y);
                if (p.B > 150 && p.G > 120 && p.B > p.R + 40) dayBlue++;   // the blue of the day, not a white star
            }
        Assert.That(dayBlue, Is.Zero, "daytime blue showed in the upper half of a midnight frame (the frame is cleared to the wrong colour)");
    }
}
