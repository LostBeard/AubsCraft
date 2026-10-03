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
    /// <summary>
    /// Internal Simple Voice Chat UDP port (behind the proxy every server needs its own; the proxy relays the
    /// public voice port to it). 0 = not set.
    /// </summary>
    public int VoicePort { get; set; }
    /// <summary>JVM max heap (-Xmx) in MB. Used to warn before starting more servers than the machine has RAM for.</summary>
    public int MemoryMb { get; set; } = 3072;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// Modrinth projects the PLAYERS' clients need for this server (mods that add blocks, creatures, sounds).
    /// The /quest installer pushes them (with their dependencies) to headsets.
    /// </summary>
    public List<string> ClientMods { get; set; } = [];

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
    /// <summary>The Velocity proxy in front of the servers; null until it is set up.</summary>
    public ProxyDefinition? Proxy { get; set; }
}

/// <summary>
/// The Velocity proxy players connect to. It owns the public ports (Java, Bedrock via Geyser, voice chat);
/// every server behind it listens on 127.0.0.1 only and trusts the proxy through modern forwarding.
/// </summary>
public class ProxyDefinition
{
    /// <summary>The proxy's folder (velocity.jar, velocity.toml, plugins/).</summary>
    public string Path { get; set; } = "/opt/minecraft/velocity";
    public string ServiceName { get; set; } = "velocity";
    /// <summary>Velocity release line. 4.x: current Geyser needs its newer Adventure API (3.5.1 throws NoSuchMethodError); needs Java 25.</summary>
    public string VelocityVersion { get; set; } = "4.2.0";
    public string BindHost { get; set; } = "0.0.0.0";
    /// <summary>Public Java port (players and the SRV record point here).</summary>
    public int Port { get; set; } = 25565;
    /// <summary>Public Bedrock (Geyser) UDP port.</summary>
    public int BedrockPort { get; set; } = 19132;
    /// <summary>Public Simple Voice Chat UDP port (the proxy relays to each server's internal voice port).</summary>
    public int VoicePort { get; set; } = 24454;
    /// <summary>Velocircon RCON, bound to 127.0.0.1 - how the panel runs proxy commands (velocity reload, glist).</summary>
    public int RconPort { get; set; } = 25576;
    public string RconPassword { get; set; } = "";
    /// <summary>Mojang authentication at the proxy. Only tests turn it off.</summary>
    public bool OnlineMode { get; set; } = true;
    /// <summary>Server-list message (MiniMessage).</summary>
    public string Motd { get; set; } = "<aqua>AubsCraft</aqua>";
    /// <summary>Hostname -> server ids (e.g. "creative.spawndev.com" -> ["creative"]): that hostname joins that server first.</summary>
    public Dictionary<string, List<string>> ForcedHosts { get; set; } = [];
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
