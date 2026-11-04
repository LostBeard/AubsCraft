using System.Globalization;
using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Tests;
using AubsCraft.Live.Model;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The viewer is the whole page and everything about it is in its own menu, drawn on the GPU: TJ's list of 2026-11-04. These press the real menu with the real mouse (the menu says where its buttons are on the
/// console when it opens), on a flat world the test writes itself, with players the test feeds as the plugin does.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_TAG      where the shots go, under screenshots/viewer (default "menu")
/// </summary>
[NonParallelizable]
public class ViewerMenuTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!, _server = null!;
    // The grass of the test's world is the top of section -2: its top face is at y -16
    private const float Ground = -16f;
    private const float CentreX = 144.5f, CentreZ = 144.5f;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "menu");
        Directory.CreateDirectory(_outDir);
        _server = Path.Combine(Path.GetTempPath(), "aubscraft-menu", Guid.NewGuid().ToString("N"));
        var chunks = new List<RegionChunk>();
        for (int z = 0; z < 20; z++)
            for (int x = 0; x < 20; x++)
                chunks.Add(new RegionChunk(x, z, "minecraft:full", [(-4, "minecraft:stone"), (-3, "minecraft:stone"), (-2, "minecraft:grass_block")], 1_790_000_000));
        RegionFileWriter.Write(Path.Combine(_server, "world", "region", "r.0.0.mca"), chunks);
        _rig = await ViewerRig.StartAsync(publish!, _server, secondServerId: "spooky");
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private sealed class Viewer(IPage page, List<string> errors, System.Collections.Concurrent.ConcurrentQueue<string> log)
    {
        public readonly IPage Page = page;
        public readonly List<string> Errors = errors;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Log = log;
    }

    private async Task<Viewer> OpenAsync(string query, ViewerRig? on = null)
    {
        var rig = on ?? _rig;
        var page = await rig.Context.NewPageAsync();
        var errors = new List<string>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void OnLine(IConsoleMessage m)
        {
            log.Enqueue(m.Text);
            if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text);
        }
        page.Console += (_, m) => OnLine(m);
        page.Worker += (_, w) => w.Console += (_, m) => OnLine(m);
        page.PageError += (_, e) => errors.Add(e);
        await page.GotoAsync($"{rig.BaseUrl}/map?{query}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
        await ViewerShotTests.WaitForSettledAsync(page, minSections: 10, quietSeconds: 3, maxSeconds: 60);
        return new Viewer(page, errors, log);
    }

    private static async Task PressAsync(IPage page, float x, float y)
    {
        await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = 3 });
        await Task.Delay(120);
        await page.Mouse.DownAsync();
        await Task.Delay(100);
        await page.Mouse.UpAsync();
        await Task.Delay(400);
    }

    // The newest line the menu logged after opening: its buttons by id
    private static Dictionary<string, (float X, float Y, float W, float H)> MenuLayout(Viewer v)
    {
        var line = v.Log.LastOrDefault(l => l.StartsWith("[HudMenu]", StringComparison.Ordinal)) ?? throw new AssertionException("The menu did not say where its buttons are: it did not open");
        var map = new Dictionary<string, (float, float, float, float)>();
        foreach (Match m in Regex.Matches(line, @"(\w+)=(-?[\d.]+),(-?[\d.]+),([\d.]+),([\d.]+)"))
            map[m.Groups[1].Value] = (F(m.Groups[2].Value), F(m.Groups[3].Value), F(m.Groups[4].Value), F(m.Groups[5].Value));
        return map;
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    }

    // Opens the menu with its own button (top left, right of the stats), and gives the menu's layout
    private static async Task<Dictionary<string, (float X, float Y, float W, float H)>> OpenMenuAsync(Viewer v)
    {
        int before = v.Log.Count(l => l.StartsWith("[HudMenu]", StringComparison.Ordinal));
        await PressAsync(v.Page, 8 + 270 + 6 + 29, 8 + 14);
        for (int i = 0; i < 20 && v.Log.Count(l => l.StartsWith("[HudMenu]", StringComparison.Ordinal)) == before; i++) await Task.Delay(100);
        Assert.That(v.Log.Count(l => l.StartsWith("[HudMenu]", StringComparison.Ordinal)), Is.GreaterThan(before), "the Menu button opens the menu");
        TestContext.Progress.WriteLine("[menu] " + v.Log.Last(l => l.StartsWith("[HudMenu]", StringComparison.Ordinal)));
        return MenuLayout(v);
    }

    private static Task PressMenuAsync(Viewer v, Dictionary<string, (float X, float Y, float W, float H)> menu, string id)
    {
        Assert.That(menu.ContainsKey(id), "The menu has no " + id + ": " + string.Join(' ', menu.Keys));
        var b = menu[id];
        return PressAsync(v.Page, b.X + b.W / 2f, b.Y + b.H / 2f);
    }

    [Test]
    public async Task TheViewer_IsTheWholeWindow_WithNothingOfThePanelAroundIt()
    {
        var v = await OpenAsync("time=6000&dd=8&cam=144,40,144,-60,0");
        try
        {
            var box = (await v.Page.Locator("#map-canvas").BoundingBoxAsync())!;
            var size = v.Page.ViewportSize!;
            Assert.That((box.X, box.Y, box.Width, box.Height), Is.EqualTo(((double)0, (double)0, (double)size.Width, (double)size.Height)), "the canvas fills the window");
            Assert.That(await v.Page.Locator("h1").CountAsync(), Is.Zero, "no heading");
            Assert.That(await v.Page.Locator(".sidebar").CountAsync(), Is.Zero, "no panel menu");
            var seen = await v.Page.EvaluateAsync<string[]>("[...document.querySelectorAll('button,a,select,h1')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 1 && r.height > 1 && !e.closest('.visually-hidden'); }).map(e => e.tagName + ':' + e.textContent)");
            Assert.That(seen, Is.Empty, "nothing outside the viewer's own UI");
            Assert.That(await v.Page.EvaluateAsync<bool>("document.documentElement.scrollHeight <= window.innerHeight"), Is.True, "and no page to scroll");
            await v.Page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "whole-window.png") });
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally { await v.Page.CloseAsync(); }
    }

    [Test]
    public async Task TheMenu_ChangesTheViewer_AndRemembersIt()
    {
        var v = await OpenAsync("time=6000&dd=8&cam=144,40,144,-60,0");
        try
        {
            var menu = await OpenMenuAsync(v);
            await v.Page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "menu-open.png") });
            Assert.That(await v.Page.EvaluateAsync<bool>("document.pointerLockElement !== null"), Is.False, "pressing the menu does not take the mouse for the camera");

            // Lighting: the page's own setting, the button says so
            Assert.That(await v.Page.GetByRole(AriaRole.Button, new() { Name = "Lighting: On" }).CountAsync(), Is.EqualTo(1));
            await PressMenuAsync(v, menu, "lighting");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Lighting: Off" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });
            await PressMenuAsync(v, menu, "lighting");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Lighting: On" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });

            // A setting of the HUD: remembered in the browser
            await PressMenuAsync(v, menu, "stats");
            await PressMenuAsync(v, menu, "bars");
            string? saved = null;
            for (int i = 0; i < 30 && saved?.Contains("\"PlayerBars\":false") != true; i++)
            {
                await Task.Delay(100);
                saved = await v.Page.EvaluateAsync<string?>("localStorage.getItem('aubscraft.viewer')");
            }
            Assert.That(saved, Does.Contain("\"Stats\":false").And.Contain("\"PlayerBars\":false"));
            // The next visit reads it: back on, so a test after this one finds the defaults
            await PressMenuAsync(v, menu, "stats");
            await PressMenuAsync(v, menu, "bars");

            // A click away from the menu closes it: the next click on the view then takes the mouse
            await PressAsync(v.Page, 100, 600);
            await ViewerRig.TakeTheMouseAsync(v.Page, 100, 600);
            await v.Page.EvaluateAsync("document.exitPointerLock()");
            await Task.Delay(1500);
            // A slider dragged off the panel does not
            var dragMenu = await OpenMenuAsync(v);
            var dragged = dragMenu["fovSlider"];
            await v.Page.Mouse.MoveAsync(dragged.X + dragged.W / 2f, dragged.Y + 28f);
            await Task.Delay(100);
            await v.Page.Mouse.DownAsync();
            await v.Page.Mouse.MoveAsync(1100, dragged.Y + 28f, new MouseMoveOptions { Steps = 5 });
            await Task.Delay(100);
            await v.Page.Mouse.UpAsync();
            await Task.Delay(300);
            await v.Page.Mouse.ClickAsync(640, 300);   // on the panel: the menu is still there to take it, so no lock
            await Task.Delay(300);
            Assert.That(await v.Page.EvaluateAsync<bool>("document.pointerLockElement !== null"), Is.False, "the menu stayed open through the drag");

            // Tab closes it (the mouse is back over the world: a click flies)
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(300);
            await ViewerRig.TakeTheMouseAsync(v.Page, 640, 500);
            // Tab from flying opens it and gives the mouse back at once
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(500);
            Assert.That(await v.Page.EvaluateAsync<bool>("document.pointerLockElement !== null"), Is.False, "the menu needs the mouse");
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally
        {
            try { await v.Page.EvaluateAsync("localStorage.removeItem('aubscraft.viewer')"); } catch { }
            await v.Page.CloseAsync();
        }
    }

    /// <summary>
    /// TJ 2026-11-04: pressing Shading in the menu froze the whole UI ("It will not go away now and nothign works ... tab does nothing") with the world still drawing behind it. The page ran the menu's
    /// commands inside its input loop and waited for each to end, and changing the shading meshed everything loaded. Whatever a command costs, the menu goes on answering.
    /// </summary>
    [Test]
    public async Task EveryButtonThatTakesAWhile_LeavesTheMenuAnswering()
    {
        // The real world: thousands of chunks, as TJ had (the test's small world meshes again in a moment, and passed on the frozen build)
        await using var rig = await ViewerRig.StartAsync(Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH")!, Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server");
        var v = await OpenAsync("time=6000&cam=-455,137,-421,-38,249", rig);
        try
        {
            await Task.Delay(30000);   // the stream well on: plenty to mesh again
            var menu = await OpenMenuAsync(v);
            await PressMenuAsync(v, menu, "shading");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Tab closes it and the Menu button opens it again: each goes through the page's input loop
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(150);
            var again = await OpenMenuAsync(v);
            TestContext.Progress.WriteLine($"[menu] after Shading the menu closed and opened again in {clock.ElapsedMilliseconds} ms");
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(3000), "the UI goes on answering");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Shading: Flat" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });
            await PressMenuAsync(v, again, "shading");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Shading: Soft" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally
        {
            try { await v.Page.EvaluateAsync("localStorage.removeItem('aubscraft.ao')"); } catch { }
            await v.Page.CloseAsync();
        }
    }

    /// <summary>
    /// Every other button of the menu, pressed with the mouse, each checked for what it does, on the real world, and the menu still answering after each one. TJ found the Shading freeze by pressing a button no
    /// gate had pressed at real size. Skipped here because they have gates of their own: lighting, shading, fullscreen, the servers, the sliders; bluemap needs a BlueMap the rig does not have; vr and ar need a headset (ViewerXrTests).
    /// </summary>
    [Test]
    public async Task EveryOtherButton_DoesWhatItSays_AndTheMenuGoesOnAnswering()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH")!;
        await using var rig = await ViewerRig.StartAsync(publish, Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server");
        // Edit mode asks whether the server has the plugin: a stand-in answers, as it does for the edit gates
        var plugin = new FakePlugin(rig);
        plugin.Start();
        await plugin.PostWorldAsync();
        var v = await OpenAsync("time=6000&dd=12&cam=-455,137,-421,-38,249&hudfeed=demo", rig);
        var canvas = v.Page.Locator("#map-canvas");
        var results = new List<string>();
        try
        {
            await Task.Delay(15000);
            async Task<string> ShotAsync(string name)
            {
                await Task.Delay(700);
                var path = Path.Combine(_outDir, $"button-{name}.png");
                await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = path });
                return path;
            }
            // How much a corner of the view changed: the HUD's parts sit in the corners, away from the world's movement
            static double CornerChange(string first, string second, int x, int y, int w, int h)
            {
                using var a = new System.Drawing.Bitmap(first);
                using var b = new System.Drawing.Bitmap(second);
                int changed = 0, total = 0;
                for (int py = y; py < y + h; py += 2)
                    for (int px = x; px < x + w; px += 2)
                    {
                        var c = a.GetPixel(px, py);
                        var d = b.GetPixel(px, py);
                        total++;
                        if (Math.Abs(c.R - d.R) + Math.Abs(c.G - d.G) + Math.Abs(c.B - d.B) > 40) changed++;
                    }
                return changed / (double)total;
            }
            async Task PressThenCloseAsync(string id)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                TestContext.Progress.WriteLine($"[menu] pressing {id}");
                File.WriteAllLines(Path.Combine(_outDir, "console-buttons.log"), v.Log.ToArray());
                var menu = await OpenMenuAsync(v);
                await PressMenuAsync(v, menu, id);
                if (await v.Page.EvaluateAsync<bool>("document.pointerLockElement !== null")) Assert.Fail(id + " took the mouse for the camera");
                await v.Page.Keyboard.PressAsync("Tab");
                await Task.Delay(300);
                results.Add($"{id} {clock.ElapsedMilliseconds} ms");
                TestContext.Progress.WriteLine($"[menu] pressed {id}: {clock.ElapsedMilliseconds} ms");
                Assert.That(clock.ElapsedMilliseconds, Is.LessThan(4000), id + ": the menu goes on answering");
            }
            // What each HUD toggle takes off the screen: where it sits on a 1280 x 720 window
            async Task HudToggleAsync(string id, int x, int y, int w, int h)
            {
                var on = await ShotAsync(id + "-on");
                await PressThenCloseAsync(id);
                var off = await ShotAsync(id + "-off");
                double gone = CornerChange(on, off, x, y, w, h);
                await PressThenCloseAsync(id);
                var back = await ShotAsync(id + "-back");
                double returned = CornerChange(on, back, x, y, w, h);
                results.Add($"{id}: {gone:P0} of its place changed, {returned:P0} after turning it back on");
                Assert.That(gone, Is.GreaterThan(0.2), id + " goes from the screen");
                Assert.That(returned, Is.LessThan(gone / 2), id + " comes back");
            }

            await HudToggleAsync("stats", 8, 8, 268, 92);
            await HudToggleAsync("compass", 430, 8, 420, 28);
            await HudToggleAsync("minimap", 1096, 8, 176, 210);
            await HudToggleAsync("players", 1084, 228, 186, 100);
            // The demo's lines have faded by now (chat is a feed): a fresh one to take off the screen. The demo echoes what is typed.
            await v.Page.Mouse.MoveAsync(640, 400);
            await v.Page.Keyboard.PressAsync("Enter");
            await Task.Delay(300);
            await v.Page.Keyboard.TypeAsync("a line to see go");
            await v.Page.Keyboard.PressAsync("Enter");
            await Task.Delay(500);
            await v.Page.Keyboard.PressAsync("Escape");   // the input closes: the feed is what is compared
            await Task.Delay(500);
            await HudToggleAsync("chat", 8, 636, 380, 50);

            // The link's fixed draw distance (dd=12) has survived all those settings being changed
            await Task.Delay(1500);
            Assert.That((await ViewerStats.ReadAsync(v.Page))?.DrawDistance, Is.EqualTo(12), "changing another setting must not undo the link's draw distance");

            // Copy link: says where the camera is
            int linksBefore = v.Log.Count(l => l.Contains("Link to this place"));
            await PressThenCloseAsync("copylink");
            for (int i = 0; i < 30 && v.Log.Count(l => l.Contains("Link to this place")) == linksBefore; i++) await Task.Delay(100);
            var link = v.Log.LastOrDefault(l => l.Contains("Link to this place")) ?? "";
            Assert.That(link, Does.Contain("/map?cam=-455.0,137.0,-421.0"), "the link names this place");

            // Clear cache: the page says so, and nothing breaks
            int cleared = v.Log.Count(l => l.Contains("cache cleared"));
            await PressThenCloseAsync("clearcache");
            for (int i = 0; i < 30 && v.Log.Count(l => l.Contains("cache cleared")) == cleared; i++) await Task.Delay(100);
            Assert.That(v.Log.Count(l => l.Contains("cache cleared")), Is.GreaterThan(cleared), "the cache was cleared");

            // Edit: an admin's mode goes on and off, from the menu
            await PressThenCloseAsync("edit");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Edit: On" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });
            await PressThenCloseAsync("edit");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Edit: Off" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });

            // The world...: the menu closes and the world panel opens, which needs the mouse as a menu does (a click stays the HUD's)
            var worldMenu = await OpenMenuAsync(v);
            var worldBefore = await ShotAsync("world-before");
            await PressMenuAsync(v, worldMenu, "world");
            var worldPanel = await ShotAsync("world-panel");
            Assert.That(Compare(worldBefore, worldPanel).Difference, Is.GreaterThan(1), "the menu went and the world panel came");
            await v.Page.Keyboard.PressAsync("KeyO");   // closes the world panel
            await Task.Delay(300);

            // Voice: the page connects, and the Mic button appears in the menu; the fake microphone of the rig opens and closes
            await PressThenCloseAsync("voice");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Voice: On" }).WaitForAsync(new() { Timeout = 10000, State = WaitForSelectorState.Attached });
            await Task.Delay(500);
            var voiceMenu = await OpenMenuAsync(v);
            Assert.That(voiceMenu.ContainsKey("mic"), "the Mic button is offered once the voice is on");
            await PressMenuAsync(v, voiceMenu, "mic");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Mic: On" }).WaitForAsync(new() { Timeout = 10000, State = WaitForSelectorState.Attached });
            await PressMenuAsync(v, voiceMenu, "mic");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Mic: Off" }).WaitForAsync(new() { Timeout = 10000, State = WaitForSelectorState.Attached });
            await PressMenuAsync(v, voiceMenu, "voice");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Voice: Off" }).WaitForAsync(new() { Timeout = 10000, State = WaitForSelectorState.Attached });
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(300);

            // Names: no player near in this scene to read a tag from: the setting goes and comes (ThePlayers_ checks what it does)
            await PressThenCloseAsync("tags");
            await PressThenCloseAsync("tags");

            // Admin panel: out to the panel, last, as it leaves the page
            var last = await OpenMenuAsync(v);
            await PressMenuAsync(v, last, "panel");
            for (int i = 0; i < 50 && v.Page.Url.Contains("/map"); i++) await Task.Delay(100);
            Assert.That(v.Page.Url, Does.Not.Contain("/map"), "the Admin panel button leaves the viewer");
            await v.Page.Locator(".sidebar").WaitForAsync(new() { Timeout = 10000 });

            TestContext.Progress.WriteLine("[menu] buttons: " + string.Join("; ", results));
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally
        {
            TestContext.Progress.WriteLine("[menu] so far: " + string.Join("; ", results));
            await plugin.StopAsync();
            try { await v.Page.EvaluateAsync("localStorage.removeItem('aubscraft.viewer')"); } catch { }
            await v.Page.CloseAsync();
        }
    }

    /// <summary>The menu's sliders, dragged with the real mouse: what each one sets is what the viewer then does, and it is remembered.</summary>
    [Test]
    public async Task TheSliders_ChangeTheViewer()
    {
        var v = await OpenAsync("time=6000&dd=8&cam=144,40,144,-30,0");
        var canvas = v.Page.Locator("#map-canvas");
        async Task SetAsync(string slider, float fraction)
        {
            var menu = await OpenMenuAsync(v);
            var s = menu[slider];
            await PressAsync(v.Page, s.X + s.W * fraction, s.Y + 28f);
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(400);
        }
        async Task<System.Text.Json.JsonElement> SavedAsync()
        {
            string? saved = null;
            for (int i = 0; i < 30 && saved == null; i++)
            {
                await Task.Delay(100);
                saved = await v.Page.EvaluateAsync<string?>("localStorage.getItem('aubscraft.viewer')");
            }
            Assert.That(saved, Is.Not.Null, "the setting is remembered");
            return System.Text.Json.JsonDocument.Parse(saved!).RootElement;
        }
        try
        {
            await Task.Delay(2000);
            var before = Path.Combine(_outDir, "fov-before.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = before });

            // Field of view: the far end, and the world is seen wider
            await SetAsync("fovSlider", 0.98f);
            Assert.That((await SavedAsync()).GetProperty("Fov").GetSingle(), Is.GreaterThan(100f));
            await Task.Delay(800);
            var wide = Path.Combine(_outDir, "fov-wide.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = wide });
            double change = Compare(before, wide).Difference;
            TestContext.Progress.WriteLine($"[menu] the widest field of view changes the view by {change:F1} of 255");
            Assert.That(change, Is.GreaterThan(2), "the view is wider");

            // Flying speed: near the slow end, and a second of flying covers about that much
            await SetAsync("speedSlider", 0.05f);
            float speed = (await SavedAsync()).GetProperty("FlySpeed").GetSingle();
            Assert.That(speed, Is.LessThan(40f), "the slider's low end");
            await Task.Delay(1500);
            var from = (await ViewerStats.ReadAsync(v.Page))!;
            await ViewerRig.TakeTheMouseAsync(v.Page, 640, 500);
            await v.Page.Keyboard.DownAsync("KeyW");
            await Task.Delay(3000);
            await v.Page.Keyboard.UpAsync("KeyW");
            await Task.Delay(1500);
            var to = (await ViewerStats.ReadAsync(v.Page))!;
            double flown = to.X - from.X;
            TestContext.Progress.WriteLine($"[menu] at {speed:F0} blocks a second, 3 s of flying covered {flown:F0} blocks");
            Assert.That(flown, Is.InRange(speed * 3 * 0.5, speed * 3 * 1.6), "the camera flies at that speed");
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally
        {
            try { await v.Page.EvaluateAsync("localStorage.removeItem('aubscraft.viewer')"); } catch { }
            await v.Page.CloseAsync();
        }
    }

    /// <summary>
    /// BlueMap from the menu and back. The canvas's drawing surface went to the render worker when the page opened and cannot be given again, so the canvas must stay in the page: going back used to leave a dead one,
    /// and the view must be alive again afterwards, not only on the screen.
    /// </summary>
    [Test]
    public async Task BlueMap_AndBack_LeavesTheViewerAlive()
    {
        Environment.SetEnvironmentVariable("BlueMap__Enabled", "true");
        Environment.SetEnvironmentVariable("BlueMap__Url", "about:blank");
        ViewerRig rig;
        try { rig = await ViewerRig.StartAsync(Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH")!, _server); }
        finally
        {
            Environment.SetEnvironmentVariable("BlueMap__Enabled", null);
            Environment.SetEnvironmentVariable("BlueMap__Url", null);
        }
        await using var _ = rig;
        var v = await OpenAsync("time=6000&dd=8&cam=144,40,144,-30,0", rig);
        var canvas = v.Page.Locator("#map-canvas");
        try
        {
            var menu = await OpenMenuAsync(v);
            Assert.That(menu.ContainsKey("bluemap"), "offered, with BlueMap configured: " + string.Join(' ', menu.Keys));
            await PressMenuAsync(v, menu, "bluemap");
            await v.Page.Locator("iframe").WaitForAsync(new() { Timeout = 5000 });
            Assert.That(await canvas.IsVisibleAsync(), Is.False, "the map shows instead of the viewer");

            await v.Page.GetByRole(AriaRole.Button, new() { Name = "3D Viewer" }).ClickAsync();
            await Task.Delay(1000);
            Assert.That(await v.Page.Locator("iframe").CountAsync(), Is.Zero, "the map has gone");
            Assert.That(await canvas.IsVisibleAsync(), Is.True, "the viewer is back");

            // The keys work at once on coming back (the button that had them is gone): Tab opens the menu, Tab closes it. And the menu closed itself on the way to the map
            Assert.That(await v.Page.EvaluateAsync<bool>("document.activeElement && document.activeElement.id === 'map-canvas'"), Is.True, "the canvas has the keys again");
            var count = v.Log.Count(l => l.StartsWith("[HudMenu]", StringComparison.Ordinal));
            await v.Page.Keyboard.PressAsync("Tab");
            for (int i = 0; i < 20 && v.Log.Count(l => l.StartsWith("[HudMenu]", StringComparison.Ordinal)) == count; i++) await Task.Delay(100);
            Assert.That(v.Log.Count(l => l.StartsWith("[HudMenu]", StringComparison.Ordinal)), Is.GreaterThan(count), "Tab opens the menu");
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(300);

            // Alive: the camera flies and the picture moves with it
            var from = Path.Combine(_outDir, "bluemap-back-from.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = from });
            var start = (await ViewerStats.ReadAsync(v.Page))!;
            await ViewerRig.TakeTheMouseAsync(v.Page, 640, 500);
            await v.Page.Keyboard.DownAsync("KeyW");
            await Task.Delay(2000);
            await v.Page.Keyboard.UpAsync("KeyW");
            await Task.Delay(1500);
            var end = (await ViewerStats.ReadAsync(v.Page))!;
            Assert.That(end.X - start.X, Is.GreaterThan(30), "the camera flies again");
            var to = Path.Combine(_outDir, "bluemap-back-to.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = to });
            double moved = Compare(from, to).Difference;
            TestContext.Progress.WriteLine($"[menu] after BlueMap and back, flying changes the picture by {moved:F1} of 255");
            Assert.That(moved, Is.GreaterThan(1), "and the picture moves: the canvas is the live one");
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally { await v.Page.CloseAsync(); }
    }

    /// <summary>The Shading button in the running viewer: the shadows come and go near the camera without a reload, the meshes made again in the background.</summary>
    [Test]
    public async Task TheShadingButton_ChangesTheRunningView()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH")!;
        await using var rig = await ViewerRig.StartAsync(publish, Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server");
        var v = await OpenAsync("time=6000&dd=6&light=0&cam=-10,-52,30,-20,0", rig);
        var canvas = v.Page.Locator("#map-canvas");
        try
        {
            await Task.Delay(6000);
            var soft = Path.Combine(_outDir, "shading-soft.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = soft });
            var menu = await OpenMenuAsync(v);
            await PressMenuAsync(v, menu, "shading");
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(6000);
            var flat = Path.Combine(_outDir, "shading-flat.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = flat });
            var change = Compare(soft, flat);
            TestContext.Progress.WriteLine($"[menu] shading off changes the view by {change.Difference:F2} of 255");
            Assert.That(change.Difference, Is.GreaterThan(0.3), "the shadows went");
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally
        {
            try { await v.Page.EvaluateAsync("localStorage.removeItem('aubscraft.ao')"); } catch { }
            await v.Page.CloseAsync();
        }
    }

    // How far apart two pictures are in the middle of the view (mean of the colour differences, 0..255), and how bright the first is
    private static (double Difference, double Luma) Compare(string first, string second)
    {
        using var x = new System.Drawing.Bitmap(first);
        using var y = new System.Drawing.Bitmap(second);
        double sum = 0, luma = 0;
        long n = 0;
        for (int py = x.Height / 4; py < x.Height * 3 / 4; py += 3)
            for (int px = x.Width / 4; px < x.Width * 3 / 4; px += 3)
            {
                var c = x.GetPixel(px, py);
                var d = y.GetPixel(px, py);
                sum += (Math.Abs(c.R - d.R) + Math.Abs(c.G - d.G) + Math.Abs(c.B - d.B)) / 3.0;
                luma += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                n++;
            }
        return (sum / n, luma / n);
    }

    /// <summary>
    /// TJ 2026-11-04: "clicking the lighting button appeared to do nothing". In a closed room the light is most of the picture, so the button pressed in the running viewer must change it, and back. The same is
    /// measured in the open by day, where everything is sky-lit, and printed: it says how much a viewer can expect to see there.
    /// </summary>
    [Test]
    public async Task TheLightingButton_ChangesTheRunningView_AndBack()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH")!;
        await using var rig = await ViewerRig.StartAsync(publish, Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server");
        async Task<(double Change, double Back, double Luma)> MeasureAsync(string name, string cam)
        {
            var v = await OpenAsync($"time=6000&dd=6&cam={cam}", rig);
            var canvas = v.Page.Locator("#map-canvas");
            try
            {
                await Task.Delay(8000);   // the light of the columns near the camera comes a little after the columns
                async Task<string> ShotAsync(string step)
                {
                    await Task.Delay(1000);
                    var path = Path.Combine(_outDir, $"lighting-{name}-{step}.png");
                    await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = path });
                    return path;
                }
                async Task PressLightingAsync()
                {
                    var menu = await OpenMenuAsync(v);
                    await PressMenuAsync(v, menu, "lighting");
                    await v.Page.Keyboard.PressAsync("Tab");
                }
                var on = await ShotAsync("on");
                await PressLightingAsync();
                await v.Page.GetByRole(AriaRole.Button, new() { Name = "Lighting: Off" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });
                var off = await ShotAsync("off");
                await PressLightingAsync();
                await v.Page.GetByRole(AriaRole.Button, new() { Name = "Lighting: On" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });
                var back = await ShotAsync("back");
                Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
                var change = Compare(on, off);
                return (change.Difference, Compare(on, back).Difference, change.Luma);
            }
            finally
            {
                try { await v.Page.EvaluateAsync("localStorage.removeItem('aubscraft.lighting')"); } catch { }
                await v.Page.CloseAsync();
            }
        }

        var room = await MeasureAsync("room", "63.6,-58.9,63.7,5,45");
        var open = await MeasureAsync("open", "-10,-52,30,-20,0");
        TestContext.Progress.WriteLine($"[menu] lighting off changes the closed room by {room.Change:F1} of 255 (back on: {room.Back:F1}); the open by day by {open.Change:F1} (back on: {open.Back:F1})");
        Assert.That(room.Change, Is.GreaterThan(8), "in a closed room the button changes the picture");
        Assert.That(room.Back, Is.LessThan(room.Change / 3), "and pressing it again brings the picture back");
    }

    [Test]
    public async Task Fullscreen_FromTheMenu_FillsTheScreen_AndComesBack()
    {
        var v = await OpenAsync("time=6000&dd=8&cam=144,40,144,-60,0");
        try
        {
            var menu = await OpenMenuAsync(v);
            await PressMenuAsync(v, menu, "fullscreen");
            string? element = null;
            for (int i = 0; i < 30 && element == null; i++)
            {
                await Task.Delay(100);
                element = await v.Page.EvaluateAsync<string?>("document.fullscreenElement ? document.fullscreenElement.id : null");
            }
            Assert.That(element, Is.EqualTo("map-canvas"), "the press on the GPU menu, through the worker and back, is still a press to the browser");
            await Task.Delay(800);
            var sizes = await v.Page.EvaluateAsync<int[]>("[document.getElementById('map-canvas').clientWidth, document.getElementById('map-canvas').clientHeight, screen.width, screen.height]");
            var (w, h, sw, sh) = (sizes[0], sizes[1], sizes[2], sizes[3]);
            Assert.That((w, h), Is.EqualTo((sw, sh)), "the canvas is the screen");
            await v.Page.GetByRole(AriaRole.Button, new() { Name = "Fullscreen: On" }).WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Attached });

            // The menu is laid out for the new size: open it again from its new place
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(300);
            var again = await OpenMenuAsync(v);
            await PressMenuAsync(v, again, "fullscreen");
            for (int i = 0; i < 30 && element != null; i++)
            {
                await Task.Delay(100);
                element = await v.Page.EvaluateAsync<string?>("document.fullscreenElement ? document.fullscreenElement.id : null");
            }
            Assert.That(element, Is.Null, "and it comes back");
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally { await v.Page.CloseAsync(); }
    }

    [Test]
    public async Task SwitchingServer_IsDoneInTheViewer()
    {
        var v = await OpenAsync("time=6000&dd=8&cam=144,40,144,-60,0");
        try
        {
            var menu = await OpenMenuAsync(v);
            Assert.That(menu.ContainsKey("server0") && menu.ContainsKey("server1"), "two servers are offered: " + string.Join(' ', menu.Keys));
            await PressMenuAsync(v, menu, "server1");
            string? chosen = null;
            for (int i = 0; i < 50 && chosen != "spooky"; i++)
            {
                await Task.Delay(100);
                if (await v.Page.Locator("select[aria-label='Server']").CountAsync() > 0) chosen = await v.Page.Locator("select[aria-label='Server']").InputValueAsync();
            }
            Assert.That(chosen, Is.EqualTo("spooky"));
            Assert.That(v.Page.Url, Does.Contain("/map"), "still in the viewer");
            // The world comes up again in the same place, for the other server
            for (int i = 0; i < 100 && !v.Log.Any(l => l.Contains("Switching worlds")); i++) await Task.Delay(100);
            Assert.That(v.Log.Any(l => l.Contains("Switching worlds")), Is.True, "the viewer changed world");
            await ViewerShotTests.WaitForSettledAsync(v.Page, minSections: 10, quietSeconds: 3, maxSeconds: 60);
            Assert.That(await v.Page.Locator("#map-canvas").CountAsync(), Is.EqualTo(1));
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));

            // Back, so the next test finds the first server
            await v.Page.Locator("select[aria-label='Server']").SelectOptionAsync("aubscraft");
        }
        finally { await v.Page.CloseAsync(); }
    }

    private static readonly HotbarSlot[] Tools =
    [
        new("diamond_sword", 1), new("diamond_pickaxe", 1), new("oak_planks", 64), new("bread", 12), new("torch", 33), new("water_bucket", 1), new("cobblestone", 48), new("", 0), new("apple", 3),
    ];

    private static byte[] Frame(long tick, float aubsX) => LiveFrameCodec.Write(new LiveFrame
    {
        FullTime = tick, DayTime = 6000,
        Worlds =
        [
            new LiveWorld
            {
                Name = "world",
                Entities =
                [
                    new LiveEntity(21, "player", aubsX, Ground, CentreZ, -90f, 0f, -90f, LiveFlags.Player | LiveFlags.OnGround, 0f, 0f, 0f, null, null),
                    new LiveEntity(22, "player", CentreX + 4f, Ground, CentreZ + 3f, 180f, 0f, 180f, LiveFlags.Player | LiveFlags.OnGround, 0f, 0f, 0f, null, null),
                    new LiveEntity(23, "player", CentreX - 5f, Ground, CentreZ + 12f, 0f, 0f, 0f, LiveFlags.Player | LiveFlags.OnGround, 0f, 0f, 0f, null, null),
                ],
                Players =
                [
                    new LivePlayer(21, "11111111-2222-3333-4444-555555555555", "Aubs", "", false, PlayerBarLayout.Survival, "bread", 15f / 20f,
                        new LivePlayerStatus(15, 20, 0, 7, 8, 11, 3, 30, 0.6f, Tools)),
                    new LivePlayer(22, "21111111-2222-3333-4444-555555555555", "SpudArt", "", false, PlayerBarLayout.Creative, "stone", 1f,
                        new LivePlayerStatus(20, 20, 0, 20, 0, 20, 2, 0, 0f, [new("stone", 64), new("glass", 16), new("gold_block", 64), new("oak_stairs", 8), new("", 0), new("", 0), new("", 0), new("", 0), new("redstone_torch", 2)])),
                    new LivePlayer(23, "31111111-2222-3333-4444-555555555555", "Nikki", "", false, PlayerBarLayout.Adventure, "air", 0.2f,
                        new LivePlayerStatus(4, 20, 6, 20, 20, 20, 0, 5, 0.1f, Tools)),
                ],
            },
        ],
    });

    // Red of the hearts and of the selected things: the grass of this world has none
    private static int Red(string png)
    {
        using var b = new System.Drawing.Bitmap(png);
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                var p = b.GetPixel(x, y);
                if (p.R > 170 && p.G < 60 && p.B < 60) n++;
            }
        return n;
    }

    [Test]
    public async Task ThePlayers_HaveTheirHudOverTheirHeads_AndTheWheelMovesAFollowingCamera()
    {
        long tick = ViewerRig.NextTickBase();
        float aubsX = CentreX;
        using var stop = new CancellationTokenSource();
        var poster = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                int status = await _rig.PostLiveFrameAsync(Frame(tick, aubsX));
                if (status != 204) throw new InvalidOperationException($"The panel answered {status} to a live frame");
                tick += 4;
                await Task.Delay(200);
            }
        });
        // South of Aubs, looking north at them from a little above: the others beside and behind
        var v = await OpenAsync($"hudfeed=demo&time=6000&dd=8&cam={F(CentreX)},{F(Ground + 5f)},{F(CentreZ + 7f)},-20,-90");
        var canvas = v.Page.Locator("#map-canvas");
        try
        {
            await Task.Delay(4000);
            var withBars = Path.Combine(_outDir, "bars-on.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = withBars });
            int redOn = Red(withBars);

            var menu = await OpenMenuAsync(v);
            await PressMenuAsync(v, menu, "bars");
            await PressMenuAsync(v, menu, "tags");
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(500);
            var noBars = Path.Combine(_outDir, "bars-off.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = noBars });
            int redOff = Red(noBars);
            TestContext.Progress.WriteLine($"[menu] red pixels with the bars {redOn}, without {redOff}");
            Assert.That(redOn, Is.GreaterThan(redOff + 60), "the hearts show over the players' heads, and the setting takes them away");
            var again = await OpenMenuAsync(v);
            await PressMenuAsync(v, again, "bars");
            await PressMenuAsync(v, again, "tags");
            await v.Page.Keyboard.PressAsync("Tab");
            await Task.Delay(300);

            // Follow Aubs (the demo list's first row): the list's row is under the minimap
            var box = (await canvas.BoundingBoxAsync())!;
            float listX = (float)box.Width - 8 - 95, rowY = 262;
            await PressAsync(v.Page, listX, rowY);
            float menuX = Math.Min(listX, (float)box.Width - 210) + 30;
            await PressAsync(v.Page, menuX, rowY + 18 + 28);   // Follow Aubs

            async Task<double> Distance()
            {
                await Task.Delay(1300);   // the stats line refreshes about once a second
                var s = (await ViewerStats.ReadAsync(v.Page))!;
                double dx = s.X - aubsX, dy = s.Y - (Ground + 1.62), dz = s.Z - CentreZ;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            double start = await Distance();
            TestContext.Progress.WriteLine($"[menu] following at {start:F1} blocks");
            Assert.That(start, Is.EqualTo(FollowCamera.DefaultDistance).Within(1.5), "the follow camera stands behind the player");
            var chase = Path.Combine(_outDir, "bars-chase.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = chase });

            // The wheel, over the view, with the mouse free: out, and the page has not moved
            await v.Page.Mouse.MoveAsync(640, 500);
            for (int i = 0; i < 8; i++) { await v.Page.Mouse.WheelAsync(0, 100); await Task.Delay(40); }
            double far = await Distance();
            TestContext.Progress.WriteLine($"[menu] after 8 notches out: {far:F1}");
            Assert.That(far, Is.GreaterThan(start * 1.6), "the wheel moves the camera away");
            Assert.That(await v.Page.EvaluateAsync<double>("window.scrollY"), Is.Zero, "it does not scroll the page");
            var zoomed = Path.Combine(_outDir, "bars-chase-far.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = zoomed });
            for (int i = 0; i < 14; i++) { await v.Page.Mouse.WheelAsync(0, -100); await Task.Delay(40); }
            double near = await Distance();
            TestContext.Progress.WriteLine($"[menu] after 14 notches in: {near:F1}");
            Assert.That(near, Is.LessThan(far / 2), "and back toward the player");
            Assert.That(near, Is.GreaterThanOrEqualTo(FollowCamera.MinDistance - 0.5));

            // With the camera holding the mouse too
            await ViewerRig.TakeTheMouseAsync(v.Page, 640, 500);
            for (int i = 0; i < 8; i++) { await v.Page.Mouse.WheelAsync(0, 100); await Task.Delay(40); }
            double farLocked = await Distance();
            Assert.That(farLocked, Is.GreaterThan(near * 1.6), "the same while the camera has the mouse");
            await v.Page.EvaluateAsync("document.exitPointerLock()");
            await Task.Delay(300);

            // Through the player's eyes: their HUD along the bottom of the view
            await PressAsync(v.Page, listX, rowY);
            await PressAsync(v.Page, menuX, rowY + 18);   // first row: Go to (this stops following)
            await PressAsync(v.Page, listX, rowY);
            await PressAsync(v.Page, menuX, rowY + 18 + 28 + 28);   // See through Aubs's eyes
            await Task.Delay(1500);
            var eyes = Path.Combine(_outDir, "bars-eyes.png");
            await canvas.ScreenshotAsync(new LocatorScreenshotOptions { Path = eyes });
            Assert.That(Red(eyes), Is.GreaterThan(100), "the hearts along the bottom of the view");
            // Looking out of the player, not at the inside of their head: the sky and the ground are in the view (their head's dark skin filled it)
            using (var picture = new System.Drawing.Bitmap(eyes))
            {
                var sky = picture.GetPixel(picture.Width / 2, picture.Height / 8);
                Assert.That(sky.B, Is.GreaterThan(sky.R + 40), "the sky above the middle of the view, with no head in the way");
            }
            Assert.That(v.Errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", v.Errors));
        }
        finally
        {
            stop.Cancel();
            try { await poster; } catch { }
            try { await v.Page.EvaluateAsync("localStorage.removeItem('aubscraft.viewer')"); } catch { }
            await v.Page.CloseAsync();
        }
    }

    [Test]
    public async Task TheWheel_ChangesTheFlyingSpeed_WhenNotFollowing()
    {
        var v = await OpenAsync("time=6000&dd=8&cam=144,40,144,-60,0");
        try
        {
            await v.Page.Mouse.MoveAsync(640, 500);
            for (int i = 0; i < 5; i++) { await v.Page.Mouse.WheelAsync(0, -100); await Task.Delay(40); }
            string? saved = null;
            for (int i = 0; i < 30 && saved == null; i++)
            {
                await Task.Delay(100);
                saved = await v.Page.EvaluateAsync<string?>("localStorage.getItem('aubscraft.viewer')");
            }
            Assert.That(saved, Is.Not.Null);
            var speed = System.Text.Json.JsonDocument.Parse(saved!).RootElement.GetProperty("FlySpeed").GetSingle();
            Assert.That(speed, Is.EqualTo(60f * MathF.Pow(1.1f, 5)).Within(1f), "five notches up: faster, and remembered");
        }
        finally
        {
            try { await v.Page.EvaluateAsync("localStorage.removeItem('aubscraft.viewer')"); } catch { }
            await v.Page.CloseAsync();
        }
    }

    // "... | gaps 3 -29,-27:none ..." at the end of the worker's [Voxel] line
    private static readonly Regex GapLine = new(@"\| gaps (?<n>\d+)(?<first>[^|]*)$", RegexOptions.Compiled);

    /// <summary>
    /// Columns leaving the full-detail range must hand over to their far chunks with nothing missing in between. The hole needs a far chunk with no mesh under a column about to go: flying at a short
    /// draw distance gives the far meshes behind the camera up; growing the distance puts those chunks back in view; the columns then leave the range as the camera flies on. Before the fix the far
    /// chunk was skipped while its column stood, and not looked at again until the camera next changed chunk. Real world: this flies across it.
    /// NOT a guard of that fix yet: with the hand-over disabled (2026-11-04, build red2) this still passes, gaps 0, so the scene does not reach the hole the code reading found. It does gate that changing the distance in flight leaves nothing undrawn.
    /// </summary>
    [Test]
    public async Task AColumnLeavingTheRange_LeavesNoHole_AfterTheDrawDistanceGrew()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH")!;
        await using var rig = await ViewerRig.StartAsync(publish, Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server");
        var page = await rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => log.Enqueue(m.Text);
        page.Worker += (_, w) => w.Console += (_, m) => log.Enqueue(m.Text);
        var v = new Viewer(page, new List<string>(), log);
        try
        {
            await page.GotoAsync($"{rig.BaseUrl}/map?cam=-400,140,-300,-30,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 180_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 10, maxSeconds: 180);

            // The shortest draw distance
            var menu = await OpenMenuAsync(v);
            await PressMenuAsync(v, menu, "distance");
            await page.Keyboard.PressAsync("Tab");
            await Task.Delay(300);

            async Task FlyAsync(int ms)
            {
                await ViewerRig.TakeTheMouseAsync(page, 640, 500);
                await page.Keyboard.DownAsync("KeyW");
                await Task.Delay(ms);
                await page.Keyboard.UpAsync("KeyW");
                await Task.Delay(200);
                await page.EvaluateAsync("document.exitPointerLock()");
                await Task.Delay(300);
            }

            var from = await ViewerStats.ReadAsync(page);
            await FlyAsync(7000);
            await Task.Delay(1500);
            var to = await ViewerStats.ReadAsync(page);
            Assert.That(to!.X - from!.X, Is.GreaterThan(16 * 6), "the camera really flew");
            await Task.Delay(8000);   // settled: the far meshes behind have gone, the columns are all in

            // Grow it to the longest: the slider's far end
            menu = await OpenMenuAsync(v);
            var slider = menu["distanceSlider"];
            await PressAsync(page, slider.X + slider.W - 1f, slider.Y + 28f);
            await page.Keyboard.PressAsync("Tab");
            await Task.Delay(300);
            Assert.That((await ViewerStats.ReadAsync(page))?.DrawDistance, Is.GreaterThan(12).Or.Null, "the distance grew");

            // Everything the longer distance wanted has its mesh now, but for the far chunks under columns; a mesher still at work would pick up the very chunk the fix is about
            await Task.Delay(30000);

            // On a little: the columns at the edge of the range go; then hold still
            await FlyAsync(1500);
            string last = "";
            int worst = 0, gaps = -1, seen = log.Count;
            for (int i = 0; i < 45 && gaps != 0; i++)
            {
                await Task.Delay(2000);
                last = log.Skip(seen).LastOrDefault(l => GapLine.IsMatch(l)) ?? "";
                var m = GapLine.Match(last);
                if (!m.Success) continue;
                gaps = int.Parse(m.Groups["n"].Value);
                worst = Math.Max(worst, gaps);
            }
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "after-growing.png") });
            TestContext.Progress.WriteLine($"[menu] worst {worst}; after the stop: gaps {gaps} " + GapLine.Match(last).Groups["first"].Value);
            Assert.That(gaps, Is.EqualTo(0), "columns undrawn with the camera still: " + GapLine.Match(last).Value);
        }
        finally { await page.CloseAsync(); }
    }

    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);
}
