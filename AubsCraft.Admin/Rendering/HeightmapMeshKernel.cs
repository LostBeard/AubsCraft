using ILGPU;
using ILGPU.Runtime;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Per-column heightmap data packed into a single struct to reduce GPU binding count.
/// Fields use int (not short) to avoid sub-word struct complications on WebGPU.
/// Wire format is Int16 - conversion happens once at upload time.
/// </summary>
public struct HeightmapColumn
{
    public int Height;
    public int BlockId;
    public int SeabedHeight;
    public int SeabedBlockId;
}

/// <summary>
/// Per-palette-entry data packed into a single struct.
/// Combines palette colors, atlas UVs, and block flags into one binding.
/// </summary>
public struct BlockPalette
{
    public float R, G, B;
    public float U0, V0, U1, V1;
    public float Flag;
}

/// <summary>
/// ILGPU compute kernel for generating heightmap mesh vertices.
/// 256 threads (16x16 grid), each thread handles one column.
///
/// Uses struct parameters to keep binding count under WebGPU's
/// maxStorageBuffersPerShaderStage limit (typically 10).
/// 5 ArrayViews + 1 scalar buffer = 6 bindings (was 12, which exceeded the limit).
///
/// Produces two vertex streams: opaque (terrain + seabed) and water (transparent).
/// Vertex format: 11 floats per vertex (position.xyz + normal.xyz + color.rgb + uv.xy).
/// </summary>
public static class HeightmapMeshKernel
{
    private const int W = 16;
    private const int FPV = 11; // floats per vertex
    private const int FPF = FPV * 6; // floats per face (6 vertices)

