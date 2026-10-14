using System.Drawing;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The GPU HUD (SpawnDev.GameUI in the render worker), checked on the real pixels of the real viewer: it is drawn on top of
/// the world, F3 toggles the debug overlay, and taking the pointer lock swaps the hint for a crosshair. The page forwards the
/// input; the worker, which has no DOM, draws and reacts - so each test here crosses the main thread -> worker input path.
///
/// The control is the SAME scene without a HUD: the viewer is opened first with <c>/map?hud=0</c>, then normally, from the same
/// default start, and every "the HUD is drawn" assertion is a difference between the two frames. A threshold on one frame alone
/// is not evidence: terrain can be dark, bright or textured wherever a panel happens to sit.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      run name; shots land in screenshots/viewer/{tag}-hud/ (default "run")
/// </summary>
[NonParallelizable]
public class ViewerHudTests
{
    private ViewerRig _rig = null!;
    private IPage _page = null!;
    private string _outDir = null!;
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _console = [];
    private (Pixels.Region_ Panel, Pixels.Region_ Compass) _noHud;

    // The stats panel sits top-left, 8 px in; the compass bar top-centre, 8 px down, 420 wide
    private static (Pixels.Region_ Panel, Pixels.Region_ Compass) Measure(Bitmap canvas) =>
        (Pixels.Region(canvas, 14, 14, 240, 60), Pixels.Region(canvas, canvas.Width / 2 - 190, 12, 380, 20));

