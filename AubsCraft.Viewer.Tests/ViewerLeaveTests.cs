using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Leaving the map page (to Home, in the app) and coming back must not throw: the render worker outlives the page (RenderWorkerHost), so the
/// page detaches its canvas and the worker's loops, pumps and streams keep running or wind down with nobody looking.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      run name (default "leave")
/// </summary>
[NonParallelizable]
public class ViewerLeaveTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "leave";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world, secondServerId: "spooky");
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    [Test]
    public async Task LeavingTheMap_ForHome_AndComingBack_ThrowsNothing()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var phase = "open";
        void Line(string source, string type, string text)
        {
            var line = $"[{phase}] [{source} {type}] {text}";
            log.Enqueue(line);
            // Toasts of the local panel tailing the real server's log are not this page's errors (see ViewerStats)
            if (type == "error" && !text.Contains("whitelisted")) errors.Enqueue(line);
        }
        page.Console += (_, m) => Line("page", m.Type, m.Text);
        page.PageError += (_, e) => errors.Enqueue($"[{phase}] [pageerror] {e}");
        page.Worker += (_, w) => w.Console += (_, m) => Line("worker", m.Type, m.Text);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await Task.Delay(30_000); // columns and far chunks streaming, meshes in flight, the pumps busy

            phase = "to-home";
            await page.Locator("a.nav-link[href='']").First.ClickAsync();
            await Task.Delay(15_000); // the worker keeps going without a canvas

            phase = "back-to-map";
            await page.Locator("a.nav-link[href='map']").First.ClickAsync();
            await Task.Delay(20_000);

            phase = "to-home-again";
            await page.Locator("a.nav-link[href='']").First.ClickAsync();
            await Task.Delay(8_000);
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "leave-console.log"), log); } catch { }
            await page.CloseAsync();
        }
        Assert.That(errors, Is.Empty, "errors while leaving and returning:\n" + string.Join("\n", errors.Take(10)));
    }

    [Test]
    public async Task SwitchingTheViewedServer_ThrowsNothing_AndStartsTheOtherWorld()
    {
        // The panel's content is keyed on the selected server, so a switch disposes the whole map page and builds a new one
        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var phase = "open";
        void Line(string source, string type, string text)
        {
            var line = $"[{phase}] [{source} {type}] {text}";
            log.Enqueue(line);
            if (type == "error" && !text.Contains("whitelisted")) errors.Enqueue(line);
        }
        page.Console += (_, m) => Line("page", m.Type, m.Text);
        page.PageError += (_, e) => errors.Enqueue($"[{phase}] [pageerror] {e}");
        page.Worker += (_, w) => w.Console += (_, m) => Line("worker", m.Type, m.Text);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await Task.Delay(25_000);

            phase = "to-second";
            await page.Locator("select[aria-label='Server']").SelectOptionAsync("spooky");
            await Task.Delay(25_000);

            phase = "back-to-first";
            await page.Locator("select[aria-label='Server']").SelectOptionAsync("aubscraft");
            await Task.Delay(20_000);
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "switch-console.log"), log); } catch { }
            await page.CloseAsync();
        }
        Assert.That(log.Any(l => l.Contains("Switching worlds")), Is.True, "the new page should have ended the old server's worker (did the switch happen?)");
        Assert.That(errors, Is.Empty, "errors while switching servers:\n" + string.Join("\n", errors.Take(10)));
    }
}
