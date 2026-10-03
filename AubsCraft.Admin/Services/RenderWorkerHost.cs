using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.WebWorkers;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Singleton that owns the render worker's lifetime.
/// The render worker creates and controls the JS data worker internally.
/// Hierarchy: Window -> Render Worker (Blazor) -> JS Data Worker
/// </summary>
public class RenderWorkerHost
{
    private readonly WebWorkerService _workerService;
    private WebWorker? _renderWorker;
    private IRenderWorkerService? _service;
    private readonly string _serviceKey = Guid.NewGuid().ToString();

    // Saved camera state for page re-mount
    public float CamX { get; set; }
    public float CamY { get; set; } = 100f;
    public float CamZ { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }

    public bool IsWorkerCreated => _renderWorker != null;
    public bool IsStarted { get; set; }
    /// <summary>The server whose world the running worker renders. A different server needs a new worker (ResetAsync).</summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Ends the current worker (terminating it also ends its JS data worker and WebSocket) so the next
    /// EnsureWorkerAsync starts fresh - used when the panel switches to another server's world.
    /// </summary>
    public async Task ResetAsync()
    {
        if (_service != null)
        {
            try { await _service.DisposeAsync(); }
            catch (Exception ex) { Console.WriteLine($"[RenderWorkerHost] Worker dispose failed: {ex.Message}"); }
        }
        _renderWorker?.Dispose();
        _renderWorker = null;
        _service = null;
        IsStarted = false;
        ServerId = null;
    }

    public RenderWorkerHost(WebWorkerService workerService)
    {
        _workerService = workerService;
    }

    /// <summary>
    /// Get or create the render worker. First call creates the worker thread.
    /// The render worker internally creates the JS data worker.
    /// </summary>
    public async Task<(WebWorker worker, IRenderWorkerService service)> EnsureWorkerAsync(
        OffscreenCanvas canvas, int width, int height)
    {
        if (_renderWorker == null)
        {
            _renderWorker = await _workerService.GetWebWorker()
                ?? throw new InvalidOperationException("Could not start the render worker.");
            await _renderWorker.New<IRenderWorkerService>(_serviceKey,
                () => new RenderWorkerService(canvas, width, height));
            _service = _renderWorker.GetKeyedService<IRenderWorkerService>(_serviceKey);
        }
        return (_renderWorker, _service!);
    }

    public IRenderWorkerService? Service => _service;
}
