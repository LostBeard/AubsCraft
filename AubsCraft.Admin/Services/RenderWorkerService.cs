using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.WebWorkers;
using AubsCraft.Admin.Rendering;
using AubsCraft.Live.Model;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using System.Numerics;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Render pipeline running in a dedicated Web Worker via SpawnDev.SpawnJS.WebWorkers.
/// Owns the OffscreenCanvas, WebGPU device, ILGPU accelerator, the world (<see cref="VoxelWorldRenderer"/>), the HUD and the render
/// loop. The main thread sends input and what SignalR delivered; this service does ALL GPU work, and the block data never enters
/// .NET: it goes from the fetched ArrayBuffer to a GPU buffer.
/// </summary>
public class RenderWorkerService : IRenderWorkerService
{
    private readonly SpawnJSRuntime _js;
    private readonly GpuContextService _gpu;
    private readonly MapRenderService _renderer;
    private readonly ViewerHud _hud;
    private OffscreenCanvas? _canvas;
    /// <summary>Draws the world with SpawnDev.VoxelEngine (created once the GPU is up).</summary>
    private VoxelWorldRenderer? _voxel;
    /// <summary>The creatures of the live feed and the socket that feeds them.</summary>
    private EntityLayer? _entities;
    private ParticleLayer? _particles;
    private LiveFeedClient? _liveFeed;
    /// <summary>Spreads the meshing of arriving columns over the frames, by how the frames are doing.</summary>
    private readonly IngestGovernor _ingest = new();
    private bool _initialized;
    private bool _disposed;
    public int LoadedCount { get; private set; }

    /// <summary>
    /// True for a world that is not drawn by a loop of its own: a headset's. Its owner calls <see cref="XrFrame"/> and <see cref="XrRenderEye"/> each frame of the session instead. Set before
    /// <see cref="StartAsync"/>.
    /// </summary>
    public bool ManualFrames { get; set; }
    private System.Diagnostics.Stopwatch _loadTimer = new();
    private float _chunksPerSecond;

    /// <summary>
    /// Constructor called via worker.New() with the transferred OffscreenCanvas.
    /// DI services injected via [FromServices] parameter attributes.
    /// Only canvas, width, height are passed explicitly in the New() expression.
    /// </summary>
    public RenderWorkerService(
        OffscreenCanvas canvas, int width, int height,
        [FromServices] SpawnJSRuntime? js = null,
        [FromServices] GpuContextService? gpu = null,
        [FromServices] MapRenderService? renderer = null,
        [FromServices] ViewerHud? hud = null)
    {
        _js = js!;
        _gpu = gpu!;
        _renderer = renderer!;
        _hud = hud!;
        _canvas = canvas;
        _canvas.Width = width;
        _canvas.Height = height;

        Console.WriteLine($"[RenderWorker] Created with {width}x{height} canvas");
    }

