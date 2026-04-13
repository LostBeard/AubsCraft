using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.WebWorkers;
using AubsCraft.Admin.Rendering;
using System.Numerics;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Render pipeline running in a dedicated Web Worker via SpawnDev.BlazorJS.WebWorkers.
/// Owns the OffscreenCanvas, WebGPU device, ILGPU accelerator, and render loop.
/// Main thread sends input/camera updates, this service does ALL GPU work.
/// </summary>
public class RenderWorkerService : IRenderWorkerService
{
    private readonly BlazorJSRuntime _js;
    private readonly VoxelEngineService _engine;
    private readonly MapRenderService _renderer;
    private readonly WorldCacheService _cache;
    private readonly ChunkStreamService _chunkStream;
    private OffscreenCanvas? _canvas;
    private bool _initialized;
    private bool _disposed;
    public int LoadedCount { get; private set; }
    private System.Diagnostics.Stopwatch _loadTimer = new();
    private int _lastLoadCount;
    private float _chunksPerSecond;

    /// <summary>
    /// Constructor called via worker.New() with the transferred OffscreenCanvas.
    /// DI services injected via [FromServices] parameter attributes.
    /// Only canvas, width, height are passed explicitly in the New() expression.
    /// </summary>
    public RenderWorkerService(
        OffscreenCanvas canvas, int width, int height,
        [FromServices] BlazorJSRuntime? js = null,
        [FromServices] VoxelEngineService? engine = null,
        [FromServices] MapRenderService? renderer = null,
        [FromServices] WorldCacheService? cache = null,
        [FromServices] ChunkStreamService? chunkStream = null)
    {
        _js = js!;
        _engine = engine!;
        _renderer = renderer!;
        _cache = cache!;
        _chunkStream = chunkStream!;
        _canvas = canvas;
        _canvas.Width = width;
        _canvas.Height = height;

        Console.WriteLine($"[RenderWorker] Created with {width}x{height} canvas");
    }

    /// <summary>
    /// Initialize GPU and start the render loop + chunk loading.
    /// </summary>
    public async Task StartAsync(float camX, float camY, float camZ, float pitch, float yaw)
    {
        // Init ILGPU + WebGPU in the worker
        await _engine.InitAsync();
        _renderer.InitOffscreen(_canvas!, _engine.Accelerator!);
        _initialized = true;
        Console.WriteLine($"[RenderWorker] Initialized: {_engine.BackendName}");

        _renderer.Camera.Position = new Vector3(camX, camY, camZ);
        _renderer.Camera.Pitch = pitch;
        _renderer.Camera.Yaw = yaw;

        // Cache base URL for full chunk HTTP requests
        _baseUrl = _js.DedicateWorkerThis?.Location.Origin
            ?? _js.WindowThis?.Origin ?? "";

        // Hook render loop to trigger full 3D loading near camera
        _renderer.OnUpdate = OnRenderFrame;
        _renderer.OnChunksEvicted = OnChunksEvicted;
        _renderer.StartRenderLoop();

        // Load atlas in the worker - fetch directly, stays in JS
        _ = LoadAtlasAsync();

        // Start loading from OPFS cache
        _ = LoadCachedChunksAsync();

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
        var wsUrl = $"{protocol}//{host}/api/world/ws";

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

        // Signal ready for chunks (GPU is initialized)
        _dataPort.PostMessage(new { type = "ready" });
        Console.WriteLine("[RenderWorker] Data worker created, GPU ready, requesting first chunk");
    }

    /// <summary>
    /// Process keyboard/mouse input from the main thread.
    /// </summary>
    public Task ResizeAsync(int width, int height)
    {
        if (_canvas != null)
        {
            _canvas.Width = width;
            _canvas.Height = height;
            _renderer.HandleResize(width, height);
        }
        return Task.CompletedTask;
    }

    private readonly HashSet<string> _inputKeysReuse = new();

    public Task ProcessInputAsync(float dx, float dy, float dt, string[] keysDown)
    {
        if (!_initialized) return Task.CompletedTask;
        if (dx != 0 || dy != 0)
            _renderer.Camera.ProcessMouseMovement(dx, dy);
        _inputKeysReuse.Clear();
        foreach (var k in keysDown) _inputKeysReuse.Add(k);
        _renderer.Camera.ProcessKeyboard(_inputKeysReuse, dt);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Get current stats for the main thread to display.
    /// </summary>
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
        _renderer.OnUpdate = OnRenderFrame;
        _renderer.StartRenderLoop();
        Console.WriteLine($"[RenderWorker] Canvas re-attached: {width}x{height}, chunks already loaded: {LoadedCount}");
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
        public ArrayBuffer Buffer { get; set; }
    }

