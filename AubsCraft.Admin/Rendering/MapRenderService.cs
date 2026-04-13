using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.ILGPU.WebGPU;
using ILGPU.Runtime;
using System.Numerics;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// WebGPU render pipeline for the 3D Minecraft world viewer.
/// Uses a single sub-allocated vertex buffer with free-list allocation.
/// Adapted from Lost Spawns RenderService.cs.
/// </summary>
public sealed class MapRenderService : IDisposable
{
    private readonly BlazorJSRuntime _js;

    private GPUDevice? _device;
    private GPUQueue? _queue;
    private GPUCanvasContext? _context;
    private GPURenderPipeline? _pipeline;
    private GPUShaderModule? _shaderModule;
    private string _canvasFormat = "bgra8unorm";

    private GPUTexture? _depthTexture;
    private GPUTextureView? _depthView;
    // No canvas ID needed - worker uses OffscreenCanvas, window uses ElementReference
    private int _canvasWidth;
    private int _canvasHeight;

    private GPURenderPipeline? _transparentPipeline;

    // Opaque vertex buffer (solid blocks + plants)
    private GPUBuffer? _vertexBuffer;
    private int _bufferCapacityVertices;
    private int _nextFreeVertex;
    private readonly Dictionary<(int cx, int cz), ChunkSlot> _slots = new();
    private readonly List<(int firstVertex, int vertexCount)> _freeSlots = new();

    // Water vertex buffer (transparent pass)
    private GPUBuffer? _waterVertexBuffer;
    private int _waterBufferCapacity;
    private int _waterNextFreeVertex;
    private readonly Dictionary<(int cx, int cz), ChunkSlot> _waterSlots = new();
    private readonly List<(int firstVertex, int vertexCount)> _waterFreeSlots = new();
    private const int WaterInitialCapacity = 1_000_000;
    private const int WaterMaxCapacity = 5_000_000;

    private const int BytesPerVertex = 11 * 4; // 11 floats x 4 bytes (pos3 + normal3 + color3 + uv2)
    private const int InitialCapacityVertices = 5_000_000;
    private const int MaxBufferVertices = 30_000_000; // increased for seabed + water geometry
    private const int ChunkXZ = 16;
    private const int ChunkHeight = 384;

    private GPUBuffer? _uniformBuffer;
    private GPUBindGroup? _uniformBindGroup;
    private GPUBindGroup? _waterBindGroup; // separate bind group for transparent pipeline
    private GPUTexture? _atlasTexture;
    private GPUSampler? _atlasSampler;

    private bool _running;
    private bool _disposed;
    private double _lastTimestamp;
    private ActionCallback<double>? _rafCallback;
    private readonly float[] _uniformFloats = new float[24]; // 16 MVP + 4 camera pos + 4 time_data

    /// <summary>
    /// Time of day as 0.0-1.0 (maps to Minecraft's 24000 tick cycle).
    /// 0.0=sunrise(6AM), 0.25=noon, 0.5=sunset(6PM), 0.75=midnight.
    /// Set from RCON time query via RenderWorkerService.
    /// </summary>
    public float TimeOfDay { get; set; } = 0.25f; // default to noon
    private byte[]? _uniformBytes;

    public FpsCamera Camera { get; } = new();

    /// <summary>Handle canvas resize from external source (e.g. main thread notifying worker).</summary>
    public void HandleResize(int width, int height)
    {
        if (width > 0 && height > 0 && (width != _canvasWidth || height != _canvasHeight))
        {
            _canvasWidth = width;
            _canvasHeight = height;
            CreateDepthTexture();
            // Update cached descriptor with new depth view
            if (_cachedDepthAttachment != null)
                _cachedDepthAttachment.View = _depthView!;
        }
    }
    public bool IsInitialized { get; private set; }
    public Action<float>? OnUpdate { get; set; }
    public int VisibleChunkCount { get; private set; }
    public int TotalChunkCount => _slots.Count;
    public float Fps { get; private set; }

    // Cached per-frame descriptors to avoid allocation every frame
    private GPURenderPassColorAttachment? _cachedColorAttachment;
    private GPURenderPassDepthStencilAttachment? _cachedDepthAttachment;
    private GPURenderPassDescriptor? _cachedPassDescriptor;

    // Pre-allocated water sort list - reused every frame, zero allocations
    private readonly List<(int dist, (int cx, int cz) key, ChunkSlot slot)> _waterSortList = new(256);
    // Cached submit array - reused every frame
    private readonly GPUCommandBuffer[] _submitArray = new GPUCommandBuffer[1];
    public float FrameTimeMs { get; private set; }
    public int TotalVertices { get; private set; }
    public int VisibleVertices { get; private set; }

