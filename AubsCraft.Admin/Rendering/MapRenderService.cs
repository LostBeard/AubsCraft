using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.ILGPU.WebGPU;
using ILGPU.Runtime;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The viewer's frame loop and canvas: owns the WebGPU canvas context, the camera and the day/night clock, and each animation
/// frame asks <see cref="VoxelWorldRenderer"/> to draw the world into the canvas texture, then lets the HUD draw on top
/// (<see cref="OnOverlay"/>) in the same command encoder.
/// </summary>
public sealed class MapRenderService : IDisposable
{
    private readonly SpawnJSRuntime _js;

    private GPUDevice? _device;
    private GPUQueue? _queue;
    private GPUCanvasContext? _context;
    private string _canvasFormat = "bgra8unorm";
    private int _canvasWidth;
    private int _canvasHeight;

    private bool _running;
    private bool _disposed;
    private double _lastTimestamp;
    private ActionCallback<double>? _rafCallback;
    // Cached submit array - reused every frame
    private readonly GPUCommandBuffer[] _submitArray = new GPUCommandBuffer[1];

    /// <summary>
    /// Time of day as 0.0-1.0 (maps to Minecraft's 24000 tick cycle): 0 = midnight, 0.25 = sunrise, 0.5 = noon (sun elevation =
    /// -cos(2*pi*t)); noon until the server reports its time. Set from the server's time via RenderWorkerService.
    /// </summary>
    public float TimeOfDay { get; set; } = 0.5f;
    /// <summary>How hard it rains (0..1) and how stormy it is (0..1): eased toward what the server's weather says, so a shower does not snap on.</summary>
    public float RainLevel { get; set; }
    public float ThunderLevel { get; set; }

    public FpsCamera Camera { get; } = new();

    /// <summary>The renderer that draws the world (set by the render worker once the GPU is up).</summary>
    public VoxelWorldRenderer? Voxel { get; set; }

    public bool IsInitialized { get; private set; }
    public Action<float>? OnUpdate { get; set; }

    /// <summary>The WebGPU device and queue the world draws with (null until Init): the HUD draws with the same ones.</summary>
    internal GPUDevice? Device => _device;
    internal GPUQueue? Queue => _queue;
    /// <summary>The canvas's texture format; anything drawn on top of the world must use it.</summary>
    public string CanvasFormat => _canvasFormat;

    /// <summary>
    /// Called with the frame's command encoder, the canvas texture view and its size right after the world's render pass
    /// ends and before the encoder is finished: whatever it records is drawn on top of the world, in the same submit.
    /// </summary>
    public Action<GPUCommandEncoder, GPUTextureView, int, int>? OnOverlay { get; set; }

    // What the last frame drew (the HUD and the stats line show these)
    public int VisibleChunkCount { get; private set; }
    public int TotalChunkCount { get; private set; }
    public int VisibleVertices { get; private set; }
    public int TotalVertices { get; private set; }
    public float Fps { get; private set; }
    public float FrameTimeMs { get; private set; }

    /// <summary>The last voxel frame's numbers (for the HUD's debug overlay).</summary>
    public VoxelFrameStats LastVoxelStats { get; private set; }
    /// <summary>CPU milliseconds the last voxel frame took end to end: record, overlay, finish, submit.</summary>
    public double LastVoxelFrameMs { get; private set; }
    private double _voxelLogTimer;

    // FPS tracking + adaptive draw distance (chunks): frame rate alone steers it
    private int _frameCount;
    private double _fpsAccumulator;
    private float _lastDt;
    public int DrawDistance { get; private set; } = 30;
    private bool _drawDistanceFixed;

    /// <summary>Hold the draw distance at this many chunks (within the policy's range) instead of adapting it: for the gate and for screenshots.</summary>
    public void FixDrawDistance(int chunks)
    {
        DrawDistance = Math.Clamp(chunks, FarWindow.MinDrawDistance, FarWindow.MaxDrawDistance);
        _drawDistanceFixed = true;
    }

    public MapRenderService(SpawnJSRuntime js)
    {
        _js = js;
    }

    public void Init(HTMLCanvasElement canvas, Accelerator accelerator)
    {
        var ctx = canvas.GetContext<GPUCanvasContext>("webgpu")
            ?? throw new InvalidOperationException("Failed to get WebGPU canvas context");
        InitWithContext(ctx, canvas.ClientWidth, canvas.ClientHeight, accelerator);
        canvas.Width = _canvasWidth;
        canvas.Height = _canvasHeight;
    }