    [OneTimeSetUp]
    public async Task OpenViewerAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "run";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag + "-hud");
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);

        try
        {
            // The control: the default start with the HUD hidden by the link
            var controlPage = await OpenAsync("/map?hud=0");
            try
            {
                var path = Path.Combine(_outDir, "00-control-hud-off.png");
                await controlPage.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = path });
                using var bmp = new Bitmap(path);
                _noHud = Measure(bmp);
                TestContext.Progress.WriteLine($"[hud] control (hud=0): panel corner {_noHud.Panel.Bright} bright px, compass band luma {_noHud.Compass.Luma:F0} spread {_noHud.Compass.LumaStdDev:F1}");
            }
            finally { await controlPage.CloseAsync(); }

            // AUBS_VIEWER_MAP_PATH exists for the red-check: point the tests at /map?hud=0 and every HUD assertion must fail
            _page = await OpenAsync(Environment.GetEnvironmentVariable("AUBS_VIEWER_MAP_PATH") ?? "/map");
        }
        catch
        {
            // The page's own account of why it did not start: its console, and the server's
            string[] lines;
            lock (_console) lines = _console.Where(l => !l.Contains("[DIAG]")).TakeLast(60).ToArray();
            TestContext.Progress.WriteLine("[hud] viewer did not start. Page console (last 60, [DIAG] lines dropped):\n" + string.Join("\n", lines));
            TestContext.Progress.WriteLine("[hud] server log (last 25 lines):\n" + string.Join("\n", _rig.ServerLog.Split('\n').TakeLast(25)));
            throw;
        }
    }

    /// <summary>Opens the viewer at <paramref name="path"/> (default start unless the link says otherwise) and waits until it has some terrain.</summary>
    private async Task<IPage> OpenAsync(string path)
    {
        var page = await _rig.Context.NewPageAsync();
        page.Console += (_, m) =>
        {
            lock (_console) { if (_console.Count < 600) _console.Add($"[{m.Type}] {m.Text}"); }
            if (m.Type == "error") _consoleErrors.Add(m.Text);
        };
        page.PageError += (_, e) => { lock (_console) _console.Add("[pageerror] " + e); _consoleErrors.Add(e); };

        await page.GotoAsync(_rig.BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });

        // Terrain is not the subject here: wait until the viewer is up and has some, plus a few HUD text refreshes.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        ViewerStats? stats = null;
        while (DateTime.UtcNow < deadline)
        {
            await ViewerStats.FailIfTheViewerFailedAsync(page);
            stats = await ViewerStats.ReadAsync(page);
            if (stats is { TotalSections: >= 150, Fps: > 0 }) break;
            await Task.Delay(1000);
        }
        Assert.That(stats, Is.Not.Null.And.Property("TotalSections").GreaterThanOrEqualTo(150), $"The viewer at {path} started and streamed terrain");
        await Task.Delay(3000);
        return page;
    }

    [OneTimeTearDown]
    public async Task CloseViewerAsync()
    {
        try { if (_page != null) await _page.CloseAsync(); } catch { }
        if (_rig != null) await _rig.DisposeAsync();
    }

    private async Task<Bitmap> ShotAsync(string name)
    {
        var path = Path.Combine(_outDir, name + ".png");
        await _page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = path });
        return new Bitmap(path);
    }

    [Test, Order(1)]
    public async Task Hud_StatsPanelAndCompass_AreDrawnOnTopOfTheWorld()
    {
        using var shot = await ShotAsync("01-hud");
        var (panel, compass) = Measure(shot);
        TestContext.Progress.WriteLine($"[hud] with HUD: panel corner {panel.Bright} bright px; compass band luma {compass.Luma:F0} spread {compass.LumaStdDev:F1}");

        // Control sanity: with hud=0 the panel's corner of this scene has no text in it (it is forest here)
        Assert.That(_noHud.Panel.Bright, Is.LessThanOrEqualTo(40), "the control frame (hud=0) has no HUD text in the panel corner");

        // Stats panel: four lines of white text that the same scene does not have
        Assert.That(panel.Bright - _noHud.Panel.Bright, Is.GreaterThan(40), "white text is drawn in the stats panel");

        // Compass: a dark bar over the control's brighter band, with marker text in it (spread: text on a bar vs a flat band)
        Assert.That(_noHud.Compass.Luma - compass.Luma, Is.GreaterThanOrEqualTo(25), "the compass bar darkens the band it sits on");
        Assert.That(compass.LumaStdDev - _noHud.Compass.LumaStdDev, Is.GreaterThan(10), "and its markers are drawn on it");

        // The world is still there around the HUD: the middle of the frame is textured terrain, not a flat fill
        var world = Pixels.Region(shot, shot.Width / 2 - 150, shot.Height / 2 - 100, 300, 200);
        TestContext.Progress.WriteLine($"[hud] world behind the HUD: luma spread {world.LumaStdDev:F1}");
        Assert.That(world.LumaStdDev, Is.GreaterThan(15), "terrain is visible in the middle of the frame (the HUD is an overlay, not the frame)");

        Assert.That(_consoleErrors, Is.Empty, "GameUI initialised in the worker without errors");
    }

    [Test, Order(2)]
    public async Task F3_TogglesTheDebugOverlay_UnderThePanel()
    {
        // Keys the canvas sees go to the worker's HUD: focus it, press F3.
        await _page.Locator("#map-canvas").FocusAsync();

        // Under the stats panel (panel is ~90 px tall at y 8): where the overlay goes. Same camera in all three shots, so the
        // terrain there is the same; only the overlay changes it - darker (a 70% black panel) and with white text.
        const int x = 14, y = 118, w = 200, h = 50;
        Pixels.Region_ before, on, off;
        using (var shot = await ShotAsync("02a-before-f3")) before = Pixels.Region(shot, x, y, w, h);

        await _page.Keyboard.PressAsync("F3");
        await Task.Delay(800);
        using (var shot = await ShotAsync("02b-f3-on")) on = Pixels.Region(shot, x, y, w, h);

        await _page.Keyboard.PressAsync("F3");
        await Task.Delay(800);
        using (var shot = await ShotAsync("02c-f3-off")) off = Pixels.Region(shot, x, y, w, h);

        TestContext.Progress.WriteLine($"[hud] under the panel: luma {before.Luma:F0} / {on.Luma:F0} (F3 on) / {off.Luma:F0} (F3 off); " +
            $"bright px {before.Bright} / {on.Bright} / {off.Bright}");
        Assert.That(on.Luma, Is.LessThan(before.Luma * 0.6), "F3 darkens that area (the debug overlay's panel)");
        Assert.That(on.Bright - before.Bright, Is.GreaterThanOrEqualTo(20), "and writes white text in it");
        Assert.That(off.Luma, Is.GreaterThan(on.Luma * 1.4), "a second F3 hides it again");
        Assert.That(_consoleErrors, Is.Empty);
    }

    [Test, Order(3)]
    public async Task ClickingTheView_LocksThePointer_SwapsTheHintForACrosshair()
    {
        var canvas = _page.Locator("#map-canvas");
        var box = (await canvas.BoundingBoxAsync())!;
        int cx = (int)(box.Width / 2), cy = (int)(box.Height / 2);

        // Same camera before and after the click, so only the HUD differs: crosshair at the centre, hint along the bottom
        Pixels.Region_ crossBefore, hintBefore, crossLocked, hintLocked;
        using (var shot = await ShotAsync("03a-unlocked"))
        {
            crossBefore = Pixels.Region(shot, cx - 14, cy - 14, 28, 28, brightLuma: 235);
            hintBefore = Pixels.Region(shot, cx - 250, shot.Height - 36, 500, 28);
        }

        // Chrome drops a pointer lock the moment its window is not the active one, and this gate's window is off-screen: make
        // the tab the active one first.
        await _page.BringToFrontAsync();
        await canvas.ClickAsync(); // a real click: the page asks for the pointer lock
        await TestUtilWait(async () => (await ViewerStats.ReadAsync(_page)) is { ClickToFly: false },
            TimeSpan.FromSeconds(10), "the page reports the pointer locked (no more 'Click to fly')");
        await Task.Delay(800);
        // ...and it is still held after the HUD had time to react: a lock that was granted and then lost reads the same
        // in the first poll and would otherwise show up only as a missing crosshair.
        Assert.That((await ViewerStats.ReadAsync(_page))?.ClickToFly, Is.False,
            "the pointer lock is still held 800 ms after the click (it was granted and then lost: the window is not active)");

        using (var shot = await ShotAsync("03b-locked"))
        {
            crossLocked = Pixels.Region(shot, cx - 14, cy - 14, 28, 28, brightLuma: 235);
            hintLocked = Pixels.Region(shot, cx - 250, shot.Height - 36, 500, 28);
        }
        TestContext.Progress.WriteLine($"[hud] crosshair white px {crossBefore.Bright} -> {crossLocked.Bright}; hint bright px {hintBefore.Bright} -> {hintLocked.Bright}");
        Assert.That(crossLocked.Bright - crossBefore.Bright, Is.GreaterThanOrEqualTo(20), "the crosshair appears once the camera owns the pointer");
        Assert.That(hintBefore.Bright, Is.GreaterThanOrEqualTo(30), "the hint was drawn before the lock");
        Assert.That(hintLocked.Bright, Is.LessThanOrEqualTo(hintBefore.Bright * 0.3), "and is gone after it");
        Assert.That(_consoleErrors, Is.Empty);
    }

    private static async Task TestUtilWait(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        Assert.Fail($"Timed out after {timeout.TotalSeconds:F0}s waiting for: {because}");
    }
}

