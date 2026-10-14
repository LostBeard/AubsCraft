using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Fixed-camera screenshots of the live 3D viewer over the real world, plus its own stats line. Run against ANY
/// build of the viewer (set AUBS_VIEWER_TAG to name the run) so two builds can be compared shot for shot:
/// the V0 baseline of the current renderer, then the VoxelEngine renderer that replaces it.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      run name; shots land in screenshots/viewer/{tag}/ (default "run")
///
/// A missing AUBS_VIEWER_PUBLISH FAILS the test: a gate that quietly skips is a gate that reports green for nothing.
/// </summary>
[NonParallelizable]
public class ViewerShotTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;
    private readonly List<ShotRecord> _records = [];
    private (int X, int Z) _spawn;

    public sealed record ShotRecord(string Name, double SecondsToSettle, bool Settled, int Fps, int VisibleKVerts, int TotalKVerts,
        int VisibleSections, int TotalSections, int DrawDistance, int CamX, int CamY, int CamZ, double PixelStdDev, string File);

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "run";
        _outDir = Path.Combine(RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
        TestContext.Progress.WriteLine($"[viewer] server {_rig.BaseUrl}, shots -> {_outDir}");
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        File.WriteAllText(Path.Combine(_outDir, "shots.json"),
            JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true }));
        if (_rig != null) await _rig.DisposeAsync();
    }

    [Test, Order(1)]
    public async Task Shot01_SpawnTopDown_DefaultStart()
    {
        // No cam= link: the viewer's own start (above the world spawn, looking down).
        var s = await ShotAsync("01-spawn-topdown", cam: null, requireSections: 150);
        _spawn = (s.X, s.Z);
        Assert.That(s.Y, Is.GreaterThan(100), "The default start is high above the spawn");
    }

    [TestCase("02-aerial-north", -90f)]
    [TestCase("03-aerial-east", 0f)]
    [TestCase("04-aerial-south", 90f)]
    [TestCase("05-aerial-west", 180f), Order(2)]
    public async Task Aerial_FromSpawn(string name, float yaw)
    {
        RequireSpawn();
        var s = await ShotAsync(name, new Cam(_spawn.X, 110, _spawn.Z, -30, yaw), requireSections: 150);
        AssertCameraAt(s, 110);
    }

    [TestCase("06-ground-north", -90f)]
    [TestCase("07-ground-east", 0f), Order(3)]
    public async Task Ground_AtSpawn(string name, float yaw)
    {
        RequireSpawn();
        // Eye height 1.62 above the highest solid block in the spawn column, found from the same bytes the viewer streams.
        var col = await _rig.GetChunkAsync(FloorDiv(_spawn.X, 16), FloorDiv(_spawn.Z, 16));
        int lx = Mod(_spawn.X, 16), lz = Mod(_spawn.Z, 16);
        int top = col.SurfaceTop(lx, lz) ?? throw new AssertionException($"No ground under the spawn column ({_spawn.X},{_spawn.Z})");
        float eye = top + 1.62f;
        var s = await ShotAsync(name, new Cam(_spawn.X + 0.5f, eye, _spawn.Z + 0.5f, -5, yaw), requireSections: 150);
        AssertCameraAt(s, eye);
    }

    private void RequireSpawn() =>
        Assert.That(_spawn != default, Is.True, "Shot01 supplies the spawn the others are placed from; run the whole fixture");

    private record Cam(float X, float Y, float Z, float Pitch, float Yaw);

    private void AssertCameraAt(ViewerStats s, float y)
    {
        Assert.That(Math.Abs(s.Y - y), Is.LessThanOrEqualTo(1.5), $"The cam= link put the camera at Y {s.Y}, asked for {y}");
    }

    /// <summary>
    /// Opens the viewer at <paramref name="cam"/>, waits for the terrain around it to stream in (the stats line stops
    /// growing), saves the canvas, and records the stats.
    /// </summary>
    private async Task<ViewerStats> ShotAsync(string name, Cam? cam, int requireSections)
    {
        var page = await _rig.Context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") consoleErrors.Add(m.Text); };
        page.PageError += (_, e) => consoleErrors.Add(e);
        try
        {
            string url = _rig.BaseUrl + "/map" + (cam == null ? "" : string.Create(CultureInfo.InvariantCulture,
                $"?cam={cam.X},{cam.Y},{cam.Z},{cam.Pitch},{cam.Yaw}"));
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (stats, settled) = await WaitForSettledAsync(page, requireSections, quietSeconds: 20, maxSeconds: 240);
            double seconds = sw.Elapsed.TotalSeconds;

            var file = Path.Combine(_outDir, name + ".png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
            double sd = PixelStdDev(file);

            _records.Add(new ShotRecord(name, Math.Round(seconds, 1), settled, stats.Fps, stats.VisibleKVerts, stats.TotalKVerts,
                stats.VisibleSections, stats.TotalSections, stats.DrawDistance, stats.X, stats.Y, stats.Z, Math.Round(sd, 2), Path.GetFileName(file)));
            TestContext.Progress.WriteLine($"[viewer] {name}: {stats.Fps} FPS, {stats.VisibleSections}/{stats.TotalSections} sections, " +
                $"{stats.VisibleKVerts}K/{stats.TotalKVerts}K verts, draw {stats.DrawDistance}, settled={settled} after {seconds:F0}s, pixel sd {sd:F1}");

            Assert.That(consoleErrors, Is.Empty, "The page logged errors");
            Assert.That(stats.TotalSections, Is.GreaterThanOrEqualTo(requireSections), "Terrain streamed in");
            // A blank sky/fog frame has near-zero spread; terrain, water and trees do not.
            Assert.That(sd, Is.GreaterThan(12.0), "The frame is not a flat colour");
            if (cam != null)
            {
                Assert.That(Math.Abs(stats.X - cam.X), Is.LessThanOrEqualTo(1.5), "The cam= link put the camera at the asked X");
                Assert.That(Math.Abs(stats.Z - cam.Z), Is.LessThanOrEqualTo(1.5), "The cam= link put the camera at the asked Z");
            }
            return stats;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task<(ViewerStats Stats, bool Settled)> WaitForSettledAsync(IPage page, int minSections, int quietSeconds, int maxSeconds)
    {
        var started = DateTime.UtcNow;
        ViewerStats? last = null;
        int lastSections = -1;
        var lastChange = DateTime.UtcNow;
        while ((DateTime.UtcNow - started).TotalSeconds < maxSeconds)
        {
            await Task.Delay(1000);

            // A failed GPU init replaces the canvas with an alert: report it, do not wait it out.
            await ViewerStats.FailIfTheViewerFailedAsync(page);

            if (await ViewerStats.ReadAsync(page) is not { } now) continue;
            last = now;
            if (last.TotalSections != lastSections) { lastSections = last.TotalSections; lastChange = DateTime.UtcNow; }
            if (last.TotalSections >= minSections && (DateTime.UtcNow - lastChange).TotalSeconds >= quietSeconds)
                return (last, true);
        }
        if (last == null) Assert.Fail($"No stats line appeared within {maxSeconds} s");
        return (last!, false);
    }

    private static double PixelStdDev(string pngPath)
    {
        using var bmp = new Bitmap(pngPath);
        double sum = 0, sumSq = 0;
        long n = 0;
        // Every 5th pixel each way: plenty for a spread estimate.
        for (int y = 0; y < bmp.Height; y += 5)
            for (int x = 0; x < bmp.Width; x += 5)
            {
                var c = bmp.GetPixel(x, y);
                double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                sum += l; sumSq += l * l; n++;
            }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
    private static int Mod(int a, int b) => ((a % b) + b) % b;

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AubsCraft.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AubsCraft.slnx not found above " + AppContext.BaseDirectory);
    }
}