    private void OnDataWorkerMessage(MessageEvent msg)
    {
        try
        {
            // JS data worker posts: { type: "heightmap", cx, cz, buffer }
            using var data = msg.GetData<JSObject>();
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

    private async Task ProcessDataWorkerChunkAsync(int cx, int cz, ArrayBuffer buffer)
    {
        try
        {
            _populatedChunks.Add((cx, cz));
            if (!_loadedChunks.Contains((cx, cz)))
            {
                await RenderFromFrameAsync(buffer);
                LoadedCount++;
            }
            else
            {
                buffer.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RenderWorker] Chunk ({cx},{cz}) process error: {ex.Message}");
            buffer.Dispose();
        }

        // Request next chunk (pull-based flow control)
        _dataPort?.PostMessage(new { type = "ready" });
    }

    public Task SetTimeOfDay(int ticks)
    {
        if (ticks >= 0)
            _renderer.TimeOfDay = (ticks % 24000) / 24000f;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Upload chunk mesh data to the renderer (called when chunk data is ready).
    /// </summary>
    public void UploadChunkMesh(int cx, int cz, float[] vertices)
    {
        _renderer.UploadChunkMesh(cx, cz, vertices);
    }

    /// <summary>
    /// Upload water mesh data to the renderer.
    /// </summary>
    public void UploadWaterMesh(int cx, int cz, float[] vertices)
    {
        _renderer.UploadWaterMesh(cx, cz, vertices);
    }

    private async Task LoadAtlasAsync()
    {
        try
        {
            var origin = _js.DedicateWorkerThis?.Location.Origin
                ?? _js.WindowThis?.Origin
                ?? "";
            var atlasUrl = $"{origin}/atlas.rgba";
            Console.WriteLine($"[RenderWorker] Fetching atlas from: {atlasUrl}");

            using var response = await _js.Fetch(atlasUrl, new FetchOptions { Credentials = "include" });
            Console.WriteLine($"[RenderWorker] Atlas fetch status: {response.Status}");

            if (!response.Ok)
            {
                Console.WriteLine($"[RenderWorker] Atlas fetch failed: {response.StatusText}");
                return;
            }

            using var buffer = await response.ArrayBuffer();
            var rgba = buffer.ReadBytes();
            int size = (int)Math.Sqrt(rgba.Length / 4);
            Console.WriteLine($"[RenderWorker] Atlas data: {rgba.Length} bytes, {size}x{size}");
            _renderer.UploadAtlas(rgba, size, size);
            Console.WriteLine($"[RenderWorker] Atlas uploaded to GPU");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RenderWorker] Atlas load error: {ex.Message}");
        }
    }

    private HashSet<(int, int)> _populatedChunks = new();
    private HashSet<(int, int)> _loadedChunks = new();
    private HashSet<(int, int)> _fullChunks = new();
    private int _lastFullCX = int.MinValue, _lastFullCZ = int.MinValue;
    private bool _loadingFull;
    // Full chunk data loaded within this radius. LOD kernel reduces detail for distant chunks.
    // Heightmap from WebSocket covers beyond this radius.
    private const int FullRenderRadius = 16;

    /// <summary>
    /// Budget-aware LOD selection. Tiers shift based on vertex pressure.
    /// Returns 0 (full), 2 (LOD 2x2x2), or 4 (LOD 4x4x4).
    /// </summary>
    private int SelectLOD(int distSq)
    {
        int totalVerts = _renderer.TotalVertices;
        int budget = _renderer.VertexBudget;
        float pressure = (float)totalVerts / Math.Max(budget, 1);

        if (pressure > 0.9f)
        {
            // High pressure - aggressive LOD, only closest get full detail
            if (distSq <= 4) return 0;
            if (distSq <= 25) return 2;
            return 4;
        }
        else if (pressure > 0.6f)
        {
            // Medium pressure - normal tiers
            if (distSq <= 9) return 0;
            if (distSq <= 64) return 2;
            return 4;
        }
        else
        {
            // Under budget - more detail at distance
            if (distSq <= 25) return 0;
            if (distSq <= 100) return 2;
            return 4;
        }
    }
    private string? _baseUrl;



    private async Task LoadCachedChunksAsync()
    {
        _loadTimer.Start();

        // Load from OPFS cache if available. WebSocket streaming handled by data worker.
        var regionCount = await _cache.GetCachedRegionCountAsync();
        if (regionCount > 0)
        {
            Console.WriteLine($"[RenderWorker] Loading {regionCount} cached regions...");
            var cached = await _cache.LoadAllCachedHeightmapsAsync();

            // Sort by camera distance - nearest chunks load first
            int camCX = (int)MathF.Floor(_renderer.Camera.Position.X / 16f);
            int camCZ = (int)MathF.Floor(_renderer.Camera.Position.Z / 16f);
            cached.Sort((a, b) =>
            {
                int da = (a.cx - camCX) * (a.cx - camCX) + (a.cz - camCZ) * (a.cz - camCZ);
                int db = (b.cx - camCX) * (b.cx - camCX) + (b.cz - camCZ) * (b.cz - camCZ);
                return da.CompareTo(db);
            });

            // Load nearby heightmaps first (within full 3D radius + margin)
            // so the full 3D pass has populated chunks to work with
            int nearbyThreshold = (FullRenderRadius + 2) * (FullRenderRadius + 2);
            int batchCount = 0;
            int nearbyLoaded = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var (cx, cz, frame) in cached)
            {
                try
                {
                    using var jsFrame = new Uint8Array(frame);
                    await RenderFromFrameAsync(jsFrame.Buffer);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RenderWorker] Chunk ({cx},{cz}) error: {ex.Message}\n{ex.StackTrace}");
                }
                LoadedCount++;

                int dist = (cx - camCX) * (cx - camCX) + (cz - camCZ) * (cz - camCZ);
                if (dist <= nearbyThreshold) nearbyLoaded++;

                batchCount++;
                if (batchCount >= 10)
                {
                    batchCount = 0;
                    await Task.Delay(1); // yield to render loop
                }

                // After loading nearby heightmaps, kick off full 3D immediately
                // Don't wait for all distant heightmaps to finish
                if (nearbyLoaded >= 20 && !_loadingFull)
                {
                    _ = LoadFullChunksNearbyAsync(camCX, camCZ);
                }
            }
            sw.Stop();
            Console.WriteLine($"[RenderWorker] Loaded {LoadedCount} from cache in {sw.ElapsedMilliseconds}ms");
        }

        // Ensure full 3D starts even if cache had few nearby chunks
        if (!_loadingFull)
        {
            int camCX = (int)MathF.Floor(_renderer.Camera.Position.X / 16f);
            int camCZ = (int)MathF.Floor(_renderer.Camera.Position.Z / 16f);
            _ = LoadFullChunksNearbyAsync(camCX, camCZ);
        }

        // WebSocket streaming now handled by the JS data worker
        // Chunks arrive via MessagePort -> OnDataWorkerMessage -> ProcessDataWorkerChunkAsync
    }

