using ILGPU;
using ILGPU.Runtime;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// ILGPU compute kernel for generating voxel mesh from Minecraft block data.
/// Runs on all SpawnDev.ILGPU backends (WebGPU, WebGL, Wasm, CUDA, OpenCL, CPU).
///
/// Vertex format: 11 floats per vertex (position.xyz + normal.xyz + color.rgb + uv.xy).
/// Uses atomic counters for variable-length output.
/// Produces two vertex streams: opaque geometry and transparent geometry (water).
/// Block flags: 0 = solid opaque, 1 = plant (cross-quad, transparent), 2 = water (transparent).
/// </summary>
public static class MinecraftMeshKernel
{
    private const int SizeXZ = 16;
    private const int Height = 384;
    private const int SizeXZ2 = SizeXZ * SizeXZ;
    private const int FloatsPerVertex = 11;
    private const int FloatsPerFace = FloatsPerVertex * 6; // 66

    // Block flag values in the blockFlags buffer
    private const float FLAG_SOLID = 0f;
    private const float FLAG_PLANT = 1f;
    private const float FLAG_WATER = 2f;

    /// <summary>
    /// GPU kernel: generates textured mesh vertices for a Minecraft chunk.
    /// Produces two output streams: opaque vertices and water (transparent) vertices.
    /// blockFlags: per-palette float - 0=solid, 1=plant (cross-quad), 2=water (transparent)
    /// opaqueVerts/opaqueCounter: opaque geometry output (solid blocks + plants)
    /// waterVerts/waterCounter: transparent geometry output (water blocks)
    /// </summary>
    public static void MeshKernel(
        Index1D index,
        ArrayView<int> blocks,
        ArrayView<float> paletteColors,
        ArrayView<float> atlasUVs,
        ArrayView<float> blockFlags,
        ArrayView<float> opaqueVerts,
        ArrayView<int> opaqueCounter,
        ArrayView<float> waterVerts,
        ArrayView<int> waterCounter,
        int chunkWorldX,
        int chunkWorldZ)
    {
        int y = index / SizeXZ2;
        int rem = index - y * SizeXZ2;
        int z = rem / SizeXZ;
        int x = rem - z * SizeXZ;

        int blockId = blocks[index];
        if (blockId == 0) return;

        float flag = blockFlags[blockId];
        float baseR = paletteColors[blockId * 3];
        float baseG = paletteColors[blockId * 3 + 1];
        float baseB = paletteColors[blockId * 3 + 2];

        // Tint: flags 1=plant, 2=water, 3=solid tinted keep vertex color.
        // Flag 0 = solid non-tinted: if textured, use white so texture shows true color.
        if (flag < 0.5f)
        {
            int uvBase0 = blockId * 12;
            if (atlasUVs[uvBase0] >= 0f)
            { baseR = 1f; baseG = 1f; baseB = 1f; }
        }

        float wx = chunkWorldX * SizeXZ + x;
        float wy = y - 64f;
        float wz = chunkWorldZ * SizeXZ + z;

        // Plant blocks: emit cross-shaped quads to the opaque buffer
        if (flag > 0.5f && flag < 1.5f)
        {
            int uvBase = blockId * 12;
            float u0 = atlasUVs[uvBase];
            float v0 = atlasUVs[uvBase + 1];
            float u1 = atlasUVs[uvBase + 2];
            float v1 = atlasUVs[uvBase + 3];
            EmitCrossQuads(opaqueVerts, opaqueCounter, wx, wy, wz, baseR, baseG, baseB, u0, v0, u1, v1);
            return;
        }

        // Water blocks: emit faces to the water buffer (transparent pass)
        // Water emits faces toward air or solid blocks, but NOT toward other water
        if (flag > 1.5f)
        {
            int uvBase = blockId * 12;
            float u0 = atlasUVs[uvBase];
            float v0 = atlasUVs[uvBase + 1];
            float u1 = atlasUVs[uvBase + 2];
            float v1 = atlasUVs[uvBase + 3];
            // Water top face is slightly below block top (0.9 instead of 1.0)
            if (IsAirOrPlant(blocks, blockFlags, x + 1, y, z))
                EmitFace(waterVerts, waterCounter, wx + 1, wy, wz, 1f, 0f, 0f, baseR, baseG, baseB, u0, v0, u1, v1, 3);
            if (IsAirOrPlant(blocks, blockFlags, x - 1, y, z))
                EmitFace(waterVerts, waterCounter, wx, wy, wz, -1f, 0f, 0f, baseR, baseG, baseB, u0, v0, u1, v1, 2);
            if (GetBlock(blocks, x, y + 1, z) == 0)
                EmitFace(waterVerts, waterCounter, wx, wy + 0.9f, wz, 0f, 1f, 0f, baseR, baseG, baseB, u0, v0, u1, v1, 0);
            if (IsAirOrPlant(blocks, blockFlags, x, y - 1, z))
                EmitFace(waterVerts, waterCounter, wx, wy, wz, 0f, -1f, 0f, baseR * 0.7f, baseG * 0.7f, baseB * 0.7f, u0, v0, u1, v1, 1);
            if (IsAirOrPlant(blocks, blockFlags, x, y, z + 1))
                EmitFace(waterVerts, waterCounter, wx, wy, wz + 1, 0f, 0f, 1f, baseR, baseG, baseB, u0, v0, u1, v1, 5);
            if (IsAirOrPlant(blocks, blockFlags, x, y, z - 1))
                EmitFace(waterVerts, waterCounter, wx, wy, wz, 0f, 0f, -1f, baseR, baseG, baseB, u0, v0, u1, v1, 4);
            return;
        }

        // Solid opaque blocks: emit faces toward air, plants, and water
        {
            int uvBase = blockId * 12;
            float topU0 = atlasUVs[uvBase];
            float topV0 = atlasUVs[uvBase + 1];
            float topU1 = atlasUVs[uvBase + 2];
            float topV1 = atlasUVs[uvBase + 3];
            float sideU0 = atlasUVs[uvBase + 4];
            float sideV0 = atlasUVs[uvBase + 5];
            float sideU1 = atlasUVs[uvBase + 6];
            float sideV1 = atlasUVs[uvBase + 7];
            float botU0 = atlasUVs[uvBase + 8];
            float botV0 = atlasUVs[uvBase + 9];
            float botU1 = atlasUVs[uvBase + 10];
            float botV1 = atlasUVs[uvBase + 11];

            // Emit face if neighbor is air, plant, or water (any transparent block)
            if (IsTransparent(blocks, blockFlags, x + 1, y, z))
                EmitFace(opaqueVerts, opaqueCounter, wx + 1, wy, wz, 1f, 0f, 0f, baseR * 0.88f, baseG * 0.88f, baseB * 0.88f, sideU0, sideV0, sideU1, sideV1, 3);
            if (IsTransparent(blocks, blockFlags, x - 1, y, z))
                EmitFace(opaqueVerts, opaqueCounter, wx, wy, wz, -1f, 0f, 0f, baseR * 0.88f, baseG * 0.88f, baseB * 0.88f, sideU0, sideV0, sideU1, sideV1, 2);
            if (IsTransparent(blocks, blockFlags, x, y + 1, z))
                EmitFace(opaqueVerts, opaqueCounter, wx, wy + 1, wz, 0f, 1f, 0f, baseR * 1.05f, baseG * 1.05f, baseB * 1.05f, topU0, topV0, topU1, topV1, 0);
            if (IsTransparent(blocks, blockFlags, x, y - 1, z))
                EmitFace(opaqueVerts, opaqueCounter, wx, wy, wz, 0f, -1f, 0f, baseR * 0.70f, baseG * 0.70f, baseB * 0.70f, botU0, botV0, botU1, botV1, 1);
            if (IsTransparent(blocks, blockFlags, x, y, z + 1))
                EmitFace(opaqueVerts, opaqueCounter, wx, wy, wz + 1, 0f, 0f, 1f, baseR * 0.82f, baseG * 0.82f, baseB * 0.82f, sideU0, sideV0, sideU1, sideV1, 5);
            if (IsTransparent(blocks, blockFlags, x, y, z - 1))
                EmitFace(opaqueVerts, opaqueCounter, wx, wy, wz, 0f, 0f, -1f, baseR * 0.82f, baseG * 0.82f, baseB * 0.82f, sideU0, sideV0, sideU1, sideV1, 4);
        }
    }

