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
        // The server's own account of the run, beside the shots (a WebSocket that closes with 1006 is only explained here)
        if (_rig != null) try { File.WriteAllText(Path.Combine(_outDir, "server.log"), _rig.ServerLog); } catch { }
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

    // The two scenes the first seven shots do not cover: where land meets water at eye level, and the inside of a cave. Both spots are
    // FOUND from the same chunk bytes the viewer streams (nearest to the spawn, in a fixed scan order), so every build and both
    // renderers are shot from the same camera.

    [Test, Order(4)]
    public async Task Shoreline_08_LandMeetsWaterAtEyeLevel()
    {
        RequireSpawn();
        var spot = await FindShorelineAsync() ?? throw new AssertionException("No shoreline (a beach cell next to water) within 6 chunks of the spawn");
        TestContext.Progress.WriteLine($"[viewer] shoreline at {spot.X:F1} {spot.Y:F1} {spot.Z:F1} yaw {spot.Yaw:F0}");
        var s = await ShotAsync("08-shoreline", spot, requireSections: 150);
        AssertCameraAt(s, spot.Y);
    }

    [Test, Order(5)]
    public async Task Cave_09_InsideARoomyCave()
    {
        RequireSpawn();
        var spot = await FindCaveAsync() ?? throw new AssertionException("No roomy cave (5x3x5 of air, 12 blocks under the surface) within 6 chunks of the spawn");
        TestContext.Progress.WriteLine($"[viewer] cave at {spot.X:F1} {spot.Y:F1} {spot.Z:F1} yaw {spot.Yaw:F0}");
        var s = await ShotAsync("09-cave", spot, requireSections: 150);
        AssertCameraAt(s, spot.Y);
    }

    private static readonly (int dx, int dz)[] FourWays = { (1, 0), (0, 1), (-1, 0), (0, -1) };

    private static float YawOf(int dx, int dz) => MathF.Atan2(dz, dx) * 180f / MathF.PI;

    /// <summary>Chunks around the spawn, nearest first (so the first match is the closest one to the camera of the other shots).</summary>
    private IEnumerable<(int cx, int cz)> ChunksAroundSpawn(int radius)
    {
        int scx = FloorDiv(_spawn.X, 16), scz = FloorDiv(_spawn.Z, 16);
        var all = new List<(int cx, int cz)>();
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++) all.Add((scx + dx, scz + dz));
        return all.OrderBy(c => (c.cx - scx) * (c.cx - scx) + (c.cz - scz) * (c.cz - scz)).ThenBy(c => c.cx).ThenBy(c => c.cz);
    }

    /// <summary>A beach cell (land at most 3 above the water) with water beside it, eye height above it, looking at the water.</summary>
    private async Task<Cam?> FindShorelineAsync()
    {
        foreach (var (cx, cz) in ChunksAroundSpawn(6))
        {
            ChunkColumn col;
            try { col = await _rig.GetChunkAsync(cx, cz); } catch (InvalidOperationException) { continue; }
            for (int z = 1; z < 15; z++)
                for (int x = 1; x < 15; x++)
                {
                    if (col.SurfaceTop(x, z) is not { } land || col.TopOfAnything(x, z) is not { } landAny || landAny != land) continue; // a dry column
                    foreach (var (dx, dz) in FourWays)
                    {
                        int wx = x + dx, wz = z + dz;
                        if (col.SurfaceTop(wx, wz) is not { } floor || col.TopOfAnything(wx, wz) is not { } surface || surface <= floor) continue; // not water
                        // The water must be deep enough to read as water and the beach low: standing at the edge of the sea
                        if (surface - floor < 2 || land > surface + 3 || land < surface) continue;
                        // ... and open water a few blocks further on, so the shot is not a puddle
                        int wet = 0;
                        for (int k = 1; k <= 4; k++)
                        {
                            int ax = x + dx * k, az = z + dz * k;
                            if (ax < 0 || ax > 15 || az < 0 || az > 15) break;
                            if (col.TopOfAnything(ax, az) is { } t && col.SurfaceTop(ax, az) is { } f && t > f) wet++;
                        }
                        if (wet < 4) continue;
                        return new Cam(cx * 16 + x + 0.5f, land + 1.62f, cz * 16 + z + 0.5f, -4, YawOf(dx, dz));
                    }
                }
        }
        return null;
    }

    /// <summary>The middle of a 5 x 3 x 5 block of air that lies at least 12 blocks below the surface, looking along its longest open run.</summary>
    private async Task<Cam?> FindCaveAsync()
    {
        foreach (var (cx, cz) in ChunksAroundSpawn(6))
        {
            ChunkColumn col;
            try { col = await _rig.GetChunkAsync(cx, cz); } catch (InvalidOperationException) { continue; }
            for (int y = 2; y < ChunkColumn.Height - 4; y++)
                for (int z = 2; z < 14; z++)
                    for (int x = 2; x < 14; x++)
                    {
                        if (!col.IsAirAt(x, y, z)) continue;
                        if (col.TopOfAnything(x, z) is not { } surface || y + ChunkColumn.BaseY > surface - 12) continue;
                        bool roomy = true;
                        for (int dy = 0; dy < 3 && roomy; dy++)
                            for (int dz = -2; dz <= 2 && roomy; dz++)
                                for (int dx = -2; dx <= 2 && roomy; dx++)
                                    roomy = col.IsAirAt(x + dx, y + dy, z + dz);
                        if (!roomy) continue;
                        // Look where the air runs farthest (up to 14 cells, within the chunk)
                        int bestRun = -1, bdx = 1, bdz = 0;
                        foreach (var (dx, dz) in FourWays)
                        {
                            int run = 0;
                            while (run < 14 && x + dx * (run + 1) is >= 0 and <= 15 && z + dz * (run + 1) is >= 0 and <= 15
                                   && col.IsAirAt(x + dx * (run + 1), y + 1, z + dz * (run + 1))) run++;
                            if (run > bestRun) { bestRun = run; bdx = dx; bdz = dz; }
                        }
                        return new Cam(cx * 16 + x + 0.5f, y + ChunkColumn.BaseY + 1.62f, cz * 16 + z + 0.5f, -2, YawOf(bdx, bdz));
                    }
        }
        return null;
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
        // Every console line of the page and its workers (the render worker's [RenderWorker]/[Voxel] lines are where a silent
        // failure shows up) goes to console-<shot>.log beside the screenshot
        var consoleLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) =>
        {
            consoleLog.Enqueue($"[page {m.Type}] {m.Text}");
            if (m.Type == "error") consoleErrors.Add(m.Text);
        };
        page.Worker += (_, w) => w.Console += (_, m) => consoleLog.Enqueue($"[worker {m.Type}] {m.Text}");
        page.PageError += (_, e) => consoleErrors.Add(e);
        try
        {
            var query = new List<string>();
            if (cam != null) query.Add(string.Create(CultureInfo.InvariantCulture, $"cam={cam.X},{cam.Y},{cam.Z},{cam.Pitch},{cam.Yaw}"));
            // AUBS_VIEWER_HUD=0: no HUD in the frame, so two renderers' shots compare as worlds
            if (Environment.GetEnvironmentVariable("AUBS_VIEWER_HUD") == "0") query.Add("hud=0");
            string url = _rig.BaseUrl + "/map" + (query.Count == 0 ? "" : "?" + string.Join("&", query));
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (stats, settled) = await WaitForSettledAsync(page, requireSections, quietSeconds: 20, maxSeconds: int.TryParse(Environment.GetEnvironmentVariable("AUBS_VIEWER_MAX_SECONDS"), out var cap) ? cap : 240);
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
            try { File.WriteAllLines(Path.Combine(_outDir, $"console-{name}.log"), consoleLog); } catch { }
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
