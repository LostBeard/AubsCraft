using System.Numerics;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model;
using AubsCraft.Live.Model.Entities;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The items lying about the world, as the game draws them: a flat item (a sword, an apple) a thin sprite, a block a small cube, spinning and bobbing. Each is one box of its own
/// atlas (<see cref="ItemAssets"/>: the unfolding of that box, made from the game's own item models and textures), drawn by a second <see cref="EntityRenderPipeline"/> in the
/// same pass as the creatures, lit and fogged like them. <see cref="EntityLayer"/> hands it every item entity of the live feed that is near and in view.
/// </summary>
public sealed class ItemLayer : IDisposable
{
    /// <summary>The most items drawn in a frame (the nearest ones are kept).</summary>
    public const int MaxDrawn = 400;
    /// <summary>A flat item is drawn half a block across, a block a quarter.</summary>
    public const float SpriteScale = 0.5f, CubeScale = 0.25f;
    // The creatures' light (ambient and sun on the face) is dim for a picture that the game shows at full brightness: lift it
    private static readonly Vector4 BlockBrightness = new(1.3f, 1.3f, 1.3f, 1f);
    private static readonly Vector4 Brighten = new(1.7f, 1.7f, 1.7f, 1f);

    private readonly SpawnJSRuntime _js;
    private readonly EntityRenderPipeline _pipe = new();
    private readonly List<(float distSq, EntityPartInstance part)> _wanted = new();
    private EntityPartInstance[] _parts = new EntityPartInstance[256];
    private EntityTextureAtlas? _atlas;
    private ItemAssets? _assets;
    private string _origin = "";
    private bool _disposed;

    public ItemLayer(SpawnJSRuntime js) => _js = js;

    /// <summary>Items drawn in the last frame.</summary>
    public int Count { get; private set; }
    public bool Ready => _atlas != null;

    public void Init(GPUDevice device, GPUQueue queue, string colorFormat, GPUBuffer frameUniforms, string origin)
    {
        _origin = origin;
        _pipe.Init(device, queue, colorFormat, frameUniforms);
    }

    /// <summary>Fetch the item pictures (mc-items.json and the pixels, which go from the fetched ArrayBuffer to the GPU without entering .NET).</summary>
    public async Task LoadAssetsAsync(GPUDevice device, GPUQueue queue)
    {
        using var jsonResponse = await _js.Fetch($"{_origin}/assets/mc-items.json", new FetchOptions { Credentials = "include" });
        if (!jsonResponse.Ok) { Console.WriteLine($"[Items] Item assets HTTP {jsonResponse.Status}: dropped items will not be drawn"); return; }
        var assets = System.Text.Json.JsonSerializer.Deserialize(await jsonResponse.Text(), ItemAssetsJson.Default.ItemAssets);
        if (assets == null || assets.AtlasWidth <= 0) return;
        using var pixelResponse = await _js.Fetch($"{_origin}/assets/mc-item-atlas.rgba", new FetchOptions { Credentials = "include" });
        if (!pixelResponse.Ok) { Console.WriteLine($"[Items] Item atlas HTTP {pixelResponse.Status}"); return; }
        using var buffer = await pixelResponse.ArrayBuffer();
        using var pixels = new Uint8Array(buffer);
        if (pixels.Length != assets.AtlasWidth * assets.AtlasHeight * 4) { Console.WriteLine($"[Items] Item atlas is {pixels.Length} bytes, the assets describe {assets.AtlasWidth}x{assets.AtlasHeight}"); return; }
        var atlas = EntityTextureAtlas.Create(device, queue, assets.AtlasWidth, assets.AtlasHeight);
        atlas.WriteRegion(0, 0, assets.AtlasWidth, assets.AtlasHeight, pixels);
        _pipe.SetTextures(atlas);
        _atlas = atlas;
        _assets = assets;
        Console.WriteLine($"[Items] Item assets: {assets.Items.Count} items in {assets.AtlasWidth}x{assets.AtlasHeight}");
    }

    /// <summary>Start a frame: forget the last one's items.</summary>
    public void Begin() => _wanted.Clear();

