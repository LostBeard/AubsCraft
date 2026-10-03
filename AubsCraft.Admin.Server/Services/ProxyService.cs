using AubsCraft.Admin.Server.Models;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Sets up and drives the Velocity proxy in front of the servers, and points each server at it.
///
/// Proxy (ProxyDefinition.Path): velocity.jar + the proxy plugins, every one downloaded with its published
/// checksum (AddonDownloader): Geyser + Floodgate (Bedrock), ViaVersion/ViaBackwards/ViaRewind (any Java
/// version), Simple Voice Chat (one public UDP port relayed to each server), Vivecraft Velocity Extensions
/// (forwards vivecraft:data), Velocircon (RCON for the panel).
///
/// Config is edited IN the files Velocity and the plugins generate on their first start (ConfigFiles), never
/// written from scratch: plugin updates add keys, and a hand-written file would silently drop them. So setup is
/// install -> first start (generates defaults, Floodgate's key.pem, forwarding.secret) -> ConfigureProxy -> start.
///
/// Servers behind it: 127.0.0.1 only, online-mode=false (the proxy authenticates), Velocity modern forwarding
/// with the proxy's secret (real UUIDs and skins reach the server), the proxy's Floodgate key, and an internal
/// voice port. Their own Geyser and Via jars are disabled: the proxy runs those now, and a second Geyser would
/// fight the proxy's for UDP 19132.
/// </summary>
public class ProxyService
{
    private readonly AddonDownloader _downloader;
    private readonly ILogger<ProxyService> _logger;

    public ProxyService(AddonDownloader downloader, ILogger<ProxyService> logger)
    {
        _downloader = downloader;
        _logger = logger;
    }

    /// <summary>Modrinth slugs of the proxy plugins that publish Velocity builds there.</summary>
    public static readonly string[] ModrinthProxyPlugins = ["viaversion", "viabackwards", "viarewind", "simple-voice-chat", "velocircon"];

    /// <summary>Server plugins the proxy replaces: disabled on every server behind it.</summary>
    public static readonly string[] ReplacedByProxy = ["Geyser-Spigot", "ViaVersion", "ViaBackwards", "ViaRewind"];

    /// <summary>
    /// Downloads velocity.jar and every proxy plugin into the proxy folder (verified). Files already matching
    /// their checksum are kept. Returns what was installed.
    /// </summary>
    public async Task<List<AddonArtifact>> InstallFilesAsync(ProxyDefinition proxy, CancellationToken ct = default)
    {
        var plugins = Path.Combine(proxy.Path, "plugins");
        Directory.CreateDirectory(plugins);
        var artifacts = new List<(AddonArtifact a, string path)>
        {
            (await _downloader.PaperMcAsync("velocity", proxy.VelocityVersion, ct), Path.Combine(proxy.Path, "velocity.jar")),
        };
        foreach (var project in new[] { "geyser", "floodgate" })
        {
            var a = await _downloader.GeyserMcAsync(project, "velocity", ct);
            artifacts.Add((a, Path.Combine(plugins, a.FileName)));
        }
        foreach (var slug in ModrinthProxyPlugins)
        {
            var a = await _downloader.ModrinthAsync(slug, "velocity", null, ct);
            artifacts.Add((a, Path.Combine(plugins, a.FileName)));
        }
        artifacts.Add((AddonDownloader.VivecraftVelocityExtensions, Path.Combine(plugins, AddonDownloader.VivecraftVelocityExtensions.FileName)));

        foreach (var (a, path) in artifacts)
        {
            if (AddonDownloader.IsCurrent(a, path)) continue;
            // An older build of the same plugin (different file name) would load twice: remove it first.
            RemoveOtherVersions(plugins, a);
            await _downloader.DownloadAsync(a, path, ct);
            _logger.LogInformation("Proxy: installed {File} ({Version})", a.FileName, a.Version);
        }
        return artifacts.Select(x => x.a).ToList();
    }

    private static void RemoveOtherVersions(string pluginsDir, AddonArtifact a)
    {
        // Plugin jars are named "<Name>-<version>.jar" (Modrinth) or a fixed name (GeyserMC): a different
        // version of the same project shares the part before the first digit-led segment.
        var stem = JarStem(a.FileName);
        foreach (var f in Directory.GetFiles(pluginsDir, "*.jar"))
            if (!Path.GetFileName(f).Equals(a.FileName, StringComparison.OrdinalIgnoreCase) && JarStem(Path.GetFileName(f)) == stem)
                File.Delete(f);
    }

    private static string JarStem(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var parts = name.Split('-', '_');
        return string.Join('-', parts.TakeWhile(p => p.Length == 0 || !char.IsDigit(p[0])));
    }

