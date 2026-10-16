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

    /// <summary>A world of one chunk (-1, 0): a flat floor of grass at y -61, with what the edits have put on it. It speaks the plugin's language: commands in, chunk updates and results out.</summary>
    private sealed class FakePlugin
    {
        private readonly ViewerRig _rig;
        private readonly Dictionary<(int x, int y, int z), string> _blocks = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;
        public readonly List<(string Kind, int[] At, string Block)> Received = new();
        public readonly List<string> Edits = new();
        private readonly Dictionary<string, List<((int, int, int) At, string Old)>> _journals = new();

        public FakePlugin(ViewerRig rig) => _rig = rig;

        public void Start() => _loop = Task.Run(Loop);

        public async Task StopAsync()
        {
            _stop.Cancel();
            try { if (_loop != null) await _loop; } catch (OperationCanceledException) { }
        }

        private static byte[] ChunkBody(IReadOnlyDictionary<(int x, int y, int z), string> blocks)
        {
            const int top = 20;
            var palette = new List<string> { "minecraft:air", "minecraft:stone", "minecraft:grass_block[snowy=false]" };
            foreach (var b in blocks.Values) if (!palette.Contains(b)) palette.Add(b);
            using var ms = new MemoryStream();
            void U8(int v) => ms.WriteByte((byte)v);
            void U16(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
            U8(1); U8(0); U16(top); U16(palette.Count);
            foreach (var p in palette) { var bytes = Encoding.UTF8.GetBytes(p); U16(bytes.Length); ms.Write(bytes); }
            for (int y = 0; y < top; y++)
                for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++)
                    {
                        int wy = y - 64, wx = x - 16;
                        int id = wy < -61 ? 1 : wy == -61 ? 2 : 0;
                        if (blocks.TryGetValue((wx, wy, z), out var over)) id = palette.IndexOf(over);
                        U16(id);
                    }
            for (int i = 0; i < 256 * top; i++) U8(0xF0);
            for (int i = 0; i < 256; i++) U16(4);
            U16(0); U16(0);
            return ms.ToArray();
        }

        public Task PostWorldAsync() => _rig.PostLiveChunkAsync(-1, 0, ChunkBody(_blocks));

        private async Task Loop()
        {
            using var http = new HttpClient { BaseAddress = new Uri(_rig.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "/api/live/commands/aubscraft");
                    req.Headers.Add("X-Live-Secret", ViewerRig.LiveSecret);
                    using var resp = await http.SendAsync(req, _stop.Token);
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK) { await Task.Delay(200, _stop.Token); continue; }
                    var bytes = await resp.Content.ReadAsByteArrayAsync(_stop.Token);
                    int count = BitConverter.ToUInt16(bytes, 0), at = 2;
                    for (int i = 0; i < count; i++)
                    {
                        int len = BitConverter.ToInt32(bytes, at); at += 4;
                        await HandleAsync(http, bytes.AsSpan(at, len).ToArray());
                        at += len;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { await Task.Delay(200); }
            }
        }

        private async Task HandleAsync(HttpClient http, byte[] cmd)
        {
            int p = 0;
            int U8() => cmd[p++];
            int U16() { int v = cmd[p] | (cmd[p + 1] << 8); p += 2; return v; }
            int I32() { int v = BitConverter.ToInt32(cmd, p); p += 4; return v; }
            string Str() { int n = U16(); var s = Encoding.UTF8.GetString(cmd, p, n); p += n; return s; }
            int kind = U8();
            string id = Str();
            string result;
            if (kind == 1)
            {
                Str(); Str();   // editor, label
                var palette = new string[U16()];
                for (int i = 0; i < palette.Length; i++) palette[i] = Str();
                int ops = U16();
                var journal = new List<((int, int, int), string)>();
                int placed = 0;
                for (int i = 0; i < ops; i++)
                {
                    int type = U8();
                    Assert.That(type, Is.EqualTo(1), "the viewer's clicks are single blocks");
                    int x = I32(), y = I32(), z = I32();
                    var block = palette[U16()];
                    Received.Add(("set", [x, y, z], block));
                    journal.Add(((x, y, z), _blocks.TryGetValue((x, y, z), out var old) ? old : (y == -61 ? "minecraft:grass_block[snowy=false]" : y < -61 ? "minecraft:stone" : "minecraft:air")));
                    if (block == "minecraft:air" && y > -61) _blocks.Remove((x, y, z)); else _blocks[(x, y, z)] = block;
                    placed++;
                }
                _journals[id] = journal;
                Edits.Add(id);
                result = $"{{\"id\":\"{id}\",\"ok\":true,\"edit\":\"{id}\",\"placed\":{placed},\"skipped\":0,\"applied\":true}}";
            }
            else
            {
                var edit = Str();
                Received.Add(("undo", [], edit));
                var journal = _journals[edit];
                foreach (var (pos, old) in journal) { if (old == "minecraft:air") _blocks.Remove(pos); else _blocks[pos] = old; }
                result = $"{{\"id\":\"{id}\",\"ok\":true,\"edit\":\"{edit}\",\"placed\":{journal.Count},\"skipped\":0,\"applied\":false}}";
            }
            await PostWorldAsync();
            using var post = new HttpRequestMessage(HttpMethod.Post, "/api/live/commands/aubscraft/result") { Content = new StringContent(result, Encoding.UTF8, "application/json") };
            post.Headers.Add("X-Live-Secret", ViewerRig.LiveSecret);
            (await http.SendAsync(post)).EnsureSuccessStatusCode();
        }
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
}
