using System.Text.Json;
using AubsCraft.Assets.Model;

namespace AubsCraft.Assets;

/// <summary>Which model a block state uses and how it is turned (degrees about X and Y, in steps of 90); with <paramref name="UvLock"/> its textures keep their direction in the world.</summary>
public sealed record StateModel(string Model, int X, int Y, bool UvLock = false);

/// <summary>A part of a multipart blockstate: the condition under which it applies (null: always) and its model.</summary>
public sealed record MultipartPart(ShapeCondition? When, StateModel Model);

/// <summary>A block's blockstate file: variants by property combination, or multipart (a base model plus parts that apply when properties match).</summary>
public sealed class BlockStateFile
{
    /// <summary>Property combination ("facing=north,lit=false", "" for none) to its model (the first of a random set).</summary>
    public List<(string Key, StateModel Model)> Variants { get; } = new();
    /// <summary>The models of a multipart file, in order; the first applies without conditions in most of them (the post of a fence).</summary>
    public List<StateModel> Parts { get; } = new();
    /// <summary>The same parts with their conditions.</summary>
    public List<MultipartPart> Multipart { get; } = new();

    public static BlockStateFile Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var file = new BlockStateFile();
        var root = doc.RootElement;
        if (root.TryGetProperty("variants", out var variants))
            foreach (var v in variants.EnumerateObject())
                if (ReadModel(v.Value) is { } m) file.Variants.Add((v.Name, m));
        if (root.TryGetProperty("multipart", out var multipart))
            foreach (var part in multipart.EnumerateArray())
                if (part.TryGetProperty("apply", out var apply) && ReadModel(apply) is { } m)
                {
                    file.Parts.Add(m);
                    file.Multipart.Add(new MultipartPart(part.TryGetProperty("when", out var when) ? ReadCondition(when) : null, m));
                }
        return file;
    }

    // A variant is one model object or an array of weighted alternatives (a random rotation of a block): the first is enough
    private static StateModel? ReadModel(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var first in e.EnumerateArray()) return ReadModel(first);
            return null;
        }
        if (!e.TryGetProperty("model", out var model) || model.GetString() is not { } id) return null;
        return new StateModel(ModelResolver.StripNamespace(id),
            e.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
            e.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
            e.TryGetProperty("uvlock", out var uvlock) && uvlock.ValueKind == JsonValueKind.True);
    }

    // "when": { "north": "true", "up": "true|false" } or { "OR": [ ... ] } or { "AND": [ ... ] }
    private static ShapeCondition ReadCondition(JsonElement e)
    {
        var c = new ShapeCondition();
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name == "OR") c.Or = p.Value.EnumerateArray().Select(ReadCondition).ToList();
            else if (p.Name == "AND") c.And = p.Value.EnumerateArray().Select(ReadCondition).ToList();
            else (c.Props ??= new())[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString().ToLowerInvariant();
        }
        return c;
    }

    // Property values that mean "the plain state": not lit, not open, facing north, bottom half, upright. A block NAME carries no properties, so
    // the state to draw is the plainest one.
    private static readonly HashSet<string> PlainValues = new(StringComparer.Ordinal)
    {
        "false", "0", "north", "bottom", "y", "lower", "floor", "normal", "none", "straight", "single", "low", "north_south",
    };

    /// <summary>The state the viewer draws for a block name: the variant with no properties, else the one with the fewest non-plain property values (the first on a tie), else the first part.</summary>
    public StateModel? DefaultModel()
    {
        if (Variants.Count > 0)
        {
            StateModel? best = null;
            int bestScore = int.MaxValue;
            foreach (var (key, model) in Variants)
            {
                int score = key.Length == 0 ? 0 : key.Split(',').Count(kv => !PlainValues.Contains(kv[(kv.IndexOf('=') + 1)..]));
                if (score < bestScore) { best = model; bestScore = score; }
            }
            return best;
        }
        return Parts.Count > 0 ? Parts[0] : null;
    }
}
