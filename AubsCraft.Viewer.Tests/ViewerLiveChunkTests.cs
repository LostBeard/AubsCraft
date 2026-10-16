using System.Drawing;
using System.Text;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The viewer shows the world as it is NOW: the panel is sent a changed chunk (as the plugin sends it while the server runs), tells the viewer, and the viewer's column changes on screen - a gold tower
/// appears where it looks, and goes again when the chunk comes without it - with no reload.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerLiveChunkTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "livechunk";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    // The plugin's chunk update (LiveChunkReader.cs): a flat grass floor at y -61, and a tower of gold blocks 3 x 3 from y -60 up to -53 when asked
    private static byte[] Chunk(bool tower, int towerX, int towerZ)
    {
        const int top = 20;   // layers from the bottom: up to y -45
        var palette = new[] { "minecraft:air", "minecraft:stone", "minecraft:grass_block[snowy=false]", "minecraft:gold_block" };
        using var ms = new MemoryStream();
        void U8(int v) => ms.WriteByte((byte)v);
        void U16(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
        U8(1); U8(0); U16(top); U16(palette.Length);
        foreach (var p in palette) { var b = Encoding.UTF8.GetBytes(p); U16(b.Length); ms.Write(b); }
        for (int y = 0; y < top; y++)
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int wy = y - 64;
                    int id = wy < -61 ? 1 : wy == -61 ? 2 : 0;
                    if (tower && wy >= -60 && wy <= -53 && Math.Abs(x - towerX) <= 1 && Math.Abs(z - towerZ) <= 1) id = 3;
                    U16(id);
                }
        for (int i = 0; i < 256 * top; i++) U8(0xF0);
        for (int i = 0; i < 256; i++) U16(4);
        U16(0); U16(0);
        return ms.ToArray();
    }

    private static int Gold(Bitmap b)
    {
        int n = 0;
        for (int y = 6; y < b.Height - 6; y += 2)
            for (int x = 6; x < b.Width - 6; x += 2)
            {
                var p = b.GetPixel(x, y);
                if (p.R > 50 && p.R >= p.G && p.B * 2 < p.G) n++;   // gold: red at least as strong as green, little blue (grass is greener than it is red)
            }
        return n;
    }

    [Test]
    public async Task AChangedChunk_ChangesWhatTheViewerShows_WithoutAReload()
    {
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            // Looking west over the open grass at the scene's edge, from x -2.5: the chunk (-1, 0) is in front, the tower will stand 5 blocks ahead
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&light=0&cam=-2.5,-58.0,2.0,4,180", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            // The live socket is open once frames come (the viewer subscribes at start); give it a moment
            await Task.Delay(3000);

            var before = Path.Combine(_outDir, "before.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = before });

            Assert.That(await _rig.PostLiveChunkAsync(-1, 0, Chunk(tower: true, towerX: 8, towerZ: 2)), Is.EqualTo(204), "the panel took the chunk");
            await Task.Delay(4000);
            var withTower = Path.Combine(_outDir, "tower.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = withTower });

            Assert.That(await _rig.PostLiveChunkAsync(-1, 0, Chunk(tower: false, towerX: 8, towerZ: 2)), Is.EqualTo(204));
            await Task.Delay(4000);
            var gone = Path.Combine(_outDir, "gone.png");
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = gone });

            using var a = new Bitmap(before); using var b = new Bitmap(withTower); using var c = new Bitmap(gone);
            int ga = Gold(a), gb = Gold(b), gc = Gold(c);
            TestContext.Progress.WriteLine($"[livechunk] gold pixels: before {ga}, with the tower {gb}, after it is taken away {gc}");
            Assert.That(gb, Is.GreaterThan(ga + 400), "the tower shows");
            Assert.That(gc, Is.LessThan(gb / 4), "and goes again");
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally { await page.CloseAsync(); }
    }
}
