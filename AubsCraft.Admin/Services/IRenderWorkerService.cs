using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Interface for the render worker service running in a dedicated Web Worker.
/// All methods are async for cross-worker RPC via SpawnDev.SpawnJS.WebWorkers.
/// </summary>
public interface IRenderWorkerService : IAsyncDisposable
{
    /// <summary>Starts rendering serverId's world (the worker serves ONE server for its whole life).</summary>
    Task StartAsync(string serverId, float camX, float camY, float camZ, float pitch, float yaw);
    Task AttachCanvasAsync(OffscreenCanvas canvas, int width, int height);
    Task DetachCanvasAsync();
    Task ProcessInputAsync(float dx, float dy, float dt, string[] keysDown);
    Task ResizeAsync(int width, int height);
    Task<RenderStats> GetStatsAsync();
    Task SetTimeOfDay(int ticks);
}
