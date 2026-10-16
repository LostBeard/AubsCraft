using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AubsCraft.Assets.Model;
using StbImageSharp;

namespace AubsCraft.Assets;

/// <summary>The packed item pictures and what describes them.</summary>
public sealed record ItemAtlasResult(ItemAssets Assets, byte[] Rgba, string Report);

/// <summary>
/// Packs the pictures of the game's items (assets/minecraft/items/*.json name the model, the model names the textures) into one atlas of the unfoldings the entity renderer wants (see
/// <see cref="ItemAssets"/>). A model made of flat layers (generated, handheld) becomes a thin box showing the layers stacked; a model of elements (a block) becomes a cube whose sides and top
/// are what the model's faces show (a tinted face is tinted with the colour the game uses for a dropped item); a model the jar does not draw with elements (a chest, a bed, a shulker
/// box) shows its particle texture on a cube. Pictures that come out identical share one place.
/// </summary>
public sealed class ItemAtlasBuilder
{
    public const int AtlasWidth = 1024;
    private static readonly (byte r, byte g, byte b) GrassTint = (145, 189, 89), FoliageTint = (72, 181, 24), LeatherTint = (160, 101, 64), WaterTint = (63, 118, 228);

    private readonly ClientJar _jar;
    private readonly string _version;
    private readonly ModelResolver _models;
    private readonly Dictionary<string, byte[]?> _textures = new(StringComparer.Ordinal);

    public ItemAtlasBuilder(ClientJar jar, string version)
    {
        _jar = jar;
        _version = version;
        _models = new ModelResolver(jar);
    }

    private sealed class Picture
    {
        public byte[] Rgba = System.Array.Empty<byte>();
        public int Kind;
        /// <summary>A flat item held like a tool (the model's parent is item/handheld): the game turns it in the hand where it would not turn a flat item of another kind.</summary>
        public bool Handheld;
    }

