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

    /// <param name="jvmOptions">Extra JVM options before -jar (e.g. -Dname=value test switches).</param>
    public static async Task<VelocityProcess> StartAsync(string dir, TimeSpan timeout, params string[] jvmOptions)
    {
        var psi = new ProcessStartInfo(await Jdk.JavaAsync(), ["-Xms256M", "-Xmx512M", .. jvmOptions, "-jar", "velocity.jar"])
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

    /// <summary>
    /// Waits until the proxy's Geyser has started and proves it listens where transport "both" says
    /// (<see cref="Services.ProxyService.WriteGeyserBedrock"/>): RakNet on <paramref name="bedrockPort"/> UDP, built-in
    /// NetherNet signaling on <paramref name="bedrockPort"/> TCP, NetherNet WebRTC on <paramref name="webRtcPort"/> UDP.
    /// Geyser's own "[geyser]: Done (" line first, then each port is probed: a UDP bind from here must fail as in use,
    /// the TCP port must accept a connection.
    /// Not a wait for "Started Geyser on UDP port N": Geyser 2.12 does not print it when NetherNet is on, and these
    /// tests download the newest Geyser, so its wording is not a contract; the ports are.
    /// </summary>
    public async Task WaitForGeyserAsync(int bedrockPort, int webRtcPort, TimeSpan timeout)
    {
        await WaitForLineAsync("[geyser]: Done (", timeout);
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            bool raknet = UdpHeld(bedrockPort), webRtc = UdpHeld(webRtcPort), signaling = await TcpAccepts(bedrockPort);
            if (raknet && webRtc && signaling) return;
            if (DateTime.UtcNow > end)
                Assert.Fail($"Geyser said Done but not every Bedrock listener is up: RakNet UDP {bedrockPort} {(raknet ? "held" : "FREE")}, " +
                    $"signaling TCP {bedrockPort} {(signaling ? "accepts" : "REFUSED")}, NetherNet UDP {webRtcPort} {(webRtc ? "held" : "FREE")}. " +
                    $"Last output:\n{string.Join('\n', Output.TakeLast(30))}");
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// Is a UDP socket bound to this port, from the OS's own table (what netstat lists)? Not a bind attempt: Geyser's
    /// RakNet socket is exclusive (a second bind fails with AccessDenied), but its NetherNet socket is shareable (a second
    /// bind SUCCEEDS while Geyser holds it on ::), so a bind could not tell a held NetherNet port from a free one
    /// (2026-11-02, Geyser 2.12.1, checked with Get-NetUDPEndpoint against the proxy's PID).
    /// </summary>
    private static bool UdpHeld(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == port);

    private static async Task<bool> TcpAccepts(int port)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        try { await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2)); return true; }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or TimeoutException) { return false; }
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
