using System.Diagnostics;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// Starts a server the way minecraft@.service does: java $JAVA_OPTS $LAUNCH nogui in its folder, with both
/// values read from the folder's aubscraft.env (written by ServerProvisioningService). Java 25, killed with
/// the test host. "stop" on the console stops it.
/// </summary>
public sealed class EnvFileProcess : IAsyncDisposable
{
    public string Dir { get; }
    private readonly Process _process;
    private readonly List<string> _output = [];

    private EnvFileProcess(string dir, Process process) { Dir = dir; _process = process; }

    public List<string> Output { get { lock (_output) return _output.ToList(); } }

    public static async Task<EnvFileProcess> StartAsync(string dir)
    {
        var env = File.ReadAllLines(Path.Combine(dir, "aubscraft.env"))
            .Where(l => l.Contains('='))
            .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
        var args = env["JAVA_OPTS"].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(env["LAUNCH"].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Append("nogui").ToList();
        var psi = new ProcessStartInfo(await Jdk.JavaAsync(), args)
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("java did not start");
        KillOnExitJob.Add(p);
        var e = new EnvFileProcess(dir, p);
        p.OutputDataReceived += (_, a) => { if (a.Data != null) lock (e._output) e._output.Add(a.Data); };
        p.ErrorDataReceived += (_, a) => { if (a.Data != null) lock (e._output) e._output.Add(a.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return e;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                await _process.StandardInput.WriteLineAsync("stop");
                await _process.StandardInput.FlushAsync();
            }
            catch (IOException) { }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await _process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
        }
        _process.Dispose();
    }
}
