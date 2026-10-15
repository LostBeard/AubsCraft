using System.Numerics;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model;
using AubsCraft.Live.Model.Entities;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>A label to draw over a creature, at this screen position (pixels, the label's bottom centre).</summary>
public readonly record struct NameTag(float X, float Y, string Text, bool IsPlayer);

/// <summary>
/// The creatures of the world: everything the live feed says is out there (animals, monsters, villagers, players), drawn where it is, walking as it
/// walks, as the game's own models wearing the game's own textures. The feed is a few frames a second; <see cref="LiveWorldState"/> turns it into smooth
/// positions at the display rate, <see cref="EntityPoser"/> into model boxes, and <see cref="EntityRenderPipeline"/> draws every box in one instanced draw
/// inside the world's own render pass, lit and fogged like the ground.
///
/// Players wear their own skins: fetched through the panel (/api/live/skin), composed in a canvas (a legacy 64 x 32 skin is converted, a high-res one
/// scaled) and drawn GPU to GPU into a slot of the atlas. Until a skin is there (or if it never comes) the player has the game's default skin for their UUID.
/// </summary>
public sealed class EntityLayer : IDisposable
{
    /// <summary>The most creatures drawn in a frame (the nearest ones): a few hundred already fill a view; the rest are specks.</summary>
    public const int MaxEntitiesDrawn = 600;
    private const int ParallelSkinLoads = 3;

    private readonly SpawnJSRuntime _js;
    private readonly LiveWorldState _state = new();
    private readonly EntityRenderPipeline _pipe = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<PartDraw> _draws = new();
    private readonly List<(float distSq, LiveEntityView view)> _inRange = new();
    private readonly Dictionary<string, EntityModel> _fallbacks = new();
    private readonly Dictionary<string, Skin> _skins = new();
    private EntityPartInstance[] _parts = new EntityPartInstance[4096];
    private EntityTextureAtlas? _atlas;
    private EntityAssets? _assets;
    private GPUQueue? _queue;
    private string _origin = "";
    private string[] _slotOwner = System.Array.Empty<string>();
    private int _skinLoads;
    private bool _disposed;
    private double _lastLog = double.NegativeInfinity;
    private long _frames;

    private sealed class Skin
    {
        public int Slot = -1;
        public string Url = "";
        public bool Loaded, Loading;
        public double LastUsed, FailedAt = double.NegativeInfinity;
    }

    public EntityLayer(SpawnJSRuntime js) => _js = js;

    /// <summary>Creatures drawn in the last frame, and the boxes they were made of.</summary>
    public int EntityCount { get; private set; }
    public int PartCount => _pipe.PartCount;
    /// <summary>Creatures the feed has told us about (all worlds, drawn or not).</summary>
    public int Known => _state.TrackCount;
    public bool Ready => _atlas != null;

    /// <summary>Where the sky is by the server's own clock and weather, from the latest frame (-1 time when no frame has come).</summary>
    public LiveWorldState State => _state;

    public void Init(GPUDevice device, GPUQueue queue, string colorFormat, GPUBuffer frameUniforms, string origin)
    {
        _queue = queue;
        _origin = origin;
        _pipe.Init(device, queue, colorFormat, frameUniforms);
    }

    /// <summary>Fetch the creatures' textures (mc-entities.json and the pixels, which go from the fetched ArrayBuffer to the GPU without entering .NET).</summary>
    public async Task LoadAssetsAsync(GPUDevice device, GPUQueue queue)
    {
        using var jsonResponse = await _js.Fetch($"{_origin}/assets/mc-entities.json", new FetchOptions { Credentials = "include" });
        if (!jsonResponse.Ok)
        {
            Console.WriteLine($"[Live] Entity assets HTTP {jsonResponse.Status}: no creatures will be drawn");
            return;
        }
        var assets = System.Text.Json.JsonSerializer.Deserialize(await jsonResponse.Text(), EntityAssetsJson.Default.EntityAssets);
        if (assets == null) return;
        using var pixelResponse = await _js.Fetch($"{_origin}/assets/mc-entity-atlas.rgba", new FetchOptions { Credentials = "include" });
        if (!pixelResponse.Ok)
        {
            Console.WriteLine($"[Live] Entity atlas HTTP {pixelResponse.Status}");
            return;
        }
        using var buffer = await pixelResponse.ArrayBuffer();
        using var pixels = new Uint8Array(buffer);
        if (pixels.Length != assets.AtlasWidth * assets.StaticHeight * 4)
        {
            Console.WriteLine($"[Live] Entity atlas is {pixels.Length} bytes, the assets describe {assets.AtlasWidth}x{assets.StaticHeight}");
            return;
        }
        var atlas = EntityTextureAtlas.Create(device, queue, assets.AtlasWidth, assets.AtlasHeight);
        atlas.WriteRegion(0, 0, assets.AtlasWidth, assets.StaticHeight, pixels);
        _slotOwner = new string[assets.SkinColumns * assets.SkinRows];
        _assets = assets;
        _atlas = atlas;
        _pipe.SetTextures(atlas);
        Console.WriteLine($"[Live] Entity assets: {assets.Textures.Count} textures, {_slotOwner.Length} skin slots");
    }

    /// <summary>A frame from the feed.</summary>
    public void OnFrame(byte[] bytes)
    {
        if (_disposed) return;
        LiveFrame frame;
        try { frame = LiveFrameCodec.Parse(bytes); }
        catch (InvalidDataException ex)
        {
            Console.WriteLine("[Live] A frame was refused: " + ex.Message);
            return;
        }
        _frames++;
        _state.Apply(frame, _clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Pose the creatures near <paramref name="camera"/> for this moment and upload their boxes. Call before the render pass; <see cref="Draw"/> inside it.
    /// </summary>
    public void Prepare(Vector3 camera, float maxDistance, in Matrix4x4 viewProj, int width, int height)
    {
        EntityCount = 0;
        Tags.Clear();
        _viewProj = viewProj;
        _width = width;
        _height = height;
        var world = _state.PrimaryWorld;
        if (_atlas == null || _assets == null || world == null)
        {
            if (_pipe.PartCount != 0) _pipe.SetParts(ReadOnlySpan<EntityPartInstance>.Empty);
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        float ageTicks = (float)(now * 20.0);
        float maxSq = maxDistance * maxDistance;

        _inRange.Clear();
        foreach (var v in _state.Sample(world, now))
        {
            if ((v.Flags & LiveFlags.Invisible) != 0 || EntityModels.IsNotACreature(v.Type)) continue;
            float dx = v.X - camera.X, dy = v.Y - camera.Y, dz = v.Z - camera.Z;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 <= maxSq) _inRange.Add((d2, v));
        }
        if (_inRange.Count > MaxEntitiesDrawn)
        {
            _inRange.Sort((a, b) => a.distSq.CompareTo(b.distSq));
            _inRange.RemoveRange(MaxEntitiesDrawn, _inRange.Count - MaxEntitiesDrawn);
        }

        _draws.Clear();
        foreach (var (d2, v) in _inRange) PoseOne(world, v, ageTicks, now, d2);
        EntityCount = _inRange.Count;

        if (_parts.Length < _draws.Count) _parts = new EntityPartInstance[Math.Max(_draws.Count, _parts.Length * 2)];
        int n = 0;
        foreach (var d in _draws)
        {
            if (!TextureOrigin(d.Texture, out int tx, out int ty)) continue;   // a texture the assets lack: that box is not drawn
            _parts[n++] = new EntityPartInstance
            {
                Model = d.Model,
                OriginInflate = new Vector4(d.Origin, d.Inflate),
                SizeFlags = new Vector4(d.Size, d.Mirror ? EntityPartInstance.FlagMirror : 0f),
                Uv = new Vector4(tx + d.U, ty + d.V, 0f, 0f),
                Tint = new Vector4(d.Tint, 1f),
            };
        }
        _pipe.SetParts(_parts.AsSpan(0, n));

        // A line every few seconds while there is anything to draw (the gate reads it; so does anyone wondering why a creature is missing)
        if (now - _lastLog >= 3.0 && Known > 0 && EntityCount > 0)
        {
            _lastLog = now;
            Console.WriteLine($"[Live] creatures {EntityCount} drawn of {Known} ({n} parts, {_state.PlayerCount} players, {_frames} frames, {Tags.Count} name tags)");
        }
    }

    private void PoseOne(string world, in LiveEntityView v, float ageTicks, double now, float distSq)
    {
        EntityModel model;
        string? texture = null;
        bool fallback = false;
        if (v.IsPlayer)
        {
            var player = _state.PlayerOf(world, v.Id);
            model = EntityModels.Get(player is { Slim: true } ? "player_slim" : "player")!;
            texture = player != null ? SkinTextureFor(player, now) : EntityModels.DefaultSkinFor(v.Id.ToString(), false);
        }
        else if (EntityModels.Get(v.Type) is { } known) model = known;
        else
        {
            var key = EntityModels.Normalise(v.Type);
            if (!_fallbacks.TryGetValue(key, out model!)) _fallbacks[key] = model = EntityModels.Fallback(key);
            fallback = true;
        }

        AddTag(world, v, model, distSq);

        int first = _draws.Count;
        EntityPoser.Pose(v, model, ageTicks, _draws, texture);
        if (fallback)
        {
            // A creature with no model of its own is a box, coloured by its kind so different kinds can be told apart
            var tint = HashColour(v.Type);
            for (int i = first; i < _draws.Count; i++) { var d = _draws[i]; d.Tint = tint; _draws[i] = d; }
        }
    }

    /// <summary>Name tags for the HUD this frame: players by name, any creature that has been given a name by its custom name; screen pixels.</summary>
    public List<NameTag> Tags { get; } = new();
    private Matrix4x4 _viewProj;
    private int _width, _height;
    private const float PlayerTagRange = 96f, MobTagRange = 32f;
    private const int MaxTags = 24;

    private void AddTag(string world, in LiveEntityView v, EntityModel model, float distSq)
    {
        if (Tags.Count >= MaxTags || _width <= 0) return;
        string? text = v.IsPlayer ? _state.PlayerOf(world, v.Id)?.Name : v.Name;
        if (string.IsNullOrEmpty(text)) return;
        float range = v.IsPlayer ? PlayerTagRange : MobTagRange;
        if (distSq > range * range) return;

        float top = v.Y + model.Height * (v.Baby ? model.BabyScale : 1f) + 0.35f;
        var clip = Vector4.Transform(new Vector4(v.X, top, v.Z, 1f), _viewProj);
        if (clip.W <= 0.05f) return;   // behind the camera
        float nx = clip.X / clip.W, ny = clip.Y / clip.W;
        if (nx < -1.2f || nx > 1.2f || ny < -1.2f || ny > 1.2f) return;
        Tags.Add(new NameTag((nx * 0.5f + 0.5f) * _width, (1f - (ny * 0.5f + 0.5f)) * _height, text, v.IsPlayer));
    }

    private static Vector3 HashColour(string type)
    {
        uint h = 2166136261;
        foreach (char c in type) h = (h ^ c) * 16777619;
        // Mid-bright, not grey: hue from the hash
        float hue = (h & 0xFFFF) / 65535f;
        float r = Math.Clamp(MathF.Abs(hue * 6f - 3f) - 1f, 0f, 1f), g = Math.Clamp(2f - MathF.Abs(hue * 6f - 2f), 0f, 1f), b = Math.Clamp(2f - MathF.Abs(hue * 6f - 4f), 0f, 1f);
        return new Vector3(0.45f + 0.5f * r, 0.45f + 0.5f * g, 0.45f + 0.5f * b);
    }

    /// <summary>Draw the boxes uploaded by <see cref="Prepare"/>: inside the block pipeline's pass, after the opaque and plant passes, before the water.</summary>
    public void Draw(GPURenderPassEncoder pass)
    {
        if (_atlas != null) _pipe.Draw(pass);
    }

    // ---- textures and skins ----

    private bool TextureOrigin(string name, out int x, out int y)
    {
        if (name.StartsWith("skin:", StringComparison.Ordinal))
        {
            if (_skins.TryGetValue(name[5..], out var skin) && skin.Loaded && skin.Slot >= 0)
            {
                int cols = _assets!.SkinColumns;
                x = (skin.Slot % cols) * _assets.SkinSize;
                y = _assets.SkinY + (skin.Slot / cols) * _assets.SkinSize;
                return true;
            }
            x = y = 0;
            return false;
        }
        if (_assets!.Textures.TryGetValue(name, out var rect)) { x = rect[0]; y = rect[1]; return true; }
        x = y = 0;
        return false;
    }

    private string SkinTextureFor(LivePlayer player, double now)
    {
        var fallbackTexture = EntityModels.DefaultSkinFor(player.Uuid, player.Slim);
        if (string.IsNullOrEmpty(player.SkinUrl) || _slotOwner.Length == 0) return fallbackTexture;
        if (!_skins.TryGetValue(player.Uuid, out var skin)) _skins[player.Uuid] = skin = new Skin();
        skin.LastUsed = now;
        if (skin.Loaded && skin.Url == player.SkinUrl) return "skin:" + player.Uuid;
        // Not loaded (yet, or the player changed skin): start a load if none is under way and the last failure is not recent
        if (!skin.Loading && _skinLoads < ParallelSkinLoads && now - skin.FailedAt > 60.0)
            _ = LoadSkinAsync(player, skin, now);
        return fallbackTexture;
    }

    private int AllocateSlot(string uuid)
    {
        for (int i = 0; i < _slotOwner.Length; i++)
            if (_slotOwner[i] == null) { _slotOwner[i] = uuid; return i; }
        // Full: the skin of whoever was seen longest ago gives up its slot
        int victim = -1;
        double oldest = double.PositiveInfinity;
        for (int i = 0; i < _slotOwner.Length; i++)
        {
            if (!_skins.TryGetValue(_slotOwner[i], out var s) || s.Loading) continue;
            if (s.LastUsed < oldest) { oldest = s.LastUsed; victim = i; }
        }
        if (victim < 0) return -1;
        if (_skins.TryGetValue(_slotOwner[victim], out var evicted)) { evicted.Loaded = false; evicted.Slot = -1; }
        _slotOwner[victim] = uuid;
        return victim;
    }

    private async Task LoadSkinAsync(LivePlayer player, Skin skin, double now)
    {
        skin.Loading = true;
        _skinLoads++;
        try
        {
            using var response = await _js.Fetch($"{_origin}/api/live/skin?url={Uri.EscapeDataString(player.SkinUrl)}", new FetchOptions { Credentials = "include" });
            if (!response.Ok) throw new InvalidOperationException($"HTTP {response.Status}");
            using var blob = await response.Blob();
            using var bitmap = await _js.DedicateWorkerThis!.CreateImageBitmap(blob);
            using var canvas = ComposeSkin(bitmap);
            if (_disposed || _atlas == null) return;

            int slot = skin.Slot >= 0 ? skin.Slot : AllocateSlot(player.Uuid);
            if (slot < 0) throw new InvalidOperationException("no free skin slot");
            int cols = _assets!.SkinColumns;
            _atlas.WriteCanvas((slot % cols) * _assets.SkinSize, _assets.SkinY + (slot / cols) * _assets.SkinSize, canvas);
            skin.Slot = slot;
            skin.Url = player.SkinUrl;
            skin.Loaded = true;
        }
        catch (Exception ex)
        {
            skin.FailedAt = _clock.Elapsed.TotalSeconds;
            Console.WriteLine($"[Live] Skin of {player.Name} did not load ({ex.Message}); using the default skin");
        }
        finally
        {
            skin.Loading = false;
            _skinLoads--;
        }
    }

    // The legacy 64 x 32 layout has one set of limbs: the game copies the right leg and arm, mirrored, to where a modern skin keeps the left ones
    private static readonly (int x, int y, int dx, int dy, int w, int h)[] LegacyLimbCopies =
    {
        (4, 16, 16, 32, 4, 4), (8, 16, 24, 32, 4, 4), (0, 20, 24, 48, 4, 12), (4, 20, 16, 48, 4, 12), (8, 20, 8, 48, 4, 12), (12, 20, 16, 48, 4, 12),
        (44, 16, -8, 32, 4, 4), (48, 16, -8, 32, 4, 4), (40, 20, 0, 32, 4, 12), (44, 20, -8, 32, 4, 12), (48, 20, -16, 32, 4, 12), (52, 20, -8, 32, 4, 12),
    };

    /// <summary>A 64 x 64 canvas holding the skin in the modern layout: a square one scaled to size, a legacy 2:1 one converted.</summary>
    private static OffscreenCanvas ComposeSkin(ImageBitmap bitmap)
    {
        int w = (int)bitmap.Width, h = (int)bitmap.Height;
        var canvas = new OffscreenCanvas(64, 64);
        try
        {
            using var ctx = canvas.Get2DContext();
            ctx.ImageSmoothingEnabled = false;
            if (w == h && w >= 64) ctx.DrawImage(bitmap, 0, 0, w, h, 0, 0, 64, 64);
            else if (w == h * 2 && w >= 64)
            {
                double s = w / 64.0;
                ctx.DrawImage(bitmap, 0, 0, w, h, 0, 0, 64, 32);
                foreach (var (x, y, dx, dy, cw, ch) in LegacyLimbCopies)
                {
                    ctx.Save();
                    ctx.Translate(x + dx + cw, y + dy);
                    ctx.Scale(-1, 1);
                    ctx.DrawImage(bitmap, x * s, y * s, cw * s, ch * s, 0, 0, cw, ch);
                    ctx.Restore();
                }
            }
            else throw new InvalidOperationException($"a {w}x{h} skin is not a skin");
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe.Dispose();
        _atlas?.Dispose();
    }
}
