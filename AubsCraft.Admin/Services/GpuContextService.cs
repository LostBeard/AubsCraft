using ILGPU;
using ILGPU.Runtime;
using SpawnDev.ILGPU;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Owns the ILGPU Context and the preferred Accelerator (WebGPU in the browser) the viewer's GPU work runs on: the voxel
/// renderer's meshers and the WebGPU device it draws with come from it.
/// </summary>
public sealed class GpuContextService : IAsyncDisposable
{
    private Context? _context;
    private Accelerator? _accelerator;

    public Accelerator? Accelerator => _accelerator;
    public bool IsInitialized { get; private set; }
    public string? BackendName { get; private set; }

    public async Task InitAsync()
    {
        if (IsInitialized) return;

        var builder = Context.Create();
        await builder.AllAcceleratorsAsync();
        _context = builder.ToContext();

        _accelerator = await _context.CreatePreferredAcceleratorAsync();
        BackendName = _accelerator.AcceleratorType.ToString();

        Console.WriteLine($"[GpuContextService] Initialized: {BackendName}");
        IsInitialized = true;
    }

    public ValueTask DisposeAsync()
    {
        _accelerator?.Dispose();
        _context?.Dispose();
        _accelerator = null;
        _context = null;
        IsInitialized = false;
        return ValueTask.CompletedTask;
    }
}
