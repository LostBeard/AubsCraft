using System.Numerics;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Xr;

/// <summary>
/// The GPU half of the headset's HUD: ONE texture (the atlas) the HUD is drawn into - cleared to transparent each frame, so what it draws is premultiplied by its own blending - with a few levels
/// of smaller copies (so far or small text stays steady), and a pipeline that puts a rectangle of that texture on a quad given by four clip-space corners. Drawn over a finished eye, no depth
/// test, so a surface is always readable. Each surface of a frame has a uniform slot of its own: they are all drawn in one encoder, and a buffer written once more would change the ones before it.
/// </summary>
public sealed class XrPanelRenderer : IDisposable
{
    private const int Slots = 8;
    private const int UniformFloats = 24;

    private const string Source = """
        struct Panel { corners: array<vec4<f32>, 4>, settings: vec4<f32>, region: vec4<f32> };
        @group(0) @binding(0) var<uniform> panel: Panel;
        @group(0) @binding(1) var panelTexture: texture_2d<f32>;
        @group(0) @binding(2) var panelSampler: sampler;
        struct VsOut { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> };
        @vertex fn vs_main(@builtin(vertex_index) i: u32) -> VsOut {
            var order = array<u32, 6>(0u, 2u, 1u, 1u, 2u, 3u);
            var uvs = array<vec2<f32>, 4>(vec2<f32>(0.0, 0.0), vec2<f32>(1.0, 0.0), vec2<f32>(0.0, 1.0), vec2<f32>(1.0, 1.0));
            let corner = order[i];
            var o: VsOut;
            o.position = panel.corners[corner];
            o.uv = panel.region.xy + uvs[corner] * panel.region.zw;
            return o;
        }
        @fragment fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
            // The HUD is premultiplied (its blending leaves the colour times its alpha in a cleared texture)
            return textureSample(panelTexture, panelSampler, in.uv) * panel.settings.x;
        }
        """;

    // One level of the atlas from the one above it: the mean of 2 x 2 texels (premultiplied, so a plain mean is right)
    private const string MipSource = """
        @group(0) @binding(0) var src: texture_2d<f32>;
        @vertex fn vs_main(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
            let p = vec2<f32>(f32((i << 1u) & 2u), f32(i & 2u));
            return vec4<f32>(p * 2.0 - 1.0, 0.0, 1.0);
        }
        @fragment fn fs_main(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> {
            let at = vec2<i32>(i32(pos.x), i32(pos.y)) * 2;
            let size = vec2<i32>(textureDimensions(src));
            let a = textureLoad(src, min(at, size - 1), 0);
            let b = textureLoad(src, min(at + vec2<i32>(1, 0), size - 1), 0);
            let c = textureLoad(src, min(at + vec2<i32>(0, 1), size - 1), 0);
            let d = textureLoad(src, min(at + vec2<i32>(1, 1), size - 1), 0);
            return (a + b + c + d) * 0.25;
        }
        """;

    private GPUDevice? _device;
    private GPUQueue? _queue;
    private GPUTexture? _texture;
    private readonly GPUBuffer?[] _uniforms = new GPUBuffer?[Slots];
    private readonly GPUBindGroup?[] _bindGroups = new GPUBindGroup?[Slots];
    private GPUBindGroupLayout? _layout;
    private GPUPipelineLayout? _pipelineLayout;
    private GPURenderPipeline? _pipeline;
    private GPUSampler? _sampler;
    private GPUTextureView? _fullView;
    private GPUBindGroupLayout? _mipLayout;
    private GPUPipelineLayout? _mipPipelineLayout;
    private GPURenderPipeline? _mipPipeline;
    private readonly List<GPUTextureView> _levelViews = new();
    private readonly List<GPUBindGroup> _mipBinds = new();
    private readonly float[] _uniformData = new float[UniformFloats];
    private int _levels = 1;

    public bool IsReady => _pipeline != null;

