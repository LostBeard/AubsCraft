using System.Text.Json;
using AubsCraft.Assets.Model;

namespace AubsCraft.Assets;

// The blocks that are not cubes (stairs, slabs, fences, walls, doors, trapdoors, ladders, lanterns, farmland...): their models become boxes and their blockstate rules are kept, so
// the viewer can work out the boxes of any state. See ShapeResolver.
public sealed partial class BlockAssetBuilder
{
    private readonly List<ShapeModelAsset> _shapeModels = new();
    private readonly Dictionary<string, int> _shapeModelIndex = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions ShapeSig = new() { WriteIndented = false };

    /// <summary>Most boxes a state may have (the viewer's shape table holds 32 a shape); one with more is left a cube.</summary>
    public const int MaxBoxesPerState = 32;

    private enum ModelKind { Empty, Cube, Cross, Thin, Shape }

    private ModelKind Classify(string name, StateModel state)
    {
        var model = _models.Resolve(state.Model);
        if (model == null || model.Elements.Count == 0) return ModelKind.Empty;
        if (ForcedCross.Contains(name) || CrossAncestors.Any(model.HasAncestor)) return ModelKind.Cross;
        if (DescribeCube(name, model, state.X, state.Y) != null) return ModelKind.Cube;
        // A thin layer is drawn as the cell's cube squashed flat: right for a carpet or a snow layer, which fill the cell's footprint, wrong for a button or a candle, which do not
        return ThicknessOf(model) > 0 && FillsTheFootprint(model) ? ModelKind.Thin : ModelKind.Shape;
    }

    private static bool FillsTheFootprint(ResolvedModel model) =>
        model.Elements.Any(e => Math.Min(e.From[0], e.To[0]) <= 1 && Math.Max(e.From[0], e.To[0]) >= 15 && Math.Min(e.From[2], e.To[2]) <= 1 && Math.Max(e.From[2], e.To[2]) >= 15);

    /// <summary>
    /// The shape rules of a block, or null when it is not one: it has a model that is not a cube, a plant or a flat layer, and none that is a plant. Its properties are the ones that choose
    /// the model (a variant's key, or the properties a multipart condition names), in the order the server should name them.
    /// </summary>
    private (ShapeLogic Logic, string[] Props)? DescribeShapes(string name)
    {
        var statePath = $"assets/minecraft/blockstates/{name}.json";
        if (Invisible.Contains(name) || name.EndsWith("rail", StringComparison.Ordinal) || ForcedCross.Contains(name) || !_jar.Exists(statePath)) return null;
        var file = BlockStateFile.Parse(_jar.ReadText(statePath));
        var models = file.Variants.Select(v => v.Model).Concat(file.Parts).DistinctBy(m => (m.Model, m.X, m.Y, m.UvLock)).ToList();
        if (models.Count == 0) return null;
        var kinds = models.Select(m => Classify(name, m)).ToList();
        if (kinds.Contains(ModelKind.Cross) || !kinds.Contains(ModelKind.Shape)) return null;

        var logic = new ShapeLogic();
        string[] props;
        if (file.Variants.Count > 0)
        {
            foreach (var (key, model) in file.Variants)
                if (ToRef(name, model) is { } r) logic.Variants.Add(new ShapeVariant { Key = key, Model = r });
            if (logic.Variants.Count == 0) return null;
            props = RelevantProps(file, logic);
        }
        else
        {
            foreach (var part in file.Multipart)
                if (ToRef(name, part.Model) is { } r) logic.Parts.Add(new ShapePart { When = part.When, Models = { r } });
            if (logic.Parts.Count == 0) return null;
            var seen = new List<string>();
            void Walk(ShapeCondition? c)
            {
                if (c == null) return;
                if (c.Props != null) foreach (var p in c.Props.Keys) if (!seen.Contains(p)) seen.Add(p);
                c.And?.ForEach(Walk);
                c.Or?.ForEach(Walk);
            }
            foreach (var part in logic.Parts) Walk(part.When);
            props = seen.ToArray();
        }

        // A state with more boxes than the engine's shape takes would lose some: leave such a block a cube
        if (MostBoxes(logic, props) > MaxBoxesPerState)
        {
            _notes.Add($"{name}: a state has more than {MaxBoxesPerState} boxes, drawn as a cube");
            return null;
        }
        return (logic, props);
    }

