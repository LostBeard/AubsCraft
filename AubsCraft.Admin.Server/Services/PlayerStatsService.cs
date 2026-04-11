using System.Text.Json;
using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;
using Microsoft.Data.Sqlite;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Reads player statistics from multiple data sources:
/// - Minecraft native stats (world/stats/{uuid}.json)
/// - Essentials userdata (plugins/Essentials/userdata/{uuid}.yml)
/// - CoreProtect SQLite (plugins/CoreProtect/database.db)
/// - Advancements (world/advancements/{uuid}.json)
/// </summary>
public class PlayerStatsService
{
    private readonly string _serverPath;
    private readonly RconService _rcon;
    private readonly ILogger<PlayerStatsService> _logger;

    // Cache for live device info (refreshed when queried)
    private Dictionary<string, string> _bedrockDeviceCache = [];
    private DateTime _lastDeviceCacheRefresh = DateTime.MinValue;

    // VR player data from VRDetect plugin
    private HashSet<string> _vrOnline = [];
    private HashSet<string> _vrKnown = [];
    private DateTime _lastVrCacheRefresh = DateTime.MinValue;

    private readonly bool _enableSqlite;

    public PlayerStatsService(IConfiguration configuration, RconService rcon, ILogger<PlayerStatsService> logger)
    {
        _logger = logger;
        _rcon = rcon;
        _serverPath = configuration.GetValue<string>("Minecraft:ServerPath") ?? "/opt/minecraft/server";
        // SQLite over SSHFS breaks CoreProtect's WAL checkpoints - only enable when running locally
        _enableSqlite = configuration.GetValue("Minecraft:EnableSqliteQueries", !OperatingSystem.IsWindows());
    }

