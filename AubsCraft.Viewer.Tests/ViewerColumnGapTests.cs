using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Tests;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// An entire column of blocks (a 16 x 16 stack of the world) must never be missing from the view. TJ's report of 2026-11-04: at (-455, 137, -421) whole
/// chunks were gone with the neighbours' side walls showing all the way down, while the region files held every one of them, complete.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      where the shots go, under screenshots/viewer (default "columns")
/// </summary>
[NonParallelizable]
public class ViewerColumnGapTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "columns";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) try { File.WriteAllText(Path.Combine(_outDir, "server.log"), _rig.ServerLog); } catch { }
        if (_rig != null) await _rig.DisposeAsync();
    }

    // "... | gaps 3 -29,-27:none ..." at the end of the worker's [Voxel] line
    private static readonly Regex GapLine = new(@"\| gaps (?<n>\d+)(?<first>[^|]*)$", RegexOptions.Compiled);

    private static int Gold(string png)
    {
        using var b = new System.Drawing.Bitmap(png);
        int n = 0;
        for (int y = 2; y < b.Height - 2; y += 2)
            for (int x = 2; x < b.Width - 2; x += 2)
            {
                var p = b.GetPixel(x, y);
                if (p.R > 90 && p.R >= p.G && p.B * 2 < p.G) n++;   // gold: red at least as strong as green, little blue
            }
        return n;
    }

    /// <summary>
    /// The real scene of the report, on a world the test owns: a viewer is open over finished ground with one chunk that is only a generation stub and one the files do not hold at all. The game
    /// finishes them (the region file changes, nothing else happens: no plugin says a word): they appear, with no reload.
    /// </summary>
    [Test]
    public async Task GroundGeneratedWhileTheViewerIsOpen_FillsTheHole_WithNoReload()
    {
        const uint T0 = 1_790_000_000;
        const int StubX = 8, StubZ = 8, AbsentX = 9, AbsentZ = 8;
        var server = Path.Combine(Path.GetTempPath(), "aubscraft-columns", Guid.NewGuid().ToString("N"));
        var region = Path.Combine(server, "world", "region", "r.0.0.mca");
        (sbyte, string)[] grass = [(-4, "minecraft:stone"), (-3, "minecraft:stone"), (-2, "minecraft:grass_block")];
        (sbyte, string)[] gold = [(-4, "minecraft:gold_block"), (-3, "minecraft:gold_block"), (-2, "minecraft:gold_block")];
        (sbyte, string)[] air = [(-4, "minecraft:air"), (-3, "minecraft:air")];

        IEnumerable<RegionChunk> World(bool finished)
        {
            for (int z = 0; z < 18; z++)
                for (int x = 0; x < 18; x++)
                {
                    bool stub = x == StubX && z == StubZ, absent = x == AbsentX && z == AbsentZ;
                    if (absent && !finished) continue;
                    yield return !finished && stub
                        ? new RegionChunk(x, z, "minecraft:structure_starts", air, T0)
                        : new RegionChunk(x, z, "minecraft:full", stub || absent ? gold : grass, stub || absent ? T0 + 60 : T0);
                }
        }
        RegionFileWriter.Write(region, World(finished: false));

        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH")!;
        await using var rig = await ViewerRig.StartAsync(publish, server);
        var page = await rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            // High over the middle of the hole, looking straight down: the top of a section -2 is y -17
            await page.GotoAsync($"{rig.BaseUrl}/map?hud=0&time=6000&dd=12&light=0&cam={(StubX + 1) * 16},70,{StubZ * 16 + 8},-89,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 20, quietSeconds: 4, maxSeconds: 90);
            await Task.Delay(3000);

            var before = Path.Combine(_outDir, "generated-before.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = before });
            int goldBefore = Gold(before);

            // The game finishes the two chunks
            RegionFileWriter.Write(region, World(finished: true));

            // The panel looks over the files every 5 s; then the notes, the fetches and the meshes
            var after = Path.Combine(_outDir, "generated-after.png");
            int goldAfter = 0;
            for (int i = 0; i < 25 && goldAfter < goldBefore + 2800; i++)
            {
                await Task.Delay(1000);
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = after });
                goldAfter = Gold(after);
            }
            TestContext.Progress.WriteLine($"[columns] gold pixels: before {goldBefore}, after {goldAfter}");
            Assert.That(goldBefore, Is.LessThan(50), "the hole is there to begin with");
            Assert.That(goldAfter, Is.GreaterThan(goldBefore + 2800), "both chunks appeared without a reload");
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-generated.log"), log); } catch { }
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Fly over the real world until columns have gone out of the full-detail range, then stop: the far chunks must have taken over, with nothing left undrawn while the camera holds still. Before the
    /// fix the far chunk under a column was skipped while the column stood, and looked at again only when the camera changed chunk.
    /// </summary>
    [Test]
    public async Task AFlightThenAStop_LeavesNoColumnUndrawn()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => log.Enqueue(m.Text);
        page.Worker += (_, w) => w.Console += (_, m) => log.Enqueue(m.Text);
        try
        {
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&dd=30&cam=-400,140,-300,-30,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 180_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 10, maxSeconds: 180);

            // The mouse wanders over the page first, as anyone's does: none of that may turn the view when the camera takes the mouse
            await page.Mouse.MoveAsync(100, 100);
            await page.Mouse.MoveAsync(1100, 600, new MouseMoveOptions { Steps = 10 });
            await ViewerRig.TakeTheMouseAsync(page, 640, 500);
            await page.Keyboard.DownAsync("KeyW");
            await page.Keyboard.DownAsync("ShiftRight");   // nothing: only W flies; the right shift is not the descend key
            await Task.Delay(12000);
            await page.Keyboard.UpAsync("ShiftRight");
            await page.Keyboard.UpAsync("KeyW");
            var moved = await ViewerStats.ReadAsync(page);
            TestContext.Progress.WriteLine($"[columns] stopped at {moved?.X} {moved?.Y} {moved?.Z}");
            Assert.That(moved!.X, Is.GreaterThan(-400 + 16 * 6), "the camera really flew far enough for columns to leave the range");
            Assert.That(moved.Z, Is.EqualTo(-300).Within(2), "straight on along the way it looked: the view did not turn when the camera took the mouse");

            // Hold still; the [Voxel] line comes every 5 s. Every gap must close while nothing moves
            string last = "";
            int gaps = -1;
            for (int i = 0; i < 90 && gaps != 0; i++)
            {
                await Task.Delay(2000);
                last = log.LastOrDefault(l => GapLine.IsMatch(l)) ?? "";
                var m = GapLine.Match(last);
                if (m.Success) gaps = int.Parse(m.Groups["n"].Value);
            }
            var shot = Path.Combine(_outDir, "after-flight.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = shot });
            TestContext.Progress.WriteLine($"[columns] after the stop: {last}");
            Assert.That(gaps, Is.EqualTo(0), "columns undrawn with the camera still: " + last);
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-flight.log"), log); } catch { }
            await page.CloseAsync();
        }
    }

    /// <summary>A fresh panel and a fresh page at the place of TJ's screenshot: what the viewer draws when nothing has gone stale. Not a gate: its shot is for looking at.</summary>
    [Test, Explicit("needs the real world; the shot is for looking at")]
    public async Task TheReportedPlace_FreshPanelAndPage()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => log.Enqueue(m.Text);
        page.Worker += (_, w) => w.Console += (_, m) => log.Enqueue(m.Text);
        try
        {
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&dd=12&cam=-455,137,-421,-38,249", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 180_000 });
            var (stats, settled) = await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 15, maxSeconds: 180);
            TestContext.Progress.WriteLine($"[columns] settled={settled}, {stats.Fps} FPS, {stats.VisibleSections}/{stats.TotalSections} sections, draw {stats.DrawDistance}, at {stats.X:F0} {stats.Y:F0} {stats.Z:F0}");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "reported-place-fresh.png") });
            foreach (var line in log.Where(l => Regex.IsMatch(l, @"Column|far chunk|dropped|failed|HTTP|gaps", RegexOptions.IgnoreCase)).TakeLast(30))
                TestContext.Progress.WriteLine("[columns] " + line);
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-reported-place.log"), log); } catch { }
            await page.CloseAsync();
        }
    }
}
