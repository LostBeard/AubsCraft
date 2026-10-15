using System.Drawing;
using AubsCraft.Live.Model;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The live creatures in the real viewer: the gate posts frames to the panel exactly as the Minecraft plugin does (loopback, shared secret), the viewer's
/// render worker takes them from the WebSocket, and the animals, monsters, villagers and a player stand on the ground in front of the camera, walking.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      run name (default "live")
/// </summary>
[NonParallelizable]
public class ViewerLiveTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "live";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private static readonly (int X, int Z) Spawn = (240, -256);

    /// <summary>An open flat stretch near the spawn: a 13 x 13 patch of dry ground within one block of the same height. Returns its centre and ground height.</summary>
    private async Task<(int X, int Z, int Top)> FindFlatGroundAsync()
    {
        int scx = Spawn.X >> 4, scz = Spawn.Z >> 4;
        var chunks = new Dictionary<(int, int), ChunkColumn>();
        async Task<ChunkColumn?> Get(int cx, int cz)
        {
            if (chunks.TryGetValue((cx, cz), out var c)) return c;
            try { return chunks[(cx, cz)] = await _rig.GetChunkAsync(cx, cz); } catch (InvalidOperationException) { return null; }
        }
        async Task<int?> DryTop(int wx, int wz)
        {
            var col = await Get(wx >> 4, wz >> 4);
            if (col == null) return null;
            int lx = wx & 15, lz = wz & 15;
            if (col.SurfaceTop(lx, lz) is not { } top || col.TopOfAnything(lx, lz) != top) return null; // water on top is not ground
            return top;
        }
        for (int ring = 0; ring <= 6; ring++)
            for (int dz = -ring; dz <= ring; dz++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                    for (int cell = 0; cell < 16; cell += 4)
                    {
                        int cx = (scx + dx) * 16 + 8, cz = (scz + dz) * 16 + cell + 2;
                        if (await DryTop(cx, cz) is not { } centre) continue;
                        bool flat = true;
                        for (int z = -6; z <= 6 && flat; z += 2)
                            for (int x = -6; x <= 6 && flat; x += 2)
                                flat = await DryTop(cx + x, cz + z) is { } t && Math.Abs(t - centre) <= 1;
                        if (flat) return (cx, cz, centre);
                    }
                }
        throw new AssertionException("No 13 x 13 patch of flat dry ground within 6 chunks of the spawn");
    }

    private static LiveEntity Mob(int id, string type, float x, float y, float z, float yaw, int flags = LiveFlags.OnGround, string? variant = null, string? name = null) =>
        new(id, type, x, y, z, yaw, 0f, yaw, flags, 0f, 0f, 0f, name, variant);

    [Test]
    public async Task TheCreatures_AreDrawn_OnTheGround_AndTheOnesWalkingMove()
    {
        var (gx, gz, top) = await FindFlatGroundAsync();
        TestContext.Progress.WriteLine($"[live] flat ground at {gx},{gz} height {top}");
        float cx = gx + 0.5f, cz = gz + 0.5f;
        float y = top;   // the top surface of the ground block

        // The camera 1.62 above the ground at the patch's near edge, looking north (-z); two rows of creatures in front of it
        var cam = (x: cx, y: top + 1.62f, z: cz + 5.5f);
        LiveEntity At(int id, string type, float dx, float dz, float yaw = 180f, string? variant = null, int flags = LiveFlags.OnGround) =>
            Mob(id, type, cx + dx, y, cz + dz, yaw, flags, variant);

        // yaw 180 faces north (-z), toward the camera's view direction: the creatures look away; the second row is turned toward the camera (yaw 0)
        var stationary = new List<LiveEntity>
        {
            At(1, "pig", -2.6f, 2.5f, 20f), At(2, "cow", -0.9f, 2.0f, -30f), Mob(3, "sheep", cx + 0.9f, y, cz + 2.5f, 15f, LiveFlags.OnGround, "pink", "Dolly"), At(4, "chicken", 2.6f, 2.0f, -20f),
            At(5, "zombie", -3.5f, -0.5f, 0f), At(6, "creeper", -1.5f, -0.5f, 10f), At(7, "villager", 0.5f, -0.5f, -15f), At(8, "skeleton", 2.5f, -0.5f, 0f),
            At(9, "cow", 4.2f, 0.0f, 0f), At(10, "enderman", -4.5f, 2.0f, 0f),
            new LiveEntity(11, "player", cx + 3.4f, y, cz + 1.0f, 200f, 0f, 180f, LiveFlags.Player | LiveFlags.OnGround, 0f, 0f, 0f, null, null),
        };
        var player = new LivePlayer(11, "01234567-89ab-cdef-fedc-ba9876543210", "Steve", "", false, 0, "diamond_sword", 1f);

        long tick = 100_000;
        byte[] Frame(float walkerX)
        {
            var entities = new List<LiveEntity>(stationary)
            {
                // A pig walking across the front at 0.1 blocks a tick (2 a second)
                new LiveEntity(12, "pig", walkerX, y, cz + 1.0f, 270f, 0f, 270f, LiveFlags.OnGround, 0.1f, 0f, 0f, null, null),
            };
            return LiveFrameCodec.Write(new LiveFrame
            {
                FullTime = tick, DayTime = 6000, Raining = false, Thundering = false,
                Worlds = [new LiveWorld { Name = "world", Entities = entities, Players = [player] }],
            });
        }

        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => log.Enqueue(m.Text);
        page.PageError += (_, e) => errors.Add(e);
        using var stop = new CancellationTokenSource();
        Task? poster = null;
        try
        {
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=14&cam={cx.ToString(System.Globalization.CultureInfo.InvariantCulture)},{cam.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{cam.z.ToString(System.Globalization.CultureInfo.InvariantCulture)},-8,-90",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 150, quietSeconds: 6, maxSeconds: 90);

            var canvas = page.Locator("#map-canvas");
            var empty1 = Path.Combine(_outDir, "live-0-empty-a.png");
            var empty2 = Path.Combine(_outDir, "live-0-empty-b.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = empty1 });
            await Task.Delay(700);
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = empty2 });

            // The plugin: a frame every 200 ms (4 ticks), the pig 0.4 blocks further along each time
            float walker = cx - 5f;
            poster = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    int status = await _rig.PostLiveFrameAsync(Frame(walker));
                    if (status != 204) throw new InvalidOperationException($"The panel answered {status} to a live frame");
                    tick += 4;
                    walker += 0.4f;
                    if (walker > cx + 5f) walker = cx - 5f;
                    await Task.Delay(200);
                }
            });
            await Task.Delay(8000);   // the socket connects, frames arrive, the render worker settles into them

            var a = Path.Combine(_outDir, "live-1-a.png");
            var b = Path.Combine(_outDir, "live-1-b.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = a });
            await Task.Delay(600);
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = b });

            using var imgE1 = new Bitmap(empty1);
            using var imgE2 = new Bitmap(empty2);
            using var imgA = new Bitmap(a);
            using var imgB = new Bitmap(b);
            double noise = ChangedFraction(imgE1, imgE2);
            double drawn = ChangedFraction(imgE1, imgA);
            double moved = ChangedFraction(imgA, imgB);
            TestContext.Progress.WriteLine($"[live] changed: empty vs empty {noise:P2}, empty vs creatures {drawn:P2}, creatures vs creatures 0.6 s later {moved:P2}");

            var lines = log.ToArray();
            Assert.That(lines.Any(l => l.Contains("[Live] Entity assets:")), Is.True, "the creature textures should have loaded:\n" + string.Join("\n", lines.Where(l => l.Contains("Live")).Take(10)));
            Assert.That(lines.Any(l => l.Contains("Live feed connected")), Is.True, "the live socket should have connected");
            var creatures = lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"\[Live\] creatures (\d+) drawn of (\d+) \((\d+) parts, (\d+) players, (\d+) frames, (\d+) name tags\)")).Where(m => m.Success).ToList();
            Assert.That(creatures, Is.Not.Empty, "the worker should report the creatures");
            var best = creatures.OrderByDescending(m => int.Parse(m.Groups[1].Value)).First();
            TestContext.Progress.WriteLine($"[live] the worker reports: {best.Value}");
            Assert.That(int.Parse(best.Groups[1].Value), Is.GreaterThanOrEqualTo(10), "most of the 12 creatures should be in range and drawn");
            Assert.That(int.Parse(best.Groups[3].Value), Is.GreaterThan(60), "their boxes");

            Assert.That(noise, Is.LessThan(0.05), "with no creatures two frames are nearly alike (the lake in view animates)");
            Assert.That(drawn, Is.GreaterThan(0.03), "the creatures cover a good part of the view");
            Assert.That(moved, Is.GreaterThan(noise + 0.003), "the walking pig and the swaying arms change the picture between frames");

            // With the HUD on, names float over the player and the named sheep
            while (log.TryDequeue(out _)) { }
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=1&time=6000&dd=14&cam={cx.ToString(System.Globalization.CultureInfo.InvariantCulture)},{cam.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{cam.z.ToString(System.Globalization.CultureInfo.InvariantCulture)},-8,-90",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 150, quietSeconds: 4, maxSeconds: 90);
            await Task.Delay(6000);
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "live-2-hud-nametags.png") });
            var tagLines = log.ToArray().Select(l => System.Text.RegularExpressions.Regex.Match(l, @"(\d+) name tags\)")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            TestContext.Progress.WriteLine($"[live] name tags reported: {string.Join(",", tagLines)}");
            Assert.That(tagLines, Is.Not.Empty, "the worker should report its name tags");
            Assert.That(tagLines.Max(), Is.GreaterThanOrEqualTo(2), "the player and the named sheep each get a name tag");

            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally
        {
            stop.Cancel();
            try { if (poster != null) await poster; } catch (OperationCanceledException) { }
            try { File.WriteAllLines(Path.Combine(_outDir, "live-console.log"), log); } catch { }
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task TheZoo_EveryModel_StandsInARow_ForTheEye()
    {
        var (gx, gz, top) = await FindFlatGroundAsync();
        float cx = gx + 0.5f, cz = gz + 0.5f, y = top;
        string[] front = ["pig", "cow", "sheep", "chicken", "mooshroom", "villager", "player", "creeper"];
        string[] back = ["zombie", "husk", "skeleton", "stray", "enderman", "wolf", "spider", "cave_spider"];
        var entities = new List<LiveEntity>();
        int id = 1;
        for (int i = 0; i < front.Length; i++)
            entities.Add(new LiveEntity(id++, front[i], cx - 5.25f + i * 1.5f, y, cz + 1.5f, 180f, 0f, 180f,
                front[i] == "player" ? LiveFlags.Player | LiveFlags.OnGround : LiveFlags.OnGround, 0f, 0f, 0f, null, null));
        for (int i = 0; i < back.Length; i++)
            entities.Add(new LiveEntity(id++, back[i], cx - 5.25f + i * 1.5f, y, cz - 1.5f, 180f, 0f, 180f, LiveFlags.OnGround, 0f, 0f, 0f, null, null));
        var players = new[] { new LivePlayer(front.ToList().IndexOf("player") + 1, "89abcdef-0123-4567-89ab-cdef01234567", "Alex", "", true, 0, "air", 1f) };
        long tick = 200_000;
        byte[] Frame() => LiveFrameCodec.Write(new LiveFrame
        {
            FullTime = tick, DayTime = 6000, Raining = false, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = entities, Players = players }],
        });

        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => log.Enqueue(m.Text);
        page.PageError += (_, e) => errors.Add(e);
        using var stop = new CancellationTokenSource();
        Task? poster = null;
        try
        {
            string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Facing south (yaw 90 in the viewer's convention) from the north edge, a little above, looking at both rows from the front
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=1&time=6000&dd=14&cam={F(cx)},{F(top + 3.2f)},{F(cz - 7.5f)},-14,90",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 150, quietSeconds: 6, maxSeconds: 90);
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
            await Task.Delay(7000);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "zoo.png") });
            var drawn = log.ToArray().Select(l => System.Text.RegularExpressions.Regex.Match(l, @"\[Live\] creatures (\d+) drawn")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            Assert.That(drawn.Count == 0 ? 0 : drawn.Max(), Is.EqualTo(16), "all sixteen are in range and drawn");
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally
        {
            stop.Cancel();
            try { if (poster != null) await poster; } catch (OperationCanceledException) { }
            await page.CloseAsync();
        }
    }

    // The share of sampled pixels that differ by more than a few levels between the two images
    private static double ChangedFraction(Bitmap a, Bitmap b)
    {
        int total = 0, changed = 0;
        for (int y = 6; y < a.Height - 6; y += 2)
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
