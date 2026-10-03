using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Models;
using Microsoft.AspNetCore.SignalR;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// The panel's side of the proxy: what is set up, whether the machine is ready for the cutover, and running
/// the cutover in the background with its progress pushed live to every connected admin.
/// </summary>
public class ProxyOperationsService
{
    private readonly ServerRegistry _registry;
    private readonly ProxyService _proxy;
    private readonly ProxyCutoverService _cutover;
    private readonly IHubContext<ServerHub, IServerHubClient> _hub;
    private readonly ILogger<ProxyOperationsService> _logger;
    private readonly List<string> _log = [];
    private int _running;

    public ProxyOperationsService(ServerRegistry registry, ProxyService proxy, ProxyCutoverService cutover,
        IHubContext<ServerHub, IServerHubClient> hub, ILogger<ProxyOperationsService> logger)
    {
        _registry = registry;
        _proxy = proxy;
        _cutover = cutover;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>The cutover the panel proposes for a server: proxy on its public ports, the server moved inside.</summary>
    public CutoverPreviewDto Preview(string serverId)
    {
        var server = _registry.Get(serverId) ?? throw new HubException($"Unknown server '{serverId}'.");
        var proxy = new ProxyDefinition();
        var used = _registry.All.Select(s => s.GamePort).ToHashSet();
        var newPort = Enumerable.Range(proxy.Port + 1, 100).First(p => !used.Contains(p));
        var voice = proxy.VoicePort + 1;
        return new CutoverPreviewDto(server.Id, server.Name, proxy.Port, proxy.BedrockPort, proxy.VoicePort, newPort, voice,
            CheckPrerequisites(proxy), Log);
    }

    /// <summary>What must be true on this machine before the cutover can run (the one-time root setup script).</summary>
    public List<PrerequisiteDto> CheckPrerequisites(ProxyDefinition proxy)
    {
        var list = new List<PrerequisiteDto>();
        bool writable = false;
        try
        {
            if (Directory.Exists(proxy.Path))
            {
                var probe = Path.Combine(proxy.Path, ".panel-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                writable = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        list.Add(new PrerequisiteDto($"{proxy.Path} exists and the panel can write to it", writable,
            "Run server-setup/setup-multiserver.sh once with sudo."));
        var unit = $"/etc/systemd/system/{proxy.ServiceName}.service";
        list.Add(new PrerequisiteDto($"{proxy.ServiceName}.service is installed", File.Exists(unit),
            "Run server-setup/setup-multiserver.sh once with sudo."));
        list.Add(new PrerequisiteDto("No proxy is set up yet", _registry.Proxy == null, "The cutover runs once."));
        return list;
    }

    public List<string> Log
    {
        get { lock (_log) return _log.ToList(); }
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Starts the cutover in the background. Progress is pushed with ReceiveOperationProgress.</summary>
    public string StartCutover(string serverId, int newGamePort, int voicePort, string requestedBy)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return "A proxy operation is already running.";
        var proxy = new ProxyDefinition();
        var failed = CheckPrerequisites(proxy).Where(p => !p.Ok).ToList();
        if (failed.Count > 0)
        {
            Volatile.Write(ref _running, 0);
            return "Not ready: " + string.Join("; ", failed.Select(f => f.What));
        }
        lock (_log) _log.Clear();
        _logger.LogWarning("Proxy cutover of '{Server}' started by {User}", serverId, requestedBy);
        var plan = new ProxyCutoverService.CutoverPlan(serverId, proxy, newGamePort, voicePort);
        _ = Task.Run(async () =>
        {
            try
            {
                await _cutover.CutoverAsync(plan, new Progress<string>(Report));
                await PushAsync("Finished.", done: true, failed: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proxy cutover failed");
                await PushAsync("Cutover failed: " + ex.Message, done: true, failed: true);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return "Cutover started.";
    }

    private void Report(string line) => _ = PushAsync(line, done: false, failed: false);

    private Task PushAsync(string line, bool done, bool failed)
    {
        lock (_log) _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        return _hub.Clients.All.ReceiveOperationProgress(new OperationProgressDto("proxy-cutover", line, done, failed));
    }

    /// <summary>Proxy status for the panel: null when no proxy is set up.</summary>
    public async Task<ProxyStatusDto?> StatusAsync()
    {
        var proxy = _registry.Proxy;
        if (proxy == null) return null;
        string? players = null;
        bool online;
        try
        {
            players = await _proxy.CommandAsync(proxy, "glist all");
            online = true;
        }
        catch (Exception)
        {
            online = false;
        }
        return new ProxyStatusDto(online, proxy.Port, proxy.BedrockPort, proxy.VoicePort, proxy.VelocityVersion, players);
    }
}

public record PrerequisiteDto(string What, bool Ok, string Fix);
public record CutoverPreviewDto(string ServerId, string ServerName, int PublicPort, int BedrockPort, int VoicePort,
    int NewGamePort, int NewVoicePort, List<PrerequisiteDto> Prerequisites, List<string> Log);
public record ProxyStatusDto(bool Online, int Port, int BedrockPort, int VoicePort, string Version, string? Players);
public record OperationProgressDto(string Operation, string Message, bool Done, bool Failed);
