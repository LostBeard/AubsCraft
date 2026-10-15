using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Block states in the real viewer over the real world: finds where the world has rails (curved and sloping ones first), logs lying down, furnaces and crops (from the same
/// ids the viewer streams, and the registry's state names), and shoots each from a fixed camera. The shots are for the eye; the assertions are that the world has states at all,
/// that they arrive as state names, and that the page logs no errors.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      run name (default "states")
/// </summary>
[NonParallelizable]
public class ViewerStatesTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "states";
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
    public async Task TheWorldsStates_AreShot_FromTheirOwnSpots()
    {
        // The ids of the chunks around the spawn, as the viewer receives them, with the registry's names (state names among them)
        int scx = 240 >> 4, scz = -256 >> 4;
        var chunks = new Dictionary<(int, int), ushort[]>();
        for (int ring = 0; ring <= 18; ring++)
            for (int dz = -ring; dz <= ring; dz++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                    var r = await _rig.Context.APIRequest.GetAsync($"{_rig.BaseUrl}/api/world/chunk-ids/{scx + dx}/{scz + dz}");
                    if (!r.Ok) continue;
                    var bytes = await r.BodyAsync();
                    var ids = new ushort[bytes.Length / 2];
                    Buffer.BlockCopy(bytes, 0, ids, 0, bytes.Length);
                    chunks[(scx + dx, scz + dz)] = ids;
                }
        var namesResponse = await _rig.Context.APIRequest.GetAsync($"{_rig.BaseUrl}/api/world/blocks");
        var names = JsonSerializer.Deserialize<string[]>(await namesResponse.TextAsync())!;
        var stateNames = names.Where(n => n.Contains('[')).ToList();
        TestContext.Progress.WriteLine($"[states] {chunks.Count} chunks, {names.Length} registry names, {stateNames.Count} with states: {string.Join(", ", stateNames.Take(40))}");
        Assert.That(stateNames, Is.Not.Empty, "the server names blocks with their states");

        // Where is each wanted block? (first match in ring order; cells above y 0 of the world array)
        (int x, int y, int z)? Find(Func<string, bool> wanted)
        {
            var ok = new HashSet<ushort>(Enumerable.Range(0, names.Length).Where(i => wanted(names[i])).Select(i => (ushort)i));
            if (ok.Count == 0) return null;
            foreach (var ((cx, cz), ids) in chunks)
                for (int y = 0; y < 384; y++)
                    for (int z = 0; z < 16; z++)
                        for (int x = 0; x < 16; x++)
                            if (ok.Contains(ids[x + z * 16 + y * 256])) return (cx * 16 + x, y - 64, cz * 16 + z);
            return null;
        }

        var targets = new (string label, Func<string, bool> wanted)[]
        {
            ("rail-curve", n => n.StartsWith("minecraft:rail[") && (n.Contains("south_east") || n.Contains("south_west") || n.Contains("north_east") || n.Contains("north_west"))),
            ("rail-slope", n => n.Contains("rail[") && n.Contains("ascending")),
            ("rail-straight", n => n.Contains("rail[") && (n.Contains("north_south") || n.Contains("east_west"))),
            ("log-lying", n => n.Contains("_log[axis=x") || n.Contains("_log[axis=z") || n.Contains("_wood[axis=x") || n.Contains("_wood[axis=z")),
            ("furnace", n => n.Contains("furnace[") || n.Contains("dispenser[") || n.Contains("dropper[") || n.Contains("observer[")),
            ("crop", n => n.StartsWith("minecraft:wheat[") || n.StartsWith("minecraft:carrots[") || n.StartsWith("minecraft:potatoes[") || n.StartsWith("minecraft:beetroots[")),
            ("pillar", n => n.Contains("pillar[") || n.Contains("hay_block[") || n.Contains("bone_block[") || n.Contains("basalt[")),
            ("glazed", n => n.Contains("glazed_terracotta[")),
            ("pumpkin", n => n.Contains("pumpkin[") || n.Contains("jack_o_lantern[")),
        };

        // Where the whole world has them (RealWorldFinderTests reads every region file): shot first, the nearby ones above are the fallback
        var locations = Environment.GetEnvironmentVariable("AUBS_STATES_LOCATIONS") ?? "D:/users/tj/Projects/_scratch/states-locations.json";
        var known = new List<(string label, int x, int y, int z, string name)>();
        if (File.Exists(locations))
            foreach (var kind in JsonDocument.Parse(File.ReadAllText(locations)).RootElement.EnumerateObject())
                foreach (var place in kind.Value.EnumerateArray().Take(2))
                    known.Add((kind.Name, place.GetProperty("x").GetInt32(), place.GetProperty("y").GetInt32(), place.GetProperty("z").GetInt32(), place.GetProperty("name").GetString()!));

        // AUBS_STATES_ONLY=rail-slope,rail-curve: only these kinds (a quick look at one thing)
        if (Environment.GetEnvironmentVariable("AUBS_STATES_ONLY") is { Length: > 0 } only)
            known = known.Where(k => only.Split(',').Contains(k.label)).ToList();

        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        var shot = 0;
        try
        {
            foreach (var (label, x, y, z, name) in known)
            {
                TestContext.Progress.WriteLine($"[states] {label}: {name} at {x},{y},{z}");
                // Close, from inside the air above it: straight down from 1.9 above, and from 2.5 south and 1.7 up, tipped down
                int n = known.Where(k => k.label == label).ToList().FindIndex(k => k.x == x && k.z == z && k.y == y);
                foreach (var (suffix, cam) in new[]
                {
                    ("down", $"{F(x + 0.5f)},{F(y + 2.2f)},{F(z + 0.5f)},-89,-90"),
                    ("side", $"{F(x + 0.5f)},{F(y + 2.0f)},{F(z + 3f)},-30,-90"),
                    ("far", $"{F(x + 0.5f)},{F(y + 1.2f)},{F(z + 9f)},-2,-90"),
                    ("far-east", $"{F(x + 9f)},{F(y + 1.5f)},{F(z + 0.5f)},-5,180"),
                })
                {
                    await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=8&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
                    await ViewerShotTests.WaitForSettledAsync(page, minSections: 40, quietSeconds: 4, maxSeconds: 70);
                    await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, $"world-{label}-{n}-{suffix}.png") });
                    shot++;
                }
            }
            foreach (var (label, wanted) in known.Count > 0 ? Array.Empty<(string, Func<string, bool>)>() : targets)
            {
                var at = Find(wanted);
                if (at is null) { TestContext.Progress.WriteLine($"[states] {label}: none in the {chunks.Count} chunks around the spawn"); continue; }
                var (x, y, z) = at.Value;
                var what = names.Where(wanted).Take(3);
                TestContext.Progress.WriteLine($"[states] {label}: at {x},{y},{z} ({string.Join(" / ", what)})");
                // From the south looking north at 4 blocks and 3 above, tipped down; then straight down from 7 above
                foreach (var (suffix, cam) in new[]
                {
                    ("front", $"{F(x + 0.5f)},{F(y + 3f)},{F(z + 5.5f)},-30,-90"),
                    ("top", $"{F(x + 0.5f)},{F(y + 7f)},{F(z + 0.5f)},-89,-90"),
                })
                {
                    await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=10&cam={cam}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
                    await ViewerShotTests.WaitForSettledAsync(page, minSections: 60, quietSeconds: 5, maxSeconds: 80);
                    await page.Locator("#map-canvas").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(_outDir, $"states-{label}-{suffix}.png") });
                    shot++;
                }
            }
            Assert.That(shot, Is.GreaterThan(0), "the world has at least one of the stateful blocks near the spawn");
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally { await page.CloseAsync(); }
    }
}
