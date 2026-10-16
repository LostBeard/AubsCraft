using System.Numerics;
using AubsCraft.Admin.Rendering;
using AubsCraft.Admin.Services;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Xr;

/// <summary>
/// The viewer in a headset. WebXR lives on the page's own thread, so while a session runs the world is drawn HERE: a second <see cref="RenderWorkerService"/> with no loop of its own streams the
/// same server's columns around the head and draws each eye with WebGPU into a bridge canvas, which is copied on to the headset's WebGL layer (<see cref="WebGlXrBlit"/>). The render worker's
/// own loop (the desktop canvas) is paused meanwhile and picks up where the headset left the camera. Thumbsticks fly: the left stick along where the head faces, the right stick snap-turns and
/// (pushed forward or back) rises or sinks, a grip held makes it fast, B or Y leaves.
/// </summary>
public sealed class XrViewerHost : IDisposable
{
    private readonly SpawnJSRuntime _js;
    private readonly GpuContextService _gpu;
    private readonly MapRenderService _map;
    private readonly ViewerHud _hud;
    private readonly XrSession _session;
    private readonly XrLocomotion _loco = new();

    private RenderWorkerService? _world;
    private OffscreenCanvas? _bridge;
    private WebGlXrBlit? _blit;
    private Matrix4x4? _worldFromRoom;
    private Vector3 _startPosition;
    private Vector3 _startForward;
    private Quaternion _lastHeadOrientation = Quaternion.Identity;
    private double _lastTime = -1;
    private bool _exitWasDown = true;
    private bool _worldStarting;
    private int _framesDrawn;
    private double _drawMsSum;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    /// <summary>Blocks a second at full thumbstick.</summary>
    public float FlySpeed { get; set; } = 8f;
    /// <summary>What a held grip multiplies the speed by.</summary>
    public float BoostFactor { get; set; } = 6f;

    public XrViewerHost(SpawnJSRuntime js, GpuContextService gpu, MapRenderService map, ViewerHud hud)
    {
        _js = js;
        _gpu = gpu;
        _map = map;
        _hud = hud;
        _session = new XrSession(js);
        _session.OnFrame += OnFrame;
        _session.OnEnded += OnEnded;
    }

    public bool IsActive => _session.IsActive;
    public string? Mode => _session.Mode;

    /// <summary>Raised when the session has ended: where the head was (the fly camera's yaw and pitch, degrees), so the desktop view can take over from there.</summary>
    public event Action<Vector3, float, float>? Ended;

    public Task<bool> VrSupportedAsync() => _session.IsVrSupportedAsync();
    public Task<bool> ArSupportedAsync() => _session.IsArSupportedAsync();

    /// <summary>
    /// Enter a session and start the world around <paramref name="cameraPosition"/>. Call it straight from the click: the session is requested first thing.
    /// <paramref name="serverTicks"/> is the server's time of day (negative: unknown).
    /// </summary>
    public async Task EnterAsync(string mode, string serverId, Vector3 cameraPosition, float yawDegrees, float pitchDegrees, int serverTicks)
    {
        if (_session.IsActive) return;
        _startPosition = cameraPosition;
        float yaw = yawDegrees * MathF.PI / 180f;
        _startForward = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        _worldFromRoom = null;
        _lastTime = -1;
        _exitWasDown = true;
        _loco.Reset();
        _framesDrawn = 0;
        _drawMsSum = 0;

        await _session.EnterAsync(mode);
        _blit = new WebGlXrBlit();
        _blit.Initialize(_session.GL!);

        // The world, on this thread. The headset shows nothing until it has its first columns: the frames before that skip drawing.
        _worldStarting = true;
        try
        {
            _map.CanvasAlphaMode = mode == "immersive-ar" ? "premultiplied" : "opaque";
            _bridge = new OffscreenCanvas(1024, 1024);
            _world = new RenderWorkerService(_bridge, 1024, 1024, _js, _gpu, _map, _hud) { ManualFrames = true };
            await _world.StartAsync(serverId, cameraPosition.X, cameraPosition.Y, cameraPosition.Z, pitchDegrees, yawDegrees);
            if (serverTicks >= 0) await _world.SetTimeOfDay(serverTicks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Xr] the world could not start: {ex.Message}");
            await _session.ExitAsync();
            throw;
        }
        finally { _worldStarting = false; }
        Console.WriteLine($"[Xr] world started around {cameraPosition:F1}");
    }

