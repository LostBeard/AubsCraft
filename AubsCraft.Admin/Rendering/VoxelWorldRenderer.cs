using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using SpawnDev.ILGPU.WebGPU;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Meshing;
using SpawnDev.VoxelEngine.Rendering;
// The library's far-tier mesher; AubsCraft.Admin.Rendering has an older HeightmapMesher of its own (deleted at the cut-over, PLANS V7)
using FarMesher = SpawnDev.VoxelEngine.Meshing.HeightmapMesher;

namespace AubsCraft.Admin.Rendering;

/// <summary>What one voxel frame drew.</summary>
public readonly record struct VoxelFrameStats(int Columns, int SectionsWithQuads, int VisibleSections, long TotalQuads, long VisibleQuads,
    int DrawCalls, int BufferBinds, double RecordMs);

/// <summary>
/// The viewer's world, drawn by SpawnDev.VoxelEngine: block columns arrive as the server's global-id ushort arrays (straight from a
/// JS typed array to a GPU buffer), <see cref="ColumnMesher"/> turns each into quad lists on the GPU, and
/// <see cref="BlockRenderPipeline"/> draws them (opaque, plants, water) with the atlas as a texture array. The block data never
/// enters the .NET heap; only the 75 counters of a mesh come back.
///
/// Each column keeps its block buffer, because its neighbours need it for exact faces along the shared border: a column meshed
/// before a neighbour arrived has faces toward it, and is re-meshed when that neighbour shows up.
/// </summary>
public sealed class VoxelWorldRenderer : IDisposable
{
    public const int SectionsPerColumn = 24;
    /// <summary>The world's lowest block (a section's y origin is sy * 16 + this).</summary>
    public const float WorldMinY = -64f;
    private const float FillLightStrength = 0.18f;
    private static readonly Vector3 SkyColor = new(0.65f, 0.80f, 0.95f);
    private static readonly BlockRenderPipeline.Pass[] CubePasses = { BlockRenderPipeline.Pass.Opaque, BlockRenderPipeline.Pass.Cross };

    /// <summary>A full-detail block column (Blocks), or a far chunk (Heights): one chunk's surface, meshed by <see cref="FarMesher"/>.</summary>
    private sealed class Column
    {
        public required int Cx, Cz, Slot;
        public MemoryBuffer1D<ushort, Stride1D.Dense>? Blocks;
        public MemoryBuffer1D<int, Stride1D.Dense>? Heights;
        public bool IsFar => Heights != null;
        public ColumnMesh? Mesh;
        /// <summary>Sections that hold quads, and the lowest and highest of them: set when the mesh is made, for the frame's culling and stats.</summary>
        public int SectionsWithQuads, LowestSection, HighestSection;
        /// <summary>Which neighbours existed when the mesh was made: bit 0 -X, 1 +X, 2 -Z, 3 +Z.</summary>
        public int MeshedWith;
        public bool Removed;
    }

    private readonly BlockRenderPipeline _pipe = new();
    private readonly Dictionary<(int cx, int cz), Column> _columns = new();
    private readonly Dictionary<(int cx, int cz), Column> _far = new();
    private readonly Stack<int> _freeSlots = new();
    private readonly SemaphoreSlim _meshLock = new(1, 1);
    private ColumnMesher? _mesher;
    private FarMesher? _farMesher;
    private GPUQueue? _queue;
    private string _colorFormat = "bgra8unorm";
    private BlockTextureArray? _textures;
    private GPUTexture? _depth;
    private GPUTextureView? _depthView;
    private int _depthWidth, _depthHeight;
    private int _nextSlot;
    private int _maxColumns;
    private bool _disposed;

    // Per-frame scratch, reused
    private readonly List<Column> _visible = new(512);
    private readonly List<(float dist, Column col)> _waterOrder = new(512);

    public int ColumnCount => _columns.Count;
    public int NamesKnown { get; private set; }

