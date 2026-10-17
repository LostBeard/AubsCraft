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
    private bool _worldStarting;
    private int _framesDrawn;
    private double _drawMsSum;
    private double _cpuAverageMs;   // a moving average of what a frame costs the CPU: steers the draw distance (see FarWindow.NextXrDrawDistance)
    private const float DefaultDisplayRate = 72f;   // a Quest's usual, when the session does not say
    private readonly System.Diagnostics.Stopwatch _clock = new();

    // The HUD, in surfaces floating in the room (the menu, the window, the bar of blocks in hand, the feed): where each is, the one atlas texture they show parts of, and the controllers' pointer on them
    private readonly XrPanel _menuPanel = new(XrSurfaces.Menu), _windowPanel = new(XrSurfaces.Window), _barPanel = new(XrSurfaces.Bar), _feedPanel = new(XrSurfaces.Feed);
    private readonly XrPanel[] _surfaces;   // far to near: the order they are drawn in
    private XrPanelRenderer? _panelGpu;
    private bool _xWasDown = true, _yWasDown = true, _bWasDown = true, _pressedOnPanel, _pointerWasOnPanel;
    private XrPanel? _pointerSurface;
    // A surface carried by its handle, and by which hand
    private XrPanel? _grabSurface;
    private bool _grabRight;
    // Holding a trigger on the world keeps placing / breaking: when the next one is due
    private float _placeNext, _breakNext;
    private const float RepeatFirstSeconds = 0.45f, RepeatSeconds = 0.25f;
    private (Vector3 Spot, float YawDegrees)? _pendingTeleport;
    private bool _pointerOnPanel;
    // Augmented reality: the world is a model on the table (the sticks pan, zoom and turn it); "Life size" puts the person back in it
    private readonly XrTabletop _table = new();
    private bool _isAr, _tabletop, _tablePlaced;
    private float _panelBeamLength;
    private bool _rightThumbWas, _leftThumbWas, _rightTriggerWas, _leftTriggerWas, _rightAWas, _leftGripWas, _rightGripWas;

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
        _surfaces = [_feedPanel, _windowPanel, _barPanel, _menuPanel];
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
    public async Task EnterAsync(string mode, string serverId, Vector3 cameraPosition, float yawDegrees, float pitchDegrees, int serverTicks, bool isAdmin = false)
    {
        if (_session.IsActive) return;
        _startPosition = cameraPosition;
        _serverId = serverId;
        float yaw = yawDegrees * MathF.PI / 180f;
        _startForward = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        _worldFromRoom = null;
        _lastTime = -1;
        _loco.Reset();
        _isAr = mode == "immersive-ar";
        _tabletop = _isAr;
        _tablePlaced = false;
        _table.Focus = cameraPosition;
        _table.Yaw = 0f;
        _table.SetScale(XrTabletop.StartScale);
        _framesDrawn = 0;
        _drawMsSum = 0;
        _cpuAverageMs = 0;

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
            await _world.SetAdminAsync(isAdmin);
            // As QuestCraft's creative mode: an admin can build from the start (the first press of a trigger on the world places a block); it is undoable, and the menu turns it off
            if (isAdmin && _world.Edit is { } creative) _ = creative.SetActiveAsync(true);
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

    /// <summary>The server's time of day (ticks), as the status says every few seconds: the headset's sky follows it.</summary>
    public void SetServerTime(int ticks)
    {
        if (_world != null && _session.IsActive) _ = _world.SetTimeOfDay(ticks);
    }

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
        _hud.SetViewport(XrSurfaces.AtlasWidth, XrSurfaces.AtlasHeight);   // the layout of the atlas, before the headset's screen is made
        _hud.HeadsetMode = true;
        _hud.OnXrLeave = () => _session.RequestEnd();
        _hud.SetEditToolbar(false, false, false, false, false);   // in a headset: the Edit mode button
        _hud.OnPlayerAction = OnHudPlayerAction;
        _hud.OnTableAction = OnTableAction;
        _hud.OnMicToggle = () => _ = ToggleMicAsync();
        _hud.OnTurnToggle = () => { _loco.SmoothTurn = !_loco.SmoothTurn; _hud.SetTurnMode(_loco.SmoothTurn); Console.WriteLine($"[Xr] turning {(_loco.SmoothTurn ? "smoothly" : "in steps")}"); };
        _hud.SetTurnMode(_loco.SmoothTurn);
        _voice.Where = () => _world != null ? _world.Camera.Position : Vector3.Zero;
        _hud.SetTableToolbar(_isAr, _tabletop);
        foreach (var surface in _surfaces) surface.SetVisible(false);
        _xWasDown = _yWasDown = _bWasDown = true;
        _pressedOnPanel = _pointerWasOnPanel = false;
        _pendingTeleport = null;
        _panelGpu = new XrPanelRenderer();
        _panelGpu.Init(_map.Device!, _map.Queue!, _map.CanvasFormat, (int)(XrSurfaces.AtlasWidth * XrSurfaces.TextureScale), (int)(XrSurfaces.AtlasHeight * XrSurfaces.TextureScale));
        if (_world != null) _world.XrHudActive = false;
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
        _hud.OnTurnToggle = null;
        _hud.OnXrLeave = null;
        _hud.XrMenuOpen = false;
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

    // The nearest surface a ray meets: where (a pixel of the HUD atlas) and how far
    private bool NearestSurface(Vector3 origin, Vector3 direction, out XrPanel? surface, out float x, out float y, out float distance)
    {
        surface = null; x = y = 0; distance = float.MaxValue;
        foreach (var candidate in _surfaces)
        {
            if (candidate.Hit(origin, direction, out var sx, out var sy, out var d) && d < distance)
            {
                surface = candidate; x = sx + candidate.Spec.RegionX; y = sy + candidate.Spec.RegionY; distance = d;
            }
        }
        return surface != null;
    }

    // The controllers' rays on the surfaces: the pointer dot, hover, and the trigger as a click (on the minimap it cycles the zoom). The right hand wins where both point at one.
    private void UpdatePointer(XrFrameData f, float dt)
    {
        // Carrying a surface by its handle: it follows the hand until the trigger is let go (the stick pushes it away or draws it near)
        if (_grabSurface != null)
        {
            bool held = _grabRight ? f.RightTrigger : f.LeftTrigger;
            bool tracked = _grabRight ? f.RightHasRay : f.LeftHasRay;
            if (!held || !tracked)
            {
                _grabSurface.EndGrab();
                _grabSurface = null;
                _pressedOnPanel = false;
            }
            else
            {
                var stick = XrLocomotion.ApplyDeadzone(_grabRight ? f.RightStick : f.LeftStick);
                _grabSurface.UpdateGrab(_grabRight ? f.RightRayOrigin : f.LeftRayOrigin, _grabRight ? f.RightRayDirection : f.LeftRayDirection, f.HeadPosition, -stick.Y * 1.2f * dt);
                _hud.SetPointer(0, 0, false);
                _pointerOnPanel = true;
                _pointerWasOnPanel = true;
                return;
            }
        }
        bool hit = false, trigger = false, fromRight = false;
        float x = 0, y = 0;
        float distance = 0;
        XrPanel? surface = null;
        if (f.RightHasRay && NearestSurface(f.RightRayOrigin, f.RightRayDirection, out surface, out x, out y, out distance)) { hit = true; trigger = f.RightTrigger; fromRight = true; }
        else if (f.LeftHasRay && NearestSurface(f.LeftRayOrigin, f.LeftRayDirection, out surface, out x, out y, out distance)) { hit = true; trigger = f.LeftTrigger; }
        _panelBeamLength = hit ? distance : 0f;
        _pointerSurface = hit ? surface : null;

        _hud.SetPointer(x, y, hit);
        _pointerOnPanel = hit;
        if (hit && !_pointerWasOnPanel) _session.Pulse(fromRight, 0.12f, 10);   // the ray comes onto a surface: a tick
        if (hit)
        {
            _hud.Apply(new UiInputEvent(UiInputEvent.MouseMove, x, y));
            if (trigger && !_pressedOnPanel && surface!.Spec.GrabBand > 0 && y - surface.Spec.RegionY >= surface.PixelHeight - surface.Spec.GrabBand)
            {
                // The trigger on the handle at the bottom: take the surface along (nothing is pressed on the HUD)
                _pressedOnPanel = true;
                _grabSurface = surface;
                _grabRight = fromRight;
                _session.Pulse(fromRight, 0.35f, 30);
                surface.BeginGrab(fromRight ? f.RightRayOrigin : f.LeftRayOrigin, fromRight ? f.RightRayDirection : f.LeftRayDirection, distance);
                Console.WriteLine($"[Xr] taking the {surface.Spec.Name} by its handle");
                _pointerWasOnPanel = true;
                return;
            }
            if (trigger && !_pressedOnPanel)
            {
                _pressedOnPanel = true;
                _session.Pulse(fromRight, 0.45f, 28);   // pressed
                bool onMinimap = _hud.OverMinimap(x, y);
                Console.WriteLine($"[Xr] HUD trigger at ({x:F0}, {y:F0}) on the {surface!.Spec.Name}{(onMinimap ? " on the minimap" : "")}");
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
        _hud.Draw(encoder, gpu.View!, XrSurfaces.AtlasWidth, XrSurfaces.AtlasHeight);
        // The blocks' icons go on after the HUD (GameUI draws whole textures only)
        _hud.Icons?.Overlay(encoder, gpu.View!, _map.CanvasFormat, XrSurfaces.AtlasWidth, XrSurfaces.AtlasHeight, _hud.IconDraws);
        gpu.BuildMips(encoder);
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
            // Fast: the grips (while building they pick the block in hand, and a pressed-in stick is the fast one)
            bool fast = _world.Edit is { Active: true } ? f.LeftThumb || f.RightThumb : f.LeftGrip || f.RightGrip;
            float speed = FlySpeed * (fast ? BoostFactor : 1f);
            // Pointing at a menu (the blocks, the blueprints), the right stick scrolls it instead of turning and rising
            bool scrolling = _pointerOnPanel && _hud.XrListOpen;
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

        // X opens and closes the menu; Y the inventory (every block), as the creative inventory of QuestCraft
        if (f.LeftPrimary && !_xWasDown) { _hud.XrMenuOpen = !_hud.XrMenuOpen; Console.WriteLine($"[Xr] menu {(_hud.XrMenuOpen ? "opened" : "closed")}"); }
        _xWasDown = f.LeftPrimary;
        if (f.LeftSecondary && !_yWasDown) { _hud.ToggleXrInventory(); Console.WriteLine("[Xr] inventory toggled"); }
        _yWasDown = f.LeftSecondary;

        // Which surfaces are shown (the HUD knows what is open); each is put where the head looks when it appears
        _menuPanel.SetVisible(_hud.XrMenuOpen);
        _windowPanel.SetVisible(_hud.XrWindowOpen);
        _barPanel.SetVisible(_hud.XrBarVisible && f.LeftHasRay);   // an armband: it needs the hand
        _feedPanel.SetVisible(_hud.XrFeedVisible);
        bool anySurface = false;
        foreach (var surface in _surfaces)
        {
            if (!surface.Visible) continue;
            anySurface = true;
            if (surface.Spec.OverLeftHand) surface.PlaceOverHand(f.LeftRayOrigin, f.LeftRayDirection, f.HeadPosition);
            else surface.Update(dt, f.HeadPosition, f.HeadOrientation);
        }
        _world.XrHudActive = anySurface;
        if (anySurface) UpdatePointer(f, dt);
        else if (_pointerWasOnPanel || _pressedOnPanel)
        {
            _hud.SetPointer(0, 0, false);
            _pointerWasOnPanel = _pressedOnPanel = false;
        }
        if (!anySurface) { _pointerOnPanel = false; _pointerSurface = null; }
        if (_voice.IsOn && ++_voiceTick % 3 == 0)
        {
            _voice.SetListener(headWorld, gaze);
            _hud.SetSpeaking(_voice.Speaking());
        }
        _hud.SetMicState(_voice.IsOn, _voice.MicOn);
        _world.XrFrame(dt, (float)_cpuAverageMs, 1000f / (_session.DisplayRate ?? DefaultDisplayRate));
        foreach (var line in _hud.TakeOutgoingChat()) ChatSent?.Invoke(line);
        if (!_world.WorldReady) return;
        UpdateEditing(f, dt);
        if (anySurface) DrawPanelTexture();

        var shown = _surfaces.Where(surface => surface.Visible).ToArray();
        foreach (var v in f.Views)
        {
            var eye = XrMath.EyeInWorld(v.RoomView, _worldFromRoom.Value);
            var view = ViewSetup.FromXr(eye, XrMath.EyeView(v.RoomView, _worldFromRoom.Value), v.Projection, _session.EnvironmentBlendMode is "alpha-blend" or "additive" ? 0f : 1f,
                _tabletop ? _table.Focus : null, _tabletop ? _table.ChunkRadius : 0);
            int w = (int)v.Viewport.Width, h = (int)v.Viewport.Height;
            Action<GPUCommandEncoder, GPUTextureView>? overlay = null;
            if (shown.Length > 0 && _panelGpu is { IsReady: true } gpu)
            {
                // Each surface: its corners as this eye sees them, and its part of the atlas
                var quads = new (Vector4 Tl, Vector4 Tr, Vector4 Bl, Vector4 Br, Vector4 Region)[shown.Length];
                for (int i = 0; i < shown.Length; i++)
                {
                    var c = shown[i].Corners();
                    var spec = shown[i].Spec;
                    quads[i] = (XrPanelRenderer.ClipOf(c.TopLeft, v.RoomView, v.Projection), XrPanelRenderer.ClipOf(c.TopRight, v.RoomView, v.Projection),
                        XrPanelRenderer.ClipOf(c.BottomLeft, v.RoomView, v.Projection), XrPanelRenderer.ClipOf(c.BottomRight, v.RoomView, v.Projection),
                        new Vector4(spec.RegionX, spec.RegionY, spec.RegionWidth, spec.RegionHeight));
                }
                overlay = (encoder, target) =>
                {
                    for (int i = 0; i < quads.Length; i++) gpu.Draw(encoder, target, i, quads[i].Tl, quads[i].Tr, quads[i].Bl, quads[i].Br, quads[i].Region);
                };
            }
            _world.XrRenderEye(in view, w, h, overlay);
            _blit.Blit(_bridge, _session.Layer.Framebuffer, v.Viewport);
        }

        double spent = _clock.Elapsed.TotalMilliseconds;
        _cpuAverageMs = _cpuAverageMs <= 0 ? spent : _cpuAverageMs + 0.05 * (spent - _cpuAverageMs);
        _drawMsSum += spent;
        if (++_framesDrawn % 120 == 0)
        {
            var vp = f.Views.Length > 0 ? f.Views[0].Viewport : null;
            Console.WriteLine($"[Xr] {_framesDrawn} frames; {_drawMsSum / 120:F1} ms of CPU a frame ({f.Views.Length} eyes of {vp?.Width}x{vp?.Height}), head at {headWorld:F1}; draw distance {_map.DrawDistance}; {_map.LastVoxelStats.DrawCalls} draws, {_map.LastVoxelStats.VisibleSections}/{_map.LastVoxelStats.SectionsWithQuads} sections, {_map.LastVoxelStats.Columns} columns, {_world.LoadedCount} loaded; {_world.Note}");
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
    // Y turns building on and off (or the menu's Build tile). While building the right controller points (a beam is drawn; the block it points at is outlined): the right trigger places a block (held, it keeps
    // placing; with the Tools: makes a selection corner, puts a blueprint down), the left trigger breaks one (held, keeps breaking; with the Tools: clears the selection, stops placing), A picks the block pointed at into the hand, B undoes, the grips pick the next and the
    // previous block in hand (the bar of them is on the left arm; a pressed-in stick is the fast fly while building). Pointing at a surface of the HUD, the trigger is the surface's (UpdatePointer). Everything else - blocks, blueprints, fill, copy, paste - is a tile of the menu (X).
    private void UpdateEditing(XrFrameData f, float dt)
    {
        var edit = _world?.Edit;
        if (edit == null || _worldFromRoom is not { } toWorld) return;
        edit.RayOnly = true;
        edit.AutoName = true;

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
            _rightTriggerWas = f.RightTrigger; _leftTriggerWas = f.LeftTrigger; _rightAWas = f.RightPrimary; _leftThumbWas = f.LeftThumb; _rightThumbWas = f.RightThumb; _bWasDown = f.RightSecondary; _leftGripWas = f.LeftGrip; _rightGripWas = f.RightGrip;
            return;
        }

        float unit = _tabletop ? 1f / _table.Scale : 1f;   // blocks to a metre of the room
        edit.ReachBlocks = _tabletop ? Math.Clamp(1.5f * unit, 8f, 256f) : 48f;
        if (pointing && !_pointerOnPanel) edit.SetRay(origin, direction); else edit.ClearRay();

        if (!_pointerOnPanel)
        {
            // The right trigger places (held, it keeps placing), the left trigger breaks (held, it keeps breaking), A picks the block pointed at into the hand
            if (f.RightTrigger)
            {
                _placeNext -= dt;
                if (!_rightTriggerWas) { edit.Primary(_world!.Camera.Yaw); _placeNext = RepeatFirstSeconds; _session.Pulse(true, 0.5f, 30); }
                else if (_placeNext <= 0f && edit.CanRepeat) { edit.Primary(_world!.Camera.Yaw); _placeNext = RepeatSeconds; _session.Pulse(true, 0.35f, 20); }
            }
            if (f.LeftTrigger)
            {
                _breakNext -= dt;
                if (!_leftTriggerWas) { edit.Alternate(); _breakNext = RepeatFirstSeconds; _session.Pulse(false, 0.5f, 30); }
                else if (_breakNext <= 0f && edit.CanRepeat) { edit.Alternate(); _breakNext = RepeatSeconds; _session.Pulse(false, 0.35f, 20); }
            }
            if (f.RightPrimary && !_rightAWas) { edit.Eyedrop(); _session.Pulse(true, 0.3f, 25); }
        }
        if (f.RightSecondary && !_bWasDown) { edit.UndoNow(); _session.Pulse(true, 0.3f, 45); }
        // The grips pick the block in hand: the right one the next, the left one the one before (the bar is on the left arm)
        if (f.RightGrip && !_rightGripWas) { edit.CycleSlot(1); _session.Pulse(true, 0.25f, 15); }
        if (f.LeftGrip && !_leftGripWas) { edit.CycleSlot(-1); _session.Pulse(false, 0.25f, 15); }
        _rightTriggerWas = f.RightTrigger; _leftTriggerWas = f.LeftTrigger; _rightAWas = f.RightPrimary; _leftThumbWas = f.LeftThumb; _rightThumbWas = f.RightThumb; _bWasDown = f.RightSecondary; _leftGripWas = f.LeftGrip; _rightGripWas = f.RightGrip;

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
        _map.CpuSteered = false;   // the screen steers the draw distance by frame rate again
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
