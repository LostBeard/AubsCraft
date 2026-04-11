using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Parses Minecraft Paper 1.21.5 log lines into ActivityEvents.
/// Log format: [HH:MM:SS] [Thread/LEVEL]: Message
/// Handles both Java Edition and Bedrock/Geyser (dot-prefixed) player names.
/// </summary>
public static partial class LogLineParser
{
    // Player join: "SpudArt joined the game"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+(\S+) joined the game")]
    private static partial Regex JoinPattern();

    // Player leave: "SpudArt left the game"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+(\S+) left the game")]
    private static partial Regex LeavePattern();

    // Chat: "<SpudArt> hello everyone"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+<(\S+)>\s+(.+)")]
    private static partial Regex ChatPattern();

    // Death: "SpudArt was slain by Spider"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+(\S+)\s+(was slain|was shot|drowned|burned|fell|starved|suffocated|was blown|hit the ground|was killed|tried to swim|was poked|was squashed|was impaled|was fireballed|was stung|went off|walked into|was pricked|withered|died|experienced kinetic)(.*)")]
    private static partial Regex DeathPattern();

    // Advancement: "SpudArt has made the advancement [Monster Hunter]"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+(\S+) has made the advancement \[(.+)\]")]
    private static partial Regex AdvancementPattern();

    // Whitelist rejection - Java: "Disconnecting HereticSpawn (/192.168.1.2:57334): You are not whitelisted"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+Disconnecting\s+(\S+)\s+\(/([\d.]+:\d+)\):\s+You are not whitelisted")]
    private static partial Regex WhitelistRejectJavaPattern();

    // Whitelist rejection - Bedrock/IP only: "Disconnecting /192.168.1.2:0: You are not whitelisted"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+Disconnecting\s+/([\d.]+:\d+):\s+You are not whitelisted")]
    private static partial Regex WhitelistRejectBedrockPattern();

    // Geyser disconnect with name: "[Geyser-Spigot] Noob607 has disconnected from the Java server"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+\[Geyser-Spigot\]\s+(\S+)\s+has disconnected.*You are not whitelisted")]
    private static partial Regex WhitelistRejectGeyserPattern();

    // Server command: "HereticSpawn issued server command: /gamemode creative"
    [GeneratedRegex(@"\[(\d{2}:\d{2}:\d{2})\].*?:\s+(\S+) issued server command:\s+(.+)")]
    private static partial Regex ServerCommandPattern();

    public static ActivityEventDto? Parse(string line)
    {
        Match m;

        if ((m = JoinPattern().Match(line)).Success)
        {
            var player = m.Groups[2].Value;
            return new ActivityEventDto(
                ParseTime(m.Groups[1].Value),
                ActivityEventType.PlayerJoin,
                player,
                $"{player} joined the game");
        }

        if ((m = LeavePattern().Match(line)).Success)
        {
            var player = m.Groups[2].Value;
            return new ActivityEventDto(
                ParseTime(m.Groups[1].Value),
                ActivityEventType.PlayerLeave,
                player,
                $"{player} left the game");
        }

        if ((m = ChatPattern().Match(line)).Success)
        {
            return new ActivityEventDto(
                ParseTime(m.Groups[1].Value),
                ActivityEventType.Chat,
                m.Groups[2].Value,
                m.Groups[3].Value);
        }

        if ((m = DeathPattern().Match(line)).Success)
        {
            var player = m.Groups[2].Value;
            var cause = m.Groups[3].Value + m.Groups[4].Value;
            return new ActivityEventDto(
                ParseTime(m.Groups[1].Value),
                ActivityEventType.Death,
                player,
                $"{player} {cause}");
        }

        if ((m = AdvancementPattern().Match(line)).Success)
        {
            var player = m.Groups[2].Value;
            var advancement = m.Groups[3].Value;
            return new ActivityEventDto(
                ParseTime(m.Groups[1].Value),
                ActivityEventType.Advancement,
                player,
                $"{player} earned [{advancement}]");
        }

        if ((m = WhitelistRejectJavaPattern().Match(line)).Success)
        {
            var player = m.Groups[2].Value;
            var ip = m.Groups[3].Value;
            return new ActivityEventDto(
                ParseTime(m.Groups[1].Value),
                ActivityEventType.WhitelistRejection,
                player,
                $"{player} ({ip}) was rejected - not whitelisted");
        }

        if ((m = WhitelistRejectGeyserPattern().Match(line)).Success)
        {
            var player = m.Groups[2].Value;
            return new ActivityEventDto(
                ParseTime(m.Groups[1].Value),
                ActivityEventType.WhitelistRejection,
                player,
                $"{player} (Bedrock) was rejected - not whitelisted");
        }

        if ((m = WhitelistRejectBedrockPattern().Match(line)).Success)
        {
            var ip = m.Groups[2].Value;
            return new ActivityEventDto(
                ParseTime(m.Groups[1].Value),
                ActivityEventType.WhitelistRejection,
                null,
                $"Unknown player ({ip}) was rejected - not whitelisted");
        }

        return null;
    }

    private static DateTime ParseTime(string timeStr)
    {
        if (TimeSpan.TryParse(timeStr, out var time))
            return DateTime.Today.Add(time);
        return DateTime.UtcNow;
    }
}
