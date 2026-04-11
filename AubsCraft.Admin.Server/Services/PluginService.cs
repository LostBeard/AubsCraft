using System.IO.Compression;
using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Manages Minecraft server plugins via direct filesystem access.
/// Reads plugin.yml from jar files for metadata.
/// Enable/disable by renaming .jar to .jar.disabled.
/// </summary>
public partial class PluginService
{
    private readonly string _pluginsPath;
    private readonly ILogger<PluginService> _logger;

    public string PluginsPath => _pluginsPath;

    public PluginService(IConfiguration configuration, ILogger<PluginService> logger)
    {
        _logger = logger;
        _pluginsPath = configuration.GetValue<string>("Minecraft:PluginsPath") ?? "/opt/minecraft/server/plugins";
    }

    public List<PluginInfo> GetPlugins()
    {
        var plugins = new List<PluginInfo>();

        if (!Directory.Exists(_pluginsPath))
        {
            _logger.LogWarning("Plugins directory not found: {Path}", _pluginsPath);
            return plugins;
        }

        // Find .jar and .jar.disabled files
        var jarFiles = Directory.GetFiles(_pluginsPath, "*.jar")
            .Concat(Directory.GetFiles(_pluginsPath, "*.jar.disabled"))
            .OrderBy(f => Path.GetFileName(f));

        foreach (var jarPath in jarFiles)
        {
            try
            {
                var info = ReadPluginInfo(jarPath);
                if (info != null)
                    plugins.Add(info);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read plugin: {Path}", jarPath);
                plugins.Add(new PluginInfo
                {
                    FileName = Path.GetFileName(jarPath),
                    Name = Path.GetFileNameWithoutExtension(jarPath).Replace(".jar", ""),
                    Enabled = !jarPath.EndsWith(".disabled"),
                    Error = ex.Message,
                });
            }
        }

        return plugins;
    }

    public (bool success, string message) TogglePlugin(string fileName)
    {
        var jarPath = Path.Combine(_pluginsPath, fileName);

        if (fileName.EndsWith(".disabled"))
        {
            // Enable: rename .jar.disabled -> .jar
            var enabledPath = jarPath[..^".disabled".Length];
            if (!File.Exists(jarPath))
                return (false, $"File not found: {fileName}");

            File.Move(jarPath, enabledPath);
            _logger.LogInformation("Enabled plugin: {FileName}", fileName);
            return (true, $"Enabled {Path.GetFileNameWithoutExtension(enabledPath)}. Restart the server to apply.");
        }
        else
        {
            // Disable: rename .jar -> .jar.disabled
            var disabledPath = jarPath + ".disabled";
            if (!File.Exists(jarPath))
                return (false, $"File not found: {fileName}");

            File.Move(jarPath, disabledPath);
            _logger.LogInformation("Disabled plugin: {FileName}", fileName);
            return (true, $"Disabled {Path.GetFileNameWithoutExtension(jarPath)}. Restart the server to apply.");
        }
    }

    private PluginInfo? ReadPluginInfo(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);
        var enabled = !jarPath.EndsWith(".disabled");
        var fileSize = new FileInfo(jarPath).Length;

        using var zip = ZipFile.OpenRead(jarPath);

        // Try plugin.yml first (Bukkit/Spigot), then paper-plugin.yml (Paper)
        var ymlEntry = zip.GetEntry("plugin.yml") ?? zip.GetEntry("paper-plugin.yml");
        if (ymlEntry == null)
            return new PluginInfo { FileName = fileName, Name = CleanName(fileName), Enabled = enabled, FileSize = fileSize };

        using var stream = ymlEntry.Open();
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();

        // Simple YAML parsing for the fields we care about (no YAML library dependency)
        return new PluginInfo
        {
            FileName = fileName,
            Name = ExtractYamlValue(yaml, "name") ?? CleanName(fileName),
            Version = ExtractYamlValue(yaml, "version") ?? "",
            Description = ExtractYamlValue(yaml, "description") ?? "",
            Authors = ExtractYamlList(yaml, "authors") ?? ExtractYamlValue(yaml, "author") ?? "",
            Website = ExtractYamlValue(yaml, "website") ?? "",
            Enabled = enabled,
            FileSize = fileSize,
        };
    }

    private static string CleanName(string fileName) =>
        fileName.Replace(".jar.disabled", "").Replace(".jar", "");

    // Simple top-level YAML value extraction (no nested support needed)
    [GeneratedRegex(@"^{key}:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex YamlPattern();

    private static string? ExtractYamlValue(string yaml, string key)
    {
        var pattern = new Regex($@"^{Regex.Escape(key)}:\s*(.+)$", RegexOptions.Multiline);
        var match = pattern.Match(yaml);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    private static string? ExtractYamlList(string yaml, string key)
    {
        var pattern = new Regex($@"^{Regex.Escape(key)}:\s*\[(.+?)\]", RegexOptions.Multiline);
        var match = pattern.Match(yaml);
        if (!match.Success) return null;

        var items = match.Groups[1].Value
            .Split(',')
            .Select(s => s.Trim().Trim('"', '\''))
            .Where(s => !string.IsNullOrEmpty(s));
        return string.Join(", ", items);
    }
}