    // The properties that matter for a variants blockstate: changing only that property changes which model (turned how) is used
    private static string[] RelevantProps(BlockStateFile file, ShapeLogic logic)
    {
        var described = logic.Variants.Select(v => (Props: ParseKeyOrEmpty(v.Key), Sig: $"{v.Model.Model}|{v.Model.X}|{v.Model.Y}|{v.Model.UvLock}")).ToList();
        if (described[0].Props.Count == 0) return Array.Empty<string>();
        var relevant = new List<string>();
        foreach (var prop in described[0].Props.Select(p => p.Key))
        {
            bool matters = false;
            foreach (var a in described)
            {
                foreach (var b in described)
                {
                    if (!SameExcept(a.Props, b.Props, prop)) continue;
                    if (a.Sig != b.Sig) { matters = true; break; }
                }
                if (matters) break;
            }
            if (matters) relevant.Add(prop);
        }
        return relevant.ToArray();
    }

    private static List<KeyValuePair<string, string>> ParseKeyOrEmpty(string key) => key.Length == 0 ? new() : ParseKey(key);

    // The most boxes any state of the block has: every combination of the values its conditions name (and "something else": the value that matches none) is tried
    private int MostBoxes(ShapeLogic logic, string[] props)
    {
        int Boxes(ShapeRef r) => r.Model >= 0 && r.Model < _shapeModels.Count ? _shapeModels[r.Model].Elements.Count : 0;
        if (logic.Variants.Count > 0) return logic.Variants.Max(v => Boxes(v.Model));
        var domain = new Dictionary<string, HashSet<string>>();
        void Collect(ShapeCondition? c)
        {
            if (c == null) return;
            if (c.Props != null)
                foreach (var (prop, value) in c.Props)
                {
                    if (!domain.TryGetValue(prop, out var set)) domain[prop] = set = new();
                    foreach (var v in value.Split('|')) set.Add(v);
                }
            c.And?.ForEach(Collect);
            c.Or?.ForEach(Collect);
        }
        foreach (var p in logic.Parts) Collect(p.When);
        var names = domain.Keys.ToList();
        var values = names.Select(n => domain[n].Append("~other").ToList()).ToList();
        long combos = 1;
        foreach (var v in values) combos *= v.Count;
        if (combos > 200_000) return int.MaxValue;
        int most = 0;
        var state = new Dictionary<string, string>();
        var index = new int[names.Count];
        for (long c = 0; c < combos; c++)
        {
            for (int i = 0; i < names.Count; i++) state[names[i]] = values[i][index[i]];
            most = Math.Max(most, ShapeResolver.SelectModels(logic, state).Sum(Boxes));
            for (int i = 0; i < names.Count; i++) { if (++index[i] < values[i].Count) break; index[i] = 0; }
        }
        return most;
    }

    private ShapeRef? ToRef(string name, StateModel state)
    {
        var model = _models.Resolve(state.Model);
        if (model == null || model.Elements.Count == 0) return null;
        int index = ShapeModelIndex(model);
        return index < 0 ? null : new ShapeRef { Model = index, X = state.X, Y = state.Y, UvLock = state.UvLock };
    }

    // The model as boxes with their tiles; models that come out the same are one
    private int ShapeModelIndex(ResolvedModel model)
    {
        var asset = new ShapeModelAsset();
        foreach (var e in model.Elements)
        {
            var element = new ShapeElementAsset
            {
                From = e.From.Select(v => Math.Clamp((int)Math.Round(v), 0, 16)).ToArray(),
                To = e.To.Select(v => Math.Clamp((int)Math.Round(v), 0, 16)).ToArray(),
            };
            for (int i = 0; i < 3; i++)
                if (element.To[i] < element.From[i]) (element.From[i], element.To[i]) = (element.To[i], element.From[i]);
            for (int f = 0; f < 6; f++)
            {
                var face = new ShapeFaceAsset();
                if (e.Faces.TryGetValue(FaceDirs[f], out var mf) && model.ResolveTexture(mf.Texture) is { } texture)
                {
                    face.Tile = _tiles.GetTexture(texture);
                    face.Rotation = ((mf.Rotation % 360) + 360) % 360;
                    face.Tint = mf.TintIndex >= 0;
                    if (mf.Uv != null) face.Uv = mf.Uv.Select(v => Math.Clamp((int)Math.Round(v), 0, 16)).ToArray();
                }
                element.Faces[f] = face;
            }
            asset.Elements.Add(element);
        }
        string sig = JsonSerializer.Serialize(asset, ShapeSig);
        if (_shapeModelIndex.TryGetValue(sig, out var existing)) return existing;
        _shapeModels.Add(asset);
        return _shapeModelIndex[sig] = _shapeModels.Count - 1;
    }
}
