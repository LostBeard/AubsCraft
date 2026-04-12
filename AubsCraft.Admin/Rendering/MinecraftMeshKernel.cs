using ILGPU;
using ILGPU.Runtime;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// ILGPU compute kernel for generating voxel mesh from Minecraft block data.
/// Runs on all SpawnDev.ILGPU backends (WebGPU, WebGL, Wasm, CUDA, OpenCL, CPU).
///
/// Vertex format: 11 floats per vertex (position.xyz + normal.xyz + color.rgb + uv.xy).
/// Uses atomic counter for variable-length output.
/// </summary>
public static class MinecraftMeshKernel
{
    private const int SizeXZ = 16;
    private const int Height = 384;
    private const int SizeXZ2 = SizeXZ * SizeXZ;
    private const int FloatsPerVertex = 11;
    private const int FloatsPerFace = FloatsPerVertex * 6; // 66

    /// <summary>
    /// GPU kernel: generates textured mesh vertices for a Minecraft chunk.
    /// blocks: block IDs (0 = air)
    /// paletteColors: 3 floats per palette entry (R, G, B)
    /// atlasUVs: 4 floats per palette entry (u0, v0, u1, v1) - negative means no texture
    /// vertices: output buffer
    /// counter: atomic counter tracking floats written
    /// </summary>
    public static void MeshKernel(
        Index1D index,
        ArrayView<int> blocks,
        ArrayView<float> paletteColors,
        ArrayView<float> atlasUVs,
        ArrayView<float> vertices,
        ArrayView<int> counter,
        int chunkWorldX,
        int chunkWorldZ)
    {
        int y = index / SizeXZ2;
        int rem = index - y * SizeXZ2;
        int z = rem / SizeXZ;
        int x = rem - z * SizeXZ;

        int blockId = blocks[index];
        if (blockId == 0) return;

        float baseR = paletteColors[blockId * 3];
        float baseG = paletteColors[blockId * 3 + 1];
        float baseB = paletteColors[blockId * 3 + 2];

        float u0 = atlasUVs[blockId * 4];
        float v0 = atlasUVs[blockId * 4 + 1];
        float u1 = atlasUVs[blockId * 4 + 2];
        float v1 = atlasUVs[blockId * 4 + 3];

        float wx = chunkWorldX * SizeXZ + x;
        float wy = y - 64f;
        float wz = chunkWorldZ * SizeXZ + z;

        // +X
        if (GetBlock(blocks, x + 1, y, z) == 0)
            EmitFace(vertices, counter, wx, wy, wz, 1f, 0f, 0f, baseR * 0.88f, baseG * 0.88f, baseB * 0.88f, u0, v0, u1, v1, 3);
        // -X
        if (GetBlock(blocks, x - 1, y, z) == 0)
            EmitFace(vertices, counter, wx, wy, wz, -1f, 0f, 0f, baseR * 0.88f, baseG * 0.88f, baseB * 0.88f, u0, v0, u1, v1, 2);
        // +Y top
        if (GetBlock(blocks, x, y + 1, z) == 0)
            EmitFace(vertices, counter, wx, wy + 1, wz, 0f, 1f, 0f, baseR * 1.05f, baseG * 1.05f, baseB * 1.05f, u0, v0, u1, v1, 0);
        // -Y bottom
        if (GetBlock(blocks, x, y - 1, z) == 0)
            EmitFace(vertices, counter, wx, wy, wz, 0f, -1f, 0f, baseR * 0.70f, baseG * 0.70f, baseB * 0.70f, u0, v0, u1, v1, 1);
        // +Z
        if (GetBlock(blocks, x, y, z + 1) == 0)
            EmitFace(vertices, counter, wx, wy, wz + 1, 0f, 0f, 1f, baseR * 0.82f, baseG * 0.82f, baseB * 0.82f, u0, v0, u1, v1, 5);
        // -Z
        if (GetBlock(blocks, x, y, z - 1) == 0)
            EmitFace(vertices, counter, wx, wy, wz, 0f, 0f, -1f, baseR * 0.82f, baseG * 0.82f, baseB * 0.82f, u0, v0, u1, v1, 4);
    }

    private static int GetBlock(ArrayView<int> blocks, int x, int y, int z)
    {
        if (x < 0 || x >= SizeXZ || y < 0 || y >= Height || z < 0 || z >= SizeXZ)
            return 0;
        return blocks[x + z * SizeXZ + y * SizeXZ2];
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