    // Old WebSocket queue code removed - data worker handles WebSocket now

    /// <summary>
    /// Render a heightmap from a JS ArrayBuffer. Binary data stays in JS.
    /// Only palette strings cross to .NET for color/UV/flag computation.
    /// </summary>
    private async Task RenderFromFrameAsync(ArrayBuffer frameBuffer)
    {
        var header = ChunkStreamService.ParseFrameHeader(frameBuffer);
        if (header == null) return;
        var (cx, cz, palette, binaryOffset) = header.Value;

        _populatedChunks.Add((cx, cz));
        if (_loadedChunks.Contains((cx, cz))) return;

        // Build BlockPalette[] struct array (packs colors + UVs + flags into single binding)
        var paletteData = BuildBlockPalette(palette);

        // Dispatch kernel with JS ArrayBuffer - binary data packed into HeightmapColumn structs
        var (opaqueFloats, waterFloats) = await _engine.DispatchHeightmapFromFrameAsync(
            frameBuffer, binaryOffset, paletteData, cx, cz);

        // GPU-to-GPU copy: ILGPU output -> WebGPU vertex buffer. Zero CPU readback.
        var (opaqueGpu, waterGpu) = _engine.GetHeightmapOutputGPUBuffers();
        if (opaqueFloats > 0 && opaqueGpu != null)
            _renderer.UploadChunkMeshFromGPU(cx, cz, opaqueGpu, opaqueFloats / 11);
        if (waterFloats > 0 && waterGpu != null)
            _renderer.UploadWaterMeshFromGPU(cx, cz, waterGpu, waterFloats / 11);
        if (opaqueFloats > 0 || waterFloats > 0)
            _loadedChunks.Add((cx, cz));
    }

