using System.Text;
using System.Text.Json;

namespace AubsCraft.Live.Model.Editing;

/// <summary>
/// An admin's action on a player or the world, as the viewer sends it to the panel. <see cref="Kind"/> is <c>player</c> (with <see cref="Name"/>) or <c>world</c>. Player actions: kick, ban, gamemode (<see cref="Value"/>
/// survival, creative, adventure or spectator), teleport (to <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/>), heal, feed, kill, clear (the inventory), give (<see cref="Value"/> an item, <see cref="Count"/>).
/// World actions: time (<see cref="Value"/> day, noon, night, midnight or a number of ticks), weather (clear, rain, thunder), say (<see cref="Value"/> is the message), save.
/// </summary>
public sealed class AdminRequest
{
    public string Kind { get; set; } = "player";
    public string Action { get; set; } = "";
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? Reason { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Count { get; set; } = 1;
}

/// <summary>What the server said to an admin action.</summary>
public sealed record AdminReply(bool Ok, string Message);

/// <summary>The admin messages as JSON, by hand (see <see cref="EditJson"/> for why).</summary>
public static class AdminJson
{
    public static string Write(AdminRequest r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("kind", r.Kind);
            w.WriteString("action", r.Action);
            if (r.Name != null) w.WriteString("name", r.Name);
            if (r.Value != null) w.WriteString("value", r.Value);
            if (r.Reason != null) w.WriteString("reason", r.Reason);
            w.WriteNumber("x", r.X); w.WriteNumber("y", r.Y); w.WriteNumber("z", r.Z);
            w.WriteNumber("count", r.Count);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static AdminReply Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string message = r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? ""
                : r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            return new AdminReply(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, message);
        }
        catch (JsonException) { return new AdminReply(false, json); }
    }
}
