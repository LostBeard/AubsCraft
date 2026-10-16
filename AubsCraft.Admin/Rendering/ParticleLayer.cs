using System.Numerics;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model.Particles;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The smoke and flames of the world: campfires, furnaces, fire, torches, candles. The server says which blocks of a column give off particles (the <c>X-Emitters</c> header of a column's
/// response: a few bytes); this keeps them per column, and each frame hands the ones near the camera and in view to <see cref="ParticleRenderPipeline"/>, which places every particle on the
/// GPU from the frame clock - nothing is simulated here.
/// </summary>
public sealed class ParticleLayer : IDisposable
{
    /// <summary>Most emitters drawn in a frame (the nearest ones).</summary>
    public const int MaxDrawn = 3000;

    private readonly SpawnJSRuntime _js;
    private readonly ParticleRenderPipeline _pipe = new();
    private readonly Dictionary<(int cx, int cz), EmitterSpot[]> _columns = new();
    private readonly List<(float distSq, ParticleEmitterInstance emitter)> _near = new();
    private ParticleEmitterInstance[] _upload = new ParticleEmitterInstance[256];
    private ParticleCatalog? _catalog;
    private EntityTextureAtlas? _atlas;
    private string _origin = "";
    private bool _disposed;

    public ParticleLayer(SpawnJSRuntime js) => _js = js;

    /// <summary>Emitters the last frame drew, and all the layer knows (the columns' blocks that give off particles).</summary>
    public int DrawnCount => _pipe.EmitterCount;
    public int Known { get; private set; }
    public bool Ready => _catalog != null && _atlas != null;

    public void Init(GPUDevice device, GPUQueue queue, string colorFormat, GPUBuffer frameUniforms, string origin)
    {
        _origin = origin;
        _pipe.Init(device, queue, colorFormat, frameUniforms);
    }

    /// <summary>Fetch the particle sprites (mc-particles.json and the pixels, which go from the fetched ArrayBuffer to the GPU without entering .NET) and set up the kinds.</summary>
    public async Task LoadAssetsAsync(GPUDevice device, GPUQueue queue)
    {
        using var jsonResponse = await _js.Fetch($"{_origin}/assets/mc-particles.json", new FetchOptions { Credentials = "include" });
        if (!jsonResponse.Ok)
        {
            Console.WriteLine($"[Particles] Particle assets HTTP {jsonResponse.Status}: no smoke or flames will be drawn");
            return;
        }
        var assets = System.Text.Json.JsonSerializer.Deserialize(await jsonResponse.Text(), ParticleAssetsJson.Default.ParticleAssets);
        if (assets == null || assets.AtlasHeight <= 0) return;
        using var pixelResponse = await _js.Fetch($"{_origin}/assets/mc-particle-atlas.rgba", new FetchOptions { Credentials = "include" });
        if (!pixelResponse.Ok)
        {
            Console.WriteLine($"[Particles] Particle atlas HTTP {pixelResponse.Status}");
            return;
        }
        using var buffer = await pixelResponse.ArrayBuffer();
        using var pixels = new Uint8Array(buffer);
        if (pixels.Length != assets.AtlasWidth * assets.AtlasHeight * 4)
        {
            Console.WriteLine($"[Particles] Particle atlas is {pixels.Length} bytes, the assets describe {assets.AtlasWidth}x{assets.AtlasHeight}");
            return;
        }
        var atlas = EntityTextureAtlas.Create(device, queue, assets.AtlasWidth, assets.AtlasHeight);
        atlas.WriteRegion(0, 0, assets.AtlasWidth, assets.AtlasHeight, pixels);
        var catalog = ParticleCatalog.Build(assets);
        foreach (var missing in catalog.Missing) Console.WriteLine($"[Particles] No sprite for {missing}");
        _pipe.SetTextures(atlas);
        _pipe.SetKinds(catalog.Kinds);
        _atlas = atlas;
        _catalog = catalog;
        Console.WriteLine($"[Particles] Particle assets: {assets.Sprites.Count} sprites, {catalog.Kinds.Length} kinds");
    }

    /// <summary>A column's emitters (replacing what it had). None removes it.</summary>
    public void SetColumn(int cx, int cz, IReadOnlyList<EmitterSpot> spots)
    {
        if (_columns.Remove((cx, cz), out var old)) Known -= old.Length;
        if (spots.Count == 0) return;
        var copy = new EmitterSpot[spots.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = spots[i];
        _columns[(cx, cz)] = copy;
        Known += copy.Length;
    }

    public void RemoveColumn(int cx, int cz)
    {
        if (_columns.Remove((cx, cz), out var old)) Known -= old.Length;
    }

    /// <summary>Choose this frame's emitters - the nearest ones within their kind's range and inside the view - and upload them. Call before the render pass.</summary>
    public void Prepare(Vector3 camera, Matrix4x4 viewProjStandard)
    {
        if (_catalog == null || _disposed) return;
        var frustum = FrustumCuller.ExtractPlanes(viewProjStandard);
        _near.Clear();
        foreach (var ((cx, cz), spots) in _columns)
        {
            // A column far from the camera in both axes has nothing in range: skip it whole (the longest range is 140 blocks)
            float dxc = cx * 16f + 8f - camera.X, dzc = cz * 16f + 8f - camera.Z;
            if (dxc * dxc + dzc * dzc > 160f * 160f) continue;
            foreach (var spot in spots)
            {
                float x = cx * 16 + spot.X, y = spot.Y, z = cz * 16 + spot.Z;
                float range = ParticleCatalog.RangeOf(spot.Kind);
                float dx = x + 0.5f - camera.X, dy = y + 0.5f - camera.Y, dz = z + 0.5f - camera.Z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq > range * range) continue;
                // The plume rises above the block: the box reaches up
                if (!FrustumCuller.IsBoxVisible(in frustum, new Vector3(x - 1f, y - 1f, z - 1f), new Vector3(x + 2f, y + 10f, z + 2f))) continue;
                foreach (int kind in _catalog.KindsOf(spot.Kind))
                    _near.Add((distSq, new ParticleEmitterInstance((int)x, (int)y, (int)z, kind)));
            }
        }
        if (_near.Count > MaxDrawn)
        {
            _near.Sort((a, b) => a.distSq.CompareTo(b.distSq));
            _near.RemoveRange(MaxDrawn, _near.Count - MaxDrawn);
        }
        if (_upload.Length < _near.Count) _upload = new ParticleEmitterInstance[Math.Max(_near.Count, _upload.Length * 2)];
        for (int i = 0; i < _near.Count; i++) _upload[i] = _near[i].emitter;
        _pipe.SetEmitters(_upload.AsSpan(0, _near.Count));
    }

    /// <summary>Draw this frame's emitters inside the world's render pass (after the blocks and creatures, before the water).</summary>
    public void Draw(GPURenderPassEncoder pass)
    {
        if (_catalog == null || _disposed) return;
        _pipe.Draw(pass);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe.Dispose();
        _atlas?.Dispose();
    }
}
