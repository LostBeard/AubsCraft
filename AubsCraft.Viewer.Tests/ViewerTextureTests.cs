using System.Drawing;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The block textures of the game itself (generated from the client jar at build time) in the real viewer: they load, and the animated ones
/// move. A fixed hour (time= link) and a fixed camera over water make two frames comparable: the water changes between them, the sky does not.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      run name (default "tex")
/// </summary>
[NonParallelizable]
public class ViewerTextureTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "tex";
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
    public async Task TheAssetsLoad_AndTheWaterAnimates_WhileTheSkyDoesNot()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => log.Enqueue(m.Text);
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            // Over the water at the shore the shots find (a camera 1.6 above the surface looking out over the sea), at noon
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&dd=18&cam=228.5,66.2,-238.5,-18,90",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 150, quietSeconds: 6, maxSeconds: 90);

            var canvas = page.Locator("#map-canvas");
            var a = Path.Combine(_outDir, "water-a.png");
            var b = Path.Combine(_outDir, "water-b.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = a });
            await Task.Delay(700); // 14 ticks: the water texture moves on every other tick
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = b });

            Assert.That(log.Any(l => l.Contains("Block assets for Minecraft")), Is.True, "the render worker should have loaded the block assets:\n" + string.Join("\n", log.Where(l => l.Contains("Block") || l.Contains("assets")).Take(10)));
            Assert.That(log.Any(l => l.Contains("Block assets") && (l.Contains("HTTP") || l.Contains("error") || l.Contains("format"))), Is.False, "no asset load problem");

            using var imgA = new Bitmap(a);
            using var imgB = new Bitmap(b);
            double sky = ChangedFraction(imgA, imgB, 6, imgA.Height / 8);
            double water = ChangedFraction(imgA, imgB, imgA.Height / 2, imgA.Height - 6);
            TestContext.Progress.WriteLine($"[tex] changed between two frames 0.7 s apart: sky {sky:P2}, water {water:P2}");
            Assert.That(sky, Is.LessThan(0.002), "the sky is a flat colour and does not change");
            Assert.That(water, Is.GreaterThan(0.01), "the water texture animates, so the water region changes between two frames");
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "tex-console.log"), log); } catch { }
            await page.CloseAsync();
        }
    }

    // The share of sampled pixels in the rows [y0, y1) that differ by more than a few levels between the two images
    private static double ChangedFraction(Bitmap a, Bitmap b, int y0, int y1)
    {
        int total = 0, changed = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = 6; x < a.Width - 6; x += 2)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                total++;
                if (Math.Abs(p.R - q.R) > 6 || Math.Abs(p.G - q.G) > 6 || Math.Abs(p.B - q.B) > 6) changed++;
            }
        return total == 0 ? 0 : (double)changed / total;
    }
}