    /// <summary>
    /// Initialize GPU and start the render loop + chunk loading.
    /// </summary>
    public async Task StartAsync(string serverId, float camX, float camY, float camZ, float pitch, float yaw)
    {
        _serverQuery = "server=" + Uri.EscapeDataString(serverId);

        // Init ILGPU + WebGPU in the worker
        await _gpu.InitAsync();
        _renderer.InitOffscreen(_canvas!, _gpu.Accelerator!);
        _voxel = new VoxelWorldRenderer();
        _voxel.Init(_renderer.Device!, _renderer.Queue!, _renderer.CanvasFormat, _gpu.Accelerator!);
        _voxel.Pace = _ingest.WaitTurnAsync; // every mesh waits for its turn: loading must not take the frame rate with it
        _renderer.Voxel = _voxel;
        // The creatures of the live feed (animals, monsters, players): drawn in the world's pass, with the world's lighting and fog
        var pageOrigin = _js.DedicateWorkerThis?.Location.Origin ?? _js.WindowThis?.Origin ?? "";
        _entities = new EntityLayer(_js);
        _entities.Init(_renderer.Device!, _renderer.Queue!, _renderer.CanvasFormat, _voxel.FrameBuffer, pageOrigin);
        _voxel.Entities = _entities;
        // The smoke and flames of campfires, furnaces, fire and torches (the server says where; the GPU places every particle from the clock)
        _particles = new ParticleLayer(_js);
        _particles.Init(_renderer.Device!, _renderer.Queue!, _renderer.CanvasFormat, _voxel.FrameBuffer, pageOrigin);
        _voxel.Particles = _particles;
        // The HUD (GameUI) draws with the world's own device, into the same encoder, after the world pass.
        _hud.Init(_renderer.Device!, _renderer.Queue!, _renderer.CanvasFormat, _canvas!.Width, _canvas.Height);
        _hud.OnPlayerAction = OnPlayerAction;
        _renderer.OnOverlay = (encoder, view, w, h) =>
        {
            _hud.SetNameTags(_entities?.Tags);
            _hud.Draw(encoder, view, w, h);
        };
        _initialized = true;
        Console.WriteLine($"[RenderWorker] Initialized: {_gpu.BackendName}");

        _renderer.Camera.Position = new Vector3(camX, camY, camZ);
        _renderer.Camera.Pitch = pitch;
        _renderer.Camera.Yaw = yaw;

        // Cache base URL for the column HTTP requests
        _baseUrl = _js.DedicateWorkerThis?.Location.Origin
            ?? _js.WindowThis?.Origin ?? "";

        // Hook the render loop to load columns near the camera
        _renderer.OnUpdate = OnRenderFrame;
        if (!ManualFrames) _renderer.StartRenderLoop();
        _loadTimer.Start();

        // Load the block assets (textures, animations, what each block looks like) in the worker - fetched directly, the pixels stay in JS.
        // The block names wait for them: a block's class and material come from them
        _assetsLoad = LoadAssetsAsync();
        _ = LoadEntityAssetsAsync();
        _ = LoadParticleAssetsAsync();

        // Create JS data worker and connect via MessageChannel
        // Render worker creates and controls the data worker (clean hierarchy)
        _dataChannel = new MessageChannel();
        _dataPort = _dataChannel.Port1;
        _dataPort.OnMessage += OnDataWorkerMessage;
        _dataPort.Start();

        var location = _js.Get<Location>("location");
        var protocol = location.Protocol == "https:" ? "wss:" : "ws:";
        var host = location.Host;
        location.Dispose();
        // Heightmaps in global block ids (no palette): the bytes go to the GPU as they arrive
        var wsUrl = $"{protocol}//{host}/api/world/ws?{_serverQuery}&format=ids";

        // The live feed (what the AubsCraft Live plugin on the Minecraft server sees: every animal, monster, player). Quiet when the server has no plugin.
        _liveFeed = new LiveFeedClient($"{protocol}//{host}/api/live/ws?{_serverQuery}", _entities.OnFrame, m => Console.WriteLine("[Live] " + m));
        _liveFeed.Start();

        // Use absolute URL for worker script (relative paths may not resolve in nested worker context)
        var origin = _js.Get<string>("location.origin");
        _dataWorker = new Worker($"{origin}/data-worker.js");
        Console.WriteLine($"[RenderWorker] Creating data worker: {origin}/data-worker.js");
        _dataWorker.PostMessage(new
        {
            type = "init",
            renderPort = _dataChannel.Port2,
            wsUrl
        }, new object[] { _dataChannel.Port2 });
        // The data worker (and the server behind its WebSocket) streams nearest-the-camera first
        PostCameraToDataWorker();

        // Signal ready for chunks (GPU is initialized)
        _dataPort.PostMessage(new { type = "ready" });
        Console.WriteLine("[RenderWorker] Data worker created, GPU ready, requesting first chunk");
    }

    public Task ResizeAsync(int width, int height)
    {
        if (_canvas != null)
        {
            _canvas.Width = width;
            _canvas.Height = height;
            _renderer.HandleResize(width, height);
            _hud.SetViewport(width, height);
        }
        return Task.CompletedTask;
    }

    public Task PushUiInputAsync(UiInputEvent[] events)
    {
        if (!_initialized) return Task.CompletedTask;
        foreach (var e in events)
            _hud.Apply(e);
        return Task.CompletedTask;
    }

    public Task PushHudFeedAsync(HudFeed feed)
    {
        if (_initialized) _hud.Feed(feed);
        return Task.CompletedTask;
    }

    public Task<string[]> TakeOutgoingChatAsync() => Task.FromResult(_hud.TakeOutgoingChat());

    public Task<float[]> GetHudClickRectsAsync() => Task.FromResult(_initialized ? _hud.ClickRects() : []);

    // ---- going to a player and following one (the player list's menu) ----

    private string? _followName;
    private FollowMode _followMode;
    private float _followMissing;
    private const float FollowGraceSeconds = 2.5f;
    private static readonly string[] MoveKeys = ["KeyW", "KeyA", "KeyS", "KeyD", "Space", "ShiftLeft"];

