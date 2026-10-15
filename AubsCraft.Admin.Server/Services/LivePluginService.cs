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
    public const string JarFileName = "aubscraft-live.jar";
    private readonly LiveFeedService _live;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LivePluginService> _logger;

    public LivePluginService(LiveFeedService live, IConfiguration configuration, ILogger<LivePluginService> logger)
    {
        _live = live;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>The plugin jar shipped with the panel.</summary>
    public static string ShippedJar => Path.Combine(AppContext.BaseDirectory, "ServerPlugins", JarFileName);

    /// <summary>Where the plugin reaches the panel: the same machine (Live:PluginPanelUrl, default http://127.0.0.1:5080).</summary>
    public string PanelUrl => _configuration.GetValue<string>("Live:PluginPanelUrl") ?? "http://127.0.0.1:5080";

    private static string? PluginsFolder(ServerDefinition server) =>
        server.Loader == ServerLoader.Paper ? server.AddonsPath : null;

    /// <summary>The plugin's config for a server: the panel writes it, nobody edits the secret by hand.</summary>
    public string ConfigFor(ServerDefinition server, int intervalTicks = 4) =>
        new StringBuilder()
            .AppendLine("# Written by the AubsCraft panel: do not edit the secret by hand (the panel rewrites this file when it installs the plugin).")
            .AppendLine($"panel-url: \"{PanelUrl}\"")
            .AppendLine($"server-id: \"{server.Id}\"")
            .AppendLine($"secret: \"{_live.Secret}\"")
            .AppendLine($"interval-ticks: {intervalTicks}")
            .AppendLine("max-entities: 3000")
            .AppendLine("include-items: false")
            .ToString();

    public LivePluginState Inspect(ServerDefinition server)
    {
        var folder = PluginsFolder(server);
        if (folder == null)
            return new LivePluginState(false, false, false, false, false, "This server has no Paper plugins folder (the live view needs a Paper server).");

        var jar = Path.Combine(folder, JarFileName);
        bool installed = File.Exists(jar);
        bool upToDate = installed && File.Exists(ShippedJar) && File.ReadAllBytes(jar).AsSpan().SequenceEqual(File.ReadAllBytes(ShippedJar));
        var configPath = Path.Combine(folder, "AubsCraftLive", "config.yml");
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
        if (folder == null) return new LivePluginInstallResult(false, false, "This server has no Paper plugins folder.");
        if (!File.Exists(ShippedJar)) return new LivePluginInstallResult(false, false, "The panel was built without the live plugin jar.");
        try
        {
            bool changed = false;
            var bytes = File.ReadAllBytes(ShippedJar);
            var jar = Path.Combine(folder, JarFileName);
            if (!File.Exists(jar) || !File.ReadAllBytes(jar).AsSpan().SequenceEqual(bytes))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(jar, bytes);
                changed = true;
            }
            var configDir = Path.Combine(folder, "AubsCraftLive");
            var configPath = Path.Combine(configDir, "config.yml");
            var config = ConfigFor(server);
            if (!File.Exists(configPath) || File.ReadAllText(configPath) != config)
            {
                Directory.CreateDirectory(configDir);
                File.WriteAllText(configPath, config);
                changed = true;
            }
            if (changed) _logger.LogInformation("Live plugin: installed into {Folder} for {Server} (takes effect when the server restarts)", folder, server.Id);
            return new LivePluginInstallResult(true, changed, changed ? "Installed. Restart the server to start the live feed." : "Already installed and up to date.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Live plugin: could not install into {Folder}", folder);
            return new LivePluginInstallResult(false, false, "Could not write to the server's plugins folder: " + ex.Message);
        }
    }

    /// <summary>Remove the plugin and its config.</summary>
    public LivePluginInstallResult Uninstall(ServerDefinition server)
    {
        var folder = PluginsFolder(server);
        if (folder == null) return new LivePluginInstallResult(false, false, "This server has no Paper plugins folder.");
        try
        {
            bool changed = false;
            var jar = Path.Combine(folder, JarFileName);
            if (File.Exists(jar)) { File.Delete(jar); changed = true; }
            var configDir = Path.Combine(folder, "AubsCraftLive");
            if (Directory.Exists(configDir)) { Directory.Delete(configDir, recursive: true); changed = true; }
            return new LivePluginInstallResult(true, changed, changed ? "Removed. Restart the server to stop the live feed." : "It was not installed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LivePluginInstallResult(false, false, "Could not remove it: " + ex.Message);
        }
    }
}
