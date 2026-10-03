using ILGPU;
using ILGPU.Runtime;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.ILGPU;
using AubsCraft.Admin.Rendering;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Owns the ILGPU Context and Accelerator for GPU compute.
/// Dispatches the MinecraftMeshKernel for chunk mesh generation.
/// Adapted from Lost Spawns VoxelEngineService.cs.
/// </summary>
public sealed class VoxelEngineService : IAsyncDisposable
{
    private readonly SpawnJSRuntime _js;
    private Context? _context;
    private Accelerator? _accelerator;

    // Mesh kernel + shared buffers (serialized via _meshLock)
    private Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<int>, ArrayView<float>, ArrayView<int>, int, int>? _meshKernel;
    private Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<int>, ArrayView<float>, ArrayView<int>, int, int, int, int>? _lodKernel;
    private Action<Index1D, ArrayView<int>, ArrayView<float>, int>? _caveFillKernel;
    private MemoryBuffer1D<int, Stride1D.Dense>? _meshBlockBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshPaletteBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshAtlasUVBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshBlockFlagsBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshOpaqueVertBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _meshOpaqueCounterBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshWaterVertBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _meshWaterCounterBuffer;
    private readonly SemaphoreSlim _meshLock = new(1, 1);

    private readonly int[] _counterReset = [0, 0];
    private int[]? _blockIntsPool;

    // 16 * 384 * 16 = 98304 blocks per chunk
    private const int BlocksPerChunk = 16 * 384 * 16;
    // Worst case: every block has 6 faces x 54 floats = way too much.
    // Practical max: ~2M floats covers dense chunks.
    private const int MaxOutputFloats = 2_000_000;

    // Heightmap kernel + buffers (struct-based to keep binding count under WebGPU limit)
    // Old kernel had 11 ArrayView params = 12 bindings, exceeding Chrome's limit of 10.
    // New kernel uses structs: 5 ArrayView params = 6 bindings.
    private Action<Index1D, ArrayView<HeightmapColumn>, ArrayView<BlockPalette>,
        ArrayView<float>, ArrayView<float>, ArrayView<int>,
        int, int>? _heightmapKernel;
    private MemoryBuffer1D<HeightmapColumn, Stride1D.Dense>? _hmColumnsBuffer;
    private MemoryBuffer1D<BlockPalette, Stride1D.Dense>? _hmPaletteBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _hmOpaqueVertBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _hmWaterVertBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _hmCountersBuffer;
    private const int HmMaxOpaqueFloats = 256 * 50 * 6 * 11; // generous: up to 50 faces per column
    private const int HmMaxWaterFloats = 256 * 6 * 11; // 1 water face per column max

    // Reusable CPU-side arrays to avoid per-frame allocation
    private HeightmapColumn[]? _columnsPool;
    private BlockPalette[]? _palettePool;

    public Accelerator? Accelerator => _accelerator;
    public bool IsInitialized { get; private set; }
    public string? BackendName { get; private set; }

    public VoxelEngineService(SpawnJSRuntime js)
    {
        _js = js;
    }

