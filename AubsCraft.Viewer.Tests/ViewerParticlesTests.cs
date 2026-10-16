using System.Drawing;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Smoke and flames in the real viewer, over the SCENE world (SceneBuilderTests: lit campfires, fire, soul fire, torches at a known place): the server names the emitting blocks in a
/// header, the worker loads the particle sprites and draws the emitters near the camera. Checked by what the worker reports (the sprites loaded, emitters drawn) and by the picture (the plume
/// is gray puffs in the sky above the campfires and they MOVE).
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerParticlesTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "particles";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    // Pixels that are some gray (smoke) against the blue sky: r, g, b close to each other and not as blue as the sky
    private static int GrayPuffPixels(Bitmap bmp, Rectangle area)
    {
        int n = 0;
        for (int y = area.Top; y < area.Bottom; y += 2)
            for (int x = area.Left; x < area.Right; x += 2)
            {
                var p = bmp.GetPixel(x, y);
                int max = Math.Max(p.R, Math.Max(p.G, p.B)), min = Math.Min(p.R, Math.Min(p.G, p.B));
                if (max - min < 24 && max > 60 && max < 235) n++;
            }
        return n;
    }

    [Test]
    public async Task Campfires_SmokeIntoTheSky_AndTheWorkerSaysSo()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            // The scene's campfires stand at x 20..28, z 46: from the south, a little above, looking north and a little up so the sky above them is in view
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&dd=6&cam=24.5,-57.5,53.5,12,-90", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            for (int i = 0; i < 30 && !log.Any(l => Regex.IsMatch(l, @"\[Voxel\].*particles [1-9]\d*/\d+ emitters")); i++) await Task.Delay(1000);

            // A few frames over several seconds: a plume has gaps between its puffs, so one frame may hold few
            var files = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                var file = Path.Combine(_outDir, $"campfires-{i}.png");
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
                files.Add(file);
                await Task.Delay(2000);
            }

            Assert.That(log.Any(l => Regex.IsMatch(l, @"\[Particles\] Particle assets: \d+ sprites, \d+ kinds")), Is.True, "the particle sprites loaded:\n" + string.Join("\n", log.Where(l => l.Contains("Particles"))));
            Assert.That(log.Any(l => l.Contains("[Particles] No sprite for")), Is.False, "every sprite the catalog names exists");
            var drawn = log.Select(l => Regex.Match(l, @"particles (\d+)/(\d+) emitters")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
            Assert.That(drawn, Is.GreaterThanOrEqualTo(3), "emitters near the camera are drawn (two lit campfires, three embers)");

            var frames = files.Select(f => new Bitmap(f)).ToList();
            try
            {
                var sky = new Rectangle(0, 0, frames[0].Width, frames[0].Height / 2);
                var puffs = frames.Select(f => GrayPuffPixels(f, sky)).ToList();
                int changed = 0, total = 0;
                for (int y = 0; y < sky.Height; y += 3)
                    for (int x = 0; x < sky.Width; x += 3)
                    {
                        total++;
                        var pa = frames[0].GetPixel(x, y); var pb = frames[1].GetPixel(x, y);
                        if (Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B) > 40) changed++;
                    }
                TestContext.Progress.WriteLine($"[particles] drawn {drawn}; gray puff pixels in the sky per frame {string.Join(" ", puffs)}; the sky changed {(double)changed / total:P2} in 2 s");
                Assert.That(puffs.Max(), Is.GreaterThan(40), "gray smoke puffs are in the sky above the campfires");
                Assert.That((double)changed / total, Is.GreaterThan(0.003), "the smoke moves");
            }
            finally { foreach (var f in frames) f.Dispose(); }
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-particles.log"), log); } catch { }
            await page.CloseAsync();
        }
    }
}
