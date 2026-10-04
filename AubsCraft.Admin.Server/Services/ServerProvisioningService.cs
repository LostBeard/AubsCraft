using System.Security.Cryptography;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Creates a new Minecraft server behind the proxy, from nothing to "players can /server into it":
///   install the loader (ServerSoftwareService) -> server.properties (127.0.0.1, own ports, RCON) + the
///   primary server's whitelist/ops -> aubscraft.env (heap + launch, read by minecraft@.service) -> the
///   add-ons every server needs behind this proxy (forwarding, voice chat, Floodgate, Vivecraft) plus the
///   requested ones, each with its required dependencies -> FIRST START (the server and its add-ons write
///   their config) -> stop -> point it at the proxy -> register it -> proxy reload -> enable + start.
/// A failure at any step removes everything it created (folder, registry entry, proxy entry).
/// </summary>
public class ServerProvisioningService
{
    private readonly ServerRegistry _registry;
    private readonly ServerSoftwareService _software;
    private readonly AddonDownloader _downloader;
    private readonly ProxyService _proxy;
    private readonly IServiceRunner _runner;
    private readonly ILogger<ServerProvisioningService> _logger;

    /// <summary>Folder that holds one sub-folder per created server (minecraft@.service: /opt/minecraft/servers/%i).</summary>
    public string ServersRoot { get; set; }
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(8);

    public ServerProvisioningService(ServerRegistry registry, ServerSoftwareService software, AddonDownloader downloader,
        ProxyService proxy, IServiceRunner runner, IConfiguration configuration, ILogger<ServerProvisioningService> logger)
    {
        _registry = registry;
        _software = software;
        _downloader = downloader;
        _proxy = proxy;
        _runner = runner;
        _logger = logger;
        ServersRoot = configuration.GetValue<string>("Servers:Root") ?? "/opt/minecraft/servers";
    }

    public record CreateServerRequest(
        string Id,
        string Name,
        ServerLoader Loader,
        string GameVersion,
        int MemoryMb,
        IReadOnlyList<string> ExtraModrinthProjects,
        string? Seed = null,
        IReadOnlyList<string>? ClientMods = null);

    /// <summary>Add-ons every server behind the proxy gets (Modrinth slugs; Paper's Floodgate comes from GeyserMC).</summary>
    public static string[] BaseAddons(ServerLoader loader) => ServerSoftwareService.Effective(loader) switch
    {
        // Simple Voice Chat (voice through the proxy). Floodgate: added separately (GeyserMC download).
        ServerLoader.Paper => ["simple-voice-chat"],
        // Forwarding (FabricProxy-Lite + Fabric API), voice, Bedrock identity, VR (QuestCraft clients run Vivecraft).
        ServerLoader.Fabric => ["fabric-api", "fabricproxy-lite", "simple-voice-chat", "floodgate", "vivecraft"],
        ServerLoader.Forge => ["proxy-compatible-forge", "simple-voice-chat"],
        ServerLoader.NeoForge => ["proxy-compatible-forge", "simple-voice-chat", "floodgate"],
        _ => [],
    };

    /// <summary>Base add-ons a server can run without; skipped (with a note) when there is no build for the version.</summary>
    private static readonly HashSet<string> OptionalBase = new(StringComparer.OrdinalIgnoreCase) { "floodgate", "vivecraft", "simple-voice-chat" };