    public async Task InitAsync()
    {
        if (IsInitialized) return;

        var builder = Context.Create();
        await builder.AllAcceleratorsAsync();
        _context = builder.ToContext();

        _accelerator = await _context.CreatePreferredAcceleratorAsync();
        BackendName = _accelerator.AcceleratorType.ToString();

        _meshKernel = _accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView<int>,   // blocks
            ArrayView<float>, // paletteColors
            ArrayView<float>, // atlasUVs
            ArrayView<float>, // blockFlags (0=solid, 1=plant, 2=water)
            ArrayView<float>, // opaqueVerts (output)
            ArrayView<int>,   // opaqueCounter
            ArrayView<float>, // waterVerts (output)
            ArrayView<int>,   // waterCounter
            int, int           // chunkWorldX, chunkWorldZ
        >(MinecraftMeshKernel.MeshKernel);

        _lodKernel = _accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView<int>,   // blocks
            ArrayView<float>, // paletteColors
            ArrayView<float>, // atlasUVs
            ArrayView<float>, // blockFlags
            ArrayView<float>, // opaqueVerts (output)
            ArrayView<int>,   // opaqueCounter
            ArrayView<float>, // waterVerts (output)
            ArrayView<int>,   // waterCounter
            int, int,          // chunkWorldX, chunkWorldZ
            int, int           // lodSize, lodGridW
        >(MinecraftLODKernel.LODKernel);

        _caveFillKernel = _accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView<int>,   // blocks (filled in place)
            ArrayView<float>, // blockFlags
            int               // fillId: opaque palette entry written into deep-underground cells
        >(CaveFillKernel.Kernel);

        _heightmapKernel = _accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView<HeightmapColumn>, // columns (256) - height, blockId, seabedHeight, seabedBlockId
            ArrayView<BlockPalette>,    // palette entries - RGB + atlas UVs + flags
            ArrayView<float>,           // opaqueVerts (output)
            ArrayView<float>,           // waterVerts (output)
            ArrayView<int>,             // counters[2] - [0]=opaque, [1]=water
            int, int                    // chunkWorldX, chunkWorldZ
        >(HeightmapMeshKernel.Kernel);

        _hmColumnsBuffer = _accelerator.Allocate1D<HeightmapColumn>(256);
        _hmOpaqueVertBuffer = _accelerator.Allocate1D<float>(HmMaxOpaqueFloats);
        _hmWaterVertBuffer = _accelerator.Allocate1D<float>(HmMaxWaterFloats);
        _hmCountersBuffer = _accelerator.Allocate1D<int>(2);

        _columnsPool = new HeightmapColumn[256];
        _palettePool = new BlockPalette[256];

        _meshBlockBuffer = _accelerator.Allocate1D<int>(BlocksPerChunk);
        _meshOpaqueVertBuffer = _accelerator.Allocate1D<float>(MaxOutputFloats);
        _meshOpaqueCounterBuffer = _accelerator.Allocate1D<int>(1);
        _meshWaterVertBuffer = _accelerator.Allocate1D<float>(MaxOutputFloats / 4); // water is less dense
        _meshWaterCounterBuffer = _accelerator.Allocate1D<int>(1);
        _blockIntsPool = new int[BlocksPerChunk];

        Console.WriteLine($"[VoxelEngineService] Initialized: {BackendName}");
        IsInitialized = true;
    }

