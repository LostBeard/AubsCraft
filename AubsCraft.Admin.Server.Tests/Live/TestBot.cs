using System.Diagnostics;
using System.Text.Json;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// A REAL Minecraft Java client (mineflayer, bot/bot.js) joining a server or the proxy. Events come back as
/// JSON lines (spawn / message / kicked / end / error); commands go in through stdin.
/// </summary>
public sealed class TestBot : IAsyncDisposable
{
    public string Username { get; }
    private readonly Process _process;
    private readonly List<JsonElement> _events = [];

    private TestBot(string username, Process process)
    {
        Username = username;
        _process = process;
    }

    /// <summary>The bot folder (package.json + bot.js), found by walking up from the test binaries to the project.</summary>
    private static string BotDir()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "bot", "bot.js"))) dir = dir.Parent;
        return dir == null
            ? throw new DirectoryNotFoundException("bot/bot.js not found above " + TestContext.CurrentContext.TestDirectory)
            : Path.Combine(dir.FullName, "bot");
    }

    /// <param name="brand">The client brand it reports: "vanilla" (plain Minecraft, the default) or e.g. "fabric" (modded).</param>
    public static TestBot Start(string host, int port, string username, string brand = "vanilla")
    {
        var dir = BotDir();
        if (!Directory.Exists(Path.Combine(dir, "node_modules", "mineflayer")))
            throw new InvalidOperationException($"Run 'npm ci' in {dir} first (installs mineflayer).");
        var psi = new ProcessStartInfo("node", ["bot.js", host, port.ToString(), username, "1.21.5", brand])
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Node writes UTF-8; the default (the console's code page) turned the locator bar's "⬤" into mojibake.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("node did not start");
        KillOnExitJob.Add(p);
        var bot = new TestBot(username, p);
        p.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data) || !e.Data.StartsWith('{')) return;
            var evt = JsonDocument.Parse(e.Data).RootElement.Clone();
            lock (bot._events) bot._events.Add(evt);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (bot._events) bot._events.Add(JsonDocument.Parse(JsonSerializer.Serialize(new { @event = "stderr", text = e.Data })).RootElement.Clone());
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return bot;
    }

    public List<JsonElement> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public static string Kind(JsonElement e) => e.GetProperty("event").GetString()!;

    /// <summary>A readable dump of everything the bot reported (for assertion messages).</summary>
    public string Transcript => string.Join(" | ", Events.Select(e => e.GetRawText()));

    /// <summary>Waits for an event matching <paramref name="match"/>; returns it, or null on timeout.</summary>
    public async Task<JsonElement?> WaitForAsync(Func<JsonElement, bool> match, TimeSpan timeout, int skip = 0)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            var hit = Events.Skip(skip).Where(match).Cast<JsonElement?>().FirstOrDefault();
            if (hit != null) return hit;
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>Waits for spawn; fails with the transcript if the bot was kicked or errored instead.</summary>
    public async Task WaitForSpawnAsync(TimeSpan timeout)
    {
        var e = await WaitForAsync(x => Kind(x) is "spawn" or "kicked" or "end", timeout);
        if (e == null || Kind(e.Value) != "spawn")
            Assert.Fail($"{Username} did not spawn: {Transcript}");
    }

    public async Task ChatAsync(string text)
    {
        await _process.StandardInput.WriteLineAsync("chat " + text);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>Walks forward (the way the bot faces) for a moment - real movement, as a player stepping away.</summary>
    public async Task WalkAsync(TimeSpan duration)
    {
        await _process.StandardInput.WriteLineAsync("walk " + (int)duration.TotalMilliseconds);
        await _process.StandardInput.FlushAsync();
        await Task.Delay(duration + TimeSpan.FromMilliseconds(500));
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                await _process.StandardInput.WriteLineAsync("quit");
                await _process.StandardInput.FlushAsync();
            }
            catch (IOException) { }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
        }
        _process.Dispose();
    }
}
