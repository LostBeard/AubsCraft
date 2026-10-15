using System.Drawing;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The rest of the GPU HUD - minimap, player list, chat, notices - on the real pixels of the real viewer. Same method as
/// <see cref="ViewerHudTests"/>: the control is the SAME scene with the HUD hidden (<c>/map?hud=0&amp;hudfeed=demo</c>), and every
/// "it is drawn" assertion is a difference between the two frames. <c>hudfeed=demo</c> feeds the HUD sample chat and players, echoes a
/// typed line back as chat instead of sending it, and raises a notice on F9: nothing reaches the server.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      run name; shots land in screenshots/viewer/{tag}-hudfeed/ (default "run")
/// </summary>
[NonParallelizable]
public class ViewerHudFeedTests
{
    private ViewerRig _rig = null!;
    private IPage _page = null!;
    private string _outDir = null!;
    private Bitmap _control = null!;
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _console = [];

    // HUD layout (see ViewerHud): minimap top-right, players below it, chat bottom-left, notices under the compass
    private const int MinimapX = 8 + 176, MinimapY = 8 + 18; // distance of the map area's right / top edge from the frame's right / top
    private static (int x, int y, int w, int h) MinimapRegion(Bitmap b) => (b.Width - 8 - 176, 8 + 18, 176, 176);
    private static (int x, int y, int w, int h) PlayersRegion(Bitmap b) => (b.Width - 8 - 190, 226, 190, 70);
    private static (int x, int y, int w, int h) ChatRegion(Bitmap b) => (8, b.Height - 34 - 170, 380, 170);
    private static (int x, int y, int w, int h) ToastRegion(Bitmap b) => (b.Width / 2 - 150, 44, 300, 40);