    /// <summary>One item entity as it is shown now; <paramref name="distSq"/> is its squared distance from the camera (the nearest are kept when there are too many).</summary>
    public void Add(in LiveEntityView v, double now, float distSq)
    {
        if (_assets == null || v.Variant is not { Length: > 0 } name || !_assets.Items.TryGetValue(name, out var cell)) return;
        bool cube = cell[2] == ItemAssets.KindCube;
        float scale = cube ? CubeScale : SpriteScale;
        // The game's own motion: a turn of one radian a second, a bob of a tenth of a block; each item at its own phase
        double seconds = now + v.Id * 0.37;
        float yaw = (float)(seconds * 180.0 / Math.PI);
        float bob = 0.1f + 0.1f * MathF.Sin((float)(seconds * 2.0));
        float lift = (cube ? 0.02f : 0.1f) + bob;
        var part = new EntityPartInstance
        {
            Model = EntityPoser.Root(v.X, v.Y + lift, v.Z, yaw, scale),
            OriginInflate = cube ? new Vector4(-8f, 8f, -8f, 0f) : new Vector4(-8f, 8f, -0.5f, 0f),
            SizeFlags = cube ? new Vector4(16f, 16f, 16f, 0f) : new Vector4(16f, 16f, 1f, 0f),
            Uv = new Vector4(cell[0], cell[1], 0f, 0f),
            Tint = Brighten,
        };
        _wanted.Add((distSq, part));
    }

    /// <summary>A falling block or a primed TNT: a whole block (a hair under one, as the game's 0.98 box), not spinning. <paramref name="name"/> is the block's name.</summary>
    public void AddBlock(in LiveEntityView v, string? name, float distSq)
    {
        if (_assets == null || name is not { Length: > 0 } || !_assets.Items.TryGetValue(name, out var cell) || cell[2] != ItemAssets.KindCube) return;
        var part = new EntityPartInstance
        {
            Model = EntityPoser.Root(v.X, v.Y, v.Z, 0f, 0.98f),
            OriginInflate = new Vector4(-8f, 8f, -8f, 0f),
            SizeFlags = new Vector4(16f, 16f, 16f, 0f),
            Uv = new Vector4(cell[0], cell[1], 0f, 0f),
            Tint = BlockBrightness,
        };
        _wanted.Add((distSq, part));
    }

    /// <summary>The item a thrown or shot thing is drawn as, and how big (1 is half a block across): what the game's renderer for it shows.</summary>
    public static readonly IReadOnlyDictionary<string, (string item, float size)> Thrown = new Dictionary<string, (string, float)>
    {
        ["snowball"] = ("snowball", 1f), ["egg"] = ("egg", 1f), ["ender_pearl"] = ("ender_pearl", 1f), ["eye_of_ender"] = ("ender_eye", 1f),
        ["experience_bottle"] = ("experience_bottle", 1f), ["potion"] = ("splash_potion", 1f), ["firework_rocket"] = ("firework_rocket", 1f),
        ["fireball"] = ("fire_charge", 3f), ["small_fireball"] = ("fire_charge", 0.75f), ["wind_charge"] = ("wind_charge", 1f), ["breeze_wind_charge"] = ("wind_charge", 1f),
    };

    /// <summary>A thrown or shot thing: its item as a flat picture that always turns its face to the camera at (<paramref name="cameraX"/>, <paramref name="cameraZ"/>).</summary>
    public void AddBillboard(in LiveEntityView v, string item, float size, float cameraX, float cameraZ, float distSq)
    {
        if (_assets == null || !_assets.Items.TryGetValue(item, out var cell) || cell[2] == ItemAssets.KindHanging) return;
        bool cube = cell[2] == ItemAssets.KindCube;
        // Facing the camera: the yaw of the direction from the thing to it (south 0, west 90)
        float yaw = MathF.Atan2(-(cameraX - v.X), cameraZ - v.Z) * 180f / MathF.PI;
        _wanted.Add((distSq, new EntityPartInstance
        {
            Model = EntityPoser.Root(v.X, v.Y - 0.125f, v.Z, yaw, 0.5f * size * (cube ? 0.5f : 1f)),
            OriginInflate = cube ? new Vector4(-8f, 8f, -8f, 0f) : new Vector4(-8f, 8f, -0.5f, 0f),
            SizeFlags = cube ? new Vector4(16f, 16f, 16f, 0f) : new Vector4(16f, 16f, 1f, 0f),
            Uv = new Vector4(cell[0], cell[1], 0f, 0f),
            Tint = Brighten,
        }));
    }

