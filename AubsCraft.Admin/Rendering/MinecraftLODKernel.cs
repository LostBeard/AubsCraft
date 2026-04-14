using ILGPU;
using ILGPU.Runtime;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// ILGPU LOD kernel for Minecraft chunk meshing at reduced detail.
/// Each thread processes an LxLxL super-block (L = lodSize: 2, 4, or 8).
/// Finds the dominant visible block in the group and emits larger faces.
///
/// Thread count per LOD level (for 16x384x16 chunk):
///   LOD 2: 8x192x8 = 12288 threads (8x reduction from full)
///   LOD 4: 4x96x4  = 1536 threads  (64x reduction)
///   LOD 8: 2x48x2  = 192 threads   (512x reduction)
///
/// Vertex format: 11 floats (pos3 + normal3 + color3 + uv2) - same as full kernel.
/// </summary>
public static class MinecraftLODKernel
{
    private const int ChunkW = 16;
    private const int ChunkH = 384;
    private const int FloatsPerVertex = 11;
    private const int FloatsPerFace = FloatsPerVertex * 6;

    /// <summary>
    /// LOD mesh kernel. Each thread = one super-block of size lodSize^3.
    /// lodGridW = ChunkW / lodSize (e.g., 8 for LOD 2, 4 for LOD 4)
    /// lodGridH = ChunkH / lodSize
    /// Dispatch lodGridW * lodGridW * lodGridH threads.
    /// </summary>
    public static void LODKernel(
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
        int chunkWorldZ,
        int lodSize,
        int lodGridW)
    {
        int lodGridWW = lodGridW * lodGridW;
        int lodGridH = ChunkH / lodSize;

        int gy = index / lodGridWW;
        int rem = index - gy * lodGridWW;
        int gz = rem / lodGridW;
        int gx = rem - gz * lodGridW;

        // Block coordinates of this super-block's origin
        int bx = gx * lodSize;
        int by = by = gy * lodSize;
        int bz = gz * lodSize;

        // Find dominant block in this LxLxL group (top-most non-air surface block preferred)
        int dominantId = 0;
        float dominantR = 0f, dominantG = 0f, dominantB = 0f;
        bool hasWater = false;
        bool hasSolid = false;

        // Scan from top to bottom within the group - first non-air block type wins
        // This ensures surface blocks (grass, sand) show instead of buried stone
        for (int dy = lodSize - 1; dy >= 0 && dominantId == 0; dy--)
        for (int dz = 0; dz < lodSize && dominantId == 0; dz++)
        for (int dx = 0; dx < lodSize && dominantId == 0; dx++)
        {
            int lx = bx + dx, ly = by + dy, lz = bz + dz;
            if (lx >= ChunkW || ly >= ChunkH || lz >= ChunkW) continue;
            int id = blocks[lx + lz * ChunkW + ly * ChunkW * ChunkW];
            if (id == 0) continue;

            float f = blockFlags[id];
            if (f > 1.5f && f < 2.5f) { hasWater = true; continue; } // water
            if (f > 0.5f && f < 1.5f) continue; // skip plants for LOD

            dominantId = id;
            dominantR = paletteColors[id * 3];
            dominantG = paletteColors[id * 3 + 1];
            dominantB = paletteColors[id * 3 + 2];
            hasSolid = true;
        }

        // Count non-air blocks to determine if this super-block is "solid enough" to render
        if (!hasSolid && !hasWater) return;

        float wx = chunkWorldX * ChunkW + bx;
        float wy = by - 64f;
        float wz = chunkWorldZ * ChunkW + bz;
        float L = lodSize;

        // Tint handling
        if (dominantId > 0)
        {
            float flag = blockFlags[dominantId];
            int uvBase = dominantId * 12;
            if (flag < 0.5f && atlasUVs[uvBase] >= 0f)
            { dominantR = 1f; dominantG = 1f; dominantB = 1f; }
        }

        // Get atlas UVs for dominant block
        float u0 = -1f, v0 = -1f, u1 = -1f, v1 = -1f;
        float sideU0 = -1f, sideV0 = -1f, sideU1 = -1f, sideV1 = -1f;
        if (dominantId > 0)
        {
            int uvBase = dominantId * 12;
            u0 = atlasUVs[uvBase]; v0 = atlasUVs[uvBase + 1];
            u1 = atlasUVs[uvBase + 2]; v1 = atlasUVs[uvBase + 3];
            sideU0 = atlasUVs[uvBase + 4]; sideV0 = atlasUVs[uvBase + 5];
            sideU1 = atlasUVs[uvBase + 6]; sideV1 = atlasUVs[uvBase + 7];
        }

        // Emit water super-block
        if (hasWater && !hasSolid)
        {
            // Only emit top face for water LOD
            if (gy >= lodGridH - 1 || !HasSolidGroup(blocks, blockFlags, bx, by + lodSize, bz, lodSize))
            {
                int wo = Atomic.Add(ref waterCounter[0], FloatsPerFace);
                if (wo + FloatsPerFace > waterVerts.IntLength) { Atomic.Add(ref waterCounter[0], -FloatsPerFace); return; }
                float waterY = wy + L - 0.1f;
                WV(waterVerts, wo,      wx,   waterY, wz,   0,1,0, 0.2f,0.4f,0.8f, u0,v0);
                WV(waterVerts, wo+11,   wx,   waterY, wz+L, 0,1,0, 0.2f,0.4f,0.8f, u0,v1);
                WV(waterVerts, wo+22,   wx+L, waterY, wz+L, 0,1,0, 0.2f,0.4f,0.8f, u1,v1);
                WV(waterVerts, wo+33,   wx,   waterY, wz,   0,1,0, 0.2f,0.4f,0.8f, u0,v0);
                WV(waterVerts, wo+44,   wx+L, waterY, wz+L, 0,1,0, 0.2f,0.4f,0.8f, u1,v1);
                WV(waterVerts, wo+55,   wx+L, waterY, wz,   0,1,0, 0.2f,0.4f,0.8f, u1,v0);
            }
            return;
        }

        if (dominantId == 0) return;

        // Emit faces for solid super-block where neighbor super-block is empty/transparent
        float cr = dominantR, cg = dominantG, cb = dominantB;

        // +Y top
        if (gy >= lodGridH - 1 || !HasSolidGroup(blocks, blockFlags, bx, by + lodSize, bz, lodSize))
        {
            float br = cr * 1.05f; if (br > 1f) br = 1f;
            float bg = cg * 1.05f; if (bg > 1f) bg = 1f;
            float bb = cb * 1.05f; if (bb > 1f) bb = 1f;
            int oo = Atomic.Add(ref opaqueCounter[0], FloatsPerFace);
            if (oo + FloatsPerFace > opaqueVerts.IntLength) { Atomic.Add(ref opaqueCounter[0], -FloatsPerFace); return; }
            WV(opaqueVerts, oo,      wx,   wy+L, wz,   0,1,0, br,bg,bb, u0,v0);
            WV(opaqueVerts, oo+11,   wx,   wy+L, wz+L, 0,1,0, br,bg,bb, u0,v1);
            WV(opaqueVerts, oo+22,   wx+L, wy+L, wz+L, 0,1,0, br,bg,bb, u1,v1);
            WV(opaqueVerts, oo+33,   wx,   wy+L, wz,   0,1,0, br,bg,bb, u0,v0);
            WV(opaqueVerts, oo+44,   wx+L, wy+L, wz+L, 0,1,0, br,bg,bb, u1,v1);
            WV(opaqueVerts, oo+55,   wx+L, wy+L, wz,   0,1,0, br,bg,bb, u1,v0);
        }

        // -Y bottom
        if (gy <= 0 || !HasSolidGroup(blocks, blockFlags, bx, by - lodSize, bz, lodSize))
        {
            float br = cr * 0.70f, bg = cg * 0.70f, bb = cb * 0.70f;
            int oo = Atomic.Add(ref opaqueCounter[0], FloatsPerFace);
            if (oo + FloatsPerFace > opaqueVerts.IntLength) { Atomic.Add(ref opaqueCounter[0], -FloatsPerFace); return; }
            WV(opaqueVerts, oo,      wx,   wy, wz+L, 0,-1,0, br,bg,bb, u0,v1);
            WV(opaqueVerts, oo+11,   wx,   wy, wz,   0,-1,0, br,bg,bb, u0,v0);
            WV(opaqueVerts, oo+22,   wx+L, wy, wz,   0,-1,0, br,bg,bb, u1,v0);
            WV(opaqueVerts, oo+33,   wx,   wy, wz+L, 0,-1,0, br,bg,bb, u0,v1);
            WV(opaqueVerts, oo+44,   wx+L, wy, wz,   0,-1,0, br,bg,bb, u1,v0);
            WV(opaqueVerts, oo+55,   wx+L, wy, wz+L, 0,-1,0, br,bg,bb, u1,v1);
        }

        // +X
        if (gx >= lodGridW - 1 || !HasSolidGroup(blocks, blockFlags, bx + lodSize, by, bz, lodSize))
        {
            float br = cr * 0.88f, bg = cg * 0.88f, bb = cb * 0.88f;
            int oo = Atomic.Add(ref opaqueCounter[0], FloatsPerFace);
            if (oo + FloatsPerFace > opaqueVerts.IntLength) { Atomic.Add(ref opaqueCounter[0], -FloatsPerFace); return; }
            WV(opaqueVerts, oo,      wx+L, wy,   wz,   1,0,0, br,bg,bb, sideU0,sideV1);
            WV(opaqueVerts, oo+11,   wx+L, wy+L, wz,   1,0,0, br,bg,bb, sideU0,sideV0);
            WV(opaqueVerts, oo+22,   wx+L, wy+L, wz+L, 1,0,0, br,bg,bb, sideU1,sideV0);
            WV(opaqueVerts, oo+33,   wx+L, wy,   wz,   1,0,0, br,bg,bb, sideU0,sideV1);
            WV(opaqueVerts, oo+44,   wx+L, wy+L, wz+L, 1,0,0, br,bg,bb, sideU1,sideV0);
            WV(opaqueVerts, oo+55,   wx+L, wy,   wz+L, 1,0,0, br,bg,bb, sideU1,sideV1);
        }

        // -X
        if (gx <= 0 || !HasSolidGroup(blocks, blockFlags, bx - lodSize, by, bz, lodSize))
        {
            float br = cr * 0.88f, bg = cg * 0.88f, bb = cb * 0.88f;
            int oo = Atomic.Add(ref opaqueCounter[0], FloatsPerFace);
            if (oo + FloatsPerFace > opaqueVerts.IntLength) { Atomic.Add(ref opaqueCounter[0], -FloatsPerFace); return; }
            WV(opaqueVerts, oo,      wx, wy,   wz+L, -1,0,0, br,bg,bb, sideU0,sideV1);
            WV(opaqueVerts, oo+11,   wx, wy+L, wz+L, -1,0,0, br,bg,bb, sideU0,sideV0);
            WV(opaqueVerts, oo+22,   wx, wy+L, wz,   -1,0,0, br,bg,bb, sideU1,sideV0);
            WV(opaqueVerts, oo+33,   wx, wy,   wz+L, -1,0,0, br,bg,bb, sideU0,sideV1);
            WV(opaqueVerts, oo+44,   wx, wy+L, wz,   -1,0,0, br,bg,bb, sideU1,sideV0);
            WV(opaqueVerts, oo+55,   wx, wy,   wz,   -1,0,0, br,bg,bb, sideU1,sideV1);
        }

        // +Z
        if (gz >= lodGridW - 1 || !HasSolidGroup(blocks, blockFlags, bx, by, bz + lodSize, lodSize))
        {
            float br = cr * 0.82f, bg = cg * 0.82f, bb = cb * 0.82f;
            int oo = Atomic.Add(ref opaqueCounter[0], FloatsPerFace);
            if (oo + FloatsPerFace > opaqueVerts.IntLength) { Atomic.Add(ref opaqueCounter[0], -FloatsPerFace); return; }
            WV(opaqueVerts, oo,      wx+L, wy,   wz+L, 0,0,1, br,bg,bb, sideU0,sideV1);
            WV(opaqueVerts, oo+11,   wx+L, wy+L, wz+L, 0,0,1, br,bg,bb, sideU0,sideV0);
            WV(opaqueVerts, oo+22,   wx,   wy+L, wz+L, 0,0,1, br,bg,bb, sideU1,sideV0);
            WV(opaqueVerts, oo+33,   wx+L, wy,   wz+L, 0,0,1, br,bg,bb, sideU0,sideV1);
            WV(opaqueVerts, oo+44,   wx,   wy+L, wz+L, 0,0,1, br,bg,bb, sideU1,sideV0);
            WV(opaqueVerts, oo+55,   wx,   wy,   wz+L, 0,0,1, br,bg,bb, sideU1,sideV1);
        }

        // -Z
        if (gz <= 0 || !HasSolidGroup(blocks, blockFlags, bx, by, bz - lodSize, lodSize))
        {
            float br = cr * 0.82f, bg = cg * 0.82f, bb = cb * 0.82f;
            int oo = Atomic.Add(ref opaqueCounter[0], FloatsPerFace);
            if (oo + FloatsPerFace > opaqueVerts.IntLength) { Atomic.Add(ref opaqueCounter[0], -FloatsPerFace); return; }
            WV(opaqueVerts, oo,      wx,   wy,   wz, 0,0,-1, br,bg,bb, sideU0,sideV1);
            WV(opaqueVerts, oo+11,   wx,   wy+L, wz, 0,0,-1, br,bg,bb, sideU0,sideV0);
            WV(opaqueVerts, oo+22,   wx+L, wy+L, wz, 0,0,-1, br,bg,bb, sideU1,sideV0);
            WV(opaqueVerts, oo+33,   wx,   wy,   wz, 0,0,-1, br,bg,bb, sideU0,sideV1);
            WV(opaqueVerts, oo+44,   wx+L, wy+L, wz, 0,0,-1, br,bg,bb, sideU1,sideV0);
            WV(opaqueVerts, oo+55,   wx+L, wy,   wz, 0,0,-1, br,bg,bb, sideU1,sideV1);
        }
    }

    /// <summary>
    /// Check if a super-block group has any solid (non-air, non-plant) blocks.
    /// Used for neighbor checking to decide face emission.
    /// </summary>
    private static bool HasSolidGroup(ArrayView<int> blocks, ArrayView<float> blockFlags,
        int bx, int by, int bz, int lodSize)
    {
        // Out of chunk bounds = treat as opaque (no face emission)
        if (bx < 0 || bx >= ChunkW || bz < 0 || bz >= ChunkW) return true;
        if (by < 0 || by >= ChunkH) return false; // world top/bottom = air

        for (int dy = 0; dy < lodSize && by + dy < ChunkH; dy++)
        for (int dz = 0; dz < lodSize && bz + dz < ChunkW; dz++)
        for (int dx = 0; dx < lodSize && bx + dx < ChunkW; dx++)
        {
            int id = blocks[(bx + dx) + (bz + dz) * ChunkW + (by + dy) * ChunkW * ChunkW];
            if (id != 0)
            {
                float f = blockFlags[id];
                if (f < 0.5f || (f > 2.5f)) return true; // solid opaque or solid tinted
            }
        }
        return false;
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