    [OneTimeSetUp]
    public async Task OpenViewerAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "run";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag + "-hudfeed");
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
        try
        {
            var controlPage = await OpenAsync("/map?hud=0&hudfeed=demo");
            try
            {
                var path = Path.Combine(_outDir, "00-control-hud-off.png");
                await controlPage.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = path });
                using var bmp = new Bitmap(path);
                _control = new Bitmap(bmp);
            }
            finally { await controlPage.CloseAsync(); }
            _page = await OpenAsync("/map?hudfeed=demo");
        }
        catch
        {
            try
            {
                lock (_console) File.WriteAllLines(Path.Combine(_outDir, "setup-failure-console.log"), _console);
                File.WriteAllText(Path.Combine(_outDir, "setup-failure-server.log"), _rig.ServerLog);
            }
            catch { }
            throw;
        }
    }

    private async Task<IPage> OpenAsync(string path)
    {
        var page = await _rig.Context.NewPageAsync();
        page.Console += (_, m) =>
        {
            lock (_console) { if (_console.Count < 800) _console.Add($"[{m.Type}] {m.Text}"); }
            if (m.Type == "error") _consoleErrors.Add(m.Text);
        };
        page.Worker += (_, w) => w.Console += (_, m) => { lock (_console) { if (_console.Count < 800) _console.Add($"[worker {m.Type}] {m.Text}"); } };
        page.PageError += (_, e) => { lock (_console) _console.Add("[pageerror] " + e); _consoleErrors.Add(e); };
        await page.GotoAsync(_rig.BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });

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
        // The two page loads must show the same terrain: take both only once it has settled around the camera
        await Task.Delay(40_000);
        return page;
    }

    [OneTimeTearDown]
    public async Task CloseViewerAsync()
    {
        try { File.WriteAllLines(Path.Combine(_outDir, "console.log"), _console); } catch { }
        try { _control?.Dispose(); } catch { }
        try { if (_page != null) await _page.CloseAsync(); } catch { }
        if (_rig != null) await _rig.DisposeAsync();
    }

    private async Task<Bitmap> ShotAsync(string name)
    {
        var path = Path.Combine(_outDir, name + ".png");
        await _page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = path });
        return new Bitmap(path);
    }

    private double Changed(Bitmap shot, (int x, int y, int w, int h) r, int threshold = 30)
        => Pixels.ChangedFraction(_control, shot, r.x, r.y, r.w, r.h, threshold);

    [Test, Order(1)]
    public async Task Minimap_ShowsTheWorldFromAbove_InTheTopRightCorner()
    {
        using var shot = await ShotAsync("01-minimap");
        var r = MinimapRegion(shot);
        double changed = Changed(shot, r);
        var map = Pixels.Region(shot, r.x, r.y, r.w, r.h);
        TestContext.Progress.WriteLine($"[hudfeed] minimap: {changed:P0} of its pixels differ from the control; luma spread {map.LumaStdDev:F1}");
        Assert.That(changed, Is.GreaterThan(0.5), "the minimap covers its corner");
        Assert.That(map.LumaStdDev, Is.GreaterThan(10), "and shows terrain (land, water, shapes), not a flat fill");
        // Everything else is the same world: the middle of the frame matches the control
        Assert.That(Pixels.ChangedFraction(_control, shot, shot.Width / 2 - 150, shot.Height / 2 - 100, 300, 200, 30), Is.LessThan(0.1),
            "away from the HUD the two frames show the same world");
    }

    [Test, Order(2)]
    public async Task PlayerList_ShowsWhoIsOnline()
    {
        using var shot = await ShotAsync("02-players");
        var r = PlayersRegion(shot);
        double changed = Changed(shot, r);
        TestContext.Progress.WriteLine($"[hudfeed] players panel: {changed:P0} of its pixels differ from the control");
        Assert.That(changed, Is.GreaterThan(0.35), "the player list panel (title and four names) is drawn under the minimap");
    }

    [Test, Order(3)]
    public async Task Chat_OpensWithEnter_ShowsTheHistory_AndEchoesWhatIsTyped()
    {
        await _page.Locator("#map-canvas").FocusAsync();
        // The history that was fed has faded by now (messages fade after 14 s); the chat is a feed until it is opened
        using (var idle = await ShotAsync("03a-chat-idle"))
            Assert.That(Changed(idle, ChatRegion(idle)), Is.LessThan(0.08), "an idle chat is just a feed: the faded history leaves the corner empty");

        await _page.Keyboard.PressAsync("Enter");
        await Task.Delay(600);
        using (var open = await ShotAsync("03b-chat-open"))
        {
            double changed = Changed(open, ChatRegion(open));
            TestContext.Progress.WriteLine($"[hudfeed] opened chat: {changed:P0} of its area differs from the control");
            Assert.That(changed, Is.GreaterThan(0.15), "Enter opens the chat: the whole history and the input field are drawn");
        }

        await _page.Keyboard.TypeAsync("hello from the gate");
        await _page.Keyboard.PressAsync("Enter");
        // typed -> worker -> page -> (demo: echoed) -> worker feed -> drawn
        await Task.Delay(1500);
        using (var echoed = await ShotAsync("03c-chat-echo"))
        {
            // The echo adds a line at the bottom of the history, just above the input: pixels there change compared with the opened chat
            using var before = new Bitmap(Path.Combine(_outDir, "03b-chat-open.png"));
            var r = ChatRegion(echoed);
            double lineChanged = Pixels.ChangedFraction(before, echoed, r.x, r.y + r.h - 62, r.w, 40, 30);
            TestContext.Progress.WriteLine($"[hudfeed] echo line area: {lineChanged:P0} changed");
            Assert.That(lineChanged, Is.GreaterThan(0.01), "the typed line came back through the page and is drawn in the chat");
        }
        await _page.Keyboard.PressAsync("Escape");
        Assert.That(_consoleErrors, Is.Empty);
    }

    [Test, Order(4)]
    public async Task Notice_SlidesInUnderTheCompass()
    {
        await _page.Locator("#map-canvas").FocusAsync();
        await _page.Keyboard.PressAsync("F9"); // demo hook: raises an advancement notice
        await Task.Delay(900);
        using var shot = await ShotAsync("04-toast");
        double changed = Changed(shot, ToastRegion(shot));
        TestContext.Progress.WriteLine($"[hudfeed] notice area: {changed:P0} differs from the control");
        Assert.That(changed, Is.GreaterThan(0.3), "the notice (a coloured bar with the text) is drawn under the compass");
        Assert.That(_consoleErrors, Is.Empty);
    }
}
