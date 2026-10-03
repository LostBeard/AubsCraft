using System.Diagnostics;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// A REAL Velocity proxy (velocity.jar + plugins in <see cref="Dir"/>) started as a Java 25 process. Output is
/// kept for assertions; "end" on the console stops it; the process dies with the test host (KillOnExitJob).
/// </summary>
public sealed class VelocityProcess : IAsyncDisposable
{
    public string Dir { get; }
    private readonly Process _process;
    private readonly List<string> _output = [];
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private VelocityProcess(string dir, Process process)
    {
        Dir = dir;
        _process = process;
    }

    public static async Task<VelocityProcess> StartAsync(string dir, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(await Jdk.JavaAsync(), ["-Xms256M", "-Xmx512M", "-jar", "velocity.jar"])
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("java did not start");
        KillOnExitJob.Add(process);
        var v = new VelocityProcess(dir, process);
        process.OutputDataReceived += (_, e) => v.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => v.OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var finished = await Task.WhenAny(v._done.Task, process.WaitForExitAsync(), Task.Delay(timeout));
        if (finished != v._done.Task)
        {
            var tail = string.Join('\n', v.Output.TakeLast(40));
            await v.DisposeAsync();
            throw new InvalidOperationException($"Velocity did not finish starting. Last output:\n{tail}");
        }
        return v;
    }

    private void OnLine(string? line)
    {
        if (line == null) return;
        lock (_output) _output.Add(line);
        // "[12:38:08 INFO]: Done (1.78s)!" - Velocity's own line (Geyser prints "[geyser]: Done" later).
        if (line.Contains("INFO]: Done (")) _done.TrySetResult();
    }

    public List<string> Output
    {
        get { lock (_output) return _output.ToList(); }
    }

    /// <summary>Waits until a line containing <paramref name="text"/> has been printed (or fails after the timeout).</summary>
    public async Task WaitForLineAsync(string text, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (Output.Any(l => l.Contains(text))) return;
            await Task.Delay(200);
        }
        Assert.Fail($"Velocity never printed '{text}'. Last output:\n{string.Join('\n', Output.TakeLast(30))}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process.HasExited) { _process.Dispose(); return; }
        try
        {
            await _process.StandardInput.WriteLineAsync("end");
            await _process.StandardInput.FlushAsync();
        }
        catch (IOException) { }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
        _process.Dispose();
    }
}
