using System.Numerics;
using AubsCraft.Assets.Model;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>An icon to put on the HUD: where (top left, layout pixels), how big (a square) and which icon of the atlas.</summary>
public readonly record struct IconQuad(float X, float Y, float Size, int Cell);

/// <summary>
/// An icon for every block: a small isometric cube (its top and two sides from the block atlas, tinted as in the world), drawn once on the GPU into one texture, a cell of 64 px each. A plant
/// is its picture, a block with no texture is its flat colour. <see cref="Overlay"/> then puts the icons the HUD asks for on the HUD's texture (GameUI draws whole textures only, so the icons
/// are drawn over it by a pass of their own, after it). The block atlas never comes into .NET: the icons are made from it on the GPU.
/// </summary>
public sealed class BlockIconAtlas : IDisposable
{
    public const int Cell = 64, Columns = 32;

    private const string IconSource = """
        struct Icon { cell: u32, top: u32, left: u32, right: u32, tintTop: u32, tintLeft: u32, tintRight: u32, flags: u32 };
        struct Info { atlas: vec4<f32>, icons: vec4<f32> };
        @group(0) @binding(0) var<storage, read> icons: array<Icon>;
        @group(0) @binding(1) var<uniform> info: Info;
        @group(0) @binding(2) var blockTextures: texture_2d<f32>;
        struct VOut { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32>, @location(1) @interpolate(flat) tile: u32, @location(2) color: vec3<f32> };

        var<private> topP: array<vec2<f32>, 4> = array<vec2<f32>, 4>(vec2<f32>(0.5, 0.07), vec2<f32>(0.93, 0.285), vec2<f32>(0.5, 0.5), vec2<f32>(0.07, 0.285));
        var<private> leftP: array<vec2<f32>, 4> = array<vec2<f32>, 4>(vec2<f32>(0.07, 0.285), vec2<f32>(0.5, 0.5), vec2<f32>(0.5, 0.93), vec2<f32>(0.07, 0.715));
        var<private> rightP: array<vec2<f32>, 4> = array<vec2<f32>, 4>(vec2<f32>(0.5, 0.5), vec2<f32>(0.93, 0.285), vec2<f32>(0.93, 0.715), vec2<f32>(0.5, 0.93));
        var<private> spriteP: array<vec2<f32>, 4> = array<vec2<f32>, 4>(vec2<f32>(0.1, 0.1), vec2<f32>(0.9, 0.1), vec2<f32>(0.9, 0.9), vec2<f32>(0.1, 0.9));
        var<private> quadUV: array<vec2<f32>, 4> = array<vec2<f32>, 4>(vec2<f32>(0.0, 0.0), vec2<f32>(1.0, 0.0), vec2<f32>(1.0, 1.0), vec2<f32>(0.0, 1.0));
        var<private> corners: array<u32, 6> = array<u32, 6>(0u, 1u, 2u, 0u, 2u, 3u);

        fn unpack(c: u32) -> vec3<f32> { return vec3<f32>(f32(c & 255u), f32((c >> 8u) & 255u), f32((c >> 16u) & 255u)) / 255.0; }

        @vertex fn vs_main(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> VOut {
            let icon = icons[ii];
            let face = vi / 6u;
            let corner = corners[vi % 6u];
            var p = vec2<f32>(0.0, 0.0);
            var tile = icon.top;
            var tint = icon.tintTop;
            var shade = 1.0;
            let sprite = (icon.flags & 1u) != 0u;
            if (face == 0u) { p = select(topP[corner], spriteP[corner], sprite); }
            else if (face == 1u && !sprite) { p = leftP[corner]; tile = icon.left; tint = icon.tintLeft; shade = 0.78; }
            else if (face == 2u && !sprite) { p = rightP[corner]; tile = icon.right; tint = icon.tintRight; shade = 0.58; }
            let cell = vec2<f32>(f32(icon.cell % u32(info.icons.x)), f32(icon.cell / u32(info.icons.x)));
            let px = (cell + p) * info.icons.y;
            var o: VOut;
            o.position = vec4<f32>(px.x / info.atlas.z * 2.0 - 1.0, 1.0 - px.y / info.atlas.w * 2.0, 0.0, 1.0);
            o.uv = quadUV[corner];
            o.tile = tile;
            o.color = unpack(tint) * shade;
            return o;
        }

        @fragment fn fs_main(in: VOut) -> @location(0) vec4<f32> {
            var c = in.color;
            if (in.tile != 0xFFFFFFFFu) {
                let size = u32(info.atlas.y);
                let perRow = u32(info.atlas.x);
                let p = vec2<u32>(clamp(in.uv, vec2<f32>(0.0), vec2<f32>(0.999)) * f32(size));
                let t = textureLoad(blockTextures, vec2<i32>(i32((in.tile % perRow) * size + p.x), i32((in.tile / perRow) * size + p.y)), 0);
                if (t.a < 0.3) { discard; }
                c = c * t.rgb;
            }
            return vec4<f32>(c, 1.0);
        }
        """;