    public void InitOffscreen(OffscreenCanvas canvas, Accelerator accelerator)
    {
        var ctx = canvas.GetWebGPUContext()
            ?? throw new InvalidOperationException("Failed to get WebGPU canvas context");
        InitWithContext(ctx, canvas.Width, canvas.Height, accelerator);
    }

    private void InitWithContext(GPUCanvasContext context, int width, int height, Accelerator accelerator)
    {
        if (IsInitialized) return;

        if (accelerator is not WebGPUAccelerator webGpuAccel)
            throw new InvalidOperationException("MapRenderService requires a WebGPU accelerator");

        var nativeAccel = webGpuAccel.NativeAccelerator;
        _device = nativeAccel.NativeDevice
            ?? throw new InvalidOperationException("WebGPU native device is null");
        _queue = nativeAccel.Queue
            ?? throw new InvalidOperationException("WebGPU queue is null");

        _context = context;

        using var navigator = _js.Get<Navigator>("navigator");
        using var gpu = navigator.Gpu;
        if (gpu is not null)
            _canvasFormat = gpu.GetPreferredCanvasFormat();

        _context.Configure(new GPUCanvasConfiguration
        {
            Device = _device,
            Format = _canvasFormat,
            AlphaMode = CanvasAlphaMode,
        });

        _canvasWidth = width;
        _canvasHeight = height;
        IsInitialized = true;
    }