    /// <summary>
    /// GPU kernel: generates heightmap mesh for one chunk.
    /// Each thread = one column (x, z) of the 16x16 chunk.
    ///
    /// columns: HeightmapColumn[256] - height, blockId, seabedHeight, seabedBlockId per column
    /// palette: BlockPalette[paletteSize] - RGB + atlas UVs + flags per palette entry
    /// opaqueVerts: output buffer for opaque vertices
    /// waterVerts: output buffer for water vertices
    /// counters: int[2] - [0]=opaque floats written, [1]=water floats written
    /// chunkWorldX, chunkWorldZ: world-space chunk coordinates
    /// </summary>
    public static void Kernel(
        Index1D index,
        ArrayView<HeightmapColumn> columns,
        ArrayView<BlockPalette> palette,
        ArrayView<float> opaqueVerts,
        ArrayView<float> waterVerts,
        ArrayView<int> counters,
        int chunkWorldX,
        int chunkWorldZ)
    {
        int x = index % W;
        int z = index / W;
        int col = x + z * W;

        var column = columns[col];
        int blockId = column.BlockId;
        if (blockId == 0) return;

        var entry = palette[blockId];

        // Skip plant blocks in heightmap
        float flag = entry.Flag;
        if (flag > 0.5f && flag < 1.5f) return; // 1 = plant

        float wx = chunkWorldX * W + x;
        float wy = column.Height;
        float wz = chunkWorldZ * W + z;

        float cr = entry.R;
        float cg = entry.G;
        float cb = entry.B;

        bool isWater = flag > 1.5f && flag < 2.5f; // exactly 2 = water (not 3 = solid tinted)
        float topY = isWater ? wy - 0.1f : wy;

        // Atlas UVs for this block
        float u0 = entry.U0;
        float v0 = entry.V0;
        float u1 = entry.U1;
        float v1 = entry.V1;

        // Tint handling: biome-tinted blocks keep their vertex color (multiplied
        // with grayscale texture). Non-tinted textured blocks get white (1,1,1).
        // Flags: 0=solid non-tinted, 1=plant (tinted), 2=water (tinted), 3=solid tinted (grass/leaves)
        bool hasTexture = u0 >= 0f;
        bool isTinted = flag > 0.5f; // 1=plant, 2=water, 3=tinted solid
        float tr = cr, tg = cg, tb = cb;
        if (hasTexture && !isTinted)
        {
            tr = 1f; tg = 1f; tb = 1f;
        }

        float ty = topY + 1;

        // Water top face -> water buffer (counters[1])
        if (isWater)
        {
            int wo = Atomic.Add(ref counters[1], FPF);
            if (wo + FPF > waterVerts.IntLength) return;
            WV(waterVerts, wo,      wx,     ty, wz,     0, 1, 0, tr, tg, tb, u0, v0);
            WV(waterVerts, wo + 11, wx,     ty, wz + 1, 0, 1, 0, tr, tg, tb, u0, v1);
            WV(waterVerts, wo + 22, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            WV(waterVerts, wo + 33, wx,     ty, wz,     0, 1, 0, tr, tg, tb, u0, v0);
            WV(waterVerts, wo + 44, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            WV(waterVerts, wo + 55, wx + 1, ty, wz,     0, 1, 0, tr, tg, tb, u1, v0);
        }
        else
        {
            // Opaque top face (counters[0])
            int oo = Atomic.Add(ref counters[0], FPF);
            if (oo + FPF > opaqueVerts.IntLength) return;
            WV(opaqueVerts, oo,      wx,     ty, wz,     0, 1, 0, tr, tg, tb, u0, v0);
            WV(opaqueVerts, oo + 11, wx,     ty, wz + 1, 0, 1, 0, tr, tg, tb, u0, v1);
            WV(opaqueVerts, oo + 22, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            WV(opaqueVerts, oo + 33, wx,     ty, wz,     0, 1, 0, tr, tg, tb, u0, v0);
            WV(opaqueVerts, oo + 44, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            WV(opaqueVerts, oo + 55, wx + 1, ty, wz,     0, 1, 0, tr, tg, tb, u1, v0);

            // Side faces where terrain drops
            float sr = cr * 0.82f, sg = cg * 0.82f, sb = cb * 0.82f;
            float y1 = wy + 1;

            // -X side
            int nh = GetH(columns, x - 1, z);
            if (wy > nh)
                EmitSide(opaqueVerts, counters, wx, nh + 1, y1, wz, -1, 0, 0, sr, sg, sb, 0);

            // +X side
            nh = GetH(columns, x + 1, z);
            if (wy > nh)
                EmitSide(opaqueVerts, counters, wx + 1, nh + 1, y1, wz, 1, 0, 0, sr, sg, sb, 1);

            // -Z side
            nh = GetH(columns, x, z - 1);
            if (wy > nh)
                EmitSide(opaqueVerts, counters, wx, nh + 1, y1, wz, 0, 0, -1, sr * 0.92f, sg * 0.92f, sb * 0.92f, 2);

            // +Z side
            nh = GetH(columns, x, z + 1);
            if (wy > nh)
                EmitSide(opaqueVerts, counters, wx, nh + 1, y1, wz + 1, 0, 0, 1, sr * 0.92f, sg * 0.92f, sb * 0.92f, 3);
        }

        // Seabed pass: render solid terrain under water as darkened opaque
        int sbHeight = column.SeabedHeight;
        int sbId = column.SeabedBlockId;
        if (sbId > 0 && sbHeight > -64)
        {
            var sbEntry = palette[sbId];

            // Skip plants on seabed
            if (sbEntry.Flag > 0.5f && sbEntry.Flag < 1.5f) return;

            float sbr = sbEntry.R * 0.7f;
            float sbg = sbEntry.G * 0.7f;
            float sbb = sbEntry.B * 0.7f;

            float sbu0 = sbEntry.U0;
            float sbv0 = sbEntry.V0;
            float sbu1 = sbEntry.U1;
            float sbv1 = sbEntry.V1;

            float sbty = sbHeight + 1;
            int so = Atomic.Add(ref counters[0], FPF);
            WV(opaqueVerts, so,      wx,     sbty, wz,     0, 1, 0, sbr, sbg, sbb, sbu0, sbv0);
            WV(opaqueVerts, so + 11, wx,     sbty, wz + 1, 0, 1, 0, sbr, sbg, sbb, sbu0, sbv1);
            WV(opaqueVerts, so + 22, wx + 1, sbty, wz + 1, 0, 1, 0, sbr, sbg, sbb, sbu1, sbv1);
            WV(opaqueVerts, so + 33, wx,     sbty, wz,     0, 1, 0, sbr, sbg, sbb, sbu0, sbv0);
            WV(opaqueVerts, so + 44, wx + 1, sbty, wz + 1, 0, 1, 0, sbr, sbg, sbb, sbu1, sbv1);
            WV(opaqueVerts, so + 55, wx + 1, sbty, wz,     0, 1, 0, sbr, sbg, sbb, sbu1, sbv0);

            // Seabed cliff sides
            float ssr = sbr * 0.75f, ssg = sbg * 0.75f, ssb = sbb * 0.75f;
            float sby1 = sbHeight + 1;

            int snh = GetSeabedH(columns, x - 1, z);
            if (sbHeight > snh)
                EmitSide(opaqueVerts, counters, wx, snh + 1, sby1, wz, -1, 0, 0, ssr, ssg, ssb, 0);

            snh = GetSeabedH(columns, x + 1, z);
            if (sbHeight > snh)
                EmitSide(opaqueVerts, counters, wx + 1, snh + 1, sby1, wz, 1, 0, 0, ssr, ssg, ssb, 1);

            snh = GetSeabedH(columns, x, z - 1);
            if (sbHeight > snh)
                EmitSide(opaqueVerts, counters, wx, snh + 1, sby1, wz, 0, 0, -1, ssr, ssg, ssb, 2);

            snh = GetSeabedH(columns, x, z + 1);
            if (sbHeight > snh)
                EmitSide(opaqueVerts, counters, wx, snh + 1, sby1, wz + 1, 0, 0, 1, ssr, ssg, ssb, 3);
        }
    }

    private static int GetH(ArrayView<HeightmapColumn> columns, int x, int z)
    {
        if (x < 0 || x >= W || z < 0 || z >= W) return -64;
        return columns[x + z * W].Height;
    }

    private static int GetSeabedH(ArrayView<HeightmapColumn> columns, int x, int z)
    {
        if (x < 0 || x >= W || z < 0 || z >= W) return -64;
        return columns[x + z * W].SeabedHeight;
    }

    private static void EmitSide(
        ArrayView<float> verts, ArrayView<int> counters,
        float x, float y0, float y1, float z,
        float nx, float ny, float nz,
        float r, float g, float b,
        int face)
    {
        float snu = -1f; // no texture for sides
        int o = Atomic.Add(ref counters[0], FPF);
        if (o + FPF > verts.IntLength) return;

        switch (face)
        {
            case 0: // -X
                WV(verts, o,      x, y0, z + 1, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 11, x, y1, z + 1, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 22, x, y1, z,     nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 33, x, y0, z + 1, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 44, x, y1, z,     nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 55, x, y0, z,     nx, ny, nz, r, g, b, snu, snu);
                break;
            case 1: // +X
                WV(verts, o,      x, y0, z,     nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 11, x, y1, z,     nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 22, x, y1, z + 1, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 33, x, y0, z,     nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 44, x, y1, z + 1, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 55, x, y0, z + 1, nx, ny, nz, r, g, b, snu, snu);
                break;
            case 2: // -Z
                WV(verts, o,      x,     y0, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 11, x,     y1, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 22, x + 1, y1, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 33, x,     y0, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 44, x + 1, y1, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 55, x + 1, y0, z, nx, ny, nz, r, g, b, snu, snu);
                break;
            case 3: // +Z
                WV(verts, o,      x + 1, y0, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 11, x + 1, y1, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 22, x,     y1, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 33, x + 1, y0, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 44, x,     y1, z, nx, ny, nz, r, g, b, snu, snu);
                WV(verts, o + 55, x,     y0, z, nx, ny, nz, r, g, b, snu, snu);
                break;
        }
    }

    private static void WV(ArrayView<float> v, int o,
        float px, float py, float pz, float nx, float ny, float nz,
        float cr, float cg, float cb, float u, float uv)
    {
        v[o] = px; v[o + 1] = py; v[o + 2] = pz;
        v[o + 3] = nx; v[o + 4] = ny; v[o + 5] = nz;
        v[o + 6] = cr; v[o + 7] = cg; v[o + 8] = cb;
        v[o + 9] = u; v[o + 10] = uv;
    }
}
