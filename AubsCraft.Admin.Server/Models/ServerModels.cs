using System.Text.Json.Serialization;

namespace AubsCraft.Admin.Server.Models;

/// <summary>The server software a Minecraft server runs. Decides where add-ons live and which Modrinth loaders match.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerLoader>))]
public enum ServerLoader
{
    Paper,
    Fabric,
    Forge,
    NeoForge,
    Vanilla,
}

/// <summary>
/// One Minecraft server managed by this admin panel. Persisted in servers.json (see ServerRegistry).
/// The first server in the registry is the primary one: legacy endpoints without a server id use it,
/// and it is the proxy's default server.
/// </summary>
public class ServerDefinition
{
    /// <summary>Lowercase slug ([a-z0-9-]). Used in URLs, the systemd instance name and the proxy's server name.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ServerLoader Loader { get; set; } = ServerLoader.Paper;
    public string GameVersion { get; set; } = "1.21.5";
    /// <summary>The server's root folder (holds server.properties, the world, plugins/ or mods/).</summary>
    public string Path { get; set; } = "";
    /// <summary>The systemd unit that runs it (e.g. "minecraft" or "minecraft@creative").</summary>
    public string ServiceName { get; set; } = "";
    public string RconHost { get; set; } = "127.0.0.1";
    public int RconPort { get; set; } = 25575;
    public string RconPassword { get; set; } = "";
    /// <summary>The Minecraft (Java) port the server listens on.</summary>
    public int GamePort { get; set; } = 25565;
    /// <summary>JVM max heap (-Xmx) in MB. Used to warn before starting more servers than the machine has RAM for.</summary>
    public int MemoryMb { get; set; } = 3072;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string LogPath => System.IO.Path.Combine(Path, "logs", "latest.log");

    /// <summary>Where add-ons live: plugins/ for Paper, mods/ for the mod loaders, none for Vanilla.</summary>
    [JsonIgnore] public string? AddonsPath => Loader switch
    {
        ServerLoader.Paper => System.IO.Path.Combine(Path, "plugins"),
        ServerLoader.Fabric or ServerLoader.Forge or ServerLoader.NeoForge => System.IO.Path.Combine(Path, "mods"),
        _ => null,
    };

    /// <summary>
    /// The overworld folder, from server.properties level-name (read each call so a reset or rename on disk
    /// is picked up). Falls back to "world".
    /// </summary>
    [JsonIgnore] public string WorldPath => System.IO.Path.Combine(Path, ReadLevelName() ?? "world");

    private string? ReadLevelName()
    {
        try
        {
            var props = System.IO.Path.Combine(Path, "server.properties");
            if (!File.Exists(props)) return null;
            foreach (var line in File.ReadLines(props))
            {
                if (!line.StartsWith("level-name=", StringComparison.Ordinal)) continue;
                var name = line["level-name=".Length..].Trim();
                return name.Length > 0 ? name : null;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}

/// <summary>The servers.json document.</summary>
public class ServerRegistryFile
{
    public List<ServerDefinition> Servers { get; set; } = [];
}

/// <summary>Client-facing summary of one server (no secrets).</summary>
public record ServerSummaryDto(
    string Id,
    string Name,
    string Loader,
    string GameVersion,
    int GamePort,
    int MemoryMb,
    bool IsPrimary,
    bool Connected,
    int Online,
    int Max);
