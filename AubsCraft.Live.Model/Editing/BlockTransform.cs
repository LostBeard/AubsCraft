using System.Text;

namespace AubsCraft.Live.Model.Editing;

/// <summary>
/// Turning and mirroring blocks, for pasting a blueprint rotated: the block's own states turn with it (a stair that faced north faces east after a quarter turn clockwise, a log lying along x lies along z, a wall's
/// east arm is its south arm, a sign's rotation advances four of its sixteen steps), and so do positions. Turns are quarter turns clockwise seen from above (north to east); x runs east and z south.
/// Mirroring is done before turning: <c>mirrorX</c> swaps east and west (x becomes the width minus x), <c>mirrorZ</c> north and south.
/// </summary>
public static class BlockTransform
{
    private static readonly string[] Compass = ["north", "east", "south", "west"];

    /// <summary>A block (<c>minecraft:oak_stairs[facing=north,half=bottom]</c>) mirrored, then turned <paramref name="quarterTurns"/> times clockwise.</summary>
    public static string Transform(string block, int quarterTurns, bool mirrorX = false, bool mirrorZ = false)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        int open = block.IndexOf('[');
        if (open < 0 || (quarterTurns == 0 && !mirrorX && !mirrorZ)) return block;
        string name = block[..open];
        var props = new List<KeyValuePair<string, string>>();
        foreach (var pair in block[(open + 1)..].TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) props.Add(new(pair[..eq], pair[(eq + 1)..]));
        }
        var values = props.ToDictionary(p => p.Key, p => p.Value);
        bool flipped = mirrorX ^ mirrorZ;   // one mirror swaps handedness; two are a half turn

        // A mirror first: x flips east and west, z flips north and south
        if (mirrorX || mirrorZ)
        {
            string Mirror(string dir) => dir switch
            {
                "east" when mirrorX => "west",
                "west" when mirrorX => "east",
                "north" when mirrorZ => "south",
                "south" when mirrorZ => "north",
                _ => dir,
            };
            if (values.TryGetValue("facing", out var f)) values["facing"] = Mirror(f);
            // The four arms: each arm keeps its value, moved to the mirrored side
            var arms = Compass.Where(values.ContainsKey).ToDictionary(d => d, d => values[d]);
            foreach (var (dir, value) in arms) values.Remove(dir);
            foreach (var (dir, value) in arms) values[Mirror(dir)] = value;
            if (values.TryGetValue("rotation", out var r) && int.TryParse(r, out var step))
            {
                if (mirrorX) step = (16 - step) % 16;
                if (mirrorZ) step = (8 - step + 16) % 16;
                values["rotation"] = step.ToString();
            }
            if (flipped)
            {
                if (values.TryGetValue("shape", out var shape) && (shape.EndsWith("_left", StringComparison.Ordinal) || shape.EndsWith("_right", StringComparison.Ordinal))) values["shape"] = SwapSide(shape);
                if (values.TryGetValue("hinge", out var hinge)) values["hinge"] = SwapSide(hinge);
                if (values.TryGetValue("type", out var type) && type is "left" or "right") values["type"] = SwapSide(type);
            }
        }

        // Then the turns
        for (int i = 0; i < quarterTurns; i++)
        {
            if (values.TryGetValue("facing", out var f)) values["facing"] = Turn(f);
            if (values.TryGetValue("axis", out var a)) values["axis"] = a switch { "x" => "z", "z" => "x", _ => a };
            if (values.TryGetValue("rotation", out var r) && int.TryParse(r, out var step)) values["rotation"] = ((step + 4) % 16).ToString();
            var arms = Compass.Where(values.ContainsKey).ToDictionary(d => d, d => values[d]);
            foreach (var (dir, _) in arms) values.Remove(dir);
            foreach (var (dir, value) in arms) values[Turn(dir)] = value;
        }

        var sb = new StringBuilder(name).Append('[');
        // The order of the properties as they came
        bool first = true;
        foreach (var (key, _) in props)
        {
            if (!values.TryGetValue(key, out var v)) continue;
            if (!first) sb.Append(',');
            sb.Append(key).Append('=').Append(v);
            first = false;
        }
        return sb.Append(']').ToString();
    }

    private static string Turn(string dir) => dir switch { "north" => "east", "east" => "south", "south" => "west", "west" => "north", _ => dir };

    private static string SwapSide(string s) =>
        s.EndsWith("_left", StringComparison.Ordinal) ? s[..^5] + "_right"
        : s.EndsWith("_right", StringComparison.Ordinal) ? s[..^6] + "_left"
        : s == "left" ? "right" : s == "right" ? "left" : s;

    /// <summary>
    /// Where a block at (x, z) of a blueprint W wide (east-west) and D deep (north-south) goes: mirrored, then turned. Returns the new place and the new width and depth (they swap on an odd number of turns).
    /// </summary>
    public static (int X, int Z, int Width, int Depth) Place(int x, int z, int width, int depth, int quarterTurns, bool mirrorX = false, bool mirrorZ = false)
    {
        if (mirrorX) x = width - 1 - x;
        if (mirrorZ) z = depth - 1 - z;
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        for (int i = 0; i < quarterTurns; i++)
        {
            (x, z) = (depth - 1 - z, x);
            (width, depth) = (depth, width);
        }
        return (x, z, width, depth);
    }
}
