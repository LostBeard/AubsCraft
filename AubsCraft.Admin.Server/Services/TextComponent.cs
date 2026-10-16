using System.Text;
using System.Text.Json;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// The plain words of a text component as a world file stores it: a JSON text (<c>"Hello"</c>, <c>{"text":"Hello","extra":[...]}</c>, an array of those, or plain words), or, since 1.21.5, NBT (a string,
/// a compound with <c>text</c> and <c>extra</c>, a list of them). Colours, bold and the like are dropped; a sign is written in one dye.
/// </summary>
public static class TextComponent
{
    public static string Plain(object? component)
    {
        var builder = new StringBuilder();
        Append(builder, component, 0);
        return builder.ToString();
    }

    private static void Append(StringBuilder into, object? component, int depth)
    {
        if (depth > 16 || component == null) return;
        switch (component)
        {
            case string text:
                AppendText(into, text, depth);
                break;
            case NbtCompound compound:
                if (compound.ContainsKey("text") && compound["text"] is string t) into.Append(t);
                else if (compound.ContainsKey("translate") && compound["translate"] is string key) into.Append(key);
                if (compound.ContainsKey("extra") && compound["extra"] is NbtList extra)
                    foreach (var part in extra) Append(into, part, depth + 1);
                break;
            case NbtList list:
                foreach (var part in list) Append(into, part, depth + 1);
                break;
        }
    }

    // A string is JSON when it looks like it (a quoted string, an object, an array); anything else is the words themselves
    private static void AppendText(StringBuilder into, string text, int depth)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length > 0 && (trimmed[0] is '"' or '{' or '['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                AppendJson(into, doc.RootElement, depth);
                return;
            }
            catch (JsonException) { }
        }
        into.Append(text);
    }

    private static void AppendJson(StringBuilder into, JsonElement element, int depth)
    {
        if (depth > 16) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                into.Append(element.GetString());
                break;
            case JsonValueKind.Number:
                into.Append(element.GetRawText());
                break;
            case JsonValueKind.Array:
                foreach (var part in element.EnumerateArray()) AppendJson(into, part, depth + 1);
                break;
            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) into.Append(text.GetString());
                else if (element.TryGetProperty("translate", out var key) && key.ValueKind == JsonValueKind.String) into.Append(key.GetString());
                if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                    foreach (var part in extra.EnumerateArray()) AppendJson(into, part, depth + 1);
                break;
        }
    }
}
