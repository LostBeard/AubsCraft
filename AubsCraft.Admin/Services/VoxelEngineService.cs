using ILGPU;
using ILGPU.Runtime;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
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
    private readonly BlazorJSRuntime _js;
    private Context? _context;
    private Accelerator? _accelerator;

    // Mesh kernel + shared buffers (serialized via _meshLock)
    private Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<int>, ArrayView<float>, ArrayView<int>, int, int>? _meshKernel;
    private MemoryBuffer1D<int, Stride1D.Dense>? _meshBlockBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshPaletteBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshAtlasUVBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshBlockFlagsBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshOpaqueVertBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _meshOpaqueCounterBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshWaterVertBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _meshWaterCounterBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshResultBuffer;
    private readonly SemaphoreSlim _meshLock = new(1, 1);

    private readonly int[] _counterReset = [0];
    private int[]? _blockIntsPool;

    // 16 * 384 * 16 = 98304 blocks per chunk
    private const int BlocksPerChunk = 16 * 384 * 16;
    // Worst case: every block has 6 faces x 54 floats = way too much.
    // Practical max: ~2M floats covers dense chunks.
    private const int MaxOutputFloats = 2_000_000;

    // Heightmap kernel + buffers
    private Action<Index1D, ArrayView<int>, ArrayView<short>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<int>, ArrayView<short>, ArrayView<float>, ArrayView<int>,
        ArrayView<float>, ArrayView<int>, int, int>? _heightmapKernel;
    private MemoryBuffer1D<int, Stride1D.Dense>? _hmHeightsBuffer;
    private MemoryBuffer1D<short, Stride1D.Dense>? _hmBlockIdsBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _hmSeabedHeightsBuffer;
    private MemoryBuffer1D<short, Stride1D.Dense>? _hmSeabedBlockIdsBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _hmOpaqueVertBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _hmOpaqueCounterBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _hmWaterVertBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _hmWaterCounterBuffer;
    private const int HmMaxOpaqueFloats = 256 * 50 * 6 * 11; // generous: up to 50 faces per column
    private const int HmMaxWaterFloats = 256 * 6 * 11; // 1 water face per column max

    public Accelerator? Accelerator => _accelerator;
    public bool IsInitialized { get; private set; }
    public string? BackendName { get; private set; }

    public VoxelEngineService(BlazorJSRuntime js)
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

        _heightmapKernel = _accelerator.LoadAutoGroupedStreamKernel<
            Index1D,
            ArrayView<int>,   // heights (256)
            ArrayView<short>, // blockIds (256) - ushort from wire, kernel casts to int
            ArrayView<float>, // paletteColors
            ArrayView<float>, // atlasUVs (4 per entry: u0,v0,u1,v1)
            ArrayView<float>, // blockFlags
            ArrayView<int>,   // seabedHeights (256)
            ArrayView<short>, // seabedBlockIds (256) - ushort from wire
            ArrayView<float>, // opaqueVerts (output)
            ArrayView<int>,   // opaqueCounter
            ArrayView<float>, // waterVerts (output)
            ArrayView<int>,   // waterCounter
            int, int           // chunkWorldX, chunkWorldZ
        >(HeightmapMeshKernel.Kernel);

        _hmHeightsBuffer = _accelerator.Allocate1D<int>(256);
        _hmBlockIdsBuffer = _accelerator.Allocate1D<short>(256);
        _hmSeabedHeightsBuffer = _accelerator.Allocate1D<int>(256);
        _hmSeabedBlockIdsBuffer = _accelerator.Allocate1D<short>(256);
        _hmOpaqueVertBuffer = _accelerator.Allocate1D<float>(HmMaxOpaqueFloats);
        _hmOpaqueCounterBuffer = _accelerator.Allocate1D<int>(1);
        _hmWaterVertBuffer = _accelerator.Allocate1D<float>(HmMaxWaterFloats);
        _hmWaterCounterBuffer = _accelerator.Allocate1D<int>(1);

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
            var blockInts = _blockIntsPool!;
            for (int i = 0; i < blocks.Length && i < BlocksPerChunk; i++)
                blockInts[i] = blocks[i];

            _meshBlockBuffer!.CopyFromCPU(blockInts);
            _meshOpaqueCounterBuffer!.CopyFromCPU(_counterReset);
            _meshWaterCounterBuffer!.CopyFromCPU(_counterReset);

            _meshPaletteBuffer?.Dispose();
            _meshPaletteBuffer = _accelerator!.Allocate1D<float>(paletteColors.Length);
            _meshPaletteBuffer.CopyFromCPU(paletteColors);

            _meshAtlasUVBuffer?.Dispose();
            _meshAtlasUVBuffer = _accelerator!.Allocate1D<float>(atlasUVs.Length);
            _meshAtlasUVBuffer.CopyFromCPU(atlasUVs);

            _meshBlockFlagsBuffer?.Dispose();
            _meshBlockFlagsBuffer = _accelerator!.Allocate1D<float>(blockFlags.Length);
            _meshBlockFlagsBuffer.CopyFromCPU(blockFlags);

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
    /// Generates heightmap mesh using the GPU kernel. Replaces CPU HeightmapMesher.
    /// 256 threads process a 16x16 column grid in parallel.
    /// </summary>
    public async Task<MeshGenerationResult> GenerateHeightmapMeshAsync(
        int[] heights, short[] blockIds, float[] paletteColors, float[] atlasUVs, float[] blockFlags,
        int[] seabedHeights, short[] seabedBlockIds, int chunkX, int chunkZ)
    {
        if (_heightmapKernel == null)
            throw new InvalidOperationException("Not initialized");

        await _meshLock.WaitAsync();
        try
        {
            // CopyFromJS - browser only, no fallback
            using var jsH = new Int32Array(heights);
            ((IBrowserMemoryBuffer)_hmHeightsBuffer!.Buffer).CopyFromJS(jsH);
            using var jsB = new Int16Array(blockIds);
            ((IBrowserMemoryBuffer)_hmBlockIdsBuffer!.Buffer).CopyFromJS(jsB);
            using var jsSH = new Int32Array(seabedHeights);
            ((IBrowserMemoryBuffer)_hmSeabedHeightsBuffer!.Buffer).CopyFromJS(jsSH);
            using var jsSB = new Int16Array(seabedBlockIds);
            ((IBrowserMemoryBuffer)_hmSeabedBlockIdsBuffer!.Buffer).CopyFromJS(jsSB);
            _hmOpaqueCounterBuffer!.CopyFromCPU(_counterReset);
            _hmWaterCounterBuffer!.CopyFromCPU(_counterReset);

            _meshPaletteBuffer?.Dispose();
            _meshPaletteBuffer = _accelerator!.Allocate1D<float>(paletteColors.Length);
            _meshPaletteBuffer.CopyFromCPU(paletteColors);

            _meshAtlasUVBuffer?.Dispose();
            _meshAtlasUVBuffer = _accelerator!.Allocate1D<float>(atlasUVs.Length);
            _meshAtlasUVBuffer.CopyFromCPU(atlasUVs);

            _meshBlockFlagsBuffer?.Dispose();
            _meshBlockFlagsBuffer = _accelerator!.Allocate1D<float>(blockFlags.Length);
            _meshBlockFlagsBuffer.CopyFromCPU(blockFlags);

            _heightmapKernel(
                (Index1D)256,
                _hmHeightsBuffer!.View,
                _hmBlockIdsBuffer!.View,
                _meshPaletteBuffer!.View,
                _meshAtlasUVBuffer!.View,
                _meshBlockFlagsBuffer!.View,
                _hmSeabedHeightsBuffer!.View,
                _hmSeabedBlockIdsBuffer!.View,
                _hmOpaqueVertBuffer!.View,
                _hmOpaqueCounterBuffer!.View,
                _hmWaterVertBuffer!.View,
                _hmWaterCounterBuffer!.View,
                chunkX, chunkZ);

            // Wait for kernel to finish before reading counters
            await _accelerator!.SynchronizeAsync();

            // Read counters - CopyToHostAsync internally flushes and maps
            var opaqueCountResult = await _hmOpaqueCounterBuffer.CopyToHostAsync();
            var waterCountResult = await _hmWaterCounterBuffer.CopyToHostAsync();
            int opaqueFloats = Math.Min(opaqueCountResult[0], HmMaxOpaqueFloats);
            int waterFloats = Math.Min(waterCountResult[0], HmMaxWaterFloats);

            // Read vertex data directly from the output buffers - no intermediate copy
            float[] opaqueVerts = [];
            if (opaqueFloats > 0)
                opaqueVerts = await _hmOpaqueVertBuffer.CopyToHostAsync(0, opaqueFloats);

            float[] waterVerts = [];
            if (waterFloats > 0)
                waterVerts = await _hmWaterVertBuffer.CopyToHostAsync(0, waterFloats);

            return new MeshGenerationResult(opaqueVerts, opaqueFloats / 11, waterVerts, waterFloats / 11);
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>
    /// Get the native GPUBuffer from the ILGPU heightmap output buffers.
    /// For GPU-to-GPU copy without CPU readback. Data stays on GPU.
    /// </summary>
    public (GPUBuffer? opaqueBuffer, GPUBuffer? waterBuffer) GetHeightmapOutputGPUBuffers()
    {
        GPUBuffer? opaqueGpu = null;
        GPUBuffer? waterGpu = null;
        if (_hmOpaqueVertBuffer?.Buffer is SpawnDev.ILGPU.WebGPU.Backend.WebGPUMemoryBuffer oMem)
            opaqueGpu = oMem.NativeBuffer?.NativeBuffer;
        if (_hmWaterVertBuffer?.Buffer is SpawnDev.ILGPU.WebGPU.Backend.WebGPUMemoryBuffer wMem)
            waterGpu = wMem.NativeBuffer?.NativeBuffer;
        return (opaqueGpu, waterGpu);
    }

    /// <summary>
    /// Dispatch heightmap kernel and return vertex counts only (no CPU readback of vertex data).
    /// Use GetHeightmapOutputGPUBuffers() to get GPU buffer refs for GPU-to-GPU copy.
    /// </summary>
    public async Task<(int opaqueFloats, int waterFloats)> DispatchHeightmapKernelAsync(
        int[] heights, short[] blockIds, float[] paletteColors, float[] atlasUVs, float[] blockFlags,
        int[] seabedHeights, short[] seabedBlockIds, int chunkX, int chunkZ)
    {
        if (_heightmapKernel == null)
            throw new InvalidOperationException("Not initialized");

        await _meshLock.WaitAsync();
        try
        {
            // CopyFromJS - browser only, no fallback
            using var jsH = new Int32Array(heights);
            ((IBrowserMemoryBuffer)_hmHeightsBuffer!.Buffer).CopyFromJS(jsH);
            using var jsB = new Int16Array(blockIds);
            ((IBrowserMemoryBuffer)_hmBlockIdsBuffer!.Buffer).CopyFromJS(jsB);
            using var jsSH = new Int32Array(seabedHeights);
            ((IBrowserMemoryBuffer)_hmSeabedHeightsBuffer!.Buffer).CopyFromJS(jsSH);
            using var jsSB = new Int16Array(seabedBlockIds);
            ((IBrowserMemoryBuffer)_hmSeabedBlockIdsBuffer!.Buffer).CopyFromJS(jsSB);
            _hmOpaqueCounterBuffer!.CopyFromCPU(_counterReset);
            _hmWaterCounterBuffer!.CopyFromCPU(_counterReset);

            _meshPaletteBuffer?.Dispose();
            _meshPaletteBuffer = _accelerator!.Allocate1D<float>(paletteColors.Length);
            _meshPaletteBuffer.CopyFromCPU(paletteColors);

            _meshAtlasUVBuffer?.Dispose();
            _meshAtlasUVBuffer = _accelerator!.Allocate1D<float>(atlasUVs.Length);
            _meshAtlasUVBuffer.CopyFromCPU(atlasUVs);

            _meshBlockFlagsBuffer?.Dispose();
            _meshBlockFlagsBuffer = _accelerator!.Allocate1D<float>(blockFlags.Length);
            _meshBlockFlagsBuffer.CopyFromCPU(blockFlags);

            _heightmapKernel(
                (Index1D)256,
                _hmHeightsBuffer!.View,
                _hmBlockIdsBuffer!.View,
                _meshPaletteBuffer!.View,
                _meshAtlasUVBuffer!.View,
                _meshBlockFlagsBuffer!.View,
                _hmSeabedHeightsBuffer!.View,
                _hmSeabedBlockIdsBuffer!.View,
                _hmOpaqueVertBuffer!.View,
                _hmOpaqueCounterBuffer!.View,
                _hmWaterVertBuffer!.View,
                _hmWaterCounterBuffer!.View,
                chunkX, chunkZ);

            await _accelerator!.SynchronizeAsync();

            // Only read the counters (4 bytes each) - vertex data stays on GPU
            var opaqueCountResult = await _hmOpaqueCounterBuffer.CopyToHostAsync();
            var waterCountResult = await _hmWaterCounterBuffer.CopyToHostAsync();
            return (
                Math.Min(opaqueCountResult[0], HmMaxOpaqueFloats),
                Math.Min(waterCountResult[0], HmMaxWaterFloats)
            );
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>
    /// Dispatch heightmap kernel from a raw binary frame ArrayBuffer.
    /// Heights and seabedHeights go JS -> GPU via CopyFromJS (zero .NET copy).
    /// BlockIds need ushort->int conversion (still .NET for now - kernel fix needed).
    /// </summary>
    public async Task<(int opaqueFloats, int waterFloats)> DispatchHeightmapFromFrameAsync(
        ArrayBuffer frameBuffer, int binaryDataOffset,
        float[] paletteColors, float[] atlasUVs, float[] blockFlags,
        int chunkX, int chunkZ)
    {
        if (_heightmapKernel == null)
            throw new InvalidOperationException("Not initialized");

        await _meshLock.WaitAsync();
        try
        {
            int off = binaryDataOffset;

            // Uint8Array views directly over the JS ArrayBuffer - zero .NET copy
            using var hView = new Uint8Array(frameBuffer, off, 256 * 4);
            ((IBrowserMemoryBuffer)_hmHeightsBuffer!.Buffer).CopyFromJS(hView);
            off += 256 * 4;

            using var bView = new Uint8Array(frameBuffer, off, 256 * 2);
            ((IBrowserMemoryBuffer)_hmBlockIdsBuffer!.Buffer).CopyFromJS(bView);
            off += 256 * 2;

            using var shView = new Uint8Array(frameBuffer, off, 256 * 4);
            ((IBrowserMemoryBuffer)_hmSeabedHeightsBuffer!.Buffer).CopyFromJS(shView);
            off += 256 * 4;

            using var sbView = new Uint8Array(frameBuffer, off, 256 * 2);
            ((IBrowserMemoryBuffer)_hmSeabedBlockIdsBuffer!.Buffer).CopyFromJS(sbView);

            _hmOpaqueCounterBuffer!.CopyFromCPU(_counterReset);
            _hmWaterCounterBuffer!.CopyFromCPU(_counterReset);

            _meshPaletteBuffer?.Dispose();
            _meshPaletteBuffer = _accelerator!.Allocate1D<float>(paletteColors.Length);
            _meshPaletteBuffer.CopyFromCPU(paletteColors);

            _meshAtlasUVBuffer?.Dispose();
            _meshAtlasUVBuffer = _accelerator!.Allocate1D<float>(atlasUVs.Length);
            _meshAtlasUVBuffer.CopyFromCPU(atlasUVs);

            _meshBlockFlagsBuffer?.Dispose();
            _meshBlockFlagsBuffer = _accelerator!.Allocate1D<float>(blockFlags.Length);
            _meshBlockFlagsBuffer.CopyFromCPU(blockFlags);

            _heightmapKernel(
                (Index1D)256,
                _hmHeightsBuffer!.View, _hmBlockIdsBuffer!.View,
                _meshPaletteBuffer!.View, _meshAtlasUVBuffer!.View, _meshBlockFlagsBuffer!.View,
                _hmSeabedHeightsBuffer!.View, _hmSeabedBlockIdsBuffer!.View,
                _hmOpaqueVertBuffer!.View, _hmOpaqueCounterBuffer!.View,
                _hmWaterVertBuffer!.View, _hmWaterCounterBuffer!.View,
                chunkX, chunkZ);

            await _accelerator!.SynchronizeAsync();

            var opaqueCountResult = await _hmOpaqueCounterBuffer.CopyToHostAsync();
            var waterCountResult = await _hmWaterCounterBuffer.CopyToHostAsync();
            return (
                Math.Min(opaqueCountResult[0], HmMaxOpaqueFloats),
                Math.Min(waterCountResult[0], HmMaxWaterFloats)
            );
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>Reuse GPU buffer if large enough, only reallocate if needed.</summary>
    private void EnsureBuffer(ref MemoryBuffer1D<float, Stride1D.Dense>? buffer, int requiredLength)
    {
        if (buffer == null || buffer.Length < requiredLength)
        {
            buffer?.Dispose();
            buffer = _accelerator!.Allocate1D<float>(requiredLength);
        }
    }

    public ValueTask DisposeAsync()
    {
        _meshResultBuffer?.Dispose();
        _meshOpaqueCounterBuffer?.Dispose();
        _meshWaterCounterBuffer?.Dispose();
        _meshOpaqueVertBuffer?.Dispose();
        _meshWaterVertBuffer?.Dispose();
        _meshBlockBuffer?.Dispose();
        _meshPaletteBuffer?.Dispose();
        _meshAtlasUVBuffer?.Dispose();
        _meshBlockFlagsBuffer?.Dispose();
        _hmHeightsBuffer?.Dispose();
        _hmBlockIdsBuffer?.Dispose();
        _hmSeabedHeightsBuffer?.Dispose();
        _hmSeabedBlockIdsBuffer?.Dispose();
        _hmOpaqueVertBuffer?.Dispose();
        _hmOpaqueCounterBuffer?.Dispose();
        _hmWaterVertBuffer?.Dispose();
        _hmWaterCounterBuffer?.Dispose();
        _accelerator?.Dispose();
        _context?.Dispose();
        _meshLock.Dispose();
        IsInitialized = false;
        return ValueTask.CompletedTask;
    }
}

public record MeshGenerationResult(
    float[] OpaqueVertices, int OpaqueVertexCount,
    float[] WaterVertices, int WaterVertexCount);