    public void Init(GPUDevice device, GPUQueue queue, string colorFormat, Accelerator accelerator)
    {
        Device = device;
        _queue = queue;
        _colorFormat = colorFormat;
        // One slot per column or far chunk: its quads carry their section (EmbedSectionIndex), so a column is ONE draw placed by ONE
        // origin (16 bytes a slot)
        _pipe.Init(device, queue, colorFormat, maxSections: 1 << 15);
        _maxColumns = _pipe.MaxSections;
        _mesher = new ColumnMesher(accelerator, SectionsPerColumn) { MissingNeighborsAreSolid = false, EmbedSectionIndex = true };
        _farMesher = new FarMesher(accelerator, SectionsPerColumn) { SeabedTypeOffset = VoxelBlockTables.SeabedTypeOffset, EmbedSectionIndex = true };
    }

    public GPUDevice? Device { get; private set; }

    public bool HasColumn(int cx, int cz) => _columns.ContainsKey((cx, cz));
    public bool HasFarChunk(int cx, int cz) => _far.ContainsKey((cx, cz));
    public int FarChunkCount => _far.Count;

    /// <summary>Use these block names (index = the server's global id): rebuilds the class and material tables.</summary>
    public void SetBlockNames(IReadOnlyList<string> names)
    {
        var (classes, materials) = VoxelBlockTables.BuildWithSeabeds(names);
        _mesher!.SetBlockClasses(classes);
        _farMesher!.SetBlockClasses(classes);
        _pipe.SetMaterials(materials);
        NamesKnown = names.Count;
    }

    /// <summary>The 16x16-tile block atlas (RGBA8, square), sliced into texture-array layers on the GPU.</summary>
    public void SetAtlas(Uint8Array rgba, int size)
    {
        var old = _textures;
        _textures = BlockTextureArray.FromAtlas(Device!, _queue!, rgba, size, size, 16);
        _pipe.SetTextures(_textures);
        old?.Dispose();
    }

    /// <summary>
    /// Take a column (the server's global block ids as raw little-endian ushorts, 16 x 384 x 16) and mesh it. Returns false when there
    /// is no room (more columns than the pipeline has section slots): the caller evicts a far column and tries again.
    /// </summary>
    public async Task<bool> AddColumnAsync(int cx, int cz, Uint8Array blockIds)
    {
        if (_disposed) return false;
        if (_columns.ContainsKey((cx, cz))) RemoveColumn(cx, cz);

        int slot = TakeSlot();
        if (slot < 0) return false;

        var blocks = _mesher!.CreateColumnBufferFromJS(blockIds);
        var col = new Column { Cx = cx, Cz = cz, Slot = slot, Blocks = blocks };
        _columns[(cx, cz)] = col;
        await PlaceAndMeshAsync(col);
        return true;
    }

    /// <summary>
    /// Take a chunk's heightmap (the 3072 bytes of <c>HeightmapLayout</c>, global block ids) as a far-tier chunk: its surface, drawn
    /// wherever no full column is loaded. Returns false when there is no room.
    /// </summary>
    public async Task<bool> AddFarChunkAsync(int cx, int cz, Uint8Array heightmap)
    {
        if (_disposed) return false;
        if (_far.ContainsKey((cx, cz))) RemoveFarChunk(cx, cz);

        int slot = TakeSlot();
        if (slot < 0) return false;

        var heights = _farMesher!.CreateHeightmapBufferFromJS(heightmap);
        var chunk = new Column { Cx = cx, Cz = cz, Slot = slot, Heights = heights };
        _far[(cx, cz)] = chunk;
        await PlaceAndMeshAsync(chunk);
        return true;
    }

    private int TakeSlot() => _freeSlots.Count > 0 ? _freeSlots.Pop() : (_nextSlot < _maxColumns ? _nextSlot++ : -1);

    private async Task PlaceAndMeshAsync(Column col)
    {
        _pipe.SetSectionOrigin(col.Slot, new Vector3(col.Cx * 16, WorldMinY, col.Cz * 16));

        await MeshAsync(col);

        // A neighbour meshed before this one existed drew faces toward the gap: now they are interior, so mesh it again.
        foreach (var (n, facingMe) in NeighboursOf(col).ToList())
            if (n.Mesh != null && (n.MeshedWith & facingMe) == 0)
                await MeshAsync(n);
    }

