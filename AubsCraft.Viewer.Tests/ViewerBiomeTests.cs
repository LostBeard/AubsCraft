using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Grass, leaves and water take the colour of their biome, in the real viewer over the real world: the same ground seen from above in plains and in savanna is not the same green, in a
/// ocean the water is the ocean's blue, and the border of two biomes is a blend. The places come from RealWorldBiomesTests (AUBS_BIOMES_OUT, default a file in the temp folder).
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
/// </summary>
[NonParallelizable]
public class ViewerBiomeTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "biomes";
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

    private static List<(int cx, int cz)> PlacesOf(string biome)
    {
        var file = Environment.GetEnvironmentVariable("AUBS_BIOMES_OUT") ?? Path.Combine(Path.GetTempPath(), "aubs-biomes.txt");
        if (!File.Exists(file)) Assert.Ignore("Run RealWorldBiomesTests first: no " + file);
        var line = File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith(biome + "|"));
        if (line == null) Assert.Ignore($"The world has no {biome}");
        var places = line.Split('|')[2].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split(',')).Select(p => (int.Parse(p[0]), int.Parse(p[1]))).ToList();
        if (places.Count == 0) Assert.Ignore($"The world has no whole chunk of {biome}");
        return places;
    }

    private static (int cx, int cz) PlaceOf(string biome) => PlacesOf(biome)[0];

    // The nearest pair of chunks, one of each biome
    private static ((int cx, int cz) a, (int cx, int cz) b) NearestPair(string biomeA, string biomeB)
        => PlacesOf(biomeA).SelectMany(a => PlacesOf(biomeB).Select(b => (a, b))).OrderBy(p => Math.Abs(p.a.cx - p.b.cx) + Math.Abs(p.a.cz - p.b.cz)).First();

    // Straight down from above a chunk's middle: the ground under it fills the frame
    private async Task<string> ShootAboveAsync(IPage page, string biome, string name, float height = 70f)
    {
        var (cx, cz) = PlaceOf("minecraft:" + biome);
        var cam = $"{F(cx * 16 + 8)},{F(height)},{F(cz * 16 + 8)},-90,0";
        await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=4&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
        await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 4, maxSeconds: 60);
        var file = Path.Combine(_outDir, name + ".png");
        await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = file });
        return file;
    }

    // How yellow-green the grass-like pixels are: the mean of (red - green) over the pixels whose green is the strongest channel or close to it (leaves, grass), leaving out sand, water and gray
    private static double GreenBias(string png)
    {
        using var bmp = new Bitmap(png);
        double sum = 0; int n = 0;
        for (int y = 0; y < bmp.Height; y += 3)
            for (int x = 0; x < bmp.Width; x += 3)
            {
                var p = bmp.GetPixel(x, y);
                if (p.G < 60 || p.B > p.G || p.G < p.R - 30) continue;   // not grass-like
                sum += p.R - p.G; n++;
            }
        return n < 200 ? double.NaN : sum / n;
    }

    [Test]
    public async Task TheSameGrass_IsAnotherGreen_InAnotherBiome()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            var plains = await ShootAboveAsync(page, "plains", "plains-above");
            var savanna = await ShootAboveAsync(page, "savanna", "savanna-above");
            var forest = await ShootAboveAsync(page, "forest", "forest-above");
            double bp = GreenBias(plains), bs = GreenBias(savanna), bf = GreenBias(forest);
            TestContext.Progress.WriteLine($"[biomes] red minus green of the grass: plains {bp:F1}, savanna {bs:F1}, forest {bf:F1}");
            Assert.That(log.Any(l => Regex.IsMatch(l, @"\[RenderWorker\] Biome colours: \d+ biomes")), Is.True, "the biome colours loaded");
            Assert.That(double.IsNaN(bp) || double.IsNaN(bs), Is.False, "grass-like pixels are in both frames");
            // Plains grass is 91BD59 (green leads red by 48), savanna BFB755 (by 8): the savanna's grass is clearly more yellow
            Assert.That(bs - bp, Is.GreaterThan(20), "savanna grass is yellower than plains grass");
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-biomes.log"), log); } catch { }
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task TheBorderOfTwoBiomes_IsShot_ForTheEye()
    {
        var ((sx, sz), (px, pz)) = NearestPair("minecraft:savanna", "minecraft:plains");
        var page = await _rig.Context.NewPageAsync();
        try
        {
            // Between the two places, high, looking down at an angle
            float mx = (sx + px) * 8f + 8f, mz = (sz + pz) * 8f + 8f;
            var cam = $"{F(mx)},{F(110)},{F(mz + 60)},-50,-90";
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=10&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 4, maxSeconds: 60);
            await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, "border.png") });
        }
        finally { await page.CloseAsync(); }
    }
}