    // FPS tracking + adaptive draw distance
    private int _frameCount;
    private double _fpsAccumulator;
    private float _lastDt;
    public int DrawDistance { get; private set; } = 30;
    private const int MinDrawDistance = 25;
    private const int MaxDrawDistance = 50;

    public MapRenderService(BlazorJSRuntime js)
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
        });

        _canvasWidth = width;
        _canvasHeight = height;

        _shaderModule = _device.CreateShaderModule(new GPUShaderModuleDescriptor
        {
            Code = WgslShaderSource
        });

        _pipeline = _device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = "auto",
            Vertex = new GPUVertexState
            {
                Module = _shaderModule,
                EntryPoint = "vs_main",
                Buffers = new[]
                {
                    new GPUVertexBufferLayout
                    {
                        ArrayStride = (ulong)BytesPerVertex,
                        StepMode = GPUVertexStepMode.Vertex,
                        Attributes = new GPUVertexAttribute[]
                        {
                            new() { ShaderLocation = 0, Offset = 0,      Format = GPUVertexFormat.Float32x3 }, // position
                            new() { ShaderLocation = 1, Offset = 3 * 4,  Format = GPUVertexFormat.Float32x3 }, // normal
                            new() { ShaderLocation = 2, Offset = 6 * 4,  Format = GPUVertexFormat.Float32x3 }, // color
                            new() { ShaderLocation = 3, Offset = 9 * 4,  Format = GPUVertexFormat.Float32x2 }, // uv
                        }
                    }
                }
            },
            Fragment = new GPUFragmentState
            {
                Module = _shaderModule,
                EntryPoint = "fs_main",
                Targets = new[]
                {
                    new GPUColorTargetState { Format = _canvasFormat }
                }
            },
            Primitive = new GPUPrimitiveState
            {
                Topology = GPUPrimitiveTopology.TriangleList,
                CullMode = GPUCullMode.Back,
                FrontFace = GPUFrontFace.CCW,
            },
            DepthStencil = new GPUDepthStencilState
            {
                Format = "depth24plus",
                DepthWriteEnabled = true,
                DepthCompare = "less",
            }
        });

        // Transparent pipeline: alpha blending, depth test but no depth write
        _transparentPipeline = _device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = "auto",
            Vertex = new GPUVertexState
            {
                Module = _shaderModule,
                EntryPoint = "vs_main",
                Buffers = new[]
                {
                    new GPUVertexBufferLayout
                    {
                        ArrayStride = (ulong)BytesPerVertex,
                        StepMode = GPUVertexStepMode.Vertex,
                        Attributes = new GPUVertexAttribute[]
                        {
                            new() { ShaderLocation = 0, Offset = 0,      Format = GPUVertexFormat.Float32x3 },
                            new() { ShaderLocation = 1, Offset = 3 * 4,  Format = GPUVertexFormat.Float32x3 },
                            new() { ShaderLocation = 2, Offset = 6 * 4,  Format = GPUVertexFormat.Float32x3 },
                            new() { ShaderLocation = 3, Offset = 9 * 4,  Format = GPUVertexFormat.Float32x2 },
                        }
                    }
                }
            },
            Fragment = new GPUFragmentState
            {
                Module = _shaderModule,
                EntryPoint = "fs_water",
                Targets = new[]
                {
                    new GPUColorTargetState
                    {
                        Format = _canvasFormat,
                        Blend = new GPUBlendState
                        {
                            Color = new GPUBlendComponent
                            {
                                SrcFactor = GPUBlendFactor.SrcAlpha,
                                DstFactor = GPUBlendFactor.OneMinusSrcAlpha,
                                Operation = GPUBlendOperation.Add,
                            },
                            Alpha = new GPUBlendComponent
                            {
                                SrcFactor = GPUBlendFactor.One,
                                DstFactor = GPUBlendFactor.OneMinusSrcAlpha,
                                Operation = GPUBlendOperation.Add,
                            }
                        }
                    }
                }
            },
            Primitive = new GPUPrimitiveState
            {
                Topology = GPUPrimitiveTopology.TriangleList,
                CullMode = GPUCullMode.Back,
                FrontFace = GPUFrontFace.CCW,
            },
            DepthStencil = new GPUDepthStencilState
            {
                Format = "depth24plus",
                DepthWriteEnabled = false,  // transparent does NOT write depth
                DepthCompare = "less",      // but still tests against opaque geometry
            }
        });

        CreateDepthTexture();

        _bufferCapacityVertices = InitialCapacityVertices;
        _vertexBuffer = _device.CreateBuffer(new GPUBufferDescriptor
        {
            Size = (ulong)_bufferCapacityVertices * BytesPerVertex,
            Usage = GPUBufferUsage.Vertex | GPUBufferUsage.CopyDst | GPUBufferUsage.CopySrc,
        });

        // Water vertex buffer (transparent pass)
        _waterBufferCapacity = WaterInitialCapacity;
        _waterVertexBuffer = _device.CreateBuffer(new GPUBufferDescriptor
        {
            Size = (ulong)_waterBufferCapacity * BytesPerVertex,
            Usage = GPUBufferUsage.Vertex | GPUBufferUsage.CopyDst | GPUBufferUsage.CopySrc,
        });

        _uniformBuffer = _device.CreateBuffer(new GPUBufferDescriptor
        {
            Size = 96, // 64 MVP + 16 camera pos (vec4) + 16 time_data (vec4)
            Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst,
        });

        // Texture atlas and sampler will be set up in LoadAtlasAsync
        // Create a 1x1 placeholder texture so the bind group works before atlas loads
        _atlasTexture = _device.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { 1, 1 },
            Format = "rgba8unorm",
            Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst,
        });

        _atlasSampler = _device.CreateSampler(new GPUSamplerDescriptor
        {
            MagFilter = GPUFilterMode.Nearest, // Minecraft-style pixelated
            MinFilter = GPUFilterMode.Nearest,
            AddressModeU = "repeat",
            AddressModeV = "repeat",
        });

        CreateBindGroup();

        IsInitialized = true;
    }

    private void CreateBindGroup()
    {
        var entries = new[]
        {
            new GPUBindGroupEntry { Binding = 0, Resource = new GPUBufferBinding { Buffer = _uniformBuffer! } },
            new GPUBindGroupEntry { Binding = 1, Resource = _atlasTexture!.CreateView() },
            new GPUBindGroupEntry { Binding = 2, Resource = _atlasSampler! },
        };

        // Opaque pipeline bind group
        _uniformBindGroup?.Dispose();
        _uniformBindGroup = _device!.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = _pipeline!.GetBindGroupLayout(0),
            Entries = entries,
        });

        // Transparent pipeline bind group (same bindings, different layout from "auto")
        _waterBindGroup?.Dispose();
        if (_transparentPipeline != null)
        {
            _waterBindGroup = _device!.CreateBindGroup(new GPUBindGroupDescriptor
            {
                Layout = _transparentPipeline.GetBindGroupLayout(0),
                Entries = entries,
            });
        }
    }

    /// <summary>
    /// Load the texture atlas from a URL and upload it to the GPU.
    /// Call after Init() - the viewer works with flat colors until the atlas loads.
    /// </summary>
    /// <summary>
    /// Uploads raw RGBA pixel data as the texture atlas.
    /// </summary>
    public void UploadAtlas(byte[] rgbaPixels, int width, int height)
    {
        _atlasTexture?.Destroy();
        _atlasTexture?.Dispose();
        _atlasTexture = _device!.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { width, height },
            Format = "rgba8unorm",
            Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst,
        });

        using var pixelArray = new Uint8Array(rgbaPixels);
        _queue!.WriteTexture(
            new GPUTexelCopyTextureInfo { Texture = _atlasTexture },
            pixelArray,
            new GPUTexelCopyBufferLayout { BytesPerRow = (uint)(width * 4), RowsPerImage = (uint)height },
            new GPUExtent3DDict { Width = (uint)width, Height = (uint)height });

        CreateBindGroup();
        System.Console.WriteLine($"[MapRender] Atlas uploaded: {width}x{height}");
    }

    public void UploadChunkMesh(int cx, int cz, float[] vertices)
    {
        var key = (cx, cz);
        int vertexCount = vertices.Length / 11;
        if (vertexCount == 0) return;

        if (_slots.Remove(key, out var oldSlot))
            _freeSlots.Add((oldSlot.FirstVertex, oldSlot.VertexCount));

        int writeOffset = -1;
        for (int i = 0; i < _freeSlots.Count; i++)
        {
            if (_freeSlots[i].vertexCount >= vertexCount)
            {
                var free = _freeSlots[i];
                writeOffset = free.firstVertex;
                int remainder = free.vertexCount - vertexCount;
                if (remainder > 100)
                    _freeSlots[i] = (free.firstVertex + vertexCount, remainder);
                else
                    _freeSlots.RemoveAt(i);
                break;
            }
        }

        if (writeOffset < 0)
        {
            if (_nextFreeVertex + vertexCount > _bufferCapacityVertices)
            {
                int needed = _nextFreeVertex + vertexCount;
                int newCap = Math.Min(Math.Max(needed + needed / 4, _bufferCapacityVertices + 500_000), MaxBufferVertices);
                if (newCap < needed) return;
                GrowBuffer(newCap);
            }
            writeOffset = _nextFreeVertex;
            _nextFreeVertex += vertexCount;
        }

        ulong byteOffset = (ulong)writeOffset * BytesPerVertex;
        using var jsArray = new Float32Array(vertices);
        _queue!.WriteBuffer(_vertexBuffer!, byteOffset, jsArray);
        _slots[key] = new ChunkSlot { FirstVertex = writeOffset, VertexCount = vertexCount };
    }

    /// <summary>
    /// Upload chunk mesh directly from a GPU buffer (zero CPU copy).
    /// Uses CopyBufferToBuffer - data stays on GPU the entire time.
    /// </summary>
    public void UploadChunkMeshFromGPU(int cx, int cz, GPUBuffer sourceBuffer, int vertexCount, long sourceByteOffset = 0)
    {
        var key = (cx, cz);
        if (vertexCount == 0) return;

        if (_slots.Remove(key, out var oldSlot))
            _freeSlots.Add((oldSlot.FirstVertex, oldSlot.VertexCount));

        int writeOffset = -1;
        for (int i = 0; i < _freeSlots.Count; i++)
        {
            if (_freeSlots[i].vertexCount >= vertexCount)
            {
                var free = _freeSlots[i];
                writeOffset = free.firstVertex;
                int remainder = free.vertexCount - vertexCount;
                if (remainder > 100)
                    _freeSlots[i] = (free.firstVertex + vertexCount, remainder);
                else
                    _freeSlots.RemoveAt(i);
                break;
            }
        }

        if (writeOffset < 0)
        {
            if (_nextFreeVertex + vertexCount > _bufferCapacityVertices)
            {
                int needed = _nextFreeVertex + vertexCount;
                int newCap = Math.Min(Math.Max(needed + needed / 4, _bufferCapacityVertices + 500_000), MaxBufferVertices);
                if (newCap < needed)
                {
                    Console.WriteLine($"[MapRender] Vertex buffer full, dropping chunk ({cx},{cz})");
                    return;
                }
                GrowBuffer(newCap);
            }
            writeOffset = _nextFreeVertex;
            _nextFreeVertex += vertexCount;
        }

        // GPU-to-GPU copy - zero CPU involvement
        ulong destByteOffset = (ulong)writeOffset * BytesPerVertex;
        ulong copyBytes = (ulong)vertexCount * BytesPerVertex;
        using var encoder = _device!.CreateCommandEncoder();
        encoder.CopyBufferToBuffer(sourceBuffer, (ulong)sourceByteOffset, _vertexBuffer!, destByteOffset, copyBytes);
        _submitArray[0] = encoder.Finish();
        _queue!.Submit(_submitArray);
        _submitArray[0]?.Dispose();

        _slots[key] = new ChunkSlot { FirstVertex = writeOffset, VertexCount = vertexCount };
    }

    public void RemoveChunkMesh(int cx, int cz)
    {
        if (_slots.Remove((cx, cz), out var slot))
        {
            int start = slot.FirstVertex;
            int count = slot.VertexCount;
            for (int i = _freeSlots.Count - 1; i >= 0; i--)
            {
                var f = _freeSlots[i];
                if (f.firstVertex + f.vertexCount == start)
                { start = f.firstVertex; count += f.vertexCount; _freeSlots.RemoveAt(i); }
                else if (start + count == f.firstVertex)
                { count += f.vertexCount; _freeSlots.RemoveAt(i); }
            }
            _freeSlots.Add((start, count));
        }
    }

    /// <summary>Uploads water mesh vertices for a chunk to the transparent vertex buffer.</summary>
    public void UploadWaterMesh(int cx, int cz, float[] vertices)
    {
        var key = (cx, cz);
        int vertexCount = vertices.Length / 11;
        if (vertexCount == 0) return;

        if (_waterSlots.Remove(key, out var oldSlot))
            _waterFreeSlots.Add((oldSlot.FirstVertex, oldSlot.VertexCount));

        int writeOffset = -1;
        for (int i = 0; i < _waterFreeSlots.Count; i++)
        {
            if (_waterFreeSlots[i].vertexCount >= vertexCount)
            {
                var free = _waterFreeSlots[i];
                writeOffset = free.firstVertex;
                int remainder = free.vertexCount - vertexCount;
                if (remainder > 100)
                    _waterFreeSlots[i] = (free.firstVertex + vertexCount, remainder);
                else
                    _waterFreeSlots.RemoveAt(i);
                break;
            }
        }

        if (writeOffset < 0)
        {
            if (_waterNextFreeVertex + vertexCount > _waterBufferCapacity)
            {
                int needed = _waterNextFreeVertex + vertexCount;
                int newCap = Math.Min(Math.Max(needed + needed / 4, _waterBufferCapacity + 200_000), WaterMaxCapacity);
                if (newCap < needed) return;
                GrowWaterBuffer(newCap);
            }
            writeOffset = _waterNextFreeVertex;
            _waterNextFreeVertex += vertexCount;
        }

        ulong byteOffset = (ulong)writeOffset * BytesPerVertex;
        using var jsArray = new Float32Array(vertices);
        _queue!.WriteBuffer(_waterVertexBuffer!, byteOffset, jsArray);
        _waterSlots[key] = new ChunkSlot { FirstVertex = writeOffset, VertexCount = vertexCount };
    }

    /// <summary>Upload water mesh from GPU buffer (zero CPU copy).</summary>
    public void UploadWaterMeshFromGPU(int cx, int cz, GPUBuffer sourceBuffer, int vertexCount, long sourceByteOffset = 0)
    {
        var key = (cx, cz);
        if (vertexCount == 0) return;

        if (_waterSlots.Remove(key, out var oldSlot))
            _waterFreeSlots.Add((oldSlot.FirstVertex, oldSlot.VertexCount));

        int writeOffset = -1;
        for (int i = 0; i < _waterFreeSlots.Count; i++)
        {
            if (_waterFreeSlots[i].vertexCount >= vertexCount)
            {
                var free = _waterFreeSlots[i];
                writeOffset = free.firstVertex;
                int remainder = free.vertexCount - vertexCount;
                if (remainder > 100)
                    _waterFreeSlots[i] = (free.firstVertex + vertexCount, remainder);
                else
                    _waterFreeSlots.RemoveAt(i);
                break;
            }
        }

        if (writeOffset < 0)
        {
            if (_waterNextFreeVertex + vertexCount > _waterBufferCapacity)
            {
                int needed = _waterNextFreeVertex + vertexCount;
                int newCap = Math.Min(Math.Max(needed + needed / 4, _waterBufferCapacity + 200_000), WaterMaxCapacity);
                if (newCap < needed) return;
                GrowWaterBuffer(newCap);
            }
            writeOffset = _waterNextFreeVertex;
            _waterNextFreeVertex += vertexCount;
        }

        ulong destByteOffset = (ulong)writeOffset * BytesPerVertex;
        ulong copyBytes = (ulong)vertexCount * BytesPerVertex;
        using var encoder = _device!.CreateCommandEncoder();
        encoder.CopyBufferToBuffer(sourceBuffer, (ulong)sourceByteOffset, _waterVertexBuffer!, destByteOffset, copyBytes);
        _submitArray[0] = encoder.Finish();
        _queue!.Submit(_submitArray);
        _submitArray[0]?.Dispose();

        _waterSlots[key] = new ChunkSlot { FirstVertex = writeOffset, VertexCount = vertexCount };
    }

    private void GrowWaterBuffer(int newCapacity)
    {
        var newBuffer = _device!.CreateBuffer(new GPUBufferDescriptor
        {
            Size = (ulong)newCapacity * BytesPerVertex,
            Usage = GPUBufferUsage.Vertex | GPUBufferUsage.CopyDst | GPUBufferUsage.CopySrc,
        });
        if (_waterNextFreeVertex > 0)
        {
            var encoder = _device.CreateCommandEncoder();
            encoder.CopyBufferToBuffer(_waterVertexBuffer!, 0, newBuffer, 0,
                (ulong)_waterNextFreeVertex * BytesPerVertex);
            _submitArray[0] = encoder.Finish();
            _queue!.Submit(_submitArray);
            _submitArray[0]?.Dispose();
        }
        _waterVertexBuffer?.Destroy();
        _waterVertexBuffer = newBuffer;
        _waterBufferCapacity = newCapacity;
    }

    private void GrowBuffer(int newCapacity)
    {
        var newBuffer = _device!.CreateBuffer(new GPUBufferDescriptor
        {
            Size = (ulong)newCapacity * BytesPerVertex,
            Usage = GPUBufferUsage.Vertex | GPUBufferUsage.CopyDst | GPUBufferUsage.CopySrc,
        });
        if (_nextFreeVertex > 0 && _vertexBuffer != null)
        {
            using var encoder = _device.CreateCommandEncoder();
            encoder.CopyBufferToBuffer(_vertexBuffer, 0, newBuffer, 0, (ulong)_nextFreeVertex * BytesPerVertex);
            using var commandBuffer = encoder.Finish();
            _queue!.Submit(new[] { commandBuffer });
        }
        _vertexBuffer?.Destroy();
        _vertexBuffer?.Dispose();
        _vertexBuffer = newBuffer;
        _bufferCapacityVertices = newCapacity;
    }

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
        _lastDt = dt;

        // FPS tracking
        _frameCount++;
        _fpsAccumulator += dt;
        if (_fpsAccumulator >= 0.5)
        {
            Fps = (float)(_frameCount / _fpsAccumulator);
            FrameTimeMs = (float)(_fpsAccumulator / _frameCount * 1000.0);
            _frameCount = 0;
            _fpsAccumulator = 0;

            // Adaptive draw distance: grow when FPS is high, shrink when low
            if (Fps > 50 && DrawDistance < MaxDrawDistance)
                DrawDistance += 2;
            else if (Fps < 30 && DrawDistance > MinDrawDistance)
                DrawDistance -= 2;
        }

        OnUpdate?.Invoke(dt);
        RenderFrame();
        RequestFrame();
    }

    private void RenderFrame()
    {
        if (_device == null || _context == null || _pipeline == null ||
            _vertexBuffer == null || _slots.Count == 0)
            return;

        // Resize is handled by ResizeObserver (worker) or HandleResize (external).
        // No per-frame DOM queries.

        float aspect = (float)_canvasWidth / _canvasHeight;
        var vp = Camera.GetVpMatrix(aspect);

        Camera.WriteMvp(_uniformFloats, aspect);
        _uniformFloats[16] = Camera.Position.X;
        _uniformFloats[17] = Camera.Position.Y;
        _uniformFloats[18] = Camera.Position.Z;
        _uniformFloats[19] = 0f; // padding
        _uniformFloats[20] = TimeOfDay; // time_data.x (0.0-1.0)
        _uniformFloats[21] = 0f; // reserved
        _uniformFloats[22] = 0f; // reserved
        _uniformFloats[23] = 0f; // reserved
        _uniformBytes ??= new byte[96];
        Buffer.BlockCopy(_uniformFloats, 0, _uniformBytes, 0, 96);
        _queue!.WriteBuffer(_uniformBuffer!, 0, _uniformBytes);

        var frustum = FrustumCuller.ExtractPlanes(vp);

        using var colorTexture = _context.GetCurrentTexture();
        using var colorView = colorTexture.CreateView();
        using var encoder = _device.CreateCommandEncoder();

        // Reuse cached descriptor objects - only update the view reference
        _cachedColorAttachment ??= new GPURenderPassColorAttachment
        {
            LoadOp = GPULoadOp.Clear,
            StoreOp = GPUStoreOp.Store,
            ClearValue = new GPUColorDict { R = 0.65, G = 0.80, B = 0.95, A = 1.0 },
        };
        _cachedColorAttachment.View = colorView;

        _cachedDepthAttachment ??= new GPURenderPassDepthStencilAttachment
        {
            View = _depthView!,
            DepthLoadOp = "clear",
            DepthStoreOp = "store",
            DepthClearValue = 1.0f,
        };

        _cachedPassDescriptor ??= new GPURenderPassDescriptor
        {
            ColorAttachments = new[] { _cachedColorAttachment },
            DepthStencilAttachment = _cachedDepthAttachment,
        };

        using var pass = encoder.BeginRenderPass(_cachedPassDescriptor);

        pass.SetPipeline(_pipeline);
        pass.SetBindGroup(0, _uniformBindGroup!);
        pass.SetVertexBuffer(0, _vertexBuffer);

        int visible = 0;
        int visVerts = 0;
        int totalVerts = 0;
        // Camera chunk position for draw distance check
        int camCX = (int)MathF.Floor(Camera.Position.X / ChunkXZ);
        int camCZ = (int)MathF.Floor(Camera.Position.Z / ChunkXZ);
        int drawDistSq = DrawDistance * DrawDistance;

        foreach (var ((cx, cz), slot) in _slots)
        {
            if (slot.VertexCount == 0) continue;
            totalVerts += slot.VertexCount;

            // Quick distance check before expensive frustum test
            int dx = cx - camCX, dz = cz - camCZ;
            if (dx * dx + dz * dz > drawDistSq) continue;

            var min = new Vector3(cx * ChunkXZ, -64f, cz * ChunkXZ);
            var max = new Vector3(cx * ChunkXZ + ChunkXZ, 320f, cz * ChunkXZ + ChunkXZ);
            if (!FrustumCuller.IsBoxVisible(in frustum, min, max)) continue;
            pass.Draw((uint)slot.VertexCount, 1, (uint)slot.FirstVertex, 0);
            visible++;
            visVerts += slot.VertexCount;
        }
        VisibleChunkCount = visible;
        VisibleVertices = visVerts;
        TotalVertices = totalVerts;

        // Pass 2: Transparent water (same render pass, different pipeline)
        // Draw back-to-front using chunk distance for correct blending
        if (_waterSlots.Count > 0 && _waterVertexBuffer != null)
        {
            pass.SetPipeline(_transparentPipeline!);
            pass.SetBindGroup(0, _waterBindGroup!);
            pass.SetVertexBuffer(0, _waterVertexBuffer);

            // Sort water chunks back-to-front - reuse pre-allocated list, zero LINQ
            _waterSortList.Clear();
            foreach (var ((cx2, cz2), slot2) in _waterSlots)
            {
                if (slot2.VertexCount == 0) continue;
                int ddx = cx2 - camCX, ddz = cz2 - camCZ;
                int dist = ddx * ddx + ddz * ddz;
                if (dist <= drawDistSq)
                    _waterSortList.Add((dist, (cx2, cz2), slot2));
            }
            _waterSortList.Sort((a, b) => b.dist.CompareTo(a.dist)); // back-to-front

            foreach (var (_, key, slot) in _waterSortList)
            {
                var min = new Vector3(key.cx * ChunkXZ, -64f, key.cz * ChunkXZ);
                var max = new Vector3(key.cx * ChunkXZ + ChunkXZ, 320f, key.cz * ChunkXZ + ChunkXZ);
                if (!FrustumCuller.IsBoxVisible(in frustum, min, max)) continue;
                pass.Draw((uint)slot.VertexCount, 1, (uint)slot.FirstVertex, 0);
            }
        }

        pass.End();
        using var commandBuffer = encoder.Finish();
        _submitArray[0] = commandBuffer;
        _queue!.Submit(_submitArray);
    }

    private void CreateDepthTexture()
    {
        _depthView?.Dispose();
        _depthTexture?.Destroy();
        _depthTexture?.Dispose();
        _depthTexture = _device!.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { _canvasWidth, _canvasHeight },
            Format = "depth24plus",
            Usage = GPUTextureUsage.RenderAttachment,
        });
        _depthView = _depthTexture.CreateView();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _rafCallback?.Dispose();
        _rafCallback = null;
        _vertexBuffer?.Destroy();
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _slots.Clear();
        _freeSlots.Clear();
        _uniformBindGroup?.Dispose();
        _waterBindGroup?.Dispose();
        _waterVertexBuffer?.Destroy();
        _waterVertexBuffer?.Dispose();
        _uniformBuffer?.Destroy();
        _uniformBuffer?.Dispose();
        _depthView?.Dispose();
        _depthTexture?.Destroy();
        _depthTexture?.Dispose();
        _shaderModule?.Dispose();
        _pipeline = null;
        _context?.Unconfigure();
        _context?.Dispose();
        _context = null;
        IsInitialized = false;
        _disposed = false;
    }

    private struct ChunkSlot
    {
        public int FirstVertex;
        public int VertexCount;
    }

    #region WGSL Shader

    private const string WgslShaderSource = @"
