using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The writing on signs in the real viewer, over the SCENE world (SceneBuilderTests gives its signs words: the standing ones in red, saying which way they face, the wall ones in black, one glowing
/// blue): each sign is shot from the front, close, so the writing can be read in the picture, and the worker says how many signs have writing drawn.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerSignTextTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "signtext";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    // The scene's sign row is at z 82 (standing) and 86 (on walls), y -60, x 2, 5, 8, 11 facing south, west, north, east
    private static readonly (string name, string cam)[] Views =
    {
        ("standing-south", "2.5,-58.7,85.2,6,-90"),    // in front of the south-facing sign (it faces +z), looking north
        ("standing-west", "1.8,-58.7,82.5,6,0"),       // in front of the west-facing one (x 5, faces -x), looking east... from x 1.8 is behind sign 0: look at x=5 from x 2.5 is blocked: use the far side below
        ("standing-north", "8.5,-58.7,79.6,6,90"),     // the north-facing one (x 8, faces -z), looking south
        ("standing-east", "14.2,-58.7,82.5,6,180"),    // the east-facing one (x 11, faces +x), looking west
        ("wall-south", "2.5,-58.7,89.2,6,-90"),
        ("glowing", "14.5,-58.7,85.2,6,-90"),
        ("hanging-south", "21.5,-58.6,81.8,4,-90"),    // the hanging sign at x 21 faces south
        ("hanging-west", "22.3,-58.6,78.5,4,0"),       // the one at x 24 faces west
    };

    [Test]
    public async Task TheSignsWritingIsDrawn_OnTheBoardsFace_TheRightWayRound()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            foreach (var (name, cam) in Views)
            {
                await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
                await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
                await Task.Delay(2500);
                await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, name + ".png") });
            }
            Assert.That(log.ToArray().Any(l => Regex.IsMatch(l, @"sign texts [1-9]\d*/\d+")), Is.True, "signs with writing were drawn:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("[Voxel]")).TakeLast(3)));
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally { await page.CloseAsync(); }
    }
}
