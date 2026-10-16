using System.Text;
using System.Text.Json;

namespace AubsCraft.Live.Model.Editing;

/// <summary>
/// The edit messages as JSON, written and read by hand: the viewer is trimmed for the browser, and reflection-based serialisation of its own types (which nothing refers to statically) breaks there. The
/// property names are the web style (camelCase) the panel's endpoints expect.
/// </summary>
public static class EditJson
{
    public static string Write(EditRequest request)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            if (request.Label != null) w.WriteString("label", request.Label);
            w.WriteStartArray("ops");
            foreach (var op in request.Ops)
            {
                w.WriteStartObject();
                w.WriteString("op", op.Op);
                Triple(w, "at", op.At); Triple(w, "from", op.From); Triple(w, "to", op.To);
                if (op.Block != null) w.WriteString("block", op.Block);
                if (op.Only != null) w.WriteString("only", op.Only);
                if (op.Hollow) w.WriteBoolean("hollow", true);
                if (op.Palette != null)
                {
                    w.WriteStartArray("palette");
                    foreach (var p in op.Palette) w.WriteStringValue(p);
                    w.WriteEndArray();
                }
                if (op.Data != null) w.WriteString("data", op.Data);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Triple(Utf8JsonWriter w, string name, int[]? v)
    {
        if (v == null) return;
        w.WriteStartArray(name);
        foreach (var n in v) w.WriteNumberValue(n);
        w.WriteEndArray();
    }

    public static PickReply ReadPick(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var reply = new PickReply { Hit = r.TryGetProperty("hit", out var h) && h.ValueKind == JsonValueKind.True };
        if (!reply.Hit) return reply;
        reply.X = Int(r, "x"); reply.Y = Int(r, "y"); reply.Z = Int(r, "z");
        reply.Nx = Int(r, "nx"); reply.Ny = Int(r, "ny"); reply.Nz = Int(r, "nz");
        reply.Block = r.TryGetProperty("block", out var b) ? b.GetString() ?? "" : "";
        reply.Distance = Dbl(r, "distance"); reply.Hx = Dbl(r, "hx"); reply.Hy = Dbl(r, "hy"); reply.Hz = Dbl(r, "hz");
        return reply;
    }

    public static EditResult ReadResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new EditResult(
            r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
            r.TryGetProperty("edit", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
            r.TryGetProperty("placed", out var p) ? p.GetInt64() : 0,
            r.TryGetProperty("skipped", out var s) ? s.GetInt64() : 0,
            r.TryGetProperty("applied", out var a) && a.ValueKind == JsonValueKind.True,
            r.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null);
    }

    /// <summary>The message of an error reply (<c>{"error": "..."}</c>), or the text as it is.</summary>
    public static string ReadError(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? json : json; }
        catch (JsonException) { return json; }
    }

    public static string Write(BlueprintRequest r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("name", r.Name);
            Triple(w, "from", r.From); Triple(w, "to", r.To);
            w.WriteBoolean("trim", r.Trim);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Write(PasteRequest r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("name", r.Name);
            Triple(w, "at", r.At);
            w.WriteNumber("turns", r.Turns);
            w.WriteBoolean("mirrorX", r.MirrorX);
            w.WriteBoolean("mirrorZ", r.MirrorZ);
            w.WriteBoolean("withAir", r.WithAir);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static BlueprintItem ReadBlueprint(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ItemOf(doc.RootElement);
    }

    public static List<BlueprintItem> ReadBlueprints(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<BlueprintItem>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var e in doc.RootElement.EnumerateArray()) list.Add(ItemOf(e));
        return list;
    }

    private static BlueprintItem ItemOf(JsonElement e) => new()
    {
        Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        Width = Int(e, "width"), Height = Int(e, "height"), Depth = Int(e, "depth"), Blocks = Int(e, "blocks"),
        Author = e.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "",
    };
    private static int Int(JsonElement r, string name) => r.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
    private static double Dbl(JsonElement r, string name) => r.TryGetProperty(name, out var v) ? v.GetDouble() : 0;
}
