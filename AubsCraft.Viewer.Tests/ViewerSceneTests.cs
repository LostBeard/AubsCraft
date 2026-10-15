using System.Globalization;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The viewer over the SCENE world (SceneBuilderTests builds it on a local Paper server: every stateful block at a known place): each item shot from three sides. For the eye; the
/// assertion is that the page logs no errors. Set AUBS_VIEWER_WORLD to the scene folder (default D:/users/tj/Projects/_scratch/scene-server) and AUBS_SCENE_ITEMS to the items file.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_ONLY      comma list of item labels to shoot (default all)
/// </summary>
[NonParallelizable]
public class ViewerSceneTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "scene";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

    [Test]
    public async Task TheSceneItems_AreShot()
    {
        var itemsFile = Environment.GetEnvironmentVariable("AUBS_SCENE_ITEMS") ?? "D:/users/tj/Projects/_scratch/scene-items.txt";
        var items = File.ReadAllLines(itemsFile).Select(l => l.Split('|')).Select(p => (label: p[0], x: int.Parse(p[1]), y: int.Parse(p[2]), z: int.Parse(p[3]))).ToList();
        if (Environment.GetEnvironmentVariable("AUBS_SCENE_ONLY") is { Length: > 0 } only) items = items.Where(i => only.Split(',').Any(o => i.label.StartsWith(o))).ToList();

        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            foreach (var (label, x, y, z) in items)
            {
                // From the south (looking north), from the east (looking west), and from above
                foreach (var (suffix, cam) in new[]
                {
                    ("south", $"{F(x + 0.5f)},{F(y + 2.2f)},{F(z + 5.5f)},-18,-90"),
                    ("east", $"{F(x + 6f)},{F(y + 2.2f)},{F(z + 0.5f)},-18,180"),
                    ("top", $"{F(x + 0.5f)},{F(y + 7f)},{F(z + 0.5f)},-89,-90"),
                    ("diag", $"{F(x + 4f)},{F(y + 2.2f)},{F(z + 4f)},-22,-135"),
                    ("profile", $"{F(x + 2.5f)},{F(y + 0.5f)},{F(z + 8f)},0,-90"),
                })
                {
                    await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
                    await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
                    await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, $"{label}-{suffix}.png") });
                }
            }
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally { await page.CloseAsync(); }
    }
}
