using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// How the world LOADS in the real published viewer: the render worker's own "[Voxel]" line (every 5 s) as a time series from page open
/// until the stream settles, saved as load.csv beside the shots. Run against any build (AUBS_VIEWER_TAG names it), so two builds compare
/// on the same world.
///
/// Gates what this build is for: the quads of all columns live in a few large buffers, so a pass binds a handful of buffers while it
/// draws hundreds of columns; and a column is meshed again once for its arriving neighbours, not once per neighbour.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
///   AUBS_VIEWER_TAG      run name (default "load")
///   AUBS_VIEWER_LOAD_SECONDS  how long to sample after the page opens (default 100)
/// </summary>
[NonParallelizable]
public class ViewerLoadTests
{
    // "[Voxel] 133 fps | frame CPU 3.2 ms (record 2.9) | 837 draws, 837 binds | 2478/8125 sections | 2226 columns+far | 460K/1635K quads"
    // and, from the quad-buffer build on, " | 2900 meshes (410 re-meshes, 3 waiting) in 2 quad buffers"
    private static readonly Regex Line = new(
        @"\[Voxel\] (?<fps>\d+) fps \| frame CPU (?<cpu>[\d.]+) ms \(record (?<rec>[\d.]+)\) \| (?<draws>\d+) draws, (?<binds>\d+) binds \| " +
        @"(?<vs>\d+)/(?<ts>\d+) sections \| (?<cols>\d+) columns\+far \| (?<vq>\d+)K/(?<tq>\d+)K quads" +
        @"(?: \| (?<meshes>\d+) meshes \((?<re>\d+) re-meshes, (?<wait>\d+) waiting\) in (?<blocks>\d+) quad buffers)?" +
        @"(?: \| far (?<fm>\d+)/(?<ft>\d+) meshed)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record Sample(double Seconds, int Fps, double FrameCpuMs, double RecordMs, int Draws, int Binds, int VisibleSections,
        int TotalSections, int Columns, long VisibleQuads, long TotalQuads, int Meshes, int Remeshes, int Waiting, int QuadBuffers, int FarMeshed, int FarTotal)
    {
        public static Sample? TryParse(double seconds, string text)
        {
            var m = Line.Match(text);
            if (!m.Success) return null;
            int I(string g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : -1;
            return new Sample(seconds, I("fps"), double.Parse(m.Groups["cpu"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["rec"].Value, CultureInfo.InvariantCulture), I("draws"), I("binds"), I("vs"), I("ts"), I("cols"),
                I("vq") * 1000L, I("tq") * 1000L, I("meshes"), I("re"), I("wait"), I("blocks"), I("fm"), I("ft"));
        }
    }

    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "load";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) try { File.WriteAllText(Path.Combine(_outDir, "load-server.log"), _rig.ServerLog); } catch { }
        if (_rig != null) await _rig.DisposeAsync();
    }

    [Test]
    public async Task Load_FillsTheWorld_BindingAHandfulOfBuffers()
    {
        var page = await _rig.Context.NewPageAsync();
        var errors = new List<string>();
        var consoleLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var samples = new System.Collections.Concurrent.ConcurrentQueue<Sample>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void OnLine(string type, string text)
        {
            consoleLog.Enqueue($"[{type}] {text}");
            if (Sample.TryParse(clock.Elapsed.TotalSeconds, text) is { } s) samples.Enqueue(s);
        }
        page.Console += (_, m) => { OnLine("page " + m.Type, m.Text); if (m.Type == "error") errors.Add(m.Text); };
        page.Worker += (_, w) => w.Console += (_, m) => OnLine("worker " + m.Type, m.Text);
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            clock.Restart();
            await page.GotoAsync(_rig.BaseUrl + "/map", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            // A fixed window, not "until it settles": the far field is the whole world and keeps streaming for minutes
            int window = int.TryParse(Environment.GetEnvironmentVariable("AUBS_VIEWER_LOAD_SECONDS"), out var c) ? c : 100;
            var sampleUntil = DateTime.UtcNow.AddSeconds(window);
            while (DateTime.UtcNow < sampleUntil)
            {
                await Task.Delay(1000);
                await ViewerStats.FailIfTheViewerFailedAsync(page);
            }
            double loadSeconds = clock.Elapsed.TotalSeconds;

            var all = samples.ToArray();
            var csv = new StringBuilder("seconds,fps,frame_cpu_ms,record_ms,draws,binds,visible_sections,total_sections,columns,visible_quads,total_quads,meshes,remeshes,waiting,quad_buffers,far_meshed,far_total\n");
            foreach (var s in all)
                csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{s.Seconds:F1},{s.Fps},{s.FrameCpuMs:F1},{s.RecordMs:F1},{s.Draws},{s.Binds},{s.VisibleSections},{s.TotalSections},{s.Columns},{s.VisibleQuads},{s.TotalQuads},{s.Meshes},{s.Remeshes},{s.Waiting},{s.QuadBuffers},{s.FarMeshed},{s.FarTotal}"));
            File.WriteAllText(Path.Combine(_outDir, "load.csv"), csv.ToString());

            Assert.That(all, Is.Not.Empty, "The render worker logged no [Voxel] line");
            var last = all[^1];
            var busiest = all.OrderByDescending(s => s.Draws).First();
            TestContext.Progress.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[load] sampled {window} s (page open {loadSeconds:F0} s); {all.Length} samples; final {last.Fps} fps, frame CPU {last.FrameCpuMs:F1} ms (record {last.RecordMs:F1}), " +
                $"{last.Draws} draws, {last.Binds} binds, {last.Columns} columns+far, meshes {last.Meshes} (re-meshes {last.Remeshes}), {last.QuadBuffers} quad buffers, far {last.FarMeshed}/{last.FarTotal} meshed; " +
                $"slowest sample {all.Min(s => s.Fps)} fps / {all.Max(s => s.FrameCpuMs):F1} ms"));

            Assert.That(errors, Is.Empty, "The page logged errors");
            Assert.That(busiest.Draws, Is.GreaterThan(100), "A real scene: hundreds of columns drawn");

            if (last.QuadBuffers >= 0) // this build pools its quads
            {
                Assert.That(busiest.Binds * 10, Is.LessThanOrEqualTo(busiest.Draws), $"{busiest.Draws} draws should need a small fraction of that many buffer binds, got {busiest.Binds}");
                Assert.That(last.QuadBuffers, Is.InRange(1, 6), "the whole world fits a few 16 MiB quad buffers");
                // Only the far chunks within reach of the camera hold a mesh: a circle of (30 + 2) chunks is 3217 chunks, the rest are heightmaps
                if (last.FarMeshed >= 0) Assert.That(last.FarMeshed, Is.LessThanOrEqualTo(3300), $"{last.FarMeshed} of {last.FarTotal} far chunks are meshed; those beyond the fog should not be");
                Assert.That(last.Remeshes, Is.LessThan(last.Meshes), "re-meshes are a part of the meshing, not all of it");
            }
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "load-console.log"), consoleLog); } catch { }
            await page.CloseAsync();
        }
    }
}
