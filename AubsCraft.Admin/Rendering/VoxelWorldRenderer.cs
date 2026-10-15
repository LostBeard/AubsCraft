using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using AubsCraft.Admin.Services;
using AubsCraft.Assets.Model;
using SpawnDev.ILGPU.WebGPU;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Meshing;
using SpawnDev.VoxelEngine.Rendering;
// The library's far-tier mesher; AubsCraft.Admin.Rendering has an older HeightmapMesher of its own (deleted at the cut-over, PLANS V7)
using FarMesher = SpawnDev.VoxelEngine.Meshing.HeightmapMesher;

namespace AubsCraft.Admin.Rendering;

/// <summary>What one voxel frame drew.</summary>
public readonly record struct VoxelFrameStats(int Columns, int SectionsWithQuads, int VisibleSections, long TotalQuads, long VisibleQuads,
    int DrawCalls, int BufferBinds, double RecordMs, double GatherMs);

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
    /// <summary>Every column's and far chunk's quads live in a few large buffers: a pass binds one buffer for hundreds of columns.</summary>
    private QuadArena? _arena;
    // Columns whose mesh has faces toward a neighbour that has since arrived (hidden now, but still drawn): re-meshed in the background,
    // each once however many neighbours arrived meanwhile
    private readonly HashSet<Column> _stale = new();
    private bool _remeshRunning;
    private GPUQueue? _queue;
    private string _colorFormat = "bgra8unorm";
    private BlockTextureAtlas? _textures;
    // What each block looks like (generated from the game's own assets); empty until SetBlockAssets, when every block is its flat colour
    private BlockAssets _assets = new();
    private IReadOnlyList<string>? _names;
    // Animated textures play from this clock
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private GPUTexture? _depth;
    private GPUTextureView? _depthView;
    private int _depthWidth, _depthHeight;
    private int _nextSlot;
    private int _maxColumns;
    private bool _disposed;

    // Per-frame scratch, reused
    private readonly List<Column> _visible = new(512);
    private readonly List<(float dist, Column col)> _waterOrder = new(512);
    // The columns and far chunks Render looks at (see RebuildCandidates), and what they were built for
    private readonly List<Column> _candidates = new(2048);
    private bool _candidatesDirty = true;
    private int _candCX = int.MinValue, _candCZ = int.MinValue, _candRadius = -1;

    public int ColumnCount => _columns.Count;
    public int NamesKnown { get; private set; }

    /// <summary>
    /// Awaited before each mesh starts: lets the host spread the cost of loading over the frames (see <see cref="IngestGovernor"/>). Null: no
    /// waiting.
    /// </summary>
    public Func<IngestPriority, Task>? Pace { get; set; }

    /// <summary>Meshes made so far, first meshes and re-meshes together, and how many of them were re-meshes (a neighbour arrived).</summary>
    public long MeshesDone { get; private set; }
    public long RemeshesDone { get; private set; }
    /// <summary>Columns waiting for a re-mesh.</summary>
    public int StaleCount => _stale.Count;
    /// <summary>Quad buffers the arena has allocated, and the quads in use in them.</summary>
    public int ArenaBlocks => _arena?.BlockCount ?? 0;
    public long ArenaUsedQuads => _arena?.UsedQuads ?? 0;

    public void Init(GPUDevice device, GPUQueue queue, string colorFormat, Accelerator accelerator)
    {
        Device = device;
        _queue = queue;
        _colorFormat = colorFormat;
        // One slot per column or far chunk: its quads carry their section (EmbedSectionIndex), so a column is ONE draw placed by ONE
        // origin (16 bytes a slot)
        _pipe.Init(device, queue, colorFormat, maxSections: 1 << 15);
        _maxColumns = _pipe.MaxSections;
        _arena = new QuadArena(accelerator);
        _mesher = new ColumnMesher(accelerator, SectionsPerColumn) { MissingNeighborsAreSolid = false, EmbedSectionIndex = true, Arena = _arena };
        _farMesher = new FarMesher(accelerator, SectionsPerColumn)
        {
            SeabedTypeOffset = VoxelBlockTables.SeabedTypeOffset, EmbedSectionIndex = true, Arena = _arena,
        };
    }

    public GPUDevice? Device { get; private set; }

    /// <summary>The frame uniforms (camera, light, fog) the creatures share with the blocks, so both are lit and fogged alike.</summary>
    public GPUBuffer FrameBuffer => _pipe.FrameBuffer;

    /// <summary>The creatures of the live feed, drawn inside the world's render pass between the blocks and the water (null: none).</summary>
    public EntityLayer? Entities { get; set; }

    public bool HasColumn(int cx, int cz) => _columns.ContainsKey((cx, cz));
    public bool HasFarChunk(int cx, int cz) => _far.ContainsKey((cx, cz));
    public int FarChunkCount => _far.Count;

    /// <summary>Use these block names (index = the server's global id): rebuilds the class and material tables.</summary>
    public void SetBlockNames(IReadOnlyList<string> names)
    {
        _names = names;
        var (classes, materials) = VoxelBlockTables.BuildWithSeabeds(names, _assets);
        _mesher!.SetBlockClasses(classes);
        _farMesher!.SetBlockClasses(classes);
        _pipe.SetMaterials(materials);
        _blockClasses = classes;
        NamesKnown = names.Count;
    }

    private int[] _blockClasses = System.Array.Empty<int>();
    private bool _probing;
    private double _lastProbe = double.NegativeInfinity;

    /// <summary>The camera's eye is in water (or under a water surface): the world is drawn as seen from under water. Updated about ten times a second.</summary>
    public bool CameraUnderwater { get; private set; }

    // Is the eye's cell water? Two ids (the eye's cell and the one above it) are read back from the camera's column, never more: it is a question, not a download. Asked about ten
    // times a second, one question at a time; the answer arrives a frame or two later, which is as fast as a swimmer can cross a water surface.
    private void ProbeCamera(Vector3 eye)
    {
        if (_probing || _clock.Elapsed.TotalSeconds - _lastProbe < 0.1) return;
        int x = (int)MathF.Floor(eye.X), y = (int)MathF.Floor(eye.Y), z = (int)MathF.Floor(eye.Z);
        int ly = y - (int)WorldMinY;
        if (!_columns.TryGetValue((x >> 4, z >> 4), out var col) || col.Blocks == null || ly < 0 || ly >= 383) { CameraUnderwater = false; return; }
        _probing = true;
        _lastProbe = _clock.Elapsed.TotalSeconds;
        _ = ProbeAsync(col, (x & 15) + (z & 15) * 16 + ly * 256, eye.Y);
    }

    private async Task ProbeAsync(Column col, int index, float eyeY)
    {
        try
        {
            var blocks = col.Blocks;
            if (blocks == null || col.Removed) { CameraUnderwater = false; return; }
            // A 4-byte aligned pair of ids around each cell: a WebGPU buffer copy needs a 4-aligned offset and size, which one ushort at an odd index is not
            int at = index & ~1, atAbove = (index + 256) & ~1;
            var pair = new ushort[2];
            await blocks.CopyToHostAsync(pair, at, 2);
            var here = pair[index - at];
            await blocks.CopyToHostAsync(pair, atAbove, 2);
            var above = pair[index + 256 - atAbove];
            bool IsWater(ushort id) => id < _blockClasses.Length && (_blockClasses[id] & BlockClass.EmitWater) != 0;
            bool under = UnderwaterLook.IsUnder(IsWater(here), IsWater(above), eyeY);
            if (under != CameraUnderwater) Console.WriteLine($"[Voxel] camera {(under ? "under" : "out of")} water (eye cell id {here}, above {above}, eye y {eyeY:F2})");
            CameraUnderwater = under;
        }
        catch (Exception)
        {
            CameraUnderwater = false;   // the column went while we asked
        }
        finally { _probing = false; }
    }

    /// <summary>The 16x16-tile block atlas (RGBA8, square), sliced into texture-array layers on the GPU.</summary>
    public void SetBlockAssets(BlockAssets assets, Uint8Array atlasRgba)
    {
        var old = _textures;
        _textures = BlockTextureAtlas.FromRgba(Device!, _queue!, atlasRgba, assets.AtlasWidth, assets.AtlasHeight, assets.TileSize);
        _pipe.SetTextures(_textures);
        _pipe.SetAnimations(assets.Animations.Select(a => new TextureAnimation(a.Tile, a.Timeline)).ToList());
        old?.Dispose();
        _assets = assets;
        // Names that arrived first are drawn with the flat colours until now: the caller loads the assets before the names, so there is
        // nothing meshed under the old tables
        if (_names != null) SetBlockNames(_names);
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
    /// wherever no full column is loaded. The heightmap stays on the GPU; the chunk gets a MESH only while it is within reach of the
    /// camera (<see cref="UpdateFarWindow"/>). Returns false when there is no room.
    /// </summary>
    public Task<bool> AddFarChunkAsync(int cx, int cz, Uint8Array heightmap)
    {
        if (_disposed) return Task.FromResult(false);
        if (_far.ContainsKey((cx, cz))) RemoveFarChunk(cx, cz);

        int slot = TakeSlot();
        if (slot < 0) return Task.FromResult(false);

        var heights = _farMesher!.CreateHeightmapBufferFromJS(heightmap);
        var chunk = new Column { Cx = cx, Cz = cz, Slot = slot, Heights = heights };
        _far[(cx, cz)] = chunk;
        _pipe.SetSectionOrigin(chunk.Slot, new Vector3(chunk.Cx * 16, WorldMinY, chunk.Cz * 16));
        MarkNeighboursStale(chunk);
        _farWanted = true; // it may be inside the window: the next UpdateFarWindow decides
        return Task.FromResult(true);
    }

    private int TakeSlot() => _freeSlots.Count > 0 ? _freeSlots.Pop() : (_nextSlot < _maxColumns ? _nextSlot++ : -1);

    private async Task PlaceAndMeshAsync(Column col)
    {
        _pipe.SetSectionOrigin(col.Slot, new Vector3(col.Cx * 16, WorldMinY, col.Cz * 16));

        await PaceAsync(IngestPriority.Near);
        await MeshAsync(col);
        MarkNeighboursStale(col);
    }

    // A neighbour meshed before col existed drew faces toward the gap: now they are interior (hidden by col), so it should be meshed
    // again - but not now. A column in the middle of a loading spiral gets two or three neighbours in a row, and a mesh per arrival was as
    // much work as the first meshes; one background pass per column does it once.
    private void MarkNeighboursStale(Column col)
    {
        foreach (var (n, facingMe) in NeighboursOf(col).ToList())
            if (n.Mesh != null && (n.MeshedWith & facingMe) == 0)
                _stale.Add(n);
        if (_stale.Count > 0 && !_remeshRunning)
        {
            _remeshRunning = true;
            _ = RemeshStaleAsync();
        }
    }

    // ---- the far field is meshed where it can be seen ----

    private int _winCX = int.MinValue, _winCZ = int.MinValue, _winDrawDistance = -1;
    private int _freedCX = int.MinValue, _freedCZ = int.MinValue, _freedDrawDistance = -1;
    private bool _farPumpRunning, _farRescan, _farWanted;

    /// <summary>Far chunks that hold a mesh right now (the rest are heightmaps only).</summary>
    public int FarMeshedCount { get; private set; }

    /// <summary>
    /// Tell the renderer where the camera is. Far chunks within reach (<see cref="FarWindow"/>) get a mesh, nearest first and paced; the ones
    /// well beyond lose theirs and give the quad memory back. Their heightmaps stay, so flying back meshes them again without a fetch.
    /// Call every frame: it does work only when the camera's chunk or the draw distance changed, or a chunk arrived.
    /// </summary>
    public void UpdateFarWindow(int camCX, int camCZ, int drawDistanceChunks)
    {
        if (_disposed) return;
        bool moved = camCX != _winCX || camCZ != _winCZ || drawDistanceChunks != _winDrawDistance;
        _winCX = camCX; _winCZ = camCZ; _winDrawDistance = drawDistanceChunks;
        if (!moved && !_farWanted) return;
        if (_farPumpRunning) { _farRescan = true; return; }
        _farPumpRunning = true;
        _ = FarPumpAsync();
    }

    private async Task FarPumpAsync()
    {
        try
        {
            do
            {
                _farRescan = false;
                _farWanted = false;
                int cx = _winCX, cz = _winCZ, dd = _winDrawDistance;
                await FreeFarOutsideAsync(cx, cz, dd);
                foreach (var chunk in FarCandidates(cx, cz, dd))
                {
                    if (_disposed) return;
                    // The camera moved while this list was being worked: it is stale, rescan from the new position
                    if (_winCX != cx || _winCZ != cz || _winDrawDistance != dd) break;
                    if (chunk.Removed || chunk.Mesh != null) continue;
                    // Under a meshed full column the far chunk is never drawn
                    if (_columns.TryGetValue((chunk.Cx, chunk.Cz), out var over) && over.Mesh != null) continue;
                    try
                    {
                        await PaceAsync(IngestPriority.Far);
                        if (chunk.Removed || _disposed) continue;
                        await MeshAsync(chunk);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Voxel] far chunk ({chunk.Cx},{chunk.Cz}) failed to mesh: {ex.Message}");
                    }
                }
            } while (!_disposed && (_farRescan || _farWanted));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voxel] far window failed: {ex.Message}");
        }
        finally
        {
            _farPumpRunning = false;
        }
    }

    // Far chunks without a mesh that are within reach, nearest first
    private List<Column> FarCandidates(int camCX, int camCZ, int drawDistance)
    {
        long limit = FarWindow.MeshRadiusSq(drawDistance);
        var found = new List<(long d, Column chunk)>();
        foreach (var chunk in _far.Values)
        {
            if (chunk.Mesh != null) continue;
            long d = FarWindow.DistanceSq(chunk.Cx, chunk.Cz, camCX, camCZ);
            if (d <= limit) found.Add((d, chunk));
        }
        found.Sort((a, b) => a.d.CompareTo(b.d));
        return found.Select(f => f.chunk).ToList();
    }

    // Give up the meshes of far chunks well beyond the fog. Done once per camera chunk / draw distance, not per frame.
    private async Task FreeFarOutsideAsync(int camCX, int camCZ, int drawDistance)
    {
        if (camCX == _freedCX && camCZ == _freedCZ && drawDistance == _freedDrawDistance) return;
        _freedCX = camCX; _freedCZ = camCZ; _freedDrawDistance = drawDistance;
        List<Column>? beyond = null;
        foreach (var chunk in _far.Values)
            if (chunk.Mesh != null && FarWindow.ShouldFree(chunk.Cx, chunk.Cz, camCX, camCZ, drawDistance))
                (beyond ??= new()).Add(chunk);
        if (beyond == null) return;

        await _meshLock.WaitAsync(); // a mesh in flight may be about to replace one of these
        try
        {
            foreach (var chunk in beyond)
            {
                if (chunk.Mesh == null || chunk.Removed) continue;
                ForgetTotals(chunk);
                DisposeMesh(chunk.Mesh);
                chunk.Mesh = null;
                _candidatesDirty = true;
                chunk.SectionsWithQuads = 0;
                chunk.LowestSection = SectionsPerColumn;
                chunk.HighestSection = -1;
                chunk.MeshedWith = 0;
                FarMeshedCount--;
            }
        }
        finally
        {
            _meshLock.Release();
        }
    }

    private Task PaceAsync(IngestPriority priority) => Pace?.Invoke(priority) ?? Task.CompletedTask;

    // Mesh the stale columns again, last in line behind new columns. A column that has been removed, or whose neighbours now match what it
    // was meshed with (an earlier pass got there), is skipped.
    private async Task RemeshStaleAsync()
    {
        try
        {
            while (!_disposed && _stale.Count > 0)
            {
                var col = _stale.First();
                _stale.Remove(col);
                if (col.Removed || col.Mesh == null || col.MeshedWith == NeighbourMask(col)) continue;
                try
                {
                    await PaceAsync(IngestPriority.Remesh);
                    if (col.Removed || _disposed) continue;
                    await MeshAsync(col);
                    RemeshesDone++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Voxel] re-mesh of ({col.Cx},{col.Cz}) failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _remeshRunning = false;
        }
    }

    // Which neighbours (of the same kind) exist now: bit 0 -X, 1 +X, 2 -Z, 3 +Z - the form of Column.MeshedWith
    private int NeighbourMask(Column col)
    {
        var set = SetOf(col);
        return (set.ContainsKey((col.Cx - 1, col.Cz)) ? 1 : 0) | (set.ContainsKey((col.Cx + 1, col.Cz)) ? 2 : 0)
             | (set.ContainsKey((col.Cx, col.Cz - 1)) ? 4 : 0) | (set.ContainsKey((col.Cx, col.Cz + 1)) ? 8 : 0);
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
            if (col.IsFar && old == null) FarMeshedCount++;
            ForgetTotals(col); // the old mesh's share (its section count is read before it is overwritten below)
            col.Mesh = mesh;
            col.MeshedWith = mask;
            MeshesDone++;
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
            CountTotals(col);
            _candidatesDirty = true;
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
        // A pooled list sits in an arena block that lives (and keeps its cached bind group) until the renderer is disposed;
        // only a buffer of a list's own is going away
        foreach (var list in new[] { mesh.Opaque, mesh.Plants, mesh.Water })
            if (!list.IsPooled && list.Buffer?.GetGPUBuffer() is { } gpu) _pipe.ReleaseQuadBuffer(gpu);
        mesh.Dispose();
    }

    public void RemoveColumn(int cx, int cz)
    {
        if (!_columns.Remove((cx, cz), out var col)) return;
        col.Removed = true;
        _candidatesDirty = true; // it stops being drawn now, not when its buffers are freed
        _ = DisposeColumnAsync(col);
    }

    public void RemoveFarChunk(int cx, int cz)
    {
        if (!_far.Remove((cx, cz), out var chunk)) return;
        chunk.Removed = true;
        _candidatesDirty = true;
        _ = DisposeColumnAsync(chunk);
    }

    // Waits for a mesh in flight (it may be reading this column's buffers) before freeing anything.
    private async Task DisposeColumnAsync(Column col)
    {
        await _meshLock.WaitAsync();
        try
        {
            if (col.Mesh != null)
            {
                ForgetTotals(col);
                DisposeMesh(col.Mesh);
                if (col.IsFar) FarMeshedCount--;
            }
            col.Mesh = null;
            _candidatesDirty = true;
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
            Time = (float)_clock.Elapsed.TotalSeconds,
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
                    _pipe.Draw(pass, list.FirstQuad, list.Count, col.Slot);
                }
            _waterOrder.Sort((a, b) => b.dist.CompareTo(a.dist));
            foreach (var (_, col) in _waterOrder)
            {
                var water = col.Mesh!.Water;
                if (water.Buffer?.GetGPUBuffer() is not { } gpu) continue;
                _pipe.BindPass(pass, BlockRenderPipeline.Pass.Water, gpu);
                _pipe.Draw(pass, water.FirstQuad, water.Count, col.Slot);
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
        int drawCalls = 0;
        // The calls the pipeline really made (BindPass skips repeats): buffer binds and pipeline switches since this frame began
        long bindsBefore = _pipe.QuadBufferBinds + _pipe.PipelineSwitches;
        EnsureDepth(width, height);
        float aspect = (float)width / Math.Max(height, 1);
        float fovRad = camera.FovDegrees * MathF.PI / 180f;
        var frustum = FrustumCuller.ExtractPlanes(camera.GetVpMatrix(aspect)); // plane extraction wants the standard-Z matrix

        // Under water everything fades into murk a few dozen blocks out, and the frame is cleared to it
        ProbeCamera(camera.Position);
        bool underwater = CameraUnderwater;
        var fogColour = underwater ? UnderwaterLook.FogColor(light.SunStrength) : light.FogColor;
        var fogStart = underwater ? UnderwaterLook.FogStart : FarWindow.FogStartBlocks(drawDistanceChunks);
        var fogEnd = underwater ? UnderwaterLook.FogEnd : FarWindow.FogEndBlocks(drawDistanceChunks);

        _pipe.BeginFrame(new BlockFrameUniforms
        {
            ViewProjection = camera.GetViewMatrix() * ReversedZHelper.CreateInfinitePerspective(fovRad, aspect, camera.NearPlane),
            CameraPosition = new Vector4(camera.Position, 1f),
            SunDirection = new Vector4(light.SunDirection, 0f),
            SunColor = new Vector4(light.SunColor, light.SunStrength),
            Ambient = new Vector4(light.Ambient, FillLightStrength),
            FogColor = new Vector4(fogColour, 1f),
            FogRange = new Vector4(fogStart, fogEnd, 0f, 0f),
            Time = (float)_clock.Elapsed.TotalSeconds,
        });

        // Creatures are posed for this moment and uploaded before the pass; nothing past the fog is posed
        Entities?.Prepare(camera.Position, FarWindow.FogEndBlocks(drawDistanceChunks), camera.GetVpMatrix(aspect), width, height);

        int camCX = (int)MathF.Floor(camera.Position.X / 16f);
        int camCZ = (int)MathF.Floor(camera.Position.Z / 16f);
        // Never past the fog: terrain beyond it is the fog colour (the frame is cleared to it below) and only costs draws
        long drawDistSq = (long)FarWindow.CullRadius(drawDistanceChunks) * FarWindow.CullRadius(drawDistanceChunks);
        var gatherTimer = System.Diagnostics.Stopwatch.StartNew();
        _visible.Clear();
        _waterOrder.Clear();
        // Only the meshes in range are looked at each frame: the list is rebuilt when the camera changes chunk, the draw distance changes or a
        // mesh appears, goes or is replaced - not by walking every column and far chunk of the world 60 times a second (7300 of them on a
        // loaded world; on a laptop CPU that walk was 4-5 ms of the frame)
        int radius = FarWindow.CullRadius(drawDistanceChunks);
        if (_candidatesDirty || camCX != _candCX || camCZ != _candCZ || radius != _candRadius)
            RebuildCandidates(camCX, camCZ, radius);
        foreach (var col in _candidates)
            GatherVisible(col, in frustum, camCX, camCZ);
        double gatherMs = gatherTimer.Elapsed.TotalMilliseconds;

        int visibleSections = 0;
        long visibleQuads = 0;

        // Cleared to the fog colour: what lies past the fog (not drawn) must look exactly like fogged terrain, at any hour
        using var pass = _pipe.BeginRenderPass(encoder, colorView, _depthView!, fogColour);

        foreach (var kind in CubePasses)
        {
            foreach (var col in _visible)
            {
                var list = kind == BlockRenderPipeline.Pass.Opaque ? col.Mesh!.Opaque : col.Mesh!.Plants;
                if (list.Count == 0 || list.Buffer?.GetGPUBuffer() is not { } gpu) continue;
                _pipe.BindPass(pass, kind, gpu);
                // The whole column in one draw: every quad knows its section
                _pipe.Draw(pass, list.FirstQuad, list.Count, col.Slot);
                drawCalls++;
                if (kind == BlockRenderPipeline.Pass.Opaque) visibleSections += col.SectionsWithQuads;
                visibleQuads += list.Count;
            }
        }

        // The creatures: depth tested against the ground, before the water so water in front of one blends over it
        Entities?.Draw(pass);

        // Water last, farthest column first, so nearer water blends over farther water
        _waterOrder.Sort((a, b) => b.dist.CompareTo(a.dist));
        foreach (var (_, col) in _waterOrder)
        {
            var list = col.Mesh!.Water;
            if (list.Buffer?.GetGPUBuffer() is not { } gpu) continue;
            _pipe.BindPass(pass, BlockRenderPipeline.Pass.Water, gpu);
            _pipe.Draw(pass, list.FirstQuad, list.Count, col.Slot);
            drawCalls++;
            visibleQuads += list.Count;
        }
        pass.End();

        return new VoxelFrameStats(_columns.Count + _far.Count, _totalSections, visibleSections, _totalQuads, visibleQuads,
            drawCalls, (int)(_pipe.QuadBufferBinds + _pipe.PipelineSwitches - bindsBefore), recordTimer.Elapsed.TotalMilliseconds, gatherMs);
    }

    // Adds col to the frame's draw lists when it is meshed, in range and in view; counts its quads and sections either way.
    // covered: the set whose meshed entry at the same position hides col (far chunks), or null.
    // Adds a candidate to the frame's draw lists when it is in view (it is already known to be meshed, in range and not under a full column).
    private void GatherVisible(Column col, in FrustumCuller.Frustum frustum, int camCX, int camCZ)
    {
        var mesh = col.Mesh;
        if (mesh == null || col.HighestSection < 0) return; // gone since the list was built: it is rebuilt next frame
        long dx = col.Cx - camCX, dz = col.Cz - camCZ;
        // The box of the sections that hold quads, not the whole 384 blocks
        var min = new Vector3(col.Cx * 16, WorldMinY + col.LowestSection * 16, col.Cz * 16);
        var max = new Vector3(col.Cx * 16 + 16, WorldMinY + (col.HighestSection + 1) * 16, col.Cz * 16 + 16);
        if (!FrustumCuller.IsBoxVisible(in frustum, min, max)) return;
        _visible.Add(col);
        if (mesh.Water.Count > 0) _waterOrder.Add((dx * dx + dz * dz, col));
    }

    // The meshes worth looking at this frame: meshed, something to draw, within the draw radius, and a far chunk only where no full column
    // is meshed on the same spot
    private void RebuildCandidates(int camCX, int camCZ, int radius)
    {
        _candidates.Clear();
        long limit = (long)radius * radius;
        foreach (var col in _columns.Values)
            if (col.Mesh != null && col.HighestSection >= 0 && FarWindow.DistanceSq(col.Cx, col.Cz, camCX, camCZ) <= limit)
                _candidates.Add(col);
        foreach (var chunk in _far.Values)
        {
            if (chunk.Mesh == null || chunk.HighestSection < 0 || FarWindow.DistanceSq(chunk.Cx, chunk.Cz, camCX, camCZ) > limit) continue;
            if (_columns.TryGetValue((chunk.Cx, chunk.Cz), out var over) && over.Mesh != null) continue;
            _candidates.Add(chunk);
        }
        _candCX = camCX; _candCZ = camCZ; _candRadius = radius;
        _candidatesDirty = false;
    }

    // Running totals over every mesh held, for the stats: kept as meshes come and go instead of added up each frame
    private long _totalQuads;
    private int _totalSections;
    private static long QuadsOf(ColumnMesh m) => m.Opaque.Count + m.Plants.Count + m.Water.Count;
    private void CountTotals(Column col)
    {
        _totalQuads += QuadsOf(col.Mesh!);
        _totalSections += col.SectionsWithQuads;
    }
    private void ForgetTotals(Column col)
    {
        if (col.Mesh == null) return;
        _totalQuads -= QuadsOf(col.Mesh);
        _totalSections -= col.SectionsWithQuads;
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
        _stale.Clear();
        _mesher?.Dispose();
        _farMesher?.Dispose();
        // The bind groups of the arena's blocks go before the blocks do
        _pipe.InvalidateQuadBuffers();
        _arena?.Dispose();
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
