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
    private string? _canvasId;
    private int _canvasWidth;
    private int _canvasHeight;

    private GPUBuffer? _vertexBuffer;
    private int _bufferCapacityVertices;
    private int _nextFreeVertex;
    private readonly Dictionary<(int cx, int cz), ChunkSlot> _slots = new();
    private readonly List<(int firstVertex, int vertexCount)> _freeSlots = new();

    private const int BytesPerVertex = 11 * 4; // 11 floats x 4 bytes (pos3 + normal3 + color3 + uv2)
    private const int InitialCapacityVertices = 3_000_000;
    private const int MaxBufferVertices = 7_000_000;
    private const int ChunkXZ = 16;
    private const int ChunkHeight = 384;

    private GPUBuffer? _uniformBuffer;
    private GPUBindGroup? _uniformBindGroup;
    private GPUTexture? _atlasTexture;
    private GPUSampler? _atlasSampler;

    private bool _running;
    private bool _disposed;
    private double _lastTimestamp;
    private ActionCallback<double>? _rafCallback;
    private readonly float[] _uniformFloats = new float[20]; // 16 MVP + 4 camera pos
    private byte[]? _uniformBytes;

    public FpsCamera Camera { get; } = new();
    public bool IsInitialized { get; private set; }
    public Action<float>? OnUpdate { get; set; }
    public int VisibleChunkCount { get; private set; }
    public int TotalChunkCount => _slots.Count;
    public float Fps { get; private set; }
    public float FrameTimeMs { get; private set; }
    public int TotalVertices { get; private set; }
    public int VisibleVertices { get; private set; }

    // FPS tracking + adaptive draw distance
    private int _frameCount;
    private double _fpsAccumulator;
    private float _lastDt;
    public int DrawDistance { get; private set; } = 20;
    private const int MinDrawDistance = 10;
    private const int MaxDrawDistance = 50;

    public MapRenderService(BlazorJSRuntime js)
    {
        _js = js;
    }

    public void Init(HTMLCanvasElement canvas, Accelerator accelerator)
    {
        if (IsInitialized) return;

        if (accelerator is not WebGPUAccelerator webGpuAccel)
            throw new InvalidOperationException("MapRenderService requires a WebGPU accelerator");

        var nativeAccel = webGpuAccel.NativeAccelerator;
        _device = nativeAccel.NativeDevice
            ?? throw new InvalidOperationException("WebGPU native device is null");
        _queue = nativeAccel.Queue
            ?? throw new InvalidOperationException("WebGPU queue is null");

        _context = canvas.GetContext<GPUCanvasContext>("webgpu")
            ?? throw new InvalidOperationException("Failed to get WebGPU canvas context");

        using var navigator = _js.Get<Navigator>("navigator");
        using var gpu = navigator.Gpu;
        if (gpu is not null)
            _canvasFormat = gpu.GetPreferredCanvasFormat();

        _context.Configure(new GPUCanvasConfiguration
        {
            Device = _device,
            Format = _canvasFormat,
        });

        _canvasId = canvas.Id;
        _canvasWidth = canvas.ClientWidth;
        _canvasHeight = canvas.ClientHeight;
        canvas.Width = _canvasWidth;
        canvas.Height = _canvasHeight;

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

        CreateDepthTexture();

        _bufferCapacityVertices = InitialCapacityVertices;
        _vertexBuffer = _device.CreateBuffer(new GPUBufferDescriptor
        {
            Size = (ulong)_bufferCapacityVertices * BytesPerVertex,
            Usage = GPUBufferUsage.Vertex | GPUBufferUsage.CopyDst | GPUBufferUsage.CopySrc,
        });

        _uniformBuffer = _device.CreateBuffer(new GPUBufferDescriptor
        {
            Size = 80, // 64 MVP + 16 camera pos (vec4)
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
        _uniformBindGroup?.Dispose();
        _uniformBindGroup = _device!.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = _pipeline!.GetBindGroupLayout(0),
            Entries = new[]
            {
                new GPUBindGroupEntry { Binding = 0, Resource = new GPUBufferBinding { Buffer = _uniformBuffer! } },
                new GPUBindGroupEntry { Binding = 1, Resource = _atlasTexture!.CreateView() },
                new GPUBindGroupEntry { Binding = 2, Resource = _atlasSampler! },
            }
        });
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
        using var window = _js.Get<Window>("window");
        window.RequestAnimationFrame(_rafCallback);
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

        if (_canvasId != null)
        {
            using var doc = _js.Get<Document>("document");
            using var canvasEl = doc.GetElementById<HTMLCanvasElement>(_canvasId);
            if (canvasEl != null)
            {
                int cw = canvasEl.ClientWidth;
                int ch = canvasEl.ClientHeight;
                if (cw > 0 && ch > 0 && (cw != _canvasWidth || ch != _canvasHeight))
                {
                    _canvasWidth = cw;
                    _canvasHeight = ch;
                    canvasEl.Width = cw;
                    canvasEl.Height = ch;
                    CreateDepthTexture();
                }
            }
        }

        float aspect = (float)_canvasWidth / _canvasHeight;
        var vp = Camera.GetVpMatrix(aspect);

        Camera.WriteMvp(_uniformFloats, aspect);
        _uniformFloats[16] = Camera.Position.X;
        _uniformFloats[17] = Camera.Position.Y;
        _uniformFloats[18] = Camera.Position.Z;
        _uniformFloats[19] = 0f; // padding
        _uniformBytes ??= new byte[80];
        Buffer.BlockCopy(_uniformFloats, 0, _uniformBytes, 0, 80);
        _queue!.WriteBuffer(_uniformBuffer!, 0, _uniformBytes);

        var frustum = FrustumCuller.ExtractPlanes(vp);

        using var colorTexture = _context.GetCurrentTexture();
        using var colorView = colorTexture.CreateView();
        using var encoder = _device.CreateCommandEncoder();

        using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[]
            {
                new GPURenderPassColorAttachment
                {
                    View = colorView,
                    LoadOp = GPULoadOp.Clear,
                    StoreOp = GPUStoreOp.Store,
                    ClearValue = new GPUColorDict { R = 0.65, G = 0.80, B = 0.95, A = 1.0 },
                }
            },
            DepthStencilAttachment = new GPURenderPassDepthStencilAttachment
            {
                View = _depthView!,
                DepthLoadOp = "clear",
                DepthStoreOp = "store",
                DepthClearValue = 1.0f,
            }
        });

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

        pass.End();
        using var commandBuffer = encoder.Finish();
        _queue!.Submit(new[] { commandBuffer });
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

@fragment
fn fs_main(input : VertexOutput) -> @location(0) vec4<f32> {
    let sun_dir = normalize(vec3<f32>(0.35, 0.85, 0.40));
    let fill_dir = normalize(vec3<f32>(-0.3, 0.2, -0.5));
    let n = normalize(input.world_normal);

    let sun_intensity = max(dot(n, sun_dir), 0.0);
    let fill_intensity = max(dot(n, fill_dir), 0.0);

    let sun_color = vec3<f32>(1.0, 0.95, 0.85);
    let fill_color = vec3<f32>(0.55, 0.65, 0.85);
    let ambient = vec3<f32>(0.30, 0.32, 0.38);

    let light = ambient + sun_color * sun_intensity * 0.55 + fill_color * fill_intensity * 0.18;

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
    let fog_color = vec3<f32>(0.65, 0.80, 0.95);
    let fog_factor = clamp((dist - fog_start) / (fog_end - fog_start), 0.0, 1.0);
    color = mix(color, fog_color, fog_factor * fog_factor);

    return vec4<f32>(color, 1.0);
}
";

    #endregion
}
