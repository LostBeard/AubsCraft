namespace AubsCraft.Assets.Model;

// The shapes of the blocks that are not cubes (stairs, slabs, fences, doors, ladders, lanterns...): their models as boxes with a tile on each face, and the blockstate rules that say
// which models (turned how) a state of the block uses. The viewer resolves a state on demand (ShapeResolver) for the states a world really has, so the assets hold a block's few models
// and its rules, not every state it can be in (a wall has 162, a stair 40).

/// <summary>One face of a box of a shape model.</summary>
public sealed class ShapeFaceAsset
{
    /// <summary>The atlas tile, or -1 when the box has no such face.</summary>
    public int Tile { get; set; } = -1;
    /// <summary>The window of the tile in sixteenths (u0, v0, u1, v1), or null for the default (what the face's place in the model gives: see <see cref="ShapeResolver.DefaultWindow"/>).</summary>
    public int[]? Uv { get; set; }
    /// <summary>The model's own turn of the tile, degrees clockwise (0, 90, 180, 270).</summary>
    public int Rotation { get; set; }
    /// <summary>The block's tint colour multiplies this face.</summary>
    public bool Tint { get; set; }
}

/// <summary>A box of a shape model, in sixteenths of a block. <see cref="Faces"/> are in the viewer's order: +X, -X, +Z, -Z, +Y, -Y.</summary>
public sealed class ShapeElementAsset
{
    public int[] From { get; set; } = new int[3];
    public int[] To { get; set; } = new int[3];
    public ShapeFaceAsset[] Faces { get; set; } = new ShapeFaceAsset[6];
}

/// <summary>One model of a shaped block, as boxes.</summary>
public sealed class ShapeModelAsset
{
    public List<ShapeElementAsset> Elements { get; set; } = new();
}

/// <summary>A model a blockstate uses, turned about x then y (degrees, multiples of 90); with <see cref="UvLock"/> the textures keep their direction in the world instead of turning with the model.</summary>
public sealed class ShapeRef
{
    /// <summary>Index into <see cref="BlockAssets.ShapeModels"/>.</summary>
    public int Model { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool UvLock { get; set; }
}

/// <summary>One variant of a blockstate: the property values it is for ("facing=east,half=bottom") and the model it uses.</summary>
public sealed class ShapeVariant
{
    public string Key { get; set; } = "";
    public ShapeRef Model { get; set; } = new();
}

/// <summary>When a part of a multipart blockstate applies: all of <see cref="Props"/> match (a value "a|b" matches either), and every one of <see cref="And"/>, or any one of <see cref="Or"/>.</summary>
public sealed class ShapeCondition
{
    public Dictionary<string, string>? Props { get; set; }
    public List<ShapeCondition>? Or { get; set; }
    public List<ShapeCondition>? And { get; set; }
}

/// <summary>A part of a multipart blockstate (the post of a fence, one of its arms): the models it adds when its condition holds (always, when there is none).</summary>
public sealed class ShapePart
{
    public ShapeCondition? When { get; set; }
    public List<ShapeRef> Models { get; set; } = new();
}

/// <summary>The rules of one block name: its variants, or its parts, and the properties that decide (the order the server names them in).</summary>
public sealed class ShapeLogic
{
    public List<ShapeVariant> Variants { get; set; } = new();
    public List<ShapePart> Parts { get; set; } = new();
}

/// <summary>A face of a resolved box, ready for the engine: the tile, the window of it, quarter turns clockwise, tinted.</summary>
public readonly record struct ResolvedFace(int Tile, int U0, int V0, int U1, int V1, int Turns, bool Tint);

/// <summary>A box of a block state's shape (sixteenths of the cell) with its faces in the order +X, -X, +Z, -Z, +Y, -Y.</summary>
public sealed record ResolvedBox(int X0, int Y0, int Z0, int X1, int Y1, int Z1, ResolvedFace[] Faces);

/// <summary>Works out the boxes of a block state from the rules and models in <see cref="BlockAssets"/>.</summary>
public static class ShapeResolver
{
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
    public static (int source, int uvRotation) SourceFace(int dest, int xRotation, int yRotation)
    {
        int xs = ((xRotation % 360) + 360) % 360 / 90, ys = ((yRotation % 360) + 360) % 360 / 90;
        for (int s = 0; s < 6; s++)
        {
            var n = Turn(FaceNormals[s], xs, ys);
            if (n != FaceNormals[dest]) continue;
            var up = Turn(FaceUps[s], xs, ys);
            var u0 = FaceUps[dest];
            // The angle from the face's natural top to the turned top, counter-clockwise about its normal; the texture turn is clockwise, so the opposite
            int cross = (u0.y * up.z - u0.z * up.y) * n.x + (u0.z * up.x - u0.x * up.z) * n.y + (u0.x * up.y - u0.y * up.x) * n.z;
            int dot = u0.x * up.x + u0.y * up.y + u0.z * up.z;
            int degrees = (int)Math.Round(-Math.Atan2(cross, dot) * 180.0 / Math.PI);
            return (s, ((degrees % 360) + 360) % 360);
        }
        throw new InvalidOperationException("a quarter-turn model has a face looking every way");
    }