    private const string OverlaySource = """
        struct Quad { box: vec4<f32>, cell: vec4<f32> };   // x, y, size (layout pixels), cell
        struct Info { grid: vec4<f32>, icons: vec4<f32> };   // the HUD layout width and height; icon columns, cell px, atlas width and height
        @group(0) @binding(0) var<storage, read> quads: array<Quad>;
        @group(0) @binding(1) var<uniform> info: Info;
        @group(0) @binding(2) var iconTexture: texture_2d<f32>;
        @group(0) @binding(3) var iconSampler: sampler;
        struct VOut { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> };
        var<private> corners: array<u32, 6> = array<u32, 6>(0u, 1u, 2u, 0u, 2u, 3u);
        var<private> quadUV: array<vec2<f32>, 4> = array<vec2<f32>, 4>(vec2<f32>(0.0, 0.0), vec2<f32>(1.0, 0.0), vec2<f32>(1.0, 1.0), vec2<f32>(0.0, 1.0));
        @vertex fn vs_main(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> VOut {
            let q = quads[ii];
            let corner = corners[vi % 6u];
            let uv = quadUV[corner];
            let px = q.box.xy + uv * q.box.z;
            let cell = u32(q.cell.x);
            let columns = u32(info.icons.x);
            let origin = vec2<f32>(f32(cell % columns), f32(cell / columns)) * info.icons.y;
            var o: VOut;
            o.position = vec4<f32>(px.x / info.grid.x * 2.0 - 1.0, 1.0 - px.y / info.grid.y * 2.0, 0.0, 1.0);
            o.uv = (origin + uv * info.icons.y) / vec2<f32>(info.icons.z, info.icons.w);
            return o;
        }
        @fragment fn fs_main(in: VOut) -> @location(0) vec4<f32> {
            return textureSample(iconTexture, iconSampler, in.uv);
        }
        """;

    private readonly GPUDevice _device;
    private readonly GPUQueue _queue;
    private GPUTexture? _texture;
    private GPUTextureView? _view;
    private GPUBuffer? _overlayQuads, _overlayInfo;
    private GPUSampler? _sampler;
    private GPUBindGroupLayout? _overlayLayout;
    private GPUPipelineLayout? _overlayPipelineLayout;
    private GPURenderPipeline? _overlayPipeline;
    private GPUBindGroup? _overlayBind;
    private string? _overlayFormat;
    private int _width, _height;
    private const int MaxQuads = 256;

    /// <summary>Block name ("minecraft:stone") to its icon (a cell).</summary>
    public IReadOnlyDictionary<string, int> Cells { get; private set; } = new Dictionary<string, int>();

    public bool IsReady => _texture != null;

    private BlockIconAtlas(GPUDevice device, GPUQueue queue)
    {
        _device = device;
        _queue = queue;
    }

    /// <summary>The cell of a block's icon (the name may carry a state, <c>minecraft:oak_stairs[facing=east]</c>), or -1 for none.</summary>
    public int CellOf(string? block)
    {
        if (string.IsNullOrEmpty(block)) return -1;
        int open = block.IndexOf('[');
        return Cells.TryGetValue(open < 0 ? block : block[..open], out var cell) ? cell : -1;
    }

    private static uint Pack(int r, int g, int b) => (uint)(Math.Clamp(r, 0, 255) | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(b, 0, 255) << 16);

