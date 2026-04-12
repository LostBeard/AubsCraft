using ILGPU;
using ILGPU.Runtime;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// ILGPU compute kernel for generating voxel mesh from Minecraft block data.
/// Runs on all SpawnDev.ILGPU backends (WebGPU, WebGL, Wasm, CUDA, OpenCL, CPU).
/// Adapted from Lost Spawns TerrainKernels.cs for Minecraft's 16x384x16 chunks.
///
/// Vertex format: 9 floats per vertex (position.xyz + normal.xyz + color.rgb).
/// Uses atomic counter for variable-length output.
/// </summary>
public static class MinecraftMeshKernel
{
    private const int SizeXZ = 16;
    private const int Height = 384;
    private const int SizeXZ2 = SizeXZ * SizeXZ; // 256

    /// <summary>
    /// GPU kernel: generates mesh vertices for a Minecraft chunk.
    /// Each work item handles one block (index 0..98303).
    /// blocks: ushort block IDs (0 = air)
    /// paletteColors: flat float array, 3 floats per palette entry (R, G, B)
    /// vertices: output buffer (pre-allocated, large enough for worst case)
    /// counter: single-element atomic counter tracking floats written
    /// chunkWorldX/Z: world-space offset for this chunk (in blocks)
    /// </summary>
    public static void MeshKernel(
        Index1D index,
        ArrayView<int> blocks,
        ArrayView<float> paletteColors,
        ArrayView<float> vertices,
        ArrayView<int> counter,
        int chunkWorldX,
        int chunkWorldZ)
    {
        // Decompose flat index to (x, y, z)
        // Index layout: x + z*16 + y*256
        int y = index / SizeXZ2;
        int rem = index - y * SizeXZ2;
        int z = rem / SizeXZ;
        int x = rem - z * SizeXZ;

        int blockId = blocks[index];
        if (blockId == 0) return; // Air

        // Look up base color from palette
        float baseR = paletteColors[blockId * 3];
        float baseG = paletteColors[blockId * 3 + 1];
        float baseB = paletteColors[blockId * 3 + 2];

        // World position (Minecraft Y: -64 to 319, stored as 0 to 383)
        float wx = chunkWorldX * SizeXZ + x;
        float wy = y - 64f;
        float wz = chunkWorldZ * SizeXZ + z;

        // Check 6 neighbors and emit faces where neighbor is air
        // Face brightness varies by direction for natural look

        // +X (east)
        if (GetBlock(blocks, x + 1, y, z) == 0)
            EmitFace(vertices, counter, wx, wy, wz, 1f, 0f, 0f, baseR * 0.88f, baseG * 0.88f, baseB * 0.88f, 0);

        // -X (west)
        if (GetBlock(blocks, x - 1, y, z) == 0)
            EmitFace(vertices, counter, wx, wy, wz, -1f, 0f, 0f, baseR * 0.88f, baseG * 0.88f, baseB * 0.88f, 1);

        // +Y (top) - brightest
        if (GetBlock(blocks, x, y + 1, z) == 0)
            EmitFace(vertices, counter, wx, wy, wz, 0f, 1f, 0f, baseR * 1.05f, baseG * 1.05f, baseB * 1.05f, 2);

        // -Y (bottom) - darkest
        if (GetBlock(blocks, x, y - 1, z) == 0)
            EmitFace(vertices, counter, wx, wy, wz, 0f, -1f, 0f, baseR * 0.70f, baseG * 0.70f, baseB * 0.70f, 3);

        // +Z (south)
        if (GetBlock(blocks, x, y, z + 1) == 0)
            EmitFace(vertices, counter, wx, wy, wz, 0f, 0f, 1f, baseR * 0.82f, baseG * 0.82f, baseB * 0.82f, 4);

        // -Z (north)
        if (GetBlock(blocks, x, y, z - 1) == 0)
            EmitFace(vertices, counter, wx, wy, wz, 0f, 0f, -1f, baseR * 0.82f, baseG * 0.82f, baseB * 0.82f, 5);
    }

    private static int GetBlock(ArrayView<int> blocks, int x, int y, int z)
    {
        if (x < 0 || x >= SizeXZ || y < 0 || y >= Height || z < 0 || z >= SizeXZ)
            return 0; // Out of bounds = air
        return blocks[x + z * SizeXZ + y * SizeXZ2];
    }