    /// <summary>
    /// Gets a list of all known players from Essentials userdata.
    /// </summary>
    public List<PlayerSummary> GetAllPlayers()
    {
        var players = new List<PlayerSummary>();
        var userdataPath = Path.Combine(_serverPath, "plugins", "Essentials", "userdata");
        if (!Directory.Exists(userdataPath)) return players;

        foreach (var file in Directory.GetFiles(userdataPath, "*.yml"))
        {
            try
            {
                var uuid = Path.GetFileNameWithoutExtension(file);
                var yaml = File.ReadAllText(file);
                var name = ExtractYamlValue(yaml, "last-account-name") ?? uuid;
                var ip = ExtractYamlValue(yaml, "ip-address") ?? "";
                var loginMs = ExtractYamlLong(yaml, "timestamps.login");
                var logoutMs = ExtractYamlLong(yaml, "timestamps.logout");

                // Read play time from native stats
                var statsFile = Path.Combine(_serverPath, "world", "stats", $"{uuid}.json");
                long playTimeTicks = 0;
                if (File.Exists(statsFile))
                {
                    var statsJson = File.ReadAllText(statsFile);
                    playTimeTicks = ExtractStatValue(statsJson, "minecraft:play_time");
                }

                // Detect platform
                var isBedrock = name.StartsWith('.') || uuid.StartsWith("00000000-0000-0000");
                _bedrockDeviceCache.TryGetValue(name.TrimStart('.'), out var deviceOs);
                var (currentlyVR, everVR) = IsVR(uuid);

                players.Add(new PlayerSummary
                {
                    UUID = uuid,
                    Name = name,
                    IpAddress = ip,
                    LastLogin = loginMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(loginMs).UtcDateTime : null,
                    LastLogout = logoutMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(logoutMs).UtcDateTime : null,
                    PlayTimeTicks = playTimeTicks,
                    Platform = isBedrock ? "Bedrock" : "Java",
                    DeviceOS = currentlyVR ? "VR" : deviceOs,
                    IsVR = currentlyVR || everVR,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read player data from {File}", file);
            }
        }

        return players.OrderBy(p => p.Name).ToList();
    }

    /// <summary>
    /// Gets detailed stats for a specific player.
    /// </summary>
    public PlayerProfile? GetPlayerProfile(string uuid)
    {
        var profile = new PlayerProfile { UUID = uuid };

        // -- Essentials --
        var essFile = Path.Combine(_serverPath, "plugins", "Essentials", "userdata", $"{uuid}.yml");
        if (File.Exists(essFile))
        {
            var yaml = File.ReadAllText(essFile);
            profile.Name = ExtractYamlValue(yaml, "last-account-name") ?? uuid;
            profile.IpAddress = ExtractYamlValue(yaml, "ip-address") ?? "";
            profile.GodMode = ExtractYamlValue(yaml, "godmode") == "true";
            var loginMs = ExtractYamlLong(yaml, "timestamps.login");
            var logoutMs = ExtractYamlLong(yaml, "timestamps.logout");
            profile.LastLogin = loginMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(loginMs).UtcDateTime : null;
            profile.LastLogout = logoutMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(logoutMs).UtcDateTime : null;

            // Detect platform
            var isBedrock = profile.Name.StartsWith('.') || uuid.StartsWith("00000000-0000-0000");
            profile.Platform = isBedrock ? "Bedrock" : "Java";
            _bedrockDeviceCache.TryGetValue(profile.Name.TrimStart('.'), out var deviceOs);
            var (currentlyVR, everVR) = IsVR(uuid);
            profile.DeviceOS = currentlyVR ? "VR" : deviceOs;
            profile.IsVR = currentlyVR || everVR;
        }

        // -- Native Stats --
        var statsFile = Path.Combine(_serverPath, "world", "stats", $"{uuid}.json");
        if (File.Exists(statsFile))
        {
            var json = File.ReadAllText(statsFile);
            profile.PlayTimeTicks = ExtractStatValue(json, "minecraft:play_time");
            profile.TotalWorldTimeTicks = ExtractStatValue(json, "minecraft:total_world_time");
            profile.Deaths = ExtractStatValue(json, "minecraft:deaths");
            profile.MobKills = ExtractStatValue(json, "minecraft:mob_kills");
            profile.DamageTaken = ExtractStatValue(json, "minecraft:damage_taken");
            profile.DamageDealt = ExtractStatValue(json, "minecraft:damage_dealt");
            profile.Jumps = ExtractStatValue(json, "minecraft:jump");
            profile.TimeSinceDeathTicks = ExtractStatValue(json, "minecraft:time_since_death");
            profile.SleepInBed = ExtractStatValue(json, "minecraft:sleep_in_bed");

            // Distance (stored in cm)
            profile.WalkDistanceCm = ExtractStatValue(json, "minecraft:walk_one_cm");
            profile.SprintDistanceCm = ExtractStatValue(json, "minecraft:sprint_one_cm");
            profile.CrouchDistanceCm = ExtractStatValue(json, "minecraft:crouch_one_cm");
            profile.FlyDistanceCm = ExtractStatValue(json, "minecraft:fly_one_cm");
            profile.FallDistanceCm = ExtractStatValue(json, "minecraft:fall_one_cm");
            profile.SwimDistanceCm = ExtractStatValue(json, "minecraft:walk_under_water_one_cm");

            // Mob kills breakdown
            profile.KilledMobs = ExtractStatCategory(json, "minecraft:killed");
            // Killed by breakdown
            profile.KilledByMobs = ExtractStatCategory(json, "minecraft:killed_by");
            // Blocks mined
            profile.BlocksMined = ExtractStatCategory(json, "minecraft:mined");
            // Items used
            profile.ItemsUsed = ExtractStatCategory(json, "minecraft:used");
            // Items picked up
            profile.ItemsPickedUp = ExtractStatCategory(json, "minecraft:picked_up");
        }

        // -- Advancements --
        var advFile = Path.Combine(_serverPath, "world", "advancements", $"{uuid}.json");
        if (File.Exists(advFile))
        {
            try
            {
                var json = File.ReadAllText(advFile);
                var doc = JsonDocument.Parse(json);
                var completedCount = 0;
                var advancements = new List<string>();

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name == "DataVersion") continue;
                    // Skip recipe unlocks
                    if (prop.Name.Contains("recipes/")) continue;

                    if (prop.Value.TryGetProperty("done", out var done) && done.GetBoolean())
                    {
                        completedCount++;
                        // Clean up the name
                        var name = prop.Name.Replace("minecraft:", "").Replace("/", " - ");
                        advancements.Add(name);
                    }
                }
                profile.AdvancementsCompleted = completedCount;
                profile.Advancements = advancements;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse advancements for {UUID}", uuid);
            }
        }

        // -- CoreProtect session data (skip over SSHFS to avoid breaking CoreProtect's WAL) --
        if (_enableSqlite) try
        {
            var dbPath = Path.Combine(_serverPath, "plugins", "CoreProtect", "database.db");
            if (File.Exists(dbPath))
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();

                // Get user ID
                using var userCmd = conn.CreateCommand();
                userCmd.CommandText = "SELECT rowid FROM co_user WHERE uuid = @uuid OR user = @name";
                userCmd.Parameters.AddWithValue("@uuid", uuid);
                userCmd.Parameters.AddWithValue("@name", profile.Name ?? "");
                var userId = userCmd.ExecuteScalar();

                if (userId != null)
                {
                    var uid = Convert.ToInt64(userId);

                    // Block stats
                    using var blockCmd = conn.CreateCommand();
                    blockCmd.CommandText = "SELECT action, COUNT(*) FROM co_block WHERE user = @uid GROUP BY action";
                    blockCmd.Parameters.AddWithValue("@uid", uid);
                    using var blockReader = blockCmd.ExecuteReader();
                    while (blockReader.Read())
                    {
                        var action = blockReader.GetInt32(0);
                        var count = blockReader.GetInt64(1);
                        if (action == 0) profile.BlocksBroken = count;
                        else if (action == 1) profile.BlocksPlaced = count;
                    }

                    // Session count
                    using var sessCmd = conn.CreateCommand();
                    sessCmd.CommandText = "SELECT COUNT(*) FROM co_session WHERE user = @uid AND action = 1";
                    sessCmd.Parameters.AddWithValue("@uid", uid);
                    profile.SessionCount = Convert.ToInt64(sessCmd.ExecuteScalar() ?? 0);

                    // First session
                    using var firstCmd = conn.CreateCommand();
                    firstCmd.CommandText = "SELECT MIN(time) FROM co_session WHERE user = @uid";
                    firstCmd.Parameters.AddWithValue("@uid", uid);
                    var firstTime = firstCmd.ExecuteScalar();
                    if (firstTime != null && firstTime != DBNull.Value)
                        profile.FirstSeen = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(firstTime)).UtcDateTime;

                    // Chat message count
                    using var chatCmd = conn.CreateCommand();
                    chatCmd.CommandText = "SELECT COUNT(*) FROM co_chat WHERE user = @uid";
                    chatCmd.Parameters.AddWithValue("@uid", uid);
                    profile.ChatMessages = Convert.ToInt64(chatCmd.ExecuteScalar() ?? 0);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read CoreProtect data for {UUID}", uuid);
        }

        // -- GriefPrevention --
        var gpFile = Path.Combine(_serverPath, "plugins", "GriefPreventionData", "PlayerData", uuid);
        if (File.Exists(gpFile))
        {
            var lines = File.ReadAllLines(gpFile);
            if (lines.Length >= 3 && long.TryParse(lines[1], out var accrued))
                profile.ClaimBlocksAccrued = accrued;
        }

        return profile;
    }

