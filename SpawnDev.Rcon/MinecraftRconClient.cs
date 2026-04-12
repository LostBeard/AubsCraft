using System.Text.RegularExpressions;

namespace SpawnDev.Rcon;

/// <summary>
/// Utility for stripping Minecraft formatting codes from text.
/// </summary>
public static class MinecraftText
{
    /// <summary>
    /// Strips section-sign color/format codes (e.g. "§6", "§c") from Minecraft text.
    /// </summary>
    public static string StripColorCodes(string text)
        => Regex.Replace(text, @"§.", "");
}

/// <summary>
/// Minecraft-specific RCON client with typed commands for common server operations.
/// Wraps the generic Source RCON client with Minecraft command parsing.
/// </summary>
public class MinecraftRconClient : IAsyncDisposable, IDisposable
{
    private readonly RconClient _client;

    public bool IsConnected => _client.IsConnected;

    public event EventHandler? Disconnected
    {
        add => _client.Disconnected += value;
        remove => _client.Disconnected -= value;
    }

    public MinecraftRconClient(string host, int port = 25575)
    {
        _client = new RconClient(host, port);
    }

    public Task<bool> ConnectAsync(string password, CancellationToken cancellationToken = default)
        => _client.ConnectAsync(password, cancellationToken);

    public Task DisconnectAsync() => _client.DisconnectAsync();

    /// <summary>
    /// Send a raw command and get the response.
    /// </summary>
    public Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync(command, cancellationToken);

    // -- Player List --

