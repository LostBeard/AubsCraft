using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.WebWorkers;

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
            _renderWorker = await _workerService.GetWebWorker();
            await _renderWorker.New<IRenderWorkerService>(_serviceKey,
                () => new RenderWorkerService(canvas, width, height));
            _service = _renderWorker.GetKeyedService<IRenderWorkerService>(_serviceKey);
        }
        return (_renderWorker, _service!);
    }

    public IRenderWorkerService? Service => _service;
}