/// <summary>Pixel statistics over a rectangle of a screenshot.</summary>
internal static class Pixels
{
    public readonly record struct Region_(double Luma, double R, double G, double B, int Bright, double LumaStdDev);

    /// <summary>Mean luma and colour over the rectangle, its luma spread, and how many pixels are brighter than <paramref name="brightLuma"/>.</summary>
    public static Region_ Region(Bitmap bmp, int x, int y, int w, int h, int brightLuma = 200)
    {
        x = Math.Clamp(x, 0, bmp.Width - 1);
        y = Math.Clamp(y, 0, bmp.Height - 1);
        w = Math.Min(w, bmp.Width - x);
        h = Math.Min(h, bmp.Height - y);
        double sr = 0, sg = 0, sb = 0, sl = 0, sl2 = 0;
        int bright = 0, n = 0;
        for (int j = y; j < y + h; j++)
            for (int i = x; i < x + w; i++)
            {
                var c = bmp.GetPixel(i, j);
                double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                sr += c.R; sg += c.G; sb += c.B; sl += l; sl2 += l * l;
                if (l > brightLuma) bright++;
                n++;
            }
        double mean = sl / n;
        return new Region_(mean, sr / n, sg / n, sb / n, bright, Math.Sqrt(Math.Max(0, sl2 / n - mean * mean)));
    }
}
