using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Interface for the render worker service running in a dedicated Web Worker.
/// All methods are async for cross-worker RPC via SpawnDev.SpawnJS.WebWorkers.
/// </summary>
public interface IRenderWorkerService : IAsyncDisposable
{
    /// <summary>Starts rendering serverId's world (the worker serves ONE server for its whole life), drawn by SpawnDev.VoxelEngine.</summary>
    Task StartAsync(string serverId, float camX, float camY, float camZ, float pitch, float yaw);
    Task AttachCanvasAsync(OffscreenCanvas canvas, int width, int height);
    Task DetachCanvasAsync();
    Task ProcessInputAsync(float dx, float dy, float dt, string[] keysDown);
    /// <summary>Input the page saw on the canvas, for the worker's GPU HUD (the worker has no DOM to listen to).</summary>
    Task PushUiInputAsync(UiInputEvent[] events);
    /// <summary>The camera took (or released) the pointer: the HUD shows its crosshair and stops hovering under the mouse.</summary>
    Task SetPointerLockedAsync(bool locked);
    /// <summary>Shows or hides the GPU HUD (<c>/map?hud=0</c> hides it: a clean frame for streaming or capture).</summary>
    Task SetHudVisibleAsync(bool visible);
    /// <summary>Holds the draw distance at this many chunks instead of adapting it (<c>/map?dd=14</c>): for the gate and for screenshots.</summary>
    Task SetDrawDistanceAsync(int chunks);
    /// <summary>Chat, the player list and notices from the page's SignalR connection, for the worker's HUD.</summary>
    Task PushHudFeedAsync(HudFeed feed);
    /// <summary>The lines the player typed into the HUD's chat since the last call (the page sends them to the server).</summary>
    Task<string[]> TakeOutgoingChatAsync();
    /// <summary>
    /// Where a click belongs to the HUD, not to the camera, as rectangles of four floats (x, y, width, height; canvas pixels): the player list, and the whole
    /// view while a menu is open. The page asks before it gives the pointer to the camera.
    /// </summary>
    Task<float[]> GetHudClickRectsAsync();
    Task ResizeAsync(int width, int height);
    Task<RenderStats> GetStatsAsync();
    Task SetTimeOfDay(int ticks);
}
