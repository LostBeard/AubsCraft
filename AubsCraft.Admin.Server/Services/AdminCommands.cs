using System.Globalization;
using System.Text.RegularExpressions;
using AubsCraft.Live.Model.Editing;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Turns an admin request into the server command that does it - or refuses it. Everything that goes into a command is checked here, because a command is a line of text the server obeys:
/// a name is a Minecraft name and nothing else, an item is a name with a namespace, a number is a number in range, a message has no line breaks. The panel never builds a command out of text it has not checked.
/// </summary>
public static class AdminCommands
{
    private static readonly Regex PlayerName = new(@"^\.?[A-Za-z0-9_]{1,16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Item = new(@"^[a-z0-9_.\-]+:[a-z0-9_./\-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static readonly string[] Gamemodes = ["survival", "creative", "adventure", "spectator"];
    public static readonly string[] Times = ["day", "noon", "night", "midnight"];
    public static readonly string[] Weathers = ["clear", "rain", "thunder"];
    public const int MaxMessage = 200, MaxReason = 100, MaxGive = 2304, MaxCoordinate = 30_000_000;

    public static bool IsPlayerName(string? name) => name != null && PlayerName.IsMatch(name);

    /// <summary>A line of free text made safe for a command: no control characters, trimmed, cut short.</summary>
    public static string CleanText(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max];
    }

    /// <summary>
    /// The command(s) for an action on a player or the world (a few actions are two commands), or an error saying why not. A ban is not here: it goes through the network-wide ban list.
    /// </summary>
    public static (string[]? Commands, string? Error) For(AdminRequest r)
    {
        static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        if (r.Kind == "world")
        {
            switch (r.Action)
            {
                case "time":
                    if (r.Value is { } t && Times.Contains(t)) return ([$"time set {t}"], null);
                    if (int.TryParse(r.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks is >= 0 and <= 24000) return ([$"time set {ticks}"], null);
                    return (null, "Time is day, noon, night, midnight or a number of ticks (0 to 24000)");
                case "weather":
                    return r.Value is { } w && Weathers.Contains(w) ? ([$"weather {w}"], null) : (null, "Weather is clear, rain or thunder");
                case "say":
                    var message = CleanText(r.Value, MaxMessage);
                    return message.Length == 0 ? (null, "Say what?") : ([$"say {message}"], null);
                case "save":
                    return (["save-all"], null);
                default:
                    return (null, "Unknown world action: " + r.Action);
            }
        }
        if (r.Kind != "player") return (null, "Unknown kind: " + r.Kind);
        if (!IsPlayerName(r.Name)) return (null, "That is not a player name");
        var name = r.Name!;
        switch (r.Action)
        {
            case "kick":
                var reason = CleanText(r.Reason, MaxReason);
                return ([reason.Length == 0 ? $"kick {name}" : $"kick {name} {reason}"], null);
            case "gamemode":
                return r.Value is { } mode && Gamemodes.Contains(mode) ? ([$"gamemode {mode} {name}"], null) : (null, "Gamemode is survival, creative, adventure or spectator");
            case "teleport":
                if (!Finite(r.X) || !Finite(r.Y) || !Finite(r.Z) || Math.Abs(r.X) > MaxCoordinate || Math.Abs(r.Z) > MaxCoordinate || r.Y < -2048 || r.Y > 2048) return (null, "That place is outside the world");
                return ([$"tp {name} {N(r.X)} {N(r.Y)} {N(r.Z)}"], null);
            case "heal":
                return ([$"effect give {name} minecraft:instant_health 1 10 true", $"effect give {name} minecraft:saturation 1 20 true"], null);
            case "feed":
                return ([$"effect give {name} minecraft:saturation 1 20 true"], null);
            case "kill":
                return ([$"kill {name}"], null);
            case "clear":
                return ([$"clear {name}"], null);
            case "give":
                if (r.Value is not { } item || !Item.IsMatch(item) || item.Length > 100) return (null, "That is not an item");
                if (r.Count < 1 || r.Count > MaxGive) return (null, $"A count from 1 to {MaxGive}");
                return ([$"give {name} {item} {r.Count}"], null);
            default:
                return (null, "Unknown player action: " + r.Action);
        }
    }

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