    /// <summary>Handle canvas resize from external source (e.g. main thread notifying worker).</summary>
    public void HandleResize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _canvasWidth = width;
            _canvasHeight = height;
        }
    }

    /// <summary>
    /// Re-attach a new canvas to an already-initialized renderer. Reuses the WebGPU device and everything loaded; only the canvas
    /// context is recreated (the voxel renderer re-creates its depth texture when the size differs).
    /// </summary>
    public void AttachCanvas(OffscreenCanvas canvas)
    {
        if (!IsInitialized || _device == null)
            throw new InvalidOperationException("Renderer not initialized - call InitOffscreen first");

        StopRenderLoop();

        _context?.Unconfigure();
        _context?.Dispose();
        _context = null;

        _context = canvas.GetWebGPUContext()
            ?? throw new InvalidOperationException("Failed to get WebGPU canvas context");
        _context.Configure(new GPUCanvasConfiguration
        {
            Device = _device,
            Format = _canvasFormat,
            AlphaMode = CanvasAlphaMode,
        });

        _canvasWidth = canvas.Width;
        _canvasHeight = canvas.Height;
        _disposed = false;
    }

    /// <summary>Detach the canvas context without destroying GPU resources: the loop stops, the device and the loaded world stay.</summary>
    public void DetachCanvas()
    {
        StopRenderLoop();
        _context?.Unconfigure();
        _context?.Dispose();
        _context = null;
    }

    /// <summary>"opaque" (the page's canvas), or "premultiplied" for a headset's augmented-reality frames, where clear alpha 0 shows the camera feed. Set before the canvas is configured.</summary>
    public string CanvasAlphaMode { get; set; } = "opaque";

    public void StartRenderLoop()
    {
        if (_running) return;
        _running = true;
        _lastTimestamp = 0;
        _rafCallback ??= new ActionCallback<double>(OnAnimationFrame);
        RequestFrame();
    }

    public void StopRenderLoop() => _running = false;

    private void RequestFrame()
    {
        if (!_running || _disposed || _rafCallback == null) return;
        // Use globalThis.requestAnimationFrame - works in both Window and Worker contexts
        _js.CallVoid("requestAnimationFrame", _rafCallback);
    }

    private void OnAnimationFrame(double timestamp)
    {
        if (!_running || _disposed) return;
        float dt = _lastTimestamp > 0 ? (float)((timestamp - _lastTimestamp) / 1000.0) : 1f / 60f;
        _lastTimestamp = timestamp;
        dt = Math.Min(dt, 0.1f);
        TrackFrame(dt);
        OnUpdate?.Invoke(dt);
        RenderFrame();
        RequestFrame();
    }

    /// <summary>Count a frame of <paramref name="dt"/> seconds: the frame rate the HUD shows and the adaptive draw distance steers by (a headset's frame loop calls it too).</summary>
    public void TrackFrame(float dt)
    {
        _lastDt = dt;
        _frameCount++;
        _fpsAccumulator += dt;
        if (_fpsAccumulator >= 0.5)
        {
            Fps = (float)(_frameCount / _fpsAccumulator);
            FrameTimeMs = (float)(_fpsAccumulator / _frameCount * 1000.0);
            _frameCount = 0;
            _fpsAccumulator = 0;

            // Adaptive draw distance (the fog follows it, so a shorter one is a closer horizon, not a visible edge)
            if (!_drawDistanceFixed) DrawDistance = FarWindow.NextDrawDistance(DrawDistance, Fps);
        }
    }

    private void RenderFrame()
    {
        if (Voxel is not { } voxel || _device == null || _context == null) return;

        var frameTimer = System.Diagnostics.Stopwatch.StartNew();
        using var colorTexture = _context.GetCurrentTexture();
        using var colorView = colorTexture.CreateView();
        using var encoder = _device.CreateCommandEncoder();

        var light = DayLighting.At(TimeOfDay, RainLevel, ThunderLevel);
        voxel.TimeOfDay = TimeOfDay; voxel.Rain = RainLevel; voxel.Thunder = ThunderLevel;
        var stats = voxel.Render(encoder, colorView, _canvasWidth, _canvasHeight, Camera, in light, DrawDistance);
        VisibleChunkCount = stats.VisibleSections;
        TotalChunkCount = stats.SectionsWithQuads;
        VisibleVertices = (int)Math.Min(stats.VisibleQuads * 6, int.MaxValue);
        TotalVertices = (int)Math.Min(stats.TotalQuads * 6, int.MaxValue);

        OnOverlay?.Invoke(encoder, colorView, _canvasWidth, _canvasHeight);
        using var commandBuffer = encoder.Finish();
        _submitArray[0] = commandBuffer;
        _queue!.Submit(_submitArray);

        LastVoxelStats = stats;
        LastVoxelFrameMs = frameTimer.Elapsed.TotalMilliseconds;
        _voxelLogTimer += _lastDt;
        if (_voxelLogTimer >= 5.0)
        {
            _voxelLogTimer = 0;
            Console.WriteLine($"[Voxel] {Fps:F0} fps | frame CPU {LastVoxelFrameMs:F1} ms (record {stats.RecordMs:F1}, of it gather {stats.GatherMs:F1}) | {stats.DrawCalls} draws, {stats.BufferBinds} binds | " +
                $"{stats.VisibleSections}/{stats.SectionsWithQuads} sections | {stats.Columns} columns+far | {stats.VisibleQuads / 1000}K/{stats.TotalQuads / 1000}K quads | " +
                $"{voxel.MeshesDone} meshes ({voxel.RemeshesDone} re-meshes, {voxel.StaleCount} waiting) in {voxel.ArenaBlocks} quad buffers | far {voxel.FarMeshedCount}/{voxel.FarChunkCount} meshed" +
                (voxel.Entities is { Known: > 0 } e ? $" | creatures {e.EntityCount}/{e.Known} {e.PartCount} parts {e.PrepareMs:F1} ms" : "") +
                (voxel.Particles is { Known: > 0 } p ? $" | particles {p.DrawnCount}/{p.Known} emitters" : ""));
        }
    }

    /// <summary>
    /// Draw the world once from <paramref name="view"/> into the canvas (a headset's eye: the caller copies the canvas on to the headset's layer right after), at the canvas's
    /// current size, with no HUD. Does nothing without a canvas or a world. <paramref name="width"/> and <paramref name="height"/> are the canvas's size in pixels.
    /// </summary>
    public VoxelFrameStats? RenderView(in ViewSetup view, int width, int height)
    {
        if (Voxel is not { } voxel || _device == null || _context == null) return null;
        _canvasWidth = width;
        _canvasHeight = height;
        using var colorTexture = _context.GetCurrentTexture();
        using var colorView = colorTexture.CreateView();
        using var encoder = _device.CreateCommandEncoder();
        var light = DayLighting.At(TimeOfDay, RainLevel, ThunderLevel);
        voxel.TimeOfDay = TimeOfDay; voxel.Rain = RainLevel; voxel.Thunder = ThunderLevel;
        var stats = voxel.Render(encoder, colorView, width, height, in view, in light, DrawDistance);
        using var commandBuffer = encoder.Finish();
        _submitArray[0] = commandBuffer;
        _queue!.Submit(_submitArray);
        LastVoxelStats = stats;
        return stats;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _rafCallback?.Dispose();
        _rafCallback = null;
        _context?.Unconfigure();
        _context?.Dispose();
        _context = null;
        IsInitialized = false;
        _disposed = false;
    }
}