struct Uniforms {
    mvp : mat4x4<f32>,
    camera_pos : vec4<f32>,
    time_data : vec4<f32>,  // x = time_of_day (0.0-1.0, maps to MC 24000 ticks), y/z/w reserved
};

@group(0) @binding(0) var<uniform> uniforms : Uniforms;
@group(0) @binding(1) var atlas_texture : texture_2d<f32>;
@group(0) @binding(2) var atlas_sampler : sampler;

struct VertexInput {
    @location(0) position : vec3<f32>,
    @location(1) normal   : vec3<f32>,
    @location(2) color    : vec3<f32>,
    @location(3) uv       : vec2<f32>,
};

struct VertexOutput {
    @builtin(position) clip_position : vec4<f32>,
    @location(0) world_normal : vec3<f32>,
    @location(1) base_color   : vec3<f32>,
    @location(2) world_pos    : vec3<f32>,
    @location(3) tex_uv       : vec2<f32>,
};

@vertex
fn vs_main(input : VertexInput) -> VertexOutput {
    var output : VertexOutput;
    output.clip_position = uniforms.mvp * vec4<f32>(input.position, 1.0);
    output.world_normal = input.normal;
    output.base_color = input.color;
    output.world_pos = input.position;
    output.tex_uv = input.uv;
    return output;
}