    /// <summary>
    /// Refreshes Bedrock device info by querying Geyser via RCON.
    /// Only queries if cache is older than 30 seconds.
    /// </summary>
    public async Task RefreshDeviceCacheAsync()
    {
        if ((DateTime.UtcNow - _lastDeviceCacheRefresh).TotalSeconds < 30) return;
        if (!_rcon.IsConnected) return;

        try
        {
            // "geyser list" returns something like:
            // "1 player(s) online: Noob607 (Android)"
            var response = await _rcon.SendCommandAsync("geyser list");
            _lastDeviceCacheRefresh = DateTime.UtcNow;

            if (string.IsNullOrEmpty(response)) return;

            // Parse: "PlayerName (DeviceOS)" patterns
            var matches = Regex.Matches(response, @"(\w+)\s+\(([^)]+)\)");
            var newCache = new Dictionary<string, string>();
            foreach (Match m in matches)
            {
                newCache[m.Groups[1].Value] = m.Groups[2].Value;
            }
            _bedrockDeviceCache = newCache;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Geyser device cache");
        }
    }

    /// <summary>
    /// Refreshes VR player data from VRDetect plugin's JSON file.
    /// </summary>
    public void RefreshVrCache()
    {
        if ((DateTime.UtcNow - _lastVrCacheRefresh).TotalSeconds < 10) return;

        try
        {
            var vrFile = Path.Combine(_serverPath, "plugins", "VRDetect", "vr-players.json");
            if (!File.Exists(vrFile)) return;

            var json = File.ReadAllText(vrFile);
            _lastVrCacheRefresh = DateTime.UtcNow;

            // Parse "online" and "known" sections
            _vrOnline = ExtractUuidsFromJsonSection(json, "online");
            _vrKnown = ExtractUuidsFromJsonSection(json, "known");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh VR player cache");
        }
    }

