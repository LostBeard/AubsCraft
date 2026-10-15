using System.Text.Json;

namespace AubsCraft.Assets;

/// <summary>Which model a block state uses and how it is turned (degrees about X and Y, in steps of 90).</summary>
public sealed record StateModel(string Model, int X, int Y);

/// <summary>A block's blockstate file: variants by property combination, or multipart (a base model plus parts that apply when properties match).</summary>
public sealed class BlockStateFile
{
    /// <summary>Property combination ("facing=north,lit=false", "" for none) to its model (the first of a random set).</summary>
    public List<(string Key, StateModel Model)> Variants { get; } = new();
    /// <summary>The models of a multipart file, in order; the first applies without conditions in most of them (the post of a fence).</summary>
    public List<StateModel> Parts { get; } = new();

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
                if (part.TryGetProperty("apply", out var apply) && ReadModel(apply) is { } m) file.Parts.Add(m);
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
            e.TryGetProperty("y", out var y) ? y.GetInt32() : 0);
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