    private Dictionary<(int cx, int cz), Column> SetOf(Column col) => col.IsFar ? _far : _columns;

    // The 4 neighbours (of the same kind) that exist, each with the bit that col occupies in THAT neighbour's MeshedWith.
    private IEnumerable<(Column col, int bitInNeighbour)> NeighboursOf(Column col)
    {
        var set = SetOf(col);
        if (set.TryGetValue((col.Cx - 1, col.Cz), out var a)) yield return (a, 2); // my -X neighbour has me on its +X
        if (set.TryGetValue((col.Cx + 1, col.Cz), out var b)) yield return (b, 1);
        if (set.TryGetValue((col.Cx, col.Cz - 1), out var c)) yield return (c, 8);
        if (set.TryGetValue((col.Cx, col.Cz + 1), out var d)) yield return (d, 4);
    }

    private async Task MeshAsync(Column col)
    {
        await _meshLock.WaitAsync();
        try
        {
            if (col.Removed || _disposed) return;
            var set = SetOf(col);
            set.TryGetValue((col.Cx - 1, col.Cz), out var nx);
            set.TryGetValue((col.Cx + 1, col.Cz), out var px);
            set.TryGetValue((col.Cx, col.Cz - 1), out var nz);
            set.TryGetValue((col.Cx, col.Cz + 1), out var pz);
            int mask = (nx != null ? 1 : 0) | (px != null ? 2 : 0) | (nz != null ? 4 : 0) | (pz != null ? 8 : 0);

            var mesh = col.IsFar
                ? await _farMesher!.MeshAsync(col.Heights!, nx?.Heights, px?.Heights, nz?.Heights, pz?.Heights)
                : await _mesher!.MeshAsync(col.Blocks!, nx?.Blocks, px?.Blocks, nz?.Blocks, pz?.Blocks);
            if (mesh.Overflow != 0)
                Console.WriteLine($"[Voxel] {(col.IsFar ? "far chunk" : "column")} ({col.Cx},{col.Cz}) overflowed its quad scratch by {mesh.Overflow}");
            if (col.Removed || _disposed)
            {
                mesh.Dispose();
                return;
            }
            var old = col.Mesh;
            col.Mesh = mesh;
            col.MeshedWith = mask;
            col.SectionsWithQuads = 0;
            col.LowestSection = SectionsPerColumn;
            col.HighestSection = -1;
            for (int sy = 0; sy < SectionsPerColumn; sy++)
            {
                if (mesh.Opaque.QuadsIn(sy) + mesh.Plants.QuadsIn(sy) + mesh.Water.QuadsIn(sy) == 0) continue;
                col.SectionsWithQuads++;
                col.LowestSection = Math.Min(col.LowestSection, sy);
                col.HighestSection = sy;
            }
            if (old != null) DisposeMesh(old);
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>Mesh every loaded column again (after the block tables changed).</summary>
    public async Task RemeshAllAsync()
    {
        foreach (var col in _columns.Values.ToArray())
            await MeshAsync(col);
        foreach (var chunk in _far.Values.ToArray())
            await MeshAsync(chunk);
    }

    private void DisposeMesh(ColumnMesh mesh)
    {
        foreach (var list in new[] { mesh.Opaque, mesh.Plants, mesh.Water })
            if (list.Buffer?.GetGPUBuffer() is { } gpu) _pipe.ReleaseQuadBuffer(gpu);
        mesh.Dispose();
    }

    public void RemoveColumn(int cx, int cz)
    {
        if (!_columns.Remove((cx, cz), out var col)) return;
        col.Removed = true;
        _ = DisposeColumnAsync(col);
    }

    public void RemoveFarChunk(int cx, int cz)
    {
        if (!_far.Remove((cx, cz), out var chunk)) return;
        chunk.Removed = true;
        _ = DisposeColumnAsync(chunk);
    }

    // Waits for a mesh in flight (it may be reading this column's buffers) before freeing anything.
    private async Task DisposeColumnAsync(Column col)
    {
        await _meshLock.WaitAsync();
        try
        {
            if (col.Mesh != null) DisposeMesh(col.Mesh);
            col.Mesh = null;
            col.Blocks?.Dispose();
            col.Heights?.Dispose();
            _freeSlots.Push(col.Slot);
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>The columns' (cx, cz), for the caller's eviction decisions.</summary>
    public IEnumerable<(int cx, int cz)> ColumnKeys => _columns.Keys;

    // ---- minimap: the loaded world seen from straight above, heading up, in a small texture the HUD shows ----

    /// <summary>Side of the minimap texture in pixels.</summary>
    public const int MinimapSize = 192;
    private GPUTexture? _miniTexture, _miniDepth;
    private GPUTextureView? _miniView, _miniDepthView;

    /// <summary>The minimap texture (null until the first <see cref="RenderMinimap"/>).</summary>
    public GPUTextureView? MinimapView => _miniView;

    /// <summary>
    /// Draw the world within <paramref name="extent"/> blocks of (centerX, centerZ) into the minimap texture: straight down, the camera's
    /// heading (yaw degrees) at the top. It submits its own commands first, so the frame's uniforms written afterwards (<see cref="Render"/>)
    /// are not seen by this pass: queue writes and submits are ordered.
    /// </summary>
    public void RenderMinimap(float centerX, float centerZ, float yawDegrees, float extent)
    {
        if (_disposed || Device == null || _queue == null) return;
        if (_miniTexture == null)
        {
            _miniTexture = Device.CreateTexture(new GPUTextureDescriptor
            {
                Size = new[] { MinimapSize, MinimapSize },
                Format = _colorFormat,
                Usage = GPUTextureUsage.RenderAttachment | GPUTextureUsage.TextureBinding,
            });
            _miniView = _miniTexture.CreateView();
            _miniDepth = Device.CreateTexture(new GPUTextureDescriptor
            {
                Size = new[] { MinimapSize, MinimapSize },
                Format = ReversedZHelper.DepthFormat,
                Usage = GPUTextureUsage.RenderAttachment,
            });
            _miniDepthView = _miniDepth.CreateView();
        }

        float yaw = yawDegrees * MathF.PI / 180f;
        var heading = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw)); // screen up = where the camera looks
        var eye = new Vector3(centerX, 500f, centerZ);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(centerX, 0f, centerZ), heading);
        var proj = ReversedZHelper.CreateReversedOrthographic(2f * extent, 2f * extent, 1f, 1000f);
        _pipe.BeginFrame(new BlockFrameUniforms
        {
            ViewProjection = view * proj,
            CameraPosition = new Vector4(eye, 1f),
            SunDirection = new Vector4(Vector3.Normalize(new Vector3(0.35f, 0.9f, 0.25f)), 0f),
            SunColor = new Vector4(1f, 0.96f, 0.88f, 0.45f),
            Ambient = new Vector4(0.62f, 0.64f, 0.7f, 0f),   // the same at any hour: a map should stay readable at night
            FogColor = new Vector4(SkyColor, 1f),
            FogRange = new Vector4(1e9f, 2e9f, 0f, 0f),
        });

        _visible.Clear();
        _waterOrder.Clear();
        float reach = extent + 24f; // a chunk's half-diagonal beyond the edge still shows a corner
        foreach (var col in _columns.Values) AddToMinimap(col, null, centerX, centerZ, reach);
        foreach (var chunk in _far.Values) AddToMinimap(chunk, _columns, centerX, centerZ, reach);

        using var encoder = Device.CreateCommandEncoder();
        using (var pass = _pipe.BeginRenderPass(encoder, _miniView!, _miniDepthView!, SkyColor * 0.6f))
        {
            foreach (var kind in CubePasses)
                foreach (var col in _visible)
                {
                    var list = kind == BlockRenderPipeline.Pass.Opaque ? col.Mesh!.Opaque : col.Mesh!.Plants;
                    if (list.Count == 0 || list.Buffer?.GetGPUBuffer() is not { } gpu) continue;
                    _pipe.BindPass(pass, kind, gpu);
                    _pipe.Draw(pass, 0, list.Count, col.Slot);
                }
            _waterOrder.Sort((a, b) => b.dist.CompareTo(a.dist));
            foreach (var (_, col) in _waterOrder)
            {
                if (col.Mesh!.Water.Buffer?.GetGPUBuffer() is not { } gpu) continue;
                _pipe.BindPass(pass, BlockRenderPipeline.Pass.Water, gpu);
                _pipe.Draw(pass, 0, col.Mesh.Water.Count, col.Slot);
            }
            pass.End();
        }
        using var commands = encoder.Finish();
        _queue.Submit(new[] { commands });
    }

    private void AddToMinimap(Column col, Dictionary<(int cx, int cz), Column>? covered, float centerX, float centerZ, float reach)
    {
        var mesh = col.Mesh;
        if (mesh == null || col.HighestSection < 0) return;
        if (covered != null && covered.TryGetValue((col.Cx, col.Cz), out var over) && over.Mesh != null) return;
        float dx = col.Cx * 16 + 8 - centerX, dz = col.Cz * 16 + 8 - centerZ;
        // A rotated square view reaches farthest along its diagonal
        if (MathF.Abs(dx) > reach * 1.42f || MathF.Abs(dz) > reach * 1.42f) return;
        _visible.Add(col);
        if (mesh.Water.Count > 0) _waterOrder.Add((dx * dx + dz * dz, col));
    }

    private void EnsureDepth(int width, int height)
    {
        if (_depth != null && _depthWidth == width && _depthHeight == height) return;
        _depthView?.Dispose();
        _depth?.Destroy();
        _depth?.Dispose();
        _depth = Device!.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { width, height },
            Format = ReversedZHelper.DepthFormat,
            Usage = GPUTextureUsage.RenderAttachment,
        });
        _depthView = _depth.CreateView();
        _depthWidth = width;
        _depthHeight = height;
    }

    /// <summary>
    /// Record the world into <paramref name="encoder"/>: clears <paramref name="colorView"/> to the sky, draws opaque blocks, plants,
    /// then water back to front. The caller draws any overlay after this and submits.
    /// </summary>
    public VoxelFrameStats Render(GPUCommandEncoder encoder, GPUTextureView colorView, int width, int height,
        FpsCamera camera, in DayLighting light, int drawDistanceChunks)
    {
        var recordTimer = System.Diagnostics.Stopwatch.StartNew();
        int drawCalls = 0, binds = 0;
        EnsureDepth(width, height);
        float aspect = (float)width / Math.Max(height, 1);
        float fovRad = camera.FovDegrees * MathF.PI / 180f;
        var frustum = FrustumCuller.ExtractPlanes(camera.GetVpMatrix(aspect)); // plane extraction wants the standard-Z matrix

        _pipe.BeginFrame(new BlockFrameUniforms
        {
            ViewProjection = camera.GetViewMatrix() * ReversedZHelper.CreateInfinitePerspective(fovRad, aspect, camera.NearPlane),
            CameraPosition = new Vector4(camera.Position, 1f),
            SunDirection = new Vector4(light.SunDirection, 0f),
            SunColor = new Vector4(light.SunColor, light.SunStrength),
            Ambient = new Vector4(light.Ambient, FillLightStrength),
            FogColor = new Vector4(light.FogColor, 1f),
            FogRange = new Vector4(DayLighting.FogStart, DayLighting.FogEnd, 0f, 0f),
        });

        int camCX = (int)MathF.Floor(camera.Position.X / 16f);
        int camCZ = (int)MathF.Floor(camera.Position.Z / 16f);
        long drawDistSq = (long)drawDistanceChunks * drawDistanceChunks;
        int sectionsWithQuads = 0;
        long totalQuads = 0;

        _visible.Clear();
        _waterOrder.Clear();
        foreach (var col in _columns.Values)
            GatherVisible(col, null, in frustum, camCX, camCZ, drawDistSq, ref sectionsWithQuads, ref totalQuads);
        // A far chunk gives way to the full column on the same spot, once that column is meshed
        foreach (var chunk in _far.Values)
            GatherVisible(chunk, _columns, in frustum, camCX, camCZ, drawDistSq, ref sectionsWithQuads, ref totalQuads);

        int visibleSections = 0;
        long visibleQuads = 0;

        using var pass = _pipe.BeginRenderPass(encoder, colorView, _depthView!, SkyColor);

        foreach (var kind in CubePasses)
        {
            foreach (var col in _visible)
            {
                var list = kind == BlockRenderPipeline.Pass.Opaque ? col.Mesh!.Opaque : col.Mesh!.Plants;
                if (list.Count == 0 || list.Buffer?.GetGPUBuffer() is not { } gpu) continue;
                _pipe.BindPass(pass, kind, gpu);
                binds++;
                // The whole column in one draw: every quad knows its section
                _pipe.Draw(pass, 0, list.Count, col.Slot);
                drawCalls++;
                if (kind == BlockRenderPipeline.Pass.Opaque) visibleSections += col.SectionsWithQuads;
                visibleQuads += list.Count;
            }
        }

        // Water last, farthest column first, so nearer water blends over farther water
        _waterOrder.Sort((a, b) => b.dist.CompareTo(a.dist));
        foreach (var (_, col) in _waterOrder)
        {
            var list = col.Mesh!.Water;
            if (list.Buffer?.GetGPUBuffer() is not { } gpu) continue;
            _pipe.BindPass(pass, BlockRenderPipeline.Pass.Water, gpu);
            binds++;
            _pipe.Draw(pass, 0, list.Count, col.Slot);
            drawCalls++;
            visibleQuads += list.Count;
        }
        pass.End();

        return new VoxelFrameStats(_columns.Count + _far.Count, sectionsWithQuads, visibleSections, totalQuads, visibleQuads,
            drawCalls, binds, recordTimer.Elapsed.TotalMilliseconds);
    }

    // Adds col to the frame's draw lists when it is meshed, in range and in view; counts its quads and sections either way.
    // covered: the set whose meshed entry at the same position hides col (far chunks), or null.
    private void GatherVisible(Column col, Dictionary<(int cx, int cz), Column>? covered, in FrustumCuller.Frustum frustum,
        int camCX, int camCZ, long drawDistSq, ref int sectionsWithQuads, ref long totalQuads)
    {
        var mesh = col.Mesh;
        if (mesh == null) return;
        if (covered != null && covered.TryGetValue((col.Cx, col.Cz), out var over) && over.Mesh != null) return;
        totalQuads += mesh.Opaque.Count + mesh.Plants.Count + mesh.Water.Count;
        sectionsWithQuads += col.SectionsWithQuads;
        if (col.HighestSection < 0) return; // nothing to draw
        long dx = col.Cx - camCX, dz = col.Cz - camCZ;
        if (dx * dx + dz * dz > drawDistSq) return;
        // The box of the sections that hold quads, not the whole 384 blocks
        var min = new Vector3(col.Cx * 16, WorldMinY + col.LowestSection * 16, col.Cz * 16);
        var max = new Vector3(col.Cx * 16 + 16, WorldMinY + (col.HighestSection + 1) * 16, col.Cz * 16 + 16);
        if (!FrustumCuller.IsBoxVisible(in frustum, min, max)) return;
        _visible.Add(col);
        if (mesh.Water.Count > 0) _waterOrder.Add((dx * dx + dz * dz, col));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var col in _columns.Values)
        {
            col.Removed = true;
            if (col.Mesh != null) DisposeMesh(col.Mesh);
            col.Blocks?.Dispose();
        }
        _columns.Clear();
        foreach (var chunk in _far.Values)
        {
            chunk.Removed = true;
            if (chunk.Mesh != null) DisposeMesh(chunk.Mesh);
            chunk.Heights?.Dispose();
        }
        _far.Clear();
        _mesher?.Dispose();
        _farMesher?.Dispose();
        _textures?.Dispose();
        _depthView?.Dispose();
        _depth?.Destroy();
        _depth?.Dispose();
        _miniView?.Dispose();
        _miniTexture?.Destroy();
        _miniTexture?.Dispose();
        _miniDepthView?.Dispose();
        _miniDepth?.Destroy();
        _miniDepth?.Dispose();
        _pipe.Dispose();
        _meshLock.Dispose();
    }
}
