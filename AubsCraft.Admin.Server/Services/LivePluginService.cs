using System.Text;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>Where one server stands with the AubsCraft Live plugin.</summary>
public sealed record LivePluginState(bool Supported, bool JarInstalled, bool JarUpToDate, bool ConfigWritten, bool Live, string Message);

/// <summary>What an install changed (a server loads its plugins when it starts, so a change needs a restart to take effect).</summary>
public sealed record LivePluginInstallResult(bool Success, bool Changed, string Message);

/// <summary>
/// Puts the AubsCraft Live plugin (shipped with the panel: source in LivePlugin/) into a server's plugins folder and writes its config: where the
/// panel is, which server this is, the shared secret. The plugin does nothing without that secret, so the jar alone changes nothing.
/// </summary>
public sealed class LivePluginService
{
    /// <summary>The Paper plugin and the Fabric mod: the same frames, two builds (each is shipped with the panel, built from LivePlugin/ and LiveFabric/).</summary>
    public const string JarFileName = "aubscraft-live.jar";
    public const string FabricJarFileName = "aubscraft-live-fabric.jar";
    private readonly LiveFeedService _live;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LivePluginService> _logger;

    public LivePluginService(LiveFeedService live, IConfiguration configuration, ILogger<LivePluginService> logger)
    {
        _live = live;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>The Paper plugin jar shipped with the panel.</summary>
    public static string ShippedJar => Path.Combine(AppContext.BaseDirectory, "ServerPlugins", JarFileName);
    /// <summary>The Fabric mod jar shipped with the panel.</summary>
    public static string ShippedFabricJar => Path.Combine(AppContext.BaseDirectory, "ServerPlugins", FabricJarFileName);

    private static bool Supports(ServerDefinition server) => server.Loader is ServerLoader.Paper or ServerLoader.Fabric;
    private static bool IsFabric(ServerDefinition server) => server.Loader == ServerLoader.Fabric;
    private static string ShippedFor(ServerDefinition server) => IsFabric(server) ? ShippedFabricJar : ShippedJar;
    private static string InstalledJarName(ServerDefinition server) => IsFabric(server) ? FabricJarFileName : JarFileName;

    /// <summary>Where the config goes: a Paper plugin has its own folder, a Fabric mod a file in the server's config folder.</summary>
    private static string ConfigPath(ServerDefinition server, string folder) =>
        IsFabric(server) ? Path.Combine(server.Path, "config", "aubscraft-live.properties") : Path.Combine(folder, "AubsCraftLive", "config.yml");

    /// <summary>Where the plugin reaches the panel: the same machine (Live:PluginPanelUrl, default http://127.0.0.1:5080).</summary>
    public string PanelUrl => _configuration.GetValue<string>("Live:PluginPanelUrl") ?? "http://127.0.0.1:5080";

    private static string? PluginsFolder(ServerDefinition server) => Supports(server) ? server.AddonsPath : null;

    /// <summary>The plugin's (or mod's) config for a server: the panel writes it, nobody edits the secret by hand.</summary>
    public string ConfigFor(ServerDefinition server, int intervalTicks = 4)
    {
        if (IsFabric(server))
            return new StringBuilder()
                .AppendLine("# Written by the AubsCraft panel: do not edit the secret by hand (the panel rewrites this file when it installs the mod).")
                .AppendLine($"panel-url={PanelUrl}")
                .AppendLine($"server-id={server.Id}")
                .AppendLine($"secret={_live.Secret}")
                .AppendLine($"interval-ticks={intervalTicks}")
                .AppendLine("max-entities=3000")
                .AppendLine("include-items=true")
                .AppendLine("max-items=500")
                .ToString();
        return new StringBuilder()
            .AppendLine("# Written by the AubsCraft panel: do not edit the secret by hand (the panel rewrites this file when it installs the plugin).")
            .AppendLine($"panel-url: \"{PanelUrl}\"")
            .AppendLine($"server-id: \"{server.Id}\"")
            .AppendLine($"secret: \"{_live.Secret}\"")
            .AppendLine($"interval-ticks: {intervalTicks}")
            .AppendLine("max-entities: 3000")
            .AppendLine("include-items: true")
            .AppendLine("max-items: 500")
            .ToString();
    }

    public LivePluginState Inspect(ServerDefinition server)
    {
        var folder = PluginsFolder(server);
        if (folder == null)
            return new LivePluginState(false, false, false, false, false, "The live view needs a Paper or Fabric server.");

        var jar = Path.Combine(folder, InstalledJarName(server));
        var shipped = ShippedFor(server);
        bool installed = File.Exists(jar);
        bool upToDate = installed && File.Exists(shipped) && File.ReadAllBytes(jar).AsSpan().SequenceEqual(File.ReadAllBytes(shipped));
        var configPath = ConfigPath(server, folder);
        bool configOk = File.Exists(configPath) && File.ReadAllText(configPath) == ConfigFor(server);
        bool live = _live.StatusOf(server.Id).Live;
        string message = live ? "Live: frames are arriving."
            : !installed ? "Not installed."
            : !upToDate ? "Installed, but an older version: update it, then restart the server."
            : !configOk ? "Installed, its config is out of date: install again, then restart the server."
            : "Installed. Restart the server to start the live feed (or it is waiting for the first frames).";
        return new LivePluginState(true, installed, upToDate, configOk, live, message);
    }

    /// <summary>Install or update the plugin and its config. Returns whether anything changed: then the server needs a restart.</summary>
    public LivePluginInstallResult Install(ServerDefinition server)
    {
        var folder = PluginsFolder(server);
        if (folder == null) return new LivePluginInstallResult(false, false, "The live view needs a Paper or Fabric server.");
        var shipped = ShippedFor(server);
        if (!File.Exists(shipped)) return new LivePluginInstallResult(false, false, "The panel was built without the live view jar for this kind of server.");
        try
        {
            bool changed = false;
            var bytes = File.ReadAllBytes(shipped);
            var jar = Path.Combine(folder, InstalledJarName(server));
            if (!File.Exists(jar) || !File.ReadAllBytes(jar).AsSpan().SequenceEqual(bytes))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(jar, bytes);
                changed = true;
            }
            var configPath = ConfigPath(server, folder);
            var config = ConfigFor(server);
            if (!File.Exists(configPath) || File.ReadAllText(configPath) != config)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, config);
                changed = true;
            }
            if (changed) _logger.LogInformation("Live view: installed into {Folder} for {Server} (takes effect when the server restarts)", folder, server.Id);
            return new LivePluginInstallResult(true, changed, changed ? "Installed. Restart the server to start the live feed." : "Already installed and up to date.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Live plugin: could not install into {Folder}", folder);
            return new LivePluginInstallResult(false, false, "Could not write to the server's plugins/mods folder: " + ex.Message);
        }
    }

    /// <summary>Remove the plugin and its config.</summary>
    public LivePluginInstallResult Uninstall(ServerDefinition server)
    {
        var folder = PluginsFolder(server);
        if (folder == null) return new LivePluginInstallResult(false, false, "The live view needs a Paper or Fabric server.");
        try
        {
            bool changed = false;
            var jar = Path.Combine(folder, InstalledJarName(server));
            if (File.Exists(jar)) { File.Delete(jar); changed = true; }
            if (IsFabric(server))
            {
                var file = ConfigPath(server, folder);
                if (File.Exists(file)) { File.Delete(file); changed = true; }
            }
            else
            {
                var configDir = Path.Combine(folder, "AubsCraftLive");
                if (Directory.Exists(configDir)) { Directory.Delete(configDir, recursive: true); changed = true; }
            }
            return new LivePluginInstallResult(true, changed, changed ? "Removed. Restart the server to stop the live feed." : "It was not installed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LivePluginInstallResult(false, false, "Could not remove it: " + ex.Message);
        }
    }
}