// Time-of-day sun direction. MC ticks: 0=sunrise(6AM), 6000=noon, 12000=sunset, 18000=midnight
// time_t is 0.0-1.0 mapping to 24000 ticks
fn get_sun_dir(time_t : f32) -> vec3<f32> {
    // Sun angle: 0=sunrise(horizon east), 0.25=noon(overhead), 0.5=sunset(horizon west)
    let angle = time_t * 6.283185; // full circle
    let sy = -cos(angle);          // up at noon, down at midnight
    let sx = sin(angle) * 0.7;     // east to west
    return normalize(vec3<f32>(sx, sy, 0.35));
}

fn get_sky_colors(time_t : f32) -> array<vec3<f32>, 4> {
    // Sun elevation: 1.0 at noon, 0.0 at horizon, -1.0 at midnight
    let elevation = -cos(time_t * 6.283185);
    let day_factor = clamp(elevation * 2.0 + 0.5, 0.0, 1.0);     // 1.0 during day, 0.0 at night
    let dawn_factor = clamp(1.0 - abs(elevation) * 4.0, 0.0, 1.0); // peaks at sunrise/sunset

    // Ambient: bright blue-white during day, dark blue at night, warm at dawn/dusk
    let day_ambient = vec3<f32>(0.30, 0.32, 0.38);
    let night_ambient = vec3<f32>(0.05, 0.06, 0.12);
    let dawn_ambient = vec3<f32>(0.25, 0.18, 0.12);
    var ambient = mix(night_ambient, day_ambient, day_factor);
    ambient = mix(ambient, dawn_ambient, dawn_factor * 0.6);

    // Sun color: white-yellow at noon, orange at dawn/dusk, dim blue at night
    let day_sun = vec3<f32>(1.0, 0.95, 0.85);
    let dawn_sun = vec3<f32>(1.0, 0.55, 0.25);
    let night_sun = vec3<f32>(0.15, 0.18, 0.35); // moonlight
    var sun_col = mix(night_sun, day_sun, day_factor);
    sun_col = mix(sun_col, dawn_sun, dawn_factor * 0.8);

    // Fog color: blue sky during day, dark at night, orange at dawn/dusk
    let day_fog = vec3<f32>(0.65, 0.80, 0.95);
    let night_fog = vec3<f32>(0.04, 0.05, 0.10);
    let dawn_fog = vec3<f32>(0.85, 0.50, 0.30);
    var fog = mix(night_fog, day_fog, day_factor);
    fog = mix(fog, dawn_fog, dawn_factor * 0.7);

    // Sun strength multiplier
    let strength = vec3<f32>(clamp(day_factor * 0.55 + 0.05, 0.05, 0.55), 0.0, 0.0);

    return array<vec3<f32>, 4>(ambient, sun_col, fog, strength);
}

