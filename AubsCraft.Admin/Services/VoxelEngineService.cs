using ILGPU;
using ILGPU.Runtime;
using SpawnDev.BlazorJS;
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
    private Action<Index1D, ArrayView<int>, ArrayView<float>, ArrayView<float>, ArrayView<int>, int, int>? _meshKernel;
    private MemoryBuffer1D<int, Stride1D.Dense>? _meshBlockBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshPaletteBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshVertexBuffer;
    private MemoryBuffer1D<int, Stride1D.Dense>? _meshCounterBuffer;
    private MemoryBuffer1D<float, Stride1D.Dense>? _meshResultBuffer;
    private readonly SemaphoreSlim _meshLock = new(1, 1);

    private readonly int[] _counterReset = [0];
    private int[]? _blockIntsPool;

    // 16 * 384 * 16 = 98304 blocks per chunk
    private const int BlocksPerChunk = 16 * 384 * 16;
    // Worst case: every block has 6 faces x 54 floats = way too much.
    // Practical max: ~2M floats covers dense chunks.
    private const int MaxOutputFloats = 2_000_000;

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
            ArrayView<float>, // vertices (output)
            ArrayView<int>,   // counter
            int, int           // chunkWorldX, chunkWorldZ
        >(MinecraftMeshKernel.MeshKernel);

        _meshBlockBuffer = _accelerator.Allocate1D<int>(BlocksPerChunk);
        _meshVertexBuffer = _accelerator.Allocate1D<float>(MaxOutputFloats);
        _meshCounterBuffer = _accelerator.Allocate1D<int>(1);
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
        ushort[] blocks, float[] paletteColors, int chunkX, int chunkZ)
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
            _meshCounterBuffer!.CopyFromCPU(_counterReset);

            // Always re-allocate palette buffer to exact size
            _meshPaletteBuffer?.Dispose();
            _meshPaletteBuffer = _accelerator!.Allocate1D<float>(paletteColors.Length);
            _meshPaletteBuffer.CopyFromCPU(paletteColors);

            _meshKernel(
                (Index1D)BlocksPerChunk,
                _meshBlockBuffer!.View,
                _meshPaletteBuffer!.View,
                _meshVertexBuffer!.View,
                _meshCounterBuffer!.View,
                chunkX, chunkZ);

            await _accelerator!.SynchronizeAsync();

            var counterResult = await _meshCounterBuffer.CopyToHostAsync();
            int floatCount = counterResult[0];

            if (floatCount <= 0)
                return new MeshGenerationResult([], 0);

            if (floatCount > MaxOutputFloats)
                floatCount = MaxOutputFloats;

            // Always re-allocate result buffer to exact size
            _meshResultBuffer?.Dispose();
            _meshResultBuffer = _accelerator!.Allocate1D<float>(floatCount);

            _meshResultBuffer.View.SubView(0, floatCount).CopyFrom(
                _meshVertexBuffer!.View.SubView(0, floatCount));
            await _accelerator!.SynchronizeAsync();

            var vertices = await _meshResultBuffer.CopyToHostAsync();
            return new MeshGenerationResult(vertices, floatCount / 9);
        }
        finally
        {
            _meshLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _meshResultBuffer?.Dispose();
        _meshCounterBuffer?.Dispose();
        _meshVertexBuffer?.Dispose();
        _meshBlockBuffer?.Dispose();
        _meshPaletteBuffer?.Dispose();
        _accelerator?.Dispose();
        _context?.Dispose();
        _meshLock.Dispose();
        IsInitialized = false;
        return ValueTask.CompletedTask;
    }
}

public record MeshGenerationResult(float[] Vertices, int VertexCount);