    public Task ExitAsync() => _session.ExitAsync();

    private void OnFrame(XrFrameData f)
    {
        if (_world == null || _worldStarting || _blit == null || _bridge == null || _session.Layer == null) return;
        _clock.Restart();

        _worldFromRoom ??= XrMath.WorldFromRoom(f.HeadPosition, f.HeadOrientation, _startPosition, _startForward);
        float dt = _lastTime < 0 ? 0f : (float)Math.Clamp((f.Time - _lastTime) / 1000.0, 0.0, 0.1);
        _lastTime = f.Time;
        _lastHeadOrientation = f.HeadOrientation;

        if (f.ExitButton && !_exitWasDown) { _session.RequestEnd(); }
        _exitWasDown = f.ExitButton;

        float speed = FlySpeed * (f.LeftGrip || f.RightGrip ? BoostFactor : 1f);
        _worldFromRoom = _loco.Step(_worldFromRoom.Value, f.HeadPosition, f.HeadOrientation, f.LeftStick, f.RightStick, dt, speed);

        // The head is the camera as far as the world's streaming and the creatures' culling go
        var headWorld = Vector3.Transform(f.HeadPosition, _worldFromRoom.Value);
        _world.Camera.Position = headWorld;
        _world.XrFrame(dt);
        if (!_world.WorldReady) return;

        foreach (var v in f.Views)
        {
            var eye = XrMath.EyeInWorld(v.RoomView, _worldFromRoom.Value);
            var view = ViewSetup.FromXr(eye, XrMath.EyeView(v.RoomView, _worldFromRoom.Value), v.Projection, _session.EnvironmentBlendMode is "alpha-blend" or "additive" ? 0f : 1f);
            int w = (int)v.Viewport.Width, h = (int)v.Viewport.Height;
            _world.XrRenderEye(in view, w, h);
            _blit.Blit(_bridge, _session.Layer.Framebuffer, v.Viewport);
        }

        _drawMsSum += _clock.Elapsed.TotalMilliseconds;
        if (++_framesDrawn % 120 == 0)
        {
            var vp = f.Views.Length > 0 ? f.Views[0].Viewport : null;
            Console.WriteLine($"[Xr] {_framesDrawn} frames; {_drawMsSum / 120:F1} ms of CPU a frame ({f.Views.Length} eyes of {vp?.Width}x{vp?.Height}), head at {headWorld:F1}; {_map.LastVoxelStats.DrawCalls} draws, {_map.LastVoxelStats.VisibleSections}/{_map.LastVoxelStats.SectionsWithQuads} sections, {_map.LastVoxelStats.Columns} columns, {_world.LoadedCount} loaded; {_world.Note}");
            _drawMsSum = 0;
        }
    }

    private void OnEnded()
    {
        var head = _worldFromRoom.HasValue && _world != null ? _world.Camera.Position : _startPosition;
        float yaw = _world?.Camera.Yaw ?? 0f, pitch = 0f;
        // Where the head was looking: its heading in the world, from the last frame's room (the room's turn is the locomotion's)
        if (_worldFromRoom is { } m)
        {
            var forward = Vector3.TransformNormal(Vector3.Transform(-Vector3.UnitZ, _lastHeadOrientation), m);
            (yaw, pitch) = XrMath.AnglesOf(forward);
        }
        try { _blit?.Dispose(); } catch { }
        _blit = null;
        var world = _world;
        _world = null;
        _bridge = null;
        if (world != null) _ = DisposeWorldAsync(world);
        _worldFromRoom = null;
        try { Ended?.Invoke(head, yaw, pitch); }
        catch (Exception ex) { Console.WriteLine($"[Xr] ended listener failed: {ex.Message}"); }
    }

    private static async Task DisposeWorldAsync(RenderWorkerService world)
    {
        try { await world.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Xr] the headset world did not dispose cleanly: {ex.Message}"); }
    }

    public void Dispose()
    {
        _session.Dispose();
        _blit?.Dispose();
    }
}