@fragment
fn fs_main(input : VertexOutput) -> @location(0) vec4<f32> {
    let time_t = uniforms.time_data.x;
    let sun_dir = get_sun_dir(time_t);
    let fill_dir = normalize(vec3<f32>(-0.3, 0.2, -0.5));
    let n = normalize(input.world_normal);

    let sky = get_sky_colors(time_t);
    let ambient = sky[0];
    let sun_color = sky[1];
    let fog_color = sky[2];
    let sun_strength = sky[3].x;

    let sun_intensity = max(dot(n, sun_dir), 0.0);
    let fill_intensity = max(dot(n, fill_dir), 0.0);
    let fill_color = vec3<f32>(0.55, 0.65, 0.85);

    let light = ambient + sun_color * sun_intensity * sun_strength + fill_color * fill_intensity * 0.18;

    // Sample texture
    let tex_color = textureSample(atlas_texture, atlas_sampler, input.tex_uv);
    let has_texture = step(0.0, input.tex_uv.x);
    var color = mix(input.base_color, tex_color.rgb * input.base_color, has_texture);

    // Discard transparent pixels (leaf gaps, plant cutouts)
    let alpha = mix(1.0, tex_color.a, has_texture);
    if (alpha < 0.3) { discard; }

    // Face-dependent shading
    if (n.y < -0.5) {
        color = color * 0.70;
    }
    if (abs(n.y) < 0.1) {
        color = color * 0.85;
    }

    color = color * light;

    // Distance fog
    let dist = length(input.world_pos - uniforms.camera_pos.xyz);
    let fog_start = 250.0;
    let fog_end = 450.0;
    let fog_factor = clamp((dist - fog_start) / (fog_end - fog_start), 0.0, 1.0);
    color = mix(color, fog_color, fog_factor * fog_factor);

    return vec4<f32>(color, 1.0);
}

