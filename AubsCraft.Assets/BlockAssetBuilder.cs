using System.Text;
using AubsCraft.Assets.Model;
using StbImageSharp;

namespace AubsCraft.Assets;

/// <summary>What <see cref="BlockAssetBuilder.Build"/> made: the block descriptions, the atlas image, and a report for people.</summary>
public sealed record BuildResult(BlockAssets Assets, byte[] AtlasRgba, int AtlasWidth, int AtlasHeight, string Report);

/// <summary>
/// Turns a client jar into the viewer's block assets. For every blockstate file it picks the plainest state (a block NAME carries no
/// properties), resolves that state's model, and describes the block: a full cube (a tile per face, rotations baked in, overlays such as the
/// grass fringe composited), a cross-shaped plant, water, or a "shape" (stairs, slabs, fences...: not a cube, drawn as one until shapes are
/// meshed). Animated textures keep their frames as consecutive tiles and a per-tick timeline.
/// </summary>
public sealed partial class BlockAssetBuilder
{
    // Face order everywhere: +X, -X, +Z, -Z, +Y, -Y = east, west, south, north, up, down
    private static readonly string[] FaceDirs = { "east", "west", "south", "north", "up", "down" };
    // Horizontal directions clockwise seen from above
    private static readonly string[] Clockwise = { "north", "east", "south", "west" };

    // Blocks that are never drawn
    private static readonly HashSet<string> Invisible = new(StringComparer.Ordinal)
    {
        "air", "cave_air", "void_air", "light", "barrier", "structure_void", "moving_piston",
    };

    // Plants whose model is not a plain crossed pair (or has no blockstate model we can read): crossed planes all the same
    private static readonly HashSet<string> ForcedCross = new(StringComparer.Ordinal)
    {
        "wildflowers", "pink_petals", "bamboo", "sugar_cane", "seagrass", "tall_seagrass", "kelp", "kelp_plant", "bush", "firefly_bush",
        "vine", "glow_lichen", "sculk_vein", "hanging_roots", "weeping_vines", "weeping_vines_plant", "twisting_vines", "twisting_vines_plant",
        "cave_vines", "cave_vines_plant", "nether_sprouts", "crimson_roots", "warped_roots",
    };

    private static readonly string[] CrossAncestors = { "block/cross", "block/tinted_cross", "block/crop" };

    private readonly ClientJar _jar;
    private readonly string _gameVersion;
    private readonly ModelResolver _models;
    private readonly TileSet _tiles;
    private readonly Colormap? _grass, _foliage;
    private readonly List<string> _notes = new();

    public BlockAssetBuilder(ClientJar jar, string gameVersion, int tileSize = 16)
    {
        _jar = jar;
        _gameVersion = gameVersion;
        _models = new ModelResolver(jar);
        _tiles = new TileSet(jar, tileSize);
        _grass = Colormap.TryLoad(jar, "assets/minecraft/textures/colormap/grass.png");
        _foliage = Colormap.TryLoad(jar, "assets/minecraft/textures/colormap/foliage.png");
    }

    // Plains: temperature 0.8, downfall 0.4
    private (int r, int g, int b) PlainsGrass => _grass?.Sample(0.8, 0.4) ?? (145, 189, 89);
    private (int r, int g, int b) PlainsFoliage => _foliage?.Sample(0.8, 0.4) ?? (119, 171, 47);
    private static readonly (int r, int g, int b) PlainsWater = (63, 118, 228);

