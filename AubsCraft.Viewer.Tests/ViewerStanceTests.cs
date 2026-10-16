using System.Text.RegularExpressions;
using AubsCraft.Live.Model;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// How players stand, in the real viewer over the SCENE world (the players hang in the air, the sky the backdrop): upright with a sword, sneaking with an apple, holding a block, swimming,
/// gliding, riding, sleeping, dead. Checked by the picture (saved to screenshots/viewer/stances) and by what the worker reports (every one drawn).
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerStanceTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "stances";
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
    public async Task Players_StandSneakSwimGlideRideSleepAndLieDead_AndHoldWhatTheyHold()
    {
        const int P = LiveFlags.Player;
        var stances = new (string name, int flags, string held, float pitch)[]
        {
            ("upright", P | LiveFlags.OnGround, "diamond_sword", 0f),
            ("sneaking", P | LiveFlags.OnGround | LiveFlags.Sneaking, "apple", 0f),
            ("block", P | LiveFlags.OnGround, "grass_block", 0f),
            ("pickaxe", P | LiveFlags.OnGround, "iron_pickaxe", 0f),
            ("swimming", P | LiveFlags.Swimming, "air", 0f),
            ("gliding", P | LiveFlags.Gliding, "air", 0f),
            ("riding", P | LiveFlags.Riding, "air", 0f),
            ("sleeping", P | LiveFlags.Sleeping, "air", 0f),
            ("dead", P | LiveFlags.Dead, "air", 0f),
        };
        var entities = new List<LiveEntity>();
        var players = new List<LivePlayer>();
        for (int i = 0; i < stances.Length; i++)
        {
            var (name, flags, held, pitch) = stances[i];
            float x = -8.4f + i * 2.1f;
            // Facing the camera (south, yaw 0 is +z; the camera is on the +z side looking north)
            entities.Add(new LiveEntity(400 + i, "player", x, 118.0f, 0.5f, 0f, pitch, 0f, flags, 0f, 0f, 0f, null, null));
            players.Add(new LivePlayer(400 + i, $"00000000-0000-4000-8000-0000000004{i:00}", name, "", false, 0, held, 1f));
        }
        entities.Add(new LiveEntity(420, "zombie", 11f, 118.0f, 0.5f, 0f, 0f, 0f, LiveFlags.Dead, 0f, 0f, 0f, null, null));
        long tick = 700_000;
        byte[] Frame() => LiveFrameCodec.Write(new LiveFrame
        {
            FullTime = tick, DayTime = 6000, Raining = false, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = entities, Players = players }],
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
            // From the south, level with the players' chests, far enough back to take them all in
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam=1.5,119.4,14.5,0,-90", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            for (int i = 0; i < 40 && !log.ToArray().Any(l => l.Contains("[Items] Item assets:")); i++) await Task.Delay(1000);
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
            for (int i = 0; i < 30 && !log.ToArray().Any(l => Regex.IsMatch(l, @"creatures 10 drawn")); i++) await Task.Delay(1000);
            await Task.Delay(2500);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "stances.png") });
            // Close to the four who stand, for a good look at what is held; then to the ones lying down
            foreach (var (name, cam) in new[] { ("close-standing", "-5.3,119.3,4.6,0,-90"), ("close-lying", "5.2,119.3,4.6,0,-90"), ("side-standing", "-11.4,119.3,0.5,0,0") })
            {
                await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
                await Task.Delay(5000);
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, name + ".png") });
            }
            Assert.That(log.ToArray().Any(l => Regex.IsMatch(l, @"creatures 10 drawn")), Is.True, "all ten are drawn:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("[Live]")).TakeLast(3)));
            // Four hold something: a sword, an apple, a block, a pickaxe
            Assert.That(log.ToArray().Any(l => Regex.IsMatch(l, @"\b[4-9] items\)")), Is.True, "what is held is drawn:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("[Live]")).TakeLast(3)));
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
    public async Task Players_SwingTheirArms_DrawBows_AndEat()
    {
        const int P = LiveFlags.Player | LiveFlags.OnGround;
        var acts = new (string name, int flags, string held)[]
        {
            ("swinging", P | LiveFlags.Swinging, "iron_pickaxe"),
            ("bow", P | LiveFlags.Using, "bow"),
            ("eating", P | LiveFlags.Using, "apple"),
        };
        var entities = new List<LiveEntity>();
        var players = new List<LivePlayer>();
        for (int i = 0; i < acts.Length; i++)
        {
            entities.Add(new LiveEntity(430 + i, "player", -2.4f + i * 2.4f, 118.0f, 0.5f, 0f, 0f, 0f, acts[i].flags, 0f, 0f, 0f, null, null));
            players.Add(new LivePlayer(430 + i, $"00000000-0000-4000-8000-0000000004{30 + i}", acts[i].name, "", i == 1, 0, acts[i].held, 1f));
        }
        long tick = 1_000_000;
        byte[] Frame() => LiveFrameCodec.Write(new LiveFrame
        {
            FullTime = tick, DayTime = 6000, Raining = false, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = entities, Players = players }],
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
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam=0.0,119.3,4.4,0,-90", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            for (int i = 0; i < 40 && !log.ToArray().Any(l => l.Contains("[Items] Item assets:")); i++) await Task.Delay(1000);
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
            for (int i = 0; i < 30 && !log.ToArray().Any(l => Regex.IsMatch(l, @"creatures 3 drawn")); i++) await Task.Delay(1000);
            // Several frames: the swing moves
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(1100);
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, $"actions-{i}.png") });
            }
            Assert.That(log.ToArray().Any(l => Regex.IsMatch(l, @"creatures 3 drawn")), Is.True, "all three are drawn");
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
    public async Task Players_WearTheirArmor()
    {
        const int P = LiveFlags.Player | LiveFlags.OnGround;
        string[] sets =
        [
            "diamond|diamond|diamond|diamond", "iron|iron|iron|iron", "gold|chainmail|iron|diamond", "leather#CC2222|leather#CC2222|leather#CC2222|leather#CC2222", "netherite|netherite|netherite|netherite", "turtle_scute|||",
        ];
        var entities = new List<LiveEntity>();
        var players = new List<LivePlayer>();
        for (int i = 0; i < sets.Length; i++)
        {
            entities.Add(new LiveEntity(440 + i, "player", -5.0f + i * 2.0f, 118.0f, 0.5f, 0f, 0f, 0f, P, 0f, 0f, 0f, null, sets[i]));
            players.Add(new LivePlayer(440 + i, $"00000000-0000-4000-8000-0000000004{40 + i}", "armor" + i, "", i % 2 == 1, 0, "air", 1f));
        }
        long tick = 1_100_000;
        byte[] Frame() => LiveFrameCodec.Write(new LiveFrame
        {
            FullTime = tick, DayTime = 6000, Raining = false, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = entities, Players = players }],
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
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam=0.0,119.3,6.0,0,-90", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
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
            for (int i = 0; i < 30 && !log.ToArray().Any(l => Regex.IsMatch(l, @"creatures 6 drawn")); i++) await Task.Delay(1000);
            await Task.Delay(3000);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "armor.png") });
            Assert.That(log.ToArray().Any(l => Regex.IsMatch(l, @"creatures 6 drawn")), Is.True, "all six are drawn");
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