    /// <summary>The files a first start of the proxy generates; ConfigureProxy edits them.</summary>
    public static string[] GeneratedFiles(ProxyDefinition proxy) =>
    [
        Path.Combine(proxy.Path, "velocity.toml"),
        Path.Combine(proxy.Path, "forwarding.secret"),
        Path.Combine(proxy.Path, "plugins", "velocircon", "rcon.yml"),
        Path.Combine(proxy.Path, "plugins", "voicechat", "voicechat-proxy.properties"),
        Path.Combine(proxy.Path, "plugins", "floodgate", "config.yml"),
        Path.Combine(proxy.Path, "plugins", "floodgate", "key.pem"),
        Path.Combine(proxy.Path, "plugins", "Geyser-Velocity", "config.yml"),
    ];

    /// <summary>
    /// Writes the panel's settings into the proxy's generated config: bind + online mode + modern forwarding,
    /// the server list (registry order; the first server is where players land), forced hosts, Velocircon,
    /// the public voice port and Floodgate data forwarding. Run with the proxy stopped, or follow with ReloadAsync.
    /// </summary>
    public void ConfigureProxy(ProxyDefinition proxy, IReadOnlyList<ServerDefinition> servers)
    {
        var missing = GeneratedFiles(proxy).Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("Start the proxy once first (it generates its config): missing " + string.Join(", ", missing));
        if (servers.Count == 0)
            throw new InvalidOperationException("The proxy needs at least one server.");

        var toml = Path.Combine(proxy.Path, "velocity.toml");
        ConfigFiles.SetTomlKey(toml, null, "bind", ConfigFiles.TomlString($"{proxy.BindHost}:{proxy.Port}"));
        ConfigFiles.SetTomlKey(toml, null, "motd", ConfigFiles.TomlString(proxy.Motd));
        ConfigFiles.SetTomlKey(toml, null, "online-mode", proxy.OnlineMode ? "true" : "false");
        ConfigFiles.SetTomlKey(toml, null, "player-info-forwarding-mode", ConfigFiles.TomlString("MODERN"));
        ConfigFiles.ReplaceTomlSection(toml, "servers", ServersSection(servers));
        ConfigFiles.ReplaceTomlSection(toml, "forced-hosts", ForcedHostsSection(proxy, servers));

        var rcon = Path.Combine(proxy.Path, "plugins", "velocircon", "rcon.yml");
        ConfigFiles.SetYamlScalar(rcon, ["enable"], "true");
        ConfigFiles.SetYamlScalar(rcon, ["host"], ConfigFiles.YamlString("127.0.0.1"));
        ConfigFiles.SetYamlScalar(rcon, ["port"], proxy.RconPort.ToString());
        ConfigFiles.SetYamlScalar(rcon, ["password"], ConfigFiles.YamlString(proxy.RconPassword));

        ConfigFiles.SetProperty(Path.Combine(proxy.Path, "plugins", "voicechat", "voicechat-proxy.properties"), "port", proxy.VoicePort.ToString());
        ConfigFiles.SetYamlScalar(Path.Combine(proxy.Path, "plugins", "floodgate", "config.yml"), ["send-floodgate-data"], "true");
        ConfigFiles.SetYamlScalar(Path.Combine(proxy.Path, "plugins", "Geyser-Velocity", "config.yml"), ["bedrock", "port"], proxy.BedrockPort.ToString());
    }

    /// <summary>[servers]: one entry per server at 127.0.0.1:GamePort, and try = the primary (first) server.</summary>
    public static IEnumerable<string> ServersSection(IReadOnlyList<ServerDefinition> servers)
    {
        foreach (var s in servers)
            yield return $"{s.Id} = {ConfigFiles.TomlString($"127.0.0.1:{s.GamePort}")}";
        yield return $"try = [{ConfigFiles.TomlString(servers[0].Id)}]";
    }