    public async Task<ServerDefinition> CreateAsync(CreateServerRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Step(string s) { _logger.LogInformation("Create '{Id}': {Step}", req.Id, s); progress?.Report(s); }

        var proxy = _registry.Proxy ?? throw new InvalidOperationException("Set up the proxy first: new servers run behind it.");
        ServerRegistry.ValidateId(req.Id);
        if (_registry.Get(req.Id) != null) throw new InvalidOperationException($"A server '{req.Id}' already exists.");
        var dir = Path.Combine(ServersRoot, req.Id);
        if (Directory.Exists(dir)) throw new InvalidOperationException($"{dir} already exists.");
        var loader = ServerSoftwareService.Effective(req.Loader);
        var primary = _registry.Primary;

        var def = new ServerDefinition
        {
            Id = req.Id,
            Name = req.Name,
            Loader = loader,
            GameVersion = req.GameVersion,
            Path = dir,
            ServiceName = $"minecraft@{req.Id}",
            RconHost = "127.0.0.1",
            RconPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            MemoryMb = req.MemoryMb,
            CreatedAt = DateTime.UtcNow,
            ClientMods = req.ClientMods?.ToList() ?? [],
        };
        AllocatePorts(def, proxy);

        var registered = false;
        try
        {
            Step($"1/7 Installing {loader} for Minecraft {req.GameVersion}");
            var installed = await _software.InstallAsync(loader, req.GameVersion, dir, ct);
            Step($"    {installed.Description}");

            Step("2/7 Writing server settings (this machine only, own ports, whitelist from the main server)");
            WriteServerFiles(def, primary, req.Seed);
            File.WriteAllText(Path.Combine(dir, "aubscraft.env"),
                $"JAVA_OPTS=-Xms{Math.Min(1024, def.MemoryMb)}M -Xmx{def.MemoryMb}M -XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200\n" +
                $"LAUNCH={installed.LaunchArgs}\n" +
                // minecraft@.service runs "${JAVA:-/usr/bin/java}": only servers that need another Java name one (Forge: 21).
                (installed.Java != null ? $"JAVA={installed.Java}\n" : ""));

            Step("3/7 Installing add-ons");
            await InstallAddonsAsync(def, req.ExtraModrinthProjects, Step, ct);

            PreseedVoiceChat(def);

            Step("4/7 First start (the server and its add-ons write their config)");
            await _runner.StartAsync(def.ServiceName, ct);
            await WaitUntilAsync(async () => Crashed(def) is { } crash ? throw new InvalidOperationException(crash) : await RconUpAsync(def, ct),
                "the first start", ct);
            await _runner.StopAsync(def.ServiceName, ct);
            await WaitUntilAsync(async () => !await RconUpAsync(def, ct), "the server to stop", ct);

            Step("5/7 Connecting it to the proxy");
            _proxy.ConfigureBackend(proxy, def);
            _registry.Add(def);
            registered = true;
            _proxy.ConfigureProxy(proxy, _registry.All);
            await _proxy.ReloadAsync(proxy, ct);

            Step("6/7 Starting it");
            await _runner.EnableAsync(def.ServiceName, ct);
            await _runner.StartAsync(def.ServiceName, ct);
            await WaitUntilAsync(() => RconUpAsync(def, ct), "the server to start", ct);

            Step($"7/7 Done: players can join with /server {def.Id}");
            return def;
        }
        catch (Exception ex)
        {
            // Read why BEFORE cleaning up (that deletes the folder and its logs).
            var why = WhyItFailed(def);
            Step($"FAILED ({ex.Message}) - removing what was created");
            foreach (var line in why) Step("    " + line);
            await CleanUpAsync(def, proxy, registered);
            if (why.Count > 0 && ex is not InvalidOperationException { Data.Count: > 0 })
                throw new InvalidOperationException($"{ex.Message} {string.Join(" | ", why.Take(4))}", ex);
            throw;
        }
    }

    /// <summary>Signs in the server's log that it will never finish starting (so the create fails now, not after the timeout).</summary>
    private static readonly string[] CrashMarkers =
        ["Incompatible mods found", "Failed to start the minecraft server", "This crash report has been saved to", "Mixin apply failed",
         "Could not execute entrypoint", "Encountered an unexpected exception"];

