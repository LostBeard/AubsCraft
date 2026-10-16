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

    // The HUD, floating in front of the person: a panel (where it is), its texture and quad pipeline, and the controllers' pointer on it
    private readonly XrPanel _panel = new();
    private XrPanelRenderer? _panelGpu;
    private bool _xWasDown = true, _pressedOnPanel, _pointerWasOnPanel;
    private (Vector3 Spot, float YawDegrees)? _pendingTeleport;

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
            StartHudPanel();
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

    /// <summary>What SignalR delivered (chat, players, notices) for the headset's HUD: the page feeds the worker's HUD and, while a session runs, this one.</summary>
    public void FeedHud(HudFeed feed)
    {
        if (_world != null && _session.IsActive) _hud.Feed(feed);
    }

    /// <summary>The players online and the latest chat when the session began (the feed only carries what is new after it).</summary>
    public void SeedHud(string[]? players, (string sender, string text)[] chat)
    {
        if (_world == null || (players == null && chat.Length == 0)) return;
        _hud.Feed(new HudFeed(chat.Select(c => c.sender).ToArray(), chat.Select(c => c.text).ToArray(), players != null, players ?? [], [], []));
    }

    private void StartHudPanel()
    {
        _hud.Visible = true;
        _hud.HeadsetMode = true;
        _hud.SetViewport(XrPanel.PixelWidth, XrPanel.PixelHeight);
        _hud.OnPlayerAction = OnHudPlayerAction;
        _panel.SetVisible(true);
        _xWasDown = true;
        _pressedOnPanel = _pointerWasOnPanel = false;
        _pendingTeleport = null;
        _panelGpu = new XrPanelRenderer();
        _panelGpu.Init(_map.Device!, _map.Queue!, _map.CanvasFormat, XrPanel.PixelWidth, XrPanel.PixelHeight);
        if (_world != null) _world.XrHudActive = true;
    }

    private void StopHudPanel()
    {
        _hud.HeadsetMode = false;
        _hud.SetPointer(0, 0, false);
        _hud.OnPlayerAction = null;
        _panelGpu?.Dispose();
        _panelGpu = null;
    }

    // "Go to" in the player list's menu moves the room so the head is behind the player (the other actions are not offered in a headset)
    private void OnHudPlayerAction(string name, PlayerAction action)
    {
        if (_world == null || action != PlayerAction.GoTo) return;
        if (_world.TryPlaceBehindPlayer(name, out var spot, out var yaw)) _pendingTeleport = (spot, yaw);
    }

    // The controllers' rays on the panel: the pointer dot, hover, and the trigger as a click (on the minimap it cycles the zoom). The right hand wins where both point at it.
    private void UpdatePointer(XrFrameData f)
    {
        bool hit = false, trigger = false;
        float x = 0, y = 0;
        if (f.RightHasRay && _panel.Hit(f.RightRayOrigin, f.RightRayDirection, out x, out y, out _)) { hit = true; trigger = f.RightTrigger; }
        else if (f.LeftHasRay && _panel.Hit(f.LeftRayOrigin, f.LeftRayDirection, out x, out y, out _)) { hit = true; trigger = f.LeftTrigger; }

        _hud.SetPointer(x, y, hit);
        if (hit)
        {
            _hud.Apply(new UiInputEvent(UiInputEvent.MouseMove, x, y));
            if (trigger && !_pressedOnPanel)
            {
                _pressedOnPanel = true;
                bool onMinimap = _hud.OverMinimap(x, y);
                Console.WriteLine($"[Xr] HUD trigger at ({x:F0}, {y:F0}){(onMinimap ? " on the minimap" : "")}");
                if (onMinimap) _hud.CycleMinimapZoom();
                else _hud.Apply(new UiInputEvent(UiInputEvent.MouseDown, 0));
            }
        }
        else if (_pointerWasOnPanel)
        {
            _hud.Apply(new UiInputEvent(UiInputEvent.MouseMove, -100, -100));   // nothing hovers any more
        }
        if (!trigger && _pressedOnPanel)
        {
            _pressedOnPanel = false;
            _hud.Apply(new UiInputEvent(UiInputEvent.MouseUp, 0));
        }
        _pointerWasOnPanel = hit;
    }

    // The HUD, drawn into the panel's texture (once a frame, however many eyes look at it)
    private void DrawPanelTexture()
    {
        if (_panelGpu is not { IsReady: true } gpu || _map.Device is not { } device || _map.Queue is not { } queue) return;
        using var encoder = device.CreateCommandEncoder();
        gpu.Clear(encoder);
        _hud.Draw(encoder, gpu.View!, XrPanel.PixelWidth, XrPanel.PixelHeight);
        using var commands = encoder.Finish();
        queue.Submit(new[] { commands });
    }

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
        // "Go to" a player from the HUD: the room is put so the head is behind them, looking their way
        if (_pendingTeleport is { } go)
        {
            _pendingTeleport = null;
            float r = go.YawDegrees * MathF.PI / 180f;
            _worldFromRoom = XrMath.WorldFromRoom(f.HeadPosition, f.HeadOrientation, go.Spot, new Vector3(MathF.Cos(r), 0f, MathF.Sin(r)));
        }

        // The head is the camera as far as the world's streaming and the creatures' culling go (and the minimap's heading)
        var headWorld = Vector3.Transform(f.HeadPosition, _worldFromRoom.Value);
        _world.Camera.Position = headWorld;
        var gaze = Vector3.TransformNormal(Vector3.Transform(-Vector3.UnitZ, f.HeadOrientation), _worldFromRoom.Value);
        (_world.Camera.Yaw, _world.Camera.Pitch) = XrMath.AnglesOf(gaze);

        // X shows or hides the HUD panel (it comes back where the head is looking)
        if (f.LeftPrimary && !_xWasDown)
        {
            _panel.SetVisible(!_panel.Visible);
            Console.WriteLine($"[Xr] HUD panel {(_panel.Visible ? "shown" : "hidden")}");
        }
        _xWasDown = f.LeftPrimary;
        _world.XrHudActive = _panel.Visible;
        if (_panel.Visible)
        {
            _panel.Update(dt, f.HeadPosition, f.HeadOrientation);
            UpdatePointer(f);
        }
        else if (_pointerWasOnPanel || _pressedOnPanel)
        {
            _hud.SetPointer(0, 0, false);
            _pointerWasOnPanel = _pressedOnPanel = false;
        }

        _world.XrFrame(dt);
        if (!_world.WorldReady) return;
        if (_panel.Visible) DrawPanelTexture();

        var corners = _panel.Corners();
        foreach (var v in f.Views)
        {
            var eye = XrMath.EyeInWorld(v.RoomView, _worldFromRoom.Value);
            var view = ViewSetup.FromXr(eye, XrMath.EyeView(v.RoomView, _worldFromRoom.Value), v.Projection, _session.EnvironmentBlendMode is "alpha-blend" or "additive" ? 0f : 1f);
            int w = (int)v.Viewport.Width, h = (int)v.Viewport.Height;
            Action<GPUCommandEncoder, GPUTextureView>? overlay = null;
            if (_panel.Visible && _panelGpu is { IsReady: true } gpu)
            {
                var tl = XrPanelRenderer.ClipOf(corners.TopLeft, v.RoomView, v.Projection);
                var tr = XrPanelRenderer.ClipOf(corners.TopRight, v.RoomView, v.Projection);
                var bl = XrPanelRenderer.ClipOf(corners.BottomLeft, v.RoomView, v.Projection);
                var br = XrPanelRenderer.ClipOf(corners.BottomRight, v.RoomView, v.Projection);
                overlay = (encoder, target) => gpu.Draw(encoder, target, tl, tr, bl, br);
            }
            _world.XrRenderEye(in view, w, h, overlay);
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
        try { StopHudPanel(); } catch (Exception ex) { Console.WriteLine($"[Xr] the HUD panel did not stop cleanly: {ex.Message}"); }
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