    private static int GetBlock(ArrayView<int> blocks, int x, int y, int z)
    {
        if (x < 0 || x >= SizeXZ || y < 0 || y >= Height || z < 0 || z >= SizeXZ)
            return 0;
        return blocks[x + z * SizeXZ + y * SizeXZ2];
    }

    /// <summary>Returns true if neighbor is air or a non-water transparent block (plant).</summary>
    private static bool IsAirOrPlant(ArrayView<int> blocks, ArrayView<float> blockFlags, int x, int y, int z)
    {
        int id = GetBlock(blocks, x, y, z);
        if (id == 0) return true;
        float f = blockFlags[id];
        return f > 0.5f && f < 1.5f; // plant only, not water
    }

    /// <summary>Returns true if neighbor is air or any transparent block (plant or water).</summary>
    private static bool IsTransparent(ArrayView<int> blocks, ArrayView<float> blockFlags, int x, int y, int z)
    {
        int id = GetBlock(blocks, x, y, z);
        if (id == 0) return true;
        return blockFlags[id] > 0.5f; // plant (1.0) or water (2.0)
    }

    /// <summary>
    /// Emits two diagonal cross-shaped quads for plant/flower blocks.
    /// Creates an X-shape when viewed from above with both front and back faces.
    /// 4 triangles x 2 diagonals = 8 triangles = 48 vertices x 11 floats = 528 floats.
    /// </summary>
    private static void EmitCrossQuads(
        ArrayView<float> vertices, ArrayView<int> counter,
        float x, float y, float z,
        float cr, float cg, float cb,
        float u0, float v0, float u1, float v1)
    {
        // 24 vertices per quad pair (2 diags x 2 sides x 6 verts)
        int offset = Atomic.Add(ref counter[0], FloatsPerFace * 4);

        // Diagonal 1: (x,z) to (x+1,z+1) - front face
        float nx1 = 0.707f, nz1 = -0.707f;
        WV(vertices, offset,       x, y, z,         nx1, 0, nz1, cr, cg, cb, u0, v1);
        WV(vertices, offset + 11,  x, y + 1, z,     nx1, 0, nz1, cr, cg, cb, u0, v0);
        WV(vertices, offset + 22,  x+1, y + 1, z+1, nx1, 0, nz1, cr, cg, cb, u1, v0);
        WV(vertices, offset + 33,  x, y, z,         nx1, 0, nz1, cr, cg, cb, u0, v1);
        WV(vertices, offset + 44,  x+1, y + 1, z+1, nx1, 0, nz1, cr, cg, cb, u1, v0);
        WV(vertices, offset + 55,  x+1, y, z+1,     nx1, 0, nz1, cr, cg, cb, u1, v1);

        // Diagonal 1: back face (flipped winding)
        int o2 = offset + FloatsPerFace;
        WV(vertices, o2,       x+1, y, z+1,     -nx1, 0, -nz1, cr, cg, cb, u0, v1);
        WV(vertices, o2 + 11,  x+1, y + 1, z+1, -nx1, 0, -nz1, cr, cg, cb, u0, v0);
        WV(vertices, o2 + 22,  x, y + 1, z,     -nx1, 0, -nz1, cr, cg, cb, u1, v0);
        WV(vertices, o2 + 33,  x+1, y, z+1,     -nx1, 0, -nz1, cr, cg, cb, u0, v1);
        WV(vertices, o2 + 44,  x, y + 1, z,     -nx1, 0, -nz1, cr, cg, cb, u1, v0);
        WV(vertices, o2 + 55,  x, y, z,         -nx1, 0, -nz1, cr, cg, cb, u1, v1);

        // Diagonal 2: (x+1,z) to (x,z+1) - front face
        float nx2 = -0.707f, nz2 = -0.707f;
        int o3 = offset + FloatsPerFace * 2;
        WV(vertices, o3,       x+1, y, z,       nx2, 0, nz2, cr, cg, cb, u0, v1);
        WV(vertices, o3 + 11,  x+1, y + 1, z,   nx2, 0, nz2, cr, cg, cb, u0, v0);
        WV(vertices, o3 + 22,  x, y + 1, z+1,   nx2, 0, nz2, cr, cg, cb, u1, v0);
        WV(vertices, o3 + 33,  x+1, y, z,       nx2, 0, nz2, cr, cg, cb, u0, v1);
        WV(vertices, o3 + 44,  x, y + 1, z+1,   nx2, 0, nz2, cr, cg, cb, u1, v0);
        WV(vertices, o3 + 55,  x, y, z+1,       nx2, 0, nz2, cr, cg, cb, u1, v1);

        // Diagonal 2: back face
        int o4 = offset + FloatsPerFace * 3;
        WV(vertices, o4,       x, y, z+1,       -nx2, 0, -nz2, cr, cg, cb, u0, v1);
        WV(vertices, o4 + 11,  x, y + 1, z+1,   -nx2, 0, -nz2, cr, cg, cb, u0, v0);
        WV(vertices, o4 + 22,  x+1, y + 1, z,   -nx2, 0, -nz2, cr, cg, cb, u1, v0);
        WV(vertices, o4 + 33,  x, y, z+1,       -nx2, 0, -nz2, cr, cg, cb, u0, v1);
        WV(vertices, o4 + 44,  x+1, y + 1, z,   -nx2, 0, -nz2, cr, cg, cb, u1, v0);
        WV(vertices, o4 + 55,  x+1, y, z,       -nx2, 0, -nz2, cr, cg, cb, u1, v1);
    }