    // A texture of the game as 16 x 16 pixels (an animated one is a strip of frames: the first is shown; a larger one is sampled down), null when the jar lacks it
    private byte[]? Texture(string id)
    {
        id = ModelResolver.StripNamespace(id);
        if (_textures.TryGetValue(id, out var cached)) return cached;
        byte[]? pixels = null;
        var path = $"assets/minecraft/textures/{id}.png";
        if (_jar.Exists(path))
        {
            var decoded = ImageResult.FromMemory(_jar.ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
            int size = decoded.Width;
            pixels = new byte[16 * 16 * 4];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    Buffer.BlockCopy(decoded.Data, ((y * size / 16) * size + x * size / 16) * 4, pixels, (y * 16 + x) * 4, 4);
        }
        _textures[id] = pixels;
        return pixels;
    }

    // Lay one picture over another (alpha over), with an optional tint multiplied into the top picture first
    private static void Over(byte[] under, byte[] top, (byte r, byte g, byte b)? tint)
    {
        for (int i = 0; i < 256; i++)
        {
            int o = i * 4;
            int a = top[o + 3];
            if (a == 0) continue;
            int r = top[o], g = top[o + 1], b = top[o + 2];
            if (tint is { } t) { r = r * t.r / 255; g = g * t.g / 255; b = b * t.b / 255; }
            int ua = under[o + 3];
            if (a == 255 || ua == 0) { under[o] = (byte)r; under[o + 1] = (byte)g; under[o + 2] = (byte)b; under[o + 3] = (byte)a; continue; }
            int outA = a + ua * (255 - a) / 255;
            under[o] = (byte)((r * a + under[o] * ua * (255 - a) / 255) / outA);
            under[o + 1] = (byte)((g * a + under[o + 1] * ua * (255 - a) / 255) / outA);
            under[o + 2] = (byte)((b * a + under[o + 2] * ua * (255 - a) / 255) / outA);
            under[o + 3] = (byte)outA;
        }
    }

    // The model an item definition names: the first "model" (or "base", for a special model) text in it
    private static string? ModelOf(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    if ((p.Name == "model" || p.Name == "base") && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && ModelOf(p.Value) is { } inner) return inner;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var e in element.EnumerateArray()) if (ModelOf(e) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    private static (byte r, byte g, byte b) TintFor(string item, string texture)
    {
        if (texture.Contains("leaves", StringComparison.Ordinal) || texture.Contains("vine", StringComparison.Ordinal)) return FoliageTint;
        if (item.StartsWith("leather_", StringComparison.Ordinal)) return LeatherTint;
        if (texture.Contains("water", StringComparison.Ordinal)) return WaterTint;
        return GrassTint;
    }

    private Picture? PictureOf(string item, string modelId)
    {
        var model = _models.Resolve(modelId);
        if (model == null) return null;
        bool flat = model.Elements.Count == 0 && model.Textures.ContainsKey("layer0");
        if (flat)
        {
            var sprite = new byte[16 * 16 * 4];
            for (int layer = 0; layer < 5 && model.Textures.ContainsKey("layer" + layer); layer++)
            {
                var id = model.ResolveTexture("#layer" + layer);
                var tex = id == null ? null : Texture(id);
                if (tex == null) continue;
                // The game tints a leather piece's base layer (the overlay layer is not tinted)
                Over(sprite, tex, layer == 0 && item.StartsWith("leather_", StringComparison.Ordinal) ? LeatherTint : null);
            }
            return IsBlank(sprite) ? null : new Picture { Kind = ItemAssets.KindSprite, Rgba = Unfold(sprite), Handheld = model.HasAncestor("item/handheld") };
        }

        byte[] side = new byte[16 * 16 * 4], top = new byte[16 * 16 * 4];
        if (model.Elements.Count > 0)
        {
            // What the model's faces show: the side as seen from the north, the top as seen from above, every element's face laid over the one before
            foreach (var element in model.Elements)
            {
                if (element.Faces.TryGetValue("north", out var north) && model.ResolveTexture(north.Texture) is { } nid && Texture(nid) is { } ntex)
                    Over(side, ntex, north.TintIndex >= 0 ? TintFor(item, nid) : null);
                if (element.Faces.TryGetValue("up", out var up) && model.ResolveTexture(up.Texture) is { } uid && Texture(uid) is { } utex)
                    Over(top, utex, up.TintIndex >= 0 ? TintFor(item, uid) : null);
            }
            if (IsBlank(side)) Over(side, top, null);
            if (IsBlank(top)) Over(top, side, null);
        }
        else
        {
            // A model drawn by the game's own code (a chest, a bed, a banner, a head): its particle texture on a cube is the nearest honest picture
            var id = model.Textures.TryGetValue("particle", out var particle) ? model.ResolveTexture(particle) : null;
            var tex = id == null ? null : Texture(id);
            if (tex == null) return null;
            Over(side, tex, null);
            Over(top, tex, null);
        }
        if (IsBlank(side) && IsBlank(top)) return null;
        return new Picture { Kind = ItemAssets.KindCube, Rgba = Cube(side, top) };
    }

    private static bool IsBlank(byte[] sprite)
    {
        for (int i = 3; i < sprite.Length; i += 4) if (sprite[i] != 0) return false;
        return true;
    }

    // The unfolding of a 16 x 16 x 1 box (34 x 17): top over the sprite's first row, left, front, right, back (the sprite again), and the sprite's last row in the bottom
    private static byte[] Unfold(byte[] sprite)
    {
        const int w = ItemAssets.SpriteWidth, h = ItemAssets.SpriteHeight;
        var cell = new byte[w * h * 4];
        void Put(int x, int y, int sx, int sy) => Buffer.BlockCopy(sprite, (sy * 16 + sx) * 4, cell, (y * w + x) * 4, 4);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Put(1 + x, 1 + y, x, y);       // front
                Put(18 + x, 1 + y, 15 - x, y); // back: seen from behind, so the mirror image; its opaque pixels then lie right behind the front ones
            }
        for (int x = 0; x < 16; x++) { Put(1 + x, 0, x, 0); Put(17 + x, 0, x, 15); }   // top, bottom
        for (int y = 0; y < 16; y++) { Put(0, 1 + y, 0, y); Put(17, 1 + y, 15, y); }   // left, right
        return cell;
    }

    // The unfolding of a 16 x 16 x 16 cube (64 x 32): top and bottom over left, front, right, back
    private static byte[] Cube(byte[] side, byte[] top)
    {
        const int w = ItemAssets.CubeWidth, h = ItemAssets.CubeHeight;
        var cell = new byte[w * h * 4];
        void Blit(byte[] tile, int x0, int y0)
        {
            for (int y = 0; y < 16; y++) Buffer.BlockCopy(tile, y * 16 * 4, cell, ((y0 + y) * w + x0) * 4, 16 * 4);
        }
        Blit(top, 16, 0); Blit(top, 32, 0);
        Blit(side, 0, 16); Blit(side, 16, 16); Blit(side, 32, 16); Blit(side, 48, 16);
        return cell;
    }

    // A texture of the game at its own size
    private (int w, int h, byte[] data)? Full(string id)
    {
        var path = $"assets/minecraft/textures/{id}.png";
        if (!_jar.Exists(path)) return null;
        var decoded = ImageResult.FromMemory(_jar.ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
        return (decoded.Width, decoded.Height, decoded.Data);
    }

    private sealed record Hanging(string Name, int W, int H, byte[] Cell);

    // The unfolding of a w x h x 1 box (2w + 2 by h + 1): the picture on the front, the 16 x 16 edge texture tiled over the back and the edges
    private static byte[] UnfoldFlat(byte[] front, int w, int h, byte[] edge16)
    {
        int cw = 2 * w + 2, ch = h + 1;
        var cell = new byte[cw * ch * 4];
        void Edge(int x, int y) => Buffer.BlockCopy(edge16, ((y % 16) * 16 + (x % 16)) * 4, cell, (y * cw + x) * 4, 4);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Buffer.BlockCopy(front, (y * w + x) * 4, cell, ((1 + y) * cw + 1 + x) * 4, 4);
                Edge(w + 2 + x, 1 + y);
            }
        for (int x = 0; x < w; x++) { Edge(1 + x, 0); Edge(w + 1 + x, 0); }
        for (int y = 0; y < h; y++) { Edge(0, 1 + y); Edge(w + 1, 1 + y); }
        return cell;
    }

    // The paintings, and the item frames (a ring of birch planks round the leather back, 12 x 12 pixels as the game's model has it)
    private List<Hanging> BuildHanging(StringBuilder report)
    {
        var list = new List<Hanging>();
        var back = Texture("painting/back");
        if (back == null) { report.AppendLine("  NO painting/back texture: paintings are not packed"); return list; }
        foreach (var path in _jar.List("assets/minecraft/textures/painting/").Where(p => p.EndsWith(".png", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == "back") continue;
            if (Full("painting/" + name) is not { } image) continue;
            list.Add(new Hanging("painting/" + name, image.w, image.h, UnfoldFlat(image.data, image.w, image.h, back)));
        }
        var wood = Texture("block/birch_planks");
        foreach (var frame in new[] { "item_frame", "glow_item_frame" })
        {
            var inner = Texture("block/" + frame);
            if (wood == null || inner == null) { report.AppendLine($"  NO texture for the {frame}"); continue; }
            var picture = new byte[12 * 12 * 4];
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++)
                {
                    bool ring = x == 0 || y == 0 || x == 11 || y == 11;
                    Buffer.BlockCopy(ring ? wood : inner, ((2 + y) * 16 + 2 + x) * 4, picture, (y * 12 + x) * 4, 4);
                }
            list.Add(new Hanging("frame/" + frame, 12, 12, UnfoldFlat(picture, 12, 12, wood)));
        }
        return list;
    }

    public ItemAtlasResult Build()
    {
        var report = new StringBuilder();
        var byHash = new Dictionary<string, Picture>(StringComparer.Ordinal);
        var itemPictures = new SortedDictionary<string, Picture>(StringComparer.Ordinal);
        int unresolved = 0;
        foreach (var path in _jar.List("assets/minecraft/items/").Where(p => p.EndsWith(".json", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            string? modelId;
            using (var doc = JsonDocument.Parse(_jar.ReadText(path))) modelId = ModelOf(doc.RootElement);
            var picture = modelId == null ? null : PictureOf(name, modelId);
            if (picture == null) { unresolved++; report.AppendLine($"  NO picture: {name} (model {modelId ?? "none"})"); continue; }
            var hash = Convert.ToHexString(SHA256.HashData(picture.Rgba)) + picture.Kind + (picture.Handheld ? "h" : "");
            if (!byHash.TryGetValue(hash, out var shared)) byHash[hash] = shared = picture;
            itemPictures[name] = shared;
        }

        // Flat pictures in a grid of 34 x 17 cells from the top, cubes in a grid of 64 x 32 cells below; each distinct picture once, in an order that does not depend on the machine
        var places = new Dictionary<Picture, (int x, int y)>(ReferenceEqualityComparer.Instance);
        int spriteColumns = AtlasWidth / ItemAssets.SpriteWidth, cubeColumns = AtlasWidth / ItemAssets.CubeWidth;
        var sprites = byHash.Where(p => p.Value.Kind == ItemAssets.KindSprite).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToList();
        var cubes = byHash.Where(p => p.Value.Kind == ItemAssets.KindCube).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToList();
        int spriteRows = (sprites.Count + spriteColumns - 1) / spriteColumns, cubeRows = (cubes.Count + cubeColumns - 1) / cubeColumns;
        int cubeTop = spriteRows * ItemAssets.SpriteHeight;
        int hangingTop = cubeTop + cubeRows * ItemAssets.CubeHeight;
        // Paintings and frames below, in rows of the tallest first
        var hanging = BuildHanging(report).OrderByDescending(h => h.H).ThenBy(h => h.Name, StringComparer.Ordinal).ToList();
        var hangingPlace = new Dictionary<string, (int x, int y)>();
        {
            int hx = 0, hy = hangingTop, rowHeight = 0;
            foreach (var h in hanging)
            {
                int cw = 2 * h.W + 2, ch = h.H + 1;
                if (hx + cw > AtlasWidth) { hy += rowHeight; hx = 0; rowHeight = 0; }
                hangingPlace[h.Name] = (hx, hy);
                hx += cw;
                rowHeight = Math.Max(rowHeight, ch);
            }
            hangingTop = hy + rowHeight;
        }
        int height = Math.Max(1, hangingTop);
        var rgba = new byte[AtlasWidth * height * 4];
        void Copy(Picture p, int w, int h, int x0, int y0)
        {
            for (int y = 0; y < h; y++) Buffer.BlockCopy(p.Rgba, y * w * 4, rgba, ((y0 + y) * AtlasWidth + x0) * 4, w * 4);
        }
        for (int i = 0; i < sprites.Count; i++)
        {
            int x = i % spriteColumns * ItemAssets.SpriteWidth, y = i / spriteColumns * ItemAssets.SpriteHeight;
            places[sprites[i]] = (x, y);
            Copy(sprites[i], ItemAssets.SpriteWidth, ItemAssets.SpriteHeight, x, y);
        }
        for (int i = 0; i < cubes.Count; i++)
        {
            int x = i % cubeColumns * ItemAssets.CubeWidth, y = cubeTop + i / cubeColumns * ItemAssets.CubeHeight;
            places[cubes[i]] = (x, y);
            Copy(cubes[i], ItemAssets.CubeWidth, ItemAssets.CubeHeight, x, y);
        }

        foreach (var h in hanging)
        {
            var (hx, hy) = hangingPlace[h.Name];
            int cw = 2 * h.W + 2;
            for (int y = 0; y < h.H + 1; y++) Buffer.BlockCopy(h.Cell, y * cw * 4, rgba, ((hy + y) * AtlasWidth + hx) * 4, cw * 4);
        }

        var assets = new ItemAssets { McVersion = _version, AtlasWidth = AtlasWidth, AtlasHeight = height };
        foreach (var h in hanging) assets.Items[h.Name] = new[] { hangingPlace[h.Name].x, hangingPlace[h.Name].y, ItemAssets.KindHanging, h.W, h.H };
        foreach (var (name, picture) in itemPictures)
        {
            var (x, y) = places[picture];
            assets.Items[name] = picture.Kind == ItemAssets.KindSprite ? new[] { x, y, picture.Kind, picture.Handheld ? 1 : 0 } : new[] { x, y, picture.Kind };
        }
        var head = new StringBuilder();
        head.AppendLine($"Item assets for Minecraft {_version}");
        head.AppendLine($"  {assets.Items.Count} entries ({sprites.Count} flat pictures, {cubes.Count} cubes, {hanging.Count} paintings and frames, {unresolved} items without a picture) in {AtlasWidth}x{height} px");
        return new ItemAtlasResult(assets, rgba, head.ToString() + report);
    }
}
