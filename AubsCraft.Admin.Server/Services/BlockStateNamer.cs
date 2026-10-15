using System.Text.Json;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Names a block as the viewer can tell it apart: <c>minecraft:oak_log[axis=x]</c> - the block's name and ONLY the properties that change how it looks (a log's axis, a furnace's
/// facing, a rail's shape, a crop's age), in the order the asset generator lists them. Every other property (waterlogged, powered, distance, persistent...) is dropped, so the
/// registry holds the states the picture needs and not the thousands the game has. The table of which properties matter comes from the asset generator
/// (<c>mc-state-props.json</c>, built from the game's own blockstate files); without it every block is just its name, as before.
/// </summary>
public sealed class BlockStateNamer
{
    private readonly Dictionary<string, string[]> _props;

    public BlockStateNamer(Dictionary<string, string[]> props) => _props = props;

    /// <summary>The namer used when reading chunks: set once at startup (<see cref="Load"/>), names only until then.</summary>
    public static BlockStateNamer Current { get; set; } = new(new());

    /// <summary>How many blocks have properties that matter.</summary>
    public int BlockCount => _props.Count;

    /// <summary>The table from the generator's file; an empty namer (names only) and a warning when it is missing or unreadable.</summary>
    public static BlockStateNamer Load(string path, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger?.LogWarning("No block state table at {Path}: blocks are named without their states (a lying log looks upright)", path);
                return new BlockStateNamer(new());
            }
            var table = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path)) ?? new();
            logger?.LogInformation("Block states: {Count} blocks have properties the viewer draws", table.Count);
            return new BlockStateNamer(table);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger?.LogError(ex, "Could not read the block state table {Path}", path);
            return new BlockStateNamer(new());
        }
    }

    /// <summary>The viewer's name of a block: its name plus the properties that matter, when it has any (the value a property is missing from the chunk is left out).</summary>
    public string Key(string name, IReadOnlyDictionary<string, object>? properties)
    {
        if (properties == null || properties.Count == 0 || !_props.TryGetValue(name, out var wanted)) return name;
        var parts = new List<string>(wanted.Length);
        foreach (var prop in wanted)
            if (properties.TryGetValue(prop, out var value) && value is string text) parts.Add($"{prop}={text}");
        return parts.Count == 0 ? name : $"{name}[{string.Join(',', parts)}]";
    }

    /// <summary>The plain block name of a key: <c>minecraft:oak_log[axis=x]</c> is <c>minecraft:oak_log</c>.</summary>
    public static string NameOf(string key)
    {
        int i = key.IndexOf('[');
        return i < 0 ? key : key[..i];
    }
}
