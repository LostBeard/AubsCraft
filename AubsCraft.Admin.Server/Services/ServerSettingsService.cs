using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// A server's everyday settings after it was created: its name and memory (registry + aubscraft.env) and the common
/// server.properties. Difficulty and the default game mode also apply at once over RCON; the rest on the next restart
/// (the result says which). Memory is editable only for servers the panel runs through minecraft@.service
/// (aubscraft.env) - the original server's heap lives in a systemd unit the panel cannot write.
/// </summary>
public partial class ServerSettingsService
{
    public static readonly string[] Difficulties = ["peaceful", "easy", "normal", "hard"];
    public static readonly string[] GameModes = ["survival", "creative", "adventure", "spectator"];

    private readonly ServerRegistry _registry;
    private readonly ProxyService _proxy;
    private readonly ILogger<ServerSettingsService> _logger;

    public ServerSettingsService(ServerRegistry registry, ProxyService proxy, ILogger<ServerSettingsService> logger)
    {
        _registry = registry;
        _proxy = proxy;
        _logger = logger;
    }

    private ServerDefinition Get(string id) => _registry.Get(id) ?? throw new InvalidOperationException($"No server '{id}'.");
    private static string Props(ServerDefinition def) => Path.Combine(def.Path, "server.properties");
    private static string EnvFile(ServerDefinition def) => Path.Combine(def.Path, "aubscraft.env");

    public ServerSettingsDto Read(string serverId)
    {
        var def = Get(serverId);
        var p = Props(def);
        string Prop(string key, string fallback) => ConfigFiles.GetProperty(p, key) ?? fallback;
        int Int(string key, int fallback) => int.TryParse(ConfigFiles.GetProperty(p, key), out var v) ? v : fallback;
        return new ServerSettingsDto(def.Name, def.MemoryMb, File.Exists(EnvFile(def)),
            Prop("difficulty", "easy"), Prop("gamemode", "survival"), Prop("pvp", "true") == "true", Prop("allow-flight", "false") == "true",
            Int("max-players", 20), Int("view-distance", 10), Int("simulation-distance", 10), Int("spawn-protection", 16));
    }

    /// <summary>Validates and saves; returns what was applied at once and what needs a restart.</summary>
    public async Task<string> SaveAsync(string serverId, ServerSettingsDto s, CancellationToken ct = default)
    {
        var def = Get(serverId);
        var old = Read(serverId);
        var name = s.Name.Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("The name must be 1-40 characters.");
        if (!Difficulties.Contains(s.Difficulty)) throw new ArgumentException("Unknown difficulty.");
        if (!GameModes.Contains(s.GameMode)) throw new ArgumentException("Unknown game mode.");
        Range(s.MaxPlayers, 1, 100, "Max players");
        Range(s.ViewDistance, 3, 32, "View distance");
        Range(s.SimulationDistance, 3, 32, "Simulation distance");
        Range(s.SpawnProtection, 0, 64, "Spawn protection");
        if (s.MemoryMb != old.MemoryMb)
        {
            if (!old.MemoryEditable) throw new ArgumentException($"{def.Name}'s memory is set in its system service, not here.");
            Range(s.MemoryMb, 512, 65536, "Memory (MB)");
        }

        var p = Props(def);
        ConfigFiles.SetProperty(p, "difficulty", s.Difficulty);
        ConfigFiles.SetProperty(p, "gamemode", s.GameMode);
        ConfigFiles.SetProperty(p, "pvp", s.Pvp ? "true" : "false");
        ConfigFiles.SetProperty(p, "allow-flight", s.AllowFlight ? "true" : "false");
        ConfigFiles.SetProperty(p, "max-players", s.MaxPlayers.ToString());
        ConfigFiles.SetProperty(p, "view-distance", s.ViewDistance.ToString());
        ConfigFiles.SetProperty(p, "simulation-distance", s.SimulationDistance.ToString());
        ConfigFiles.SetProperty(p, "spawn-protection", s.SpawnProtection.ToString());
        if (s.MemoryMb != old.MemoryMb) WriteHeap(EnvFile(def), s.MemoryMb);
        if (name != def.Name || s.MemoryMb != def.MemoryMb)
        {
            def.Name = name;
            def.MemoryMb = s.MemoryMb;
            _registry.Update(def);
            // The gate's messages name the server.
            if (_registry.Proxy is { } proxy && Directory.Exists(proxy.Path)) ProxyService.WriteJavaOnly(proxy, _registry.All, _proxy.PublicUrl);
        }

        // Live where Minecraft allows it; the rest is read at start.
        var live = new List<string>();
        await using (var rcon = new MinecraftRconClient(def.RconHost, def.RconPort))
        {
            var running = false;
            try { running = await rcon.ConnectAsync(def.RconPassword, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            if (running)
            {
                if (s.Difficulty != old.Difficulty) { await rcon.SendCommandAsync("difficulty " + s.Difficulty, ct); live.Add("difficulty"); }
                if (s.GameMode != old.GameMode) { await rcon.SendCommandAsync("defaultgamemode " + s.GameMode, ct); live.Add("default game mode"); }
            }
        }
        var restart = new List<string>();
        if (s.Pvp != old.Pvp) restart.Add("PvP");
        if (s.AllowFlight != old.AllowFlight) restart.Add("allow flight");
        if (s.MaxPlayers != old.MaxPlayers) restart.Add("max players");
        if (s.ViewDistance != old.ViewDistance) restart.Add("view distance");
        if (s.SimulationDistance != old.SimulationDistance) restart.Add("simulation distance");
        if (s.SpawnProtection != old.SpawnProtection) restart.Add("spawn protection");
        if (s.MemoryMb != old.MemoryMb) restart.Add("memory");
        _logger.LogWarning("Saved settings of {Server}: live {Live}; on restart {Restart}", def.Id, string.Join(", ", live), string.Join(", ", restart));

        var message = "Saved.";
        if (live.Count > 0) message += " Applied now: " + string.Join(", ", live) + ".";
        if (restart.Count > 0) message += " Restart " + def.Name + " to apply: " + string.Join(", ", restart) + ".";
        return message;
    }

    private static void Range(int value, int min, int max, string what)
    {
        if (value < min || value > max) throw new ArgumentException($"{what} must be {min}-{max}.");
    }

    /// <summary>Rewrites -Xmx (and -Xms, capped at 1 GB) in aubscraft.env's JAVA_OPTS, keeping every other line and option.</summary>
    public static void WriteHeap(string envFile, int memoryMb)
    {
        var lines = File.ReadAllLines(envFile).ToList();
        var i = lines.FindIndex(l => l.StartsWith("JAVA_OPTS=", StringComparison.Ordinal));
        if (i < 0) throw new InvalidOperationException("aubscraft.env has no JAVA_OPTS line.");
        var opts = Xms().Replace(Xmx().Replace(lines[i], $"-Xmx{memoryMb}M"), $"-Xms{Math.Min(1024, memoryMb)}M");
        lines[i] = opts;
        // In place: the file keeps its owner and mode.
        File.WriteAllText(envFile, string.Join("\n", lines) + "\n");
    }

    [GeneratedRegex(@"-Xmx\d+[gGmMkK]?")] private static partial Regex Xmx();
    [GeneratedRegex(@"-Xms\d+[gGmMkK]?")] private static partial Regex Xms();
}

public record ServerSettingsDto(string Name, int MemoryMb, bool MemoryEditable, string Difficulty, string GameMode, bool Pvp, bool AllowFlight,
    int MaxPlayers, int ViewDistance, int SimulationDistance, int SpawnProtection);
