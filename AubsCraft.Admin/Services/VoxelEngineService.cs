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
            // Underground skip: zero out blocks completely buried by opaque solids.
            // The kernel skips blockId=0, so zeroed blocks cost nothing on GPU.
            // Out-of-bounds neighbors treated as OPAQUE to prevent rendering deep
            // underground chunk boundaries (which caused 47M+ vertex explosions).
            var blockInts = _blockIntsPool!;
            const int W = 16, H = 384, WW = W * W;
            for (int y = 0; y < H; y++)
            for (int z = 0; z < W; z++)
            for (int x = 0; x < W; x++)
            {
                int idx = x + z * W + y * WW;
                int b = blocks[idx];
                if (b == 0) { blockInts[idx] = 0; continue; }

                // Check each neighbor - out of bounds = opaque (not air)
                bool exposed =
                    (x > 0 && IsTransparentBlock(blocks, blockFlags, idx - 1)) ||
                    (x < 15 && IsTransparentBlock(blocks, blockFlags, idx + 1)) ||
                    (z > 0 && IsTransparentBlock(blocks, blockFlags, idx - W)) ||
                    (z < 15 && IsTransparentBlock(blocks, blockFlags, idx + W)) ||
                    (y > 0 && IsTransparentBlock(blocks, blockFlags, idx - WW)) ||
                    (y < 383 && IsTransparentBlock(blocks, blockFlags, idx + WW));

                blockInts[idx] = exposed ? b : 0;
            }

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
    /// Dispatch heightmap kernel from a raw binary frame ArrayBuffer.
    /// Binary layout: int32[256] heights, int16[256] blockIds, int32[256] seabedHeights, int16[256] seabedBlockIds.
    /// Packs into HeightmapColumn[] struct and BlockPalette[] struct to keep binding count under WebGPU limit.
    /// </summary>
    public async Task<(int opaqueFloats, int waterFloats)> DispatchHeightmapFromFrameAsync(
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
            return (
                Math.Min(counters[0], HmMaxOpaqueFloats),
                Math.Min(counters[1], HmMaxWaterFloats)
            );
        }
        finally
        {
            _meshLock.Release();
        }
    }

    /// <summary>Reuse GPU buffer if large enough, only reallocate if needed.</summary>
    /// <summary>Returns true if block at index is air or transparent (plant/water).</summary>
    private static bool IsTransparentBlock(ushort[] blocks, float[] blockFlags, int idx)
    {
        int b = blocks[idx];
        return b == 0 || blockFlags[b] > 0.5f;
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
    float[] WaterVertices, int WaterVertexCount);