    private static HashSet<string> ExtractUuidsFromJsonSection(string json, string section)
    {
        var result = new HashSet<string>();
        var sectionStart = json.IndexOf($"\"{section}\"");
        if (sectionStart < 0) return result;
        var braceStart = json.IndexOf('{', sectionStart);
        var braceEnd = json.IndexOf('}', braceStart + 1);
        if (braceStart < 0 || braceEnd < 0) return result;

        var block = json.Substring(braceStart + 1, braceEnd - braceStart - 1);
        foreach (Match m in Regex.Matches(block, @"""([^""]+)""\s*:\s*""([^""]+)"""))
        {
            result.Add(m.Groups[1].Value); // UUID
        }
        return result;
    }

    /// <summary>
    /// Checks if a player UUID is currently in VR or has ever used VR.
    /// </summary>
    public (bool currentlyVR, bool everVR) IsVR(string uuid)
    {
        RefreshVrCache();
        return (_vrOnline.Contains(uuid), _vrKnown.Contains(uuid));
    }

    /// <summary>
    /// Gets aggregate world statistics.
    /// </summary>
    public WorldStats GetWorldStats()
    {
        var stats = new WorldStats();

        // Count players
        var userdataPath = Path.Combine(_serverPath, "plugins", "Essentials", "userdata");
        if (Directory.Exists(userdataPath))
            stats.TotalPlayers = Directory.GetFiles(userdataPath, "*.yml").Length;

        // Aggregate native stats
        var statsDir = Path.Combine(_serverPath, "world", "stats");
        if (Directory.Exists(statsDir))
        {
            foreach (var file in Directory.GetFiles(statsDir, "*.json"))
            {
                var json = File.ReadAllText(file);
                stats.TotalPlayTimeTicks += ExtractStatValue(json, "minecraft:play_time");
                stats.TotalDeaths += ExtractStatValue(json, "minecraft:deaths");
                stats.TotalMobKills += ExtractStatValue(json, "minecraft:mob_kills");
                stats.TotalJumps += ExtractStatValue(json, "minecraft:jump");
                stats.TotalDistanceCm += ExtractStatValue(json, "minecraft:walk_one_cm")
                    + ExtractStatValue(json, "minecraft:sprint_one_cm")
                    + ExtractStatValue(json, "minecraft:fly_one_cm")
                    + ExtractStatValue(json, "minecraft:walk_under_water_one_cm");
            }
        }

        // CoreProtect aggregate (skip over SSHFS)
        if (_enableSqlite) try
        {
            var dbPath = Path.Combine(_serverPath, "plugins", "CoreProtect", "database.db");
            if (File.Exists(dbPath))
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT action, COUNT(*) FROM co_block GROUP BY action";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var action = reader.GetInt32(0);
                    var count = reader.GetInt64(1);
                    if (action == 0) stats.TotalBlocksBroken = count;
                    else if (action == 1) stats.TotalBlocksPlaced = count;
                }

                using var chatCmd = conn.CreateCommand();
                chatCmd.CommandText = "SELECT COUNT(*) FROM co_chat";
                stats.TotalChatMessages = Convert.ToInt64(chatCmd.ExecuteScalar() ?? 0);

                using var sessCmd = conn.CreateCommand();
                sessCmd.CommandText = "SELECT COUNT(*) FROM co_session WHERE action = 1";
                stats.TotalSessions = Convert.ToInt64(sessCmd.ExecuteScalar() ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read CoreProtect world stats");
        }

        // World age from level.dat would need NBT parsing - skip for now
        // Plugin count
        var pluginsPath = Path.Combine(_serverPath, "plugins");
        if (Directory.Exists(pluginsPath))
            stats.PluginCount = Directory.GetFiles(pluginsPath, "*.jar").Length;

        return stats;
    }