    /// <summary>
    /// What a creature holds in its right hand, as the game places it: in the arm's own space the item's origin is a hand's length down and a little forward, turned to lie along the arm, and then
    /// the model's own display transform for being held (tools tilted along the hand, other flat items small and upright, blocks as little cubes). <paramref name="arm"/> takes the arm's space (model
    /// pixels, origin at the shoulder) to the world.
    /// </summary>
    public void AddHeld(Matrix4x4 arm, string item, float distSq)
    {
        if (_assets == null || !_assets.Items.TryGetValue(item, out var cell) || cell[2] == ItemAssets.KindHanging) return;
        bool cube = cell[2] == ItemAssets.KindCube;
        bool tool = !cube && cell.Length > 3 && cell[3] == 1;
        // ItemTransform: scale, rotate about x then y then z the game's way, translate (in sixteenths of a block, the same as model pixels); then the hand: (1, 2, -10) in a frame turned 180 about y and -90 about x
        float scale; Vector3 rotation, translation;
        if (cube) { scale = 0.375f; rotation = new Vector3(75f, 45f, 0f); translation = new Vector3(0f, 2.5f, 0f); }
        else if (tool) { scale = 0.85f; rotation = new Vector3(0f, -90f, 55f); translation = new Vector3(0f, 4f, 0.5f); }
        else { scale = 0.55f; rotation = Vector3.Zero; translation = new Vector3(0f, 3f, 1f); }
        const float Deg = MathF.PI / 180f;
        var local = Matrix4x4.CreateScale(1f, -1f, -1f)    // the box's y points down and its front is -z: stand it up and turn its front to +z
            * Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationZ(rotation.Z * Deg) * Matrix4x4.CreateRotationY(rotation.Y * Deg) * Matrix4x4.CreateRotationX(rotation.X * Deg)
            * Matrix4x4.CreateTranslation(translation + new Vector3(1f, 2f, -10f))
            * Matrix4x4.CreateRotationY(180f * Deg) * Matrix4x4.CreateRotationX(-90f * Deg);
        _wanted.Add((distSq, new EntityPartInstance
        {
            Model = local * arm,
            OriginInflate = cube ? new Vector4(-8f, -8f, -8f, 0f) : new Vector4(-8f, -8f, -0.5f, 0f),
            SizeFlags = cube ? new Vector4(16f, 16f, 16f, 0f) : new Vector4(16f, 16f, 1f, 0f),
            Uv = new Vector4(cell[0], cell[1], 0f, 0f),
            Tint = Brighten,
        }));
    }

    /// <summary>
    /// A painting or an item frame (and what is in it): a thin box of the picture's size standing against its wall, facing the way the entity does. <paramref name="kind"/> is <c>painting</c>,
    /// <c>item_frame</c> or <c>glow_item_frame</c>; <paramref name="variant"/> the painting's name or the item in the frame (none when empty).
    /// </summary>
    public void AddHanging(in LiveEntityView v, string kind, string? variant, float distSq)
    {
        if (_assets == null) return;
        string key = kind == "painting" ? "painting/" + variant : "frame/" + kind;
        if (!_assets.Items.TryGetValue(key, out var cell) || cell[2] != ItemAssets.KindHanging || cell.Length < 5) return;
        int w = cell[3], h = cell[4];
        _wanted.Add((distSq, new EntityPartInstance
        {
            Model = EntityPoser.Root(v.X, v.Y, v.Z, v.BodyYaw, 1f),
            OriginInflate = new Vector4(-w / 2f, 24f - h / 2f, -0.5f, 0f),
            SizeFlags = new Vector4(w, h, 1f, 0f),
            Uv = new Vector4(cell[0], cell[1], 0f, 0f),
            Tint = BlockBrightness,
        }));
        // The item in a frame floats in front of its back plate, half a block across (a block as a small cube)
        if (kind != "painting" && variant is { Length: > 0 } && _assets.Items.TryGetValue(variant, out var item) && item[2] != ItemAssets.KindHanging)
        {
            bool cube = item[2] == ItemAssets.KindCube;
            float scale = cube ? 0.3f : 0.5f;
            float depth = cube ? 8f : 0.5f;
            _wanted.Add((distSq, new EntityPartInstance
            {
                Model = EntityPoser.Root(v.X, v.Y, v.Z, v.BodyYaw, scale, new Vector3(0f, 0f, cube ? -0.2f : -0.1f)),
                OriginInflate = new Vector4(-8f, 24f - 8f, -depth / 2f, 0f),
                SizeFlags = new Vector4(16f, 16f, cube ? 16f : 1f, 0f),
                Uv = new Vector4(item[0], item[1], 0f, 0f),
                Tint = Brighten,
            }));
        }
    }

    /// <summary>End the frame: upload what was added.</summary>
    public void End()
    {
        if (_wanted.Count > MaxDrawn)
        {
            _wanted.Sort((a, b) => a.distSq.CompareTo(b.distSq));
            _wanted.RemoveRange(MaxDrawn, _wanted.Count - MaxDrawn);
        }
        if (_parts.Length < _wanted.Count) _parts = new EntityPartInstance[Math.Max(_wanted.Count, _parts.Length * 2)];
        for (int i = 0; i < _wanted.Count; i++) _parts[i] = _wanted[i].part;
        Count = _wanted.Count;
        if (_atlas == null) { if (_pipe.PartCount != 0) _pipe.SetParts(ReadOnlySpan<EntityPartInstance>.Empty); return; }
        _pipe.SetParts(_parts.AsSpan(0, Count));
    }

    public void Draw(GPURenderPassEncoder pass)
    {
        if (_atlas != null && Count > 0) _pipe.Draw(pass);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe.Dispose();
        _atlas?.Dispose();
    }
}
