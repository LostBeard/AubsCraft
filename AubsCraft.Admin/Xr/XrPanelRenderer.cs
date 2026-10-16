using System.Numerics;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Xr;

/// <summary>
/// The GPU half of the headset's HUD: a texture the HUD is drawn into (cleared to transparent each frame, so what it draws is premultiplied by its own blending), and a pipeline that puts that
/// texture on a quad given by four clip-space corners - drawn over a finished eye, no depth test, so the HUD is always readable.
/// </summary>
public sealed class XrPanelRenderer : IDisposable
{
    private const string Source = """
        struct Panel { corners: array<vec4<f32>, 4>, settings: vec4<f32> };
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
            o.uv = uvs[corner];
            return o;
        }
        @fragment fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
            // The HUD is premultiplied (its blending leaves the colour times its alpha in a cleared texture)
            return textureSample(panelTexture, panelSampler, in.uv) * panel.settings.x;
        }
        """;

    private GPUDevice? _device;
    private GPUQueue? _queue;
    private GPUTexture? _texture;
    private GPUBuffer? _uniforms;
    private GPUBindGroupLayout? _layout;
    private GPUPipelineLayout? _pipelineLayout;
    private GPURenderPipeline? _pipeline;
    private GPUSampler? _sampler;
    private GPUBindGroup? _bindGroup;
    private readonly float[] _uniformData = new float[20];

    public bool IsReady => _pipeline != null;

    /// <summary>The texture the HUD is drawn into.</summary>
    public GPUTextureView? View { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public void Init(GPUDevice device, GPUQueue queue, string format, int width, int height)
    {
        _device = device;
        _queue = queue;
        Width = width;
        Height = height;
        _texture = device.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { width, height },
            Format = format,
            Usage = GPUTextureUsage.RenderAttachment | GPUTextureUsage.TextureBinding | GPUTextureUsage.CopySrc,
        });
        View = _texture.CreateView();
        _uniforms = device.CreateBuffer(new GPUBufferDescriptor { Size = 80, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });
        _sampler = device.CreateSampler(new GPUSamplerDescriptor { MinFilter = "linear", MagFilter = "linear", AddressModeU = "clamp-to-edge", AddressModeV = "clamp-to-edge" });
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
        _bindGroup = device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = _layout,
            Entries = new GPUBindGroupEntry[]
            {
                new() { Binding = 0, Resource = new GPUBufferBinding { Buffer = _uniforms } },
                new() { Binding = 1, Resource = View },
                new() { Binding = 2, Resource = _sampler },
            },
        });
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

    /// <summary>
    /// Put the HUD over <paramref name="target"/> (a finished eye) on the quad whose corners are these clip-space positions (top left, top right, bottom left, bottom right).
    /// <paramref name="opacity"/> fades the whole panel.
    /// </summary>
    public void Draw(GPUCommandEncoder encoder, GPUTextureView target, Vector4 topLeft, Vector4 topRight, Vector4 bottomLeft, Vector4 bottomRight, float opacity = 1f)
    {
        if (_pipeline == null || _bindGroup == null || _queue == null) return;
        Put(0, topLeft); Put(4, topRight); Put(8, bottomLeft); Put(12, bottomRight);
        _uniformData[16] = opacity;
        var bytes = new byte[80];
        Buffer.BlockCopy(_uniformData, 0, bytes, 0, 80);
        _queue.WriteBuffer(_uniforms!, 0, bytes);
        using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[]
            {
                new GPURenderPassColorAttachment { View = target, LoadOp = GPULoadOp.Load, StoreOp = GPUStoreOp.Store },
            },
        });
        pass.SetPipeline(_pipeline);
        pass.SetBindGroup(0, _bindGroup);
        pass.Draw(6, 1, 0, 0);
        pass.End();

        void Put(int at, Vector4 v) { _uniformData[at] = v.X; _uniformData[at + 1] = v.Y; _uniformData[at + 2] = v.Z; _uniformData[at + 3] = v.W; }
    }

    /// <summary>A room-space point as the eye sees it: clip space (z already 0..1, as WebGPU wants it).</summary>
    public static Vector4 ClipOf(Vector3 roomPoint, Matrix4x4 roomView, Matrix4x4 projectionGl)
        => Vector4.Transform(new Vector4(roomPoint, 1f), roomView * projectionGl * AubsCraft.Admin.Rendering.ViewSetup.GlToStandardZ);

    public void Dispose()
    {
        _bindGroup?.Dispose(); _bindGroup = null;
        _pipeline?.Dispose(); _pipeline = null;
        _pipelineLayout?.Dispose(); _pipelineLayout = null;
        _layout?.Dispose(); _layout = null;
        _sampler?.Dispose(); _sampler = null;
        _uniforms?.Destroy(); _uniforms?.Dispose(); _uniforms = null;
        View?.Dispose(); View = null;
        _texture?.Destroy(); _texture?.Dispose(); _texture = null;
    }
}
