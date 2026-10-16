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
    private bool _pointerOnPanel;
    // Augmented reality: the world is a model on the table (the sticks pan, zoom and turn it); "Life size" puts the person back in it
    private readonly XrTabletop _table = new();
    private bool _isAr, _tabletop, _tablePlaced;
    private float _panelBeamLength;
    private bool _rightThumbWas, _leftThumbWas, _rightTriggerWas, _leftTriggerWas, _rightAWas;

    /// <summary>Blocks a second at full thumbstick.</summary>
    public float FlySpeed { get; set; } = 8f;
    /// <summary>What a held grip multiplies the speed by.</summary>
    public float BoostFactor { get; set; } = 6f;

    private readonly AubsCraft.Admin.Voice.VoiceClient _voice;
    private string _serverId = "";
    private int _voiceTick;

    public XrViewerHost(SpawnJSRuntime js, GpuContextService gpu, MapRenderService map, ViewerHud hud, AubsCraft.Admin.Voice.VoiceClient voice)
    {
        _voice = voice;
        _js = js;
        _gpu = gpu;
        _map = map;
        _hud = hud;
        _session = new XrSession(js);
        _session.OnFrame += OnFrame;
        _session.OnEnded += OnEnded;
    }

    public bool IsActive => _session.IsActive;

    /// <summary>A line the user said from the headset (a quick phrase of the panel): the page sends it to the server's chat.</summary>
    public event Action<string>? ChatSent;
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
        _serverId = serverId;
        float yaw = yawDegrees * MathF.PI / 180f;
        _startForward = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        _worldFromRoom = null;
        _lastTime = -1;
        _exitWasDown = true;
        _loco.Reset();
        _isAr = mode == "immersive-ar";
        _tabletop = _isAr;
        _tablePlaced = false;
        _table.Focus = cameraPosition;
        _table.Yaw = 0f;
        _table.SetScale(1f / 32f);
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
        _hud.SetEditToolbar(false, false, false, false, false);   // in a headset: the Edit mode button
        _hud.OnPlayerAction = OnHudPlayerAction;
        _hud.OnTableAction = OnTableAction;
        _hud.OnMicToggle = () => _ = ToggleMicAsync();
        _voice.Where = () => _world != null ? _world.Camera.Position : Vector3.Zero;
        _hud.SetTableToolbar(_isAr, _tabletop);
        _panel.SetVisible(true);
        _xWasDown = true;
        _pressedOnPanel = _pointerWasOnPanel = false;
        _pendingTeleport = null;
        _panelGpu = new XrPanelRenderer();
        _panelGpu.Init(_map.Device!, _map.Queue!, _map.CanvasFormat, XrPanel.PixelWidth, XrPanel.PixelHeight);
        if (_world != null) _world.XrHudActive = true;
        _rightThumbWas = _leftThumbWas = _rightTriggerWas = _leftTriggerWas = _rightAWas = true;   // a button already down when the session starts does nothing
        _pointerOnPanel = false;
    }

    private void StopHudPanel()
    {
        _hud.HeadsetMode = false;
        // Whatever the controllers were doing in the headset is over: the page's HUD is not left in edit mode
        _hud.SetEditMode(false);
        _hud.SetEditToolbar(false, false, false, false, false);
        _hud.OpenPalette(false); _hud.OpenBlueprints(false); _hud.OpenNamePrompt(false); _hud.OpenWorldPanel(false); _hud.OpenPhrases(false);
        _hud.SetPointer(0, 0, false);
        _hud.OnPlayerAction = null;
        _hud.OnTableAction = null;
        _hud.OnMicToggle = null;
        _hud.SetSpeaking([]);
        _hud.SetTableToolbar(false, false);
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
        float distance = 0;
        if (f.RightHasRay && _panel.Hit(f.RightRayOrigin, f.RightRayDirection, out x, out y, out distance)) { hit = true; trigger = f.RightTrigger; }
        else if (f.LeftHasRay && _panel.Hit(f.LeftRayOrigin, f.LeftRayDirection, out x, out y, out distance)) { hit = true; trigger = f.LeftTrigger; }
        _panelBeamLength = distance;

        _hud.SetPointer(x, y, hit);
        _pointerOnPanel = hit;
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

        if (_tabletop)
        {
            if (!_tablePlaced) { _table.PlaceBeforeHead(f.HeadPosition, f.HeadOrientation); _tablePlaced = true; }
            // The left stick slides the world under the model, the right turns it (sideways) and makes it bigger or smaller (forward and back)
            var l = XrLocomotion.ApplyDeadzone(f.LeftStick);
            var rs = XrLocomotion.ApplyDeadzone(f.RightStick);
            _table.Pan(l.X * 0.5f * dt, l.Y * 0.5f * dt);
            _table.ZoomBy(MathF.Pow(1.8f, -rs.Y * dt));
            _table.Yaw -= rs.X * 1.2f * dt;
            if (_pendingTeleport is { } there) { _pendingTeleport = null; _table.Focus = there.Spot; }
            _worldFromRoom = _table.WorldFromRoom;
        }
        else
        {
            float speed = FlySpeed * (f.LeftGrip || f.RightGrip ? BoostFactor : 1f);
            // Pointing at a menu (the blocks, the blueprints), the right stick scrolls it instead of turning and rising
            bool scrolling = _pointerOnPanel && (_hud.IsPaletteOpen || _hud.IsBlueprintsOpen);
            if (scrolling) ScrollMenu(f.RightStick.Y, dt);
            _worldFromRoom = _loco.Step(_worldFromRoom.Value, f.HeadPosition, f.HeadOrientation, f.LeftStick, scrolling ? Vector2.Zero : f.RightStick, dt, speed);
            // "Go to" a player from the HUD: the room is put so the head is behind them, looking their way
            if (_pendingTeleport is { } go)
            {
                _pendingTeleport = null;
                float r = go.YawDegrees * MathF.PI / 180f;
                _worldFromRoom = XrMath.WorldFromRoom(f.HeadPosition, f.HeadOrientation, go.Spot, new Vector3(MathF.Cos(r), 0f, MathF.Sin(r)));
            }
        }
        // The head is the camera as far as the world's streaming and the creatures' culling go (and the minimap's heading)
        var headWorld = Vector3.Transform(f.HeadPosition, _worldFromRoom.Value);
        _world.Camera.Position = _tabletop ? _table.Focus : headWorld;
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
        if (!_panel.Visible) _pointerOnPanel = false;

        if (_voice.IsOn && ++_voiceTick % 3 == 0)
        {
            _voice.SetListener(headWorld, gaze);
            _hud.SetSpeaking(_voice.Speaking());
        }
        _hud.SetMicState(_voice.IsOn, _voice.MicOn);
        _world.XrFrame(dt);
        foreach (var line in _hud.TakeOutgoingChat()) ChatSent?.Invoke(line);
        if (!_world.WorldReady) return;
        UpdateEditing(f);
        if (_panel.Visible) DrawPanelTexture();

        var corners = _panel.Corners();
        foreach (var v in f.Views)
        {
            var eye = XrMath.EyeInWorld(v.RoomView, _worldFromRoom.Value);
            var view = ViewSetup.FromXr(eye, XrMath.EyeView(v.RoomView, _worldFromRoom.Value), v.Projection, _session.EnvironmentBlendMode is "alpha-blend" or "additive" ? 0f : 1f,
                _tabletop ? _table.Focus : null, _tabletop ? _table.ChunkRadius : 0);
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

    // A stick pushed on a menu the controller points at: lines scroll by, faster the harder it is pushed
    private float _scrollAccumulator;

    private void ScrollMenu(float stickY, float dt)
    {
        var v = XrLocomotion.ApplyDeadzone(new Vector2(0f, stickY)).Y;
        _scrollAccumulator += v * dt * 12f;
        // One wheel notch is three lines of the list; a notch each time the push has built up to one
        while (MathF.Abs(_scrollAccumulator) >= 1f)
        {
            _hud.Apply(new UiInputEvent(UiInputEvent.Wheel, 0, _scrollAccumulator > 0 ? 100 : -100));
            _scrollAccumulator -= MathF.Sign(_scrollAccumulator);
        }
    }
    // The Mic button: the voice starts if it has not (a headset's press is a gesture the browser accepts), then the microphone opens or closes
    private async Task ToggleMicAsync()
    {
        try
        {
            if (!_voice.IsOn)
            {
                var url = new UriBuilder(new Uri(_js.WindowThis?.Origin ?? "http://localhost")) { Path = "/api/live/voice/ws", Query = "server=" + Uri.EscapeDataString(_serverId) };
                url.Scheme = url.Scheme == "https" ? "wss" : "ws";
                if (!await _voice.StartAsync(url.Uri.ToString())) return;
            }
            await _voice.SetMicAsync(!_voice.MicOn);
        }
        catch (Exception ex) { Console.WriteLine("[Xr] voice: " + ex.Message); }
    }
    // The table's buttons (AR): life size or tabletop, bigger, smaller, bring the model here
    private void OnTableAction(string id)
    {
        switch (id)
        {
            case "table":
                if (_tabletop)
                {
                    // Step into the model: the head is where the focus is, looking the way the model faces
                    _tabletop = false;
                    _startPosition = _table.Focus;
                    _worldFromRoom = null;   // the next frame places the room so the head is at the focus (OnFrame's first-frame placement)
                    _loco.Reset();
                }
                else
                {
                    _tabletop = true;
                    _tablePlaced = false;
                    if (_world != null) _table.Focus = _world.Camera.Position;
                }
                _hud.SetTableToolbar(_isAr, _tabletop);
                break;
            case "bigger": _table.ZoomBy(1.5f); break;
            case "smaller": _table.ZoomBy(1f / 1.5f); break;
            case "bring": _tablePlaced = false; break;
        }
    }
    // ---- building with the controllers ----
    // The right thumbstick pressed in turns edit mode on and off. In edit mode the right controller points (a beam and a hand are drawn; the block it points at is outlined): the right trigger is the main
    // button (place a block, make a selection corner, put a blueprint down), A the other (break, clear the selection, stop placing), the left trigger copies the block pointed at, the left thumbstick
    // pressed undoes. Pointing at the HUD panel, the trigger is the panel's (UpdatePointer). Everything else - blocks, blueprints, fill, copy, paste - is a button of the panel's toolbar.
    private void UpdateEditing(XrFrameData f)
    {
        var edit = _world?.Edit;
        if (edit == null || _worldFromRoom is not { } toWorld) return;
        edit.RayOnly = true;
        edit.AutoName = true;

        if (f.RightThumb && !_rightThumbWas) _ = edit.SetActiveAsync(!edit.Active);
        _rightThumbWas = f.RightThumb;

        bool pointing = f.RightHasRay || f.LeftHasRay;
        bool useRight = f.RightHasRay;
        var originRoom = useRight ? f.RightRayOrigin : f.LeftRayOrigin;
        var directionRoom = useRight ? f.RightRayDirection : f.LeftRayDirection;
        var origin = Vector3.Transform(originRoom, toWorld);
        var direction = Vector3.Normalize(Vector3.TransformNormal(directionRoom, toWorld));

        if (!edit.Active)
        {
            edit.ClearRay();
            // The panel is pointed at: show the beam
            if (pointing && _pointerOnPanel) edit.AddController(origin, direction, _panelBeamLength * (_tabletop ? 1f / _table.Scale : 1f), new Vector3(0.3f, 0.6f, 1f), _tabletop ? 1f / _table.Scale : 1f);
            _rightTriggerWas = f.RightTrigger; _leftTriggerWas = f.LeftTrigger; _rightAWas = f.RightPrimary; _leftThumbWas = f.LeftThumb;
            return;
        }

        float unit = _tabletop ? 1f / _table.Scale : 1f;   // blocks to a metre of the room
        edit.ReachBlocks = _tabletop ? Math.Clamp(1.5f * unit, 8f, 256f) : 48f;
        if (pointing && !_pointerOnPanel) edit.SetRay(origin, direction); else edit.ClearRay();

        if (!_pointerOnPanel)
        {
            if (f.RightTrigger && !_rightTriggerWas) edit.Primary(_world!.Camera.Yaw);
            if (f.RightPrimary && !_rightAWas) edit.Alternate();
        }
        if (f.LeftTrigger && !_leftTriggerWas) edit.Eyedrop();
        if (f.LeftThumb && !_leftThumbWas) edit.UndoNow();
        _rightTriggerWas = f.RightTrigger; _leftTriggerWas = f.LeftTrigger; _rightAWas = f.RightPrimary; _leftThumbWas = f.LeftThumb;

        if (pointing)
        {
            float length = _pointerOnPanel ? _panelBeamLength * unit : edit.TargetDistance ?? 3f * unit;
            edit.AddController(origin, direction, length, _pointerOnPanel ? new Vector3(0.3f, 0.6f, 1f) : new Vector3(1f, 0.85f, 0.2f), unit);
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
