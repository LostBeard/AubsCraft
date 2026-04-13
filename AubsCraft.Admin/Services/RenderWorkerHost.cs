using SpawnDev.BlazorJS.WebWorkers;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Singleton that owns the render worker's lifetime.
/// The WebWorker and its GPU context/chunk data persist across page navigation.
/// Map.razor attaches/detaches its canvas but never kills the worker.
/// </summary>
public class RenderWorkerHost
{
    private readonly WebWorkerService _workerService;
    private WebWorker? _worker;
    private IRenderWorkerService? _service;
    private readonly string _serviceKey = Guid.NewGuid().ToString();

    // Saved camera state for page re-mount
    public float CamX { get; set; }
    public float CamY { get; set; } = 100f;
    public float CamZ { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }

    public bool IsWorkerCreated => _worker != null;
    public bool IsStarted { get; set; }

    public RenderWorkerHost(WebWorkerService workerService)
    {
        _workerService = workerService;
    }

    /// <summary>
    /// Get or create the render worker. First call creates the worker thread.
    /// Subsequent calls return the existing proxy.
    /// </summary>
    public async Task<(WebWorker worker, IRenderWorkerService service)> EnsureWorkerAsync(
        SpawnDev.BlazorJS.JSObjects.OffscreenCanvas canvas, int width, int height)
    {
        if (_worker == null)
        {
            _worker = await _workerService.GetWebWorker();
            await _worker.New<IRenderWorkerService>(_serviceKey,
                () => new RenderWorkerService(canvas, width, height));
            _service = _worker.GetKeyedService<IRenderWorkerService>(_serviceKey);
        }
        return (_worker, _service!);
    }

    public IRenderWorkerService? Service => _service;
}
