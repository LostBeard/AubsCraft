namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Starts and stops the processes the panel manages (Minecraft servers, the proxy) by their service name.
/// Production: systemd units (<see cref="SystemdServiceRunner"/>). Tests: local processes, so multi-step
/// operations like the proxy cutover run the exact same code path against real servers.
/// </summary>
public interface IServiceRunner
{
    Task StartAsync(string serviceName, CancellationToken ct = default);
    Task StopAsync(string serviceName, CancellationToken ct = default);
    /// <summary>Start at boot (systemctl enable).</summary>
    Task EnableAsync(string serviceName, CancellationToken ct = default);
    /// <summary>No longer start at boot (systemctl disable).</summary>
    Task DisableAsync(string serviceName, CancellationToken ct = default);
}

/// <summary>systemctl start/stop through sudo (the panel's sudo rule allows exactly these units and verbs).</summary>
public class SystemdServiceRunner : IServiceRunner
{
    private readonly ILoggerFactory _loggers;

    public SystemdServiceRunner(ILoggerFactory loggers) => _loggers = loggers;

    public Task StartAsync(string serviceName, CancellationToken ct = default) => RunAsync(serviceName, "start");
    public Task StopAsync(string serviceName, CancellationToken ct = default) => RunAsync(serviceName, "stop");
    public Task EnableAsync(string serviceName, CancellationToken ct = default) => RunAsync(serviceName, "enable");
    public Task DisableAsync(string serviceName, CancellationToken ct = default) => RunAsync(serviceName, "disable");

    private async Task RunAsync(string serviceName, string verb)
    {
        var control = new ServerControlService(serviceName, _loggers.CreateLogger<ServerControlService>());
        var (ok, output) = await control.RunAsync(verb);
        if (!ok) throw new InvalidOperationException($"systemctl {verb} {serviceName} failed: {output}");
    }
}