    /// <summary>
    /// Build BlockPalette[] from palette string list. Packs colors, atlas UVs, and block flags
    /// into a single struct per entry - one GPU binding instead of three.
    /// </summary>
    private static BlockPalette[] BuildBlockPalette(List<string> palette)
    {
        var result = new BlockPalette[palette.Count];
        for (int i = 0; i < palette.Count; i++)
        {
            var name = palette[i];
            var colors = BlockColorMap.GetColor(name);
            var (u0, v0, u1, v1) = TextureAtlas.GetTileUVs(name);

            float flag = 0f;
            if (TextureAtlas.IsPlant(name))
                flag = 1f; // plant: tinted, cross-quad
            else if (name is "minecraft:water" or "minecraft:flowing_water")
                flag = 2f; // water: tinted, transparent
            else if (name.Contains("grass") || name.Contains("leaves")
                  || name.Contains("vine") || name.Contains("fern")
                  || name.Contains("lily"))
                flag = 3f; // solid tinted: biome color multiplied with texture

            result[i] = new BlockPalette
            {
                R = colors.R, G = colors.G, B = colors.B,
                U0 = u0, V0 = v0, U1 = u1, V1 = v1,
                Flag = flag
            };
        }
        return result;
    }

    private void OnChunksEvicted(List<(int cx, int cz)> evicted)
    {
        foreach (var key in evicted)
        {
            _fullChunks.Remove(key);
            _loadedChunks.Remove(key);
        }
    }

    private void OnRenderFrame(float dt)
    {
        // Trigger full 3D loading when camera moves to a new chunk
        int camCX = (int)MathF.Floor(_renderer.Camera.Position.X / 16f);
        int camCZ = (int)MathF.Floor(_renderer.Camera.Position.Z / 16f);
        if (!_loadingFull && (camCX != _lastFullCX || camCZ != _lastFullCZ))
        {
            _lastFullCX = camCX;
            _lastFullCZ = camCZ;
            _ = LoadFullChunksNearbyAsync(camCX, camCZ);
            // Camera moved - data worker re-sorts its queue via camera update from main thread
        }
    }