    // -- Helpers --

    private static long ExtractStatValue(string json, string statName)
    {
        // Look for "minecraft:custom": { ... "statName": value ... }
        var pattern = $@"""{Regex.Escape(statName)}""\s*:\s*(\d+)";
        var match = Regex.Match(json, pattern);
        return match.Success ? long.Parse(match.Groups[1].Value) : 0;
    }

    private static Dictionary<string, long> ExtractStatCategory(string json, string category)
    {
        var result = new Dictionary<string, long>();
        // Find the category block and extract key-value pairs
        var catPattern = $@"""{Regex.Escape(category)}""\s*:\s*\{{([^}}]+)\}}";
        var catMatch = Regex.Match(json, catPattern);
        if (!catMatch.Success) return result;

        var block = catMatch.Groups[1].Value;
        var itemPattern = @"""minecraft:(\w+)""\s*:\s*(\d+)";
        foreach (Match m in Regex.Matches(block, itemPattern))
        {
            result[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
        }
        return result;
    }

    private static string? ExtractYamlValue(string yaml, string key)
    {
        if (key.Contains('.'))
        {
            // Handle nested keys like "timestamps.login"
            // YAML format:
            // timestamps:
            //   login: '1775915340987'
            var parts = key.Split('.', 2);
            var parentPattern = new Regex($@"^{Regex.Escape(parts[0])}:\s*$", RegexOptions.Multiline);
            var parentMatch = parentPattern.Match(yaml);
            if (!parentMatch.Success) return null;

            // Search for the child key in the indented block after the parent
            var afterParent = yaml[(parentMatch.Index + parentMatch.Length)..];
            var childPattern = new Regex($@"^\s+{Regex.Escape(parts[1])}:\s*(.+)$", RegexOptions.Multiline);
            var childMatch = childPattern.Match(afterParent);
            return childMatch.Success ? childMatch.Groups[1].Value.Trim().Trim('\'', '"') : null;
        }

        var pattern = new Regex($@"^{Regex.Escape(key)}:\s*(.+)$", RegexOptions.Multiline);
        var match = pattern.Match(yaml);
        return match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : null;
    }

    private static long ExtractYamlLong(string yaml, string key)
    {
        var val = ExtractYamlValue(yaml, key);
        return val != null && long.TryParse(val, out var result) ? result : 0;
    }
}
