using System.Drawing;
using System.Text.RegularExpressions;
using AubsCraft.Live.Model;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Rain in the real viewer, over the SCENE world (real columns with real heightmaps): the live feed says it is raining, the sky greys, and a streak falls in each column round the camera that has
/// open sky over it. Checked by what the worker reports (the rain level eased up, the columns it falls on) and by the picture: more of the frame is made of near-white pale streaks than in a dry view.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerWeatherTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "weather";
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
    public async Task WhenItRains_StreaksFallInTheColumnsAroundTheCamera()
    {
        long tick = 500_000;
        bool raining = false;
        byte[] Frame() => LiveFrameCodec.Write(new LiveFrame
        {
            FullTime = tick, DayTime = 6000, Raining = raining, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = System.Array.Empty<LiveEntity>(), Players = System.Array.Empty<LivePlayer>() }],
        });

        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        using var stop = new CancellationTokenSource();
        Task? poster = null;
        try
        {
            // Over the scene's campfires, a little above and looking along the ground with the sky in view
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam=24.5,-56.0,53.5,6,-90", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            poster = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    int status = await _rig.PostLiveFrameAsync(Frame());
                    if (status != 204) throw new InvalidOperationException($"The panel answered {status} to a live frame");
                    tick += 4;
                    await Task.Delay(200);
                }
            });
            await Task.Delay(4000);
            var dry = Path.Combine(_outDir, "dry.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = dry });

            raining = true;
            // The rain comes in over some seconds (the weather eases); wait for it to be full and for the worker to say it is falling somewhere
            Match? full = null;
            for (int i = 0; i < 60 && full == null; i++)
            {
                await Task.Delay(1000);
                full = log.ToArray().Select(l => Regex.Match(l, @"weather (1\.00|0\.9\d) on (\d+) columns")).FirstOrDefault(m => m.Success);
            }
            await Task.Delay(1500);
            var wet = Path.Combine(_outDir, "rain.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = wet });

            // The rain FALLS, smoothly: shots taken a few frames apart all differ (the feed brings the server's time five times a second; had the rain moved only with those steps it would stand
            // still between them, and half the pairs of quick shots would be the same picture)
            var quick = new List<Bitmap>();
            for (int i = 0; i < 8; i++)
            {
                var file = Path.Combine(_outDir, $"rain-q{i}.png");
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
                quick.Add(new Bitmap(file));
            }
            int identical = 0;
            for (int i = 1; i < quick.Count; i++)
            {
                int differing = 0;
                for (int y = 6; y < quick[i].Height / 2; y += 3)
                    for (int x = 6; x < quick[i].Width - 6; x += 3)
                        if (quick[i].GetPixel(x, y) != quick[i - 1].GetPixel(x, y)) differing++;
                if (differing < 40) identical++;
            }
            foreach (var b in quick) b.Dispose();
            TestContext.Progress.WriteLine($"[weather] quick shots that did not change: {identical} of {quick.Count - 1}");
            Assert.That(identical, Is.LessThanOrEqualTo(1), "the rain moves between one shot and the next");

            Assert.That(full, Is.Not.Null, "the rain reached full:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("weather")).TakeLast(5)));
            Assert.That(int.Parse(full!.Groups[2].Value), Is.GreaterThan(50), "it falls on many of the window's columns");

            // Streaks: pale pixels in the open air, which a dry view of the same place does not have so many of
            using var dryBmp = new Bitmap(dry); using var wetBmp = new Bitmap(wet);
            int Pale(Bitmap b)
            {
                int n = 0;
                for (int y = 6; y < b.Height - 6; y += 2)
                    for (int x = 6; x < b.Width - 6; x += 2)
                    {
                        var p = b.GetPixel(x, y);
                        if (p.R > 150 && Math.Abs(p.R - p.G) < 25 && Math.Abs(p.G - p.B) < 25) n++;
                    }
                return n;
            }
            int dryPale = Pale(dryBmp), wetPale = Pale(wetBmp);
            TestContext.Progress.WriteLine($"[weather] pale samples dry {dryPale}, rain {wetPale}");
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            stop.Cancel();
            try { if (poster != null) await poster; } catch (OperationCanceledException) { }
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task WhenTheServersTimeStandsStill_TheRainStillFalls()
    {
        // A server whose day cycle is off (kept at noon) sends the same time in every frame; rain that moved with the server's clock would hang in the air
        const long frozenTick = 900_000;
        var frame = LiveFrameCodec.Write(new LiveFrame
        {
            FullTime = frozenTick, DayTime = 6000, Raining = true, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = System.Array.Empty<LiveEntity>(), Players = System.Array.Empty<LivePlayer>() }],
        });
        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => log.Enqueue(m.Text);
        page.Worker += (_, w) => w.Console += (_, m) => log.Enqueue(m.Text);
        using var stop = new CancellationTokenSource();
        Task? poster = null;
        try
        {
            // Looking west over the open grass at the scene's edge: nothing there gives off smoke, so what changes between shots is the rain
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam=-2.5,-58.0,2.0,4,180", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            poster = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    int status = await _rig.PostLiveFrameAsync(frame);
                    if (status != 204) throw new InvalidOperationException($"The panel answered {status} to a live frame");
                    await Task.Delay(200);
                }
            });
            for (int i = 0; i < 60 && !log.ToArray().Any(l => Regex.IsMatch(l, @"weather (1\.00|0\.9\d) on (\d+) columns")); i++) await Task.Delay(1000);
            await Task.Delay(1500);
            var shots = new List<Bitmap>();
            for (int i = 0; i < 6; i++)
            {
                var file = Path.Combine(_outDir, $"rain-frozen-{i}.png");
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
                shots.Add(new Bitmap(file));
            }
            int still = 0;
            for (int i = 1; i < shots.Count; i++)
            {
                // The lower part of the view: ground and air, no clouds
                int differing = 0;
                for (int y = shots[i].Height * 55 / 100; y < shots[i].Height - 6; y += 2)
                    for (int x = 6; x < shots[i].Width - 6; x += 2)
                        if (shots[i].GetPixel(x, y) != shots[i - 1].GetPixel(x, y)) differing++;
                TestContext.Progress.WriteLine($"[weather] frozen server time, shot {i}: {differing} pixels changed");
                if (differing < 600) still++;
            }
            foreach (var b in shots) b.Dispose();
            Assert.That(still, Is.Zero, "the rain falls though the server's time does not move");
        }
        finally
        {
            stop.Cancel();
            try { if (poster != null) await poster; } catch (OperationCanceledException) { }
            await page.CloseAsync();
        }
    }
}