// Water fragment shader - same time-of-day lighting as opaque but with alpha transparency
@fragment
fn fs_water(input: VertexOutput) -> @location(0) vec4<f32> {
    let time_t = uniforms.time_data.x;
    let has_texture = step(0.0, input.tex_uv.x);
    let tex_color = textureSample(atlas_texture, atlas_sampler, input.tex_uv);
    var color = mix(input.base_color, tex_color.rgb * input.base_color, has_texture);

    let sky = get_sky_colors(time_t);
    let sun_dir = get_sun_dir(time_t);
    let sun_strength_val = max(dot(input.world_normal, sun_dir), 0.0);
    let water_ambient = sky[0] * 1.2; // water reflects more ambient
    let lit = water_ambient + sky[1] * sun_strength_val * sky[3].x;
    color = color * lit;

    // Distance fog (same as opaque, time-based fog color)
    let fog_color = sky[2];
    let dist = length(input.world_pos - uniforms.camera_pos.xyz);
    let fog_start = 250.0;
    let fog_end = 450.0;
    let fog_factor = clamp((dist - fog_start) / (fog_end - fog_start), 0.0, 1.0);
    color = mix(color, fog_color, fog_factor * fog_factor);

    return vec4<f32>(color, 0.6);
}
";

    #endregion
}