    /// <summary>
    /// The window of the tile a face shows when the model names none: the game's default from the box (sixteenths; the element's own coordinates, x/y/z from and to).
    /// u runs to the right and v down, seen from outside.
    /// </summary>
    public static int[] DefaultWindow(int face, int[] from, int[] to) => face switch
    {
        0 => new[] { 16 - to[2], 16 - to[1], 16 - from[2], 16 - from[1] },     // +X (east)
        1 => new[] { from[2], 16 - to[1], to[2], 16 - from[1] },               // -X (west)
        2 => new[] { from[0], 16 - to[1], to[0], 16 - from[1] },               // +Z (south)
        3 => new[] { 16 - to[0], 16 - to[1], 16 - from[0], 16 - from[1] },     // -Z (north)
        4 => new[] { from[0], from[2], to[0], to[2] },                         // +Y (up)
        _ => new[] { from[0], 16 - to[2], to[0], 16 - from[2] },               // -Y (down)
    };

    /// <summary>
    /// The boxes of the state of block <paramref name="name"/> with property values <paramref name="props"/> (only the properties the server names count): the models of the first
    /// matching variant, or of every multipart part whose condition holds, each turned as its blockstate says. Null when the block has no shape rules.
    /// </summary>
    public static List<ResolvedBox>? Resolve(BlockAssets assets, string name, IReadOnlyDictionary<string, string> props)
    {
        if (!assets.Shapes.TryGetValue(name, out var logic)) return null;
        var refs = SelectModels(logic, props);

        var boxes = new List<ResolvedBox>();
        foreach (var r in refs)
        {
            if (r.Model < 0 || r.Model >= assets.ShapeModels.Count) continue;
            foreach (var e in assets.ShapeModels[r.Model].Elements) boxes.Add(Turned(e, r));
        }
        return boxes;
    }

    /// <summary>The models (turned) a state with these property values uses: the first matching variant's, or every multipart part whose condition holds.</summary>
    public static List<ShapeRef> SelectModels(ShapeLogic logic, IReadOnlyDictionary<string, string> props)
    {
        var refs = new List<ShapeRef>();
        if (logic.Variants.Count > 0)
        {
            foreach (var v in logic.Variants)
                if (KeyMatches(v.Key, props)) { refs.Add(v.Model); break; }
        }
        foreach (var part in logic.Parts)
            if (part.When == null || Holds(part.When, props)) refs.AddRange(part.Models);
        return refs;
    }

    // A variant key ("facing=east,half=bottom") matches when every property it names that the state has holds the same value (a property the state lacks is not asked)
    private static bool KeyMatches(string key, IReadOnlyDictionary<string, string> props)
    {
        if (key.Length == 0) return true;
        foreach (var kv in key.Split(','))
        {
            int i = kv.IndexOf('=');
            if (i < 0) continue;
            if (props.TryGetValue(kv[..i], out var have) && have != kv[(i + 1)..]) return false;
        }
        return true;
    }

    private static bool Holds(ShapeCondition c, IReadOnlyDictionary<string, string> props)
    {
        if (c.Props != null)
            foreach (var (prop, wanted) in c.Props)
            {
                if (!props.TryGetValue(prop, out var have) || !wanted.Split('|').Contains(have)) return false;
            }
        if (c.And != null && !c.And.All(a => Holds(a, props))) return false;
        if (c.Or != null && !c.Or.Any(o => Holds(o, props))) return false;
        return true;
    }

    // An element turned by the blockstate's x then y about the cell's centre: its box, and for each face of the turned box the face of the unturned one that came to it
    private static ResolvedBox Turned(ShapeElementAsset e, ShapeRef r)
    {
        int xs = ((r.X % 360) + 360) % 360 / 90, ys = ((r.Y % 360) + 360) % 360 / 90;
        var a = Turn((e.From[0] - 8, e.From[1] - 8, e.From[2] - 8), xs, ys);
        var b = Turn((e.To[0] - 8, e.To[1] - 8, e.To[2] - 8), xs, ys);
        int[] lo = { Math.Min(a.x, b.x) + 8, Math.Min(a.y, b.y) + 8, Math.Min(a.z, b.z) + 8 };
        int[] hi = { Math.Max(a.x, b.x) + 8, Math.Max(a.y, b.y) + 8, Math.Max(a.z, b.z) + 8 };
        var faces = new ResolvedFace[6];
        for (int d = 0; d < 6; d++)
        {
            var (source, uvRotation) = SourceFace(d, r.X, r.Y);
            var face = e.Faces[source];
            if (face == null || face.Tile < 0) { faces[d] = new ResolvedFace(-1, 0, 0, 16, 16, 0, false); continue; }
            int[] window;
            int turns;
            if (r.UvLock)
            {
                // The texture keeps its direction in the world: the window comes from where the face is now, and only the model's own turn applies
                window = DefaultWindow(d, lo, hi);
                turns = face.Rotation / 90 % 4;
            }
            else
            {
                window = face.Uv ?? DefaultWindow(source, e.From, e.To);
                turns = (face.Rotation + uvRotation) / 90 % 4;
            }
            faces[d] = new ResolvedFace(face.Tile, window[0], window[1], window[2], window[3], turns, face.Tint);
        }
        return new ResolvedBox(lo[0], lo[1], lo[2], hi[0], hi[1], hi[2], faces);
    }
}
