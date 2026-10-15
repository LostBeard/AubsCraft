using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The /quest installer page (ADB over WebUSB, SpawnDev.SpawnJS.TangoADB) in the real published app. No headset is attached, so this is
/// the slice that can be automated: the page renders, offers "Connect Quest", reports WebUSB support in Chrome, and logs no errors.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
/// </summary>
[NonParallelizable]
public class ViewerQuestPageTests
{
    private ViewerRig _rig = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    [Test]
    public async Task QuestPage_Renders_OffersConnect_AndLogsNoErrors()
    {
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        var requests = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        page.Response += (_, r) => requests.Add($"{r.Status} {r.Url}");
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/quest", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });

            var connect = page.GetByRole(AriaRole.Button, new() { Name = "Connect Quest" });
            await connect.WaitForAsync(new LocatorWaitForOptions { Timeout = 90_000 });
            Assert.That(await connect.IsVisibleAsync(), Is.True, "the Connect Quest button is shown");
            Assert.That(await page.GetByText("This page needs WebUSB").CountAsync(), Is.Zero, "Chrome supports WebUSB: no warning");

            // The toasts of the LOCAL panel tailing the real server's log are not this page's errors (see ViewerStats)
            Assert.That(errors.Where(e => !e.Contains("whitelisted")), Is.Empty, "the page logged errors: " + string.Join(" | ", errors));
            TestContext.Progress.WriteLine("[quest] responses for tango: " + string.Join(", ", requests.Where(r => r.Contains("tango", StringComparison.OrdinalIgnoreCase))));
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
