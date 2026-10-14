using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The viewer's own numbers, read from the page's stats line (kept for screen readers and for this gate, though the
/// sighted view of the same numbers is the GPU HUD): "65 FPS | 3923K/4756K verts | 2705/3655 chunks | draw: 80 | 1.2 c/s |
/// Worker | XYZ 10 200 -30 | Click to fly". "chunks" there are 16x16x16 sections.
/// </summary>
public sealed record ViewerStats(int Fps, int VisibleKVerts, int TotalKVerts, int VisibleSections, int TotalSections, int DrawDistance,
    double SectionsPerSecond, int X, int Y, int Z, bool ClickToFly)
{
    private static readonly Regex Line = new(
        @"(?<fps>\d+) FPS \| (?<vis>\d+)K/(?<tot>\d+)K verts \| (?<vc>\d+)/(?<tc>\d+) chunks \| draw: (?<draw>\d+) \| (?<cps>[\d.]+) c/s \| Worker \| XYZ (?<x>-?\d+) (?<y>-?\d+) (?<z>-?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ViewerStats? TryParse(string text)
    {
        var m = Line.Match(text);
        if (!m.Success) return null;
        return new ViewerStats(
            int.Parse(m.Groups["fps"].Value), int.Parse(m.Groups["vis"].Value), int.Parse(m.Groups["tot"].Value),
            int.Parse(m.Groups["vc"].Value), int.Parse(m.Groups["tc"].Value), int.Parse(m.Groups["draw"].Value),
            double.Parse(m.Groups["cps"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["z"].Value),
            ClickToFly: text.Contains("Click to fly", StringComparison.Ordinal));
    }

    /// <summary>
    /// Fails the test at once if the page shows the viewer's own failure ("GPU init failed: ..." / "GPU not available, using
    /// BlueMap"), instead of waiting out a viewer that will never start. Only that message: the page also shows toasts as
    /// `.alert` - this local panel tails the REAL server's log, so a stranger's rejected connection is a toast here too.
    /// </summary>
    public static async Task FailIfTheViewerFailedAsync(IPage page)
    {
        var failure = page.Locator(".alert:has-text('GPU')");
        if (await failure.CountAsync() > 0)
            Assert.Fail("The viewer showed: " + (await failure.First.InnerTextAsync()).Trim());
    }

    /// <summary>The page's current stats, or null while the line is not there (or not parseable) yet.</summary>
    public static async Task<ViewerStats?> ReadAsync(IPage page)
    {
        var line = page.Locator("small.font-monospace:has-text('FPS')");
        if (await line.CountAsync() == 0) return null;
        return TryParse(await line.First.InnerTextAsync());
    }
}