    private static void EmitFace(
        ArrayView<float> vertices, ArrayView<int> counter,
        float x, float y, float z,
        float nx, float ny, float nz,
        float cr, float cg, float cb,
        float u0, float v0, float u1, float v1,
        int face)
    {
        if (cr > 1f) cr = 1f;
        if (cg > 1f) cg = 1f;
        if (cb > 1f) cb = 1f;

        int offset = Atomic.Add(ref counter[0], FloatsPerFace);

        switch (face)
        {
            case 0: // +Y top
                WV(vertices, offset, x, y, z, nx, ny, nz, cr, cg, cb, u0, v0);
                WV(vertices, offset + 11, x, y, z + 1, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 22, x + 1, y, z + 1, nx, ny, nz, cr, cg, cb, u1, v1);
                WV(vertices, offset + 33, x, y, z, nx, ny, nz, cr, cg, cb, u0, v0);
                WV(vertices, offset + 44, x + 1, y, z + 1, nx, ny, nz, cr, cg, cb, u1, v1);
                WV(vertices, offset + 55, x + 1, y, z, nx, ny, nz, cr, cg, cb, u1, v0);
                break;
            case 1: // -Y bottom
                WV(vertices, offset, x, y, z + 1, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 11, x, y, z, nx, ny, nz, cr, cg, cb, u0, v0);
                WV(vertices, offset + 22, x + 1, y, z, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 33, x, y, z + 1, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 44, x + 1, y, z, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 55, x + 1, y, z + 1, nx, ny, nz, cr, cg, cb, u1, v1);
                break;
            case 2: // -X
                WV(vertices, offset, x, y, z + 1, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 11, x, y + 1, z + 1, nx, ny, nz, cr, cg, cb, u0, v0);
                WV(vertices, offset + 22, x, y + 1, z, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 33, x, y, z + 1, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 44, x, y + 1, z, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 55, x, y, z, nx, ny, nz, cr, cg, cb, u1, v1);
                break;
            case 3: // +X
                WV(vertices, offset, x, y, z, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 11, x, y + 1, z, nx, ny, nz, cr, cg, cb, u0, v0);
                WV(vertices, offset + 22, x, y + 1, z + 1, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 33, x, y, z, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 44, x, y + 1, z + 1, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 55, x, y, z + 1, nx, ny, nz, cr, cg, cb, u1, v1);
                break;
            case 4: // -Z
                WV(vertices, offset, x, y, z, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 11, x, y + 1, z, nx, ny, nz, cr, cg, cb, u0, v0);
                WV(vertices, offset + 22, x + 1, y + 1, z, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 33, x, y, z, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 44, x + 1, y + 1, z, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 55, x + 1, y, z, nx, ny, nz, cr, cg, cb, u1, v1);
                break;
            case 5: // +Z
                WV(vertices, offset, x + 1, y, z, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 11, x + 1, y + 1, z, nx, ny, nz, cr, cg, cb, u0, v0);
                WV(vertices, offset + 22, x, y + 1, z, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 33, x + 1, y, z, nx, ny, nz, cr, cg, cb, u0, v1);
                WV(vertices, offset + 44, x, y + 1, z, nx, ny, nz, cr, cg, cb, u1, v0);
                WV(vertices, offset + 55, x, y, z, nx, ny, nz, cr, cg, cb, u1, v1);
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
