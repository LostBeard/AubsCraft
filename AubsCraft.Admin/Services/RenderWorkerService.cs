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
        _renderer.StartRenderLoop();

        // Load atlas in the worker - fetch directly, stays in JS
        _ = LoadAtlasAsync();

        // Start chunk loading
        _ = LoadChunksAsync();
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
        return Task.FromResult(new RenderStats(
            _renderer.Fps,
            _renderer.VisibleChunkCount,
            _renderer.TotalChunkCount,
            _renderer.VisibleVertices,
            _renderer.TotalVertices,
            _renderer.DrawDistance,
            LoadedCount,
            _renderer.Camera.Position.X,
            _renderer.Camera.Position.Y,
            _renderer.Camera.Position.Z,
            _renderer.Camera.Pitch,
            _renderer.Camera.Yaw
        ));
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
    private const int FullRenderRadius = 3;
    private string? _baseUrl;

    private async Task LoadChunksAsync()
    {
        // Phase 1: Load from OPFS cache
        var regionCount = await _cache.GetCachedRegionCountAsync();
        if (regionCount > 0)
        {
            Console.WriteLine($"[RenderWorker] Loading {regionCount} cached regions...");
            var cached = await _cache.LoadAllCachedHeightmapsAsync();

            // Sort by camera distance
            int camCX = (int)MathF.Floor(_renderer.Camera.Position.X / 16f);
            int camCZ = (int)MathF.Floor(_renderer.Camera.Position.Z / 16f);
            cached.Sort((a, b) =>
            {
                int da = (a.cx - camCX) * (a.cx - camCX) + (a.cz - camCZ) * (a.cz - camCZ);
                int db = (b.cx - camCX) * (b.cx - camCX) + (b.cz - camCZ) * (b.cz - camCZ);
                return da.CompareTo(db);
            });

            int batchCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (cx, cz, frame) in cached)
            {
                try
                {
                    var hm = ChunkStreamService.ParseFrame(frame);
                    if (hm != null)
                        await RenderHeightmapAsync(hm);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RenderWorker] Chunk ({cx},{cz}) error: {ex.Message}\n{ex.StackTrace}");
                }
                LoadedCount++;

                batchCount++;
                if (batchCount >= 10)
                {
                    batchCount = 0;
                    await Task.Delay(1); // yield to render loop
                }
            }
            sw.Stop();
            Console.WriteLine($"[RenderWorker] Loaded {LoadedCount} from cache in {sw.ElapsedMilliseconds}ms");
        }

        // Phase 2: Connect WebSocket for live streaming
        Console.WriteLine("[RenderWorker] Connecting binary WebSocket...");
        _chunkStream.OnChunkReceived += OnChunkReceived;
        _chunkStream.Connect(_renderer.Camera.Position);
    }

    private void OnChunkReceived(int cx, int cz, byte[] frame)
    {
        _ = ProcessChunkAsync(cx, cz, frame);
    }

    private async Task ProcessChunkAsync(int cx, int cz, byte[] frame)
    {
        var hm = ChunkStreamService.ParseFrame(frame);
        if (hm == null) return;
        _populatedChunks.Add((hm.X, hm.Z));
        if (!_loadedChunks.Contains((hm.X, hm.Z)))
            await RenderHeightmapAsync(hm);
        LoadedCount++;
    }

    private async Task RenderHeightmapAsync(HeightmapData hm)
    {
        _populatedChunks.Add((hm.X, hm.Z));
        if (_loadedChunks.Contains((hm.X, hm.Z))) return;

        var paletteColors = BlockColorMap.BuildPaletteColors(hm.Palette);
        var atlasUVs = new float[hm.Palette.Count * 4];
        for (int i = 0; i < hm.Palette.Count; i++)
        {
            var (u0, v0, u1, v1) = TextureAtlas.GetTileUVs(hm.Palette[i]);
            int b = i * 4;
            atlasUVs[b] = u0; atlasUVs[b + 1] = v0; atlasUVs[b + 2] = u1; atlasUVs[b + 3] = v1;
        }
        var blockFlags = BuildBlockFlags(hm.Palette);

        var blockIdInts = new int[hm.BlockIds.Length];
        for (int i = 0; i < hm.BlockIds.Length; i++)
            blockIdInts[i] = hm.BlockIds[i];
        var seabedIdInts = new int[hm.SeabedBlockIds.Length];
        for (int i = 0; i < hm.SeabedBlockIds.Length; i++)
            seabedIdInts[i] = hm.SeabedBlockIds[i];

        var result = await _engine.GenerateHeightmapMeshAsync(
            hm.Heights, blockIdInts, paletteColors, atlasUVs, blockFlags,
            hm.SeabedHeights, seabedIdInts, hm.X, hm.Z);

        if (result.OpaqueVertexCount > 0)
            _renderer.UploadChunkMesh(hm.X, hm.Z, result.OpaqueVertices);
        if (result.WaterVertexCount > 0)
            _renderer.UploadWaterMesh(hm.X, hm.Z, result.WaterVertices);
        if (result.OpaqueVertexCount > 0 || result.WaterVertexCount > 0)
            _loadedChunks.Add((hm.X, hm.Z));
    }

    /// <summary>
    /// Block flags: 0=solid non-tinted, 1=plant (tinted, cross-quad), 2=water (tinted, transparent), 3=solid tinted (grass/leaves)
    /// </summary>
    private static float[] BuildBlockFlags(List<string> palette)
    {
        var flags = new float[palette.Count];
        for (int i = 0; i < palette.Count; i++)
        {
            var name = palette[i];
            if (TextureAtlas.IsPlant(name))
                flags[i] = 1f; // plant: tinted, cross-quad
            else if (name is "minecraft:water" or "minecraft:flowing_water")
                flags[i] = 2f; // water: tinted, transparent
            else if (name.Contains("grass") || name.Contains("leaves")
                  || name.Contains("vine") || name.Contains("fern")
                  || name.Contains("lily"))
                flags[i] = 3f; // solid tinted: biome color multiplied with texture
        }
        return flags;
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

                    var result = await _engine.GenerateMeshAsync(
                        blocks, paletteColors, atlasUVs, blockFlags, cx, cz);

                    if (result.OpaqueVertexCount > 100)
                        _renderer.UploadChunkMesh(cx, cz, result.OpaqueVertices);
                    if (result.WaterVertexCount > 0)
                        _renderer.UploadWaterMesh(cx, cz, result.WaterVertices);
                    _fullChunks.Add((cx, cz));
                    if (_fullChunks.Count <= 3)
                        Console.WriteLine($"[RenderWorker] Full3D ({cx},{cz}): palette={palette.Count}, opaque={result.OpaqueVertexCount}, water={result.WaterVertexCount}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RenderWorker] Full3D ({cx},{cz}) failed: {ex.Message}");
                    _fullChunks.Add((cx, cz));
                }
                await Task.Yield();
            }
        }
        finally
        {
            _loadingFull = false;
        }
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
        _chunkStream.OnChunkReceived -= OnChunkReceived;
        _renderer.StopRenderLoop();
        _canvas?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Stats from the render worker for the main thread UI.</summary>
public record RenderStats(
    float Fps, int VisibleChunks, int TotalChunks,
    int VisibleVerts, int TotalVerts, int DrawDistance, int LoadedCount,
    float CamX, float CamY, float CamZ, float CamPitch, float CamYaw);
