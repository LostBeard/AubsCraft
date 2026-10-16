using System.Drawing;
using System.Text;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Building from the viewer, in a real browser: an admin turns edit mode on, points at the floor and clicks. The test plays the server's plugin (it takes the commands the panel queues, notes them, "applies" them to a
/// small world of its own and posts the changed chunk back, as the real plugin does) so what is checked is everything between the mouse and the plugin: the pick, the plan, the panel's checks and queue, the result,
/// and the change appearing on screen.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerEditTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "edit";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    // Brown planks against green grass: how many pixels of a shot are planks-coloured
    private static int Planks(Bitmap b)
    {
        int n = 0;
        for (int y = 6; y < b.Height - 6; y += 2)
            for (int x = 6; x < b.Width - 6; x += 2)
            {
                var p = b.GetPixel(x, y);
                if (p.R > 90 && p.R > p.G + 12 && p.G > p.B + 15) n++;   // warm brown: red over green over blue
            }
        return n;
    }

    [Test]
    public async Task AnAdminBreaksAndPlacesWithTheMouse_AndUndoesWithCtrlZ()
    {
        var plugin = new FakePlugin(_rig);
        plugin.Start();
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await plugin.PostWorldAsync();   // the floor, before the page loads it
            // Straight down over the floor of chunk (-1, 0), at block (-9, -61, 8): the cursor points at the grass under the camera
            await page.GotoAsync($"{_rig.BaseUrl}/map?time=6000&dd=6&light=0&cam=-8.5,-58.0,8.5,-90,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);

            // The Edit button is there for the owner, and the plugin (the fake one) is polling: edit mode turns on
            var edit = page.GetByRole(AriaRole.Button, new() { Name = "Edit: Off" });
            await edit.WaitForAsync(new() { Timeout = 30_000 });
            for (int i = 0; i < 20; i++)
            {
                await edit.ClickAsync();
                try { await page.GetByRole(AriaRole.Button, new() { Name = "Edit: On" }).WaitForAsync(new() { Timeout = 3000 }); break; }
                catch (TimeoutException) { }
            }
            Assert.That(await page.GetByRole(AriaRole.Button, new() { Name = "Edit: On" }).CountAsync(), Is.EqualTo(1), "edit mode is on");

            // Take the pointer (a click on the view), then look at what is shown
            await page.Locator("#map-canvas").ClickAsync();
            await Task.Delay(2500);
            var before = Path.Combine(_outDir, "edit-before.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = before });

            // Right click: the block in hand (stone) goes on the grass; press 3 first: oak planks
            await page.Keyboard.PressAsync("Digit3");
            await Task.Delay(300);
            await page.Mouse.ClickAsync(300, 300, new MouseClickOptions { Button = MouseButton.Right });
            for (int i = 0; i < 30 && plugin.Received.Count == 0; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThanOrEqualTo(1), "the plugin was sent the edit:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("Edit") || l.Contains("edit")).TakeLast(8)));
            var placed = plugin.Received[0];
            TestContext.Progress.WriteLine($"[edit] placed {placed.Block} at {string.Join(",", placed.At)}");
            Assert.That(placed.Kind, Is.EqualTo("set"));
            Assert.That(placed.Block, Is.EqualTo("minecraft:oak_planks"));
            Assert.That(placed.At[1], Is.EqualTo(-60), "on top of the grass");
            Assert.That(placed.At[0], Is.InRange(-11, -7), "under the camera");
            Assert.That(placed.At[2], Is.InRange(6, 10));

            await Task.Delay(3500);
            var after = Path.Combine(_outDir, "edit-placed.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = after });
            using (var a = new Bitmap(before)) using (var b = new Bitmap(after))
            {
                TestContext.Progress.WriteLine($"[edit] plank-coloured samples: before {Planks(a)}, after {Planks(b)}");
                Assert.That(Planks(b), Is.GreaterThan(Planks(a) + 150), "the planks appear in the picture");
            }

            // Left click: the block pointed at is broken (the planks, which are now under the cursor)
            await page.Mouse.ClickAsync(300, 300, new MouseClickOptions { Button = MouseButton.Left });
            for (int i = 0; i < 30 && plugin.Received.Count < 2; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(plugin.Received[1].Block, Is.EqualTo("minecraft:air"), "broken");
            Assert.That(plugin.Received[1].At, Is.EqualTo(placed.At), "the block that was put there");

            // Ctrl+Z puts it back, Ctrl+Y breaks it again
            await page.Keyboard.PressAsync("Control+KeyZ");
            for (int i = 0; i < 30 && plugin.Received.Count < 3; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(plugin.Received[2].Kind, Is.EqualTo("undo"));
            Assert.That(plugin.Received[2].Block, Is.EqualTo(plugin.Edits[1]), "the break is what was undone");

            await Task.Delay(2500);
            var undone = Path.Combine(_outDir, "edit-undone.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = undone });
            using (var c = new Bitmap(undone)) Assert.That(Planks(c), Is.GreaterThan(150), "the planks are back");

            // E opens the block palette (the pointer is let go of); typing a name and Enter puts the first match in hand
            await page.Keyboard.PressAsync("KeyE");
            await Task.Delay(1500);
            var palette = Path.Combine(_outDir, "edit-palette.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = palette });
            await page.Keyboard.TypeAsync("gold block");
            await Task.Delay(500);
            var searched = Path.Combine(_outDir, "edit-palette-search.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = searched });
            await page.Keyboard.PressAsync("Enter");
            await Task.Delay(500);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "edit-palette-chosen.png") });
            await page.Locator("#map-canvas").ClickAsync();   // the pointer back to the camera
            await Task.Delay(1500);
            int before3 = plugin.Received.Count;
            await page.Mouse.ClickAsync(300, 300, new MouseClickOptions { Button = MouseButton.Right });
            for (int i = 0; i < 30 && plugin.Received.Count == before3; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThan(before3), "a block was placed after the palette");
            Assert.That(plugin.Received[^1].Block, Is.EqualTo("minecraft:gold_block"), "the palette's choice is in hand");

            // B leaves edit mode: clicks do nothing now
            await page.Keyboard.PressAsync("KeyB");
            await Task.Delay(800);
            Assert.That(await page.GetByRole(AriaRole.Button, new() { Name = "Edit: Off" }).CountAsync(), Is.EqualTo(1), "edit mode is off");
            int received = plugin.Received.Count;
            await page.Mouse.ClickAsync(300, 300, new MouseClickOptions { Button = MouseButton.Left });
            await Task.Delay(1500);
            Assert.That(plugin.Received.Count, Is.EqualTo(received), "a click edits nothing when edit mode is off");
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            await plugin.StopAsync();
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task AnAdminSelectsFillsCopiesPastesAndSavesABlueprint_WithTheKeyboardAndMouse()
    {
        var plugin = new FakePlugin(_rig);
        plugin.Place(-9, -60, 8, "minecraft:stone");   // under the camera: what the cursor points at
        plugin.Start();
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await plugin.PostWorldAsync();
            await page.GotoAsync($"{_rig.BaseUrl}/map?time=6000&dd=6&light=0&cam=-8.5,-58.0,8.5,-90,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            var edit = page.GetByRole(AriaRole.Button, new() { Name = "Edit: Off" });
            await edit.WaitForAsync(new() { Timeout = 30_000 });
            for (int i = 0; i < 20; i++)
            {
                await edit.ClickAsync();
                try { await page.GetByRole(AriaRole.Button, new() { Name = "Edit: On" }).WaitForAsync(new() { Timeout = 3000 }); break; }
                catch (TimeoutException) { }
            }
            await page.Locator("#map-canvas").ClickAsync();
            await Task.Delay(2500);

            // V: the select tool. Two clicks at the same spot: a box of one block (the stone under the camera)
            await page.Keyboard.PressAsync("KeyV");
            await Task.Delay(300);
            await page.Mouse.ClickAsync(300, 300, new MouseClickOptions { Button = MouseButton.Left });
            await Task.Delay(400);
            await page.Mouse.ClickAsync(300, 300, new MouseClickOptions { Button = MouseButton.Left });
            await Task.Delay(800);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "select-box.png") });

            // 5, then F: glass fills the box
            await page.Keyboard.PressAsync("Digit5");
            await Task.Delay(300);
            await page.Keyboard.PressAsync("KeyF");
            for (int i = 0; i < 30 && plugin.Received.Count == 0; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThanOrEqualTo(1), "the fill reached the plugin:\n" + string.Join("\n", log.ToArray().TakeLast(10)));
            Assert.That(plugin.Received[0].Kind, Is.EqualTo("fill"));
            Assert.That(plugin.Received[0].Block, Is.EqualTo("minecraft:glass"));
            Assert.That(plugin.Received[0].At, Is.EqualTo(new[] { -9, -60, 8, -9, -60, 8 }));
            await Task.Delay(2500);

            // Ctrl+C then Ctrl+V: the glass is copied (from the panel's picture of the world, which has the glass now) and a blueprint of it follows the cursor; a click puts it on the face pointed at
            await page.Keyboard.PressAsync("Control+KeyC");
            await Task.Delay(1500);
            await page.Keyboard.PressAsync("Control+KeyV");
            await Task.Delay(800);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "paste-ghost.png") });
            int before = plugin.Received.Count;
            await page.Mouse.ClickAsync(300, 300, new MouseClickOptions { Button = MouseButton.Left });
            for (int i = 0; i < 30 && plugin.Received.Count == before; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThan(before), "the paste reached the plugin");
            var pasted = plugin.Received[^1];
            Assert.That(pasted.Kind, Is.EqualTo("blocks"));
            Assert.That(pasted.Block, Is.EqualTo("minecraft:glass"), "the glass that was copied");
            Assert.That(pasted.At, Is.EqualTo(new[] { -9, -59, 8 }), "on top of the block pointed at");

            // Escape stops placing; Ctrl+S names the box and keeps it as a blueprint
            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(300);
            await page.Keyboard.PressAsync("Control+KeyS");
            await Task.Delay(1500);   // the pointer is let go of for the prompt
            await page.Keyboard.TypeAsync("glass post");
            await Task.Delay(300);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "save-prompt.png") });
            await page.Keyboard.PressAsync("Enter");
            await Task.Delay(1500);

            // It is in the list (K), and a click on it starts placing it
            await page.Keyboard.PressAsync("KeyK");
            await Task.Delay(1500);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "blueprint-menu.png") });
            using var listing = new HttpClient { BaseAddress = new Uri(_rig.BaseUrl) };
            var response = await _rig.Context.APIRequest.GetAsync($"{_rig.BaseUrl}/api/world/edit/blueprints?server=aubscraft");
            var body = await response.TextAsync();
            Assert.That(body, Does.Contain("glass post"), "saved on the panel: " + body);
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            await plugin.StopAsync();
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task AnAdminsWorldPanelOpensWithO_AndItsButtonsSendTheRequest()
    {
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync($"{_rig.BaseUrl}/map?time=6000&dd=6&light=0&cam=-8.5,-58.0,8.5,-90,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            await page.Locator("#map-canvas").ClickAsync();   // the camera takes the pointer
            await Task.Delay(2000);
            await page.Keyboard.PressAsync("KeyO");
            await Task.Delay(1500);   // the page lets go of the pointer for the panel
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "world-panel.png") });
            // Night: as a hand does it - over the button first, then down, then up (a click in one breath is a press and a release in the same frame)
            var box = (await page.Locator("#map-canvas").BoundingBoxAsync())!;   // the mouse is in the page's pixels, the panel's place is in the canvas's
            await page.Mouse.MoveAsync(box.X + 547, box.Y + 242);
            await Task.Delay(150);
            await page.Mouse.DownAsync();
            await Task.Delay(150);
            await page.Mouse.UpAsync();
            for (int i = 0; i < 30 && !log.ToArray().Any(l => l.Contains("[Admin] world time")); i++) await Task.Delay(200);
            Assert.That(log.ToArray().Any(l => l.Contains("[Admin] world time  night")), Is.True, "the request went out:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("Admin")).TakeLast(5)));
        }
        finally { await page.CloseAsync(); }
    }
}