    /// <summary>The texture the HUD is drawn into (the base level).</summary>
    public GPUTextureView? View { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public void Init(GPUDevice device, GPUQueue queue, string format, int width, int height)
    {
        _device = device;
        _queue = queue;
        Width = width;
        Height = height;
        _levels = 1;
        for (int w = Math.Min(width, height); w > 128 && _levels < 4; w /= 2) _levels++;
        _texture = device.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { width, height },
            Format = format,
            MipLevelCount = _levels,
            Usage = GPUTextureUsage.RenderAttachment | GPUTextureUsage.TextureBinding | GPUTextureUsage.CopySrc,
        });
        View = _texture.CreateView(new GPUTextureViewDescriptor { BaseMipLevel = 0, MipLevelCount = 1 });
        _fullView = _texture.CreateView();
        _sampler = device.CreateSampler(new GPUSamplerDescriptor { MinFilter = "linear", MagFilter = "linear", MipmapFilter = "linear", AddressModeU = "clamp-to-edge", AddressModeV = "clamp-to-edge" });
        _layout = device.CreateBindGroupLayout(new GPUBindGroupLayoutDescriptor
        {
            Entries = new GPUBindGroupLayoutEntry[]
            {
                new() { Binding = 0, Visibility = GPUShaderStageFlags.VERTEX | GPUShaderStageFlags.FRAGMENT, Buffer = new GPUBufferBindingLayout { Type = "uniform" } },
                new() { Binding = 1, Visibility = GPUShaderStageFlags.FRAGMENT, Texture = new GPUTextureBindingLayout { SampleType = "float", ViewDimension = "2d" } },
                new() { Binding = 2, Visibility = GPUShaderStageFlags.FRAGMENT, Sampler = new GPUSamplerBindingLayout { Type = "filtering" } },
            },
        });
        _pipelineLayout = device.CreatePipelineLayout(new GPUPipelineLayoutDescriptor { BindGroupLayouts = new[] { _layout } });
        using var shader = device.CreateShaderModule(new GPUShaderModuleDescriptor { Code = Source });
        var over = new GPUBlendComponent { SrcFactor = GPUBlendFactor.One, DstFactor = GPUBlendFactor.OneMinusSrcAlpha, Operation = GPUBlendOperation.Add };
        _pipeline = device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = _pipelineLayout,
            Vertex = new GPUVertexState { Module = shader, EntryPoint = "vs_main" },
            Fragment = new GPUFragmentState
            {
                Module = shader,
                EntryPoint = "fs_main",
                Targets = new[] { new GPUColorTargetState { Format = format, Blend = new GPUBlendState { Color = over, Alpha = over } } },
            },
            Primitive = new GPUPrimitiveState { Topology = GPUPrimitiveTopology.TriangleList, CullMode = GPUCullMode.None },
        });
        for (int i = 0; i < Slots; i++)
        {
            _uniforms[i] = device.CreateBuffer(new GPUBufferDescriptor { Size = UniformFloats * 4, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });
            _bindGroups[i] = device.CreateBindGroup(new GPUBindGroupDescriptor
            {
                Layout = _layout,
                Entries = new GPUBindGroupEntry[]
                {
                    new() { Binding = 0, Resource = new GPUBufferBinding { Buffer = _uniforms[i]! } },
                    new() { Binding = 1, Resource = _fullView },
                    new() { Binding = 2, Resource = _sampler },
                },
            });
        }
        if (_levels > 1) InitMips(device, format);
    }

    private void InitMips(GPUDevice device, string format)
    {
        _mipLayout = device.CreateBindGroupLayout(new GPUBindGroupLayoutDescriptor
        {
            Entries = new GPUBindGroupLayoutEntry[]
            {
                new() { Binding = 0, Visibility = GPUShaderStageFlags.FRAGMENT, Texture = new GPUTextureBindingLayout { SampleType = "float", ViewDimension = "2d" } },
            },
        });
        _mipPipelineLayout = device.CreatePipelineLayout(new GPUPipelineLayoutDescriptor { BindGroupLayouts = new[] { _mipLayout } });
        using var shader = device.CreateShaderModule(new GPUShaderModuleDescriptor { Code = MipSource });
        _mipPipeline = device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = _mipPipelineLayout,
            Vertex = new GPUVertexState { Module = shader, EntryPoint = "vs_main" },
            Fragment = new GPUFragmentState { Module = shader, EntryPoint = "fs_main", Targets = new[] { new GPUColorTargetState { Format = format } } },
            Primitive = new GPUPrimitiveState { Topology = GPUPrimitiveTopology.TriangleList, CullMode = GPUCullMode.None },
        });
        for (int level = 0; level < _levels; level++)
            _levelViews.Add(_texture!.CreateView(new GPUTextureViewDescriptor { BaseMipLevel = level, MipLevelCount = 1 }));
        for (int level = 1; level < _levels; level++)
            _mipBinds.Add(device.CreateBindGroup(new GPUBindGroupDescriptor
            {
                Layout = _mipLayout,
                Entries = new GPUBindGroupEntry[] { new() { Binding = 0, Resource = _levelViews[level - 1] } },
            }));
    }

    /// <summary>Empty the HUD's texture (transparent): call before the HUD draws into <see cref="View"/>.</summary>
    public void Clear(GPUCommandEncoder encoder)
    {
        if (View == null) return;
        using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[]
            {
                new GPURenderPassColorAttachment { View = View, LoadOp = GPULoadOp.Clear, StoreOp = GPUStoreOp.Store, ClearValue = new double[] { 0, 0, 0, 0 } },
            },
        });
        pass.End();
    }

    /// <summary>The smaller levels, from the base the HUD has just drawn: call after the HUD draws.</summary>
    public void BuildMips(GPUCommandEncoder encoder)
    {
        if (_mipPipeline == null) return;
        for (int level = 1; level < _levels; level++)
        {
            using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
            {
                ColorAttachments = new[] { new GPURenderPassColorAttachment { View = _levelViews[level], LoadOp = GPULoadOp.Clear, StoreOp = GPUStoreOp.Store, ClearValue = new double[] { 0, 0, 0, 0 } } },
            });
            pass.SetPipeline(_mipPipeline);
            pass.SetBindGroup(0, _mipBinds[level - 1]);
            pass.Draw(3, 1, 0, 0);
            pass.End();
        }
    }

    /// <summary>
    /// Put a rectangle of the atlas (<paramref name="region"/>: x, y, width, height in the HUD's layout pixels, <see cref="XrSurfaces.AtlasWidth"/> by <see cref="XrSurfaces.AtlasHeight"/> in all) over <paramref name="target"/> (a finished eye) on the quad whose corners are these clip-space positions
    /// (top left, top right, bottom left, bottom right). <paramref name="slot"/> is this draw's own uniform buffer (0 .. 7, one per surface of the eye). <paramref name="opacity"/> fades the whole surface.
    /// </summary>
    public void Draw(GPUCommandEncoder encoder, GPUTextureView target, int slot, Vector4 topLeft, Vector4 topRight, Vector4 bottomLeft, Vector4 bottomRight, Vector4 region, float opacity = 1f)
    {
        if (_pipeline == null || _queue == null || slot < 0 || slot >= Slots || _bindGroups[slot] == null) return;
        Put(0, topLeft); Put(4, topRight); Put(8, bottomLeft); Put(12, bottomRight);
        _uniformData[16] = opacity;
        _uniformData[17] = _uniformData[18] = _uniformData[19] = 0f;
        // The region as texture coordinates: where it starts and how much of the texture it covers
        _uniformData[20] = region.X / XrSurfaces.AtlasWidth; _uniformData[21] = region.Y / XrSurfaces.AtlasHeight;
        _uniformData[22] = region.Z / XrSurfaces.AtlasWidth; _uniformData[23] = region.W / XrSurfaces.AtlasHeight;
        var bytes = new byte[UniformFloats * 4];
        Buffer.BlockCopy(_uniformData, 0, bytes, 0, bytes.Length);
        _queue.WriteBuffer(_uniforms[slot]!, 0, bytes);
        using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[]
            {
                new GPURenderPassColorAttachment { View = target, LoadOp = GPULoadOp.Load, StoreOp = GPUStoreOp.Store },
            },
        });
        pass.SetPipeline(_pipeline);
        pass.SetBindGroup(0, _bindGroups[slot]);
        pass.Draw(6, 1, 0, 0);
        pass.End();

        void Put(int at, Vector4 v) { _uniformData[at] = v.X; _uniformData[at + 1] = v.Y; _uniformData[at + 2] = v.Z; _uniformData[at + 3] = v.W; }
    }

    /// <summary>A room-space point as the eye sees it: clip space (z already 0..1, as WebGPU wants it).</summary>
    public static Vector4 ClipOf(Vector3 roomPoint, Matrix4x4 roomView, Matrix4x4 projectionGl)
        => Vector4.Transform(new Vector4(roomPoint, 1f), roomView * projectionGl * AubsCraft.Admin.Rendering.ViewSetup.GlToStandardZ);

    public void Dispose()
    {
        foreach (var b in _mipBinds) b.Dispose();
        _mipBinds.Clear();
        foreach (var v in _levelViews) v.Dispose();
        _levelViews.Clear();
        _mipPipeline?.Dispose(); _mipPipeline = null;
        _mipPipelineLayout?.Dispose(); _mipPipelineLayout = null;
        _mipLayout?.Dispose(); _mipLayout = null;
        for (int i = 0; i < Slots; i++)
        {
            _bindGroups[i]?.Dispose(); _bindGroups[i] = null;
            _uniforms[i]?.Destroy(); _uniforms[i]?.Dispose(); _uniforms[i] = null;
        }
        _pipeline?.Dispose(); _pipeline = null;
        _pipelineLayout?.Dispose(); _pipelineLayout = null;
        _layout?.Dispose(); _layout = null;
        _sampler?.Dispose(); _sampler = null;
        _fullView?.Dispose(); _fullView = null;
        View?.Dispose(); View = null;
        _texture?.Destroy(); _texture?.Dispose(); _texture = null;
    }
}