    /// <summary>
    /// Gets the list of online players.
    /// </summary>
    public async Task<PlayerListResult> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendCommandAsync("list", cancellationToken);
        return PlayerListResult.Parse(response);
    }

    // -- Whitelist --

    public async Task<string> WhitelistAddAsync(string playerName, CancellationToken cancellationToken = default)
        => await _client.SendCommandAsync($"whitelist add {playerName}", cancellationToken);

    public async Task<string> WhitelistRemoveAsync(string playerName, CancellationToken cancellationToken = default)
        => await _client.SendCommandAsync($"whitelist remove {playerName}", cancellationToken);

    public async Task<List<string>> WhitelistListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendCommandAsync("whitelist list", cancellationToken);
        // Response: "There are N whitelisted players: name1, name2, name3"
        // or "There are 0 whitelisted players:"
        var colonIndex = response.IndexOf(':');
        if (colonIndex < 0 || colonIndex >= response.Length - 1)
            return [];

        var names = response[(colonIndex + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        return names;
    }

    public Task<string> WhitelistOnAsync(CancellationToken cancellationToken = default)
        => _client.SendCommandAsync("whitelist on", cancellationToken);

    public Task<string> WhitelistOffAsync(CancellationToken cancellationToken = default)
        => _client.SendCommandAsync("whitelist off", cancellationToken);

    public Task<string> WhitelistReloadAsync(CancellationToken cancellationToken = default)
        => _client.SendCommandAsync("whitelist reload", cancellationToken);

    // -- Kick / Ban --

    public Task<string> KickAsync(string playerName, string? reason = null, CancellationToken cancellationToken = default)
    {
        var cmd = reason != null ? $"kick {playerName} {reason}" : $"kick {playerName}";
        return _client.SendCommandAsync(cmd, cancellationToken);
    }

    public Task<string> BanAsync(string playerName, string? reason = null, CancellationToken cancellationToken = default)
    {
        var cmd = reason != null ? $"ban {playerName} {reason}" : $"ban {playerName}";
        return _client.SendCommandAsync(cmd, cancellationToken);
    }

    public Task<string> PardonAsync(string playerName, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"pardon {playerName}", cancellationToken);

    public async Task<List<string>> BanListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendCommandAsync("banlist", cancellationToken);
        // "There are no bans" or "There are N ban(s): name1, name2"
        var colonIndex = response.IndexOf(':');
        if (colonIndex < 0 || colonIndex >= response.Length - 1)
            return [];

        return response[(colonIndex + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    // -- Server Messages --

    public Task<string> SayAsync(string message, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"say {message}", cancellationToken);

    public Task<string> TellAsync(string playerName, string message, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"tell {playerName} {message}", cancellationToken);

    // -- World Control --

    public Task<string> SetTimeAsync(string time, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"time set {time}", cancellationToken);

    /// <summary>
    /// Queries the current world time. Returns formatted time string and ticks.
    /// EssentialsX "time" returns: "The current time in world is 12:47 or 12:47 PM or 6784 ticks."
    /// </summary>
    public async Task<TimeQueryResult> QueryTimeAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendCommandAsync("time", cancellationToken);
        var clean = MinecraftText.StripColorCodes(response).Trim();
        // Parse ticks from "NNNN ticks" and time from "HH:MM AM/PM"
        var ticksMatch = Regex.Match(clean, @"(\d+)\s+ticks");
        var timeMatch = Regex.Match(clean, @"(\d{1,2}:\d{2}\s*[AP]M)");
        var ticks = ticksMatch.Success ? int.Parse(ticksMatch.Groups[1].Value) : 0;
        var formatted = timeMatch.Success ? timeMatch.Groups[1].Value : FormatTimeTicks(ticks);
        return new TimeQueryResult(ticks, formatted);
    }

    private static string FormatTimeTicks(int ticks)
    {
        var hours = (ticks / 1000 + 6) % 24;
        var minutes = (int)((ticks % 1000) / 1000.0 * 60);
        var period = hours >= 12 ? "PM" : "AM";
        var displayHours = hours % 12;
        if (displayHours == 0) displayHours = 12;
        return $"{displayHours}:{minutes:D2} {period}";
    }

    /// <summary>
    /// Sets the weather using the vanilla command (bypasses EssentialsX which requires world arg from console).
    /// </summary>
    public Task<string> SetWeatherAsync(string weather, int? duration = null, CancellationToken cancellationToken = default)
    {
        var cmd = duration.HasValue ? $"minecraft:weather {weather} {duration.Value}" : $"minecraft:weather {weather}";
        return _client.SendCommandAsync(cmd, cancellationToken);
    }

    /// <summary>
    /// Gets a player's position via EssentialsX getpos command.
    /// Returns null if the player is not online.
    /// </summary>
    public async Task<PlayerPosition?> GetPlayerPositionAsync(string playerName, CancellationToken cancellationToken = default)
    {
        var response = await _client.SendCommandAsync($"essentials:getpos {playerName}", cancellationToken);
        var clean = MinecraftText.StripColorCodes(response);
        if (clean.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return null;
        // EssentialsX format: "Player is at X, Y, Z in world"
        var match = Regex.Match(clean, @"(-?[\d.]+),?\s*(-?[\d.]+),?\s*(-?[\d.]+)");
        if (!match.Success) return null;
        return new PlayerPosition(
            playerName,
            float.Parse(match.Groups[1].Value),
            float.Parse(match.Groups[2].Value),
            float.Parse(match.Groups[3].Value));
    }

    public Task<string> SetDifficultyAsync(string difficulty, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"difficulty {difficulty}", cancellationToken);

    public Task<string> SetGamemodeAsync(string playerName, string mode, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"gamemode {mode} {playerName}", cancellationToken);

    // -- Server Admin --

    public Task<string> OpAsync(string playerName, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"op {playerName}", cancellationToken);

    public Task<string> DeopAsync(string playerName, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"deop {playerName}", cancellationToken);

    public Task<string> TeleportAsync(string playerName, string destination, CancellationToken cancellationToken = default)
        => _client.SendCommandAsync($"tp {playerName} {destination}", cancellationToken);

    public Task<string> SaveAllAsync(CancellationToken cancellationToken = default)
        => _client.SendCommandAsync("save-all", cancellationToken);

    /// <summary>
    /// Gets server TPS (ticks per second). Requires Paper/Spigot.
    /// </summary>
    public async Task<TpsResult> GetTpsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendCommandAsync("tps", cancellationToken);
        return TpsResult.Parse(response);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

/// <summary>
/// Result of a time query showing ticks and formatted time.
/// </summary>
public record TimeQueryResult(int Ticks, string Formatted);

/// <summary>
/// A player's world position.
/// </summary>
public record PlayerPosition(string Name, float X, float Y, float Z);

/// <summary>
/// Result of a "list" command showing online players.
/// </summary>
public record PlayerListResult(int Online, int Max, List<string> Players)
{
    // Matches both vanilla ("of a max of") and Paper ("out of maximum") formats
    private static readonly Regex ListPattern = new(
        @"There are (\d+) (?:of a max of|out of maximum) (\d+) players? online[.:]?(.*)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static PlayerListResult Parse(string response)
    {
        // Strip Minecraft color codes (section sign + character)
        var clean = Regex.Replace(response, @"§.", "").Trim();
        var match = ListPattern.Match(clean);
        if (!match.Success)
            return new PlayerListResult(0, 0, []);

        var online = int.Parse(match.Groups[1].Value);
        var max = int.Parse(match.Groups[2].Value);

        // Player names may be on the same line after "online:" or on subsequent lines
        // Paper sends: "There are N out of maximum M players online.\nName1, Name2"
        var playersPart = match.Groups[3].Value;
        var matchEnd = match.Index + match.Length;
        if (matchEnd < clean.Length)
        {
            // Append any text after the regex match (handles newline-separated player names)
            playersPart += " " + clean[matchEnd..];
        }

        // Paper/LuckPerms groups players by permission group: "default: Player1, Player2\nadmin: Player3"
        // Strip group prefixes (word followed by colon at start of line) before parsing names
        var lines = playersPart.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var players = new List<string>();
        foreach (var line in lines)
        {
            // Strip "groupname: " prefix if present (e.g., "default: HereticSpawn, SpudArt")
            var names = Regex.IsMatch(line, @"^\w+:\s") ? line[(line.IndexOf(':') + 1)..] : line;
            players.AddRange(names
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => !string.IsNullOrWhiteSpace(n)));
        }

        return new PlayerListResult(online, max, players);
    }
}

/// <summary>
/// Result of Paper/Spigot "tps" command.
/// </summary>
public record TpsResult(double Tps1Min, double Tps5Min, double Tps15Min)
{
    private static readonly Regex TpsPattern = new(
        @"(\d+\.?\d*),\s*(\d+\.?\d*),\s*(\d+\.?\d*)",
        RegexOptions.Compiled);

    public static TpsResult Parse(string response)
    {
        // Strip Minecraft color codes (section sign + character)
        var clean = Regex.Replace(response, @"§.", "");
        var match = TpsPattern.Match(clean);
        if (!match.Success)
            return new TpsResult(20, 20, 20);

        return new TpsResult(
            double.Parse(match.Groups[1].Value),
            double.Parse(match.Groups[2].Value),
            double.Parse(match.Groups[3].Value));
    }
}