    private async Task LoadFullChunksNearbyAsync(int camCX, int camCZ)
    {
        _loadingFull = true;
        try
        {
            for (int dz = -FullRenderRadius; dz <= FullRenderRadius; dz++)
            for (int dx = -FullRenderRadius; dx <= FullRenderRadius; dx++)
            {
                if (dx * dx + dz * dz > FullRenderRadius * FullRenderRadius) continue;
                int cx = camCX + dx;
                int cz = camCZ + dz;
                if (_fullChunks.Contains((cx, cz))) continue;
                if (!_populatedChunks.Contains((cx, cz))) continue;

                try
                {
                    // Binary endpoint - raw bytes, no JSON, no base64
                    using var response = await _js.Fetch($"{_baseUrl}/api/world/chunk/{cx}/{cz}",
                        new FetchOptions { Credentials = "include" });
                    if (!response.Ok)
                    {
                        if (_fullChunks.Count <= 1)
                            Console.WriteLine($"[RenderWorker] Full3D ({cx},{cz}) HTTP {response.Status}");
                        continue;
                    }

                    using var buffer = await response.ArrayBuffer();
                    var raw = buffer.ReadBytes();
                    var parsed = ParseFullChunkBinary(raw);
                    if (parsed == null) continue;
                    var (palette, blocks) = parsed.Value;
                    if (blocks.Length != 16 * 384 * 16) continue;

                    var paletteColors = BlockColorMap.BuildPaletteColors(palette);
                    var atlasUVs = BuildFullAtlasUVs(palette);
                    var blockFlags = BuildBlockFlags(palette);

                    // Budget-aware LOD selection
                    int distSq = (cx - camCX) * (cx - camCX) + (cz - camCZ) * (cz - camCZ);
                    int lod = SelectLOD(distSq);
                    MeshGenerationResult result;
                    if (lod == 0)
                        result = await _engine.GenerateMeshAsync(blocks, paletteColors, atlasUVs, blockFlags, cx, cz);
                    else
                        result = await _engine.GenerateLODMeshAsync(blocks, paletteColors, atlasUVs, blockFlags, cx, cz, lod);

                    bool uploaded = false;
                    if (result.OpaqueVertexCount > 0)
                        uploaded = _renderer.UploadChunkMesh(cx, cz, result.OpaqueVertices);
                    if (result.WaterVertexCount > 0)
                        _renderer.UploadWaterMesh(cx, cz, result.WaterVertices);

                    // Only mark as loaded if upload actually succeeded
                    if (uploaded)
                    {
                        _fullChunks.Add((cx, cz));
                        if (_fullChunks.Count <= 3)
                            Console.WriteLine($"[RenderWorker] Full3D ({cx},{cz}): palette={palette.Count}, opaque={result.OpaqueVertexCount}, water={result.WaterVertexCount}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RenderWorker] Full3D ({cx},{cz}) failed: {ex.Message}");
                    // Do NOT add to _fullChunks on failure - allow retry
                }
                await Task.Yield();
            }
        }
        finally
        {
            _loadingFull = false;
        }
    }

    /// <summary>
    /// Block flags for the full 3D MinecraftMeshKernel (separate from heightmap palette struct).
    /// 0=solid non-tinted, 1=plant (tinted, cross-quad), 2=water (tinted, transparent), 3=solid tinted
    /// </summary>
    private static float[] BuildBlockFlags(List<string> palette)
    {
        var flags = new float[palette.Count];
        for (int i = 0; i < palette.Count; i++)
        {
            var name = palette[i];
            if (TextureAtlas.IsPlant(name))
                flags[i] = 1f;
            else if (name is "minecraft:water" or "minecraft:flowing_water")
                flags[i] = 2f;
            else if (name.Contains("grass") || name.Contains("leaves")
                  || name.Contains("vine") || name.Contains("fern")
                  || name.Contains("lily"))
                flags[i] = 3f;
        }
        return flags;
    }

    /// <summary>Per-face atlas UVs for full 3D kernel (12 floats per block).</summary>
    private static float[] BuildFullAtlasUVs(List<string> palette)
    {
        var uvs = new float[palette.Count * 12];
        for (int i = 0; i < palette.Count; i++)
        {
            var (top, side, bottom) = TextureAtlas.GetPerFaceUVs(palette[i]);
            int b = i * 12;
            uvs[b]     = top.u0;   uvs[b + 1] = top.v0;   uvs[b + 2] = top.u1;   uvs[b + 3] = top.v1;
            uvs[b + 4] = side.u0;  uvs[b + 5] = side.v0;  uvs[b + 6] = side.u1;  uvs[b + 7] = side.v1;
            uvs[b + 8] = bottom.u0; uvs[b + 9] = bottom.v0; uvs[b + 10] = bottom.u1; uvs[b + 11] = bottom.v1;
        }
        return uvs;
    }

    /// <summary>Parse binary full chunk data. No JSON, no base64.</summary>
    private static (List<string> palette, ushort[] blocks)? ParseFullChunkBinary(byte[] data)
    {
        if (data.Length < 4) return null;
        int offset = 0;

        int paletteCount = BitConverter.ToInt32(data, offset); offset += 4;
        var palette = new List<string>(paletteCount);
        for (int i = 0; i < paletteCount; i++)
        {
            if (offset + 4 > data.Length) return null;
            int strLen = BitConverter.ToInt32(data, offset); offset += 4;
            if (offset + strLen > data.Length) return null;
            palette.Add(System.Text.Encoding.UTF8.GetString(data, offset, strLen));
            offset += strLen;
        }

        int remaining = data.Length - offset;
        var blocks = new ushort[remaining / 2];
        Buffer.BlockCopy(data, offset, blocks, 0, remaining);
        return (palette, blocks);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        // Data worker handles WebSocket - no OnChunkReceived to unsubscribe
        _renderer.StopRenderLoop();
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