    public BuildResult Build()
    {
        var assets = new BlockAssets { GameVersion = _gameVersion, TileSize = _tiles.TileSize };
        var names = _jar.List("assets/minecraft/blockstates/")
            .Where(p => p.EndsWith(".json", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToList();
        // Not blocks with a blockstate file: fluids and the air variants
        foreach (var special in new[] { "water", "flowing_water", "lava", "flowing_lava", "bubble_column", "air", "cave_air", "void_air", "light", "barrier", "structure_void", "moving_piston" })
            if (!names.Contains(special)) names.Add(special);
        names.Sort(StringComparer.Ordinal);

        var shapes = new List<string>();
        var shaped = new HashSet<string>(StringComparer.Ordinal);
        var untextured = new List<string>();
        foreach (var name in names)
        {
            var asset = Describe(name);
            assets.Blocks["minecraft:" + name] = asset;
            if (asset.Kind == BlockKinds.Shape) shapes.Add(name);
            if (asset.Kind != BlockKinds.None && asset.Faces.All(f => f < 0)) untextured.Add(name);
            // Stairs, slabs, fences, doors...: boxes, and the rules that choose them
            if (DescribeShapes(name) is { } shape)
            {
                assets.Shapes["minecraft:" + name] = shape.Logic;
                assets.StateProps["minecraft:" + name] = shape.Props;
                asset.Shaped = true;
                shaped.Add(name);
            }
        }
        assets.ShapeModels = _shapeModels;

        // The states that look different from the default (a log lying down, a furnace facing east, a rail curving, a crop grown)
        foreach (var name in names)
        {
            if (shaped.Contains(name)) continue;
            if (DescribeStates(name) is not { } states) continue;
            assets.StateProps["minecraft:" + name] = states.Props;
            foreach (var (key, asset) in states.Assets) assets.Blocks[key] = asset;
        }

        var (atlas, w, h, perRow) = _tiles.BuildAtlas();
        assets.TileCount = _tiles.Count;
        assets.TilesPerRow = perRow;
        assets.Animations = _tiles.Animations.ToList();

        var report = new StringBuilder();
        report.AppendLine($"Block assets for Minecraft {_gameVersion}");
        report.AppendLine($"{assets.Blocks.Count} blocks, {assets.TileCount} tiles ({w}x{h} px atlas), {assets.Animations.Count} animated tiles");
        foreach (var g in assets.Blocks.Values.GroupBy(b => b.Kind).OrderBy(g => g.Key))
            report.AppendLine($"  {g.Key,-6} {g.Count()}");
        report.AppendLine($"  tinted {assets.Blocks.Values.Count(b => b.Tint != BlockTints.None)}, cutout {assets.Blocks.Values.Count(b => b.Render == BlockRenders.Cutout)}, translucent {assets.Blocks.Values.Count(b => b.Render == BlockRenders.Translucent)}");
        report.AppendLine($"Without any texture ({untextured.Count}): {string.Join(", ", untextured)}");
        report.AppendLine($"Not full cubes ({shapes.Count}); {shaped.Count} of them are drawn as their boxes ({_shapeModels.Count} models, {_shapeModels.Sum(m => m.Elements.Count)} boxes), the rest as cubes: {string.Join(", ", shapes.Where(s => !shaped.Contains(s)))}");
        if (_notes.Count > 0) report.AppendLine("Notes:").AppendLine(string.Join(Environment.NewLine, _notes.Select(n => "  " + n)));
        return new BuildResult(assets, atlas, w, h, report.ToString());
    }

    private BlockAsset Describe(string name)
    {
        if (Invisible.Contains(name)) return new BlockAsset { Kind = BlockKinds.None };

        // Fluids have no model: their texture is named here
        if (name is "water" or "flowing_water" or "bubble_column")
        {
            int t = _tiles.GetTexture("block/water_still");
            return new BlockAsset
            {
                Kind = BlockKinds.Water, Faces = Enumerable.Repeat(t, 6).ToArray(), TintFaces = 0b111111,
                Tint = BlockTints.Water, TintColor = Rgb(PlainsWater), Render = BlockRenders.Translucent,
            };
        }
        if (name is "lava" or "flowing_lava")
        {
            int t = _tiles.GetTexture("block/lava_still");
            return new BlockAsset { Kind = BlockKinds.Cube, Faces = Enumerable.Repeat(t, 6).ToArray() };
        }

        var statePath = $"assets/minecraft/blockstates/{name}.json";
        if (!_jar.Exists(statePath)) return new BlockAsset { Kind = BlockKinds.Cube };
        return DescribeModel(name, BlockStateFile.Parse(_jar.ReadText(statePath)).DefaultModel());
    }

    // The block drawn with this model of its blockstate (turned by the state's x and y)
    private BlockAsset DescribeModel(string name, StateModel? state)
    {
        var model = state == null ? null : _models.Resolve(state.Model);
        if (model == null)
        {
            _notes.Add($"{name}: no model");
            return new BlockAsset { Kind = BlockKinds.Cube };
        }

        if (ForcedCross.Contains(name) || CrossAncestors.Any(model.HasAncestor))
            return DescribeCross(name, model);

        // Rails: every shape (straight, curved, sloping) is drawn as a flat layer with the right texture turned the right way
        if (name.EndsWith("rail", StringComparison.Ordinal)) return DescribeFlat(name, model, state!.X, state.Y);
        var cube = DescribeCube(name, model, state!.X, state.Y);
        if (cube != null) return cube;
        return DescribeShape(name, model);
    }

    // A plant: one tile, tinted when the model says so
    private BlockAsset DescribeCross(string name, ResolvedModel model)
    {
        string? texture = null;
        int tintIndex = -1;
        foreach (var key in new[] { "cross", "plant", "crop", "texture" })
            if (model.Textures.ContainsKey(key)) { texture = model.ResolveTexture("#" + key); break; }
        foreach (var e in model.Elements)
            foreach (var f in e.Faces.Values)
            {
                texture ??= model.ResolveTexture(f.Texture);
                if (f.TintIndex >= 0) tintIndex = f.TintIndex;
            }
        int tile = texture == null ? -1 : _tiles.GetTexture(texture);
        var asset = new BlockAsset { Kind = BlockKinds.Cross, Faces = Enumerable.Repeat(tile, 6).ToArray(), Render = BlockRenders.Cutout };
        if (tintIndex >= 0 || name is "sugar_cane" or "bush" or "firefly_bush") ApplyTint(name, asset, 0b111111);
        return asset;
    }

    // A full cube: null when the model is not one (no element fills the cell with all six faces)
    private BlockAsset? DescribeCube(string name, ResolvedModel model, int xRotation, int yRotation)
    {
        var cubes = model.Elements.Where(e => e.IsFullCube).ToList();
        if (cubes.Count == 0) return null;
        if (!FaceDirs.All(d => cubes[0].Faces.ContainsKey(d))) return null;
        // Anything besides full-cell elements (a slab, a torch, a hanging bit) makes it a shape
        if (model.Elements.Count != cubes.Count) return null;

        var faces = new int[6];
        int tintMask = 0;
        for (int f = 0; f < 6; f++)
        {
            // The face now looking in direction f came from another face of the unturned model, its texture turned with it
            var (sourceFace, uvRotation) = SourceFace(f, xRotation, yRotation);
            string source = FaceDirs[sourceFace];

            var layers = cubes.Where(c => c.Faces.ContainsKey(source)).Select(c => c.Faces[source]).ToList();
            var baseFace = layers[0];
            var baseTexture = model.ResolveTexture(baseFace.Texture);
            int tile = baseTexture == null ? -1 : _tiles.GetTexture(baseTexture, baseFace.Rotation + uvRotation);
            if (baseFace.TintIndex >= 0 && tile >= 0) tintMask |= 1 << f;

            // Overlays (the grass fringe): composited into the tile, their tint baked in at the plains colour
            for (int i = 1; i < layers.Count && tile >= 0; i++)
            {
                var overlayTexture = model.ResolveTexture(layers[i].Texture);
                int overlay = overlayTexture == null ? -1 : _tiles.GetTexture(overlayTexture, layers[i].Rotation + uvRotation);
                if (overlay < 0) continue;
                var tint = layers[i].TintIndex >= 0 ? PlainsGrass : (255, 255, 255);
                int baseTile = tile;
                tile = _tiles.Add($"comp:{baseTile}|{overlay}|{tint.Item1},{tint.Item2},{tint.Item3}",
                    () => TileImages.Composite(_tiles.Pixels(baseTile), _tiles.Pixels(overlay), tint));
            }
            faces[f] = tile;
        }

        var asset = new BlockAsset { Kind = BlockKinds.Cube, Faces = faces, Render = RenderOf(name, faces) };
        if (tintMask != 0) ApplyTint(name, asset, tintMask);
        return asset;
    }

    // Not a cube: drawn as one with the model's main texture on every face (what the viewer did for everything before)
    private BlockAsset DescribeShape(string name, ResolvedModel model)
    {
        string? texture = null;
        int tintIndex = -1;
        foreach (var e in model.Elements)
            foreach (var f in e.Faces)
            {
                var t = model.ResolveTexture(f.Value.Texture);
                if (t == null) continue;
                // The first element's side face is a good stand-in for the block
                if (texture == null || f.Key is "north" or "south" or "east" or "west") texture ??= t;
                if (f.Value.TintIndex >= 0) tintIndex = f.Value.TintIndex;
            }
        texture ??= model.Textures.TryGetValue("particle", out var particle) ? model.ResolveTexture("#particle") : null;
        int tile = texture == null ? -1 : _tiles.GetTexture(texture);
        var asset = new BlockAsset { Kind = BlockKinds.Shape, Faces = Enumerable.Repeat(tile, 6).ToArray() };
        asset.Render = RenderOf(name, asset.Faces);
        asset.Thickness = ThicknessOf(model);
        if (tintIndex >= 0) ApplyTint(name, asset, 0b111111);
        return asset;
    }

    // ---- turned models ------------------------------------------------------------------------------------------------------

    // The outward normal of each face and the direction the top of its texture points (the default UVs: the top of an up face's texture is north, a down face's south)
    private static readonly (int x, int y, int z)[] FaceNormals = { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0), (0, -1, 0) };
    private static readonly (int x, int y, int z)[] FaceUps = { (0, 1, 0), (0, 1, 0), (0, 1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) };

    // A quarter turn about x as a blockstate "x": 90 does it (north goes to down, up to north), and about y clockwise seen from above (north goes to east)
    private static (int x, int y, int z) QuarterX((int x, int y, int z) v) => (v.x, v.z, -v.y);
    private static (int x, int y, int z) QuarterY((int x, int y, int z) v) => (-v.z, v.y, v.x);

    private static (int x, int y, int z) Turn((int x, int y, int z) v, int xSteps, int ySteps)
    {
        for (int i = 0; i < xSteps; i++) v = QuarterX(v);   // x first, then y: the order the game applies them in
        for (int i = 0; i < ySteps; i++) v = QuarterY(v);
        return v;
    }

    /// <summary>
    /// For a model turned by a blockstate's <paramref name="xRotation"/> and <paramref name="yRotation"/> (degrees, multiples of 90): the face of the UNTURNED model that
    /// now looks in direction <paramref name="dest"/> (0..5: +X -X +Z -Z +Y -Y), and how many degrees clockwise (seen from outside) its texture is turned with it.
    /// </summary>
    public static (int source, int uvRotation) SourceFace(int dest, int xRotation, int yRotation) => ShapeResolver.SourceFace(dest, xRotation, yRotation);

    // A layer on the floor (a rail): its up face texture turned with the model on top, the same on the sides (a strip of it), as a thin shape of one sixteenth
    private BlockAsset DescribeFlat(string name, ResolvedModel model, int xRotation, int yRotation)
    {
        var (_, turn) = SourceFace(4, xRotation, yRotation);
        int tile = -1;
        foreach (var e in model.Elements)
        {
            if (!e.Faces.TryGetValue("up", out var face)) continue;
            var texture = model.ResolveTexture(face.Texture);
            if (texture == null) continue;
            tile = _tiles.GetTexture(texture, face.Rotation + turn);
            break;
        }
        var asset = new BlockAsset { Kind = BlockKinds.Shape, Faces = Enumerable.Repeat(tile, 6).ToArray(), Thickness = 1, Slope = SlopeOf(model, yRotation) };
        asset.Render = RenderOf(name, asset.Faces);
        return asset;
    }

    // A sloping rail's model (rail_raised_ne rises toward north, rail_raised_sw toward south, before the state turns it): which way it rises as 1 east, 2 west, 3 south, 4 north; 0 for a level one
    private static int SlopeOf(ResolvedModel model, int yRotation)
    {
        (int x, int y, int z) up;
        if (model.Id.EndsWith("raised_ne", StringComparison.Ordinal)) up = (0, 0, -1);
        else if (model.Id.EndsWith("raised_sw", StringComparison.Ordinal)) up = (0, 0, 1);
        else return 0;
        up = Turn(up, 0, ((yRotation % 360) + 360) % 360 / 90);
        return up switch { (1, 0, 0) => 1, (-1, 0, 0) => 2, (0, 0, 1) => 3, _ => 4 };
    }

    // ---- states ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The states of one block that LOOK different, keyed as the server names them: <c>name[prop=value,...]</c> with only the properties that change the picture
    /// (a log's axis, a furnace's facing, a rail's shape, a crop's age), in the order the blockstate file lists them. Null when the block has no such properties,
    /// or its states are shapes the viewer cannot draw yet (stairs, fences...): those stay one asset a name.
    /// </summary>
    private (string[] Props, Dictionary<string, BlockAsset> Assets)? DescribeStates(string name)
    {
        var statePath = $"assets/minecraft/blockstates/{name}.json";
        if (Invisible.Contains(name) || !_jar.Exists(statePath)) return null;
        var variants = BlockStateFile.Parse(_jar.ReadText(statePath)).Variants;
        if (variants.Count < 2 || variants.Any(v => v.Key.Length == 0)) return null;

        var described = variants.Select(v => (Props: ParseKey(v.Key), Asset: DescribeModel(name, v.Model))).ToList();
        // Only blocks the viewer draws properly in every state: cubes, plants, flat layers
        if (described.Any(d => d.Asset.Kind == BlockKinds.Shape && d.Asset.Thickness == 0)) return null;

        // A property matters when changing only it changes the picture
        static string Sig(BlockAsset a) => $"{a.Kind}|{string.Join(",", a.Faces)}|{a.TintFaces}|{a.Render}|{a.Thickness}|{a.Slope}";
        var propNames = described[0].Props.Select(p => p.Key).ToList();
        var relevant = new List<string>();
        foreach (var prop in propNames)
        {
            bool matters = false;
            foreach (var a in described)
            {
                foreach (var b in described)
                {
                    if (!SameExcept(a.Props, b.Props, prop)) continue;
                    if (Sig(a.Asset) != Sig(b.Asset)) { matters = true; break; }
                }
                if (matters) break;
            }
            if (matters) relevant.Add(prop);
        }
        if (relevant.Count == 0) return null;

        var assets = new Dictionary<string, BlockAsset>(StringComparer.Ordinal);
        foreach (var d in described)
        {
            var key = $"minecraft:{name}[{string.Join(",", relevant.Select(r => $"{r}={d.Props.First(p => p.Key == r).Value}"))}]";
            assets.TryAdd(key, d.Asset);
        }
        return (relevant.ToArray(), assets);
    }

    private static List<KeyValuePair<string, string>> ParseKey(string key) =>
        key.Split(',').Select(kv => { int i = kv.IndexOf('='); return new KeyValuePair<string, string>(kv[..i], kv[(i + 1)..]); }).ToList();

    private static bool SameExcept(List<KeyValuePair<string, string>> a, List<KeyValuePair<string, string>> b, string except)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Key != b[i].Key) return false;
            if (a[i].Key != except && a[i].Value != b[i].Value) return false;
        }
        return true;
    }

    /// <summary>How many sixteenths of the floor a model reaches, when it is flat enough to draw as a squashed cube (at most 6); 0 otherwise.</summary>
    internal static int ThicknessOf(ResolvedModel model)
    {
        if (model.Elements.Count == 0) return 0;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var e in model.Elements)
        {
            minY = Math.Min(minY, Math.Min(e.From[1], e.To[1]));
            maxY = Math.Max(maxY, Math.Max(e.From[1], e.To[1]));
        }
        if (minY > 1.0 || maxY > 6.0) return 0;
        return Math.Clamp((int)Math.Ceiling(maxY), 1, 6);   // a plane at the floor (a rail) is still a layer of one
    }

    // opaque unless some of its texels are transparent (cutout: fully, translucent: partly); leaves are always at least cutout
    private string RenderOf(string name, int[] faces)
    {
        int min = 255;
        foreach (var t in faces.Where(f => f >= 0).Distinct())
            min = Math.Min(min, TileImages.AlphaRange(_tiles.Pixels(t)).min);
        if (min == 255) return name.EndsWith("_leaves", StringComparison.Ordinal) ? BlockRenders.Cutout : BlockRenders.Opaque;
        return min == 0 ? BlockRenders.Cutout : BlockRenders.Translucent;
    }

    private void ApplyTint(string name, BlockAsset asset, int mask)
    {
        asset.TintFaces = mask;
        switch (name)
        {
            case "birch_leaves": Fixed(asset, (128, 167, 85)); break;
            case "spruce_leaves": Fixed(asset, (97, 153, 97)); break;
            case "mangrove_leaves": Fixed(asset, (146, 199, 72)); break;
            case "lily_pad": Fixed(asset, (32, 128, 48)); break;
            case "redstone_wire": Fixed(asset, (75, 0, 0)); break;
            case var s when s.Contains("stem"): Fixed(asset, (127, 191, 63)); break;
            case var s when s.Contains("leaves") || s == "vine": Map(asset, BlockTints.Foliage, PlainsFoliage); break;
            default:
                if (!(name.Contains("grass") || name.Contains("fern") || name is "sugar_cane" or "bush" or "firefly_bush" or "wildflowers" or "pink_petals"))
                    _notes.Add($"{name}: tinted, colour map unknown, treated as grass");
                Map(asset, BlockTints.Grass, PlainsGrass);
                break;
        }
    }

    private static void Fixed(BlockAsset asset, (int r, int g, int b) c) { asset.Tint = BlockTints.Fixed; asset.TintColor = Rgb(c); }
    private static void Map(BlockAsset asset, string kind, (int r, int g, int b) c) { asset.Tint = kind; asset.TintColor = Rgb(c); }
    private static int[] Rgb((int r, int g, int b) c) => new[] { c.r, c.g, c.b };
}

/// <summary>A biome colour map (grass.png, foliage.png): temperature and downfall pick a colour.</summary>
public sealed class Colormap
{
    private readonly byte[] _rgba;
    private readonly int _w, _h;

    private Colormap(byte[] rgba, int w, int h) { _rgba = rgba; _w = w; _h = h; }

    public static Colormap? TryLoad(ClientJar jar, string path)
    {
        if (!jar.Exists(path)) return null;
        var image = ImageResult.FromMemory(jar.ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
        return new Colormap(image.Data, image.Width, image.Height);
    }

    /// <summary>The game's lookup: x from the temperature, y from temperature x downfall (both clamped to 0..1).</summary>
    public (int r, int g, int b) Sample(double temperature, double downfall)
    {
        temperature = Math.Clamp(temperature, 0, 1);
        downfall = Math.Clamp(downfall, 0, 1) * temperature;
        int x = Math.Clamp((int)((1 - temperature) * (_w - 1)), 0, _w - 1);
        int y = Math.Clamp((int)((1 - downfall) * (_h - 1)), 0, _h - 1);
        int i = (y * _w + x) * 4;
        return (_rgba[i], _rgba[i + 1], _rgba[i + 2]);
    }
}