    private void OnPlayerAction(string name, PlayerAction action)
    {
        if (_entities == null) return;
        if (action == PlayerAction.StopFollowing) { StopFollowing(null); return; }
        switch (_entities.LookupPlayer(name, out var view))
        {
            case PlayerLookup.Unknown:
                _hud.Toast($"{name} is not in the live view yet (it needs the Live view plugin on the server, and the player in a loaded area)", NotificationType.Warning);
                return;
            case PlayerLookup.OtherWorld:
                _hud.Toast($"{name} is in another dimension; the viewer shows the main world", NotificationType.Warning);
                return;
        }
        var camera = _renderer.Camera;
        if (action == PlayerAction.GoTo)
        {
            StopFollowing(null);
            var c = FollowCamera.Behind(view);
            camera.Position = new Vector3(c.X, c.Y, c.Z);
            camera.Yaw = c.Yaw;
            camera.Pitch = c.Pitch;
            return;
        }
        _followName = name;
        _followMode = action == PlayerAction.Eyes ? FollowMode.Eyes : FollowMode.Chase;
        _followMissing = 0;
        // Start behind the player, as a teleport would, then the mouse orbits from there
        camera.Yaw = FollowCamera.CameraYawOf(view.Yaw);
        camera.Pitch = FollowCamera.DefaultPitch;
        _hud.SetFollowing(name, _followMode);
        UpdateFollow(0);
    }

    private void StopFollowing(string? why)
    {
        if (_followName == null) return;
        if (why != null) _hud.Toast(why, NotificationType.Info);
        _followName = null;
        _hud.SetFollowing(null);
    }

    /// <summary>Once per frame, before anything reads the camera: put it where the followed player is.</summary>
    private void UpdateFollow(float dt)
    {
        if (_followName == null || _entities == null) return;
        if (_entities.LookupPlayer(_followName, out var view) != PlayerLookup.Found)
        {
            // A missing frame or two is nothing; gone for good is the player leaving (or going through a portal)
            _followMissing += dt;
            if (_followMissing > FollowGraceSeconds) StopFollowing($"Stopped following {_followName}: they are no longer in the view");
            return;
        }
        _followMissing = 0;
        var camera = _renderer.Camera;
        var c = FollowCamera.Place(view, _followMode, camera.Yaw, camera.Pitch, FollowCamera.DefaultDistance);
        camera.Position = new Vector3(c.X, c.Y, c.Z);
        camera.Yaw = c.Yaw;
        camera.Pitch = c.Pitch;
    }

    public Task SetPausedAsync(bool paused)
    {
        if (!_initialized) return Task.CompletedTask;
        if (paused) _renderer.StopRenderLoop(); else if (!ManualFrames) _renderer.StartRenderLoop();
        return Task.CompletedTask;
    }

    public Task SetCameraAsync(float x, float y, float z, float pitch, float yaw)
    {
        if (!_initialized) return Task.CompletedTask;
        _renderer.Camera.Position = new System.Numerics.Vector3(x, y, z);
        _renderer.Camera.Pitch = pitch;
        _renderer.Camera.Yaw = yaw;
        return Task.CompletedTask;
    }

    public Task SetPointerLockedAsync(bool locked)
    {
        if (_initialized) _hud.SetPointerLocked(locked);
        return Task.CompletedTask;
    }

    public Task SetHudVisibleAsync(bool visible)
    {
        _hud.Visible = visible;
        return Task.CompletedTask;
    }

    public Task SetDrawDistanceAsync(int chunks)
    {
        _renderer.FixDrawDistance(chunks);
        return Task.CompletedTask;
    }

    private readonly HashSet<string> _inputKeysReuse = new();

    /// <summary>Process keyboard/mouse input from the main thread.</summary>
    public Task ProcessInputAsync(float dx, float dy, float dt, string[] keysDown)
    {
        if (!_initialized) return Task.CompletedTask;
        if (dx != 0 || dy != 0)
            _renderer.Camera.ProcessMouseMovement(dx, dy);
        _inputKeysReuse.Clear();
        foreach (var k in keysDown) _inputKeysReuse.Add(k);
        // Moving yourself ends a follow (the keys then move the camera from where it is)
        if (_followName != null && MoveKeys.Any(_inputKeysReuse.Contains)) StopFollowing($"Stopped following {_followName}");
        if (_followName == null) _renderer.Camera.ProcessKeyboard(_inputKeysReuse, dt);
        return Task.CompletedTask;
    }

