using ILGPU;
using ILGPU.Runtime;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// ILGPU compute kernel for generating heightmap mesh vertices.
/// Replaces the CPU HeightmapMesher - all mesh generation runs on GPU.
/// 256 threads (16x16 grid), each thread handles one column.
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
    /// heights: int[256] - Y height per column
    /// blockIds: ushort stored as int[256] - block ID per column
    /// paletteColors: float[paletteSize * 3] - RGB per palette entry
    /// atlasUVs: float[paletteSize * 4] - (u0, v0, u1, v1) per palette entry
    /// blockFlags: float[paletteSize] - 0=solid, 1=plant (skip), 2=water
    /// seabedHeights: int[256] - seabed Y per column (-64 = no seabed)
    /// seabedBlockIds: int[256] - seabed block ID per column
    /// opaqueVerts: output buffer for opaque vertices
    /// opaqueCounter: atomic counter for opaque floats written
    /// waterVerts: output buffer for water vertices
    /// waterCounter: atomic counter for water floats written
    /// chunkWorldX, chunkWorldZ: world-space chunk coordinates
    /// </summary>
    public static void Kernel(
        Index1D index,
        ArrayView<int> heights,
        ArrayView<short> blockIds,
        ArrayView<float> paletteColors,
        ArrayView<float> atlasUVs,
        ArrayView<float> blockFlags,
        ArrayView<int> seabedHeights,
        ArrayView<short> seabedBlockIds,
        ArrayView<float> opaqueVerts,
        ArrayView<int> opaqueCounter,
        ArrayView<float> waterVerts,
        ArrayView<int> waterCounter,
        int chunkWorldX,
        int chunkWorldZ)
    {
        int x = index % W;
        int z = index / W;
        int col = x + z * W;

        int blockId = blockIds[col];
        if (blockId == 0) return;

        // Skip plant blocks in heightmap
        float flag = blockFlags[blockId];
        if (flag > 0.5f && flag < 1.5f) return; // 1 = plant

        float wx = chunkWorldX * W + x;
        float wy = heights[col];
        float wz = chunkWorldZ * W + z;

        float cr = paletteColors[blockId * 3];
        float cg = paletteColors[blockId * 3 + 1];
        float cb = paletteColors[blockId * 3 + 2];

        bool isWater = flag > 1.5f; // 2 = water
        float topY = isWater ? wy - 0.1f : wy;

        // Atlas UVs for this block
        float u0 = atlasUVs[blockId * 4];
        float v0 = atlasUVs[blockId * 4 + 1];
        float u1 = atlasUVs[blockId * 4 + 2];
        float v1 = atlasUVs[blockId * 4 + 3];

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

        // Water top face -> water buffer
        if (isWater)
        {
            int wo = Atomic.Add(ref waterCounter[0], FPF);
            WV(waterVerts, wo,      wx,     ty, wz,     0, 1, 0, tr, tg, tb, u0, v0);
            WV(waterVerts, wo + 11, wx,     ty, wz + 1, 0, 1, 0, tr, tg, tb, u0, v1);
            WV(waterVerts, wo + 22, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            WV(waterVerts, wo + 33, wx,     ty, wz,     0, 1, 0, tr, tg, tb, u0, v0);
            WV(waterVerts, wo + 44, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            WV(waterVerts, wo + 55, wx + 1, ty, wz,     0, 1, 0, tr, tg, tb, u1, v0);
        }
        else
        {
            // Opaque top face
            int oo = Atomic.Add(ref opaqueCounter[0], FPF);
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
            int nh = GetH(heights, x - 1, z);
            if (wy > nh)
                EmitSide(opaqueVerts, opaqueCounter, wx, nh + 1, y1, wz, -1, 0, 0, sr, sg, sb, 0);

            // +X side
            nh = GetH(heights, x + 1, z);
            if (wy > nh)
                EmitSide(opaqueVerts, opaqueCounter, wx + 1, nh + 1, y1, wz, 1, 0, 0, sr, sg, sb, 1);

            // -Z side
            nh = GetH(heights, x, z - 1);
            if (wy > nh)
                EmitSide(opaqueVerts, opaqueCounter, wx, nh + 1, y1, wz, 0, 0, -1, sr * 0.92f, sg * 0.92f, sb * 0.92f, 2);

            // +Z side
            nh = GetH(heights, x, z + 1);
            if (wy > nh)
                EmitSide(opaqueVerts, opaqueCounter, wx, nh + 1, y1, wz + 1, 0, 0, 1, sr * 0.92f, sg * 0.92f, sb * 0.92f, 3);
        }

        // Seabed pass: render solid terrain under water as darkened opaque
        int sbHeight = seabedHeights[col];
        int sbId = seabedBlockIds[col];
        if (sbId > 0 && sbHeight > -64)
        {
            // Skip plants on seabed
            if (blockFlags[sbId] > 0.5f && blockFlags[sbId] < 1.5f) return;

            float sbr = paletteColors[sbId * 3] * 0.7f;
            float sbg = paletteColors[sbId * 3 + 1] * 0.7f;
            float sbb = paletteColors[sbId * 3 + 2] * 0.7f;

            float sbu0 = atlasUVs[sbId * 4];
            float sbv0 = atlasUVs[sbId * 4 + 1];
            float sbu1 = atlasUVs[sbId * 4 + 2];
            float sbv1 = atlasUVs[sbId * 4 + 3];

            float sbty = sbHeight + 1;
            int so = Atomic.Add(ref opaqueCounter[0], FPF);
            WV(opaqueVerts, so,      wx,     sbty, wz,     0, 1, 0, sbr, sbg, sbb, sbu0, sbv0);
            WV(opaqueVerts, so + 11, wx,     sbty, wz + 1, 0, 1, 0, sbr, sbg, sbb, sbu0, sbv1);
            WV(opaqueVerts, so + 22, wx + 1, sbty, wz + 1, 0, 1, 0, sbr, sbg, sbb, sbu1, sbv1);
            WV(opaqueVerts, so + 33, wx,     sbty, wz,     0, 1, 0, sbr, sbg, sbb, sbu0, sbv0);
            WV(opaqueVerts, so + 44, wx + 1, sbty, wz + 1, 0, 1, 0, sbr, sbg, sbb, sbu1, sbv1);
            WV(opaqueVerts, so + 55, wx + 1, sbty, wz,     0, 1, 0, sbr, sbg, sbb, sbu1, sbv0);

            // Seabed cliff sides
            float ssr = sbr * 0.75f, ssg = sbg * 0.75f, ssb = sbb * 0.75f;
            float sby1 = sbHeight + 1;

            int snh = GetH(seabedHeights, x - 1, z);
            if (sbHeight > snh)
                EmitSide(opaqueVerts, opaqueCounter, wx, snh + 1, sby1, wz, -1, 0, 0, ssr, ssg, ssb, 0);

            snh = GetH(seabedHeights, x + 1, z);
            if (sbHeight > snh)
                EmitSide(opaqueVerts, opaqueCounter, wx + 1, snh + 1, sby1, wz, 1, 0, 0, ssr, ssg, ssb, 1);

            snh = GetH(seabedHeights, x, z - 1);
            if (sbHeight > snh)
                EmitSide(opaqueVerts, opaqueCounter, wx, snh + 1, sby1, wz, 0, 0, -1, ssr, ssg, ssb, 2);

            snh = GetH(seabedHeights, x, z + 1);
            if (sbHeight > snh)
                EmitSide(opaqueVerts, opaqueCounter, wx, snh + 1, sby1, wz + 1, 0, 0, 1, ssr, ssg, ssb, 3);
        }
    }

    private static int GetH(ArrayView<int> heights, int x, int z)
    {
        if (x < 0 || x >= W || z < 0 || z >= W) return -64;
        return heights[x + z * W];
    }

    private static void EmitSide(
        ArrayView<float> verts, ArrayView<int> counter,
        float x, float y0, float y1, float z,
        float nx, float ny, float nz,
        float r, float g, float b,
        int face)
    {
        float snu = -1f; // no texture for sides
        int o = Atomic.Add(ref counter[0], FPF);

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
