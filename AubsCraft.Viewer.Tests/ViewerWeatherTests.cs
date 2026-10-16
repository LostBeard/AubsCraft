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
}