    /// <summary>
    /// Generates mesh vertex data for a Minecraft chunk using the GPU kernel.
    /// blocks: ushort[] block IDs (0 = air), length = 98304
    /// paletteColors: float[] RGB colors, 3 per palette entry
    /// </summary>
    public async Task<MeshGenerationResult> GenerateMeshAsync(
        ushort[] blocks, float[] paletteColors, float[] atlasUVs, float[] blockFlags, int chunkX, int chunkZ)
    {
        if (_meshKernel == null)
            throw new InvalidOperationException("Not initialized");

        await _meshLock.WaitAsync();
        try
        {
            var blockInts = ToBlockInts(blocks);
            _meshBlockBuffer!.CopyFromCPU(blockInts);
            _meshOpaqueCounterBuffer!.CopyFromCPU(new int[] { 0 });
            _meshWaterCounterBuffer!.CopyFromCPU(new int[] { 0 });

            EnsureBuffer(ref _meshPaletteBuffer, paletteColors.Length);
            _meshPaletteBuffer!.CopyFromCPU(paletteColors);

            EnsureBuffer(ref _meshAtlasUVBuffer, atlasUVs.Length);
            _meshAtlasUVBuffer!.CopyFromCPU(atlasUVs);

            EnsureBuffer(ref _meshBlockFlagsBuffer, blockFlags.Length);
            _meshBlockFlagsBuffer!.CopyFromCPU(blockFlags);

            _meshKernel(
                (Index1D)BlocksPerChunk,
                _meshBlockBuffer!.View,
                _meshPaletteBuffer!.View,
                _meshAtlasUVBuffer!.View,
                _meshBlockFlagsBuffer!.View,
                _meshOpaqueVertBuffer!.View,
                _meshOpaqueCounterBuffer!.View,
                _meshWaterVertBuffer!.View,
                _meshWaterCounterBuffer!.View,
                chunkX, chunkZ);

            // Wait for kernel to finish before reading results
            await _accelerator!.SynchronizeAsync();

            // Read counters
            var opaqueCountResult = await _meshOpaqueCounterBuffer.CopyToHostAsync();
            var waterCountResult = await _meshWaterCounterBuffer.CopyToHostAsync();
            int opaqueFloats = Math.Min(opaqueCountResult[0], MaxOutputFloats);
            int waterFloats = Math.Min(waterCountResult[0], MaxOutputFloats / 4);

            // Read vertex data directly from output buffers - no intermediate copy
            float[] opaqueVerts = [];
            if (opaqueFloats > 0)
                opaqueVerts = await _meshOpaqueVertBuffer.CopyToHostAsync(0, opaqueFloats);

            float[] waterVerts = [];
            if (waterFloats > 0)
                waterVerts = await _meshWaterVertBuffer.CopyToHostAsync(0, waterFloats);

            return new MeshGenerationResult(opaqueVerts, opaqueFloats / 11, waterVerts, waterFloats / 11);
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>
    /// Generates LOD mesh from full block data at reduced detail.
    /// lodSize: 2 = 2x2x2 super-blocks (8x fewer threads), 4 = 4x4x4 (64x fewer).
    /// caveFillId >= 0: first run <see cref="CaveFillKernel"/> with that opaque palette entry, so deep
    /// underground caves (invisible from a distant camera) produce no faces.
    /// </summary>
    public async Task<MeshGenerationResult> GenerateLODMeshAsync(
        ushort[] blocks, float[] paletteColors, float[] atlasUVs, float[] blockFlags,
        int chunkX, int chunkZ, int lodSize, int caveFillId = -1)
    {
        if (_lodKernel == null)
            throw new InvalidOperationException("Not initialized");

        await _meshLock.WaitAsync();
        try
        {
            var blockInts = ToBlockInts(blocks);
            const int W = 16, H = 384;
            _meshBlockBuffer!.CopyFromCPU(blockInts);
            _meshOpaqueCounterBuffer!.CopyFromCPU(new int[] { 0 });
            _meshWaterCounterBuffer!.CopyFromCPU(new int[] { 0 });

            EnsureBuffer(ref _meshPaletteBuffer, paletteColors.Length);
            _meshPaletteBuffer!.CopyFromCPU(paletteColors);
            EnsureBuffer(ref _meshAtlasUVBuffer, atlasUVs.Length);
            _meshAtlasUVBuffer!.CopyFromCPU(atlasUVs);
            EnsureBuffer(ref _meshBlockFlagsBuffer, blockFlags.Length);
            _meshBlockFlagsBuffer!.CopyFromCPU(blockFlags);

            if (caveFillId >= 0)
                _caveFillKernel!((Index1D)(W * W), _meshBlockBuffer!.View, _meshBlockFlagsBuffer!.View, caveFillId);

            int lodGridW = W / lodSize;
            int lodGridH = H / lodSize;
            int threadCount = lodGridW * lodGridW * lodGridH;

            _lodKernel(
                (Index1D)threadCount,
                _meshBlockBuffer!.View,
                _meshPaletteBuffer!.View,
                _meshAtlasUVBuffer!.View,
                _meshBlockFlagsBuffer!.View,
                _meshOpaqueVertBuffer!.View,
                _meshOpaqueCounterBuffer!.View,
                _meshWaterVertBuffer!.View,
                _meshWaterCounterBuffer!.View,
                chunkX, chunkZ,
                lodSize, lodGridW);

            await _accelerator!.SynchronizeAsync();

            var opaqueCountResult = await _meshOpaqueCounterBuffer.CopyToHostAsync();
            var waterCountResult = await _meshWaterCounterBuffer.CopyToHostAsync();
            int opaqueFloats = Math.Min(opaqueCountResult[0], MaxOutputFloats);
            int waterFloats = Math.Min(waterCountResult[0], MaxOutputFloats / 4);

            float[] opaqueVerts = [];
            if (opaqueFloats > 0)
                opaqueVerts = await _meshOpaqueVertBuffer.CopyToHostAsync(0, opaqueFloats);
            float[] waterVerts = [];
            if (waterFloats > 0)
                waterVerts = await _meshWaterVertBuffer.CopyToHostAsync(0, waterFloats);

            return new MeshGenerationResult(opaqueVerts, opaqueFloats / 11, waterVerts, waterFloats / 11);
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>
    /// Dispatch heightmap kernel from a raw binary frame ArrayBuffer.
    /// Returns MeshGenerationResult with CPU vertex data for section splitting.
    /// Heightmap meshes are small (~5K-20K verts) so CPU readback is acceptable.
    /// </summary>
    public async Task<MeshGenerationResult> DispatchHeightmapFromFrameAsync(
        ArrayBuffer frameBuffer, int binaryDataOffset,
        BlockPalette[] palette,
        int chunkX, int chunkZ)
    {
        if (_heightmapKernel == null)
            throw new InvalidOperationException("Not initialized");

        await _meshLock.WaitAsync();
        try
        {
            // Read entire binary section as bytes (Uint8Array has no alignment requirement).
            // Layout: int32[256] heights, int16[256] blockIds, int32[256] seabedHeights, int16[256] seabedBlockIds
            // Total: 3072 bytes
            const int binarySize = 256 * 4 + 256 * 2 + 256 * 4 + 256 * 2;
            using var rawView = new Uint8Array(frameBuffer, binaryDataOffset, binarySize);
            var raw = rawView.ReadBytes();

            // Cast byte spans to typed spans - no copy, just reinterpret
            var heights = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(raw.AsSpan(0, 1024));
            var blockIds = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(raw.AsSpan(1024, 512));
            var seabedHeights = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(raw.AsSpan(1536, 1024));
            var seabedBlockIds = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(raw.AsSpan(2560, 512));

            // Pack into HeightmapColumn structs (interleave the 4 arrays)
            var columns = _columnsPool!;
            for (int i = 0; i < 256; i++)
            {
                columns[i].Height = heights[i];
                columns[i].BlockId = blockIds[i];
                columns[i].SeabedHeight = seabedHeights[i];
                columns[i].SeabedBlockId = seabedBlockIds[i];
            }

            _hmColumnsBuffer!.CopyFromCPU(columns);
            _hmCountersBuffer!.CopyFromCPU(_counterReset);

            // Palette size varies per chunk - reallocate to exact size
            if (_hmPaletteBuffer == null || _hmPaletteBuffer.Length != palette.Length)
            {
                _hmPaletteBuffer?.Dispose();
                _hmPaletteBuffer = _accelerator!.Allocate1D<BlockPalette>(palette.Length);
            }
            _hmPaletteBuffer.CopyFromCPU(palette);

            _heightmapKernel(
                (Index1D)256,
                _hmColumnsBuffer!.View,
                _hmPaletteBuffer!.View,
                _hmOpaqueVertBuffer!.View,
                _hmWaterVertBuffer!.View,
                _hmCountersBuffer!.View,
                chunkX, chunkZ);

            await _accelerator!.SynchronizeAsync();

            var counters = await _hmCountersBuffer.CopyToHostAsync();
            int opaqueFloats = Math.Min(counters[0], HmMaxOpaqueFloats);
            int waterFloats = Math.Min(counters[1], HmMaxWaterFloats);

            // Read vertex data back to CPU for section splitting
            // Heightmap meshes are small (~5K-20K verts) - readback cost is negligible
            float[] opaqueVerts = [];
            if (opaqueFloats > 0)
                opaqueVerts = await _hmOpaqueVertBuffer!.CopyToHostAsync(0, opaqueFloats);
            float[] waterVerts = [];
            if (waterFloats > 0)
                waterVerts = await _hmWaterVertBuffer!.CopyToHostAsync(0, waterFloats);

            return new MeshGenerationResult(opaqueVerts, opaqueFloats / 11, waterVerts, waterFloats / 11);
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>
    /// Widens the chunk's ushort block ids into the pooled int array the kernels read. No filtering: the
    /// kernels emit a face only toward an air/see-through neighbor (out-of-chunk X/Z neighbors count as
    /// opaque). The old pre-filter here zeroed every buried block, which turned it into AIR for the kernel,
    /// so every exposed block also emitted faces into the rock behind it (~2x the faces of the real surface).
    /// </summary>
    private int[] ToBlockInts(ushort[] blocks)
    {
        var blockInts = _blockIntsPool!;
        for (int i = 0; i < BlocksPerChunk; i++)
            blockInts[i] = blocks[i];
        return blockInts;
    }

    /// <summary>Reuse GPU buffer if exact size matches, only reallocate on size change.</summary>
    private void EnsureBuffer(ref MemoryBuffer1D<float, Stride1D.Dense>? buffer, int requiredLength)
    {
        if (buffer == null || buffer.Length != requiredLength)
        {
            buffer?.Dispose();
            buffer = _accelerator!.Allocate1D<float>(requiredLength);
        }
    }

    public ValueTask DisposeAsync()
    {
        _meshOpaqueCounterBuffer?.Dispose();
        _meshWaterCounterBuffer?.Dispose();
        _meshOpaqueVertBuffer?.Dispose();
        _meshWaterVertBuffer?.Dispose();
        _meshBlockBuffer?.Dispose();
        _meshPaletteBuffer?.Dispose();
        _meshAtlasUVBuffer?.Dispose();
        _meshBlockFlagsBuffer?.Dispose();
        _hmColumnsBuffer?.Dispose();
        _hmPaletteBuffer?.Dispose();
        _hmOpaqueVertBuffer?.Dispose();
        _hmWaterVertBuffer?.Dispose();
        _hmCountersBuffer?.Dispose();
        _accelerator?.Dispose();
        _context?.Dispose();
        _meshLock.Dispose();
        IsInitialized = false;
        return ValueTask.CompletedTask;
    }
}

public record MeshGenerationResult(
    float[] OpaqueVertices, int OpaqueVertexCount,
    float[] WaterVertices, int WaterVertexCount)
{
    /// <summary>
    /// Split flat vertex arrays into 24 section buckets by Y position.
    /// Section sy maps to Y range [sy*16-64, sy*16-64+16).
    /// Returns arrays indexed by section Y (0-23). Empty sections have null entries.
    /// </summary>
    public (float[]?[] opaqueSections, float[]?[] waterSections) SplitIntoSections()
    {
        var opaque = SplitVerticesBySectionY(OpaqueVertices, OpaqueVertexCount);
        var water = SplitVerticesBySectionY(WaterVertices, WaterVertexCount);
        return (opaque, water);
    }

    /// <summary>
    /// Buckets whole FACES (6 vertices, 2 triangles) by the section holding the face's vertical
    /// midpoint. Every kernel emits faces as 6 consecutive vertices. Bucketing single vertices tore
    /// faces apart: a side face of a block whose top lies on a section boundary put its 3 bottom
    /// vertices in one section and its 3 top vertices in the next, leaving each section a zero-area
    /// triangle, so those faces vanished in a stripe every 16 blocks.
    /// </summary>
    internal static float[]?[] SplitVerticesBySectionY(float[] vertices, int vertexCount)
    {
        const int FloatsPerVertex = 11;
        const int VerticesPerFace = 6;
        const int FloatsPerFace = FloatsPerVertex * VerticesPerFace;
        int faceCount = vertexCount / VerticesPerFace;

        // Count faces per section first (avoid list resizing)
        Span<int> counts = stackalloc int[24];
        for (int f = 0; f < faceCount; f++)
            counts[FaceSection(vertices, f * FloatsPerFace)]++;

        // Allocate per-section arrays
        var sections = new float[]?[24];
        var offsets = new int[24];
        for (int s = 0; s < 24; s++)
        {
            if (counts[s] > 0)
                sections[s] = new float[counts[s] * FloatsPerFace];
        }

        // Distribute faces into section arrays
        for (int f = 0; f < faceCount; f++)
        {
            int baseIdx = f * FloatsPerFace;
            int sy = FaceSection(vertices, baseIdx);
            vertices.AsSpan(baseIdx, FloatsPerFace).CopyTo(sections[sy]!.AsSpan(offsets[sy] * FloatsPerFace, FloatsPerFace));
            offsets[sy]++;
        }

        return sections;

        static int FaceSection(float[] v, int faceBase)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < VerticesPerFace; i++)
            {
                float y = v[faceBase + i * FloatsPerVertex + 1]; // position.y
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            return Math.Clamp((int)MathF.Floor(((minY + maxY) * 0.5f + 64f) / 16f), 0, 23);
        }
    }
};