    /// <summary>Get current stats for the main thread to display.</summary>
    public Task<RenderStats> GetStatsAsync()
    {
        // Calculate chunks/sec over the last stats interval
        if (_loadTimer.IsRunning && _loadTimer.ElapsedMilliseconds > 0)
        {
            float elapsed = _loadTimer.ElapsedMilliseconds / 1000f;
            _chunksPerSecond = LoadedCount / elapsed;
        }

        return Task.FromResult(new RenderStats(
            _renderer.Fps,
            _renderer.VisibleChunkCount,
            _renderer.TotalChunkCount,
            _renderer.VisibleVertices,
            _renderer.TotalVertices,
            _renderer.DrawDistance,
            LoadedCount,
            _chunksPerSecond,
            _renderer.Camera.Position.X,
            _renderer.Camera.Position.Y,
            _renderer.Camera.Position.Z,
            _renderer.Camera.Pitch,
            _renderer.Camera.Yaw
        ));
    }

    public Task AttachCanvasAsync(OffscreenCanvas canvas, int width, int height)
    {
        _canvas = canvas;
        _canvas.Width = width;
        _canvas.Height = height;
        _renderer.AttachCanvas(_canvas);
        _hud.SetViewport(width, height);
        _renderer.OnUpdate = OnRenderFrame;
        _renderer.StartRenderLoop();
        Console.WriteLine($"[RenderWorker] Canvas re-attached: {width}x{height}, columns already loaded: {LoadedCount}");
        return Task.CompletedTask;
    }

    public Task DetachCanvasAsync()
    {
        _renderer.DetachCanvas();
        _canvas?.Dispose();
        _canvas = null;
        Console.WriteLine("[RenderWorker] Canvas detached, worker stays alive");
        return Task.CompletedTask;
    }

    private MessagePort? _dataPort;
    private MessageChannel? _dataChannel;
    private Worker? _dataWorker;

    class HeightMapMessage
    {
        public int Cx { get; set; }
        public int Cz { get; set; }
        public ArrayBuffer Buffer { get; set; } = null!;
    }

    private void OnDataWorkerMessage(MessageEvent msg)
    {
        try
        {
            // JS data worker posts: { type: "heightmap", cx, cz, buffer }
            using var data = msg.GetData<SpawnJSObject>();
            if (data == null) return;
            var type = data.JSRef!.Get<string>("type");
            if (type == "heightmap")
            {
                var chunk = data.JSRefCopy<HeightMapMessage>();
                _ = ProcessDataWorkerChunkAsync(chunk.Cx, chunk.Cz, chunk.Buffer);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RenderWorker] Data port message error: {ex.Message}");
        }
        finally
        {
            msg.Dispose();
        }
    }

    // The frame is [cx][cz][registry count] + the 3072-byte heightmap in global ids (server HeightmapWire). It says the chunk exists
    // (a full column may be fetched) and is the far tier's mesh input.
    private async Task ProcessDataWorkerChunkAsync(int cx, int cz, ArrayBuffer buffer)
    {
        try
        {
            _populatedChunks.Add((cx, cz));
            // A heightmap arriving inside the full-3D radius makes that chunk loadable in 3D; the render loop picks it up (the
            // camera may never change chunk on its own).
            if (IsInFullRadius(cx, cz) && !_voxel!.HasColumn(cx, cz)) _fullDirty = true;

            int registryCount;
            using (var header = new Int32Array(buffer, 0, 3)) registryCount = header[2];
            if (registryCount > _voxel!.NamesKnown) await RefreshVoxelBlockNamesAsync(registryCount);
            // Biomes of the chunk's 4 x 4 block cells, 16 bytes after the heightmap (an older server's frame has none: plains)
            if (buffer.ByteLength >= 12 + 3072 + 16)
            {
                using var cells = new Uint8Array(buffer, 12 + 3072, 16);
                var ids = new byte[16];
                for (int i = 0; i < 16; i++) ids[i] = cells[i];
                _voxel.SetBiomeCells(cx, cz, ids);
            }
            if (_voxel.NamesKnown > 0 && !_voxel.HasFarChunk(cx, cz))
            {
                using var heightmap = new Uint8Array(buffer, 12, 3072);
                if (!await _voxel.AddFarChunkAsync(cx, cz, heightmap) && _farDropLogged++ == 0)
                    Console.WriteLine("[RenderWorker] Far chunk dropped: no free section slots");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RenderWorker] Chunk ({cx},{cz}) process error: {ex.Message}");
        }
        finally
        {
            buffer.Dispose();
        }

        // Request next chunk (pull-based flow control)
        _dataPort?.PostMessage(new { type = "ready" });
    }

