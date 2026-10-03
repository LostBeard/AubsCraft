using System.Text.Json;
using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// The list of Minecraft servers this panel manages, persisted to servers.json (Servers:RegistryPath).
/// On first run there is no servers.json: the registry is seeded with ONE server built from the legacy
/// single-server config (Minecraft:* and Rcon:*), so an existing install keeps working unchanged.
/// </summary>
public sealed partial class ServerRegistry
{
    public const string LegacyServerId = "aubscraft";

    private readonly string _path;
    private readonly ILogger<ServerRegistry> _logger;
    private readonly Lock _lock = new();
    private List<ServerDefinition> _servers;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ServerRegistry(IConfiguration configuration, ILogger<ServerRegistry> logger)
    {
        _logger = logger;
        _path = configuration.GetValue<string>("Servers:RegistryPath") ?? "servers.json";
        _servers = Load(configuration);
    }

    /// <summary>Raised after the server list changes (add/remove/update).</summary>
    public event Action? Changed;

    public IReadOnlyList<ServerDefinition> All
    {
        get { lock (_lock) return _servers.ToList(); }
    }

    public ServerDefinition? Get(string id)
    {
        lock (_lock) return _servers.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The primary server (first in the list). Null only when every server has been removed.</summary>
    public ServerDefinition? Primary
    {
        get { lock (_lock) return _servers.FirstOrDefault(); }
    }

    public void Add(ServerDefinition def)
    {
        ValidateId(def.Id);
        lock (_lock)
        {
            if (_servers.Any(s => s.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A server with id '{def.Id}' already exists.");
            _servers.Add(def);
            Save();
        }
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _servers.RemoveAll(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed) return false;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public void Update(ServerDefinition def)
    {
        lock (_lock)
        {
            var i = _servers.FindIndex(s => s.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new InvalidOperationException($"No server with id '{def.Id}'.");
            _servers[i] = def;
            Save();
        }
        Changed?.Invoke();
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    /// <summary>Ids go into systemd unit names, folder names and the proxy config: lowercase letters, digits and inner hyphens only.</summary>
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public static void ValidateId(string id)
    {
        if (!IsValidId(id))
            throw new ArgumentException($"Invalid server id '{id}': use 1-32 lowercase letters, digits or hyphens (no leading/trailing hyphen).");
    }

    private List<ServerDefinition> Load(IConfiguration configuration)
    {
        if (File.Exists(_path))
        {
            var file = JsonSerializer.Deserialize<ServerRegistryFile>(File.ReadAllText(_path))
                ?? throw new InvalidDataException($"{_path} is empty or not a server registry.");
            foreach (var s in file.Servers) ValidateId(s.Id);
            _logger.LogInformation("Loaded {Count} server(s) from {Path}", file.Servers.Count, _path);
            return file.Servers;
        }

        var legacy = FromLegacyConfig(configuration);
        _servers = [legacy];
        Save();
        _logger.LogInformation("Created {Path} from the single-server config: '{Id}' at {ServerPath}", _path, legacy.Id, legacy.Path);
        return _servers;
    }

    /// <summary>Builds the server definition the single-server config (Minecraft:*, Rcon:*) describes.</summary>
    public static ServerDefinition FromLegacyConfig(IConfiguration configuration)
    {
        var serviceName = configuration.GetValue<string>("Minecraft:ServiceName") ?? "minecraft";
        return new ServerDefinition
        {
            Id = LegacyServerId,
            Name = "AubsCraft",
            Loader = ServerLoader.Paper,
            GameVersion = configuration.GetValue<string>("Minecraft:GameVersion") ?? "1.21.5",
            Path = configuration.GetValue<string>("Minecraft:ServerPath") ?? "/opt/minecraft/server",
            ServiceName = serviceName,
            RconHost = configuration.GetValue<string>("Rcon:Host") ?? "127.0.0.1",
            RconPort = configuration.GetValue("Rcon:Port", 25575),
            RconPassword = configuration.GetValue<string>("Rcon:Password") ?? "",
            GamePort = 25565,
            MemoryMb = ReadUnitMaxHeapMb(serviceName) ?? 3072,
            CreatedAt = DateTime.UtcNow,
        };
    }

    [GeneratedRegex(@"-Xmx(\d+)([gGmM])")]
    private static partial Regex XmxPattern();

    /// <summary>The -Xmx of a systemd unit's ExecStart, in MB, or null when the unit file can't be read.</summary>
    public static int? ReadUnitMaxHeapMb(string serviceName)
    {
        try
        {
            var unit = $"/etc/systemd/system/{serviceName}.service";
            if (!File.Exists(unit)) return null;
            var m = XmxPattern().Match(File.ReadAllText(unit));
            if (!m.Success) return null;
            var n = int.Parse(m.Groups[1].Value);
            return m.Groups[2].Value is "g" or "G" ? n * 1024 : n;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void Save()
    {
        // Write-then-rename so a crash mid-write never leaves a truncated registry (it holds RCON passwords
        // and would otherwise be re-seeded from the legacy config, losing every added server).
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new ServerRegistryFile { Servers = _servers }, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }
}
