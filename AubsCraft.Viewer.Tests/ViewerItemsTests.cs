using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using AubsCraft.Live.Model;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Items lying about, in the real viewer, over the SCENE world (void where the items hang, so the sky is the backdrop): the live feed names each dropped item in the entity's variant,
/// the worker loads the item pictures and draws each as the game does (a flat item a sprite, a block a small cube), spinning and bobbing.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerItemsTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "items";
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
    public async Task DroppedItems_AreDrawnAsTheirPictures_FlatOnesAndBlocks()
    {
        string[] items = ["diamond_sword", "apple", "oak_log", "stone", "grass_block", "torch", "bread", "arrow", "iron_pickaxe", "oak_leaves", "cobblestone", "enchanted_golden_apple"];
        var entities = new List<LiveEntity>();
        for (int i = 0; i < items.Length; i++)
        {
            float x = -3.3f + (i % 6) * 1.3f, y = 119.2f - (i / 6) * 0.9f;
            entities.Add(new LiveEntity(100 + i, "item", x, y, 0.5f, 0f, 0f, 0f, 0, 0f, 0f, 0f, null, items[i]));
        }
        long tick = 200_000;
        byte[] Frame() => LiveFrameCodec.Write(new LiveFrame
        {
            FullTime = tick, DayTime = 6000, Raining = false, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = entities, Players = System.Array.Empty<LivePlayer>() }],
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
            // Looking along +z from three blocks back, level, at two rows of items hanging in the air
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam=0.5,119.4,-3.5,0,90", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            for (int i = 0; i < 40 && !log.Any(l => l.Contains("[Items] Item assets:")); i++) await Task.Delay(1000);
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
            await Task.Delay(6000);
            var files = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var file = Path.Combine(_outDir, $"items-{i}.png");
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
                files.Add(file);
                await Task.Delay(700);
            }

            Assert.That(log.Any(l => Regex.IsMatch(l, @"\[Items\] Item assets: \d{4} items")), Is.True, "the item pictures loaded:\n" + string.Join("\n", log.Where(l => l.Contains("Items"))));
            var drawn = log.ToArray().Select(l => Regex.Match(l, @"(\d+) items\)")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            Assert.That(drawn.Count == 0 ? 0 : drawn.Max(), Is.EqualTo(items.Length), "every item is in range and drawn");
            // Pixels that are neither sky blue nor cloud white are the items
            using var bmp = new Bitmap(files[0]);
            int itemPixels = 0;
            for (int y = 6; y < bmp.Height - 6; y += 2)
                for (int x = 6; x < bmp.Width - 6; x += 2)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.B < p.R + 30 && p.R < 215) itemPixels++;
                }
            TestContext.Progress.WriteLine($"[items] non-sky samples {itemPixels}");
            Assert.That(itemPixels, Is.GreaterThan(400), "the items show against the sky");
            // They turn: the frames differ
            using var a = new Bitmap(files[0]); using var b = new Bitmap(files[2]);
            int changed = 0;
            for (int y = 6; y < a.Height - 6; y += 3)
                for (int x = 6; x < a.Width - 6; x += 3)
                    if (a.GetPixel(x, y) != b.GetPixel(x, y)) changed++;
            Assert.That(changed, Is.GreaterThan(30), "the items spin and bob");
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
    public async Task BoatsMinecartsAndStands_AreDrawn_AsTheirGameModels()
    {
        string[] types = ["oak_boat", "bamboo_raft", "spruce_chest_boat", "cherry_boat", "minecart", "armor_stand"];
        var entities = new List<LiveEntity>();
        for (int i = 0; i < types.Length; i++)
        {
            float x = -6.25f + i * 2.5f;
            entities.Add(new LiveEntity(200 + i, types[i], x, 118.0f, 0.5f, i % 2 == 0 ? 20f : -35f, 0f, i % 2 == 0 ? 20f : -35f, 0, 0f, 0f, 0f, null, null));
        }
        long tick = 300_000;
        byte[] Frame() => LiveFrameCodec.Write(new LiveFrame
        {
            FullTime = tick, DayTime = 6000, Raining = false, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = entities, Players = System.Array.Empty<LivePlayer>() }],
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
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam=0.5,119.4,-6.5,-6,90", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
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
            for (int i = 0; i < 40 && !log.Any(l => Regex.IsMatch(l, @"\[Live\] creatures 6 drawn")); i++) await Task.Delay(1000);
            await Task.Delay(2000);
            var file = Path.Combine(_outDir, "vehicles.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
            Assert.That(log.Any(l => Regex.IsMatch(l, @"\[Live\] creatures 6 drawn")), Is.True, "all six are drawn");
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
