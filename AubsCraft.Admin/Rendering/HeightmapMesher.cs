namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Generates a heightmap mesh with top faces and side faces.
/// Vertex format: 11 floats (pos3 + normal3 + color3 + uv2).
/// UV coordinates map to the texture atlas for textured rendering.
/// </summary>
public static class HeightmapMesher
{
    private const int FloatsPerVertex = 11;
    private const int MaxFloats = 256 * 50 * 6 * FloatsPerVertex; // doubled for seabed geometry

    /// <summary>
    /// Returns (opaqueVertices, opaqueCount, waterVertices, waterCount).
    /// Water blocks are emitted to a separate vertex array for transparent rendering.
    /// Seabed terrain (solid blocks under water) is rendered as opaque geometry.
    /// </summary>
    public static (float[] opaqueVerts, int opaqueCount, float[] waterVerts, int waterCount) GenerateMesh(
        int[] heights, ushort[] blockIds, float[] paletteColors, List<string> paletteNames,
        int chunkX, int chunkZ,
        int[]? seabedHeights = null, ushort[]? seabedBlockIds = null)
    {
        var verts = new float[MaxFloats];
        int offset = 0;
        var waterVertsList = new List<float>();

        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int col = x + z * 16;
            int blockId = blockIds[col];
            if (blockId == 0) continue;

            float wx = chunkX * 16f + x;
            float wy = heights[col];
            float wz = chunkZ * 16f + z;

            float r = paletteColors[blockId * 3];
            float g = paletteColors[blockId * 3 + 1];
            float b = paletteColors[blockId * 3 + 2];

            var blockName = blockId < paletteNames.Count ? paletteNames[blockId] : "";

            // Skip plant blocks in heightmap - they look wrong as flat textures on top
            // and are invisible at distance anyway. The full 3D pass renders them as cross-quads.
            if (TextureAtlas.IsPlant(blockName)) continue;

            bool isWater = blockName is "minecraft:water" or "minecraft:flowing_water";
            float topY = isWater ? wy - 0.1f : wy; // water surface slightly below block top (matches kernel's +0.9)

            // Get atlas UV for this block (or -1,-1 for flat color)
            var (u0, v0, u1, v1) = TextureAtlas.GetTileUVs(blockName);

            // For biome-tinted blocks (grass, leaves, water), the texture is grayscale
            // so we keep the vertex color as the tint. For other blocks, set color to white
            // so the texture color shows through unmodified.
            float tr = r, tg = g, tb = b; // tint color
            bool isTinted = blockName.Contains("grass") || blockName.Contains("leaves")
                         || blockName.Contains("water") || blockName.Contains("vine")
                         || blockName.Contains("fern") || blockName.Contains("lily");
            if (u0 >= 0 && !isTinted)
            { tr = 1f; tg = 1f; tb = 1f; } // white tint = pure texture color

            // Top face - water goes to separate transparent list
            float ty = topY + 1;
            if (isWater)
            {
                VL(waterVertsList, wx, ty, wz, 0, 1, 0, tr, tg, tb, u0, v0);
                VL(waterVertsList, wx, ty, wz + 1, 0, 1, 0, tr, tg, tb, u0, v1);
                VL(waterVertsList, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
                VL(waterVertsList, wx, ty, wz, 0, 1, 0, tr, tg, tb, u0, v0);
                VL(waterVertsList, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
                VL(waterVertsList, wx + 1, ty, wz, 0, 1, 0, tr, tg, tb, u1, v0);
                continue; // water has no side faces in heightmap
            }
            V(verts, ref offset, wx, ty, wz, 0, 1, 0, tr, tg, tb, u0, v0);
            V(verts, ref offset, wx, ty, wz + 1, 0, 1, 0, tr, tg, tb, u0, v1);
            V(verts, ref offset, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            V(verts, ref offset, wx, ty, wz, 0, 1, 0, tr, tg, tb, u0, v0);
            V(verts, ref offset, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            V(verts, ref offset, wx + 1, ty, wz, 0, 1, 0, tr, tg, tb, u1, v0);

            // Side faces use flat color (no texture - avoids stretching)
            float sr, sg, sb;
            float snu = -1f; // no texture UV for sides
            if (blockName is "minecraft:grass_block" or "minecraft:podzol" or "minecraft:mycelium")
            { sr = 0.53f; sg = 0.38f; sb = 0.26f; } // dirt color
            else
            { sr = r * 0.82f; sg = g * 0.82f; sb = b * 0.82f; }

            float y1 = wy + 1;

            // -X side
            int nh = GetH(heights, x - 1, z);
            if (wy > nh)
            {
                float y0 = nh + 1;
                if (offset + 66 <= verts.Length)
                {
                    V(verts, ref offset, wx, y0, wz + 1, -1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx, y1, wz + 1, -1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx, y1, wz, -1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx, y0, wz + 1, -1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx, y1, wz, -1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx, y0, wz, -1, 0, 0, sr, sg, sb, snu, snu);
                }
            }

            // +X side
            nh = GetH(heights, x + 1, z);
            if (wy > nh)
            {
                float y0 = nh + 1;
                if (offset + 66 <= verts.Length)
                {
                    V(verts, ref offset, wx + 1, y0, wz, 1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz, 1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx + 1, y0, wz, 1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 1, 0, 0, sr, sg, sb, snu, snu);
                    V(verts, ref offset, wx + 1, y0, wz + 1, 1, 0, 0, sr, sg, sb, snu, snu);
                }
            }

            // -Z side
            nh = GetH(heights, x, z - 1);
            if (wy > nh)
            {
                float y0 = nh + 1;
                float ds = 0.92f;
                if (offset + 66 <= verts.Length)
                {
                    V(verts, ref offset, wx, y0, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx, y1, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx, y0, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx + 1, y0, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, snu, snu);
                }
            }

            // +Z side
            nh = GetH(heights, x, z + 1);
            if (wy > nh)
            {
                float y0 = nh + 1;
                float ds = 0.92f;
                if (offset + 66 <= verts.Length)
                {
                    V(verts, ref offset, wx + 1, y0, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx, y1, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx + 1, y0, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx, y1, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, snu, snu);
                    V(verts, ref offset, wx, y0, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, snu, snu);
                }
            }
        }

        // Render seabed terrain (solid blocks under water) as opaque geometry
        if (seabedHeights != null && seabedBlockIds != null)
        {
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
            {
                int col = x + z * 16;
                int sbHeight = seabedHeights[col];
                ushort sbId = seabedBlockIds[col];
                if (sbId == 0 || sbHeight <= -64) continue;

                float wx = chunkX * 16f + x;
                float wy = sbHeight;
                float wz = chunkZ * 16f + z;

                float r = paletteColors[sbId * 3];
                float g = paletteColors[sbId * 3 + 1];
                float b = paletteColors[sbId * 3 + 2];

                var sbName = sbId < paletteNames.Count ? paletteNames[sbId] : "";
                if (TextureAtlas.IsPlant(sbName)) continue;

                var (u0, v0, u1, v1) = TextureAtlas.GetTileUVs(sbName);
                float tr = r, tg = g, tb = b;
                bool isTinted = sbName.Contains("grass") || sbName.Contains("leaves");
                if (u0 >= 0 && !isTinted) { tr = 1f; tg = 1f; tb = 1f; }

                // Seabed top face
                float ty = wy + 1;
                if (offset + 66 <= verts.Length)
                {
                    V(verts, ref offset, wx, ty, wz, 0, 1, 0, tr * 0.7f, tg * 0.7f, tb * 0.7f, u0, v0);
                    V(verts, ref offset, wx, ty, wz + 1, 0, 1, 0, tr * 0.7f, tg * 0.7f, tb * 0.7f, u0, v1);
                    V(verts, ref offset, wx + 1, ty, wz + 1, 0, 1, 0, tr * 0.7f, tg * 0.7f, tb * 0.7f, u1, v1);
                    V(verts, ref offset, wx, ty, wz, 0, 1, 0, tr * 0.7f, tg * 0.7f, tb * 0.7f, u0, v0);
                    V(verts, ref offset, wx + 1, ty, wz + 1, 0, 1, 0, tr * 0.7f, tg * 0.7f, tb * 0.7f, u1, v1);
                    V(verts, ref offset, wx + 1, ty, wz, 0, 1, 0, tr * 0.7f, tg * 0.7f, tb * 0.7f, u1, v0);
                }

                // Seabed cliff sides (compare with neighbor seabed heights)
                float sr = r * 0.55f, sg = g * 0.55f, sb2 = b * 0.55f; // darker underwater
                float snu = -1f;
                float y1 = wy + 1;

                int nh;
                // -X
                nh = GetSeabedH(seabedHeights, x - 1, z);
                if (wy > nh && offset + 66 <= verts.Length)
                {
                    float y0 = nh + 1;
                    V(verts, ref offset, wx, y0, wz + 1, -1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y1, wz + 1, -1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y1, wz, -1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y0, wz + 1, -1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y1, wz, -1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y0, wz, -1, 0, 0, sr, sg, sb2, snu, snu);
                }
                // +X
                nh = GetSeabedH(seabedHeights, x + 1, z);
                if (wy > nh && offset + 66 <= verts.Length)
                {
                    float y0 = nh + 1;
                    V(verts, ref offset, wx + 1, y0, wz, 1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz, 1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y0, wz, 1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 1, 0, 0, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y0, wz + 1, 1, 0, 0, sr, sg, sb2, snu, snu);
                }
                // -Z
                nh = GetSeabedH(seabedHeights, x, z - 1);
                if (wy > nh && offset + 66 <= verts.Length)
                {
                    float y0 = nh + 1;
                    V(verts, ref offset, wx, y0, wz, 0, 0, -1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y1, wz, 0, 0, -1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz, 0, 0, -1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y0, wz, 0, 0, -1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz, 0, 0, -1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y0, wz, 0, 0, -1, sr, sg, sb2, snu, snu);
                }
                // +Z
                nh = GetSeabedH(seabedHeights, x, z + 1);
                if (wy > nh && offset + 66 <= verts.Length)
                {
                    float y0 = nh + 1;
                    V(verts, ref offset, wx + 1, y0, wz + 1, 0, 0, 1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 0, 0, 1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y1, wz + 1, 0, 0, 1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx + 1, y0, wz + 1, 0, 0, 1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y1, wz + 1, 0, 0, 1, sr, sg, sb2, snu, snu);
                    V(verts, ref offset, wx, y0, wz + 1, 0, 0, 1, sr, sg, sb2, snu, snu);
                }
            }
        }

        int opaqueCount = offset / FloatsPerVertex;
        float[] opaqueResult = [];
        if (opaqueCount > 0)
        {
            opaqueResult = new float[offset];
            Array.Copy(verts, opaqueResult, offset);
        }

        var waterArr = waterVertsList.ToArray();
        int waterCount = waterArr.Length / FloatsPerVertex;
        return (opaqueResult, opaqueCount, waterArr, waterCount);
    }

    private static int GetH(int[] heights, int x, int z)
    {
        if (x < 0 || x >= 16 || z < 0 || z >= 16) return -64;
        return heights[x + z * 16];
    }

    private static int GetSeabedH(int[] seabedHeights, int x, int z)
    {
        if (x < 0 || x >= 16 || z < 0 || z >= 16) return -64;
        return seabedHeights[x + z * 16];
    }

    private static void V(float[] v, ref int o,
        float px, float py, float pz, float nx, float ny, float nz,
        float r, float g, float b, float u, float uv)
    {
        v[o++] = px; v[o++] = py; v[o++] = pz;
        v[o++] = nx; v[o++] = ny; v[o++] = nz;
        v[o++] = r;  v[o++] = g;  v[o++] = b;
        v[o++] = u;  v[o++] = uv;
    }

    private static void VL(List<float> v,
        float px, float py, float pz, float nx, float ny, float nz,
        float r, float g, float b, float u, float uv)
    {
        v.Add(px); v.Add(py); v.Add(pz);
        v.Add(nx); v.Add(ny); v.Add(nz);
        v.Add(r);  v.Add(g);  v.Add(b);
        v.Add(u);  v.Add(uv);
    }
}
