using System.Numerics;
using System.Reflection;
using System.Text.Json;

namespace AubsCraft.Live.Model.Entities;

/// <summary>
/// The game's own creature models, as its model classes build them (<c>createBodyLayer()</c> of each: the part tree, every part's pivot and rotation, every box's texture offset, corner, size and
/// inflation): <c>mc-models.json</c>, dumped from the client jar by <c>tools/modeldump</c> for the Minecraft version the servers run. A model here is exactly the game's geometry, so a creature built
/// from it stands, faces and wears its texture the way it does in the game; <see cref="EntityModels"/> says which creature wears which model.
/// </summary>
public static class GameModels
{
    private static readonly Lazy<Dictionary<string, JsonElement>> Dump = new(Load);
    private static JsonDocument? _document;

    private static Dictionary<string, JsonElement> Load()
    {
        using var stream = typeof(GameModels).Assembly.GetManifestResourceStream("AubsCraft.Live.Model.mc-models.json")
            ?? throw new InvalidOperationException("mc-models.json is not embedded in AubsCraft.Live.Model");
        _document = JsonDocument.Parse(stream);
        var models = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in _document.RootElement.EnumerateObject()) models[p.Name] = p.Value;
        return models;
    }

    /// <summary>The models the dump holds (<c>GoatModel.createBodyLayer</c>, <c>PufferfishSmallModel.createBodyLayer</c>, ...).</summary>
    public static IEnumerable<string> Keys => Dump.Value.Keys;

    /// <summary>The size of the texture the model was made for, in pixels.</summary>
    public static (int Width, int Height) TextureSize(string key)
    {
        var tex = Get(key).GetProperty("tex");
        return (tex[0].GetInt32(), tex[1].GetInt32());
    }

    private static JsonElement Get(string key) => Dump.Value.TryGetValue(key, out var m) ? m : throw new KeyNotFoundException($"The game's model dump has no {key}");

    /// <summary>The default role of a part by its name in the game's model: the head and body, and the legs and arms by the names the game's quadrupeds and humanoids use.</summary>
    public static PartRole RoleByName(string name) => name switch
    {
        "head" => PartRole.Head,
        "body" => PartRole.Body,
        "right_hind_leg" => PartRole.RightHindLeg,
        "left_hind_leg" => PartRole.LeftHindLeg,
        "right_front_leg" => PartRole.RightFrontLeg,
        "left_front_leg" => PartRole.LeftFrontLeg,
        "right_leg" => PartRole.RightLeg,
        "left_leg" => PartRole.LeftLeg,
        "right_arm" => PartRole.RightArm,
        "left_arm" => PartRole.LeftArm,
        _ => PartRole.Static,
    };

    /// <summary>
    /// The parts of a model, parents before children (the top "root" the game's definitions all start with is dropped when it is only a frame). <paramref name="role"/> says what each part does
    /// (default <see cref="RoleByName"/>); parts named in <paramref name="hide"/> are left out with everything under them (a frog's tongue, a breeze's wind: things the game shows only at times).
    /// </summary>
    public static ModelPart[] Parts(string key, Func<string, PartRole>? role = null, IReadOnlySet<string>? hide = null)
    {
        role ??= RoleByName;
        var parts = new List<ModelPart>();
        var root = Get(key).GetProperty("root");
        Add(root, -1, true);
        return parts.ToArray();

        void Add(JsonElement node, int parent, bool top)
        {
            string name = node.GetProperty("n").GetString()!;
            if (hide != null && hide.Contains(name)) return;
            var pose = node.TryGetProperty("p", out var p) ? p : default;
            float Pose(int i) => pose.ValueKind == JsonValueKind.Array ? (float)pose[i].GetDouble() : 0f;
            bool hasCubes = node.TryGetProperty("c", out var cubes);
            int index = parent;
            // The definitions start with an empty "root": only a frame, unless it carries a pose of its own
            bool frame = top && !hasCubes && pose.ValueKind != JsonValueKind.Array && !node.TryGetProperty("s", out _);
            if (!frame)
            {
                var boxes = new List<ModelBox>();
                if (hasCubes)
                    foreach (var c in cubes.EnumerateArray())
                    {
                        var uv = c.GetProperty("uv"); var o = c.GetProperty("o"); var d = c.GetProperty("d");
                        float grow = 0f;
                        if (c.TryGetProperty("g", out var g)) grow = (float)Math.Max(g[0].GetDouble(), Math.Max(g[1].GetDouble(), g[2].GetDouble()));
                        bool mirror = c.TryGetProperty("m", out var m) && m.GetBoolean();
                        boxes.Add(new ModelBox((float)uv[0].GetDouble(), (float)uv[1].GetDouble(), (float)o[0].GetDouble(), (float)o[1].GetDouble(), (float)o[2].GetDouble(),
                            (float)d[0].GetDouble(), (float)d[1].GetDouble(), (float)d[2].GetDouble(), grow, mirror));
                    }
                Vector3? scale = null;
                if (node.TryGetProperty("s", out var s)) scale = new Vector3((float)s[0].GetDouble(), (float)s[1].GetDouble(), (float)s[2].GetDouble());
                parts.Add(new ModelPart(name, role(name), parent, new Vector3(Pose(0), Pose(1), Pose(2)), new Vector3(Pose(3), Pose(4), Pose(5)), boxes.ToArray(), PoseScale: scale));
                index = parts.Count - 1;
            }
            if (node.TryGetProperty("k", out var kids))
                foreach (var child in kids.EnumerateArray()) Add(child, index, false);
        }
    }
}