    public Task SetTimeOfDay(int ticks)
    {
        if (ticks >= 0)
            // Minecraft tick 0 is 06:00 (sunrise), 6000 noon, 18000 midnight; the renderer's TimeOfDay is 0 = midnight,
            // 0.5 = noon. Without the +6000 the server's noon rendered as sunrise (sun on the horizon, dark terrain).
            _renderer.TimeOfDay = ((ticks + 6000) % 24000) / 24000f;
        return Task.CompletedTask;
    }

    private Task? _assetsLoad;

    // mc-blocks.json + mc-atlas.rgba, generated at build time from the game's client jar (AubsCraft.AssetTool)
    private async Task LoadAssetsAsync()
    {
        try
        {
            var origin = _js.DedicateWorkerThis?.Location.Origin
                ?? _js.WindowThis?.Origin
                ?? "";
            using var jsonResponse = await _js.Fetch($"{origin}/assets/mc-blocks.json", new FetchOptions { Credentials = "include" });
            if (!jsonResponse.Ok)
            {
                Console.WriteLine($"[RenderWorker] Block assets HTTP {jsonResponse.Status}: every block will be its flat colour");
                return;
            }
            var assets = System.Text.Json.JsonSerializer.Deserialize(await jsonResponse.Text(), AubsCraft.Assets.Model.BlockAssetsJson.Default.BlockAssets);
            if (assets == null || assets.FormatVersion != AubsCraft.Assets.Model.BlockAssets.CurrentFormatVersion)
            {
                Console.WriteLine($"[RenderWorker] Block assets are format {assets?.FormatVersion}, expected {AubsCraft.Assets.Model.BlockAssets.CurrentFormatVersion}");
                return;
            }

            using var atlasResponse = await _js.Fetch($"{origin}/assets/mc-atlas.rgba", new FetchOptions { Credentials = "include" });
            if (!atlasResponse.Ok)
            {
                Console.WriteLine($"[RenderWorker] Block atlas HTTP {atlasResponse.Status}");
                return;
            }
            // The pixels go from the fetched ArrayBuffer to the GPU texture without entering .NET
            using var buffer = await atlasResponse.ArrayBuffer();
            using var pixels = new Uint8Array(buffer);
            if (pixels.Length != assets.AtlasWidth * assets.AtlasHeight * 4)
            {
                Console.WriteLine($"[RenderWorker] Block atlas is {pixels.Length} bytes, the assets describe {assets.AtlasWidth}x{assets.AtlasHeight}");
                return;
            }
            await LoadBiomeAssetsAsync(origin);
            _voxel!.SetBlockAssets(assets, pixels);
            Console.WriteLine($"[RenderWorker] Block assets for Minecraft {assets.GameVersion}: {assets.Blocks.Count} blocks, {assets.TileCount} tiles, {assets.Animations.Count} animated");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RenderWorker] Block assets load error: {ex.Message}");
        }
    }

    // What the biomes do to grass, leaves and water (a small file: colours per biome id); without it every biome is plains
    private async Task LoadBiomeAssetsAsync(string origin)
    {
        try
        {
            using var response = await _js.Fetch($"{origin}/assets/mc-biomes.json", new FetchOptions { Credentials = "include" });
            if (!response.Ok)
            {
                Console.WriteLine($"[RenderWorker] Biome assets HTTP {response.Status}: every biome is plains");
                return;
            }
            var biomes = System.Text.Json.JsonSerializer.Deserialize(await response.Text(), AubsCraft.Assets.Model.BiomeAssetsJson.Default.BiomeAssets);
            if (biomes == null) return;
            _voxel!.SetBiomeAssets(biomes);
            Console.WriteLine($"[RenderWorker] Biome colours: {biomes.Names.Length} biomes");
        }
        catch (Exception ex) { Console.WriteLine($"[RenderWorker] Biome assets load error: {ex.Message}"); }
    }

    private async Task LoadParticleAssetsAsync()
    {
        try { await _particles!.LoadAssetsAsync(_renderer.Device!, _renderer.Queue!); }
        catch (Exception ex) { Console.WriteLine($"[Particles] Particle assets load error: {ex.Message}"); }
    }

    private async Task LoadEntityAssetsAsync()
    {
        try { await _entities!.LoadAssetsAsync(_renderer.Device!, _renderer.Queue!); }
        catch (Exception ex) { Console.WriteLine($"[Live] Entity assets load error: {ex.Message}"); }
    }

    private readonly HashSet<(int, int)> _populatedChunks = new();
    private int _lastFullCX = int.MinValue, _lastFullCZ = int.MinValue;
    private bool _loadingFull;
    private bool _fullDirty;
    // Full-detail columns are loaded within this radius (chunks). The far tier (heightmaps from the WebSocket) covers the rest.
    private const int FullRenderRadius = 16;

    private string? _baseUrl;
    /// <summary>"server={id}" for every world request: the server whose world this worker renders.</summary>
    private string _serverQuery = "";

    private int _lastStreamCX = int.MinValue, _lastStreamCZ = int.MinValue;

    /// <summary>
    /// Tells the data worker where the camera is. It re-sorts its queue and forwards the position
    /// to the server, which re-sorts what it streams. Without this, heightmaps stream outward from
    /// (0, 0) no matter where the camera is or goes.
    /// </summary>
    private void PostCameraToDataWorker()
    {
        if (_dataWorker == null) return;
        var (camCX, camCZ) = CameraChunk();
        _lastStreamCX = camCX;
        _lastStreamCZ = camCZ;
        _dataWorker.PostMessage(new { type = "camera", x = _renderer.Camera.Position.X, z = _renderer.Camera.Position.Z });
    }

    private float _minimapTimer;

    // The voxel renderer's own numbers for the F3 overlay: where a frame's CPU time goes (browser calls, not the GPU)
    private string VoxelNote()
    {
        var s = _renderer.LastVoxelStats;
        return $"Voxel: {s.DrawCalls} draws, {s.BufferBinds} binds, record {s.RecordMs:F1} ms, frame {_renderer.LastVoxelFrameMs:F1} ms; far chunks {_voxel!.FarMeshedCount} meshed of {_voxel.FarChunkCount}; "
             + $"{_voxel.ArenaBlocks} quad buffers; creatures {_entities?.EntityCount ?? 0} drawn of {_entities?.Known ?? 0} ({_entities?.PartCount ?? 0} parts, {_liveFeed?.Frames ?? 0} frames); loading {_ingest.TurnsPerFrame:F1} meshes/frame, {_ingest.Waiting} waiting, {_voxel.StaleCount} to re-mesh";
    }

    // ---- a headset: this service run on the page's own thread, with no loop and no HUD (WebXR cannot be used from a worker) ----

    /// <summary>The canvas the eyes are drawn into one after another (copied on to the headset's layer between): null when the service has none.</summary>
    public OffscreenCanvas? Canvas => _canvas;

    /// <summary>The camera, as the headset's head: where the world streams around and what the live creatures are culled for.</summary>
    public FpsCamera Camera => _renderer.Camera;

    /// <summary>The world's own numbers (draws, quad buffers, how loading is going), one line, for a log.</summary>
    public string Note => _voxel == null ? "" : VoxelNote();

    /// <summary>How many columns are in.</summary>
    public bool WorldReady => _initialized && LoadedCount > 0;

    /// <summary>One frame of a headset: stream the world around where the head is (set <see cref="Camera"/>'s position first).</summary>
    public void XrFrame(float dt)
    {
        if (!_initialized) return;
        _renderer.TrackFrame(dt);
        StreamWorld(dt);
    }

    /// <summary>Draw one eye into <see cref="Canvas"/> (resized to the eye's size first).</summary>
    public VoxelFrameStats? XrRenderEye(in ViewSetup view, int width, int height)
    {
        if (!_initialized || _canvas == null) return null;
        if (_canvas.Width != width || _canvas.Height != height) { _canvas.Width = width; _canvas.Height = height; _renderer.HandleResize(width, height); }
        return _renderer.RenderView(in view, width, height);
    }

    private void OnRenderFrame(float dt)
    {
        _ingest.OnFrame(dt * 1000.0);
        UpdateFollow(dt);

        // The minimap is drawn into its own texture before the frame (a few times a second: it is a map, not a view)
        if (_voxel != null && _hud.Visible)
        {
            _minimapTimer += dt;
            if (_minimapTimer >= 0.25f || _voxel.MinimapView == null)
            {
                _minimapTimer = 0;
                _voxel.RenderMinimap(_renderer.Camera.Position.X, _renderer.Camera.Position.Z, _renderer.Camera.Yaw, _hud.MinimapExtent);
            }
            _hud.SetMinimapTexture(_voxel.MinimapView);
        }

        // HUD state first: it is drawn later in this same frame, after the world pass (MapRenderService.OnOverlay)
        _hud.Update(dt, new HudData(
            _renderer.Fps, _renderer.Camera.Position.X, _renderer.Camera.Position.Y, _renderer.Camera.Position.Z,
            _renderer.Camera.Yaw,
            _renderer.VisibleChunkCount, _renderer.TotalChunkCount, _renderer.VisibleVertices, _renderer.TotalVertices,
            _renderer.DrawDistance, LoadedCount, _chunksPerSecond, _renderer.TimeOfDay, _gpu.BackendName ?? "?",
            _voxel == null ? "" : VoxelNote()));

        StreamWorld(dt, ingest: false);
    }

    // The world around the camera: far chunks, the column stream, the data worker's idea of where we are
    private void StreamWorld(float dt, bool ingest = true)
    {
        if (ingest) _ingest.OnFrame(dt * 1000.0);
        // Trigger full 3D loading when camera moves to a new chunk
        var (camCX, camCZ) = CameraChunk();
        _voxel?.UpdateFarWindow(camCX, camCZ, _renderer.DrawDistance);
        if (camCX != _lastStreamCX || camCZ != _lastStreamCZ) PostCameraToDataWorker();
        if (!_loadingFull && (_fullDirty || camCX != _lastFullCX || camCZ != _lastFullCZ))
        {
            _lastFullCX = camCX;
            _lastFullCZ = camCZ;
            _fullDirty = false;
            _ = LoadVoxelColumnsNearbyAsync();
        }
    }

    private (int cx, int cz) CameraChunk()
        => ((int)MathF.Floor(_renderer.Camera.Position.X / 16f), (int)MathF.Floor(_renderer.Camera.Position.Z / 16f));

    private bool IsInFullRadius(int cx, int cz)
    {
        var (camCX, camCZ) = CameraChunk();
        int dx = cx - camCX, dz = cz - camCZ;
        return dx * dx + dz * dz <= FullRenderRadius * FullRenderRadius;
    }

    // ---- columns with the server's global block ids, meshed by SpawnDev.VoxelEngine ----

    /// <summary>Columns fetched at once: the round trip, not the GPU, is what a lone request waits for.</summary>
    private const int VoxelParallelFetches = 4;
    private readonly SemaphoreSlim _voxelNamesLock = new(1, 1);
    private int _farDropLogged;

    /// <summary>Loads columns nearest-first around the CURRENT camera until every populated chunk in the radius is in.</summary>
    private async Task LoadVoxelColumnsNearbyAsync()
    {
        _loadingFull = true;
        var attempted = new HashSet<(int, int)>(); // a column that fails is retried on the next pass, not this one
        try
        {
            if (_voxel!.NamesKnown == 0) await RefreshVoxelBlockNamesAsync();
            if (_voxel.NamesKnown == 0) return;

            while (!_disposed)
            {
                var (camCX, camCZ) = CameraChunk();
                EvictFarVoxelColumns(camCX, camCZ);
                var batch = NearestPendingVoxelColumns(camCX, camCZ, attempted, VoxelParallelFetches);
                if (batch.Count == 0) break;
                foreach (var c in batch) attempted.Add(c);
                await Task.WhenAll(batch.Select(c => LoadVoxelColumnAsync(c.cx, c.cz)));
                LoadedCount = _voxel.ColumnCount;
            }
        }
        finally
        {
            _loadingFull = false;
        }
    }

    private List<(int cx, int cz)> NearestPendingVoxelColumns(int camCX, int camCZ, HashSet<(int, int)> attempted, int count)
    {
        var candidates = new List<(int d, int cx, int cz)>();
        for (int dz = -FullRenderRadius; dz <= FullRenderRadius; dz++)
        for (int dx = -FullRenderRadius; dx <= FullRenderRadius; dx++)
        {
            int d = dx * dx + dz * dz;
            if (d > FullRenderRadius * FullRenderRadius) continue;
            var c = (camCX + dx, camCZ + dz);
            if (!_populatedChunks.Contains(c) || attempted.Contains(c) || _voxel!.HasColumn(c.Item1, c.Item2)) continue;
            candidates.Add((d, c.Item1, c.Item2));
        }
        candidates.Sort((a, b) => a.d.CompareTo(b.d));
        return candidates.Take(count).Select(c => (c.cx, c.cz)).ToList();
    }

    // Columns well outside the radius go, so a long flight does not run out of section slots or GPU memory.
    private void EvictFarVoxelColumns(int camCX, int camCZ)
    {
        int limit = (FullRenderRadius + 3) * (FullRenderRadius + 3);
        foreach (var (cx, cz) in _voxel!.ColumnKeys.ToArray())
        {
            int dx = cx - camCX, dz = cz - camCZ;
            if (dx * dx + dz * dz > limit) { _voxel.RemoveColumn(cx, cz); _particles?.RemoveColumn(cx, cz); }
        }
    }

    private async Task LoadVoxelColumnAsync(int cx, int cz)
    {
        try
        {
            using var response = await _js.Fetch($"{_baseUrl}/api/world/chunk-ids/{cx}/{cz}?{_serverQuery}",
                new FetchOptions { Credentials = "include" });
            if (!response.Ok)
            {
                Console.WriteLine($"[RenderWorker] Column ({cx},{cz}) HTTP {response.Status}");
                return;
            }

            // The server says how many block names it knows; a column can use ids we have no name for yet
            int registryCount = 0;
            string? emitterHeader, biomeHeader;
            using (var headers = response.Headers)
            {
                int.TryParse(headers.Get("X-Block-Registry-Count"), out registryCount);
                emitterHeader = headers.Get("X-Emitters");   // the blocks of this column that give off smoke and flames
                biomeHeader = headers.Get("X-Biomes");       // the biome of each 4 x 4 block cell: the colour of its grass, leaves and water
            }
            if (registryCount > _voxel!.NamesKnown) await RefreshVoxelBlockNamesAsync(registryCount);

            using var buffer = await response.ArrayBuffer();
            using var ids = new Uint8Array(buffer);
            if (!await _voxel.AddColumnAsync(cx, cz, ids))
                Console.WriteLine($"[RenderWorker] Column ({cx},{cz}) dropped: no free section slots");
            else
            {
                _particles?.SetColumn(cx, cz, AubsCraft.Live.Model.Particles.EmitterCodec.Decode(emitterHeader));
                if (!string.IsNullOrEmpty(biomeHeader))
                {
                    try { _voxel.SetBiomeCells(cx, cz, Convert.FromBase64String(biomeHeader)); }
                    catch (FormatException) { }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RenderWorker] Column ({cx},{cz}) failed: {ex.Message}");
        }
    }

    /// <summary>Fetch the block names (index = global id) and rebuild the class and material tables.</summary>
    private async Task RefreshVoxelBlockNamesAsync(int atLeast = 1)
    {
        if (_assetsLoad != null) await _assetsLoad; // the tables need the assets
        await _voxelNamesLock.WaitAsync();
        try
        {
            if (_voxel!.NamesKnown >= atLeast && _voxel.NamesKnown > 0) return; // another caller got there first
            using var response = await _js.Fetch($"{_baseUrl}/api/world/blocks", new FetchOptions { Credentials = "include" });
            if (!response.Ok)
            {
                Console.WriteLine($"[RenderWorker] Block names HTTP {response.Status}");
                return;
            }
            var names = System.Text.Json.JsonSerializer.Deserialize<string[]>(await response.Text());
            if (names == null || names.Length == 0) return;
            _voxel.SetBlockNames(names);
            Console.WriteLine($"[RenderWorker] {names.Length} block names");
            // No re-mesh of what is loaded: a chunk is only meshed after the names covering its ids arrived (its frame says how many
            // the server knew), and an id never changes meaning, so nothing already meshed can contain an id we had no name for.
        }
        finally
        {
            _voxelNamesLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        // Data worker handles WebSocket - no OnChunkReceived to unsubscribe
        _renderer.StopRenderLoop();
        _liveFeed?.Dispose();
        _voxel?.Dispose();
        _entities?.Dispose();
        _particles?.Dispose();
        _canvas?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Stats from the render worker for the main thread UI.</summary>
public record RenderStats(
    float Fps, int VisibleChunks, int TotalChunks,
    int VisibleVerts, int TotalVerts, int DrawDistance, int LoadedCount,
    float ChunksPerSecond,
    float CamX, float CamY, float CamZ, float CamPitch, float CamYaw);
