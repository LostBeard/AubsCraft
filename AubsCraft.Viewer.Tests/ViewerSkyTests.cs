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
    private async Task<Bitmap> ShotAsync(string name, int ticks)
    {
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            string url = _rig.BaseUrl + "/map?hud=0&time=" + ticks.ToString(CultureInfo.InvariantCulture) + "&cam=0,120,0,12,0";
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

    [Test]
    public async Task AtNoon_TheSkyIsTheOldBlue()
    {
        using var bmp = await ShotAsync("noon", ticks: 6000);
        var c = bmp.GetPixel(bmp.Width / 2, 6); // top middle: sky
        // SkyColor (0.65, 0.80, 0.95) = the fog colour at noon: nothing changes by day
        Assert.That(c.R, Is.EqualTo(166).Within(4));
        Assert.That(c.G, Is.EqualTo(204).Within(4));
        Assert.That(c.B, Is.EqualTo(242).Within(4));
    }

    [Test]
    public async Task AtMidnight_TheSkyIsDark_AndNoDaytimeBlueShowsAnywhereAboveTheTerrain()
    {
        using var bmp = await ShotAsync("midnight", ticks: 18000);
        var top = bmp.GetPixel(bmp.Width / 2, 6);
        // The fog colour at midnight is (0.04, 0.05, 0.10): 10, 13, 26
        Assert.That(top.R, Is.LessThan(30));
        Assert.That(top.G, Is.LessThan(30));
        Assert.That(top.B, Is.InRange(15, 45));
        // The sky blue of the old constant clear colour is (166, 204, 242): not one pixel of the upper half is anywhere near it
        int dayBlue = 0;
        // Inside a 6 px margin: the canvas screenshot has a white 1 px edge (rounded corners)
        for (int y = 6; y < bmp.Height / 2; y += 3)
            for (int x = 6; x < bmp.Width - 6; x += 3)
            {
                var p = bmp.GetPixel(x, y);
                if (p.B > 150 && p.G > 120) dayBlue++;
            }
        Assert.That(dayBlue, Is.Zero, "daytime blue showed in the upper half of a midnight frame (the frame is cleared to the wrong colour)");
    }
}