    private static void EmitFace(
        ArrayView<float> vertices, ArrayView<int> counter,
        float wx, float wy, float wz,
        float nx, float ny, float nz,
        float cr, float cg, float cb,
        int faceIndex)
    {
        // Clamp colors
        if (cr > 1f) cr = 1f;
        if (cg > 1f) cg = 1f;
        if (cb > 1f) cb = 1f;

        // Claim 54 floats (6 vertices x 9 floats)
        int offset = Atomic.Add(ref counter[0], 54);

        WriteVertex(vertices, offset + 0,  wx, wy, wz, nx, ny, nz, cr, cg, cb, faceIndex, 0);
        WriteVertex(vertices, offset + 9,  wx, wy, wz, nx, ny, nz, cr, cg, cb, faceIndex, 1);
        WriteVertex(vertices, offset + 18, wx, wy, wz, nx, ny, nz, cr, cg, cb, faceIndex, 2);
        WriteVertex(vertices, offset + 27, wx, wy, wz, nx, ny, nz, cr, cg, cb, faceIndex, 3);
        WriteVertex(vertices, offset + 36, wx, wy, wz, nx, ny, nz, cr, cg, cb, faceIndex, 4);
        WriteVertex(vertices, offset + 45, wx, wy, wz, nx, ny, nz, cr, cg, cb, faceIndex, 5);
    }

    private static void WriteVertex(
        ArrayView<float> vertices, int offset,
        float wx, float wy, float wz,
        float nx, float ny, float nz,
        float cr, float cg, float cb,
        int faceIndex, int vertIndex)
    {
        float vx, vy, vz;
        GetFaceVertex(faceIndex, vertIndex, out vx, out vy, out vz);

        vertices[offset + 0] = wx + vx;
        vertices[offset + 1] = wy + vy;
        vertices[offset + 2] = wz + vz;
        vertices[offset + 3] = nx;
        vertices[offset + 4] = ny;
        vertices[offset + 5] = nz;
        vertices[offset + 6] = cr;
        vertices[offset + 7] = cg;
        vertices[offset + 8] = cb;
    }

    /// <summary>
    /// Returns vertex position offset for a given face and vertex index.
    /// Branching logic (no arrays) for GPU compatibility.
    /// Winding order: CCW front face for back-face culling.
    /// </summary>
    private static void GetFaceVertex(int face, int vert, out float x, out float y, out float z)
    {
        x = 0f; y = 0f; z = 0f;

        if (face == 0) // Right (+X)
        {
            if (vert == 0) { x = 1f; y = 0f; z = 0f; }
            else if (vert == 1) { x = 1f; y = 1f; z = 0f; }
            else if (vert == 2) { x = 1f; y = 1f; z = 1f; }
            else if (vert == 3) { x = 1f; y = 0f; z = 0f; }
            else if (vert == 4) { x = 1f; y = 1f; z = 1f; }
            else { x = 1f; y = 0f; z = 1f; }
        }
        else if (face == 1) // Left (-X)
        {
            if (vert == 0) { x = 0f; y = 0f; z = 1f; }
            else if (vert == 1) { x = 0f; y = 1f; z = 1f; }
            else if (vert == 2) { x = 0f; y = 1f; z = 0f; }
            else if (vert == 3) { x = 0f; y = 0f; z = 1f; }
            else if (vert == 4) { x = 0f; y = 1f; z = 0f; }
            else { x = 0f; y = 0f; z = 0f; }
        }
        else if (face == 2) // Top (+Y)
        {
            if (vert == 0) { x = 0f; y = 1f; z = 0f; }
            else if (vert == 1) { x = 0f; y = 1f; z = 1f; }
            else if (vert == 2) { x = 1f; y = 1f; z = 1f; }
            else if (vert == 3) { x = 0f; y = 1f; z = 0f; }
            else if (vert == 4) { x = 1f; y = 1f; z = 1f; }
            else { x = 1f; y = 1f; z = 0f; }
        }
        else if (face == 3) // Bottom (-Y)
        {
            if (vert == 0) { x = 0f; y = 0f; z = 1f; }
            else if (vert == 1) { x = 0f; y = 0f; z = 0f; }
            else if (vert == 2) { x = 1f; y = 0f; z = 0f; }
            else if (vert == 3) { x = 0f; y = 0f; z = 1f; }
            else if (vert == 4) { x = 1f; y = 0f; z = 0f; }
            else { x = 1f; y = 0f; z = 1f; }
        }
        else if (face == 4) // Front (+Z)
        {
            if (vert == 0) { x = 0f; y = 0f; z = 1f; }
            else if (vert == 1) { x = 1f; y = 0f; z = 1f; }
            else if (vert == 2) { x = 1f; y = 1f; z = 1f; }
            else if (vert == 3) { x = 0f; y = 0f; z = 1f; }
            else if (vert == 4) { x = 1f; y = 1f; z = 1f; }
            else { x = 0f; y = 1f; z = 1f; }
        }
        else // Back (-Z)
        {
            if (vert == 0) { x = 1f; y = 0f; z = 0f; }
            else if (vert == 1) { x = 0f; y = 0f; z = 0f; }
            else if (vert == 2) { x = 0f; y = 1f; z = 0f; }
            else if (vert == 3) { x = 1f; y = 0f; z = 0f; }
            else if (vert == 4) { x = 0f; y = 1f; z = 0f; }
            else { x = 1f; y = 1f; z = 0f; }
        }
    }
}