    private static IEnumerable<string> ForcedHostsSection(ProxyDefinition proxy, IReadOnlyList<ServerDefinition> servers)
    {
        var known = servers.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, ids) in proxy.ForcedHosts)
        {
            var valid = ids.Where(known.Contains).ToList();
            if (valid.Count > 0)
                yield return $"{ConfigFiles.TomlString(host)} = [{string.Join(", ", valid.Select(ConfigFiles.TomlString))}]";
        }
    }

    /// <summary>
    /// Points one server at the proxy. Run with that server STOPPED (the server rewrites these files on start).
    /// </summary>
    public void ConfigureBackend(ProxyDefinition proxy, ServerDefinition server)
    {
        var secret = File.ReadAllText(Path.Combine(proxy.Path, "forwarding.secret")).Trim();
        var props = Path.Combine(server.Path, "server.properties");
        ConfigFiles.SetProperty(props, "online-mode", "false");
        ConfigFiles.SetProperty(props, "server-ip", "127.0.0.1");
        ConfigFiles.SetProperty(props, "server-port", server.GamePort.ToString());
        // RCON binds to server-ip too: after this it is reachable from this machine only.

        // Modern forwarding: the server trusts player info signed with the proxy's secret. Each loader has its own
        // switch (the files exist after the server's first start - see ServerProvisioningService).
        switch (server.Loader)
        {
            case ServerLoader.Paper:
                var paperGlobal = Path.Combine(server.Path, "config", "paper-global.yml");
                ConfigFiles.SetYamlScalar(paperGlobal, ["proxies", "velocity", "enabled"], "true");
                ConfigFiles.SetYamlScalar(paperGlobal, ["proxies", "velocity", "online-mode"], proxy.OnlineMode ? "true" : "false");
                ConfigFiles.SetYamlScalar(paperGlobal, ["proxies", "velocity", "secret"], ConfigFiles.YamlString(secret));
                break;
            case ServerLoader.Fabric:
            case ServerLoader.Vanilla: // created as Fabric
                // FabricProxy-Lite (config/FabricProxy-Lite.toml, top-level keys).
                ConfigFiles.SetTomlKey(Path.Combine(server.Path, "config", "FabricProxy-Lite.toml"), null, "secret", ConfigFiles.TomlString(secret));
                break;
            case ServerLoader.Forge:
            case ServerLoader.NeoForge:
                // Proxy-Compatible-Forge (config/proxy-compatible-forge.toml, [forwarding]).
                var pcf = Path.Combine(server.Path, "config", "proxy-compatible-forge.toml");
                ConfigFiles.SetTomlKey(pcf, "forwarding", "enabled", "true");
                ConfigFiles.SetTomlKey(pcf, "forwarding", "mode", ConfigFiles.TomlString("MODERN"));
                ConfigFiles.SetTomlKey(pcf, "forwarding", "secret", ConfigFiles.TomlString(secret));
                break;
        }

        // Plugins keep their files in plugins/<name>/, mods in config/<name>/.
        var addonConfig = server.Loader == ServerLoader.Paper ? Path.Combine(server.Path, "plugins") : Path.Combine(server.Path, "config");

        // Bedrock players reach this server through the proxy's Floodgate: the server's Floodgate must trust its key.
        var backendFloodgate = Path.Combine(addonConfig, "floodgate");
        if (Directory.Exists(backendFloodgate))
            File.Copy(Path.Combine(proxy.Path, "plugins", "floodgate", "key.pem"), Path.Combine(backendFloodgate, "key.pem"), overwrite: true);

        // Voice: an internal port per server (the proxy relays the public one), bound to this machine only.
        var voice = Path.Combine(addonConfig, "voicechat", "voicechat-server.properties");
        if (File.Exists(voice))
        {
            if (server.VoicePort == 0)
                throw new InvalidOperationException($"Server '{server.Id}' runs Simple Voice Chat but has no VoicePort.");
            ConfigFiles.SetProperty(voice, "port", server.VoicePort.ToString());
            ConfigFiles.SetProperty(voice, "bind_address", "127.0.0.1");
        }

        foreach (var name in ReplacedByProxy)
            DisablePlugin(server, name);
    }

    /// <summary>Renames plugins/{name}.jar (and versioned {name}-x.y.jar) to .jar.disabled.</summary>
    private void DisablePlugin(ServerDefinition server, string name)
    {
        var plugins = Path.Combine(server.Path, "plugins");
        if (!Directory.Exists(plugins)) return;
        foreach (var jar in Directory.GetFiles(plugins, "*.jar"))
        {
            var file = Path.GetFileNameWithoutExtension(jar);
            if (!file.Equals(name, StringComparison.OrdinalIgnoreCase) && !file.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Move(jar, jar + ".disabled", overwrite: true);
            _logger.LogInformation("Server '{Server}': disabled {Plugin} (the proxy runs it)", server.Id, Path.GetFileName(jar));
        }
    }

    /// <summary>Runs a proxy console command through Velocircon (e.g. "velocity reload", "glist").</summary>
    public async Task<string> CommandAsync(ProxyDefinition proxy, string command, CancellationToken ct = default)
    {
        await using var rcon = new MinecraftRconClient("127.0.0.1", proxy.RconPort);
        if (!await rcon.ConnectAsync(proxy.RconPassword, ct))
            throw new InvalidOperationException("Proxy RCON (Velocircon) refused the password.");
        return MinecraftText.StripColorCodes(await rcon.SendCommandAsync(command, ct));
    }

    /// <summary>Re-reads velocity.toml (server list, forced hosts) without restarting the proxy.</summary>
    public Task<string> ReloadAsync(ProxyDefinition proxy, CancellationToken ct = default) =>
        CommandAsync(proxy, "velocity reload", ct);
}