    /// <summary>Why the server crashed while starting, or null while it has not.</summary>
    private static string? Crashed(ServerDefinition def)
    {
        if (Directory.Exists(Path.Combine(def.Path, "crash-reports")) && Directory.GetFiles(Path.Combine(def.Path, "crash-reports")).Length > 0)
            return "The server crashed while starting.";
        var log = Path.Combine(def.Path, "logs", "latest.log");
        if (!File.Exists(log)) return null;
        string text;
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            text = new StreamReader(fs).ReadToEnd();
        }
        catch (IOException) { return null; }
        return CrashMarkers.FirstOrDefault(text.Contains) is { } m ? $"The server stopped while starting ({m})." : null;
    }

    /// <summary>
    /// The lines that say why a start failed: a crash report's description and its first "Caused by", else the log's
    /// ERROR/FATAL lines and Fabric's "Incompatible mods" explanation (the last ones, deduplicated).
    /// </summary>
    internal static List<string> WhyItFailed(ServerDefinition def)
    {
        var lines = new List<string>();
        try
        {
            var crashDir = Path.Combine(def.Path, "crash-reports");
            var crash = Directory.Exists(crashDir) ? Directory.GetFiles(crashDir).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            if (crash != null)
            {
                var text = File.ReadAllLines(crash);
                lines.AddRange(text.Where(l => l.StartsWith("Description:") || l.StartsWith("Caused by:")).Take(3));
                lines.AddRange(text.SkipWhile(l => !l.StartsWith("Description:")).Skip(1).Where(l => l.Trim().Length > 0).Take(2).Select(l => l.Trim()));
            }
            var log = Path.Combine(def.Path, "logs", "latest.log");
            if (File.Exists(log))
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var all = new StreamReader(fs).ReadToEnd().Split('\n');
                // Fabric explains a missing/incompatible mod in plain sentences right after "Incompatible mods found".
                var inc = Array.FindIndex(all, l => l.Contains("Incompatible mods found"));
                if (inc >= 0) lines.AddRange(all.Skip(inc + 1).Where(l => l.TrimStart().StartsWith("- ")).Take(6).Select(l => l.Trim()));
                lines.AddRange(all.Where(l => l.Contains("/ERROR]") || l.Contains("/FATAL]")).TakeLast(6).Select(l => l.Trim()));
                // The exception after "Failed to start the minecraft server": its message and every "Caused by" (not the stack).
                var fail = Array.FindIndex(all, l => l.Contains("Failed to start the minecraft server"));
                if (fail >= 0)
                    lines.AddRange(all.Skip(fail + 1).Take(200).Where(l => !l.TrimStart().StartsWith("at ") && !l.TrimStart().StartsWith("...")
                        && l.Trim().Length > 0 && !l.StartsWith("[")).Take(6).Select(l => l.Trim()));
            }
        }
        catch (IOException) { }
        return lines.Select(l => l.Length > 300 ? l[..300] : l).Distinct().ToList();
    }

    private async Task CleanUpAsync(ServerDefinition def, ProxyDefinition proxy, bool registered)
    {
        try { await _runner.StopAsync(def.ServiceName); } catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: stop"); }
        try { await _runner.DisableAsync(def.ServiceName); } catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: disable"); }
        if (registered)
        {
            _registry.Remove(def.Id);
            try
            {
                _proxy.ConfigureProxy(proxy, _registry.All);
                await _proxy.ReloadAsync(proxy);
            }
            catch (Exception ex) { _logger.LogError(ex, "Cleanup: proxy reload"); }
        }
        for (int i = 0; i < 5 && Directory.Exists(def.Path); i++)
        {
            try { Directory.Delete(def.Path, recursive: true); }
            catch (IOException) { await Task.Delay(1000); } // a just-stopped JVM can hold files for a moment (Windows)
        }
    }

    /// <summary>Game, RCON and voice ports not used by any server or the proxy (servers listen on 127.0.0.1 only).</summary>
    private void AllocatePorts(ServerDefinition def, ProxyDefinition proxy)
    {
        var used = _registry.All.SelectMany(s => new[] { s.GamePort, s.RconPort, s.VoicePort })
            .Concat([proxy.Port, proxy.RconPort, proxy.VoicePort, proxy.BedrockPort]).ToHashSet();
        int Next(int from) { var p = from; while (used.Contains(p)) p++; used.Add(p); return p; }
        def.GamePort = Next(25567);
        def.RconPort = Next(25577);
        def.VoicePort = Next(24456);
    }

    private static void WriteServerFiles(ServerDefinition def, ServerDefinition? primary, string? seed)
    {
        File.WriteAllText(Path.Combine(def.Path, "eula.txt"), "eula=true\n");
        var props = Path.Combine(def.Path, "server.properties");
        File.WriteAllText(props, "");
        ConfigFiles.SetProperty(props, "server-ip", "127.0.0.1");
        ConfigFiles.SetProperty(props, "server-port", def.GamePort.ToString());
        ConfigFiles.SetProperty(props, "online-mode", "false"); // the proxy authenticates
        ConfigFiles.SetProperty(props, "enable-rcon", "true");
        ConfigFiles.SetProperty(props, "rcon.port", def.RconPort.ToString());
        ConfigFiles.SetProperty(props, "rcon.password", def.RconPassword);
        ConfigFiles.SetProperty(props, "motd", def.Name);
        // VR players (Vivecraft teleport, Climb Claws, Jump Boots) would otherwise be kicked for "flying".
        ConfigFiles.SetProperty(props, "allow-flight", "true");
        if (!string.IsNullOrWhiteSpace(seed)) ConfigFiles.SetProperty(props, "level-seed", seed.Trim());
        if (primary == null) return;
        // The same people may play: same whitelist (and its on/off), same operators.
        var whitelistOn = ConfigFiles.GetProperty(Path.Combine(primary.Path, "server.properties"), "white-list");
        if (whitelistOn != null) ConfigFiles.SetProperty(props, "white-list", whitelistOn);
        foreach (var file in new[] { "whitelist.json", "ops.json" })
        {
            var src = Path.Combine(primary.Path, file);
            if (File.Exists(src)) File.Copy(src, Path.Combine(def.Path, file));
        }
    }

    private async Task InstallAddonsAsync(ServerDefinition def, IReadOnlyList<string> extra, Action<string> step, CancellationToken ct)
    {
        var loaderName = def.Loader == ServerLoader.Paper ? "paper" : def.Loader.ToString().ToLowerInvariant();
        var folder = def.AddonsPath!;
        Directory.CreateDirectory(folder);
        var artifacts = new List<AddonArtifact>();
        if (def.Loader == ServerLoader.Paper)
            artifacts.Add(await _downloader.GeyserMcAsync("floodgate", "spigot", ct));

        // Base add-ons one by one: an optional one with no build for this version is skipped, not fatal.
        var wanted = new List<string>();
        foreach (var slug in BaseAddons(def.Loader))
        {
            try
            {
                await _downloader.ModrinthAsync(slug, loaderName, def.GameVersion, ct);
                wanted.Add(slug);
            }
            catch (InvalidOperationException) when (OptionalBase.Contains(slug))
            {
                step($"    {slug}: no {def.Loader} build for {def.GameVersion}, skipped");
            }
        }
        wanted.AddRange(extra);
        artifacts.AddRange(await _downloader.ModrinthWithDependenciesAsync(wanted, loaderName, def.GameVersion, null, ct));

        foreach (var a in artifacts)
        {
            await _downloader.DownloadAsync(a, Path.Combine(folder, a.FileName), ct);
            step($"    {a.FileName}");
        }
    }

    /// <summary>
    /// Simple Voice Chat's default port is the proxy's public voice port (24454): without this, the FIRST start
    /// tries it, fails to bind (the proxy holds it) and logs an error. A properties file with just the port
    /// and bind address is completed by the add-on with its other defaults.
    /// </summary>
    private static void PreseedVoiceChat(ServerDefinition def)
    {
        var addons = def.AddonsPath!;
        if (!Directory.EnumerateFiles(addons, "*voicechat*.jar").Any()) return;
        var configDir = def.Loader == ServerLoader.Paper ? Path.Combine(addons, "voicechat") : Path.Combine(def.Path, "config", "voicechat");
        Directory.CreateDirectory(configDir);
        var props = Path.Combine(configDir, "voicechat-server.properties");
        ConfigFiles.SetProperty(props, "port", def.VoicePort.ToString());
        ConfigFiles.SetProperty(props, "bind_address", "127.0.0.1");
    }

    private static async Task<bool> RconUpAsync(ServerDefinition def, CancellationToken ct)
    {
        try
        {
            await using var rcon = new SpawnDev.Rcon.MinecraftRconClient(def.RconHost, def.RconPort);
            return await rcon.ConnectAsync(def.RconPassword, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what, CancellationToken ct)
    {
        var end = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < end)
        {
            if (await condition()) return;
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}
