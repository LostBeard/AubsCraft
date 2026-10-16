using System.Globalization;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The viewer over the SCENE world (SceneBuilderTests builds it on a local Paper server: every stateful block at a known place): each item shot from three sides. For the eye; the
/// assertion is that the page logs no errors. Set AUBS_VIEWER_WORLD to the scene folder (default D:/users/tj/Projects/_scratch/scene-server) and AUBS_SCENE_ITEMS to the items file.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_ONLY      comma list of item labels to shoot (default all)
/// </summary>
[NonParallelizable]
public class ViewerSceneTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "scene";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>AUBS_BOX="x0,y0,z0,x1,y1,z1": a built thing (somebody else's build, a plan's target) shot from its south-west and north-east corners, from above and from the south; AUBS_VIEWER_TAG names the folder.</summary>
    [Test]
    public async Task ABox_IsShot_FromItsCorners()
    {
        var box = Environment.GetEnvironmentVariable("AUBS_BOX");
        if (string.IsNullOrEmpty(box)) Assert.Ignore("AUBS_BOX not set");
        var v = box!.Split(',').Select(float.Parse).ToArray();
        float cx = (v[0] + v[3] + 1) / 2f, cy = (v[1] + v[4] + 1) / 2f, cz = (v[2] + v[5] + 1) / 2f;
        float size = Math.Max(Math.Max(v[3] - v[0], v[5] - v[2]), v[4] - v[1]) + 1;
        float d = size * 0.8f + 2f;
        // The camera stands (dx, dz) from the centre and looks at it: yaw 0 = +X, 90 = +Z; pitch up positive
        string Cam(float dx, float dy, float dz)
        {
            float yaw = MathF.Atan2(-dz, -dx) * 180f / MathF.PI, pitch = MathF.Atan2(-dy, MathF.Sqrt(dx * dx + dz * dz)) * 180f / MathF.PI;
            return $"{F(cx + dx)},{F(cy + dy)},{F(cz + dz)},{F(pitch)},{F(yaw)}";
        }
        var cams = new[]
        {
            ("southwest", Cam(-d, size * 0.6f, d)),
            ("northeast", Cam(d, size * 0.6f, -d)),
            ("south", Cam(0, size * 0.4f, d * 1.2f)),
            ("top", Cam(0.01f, size * 2.2f, 0.01f)),
        };
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        // AUBS_BOX_ENTITIES="cow,37.5,-60,35.5,yaw,baby;pig,...": creatures standing there (the world's region files hold no entities), posted as the plugin would
        using var stopPoster = new CancellationTokenSource();
        Task? poster = null;
        if (Environment.GetEnvironmentVariable("AUBS_BOX_ENTITIES") is { Length: > 0 } spec)
        {
            var list = new List<AubsCraft.Live.Model.LiveEntity>();
            int id = 1;
            foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(',');
                bool baby = f.Length > 5 && f[5] == "1";
                list.Add(new AubsCraft.Live.Model.LiveEntity(id++, f[0], float.Parse(f[1], CultureInfo.InvariantCulture), float.Parse(f[2], CultureInfo.InvariantCulture), float.Parse(f[3], CultureInfo.InvariantCulture),
                    f.Length > 4 ? float.Parse(f[4], CultureInfo.InvariantCulture) : 0f, 0f, f.Length > 4 ? float.Parse(f[4], CultureInfo.InvariantCulture) : 0f,
                    AubsCraft.Live.Model.LiveFlags.OnGround | (baby ? AubsCraft.Live.Model.LiveFlags.Baby : 0), 0f, 0f, 0f, null, null));
            }
            long tick = 500_000;
            poster = Task.Run(async () =>
            {
                while (!stopPoster.IsCancellationRequested)
                {
                    var frame = AubsCraft.Live.Model.LiveFrameCodec.Write(new AubsCraft.Live.Model.LiveFrame
                    {
                        FullTime = tick, DayTime = 6000, Raining = false, Thundering = false,
                        Worlds = [new AubsCraft.Live.Model.LiveWorld { Name = "world", Entities = list, Players = [] }],
                    });
                    await _rig.PostLiveFrameAsync(frame);
                    tick += 4;
                    await Task.Delay(200);
                }
            });
        }
        try
        {
            await page.SetViewportSizeAsync(1600, 900);
            foreach (var (suffix, cam) in cams)
            {
                await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
                await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
                if (poster != null) await Task.Delay(6000);   // the live socket connects and the creatures arrive
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, $"box-{suffix}.png") });
            }
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally
        {
            stopPoster.Cancel();
            try { if (poster != null) await poster; } catch (OperationCanceledException) { }
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task TheSceneItems_AreShot()
    {
        var itemsFile = Environment.GetEnvironmentVariable("AUBS_SCENE_ITEMS") ?? "D:/users/tj/Projects/_scratch/scene-items.txt";
        var items = File.ReadAllLines(itemsFile).Select(l => l.Split('|')).Select(p => (label: p[0], x: int.Parse(p[1]), y: int.Parse(p[2]), z: int.Parse(p[3]))).ToList();
        if (Environment.GetEnvironmentVariable("AUBS_SCENE_ONLY") is { Length: > 0 } only) items = items.Where(i => only.Split(',').Any(o => i.label.StartsWith(o))).ToList();

        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        // What the worker says about the camera and the water, per shot (the viewer logs when it decides the camera is under water or out of it)
        var waterLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Worker += (_, w) => w.Console += (_, m) => { if (m.Text.Contains("camera under water") || m.Text.Contains("camera out of water")) waterLog.Enqueue(m.Text); };
        try
        {
            foreach (var (label, x, y, z) in items)
            {
                // From the south (looking north), from the east (looking west), and from above
                var cams = new List<(string suffix, string cam)>
                {
                    ("south", $"{F(x + 0.5f)},{F(y + 2.2f)},{F(z + 5.5f)},-18,-90"),
                    ("east", $"{F(x + 6f)},{F(y + 2.2f)},{F(z + 0.5f)},-18,180"),
                    ("top", $"{F(x + 0.5f)},{F(y + 7f)},{F(z + 0.5f)},-89,-90"),
                    ("diag", $"{F(x + 4f)},{F(y + 2.2f)},{F(z + 4f)},-22,-135"),
                    ("profile", $"{F(x + 2.5f)},{F(y + 0.5f)},{F(z + 8f)},0,-90"),
                };
                // A pool (seven blocks across, three deep, water from y to y + 2) is also seen from INSIDE: the camera under the surface
                if (label == "pool")
                {
                    cams.Add(("under-across", $"{F(x - 2f)},{F(y + 1.2f)},{F(z - 2f)},0,45"));   // looking across the pool, two blocks under the surface
                    cams.Add(("under-up", $"{F(x + 0.5f)},{F(y + 0.7f)},{F(z + 0.5f)},55,0"));    // looking up at the surface from below
                    cams.Add(("under-down", $"{F(x + 0.5f)},{F(y + 1.7f)},{F(z - 1f)},-40,90"));  // just under the surface, looking down at the seabed
                    cams.Add(("surface-edge", $"{F(x - 3.5f)},{F(y + 2.6f)},{F(z + 0.5f)},-5,0"));   // half in the air: the camera at the waterline, looking across the surface
                }
                foreach (var (suffix, cam) in cams)
                {
                    await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
                    await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
                    await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, $"{label}-{suffix}.png") });
                    while (waterLog.TryDequeue(out var line)) TestContext.Progress.WriteLine($"[scene] {label}-{suffix}: {line}");
                }
            }
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally { await page.CloseAsync(); }
    }
}
