using System.Text.Json;

namespace AubsCraft.Assets;

/// <summary>One face of a model element: which texture (a "#variable" or an id), its rotation in degrees, whether it takes the block's tint, and the window of the texture it shows (u0, v0, u1, v1 in sixteenths) when the model gives one.</summary>
public sealed record ModelFace(string Texture, int Rotation, int TintIndex, string? CullFace, double[]? Uv = null);

/// <summary>A box of a block model, in 1/16 block units, with the faces it has (keys "north", "south", "east", "west", "up", "down").</summary>
public sealed record ModelElement(double[] From, double[] To, IReadOnlyDictionary<string, ModelFace> Faces)
{
    /// <summary>The box fills the whole block cell.</summary>
    public bool IsFullCube => From[0] == 0 && From[1] == 0 && From[2] == 0 && To[0] == 16 && To[1] == 16 && To[2] == 16;
}

/// <summary>A block model with its parent chain merged in: the child's textures win, the child's elements replace the parent's.</summary>
public sealed class ResolvedModel
{
    public required string Id { get; init; }
    /// <summary>The parents, nearest first ("block/cube_all", "block/cube", ...), without a namespace.</summary>
    public required IReadOnlyList<string> Ancestors { get; init; }
    /// <summary>Texture variables as written ("all" -> "block/stone" or "#other").</summary>
    public required IReadOnlyDictionary<string, string> Textures { get; init; }
    public required IReadOnlyList<ModelElement> Elements { get; init; }

    /// <summary>A texture reference ("#side" or "block/stone") to a texture id ("block/stone"), following variable references; null when a variable is unset.</summary>
    public string? ResolveTexture(string reference)
    {
        for (int hops = 0; hops < 16; hops++)
        {
            if (!reference.StartsWith('#')) return ModelResolver.StripNamespace(reference);
            if (!Textures.TryGetValue(reference[1..], out var next)) return null;
            reference = next;
        }
        return null;
    }

    public bool HasAncestor(string id) => Id == id || Ancestors.Contains(id);
}

/// <summary>Loads block models from the jar ("assets/minecraft/models/block/stone.json") and merges their parent chains.</summary>
public sealed class ModelResolver
{
    private readonly ClientJar _jar;
    private readonly Dictionary<string, ResolvedModel?> _cache = new();

    public ModelResolver(ClientJar jar) => _jar = jar;

    public static string StripNamespace(string id) => id.StartsWith("minecraft:", StringComparison.Ordinal) ? id["minecraft:".Length..] : id;

    /// <summary>The model with this id ("minecraft:block/stone" or "block/stone"), or null when the jar has none.</summary>
    public ResolvedModel? Resolve(string modelId)
    {
        var id = StripNamespace(modelId);
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var model = ResolveCore(id, depth: 0);
        _cache[id] = model;
        return model;
    }

    private ResolvedModel? ResolveCore(string id, int depth)
    {
        if (depth > 32) throw new InvalidDataException($"The model {id} has a parent chain that is too deep or circular");
        var path = $"assets/minecraft/models/{id}.json";
        if (!_jar.Exists(path)) return null;

        using var doc = JsonDocument.Parse(_jar.ReadText(path));
        var root = doc.RootElement;

        var textures = new Dictionary<string, string>();
        IReadOnlyList<ModelElement> elements = Array.Empty<ModelElement>();
        var ancestors = new List<string>();

        if (root.TryGetProperty("parent", out var parentProp) && parentProp.GetString() is { } parentRaw)
        {
            var parentId = StripNamespace(parentRaw);
            ancestors.Add(parentId);
            // builtin/generated, builtin/entity and the like have no file: they end the chain
            var parent = _jar.Exists($"assets/minecraft/models/{parentId}.json") ? ResolveCore(parentId, depth + 1) : null;
            if (parent != null)
            {
                foreach (var (k, v) in parent.Textures) textures[k] = v;
                elements = parent.Elements;
                ancestors.AddRange(parent.Ancestors);
            }
        }

        if (root.TryGetProperty("textures", out var texProp) && texProp.ValueKind == JsonValueKind.Object)
            foreach (var t in texProp.EnumerateObject())
                if (t.Value.ValueKind == JsonValueKind.String) textures[t.Name] = t.Value.GetString()!;

        if (root.TryGetProperty("elements", out var elProp) && elProp.ValueKind == JsonValueKind.Array)
            elements = elProp.EnumerateArray().Select(ParseElement).ToList();

        return new ResolvedModel { Id = id, Ancestors = ancestors, Textures = textures, Elements = elements };
    }

    private static ModelElement ParseElement(JsonElement e)
    {
        double[] Vec(string name) => e.GetProperty(name).EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var faces = new Dictionary<string, ModelFace>();
        if (e.TryGetProperty("faces", out var facesProp))
            foreach (var f in facesProp.EnumerateObject())
            {
                var v = f.Value;
                faces[f.Name] = new ModelFace(
                    v.GetProperty("texture").GetString() ?? "",
                    v.TryGetProperty("rotation", out var r) ? r.GetInt32() : 0,
                    v.TryGetProperty("tintindex", out var t) ? t.GetInt32() : -1,
                    v.TryGetProperty("cullface", out var c) ? c.GetString() : null,
                    v.TryGetProperty("uv", out var uv) && uv.ValueKind == JsonValueKind.Array && uv.GetArrayLength() == 4 ? uv.EnumerateArray().Select(n => n.GetDouble()).ToArray() : null);
            }
        return new ModelElement(Vec("from"), Vec("to"), faces);
    }
}