    /// <summary>Make the icons of these blocks (a cell each, in this order) from the block atlas.</summary>
    public static BlockIconAtlas Build(GPUDevice device, GPUQueue queue, BlockAssets assets, GPUTextureView blockAtlas, IReadOnlyList<string> names)
    {
        var atlas = new BlockIconAtlas(device, queue);
        var cells = new Dictionary<string, int>(StringComparer.Ordinal);
        var data = new List<uint>();
        const uint None = 0xFFFFFFFFu;
        foreach (var name in names)
        {
            if (cells.ContainsKey(name) || !assets.Blocks.TryGetValue(name, out var block) || block.Kind == BlockKinds.None) continue;
            bool flat = block.Faces.All(f => f < 0);
            var (fr, fg, fb) = BlockColorMap.GetColor(name);
            uint flatColour = Pack((int)(fr * 255), (int)(fg * 255), (int)(fb * 255));
            uint Tile(int face) => flat || block.Faces[face] < 0 ? None : (uint)block.Faces[face];
            uint Tint(int face) => flat ? flatColour : (block.TintFaces >> face & 1) != 0 ? Pack(block.TintColor[0], block.TintColor[1], block.TintColor[2]) : 0xFFFFFFu;
            bool sprite = block.Kind == BlockKinds.Cross;
            // +Y is the top, +Z the left side of the cube as drawn, +X the right
            int cell = cells.Count;
            cells[name] = cell;
            data.AddRange(new[] { (uint)cell, Tile(sprite ? 0 : 4), Tile(2), Tile(0), Tint(sprite ? 0 : 4), Tint(2), Tint(0), sprite ? 1u : 0u });
        }
        atlas.Cells = cells;
        if (cells.Count == 0) return atlas;

        int rows = (cells.Count + Columns - 1) / Columns;
        atlas._width = Columns * Cell;
        atlas._height = rows * Cell;
        atlas._texture = device.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { atlas._width, atlas._height },
            Format = "rgba8unorm",
            Usage = GPUTextureUsage.RenderAttachment | GPUTextureUsage.TextureBinding,
        });
        atlas._view = atlas._texture.CreateView();

        using var shader = device.CreateShaderModule(new GPUShaderModuleDescriptor { Code = IconSource });
        using var layout = device.CreateBindGroupLayout(new GPUBindGroupLayoutDescriptor
        {
            Entries = new GPUBindGroupLayoutEntry[]
            {
                new() { Binding = 0, Visibility = GPUShaderStageFlags.VERTEX, Buffer = new GPUBufferBindingLayout { Type = "read-only-storage" } },
                new() { Binding = 1, Visibility = GPUShaderStageFlags.VERTEX | GPUShaderStageFlags.FRAGMENT, Buffer = new GPUBufferBindingLayout { Type = "uniform" } },
                new() { Binding = 2, Visibility = GPUShaderStageFlags.FRAGMENT, Texture = new GPUTextureBindingLayout { SampleType = "float", ViewDimension = "2d" } },
            },
        });
        using var pipelineLayout = device.CreatePipelineLayout(new GPUPipelineLayoutDescriptor { BindGroupLayouts = new[] { layout } });
        using var pipeline = device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = pipelineLayout,
            Vertex = new GPUVertexState { Module = shader, EntryPoint = "vs_main" },
            Fragment = new GPUFragmentState { Module = shader, EntryPoint = "fs_main", Targets = new[] { new GPUColorTargetState { Format = "rgba8unorm" } } },
            Primitive = new GPUPrimitiveState { Topology = GPUPrimitiveTopology.TriangleList, CullMode = GPUCullMode.None },
        });

        var bytes = new byte[data.Count * 4];
        Buffer.BlockCopy(data.ToArray(), 0, bytes, 0, bytes.Length);
        using var iconBuffer = device.CreateBuffer(new GPUBufferDescriptor { Size = (ulong)bytes.Length, Usage = GPUBufferUsage.Storage | GPUBufferUsage.CopyDst });
        queue.WriteBuffer(iconBuffer, 0, bytes);
        using var infoBuffer = device.CreateBuffer(new GPUBufferDescriptor { Size = 32, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });
        var info = new float[] { assets.TilesPerRow, assets.TileSize, atlas._width, atlas._height, Columns, Cell, 0, 0 };
        var infoBytes = new byte[32];
        Buffer.BlockCopy(info, 0, infoBytes, 0, 32);
        queue.WriteBuffer(infoBuffer, 0, infoBytes);
        using var bind = device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = layout,
            Entries = new GPUBindGroupEntry[]
            {
                new() { Binding = 0, Resource = new GPUBufferBinding { Buffer = iconBuffer } },
                new() { Binding = 1, Resource = new GPUBufferBinding { Buffer = infoBuffer } },
                new() { Binding = 2, Resource = blockAtlas },
            },
        });
        using var encoder = device.CreateCommandEncoder();
        using (var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[] { new GPURenderPassColorAttachment { View = atlas._view, LoadOp = GPULoadOp.Clear, StoreOp = GPUStoreOp.Store, ClearValue = new double[] { 0, 0, 0, 0 } } },
        }))
        {
            pass.SetPipeline(pipeline);
            pass.SetBindGroup(0, bind);
            pass.Draw(18, (uint)cells.Count, 0, 0);
            pass.End();
        }
        using var commands = encoder.Finish();
        queue.Submit(new[] { commands });
        return atlas;
    }

    /// <summary>Put these icons over <paramref name="target"/> (the HUD's texture, <paramref name="format"/>), their positions in the HUD's layout pixels (<paramref name="layoutWidth"/> x <paramref name="layoutHeight"/>).</summary>
    public void Overlay(GPUCommandEncoder encoder, GPUTextureView target, string format, int layoutWidth, int layoutHeight, IReadOnlyList<IconQuad> quads)
    {
        if (_view == null || quads.Count == 0) return;
        if (_overlayPipeline == null || _overlayFormat != format) CreateOverlay(format);
        int n = Math.Min(quads.Count, MaxQuads);
        var data = new float[n * 8];
        for (int i = 0; i < n; i++)
        {
            data[i * 8] = quads[i].X; data[i * 8 + 1] = quads[i].Y; data[i * 8 + 2] = quads[i].Size; data[i * 8 + 3] = 0;
            data[i * 8 + 4] = quads[i].Cell;
        }
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        _queue.WriteBuffer(_overlayQuads!, 0, bytes);
        var info = new float[] { layoutWidth, layoutHeight, 0, 0, Columns, Cell, _width, _height };
        var infoBytes = new byte[32];
        Buffer.BlockCopy(info, 0, infoBytes, 0, 32);
        _queue.WriteBuffer(_overlayInfo!, 0, infoBytes);
        using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[] { new GPURenderPassColorAttachment { View = target, LoadOp = GPULoadOp.Load, StoreOp = GPUStoreOp.Store } },
        });
        pass.SetPipeline(_overlayPipeline!);
        pass.SetBindGroup(0, _overlayBind!);
        pass.Draw(6, (uint)n, 0, 0);
        pass.End();
    }

    private void CreateOverlay(string format)
    {
        DisposeOverlay();
        _overlayFormat = format;
        _overlayQuads = _device.CreateBuffer(new GPUBufferDescriptor { Size = MaxQuads * 32, Usage = GPUBufferUsage.Storage | GPUBufferUsage.CopyDst });
        _overlayInfo = _device.CreateBuffer(new GPUBufferDescriptor { Size = 32, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });
        _sampler = _device.CreateSampler(new GPUSamplerDescriptor { MinFilter = "linear", MagFilter = "linear", AddressModeU = "clamp-to-edge", AddressModeV = "clamp-to-edge" });
        _overlayLayout = _device.CreateBindGroupLayout(new GPUBindGroupLayoutDescriptor
        {
            Entries = new GPUBindGroupLayoutEntry[]
            {
                new() { Binding = 0, Visibility = GPUShaderStageFlags.VERTEX, Buffer = new GPUBufferBindingLayout { Type = "read-only-storage" } },
                new() { Binding = 1, Visibility = GPUShaderStageFlags.VERTEX, Buffer = new GPUBufferBindingLayout { Type = "uniform" } },
                new() { Binding = 2, Visibility = GPUShaderStageFlags.FRAGMENT, Texture = new GPUTextureBindingLayout { SampleType = "float", ViewDimension = "2d" } },
                new() { Binding = 3, Visibility = GPUShaderStageFlags.FRAGMENT, Sampler = new GPUSamplerBindingLayout { Type = "filtering" } },
            },
        });
        _overlayPipelineLayout = _device.CreatePipelineLayout(new GPUPipelineLayoutDescriptor { BindGroupLayouts = new[] { _overlayLayout } });
        using var shader = _device.CreateShaderModule(new GPUShaderModuleDescriptor { Code = OverlaySource });
        var over = new GPUBlendComponent { SrcFactor = GPUBlendFactor.One, DstFactor = GPUBlendFactor.OneMinusSrcAlpha, Operation = GPUBlendOperation.Add };
        _overlayPipeline = _device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = _overlayPipelineLayout,
            Vertex = new GPUVertexState { Module = shader, EntryPoint = "vs_main" },
            Fragment = new GPUFragmentState { Module = shader, EntryPoint = "fs_main", Targets = new[] { new GPUColorTargetState { Format = format, Blend = new GPUBlendState { Color = over, Alpha = over } } } },
            Primitive = new GPUPrimitiveState { Topology = GPUPrimitiveTopology.TriangleList, CullMode = GPUCullMode.None },
        });
        _overlayBind = _device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = _overlayLayout,
            Entries = new GPUBindGroupEntry[]
            {
                new() { Binding = 0, Resource = new GPUBufferBinding { Buffer = _overlayQuads } },
                new() { Binding = 1, Resource = new GPUBufferBinding { Buffer = _overlayInfo } },
                new() { Binding = 2, Resource = _view },
                new() { Binding = 3, Resource = _sampler },
            },
        });
    }

    private void DisposeOverlay()
    {
        _overlayBind?.Dispose(); _overlayBind = null;
        _overlayPipeline?.Dispose(); _overlayPipeline = null;
        _overlayPipelineLayout?.Dispose(); _overlayPipelineLayout = null;
        _overlayLayout?.Dispose(); _overlayLayout = null;
        _sampler?.Dispose(); _sampler = null;
        _overlayQuads?.Destroy(); _overlayQuads?.Dispose(); _overlayQuads = null;
        _overlayInfo?.Destroy(); _overlayInfo?.Dispose(); _overlayInfo = null;
    }

    public void Dispose()
    {
        DisposeOverlay();
        _view?.Dispose(); _view = null;
        _texture?.Destroy(); _texture?.Dispose(); _texture = null;
    }
}
