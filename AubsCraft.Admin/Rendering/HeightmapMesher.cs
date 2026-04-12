namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Generates a heightmap mesh with top faces and side faces.
/// Vertex format: 11 floats (pos3 + normal3 + color3 + uv2).
/// UV coordinates map to the texture atlas for textured rendering.
/// </summary>
public static class HeightmapMesher
{
    private const int FloatsPerVertex = 11;
    private const int MaxFloats = 256 * 25 * 6 * FloatsPerVertex;

    public static (float[] vertices, int vertexCount) GenerateMesh(
        int[] heights, ushort[] blockIds, float[] paletteColors, List<string> paletteNames,
        int chunkX, int chunkZ)
    {
        var verts = new float[MaxFloats];
        int offset = 0;

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
            bool isWater = blockName is "minecraft:water" or "minecraft:flowing_water";
            float topY = isWater ? wy + 0.1f : wy;

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

            // Top face
            float ty = topY + 1;
            V(verts, ref offset, wx, ty, wz, 0, 1, 0, tr, tg, tb, u0, v0);
            V(verts, ref offset, wx, ty, wz + 1, 0, 1, 0, tr, tg, tb, u0, v1);
            V(verts, ref offset, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            V(verts, ref offset, wx, ty, wz, 0, 1, 0, tr, tg, tb, u0, v0);
            V(verts, ref offset, wx + 1, ty, wz + 1, 0, 1, 0, tr, tg, tb, u1, v1);
            V(verts, ref offset, wx + 1, ty, wz, 0, 1, 0, tr, tg, tb, u1, v0);

            // Side colors (dirt for grass, darker for others)
            float sr, sg, sb;
            // Side face UVs - use dirt texture for grass sides, same texture for others
            float su0 = u0, sv0 = v0, su1 = u1, sv1 = v1;
            if (blockName is "minecraft:grass_block" or "minecraft:podzol" or "minecraft:mycelium")
            {
                sr = 1f; sg = 1f; sb = 1f; // white tint for dirt texture
                // Use dirt texture (index 1) for grass block sides
                su0 = 1f / 8f; sv0 = 0f; su1 = 2f / 8f; sv1 = 1f / 8f;
            }
            else
            {
                sr = tr * 0.82f; sg = tg * 0.82f; sb = tb * 0.82f;
            }

            float y1 = wy + 1;

            // -X side
            int nh = GetH(heights, x - 1, z);
            if (wy > nh)
            {
                float y0 = nh + 1;
                if (offset + 66 <= verts.Length)
                {
                    V(verts, ref offset, wx, y0, wz + 1, -1, 0, 0, sr, sg, sb, su0, sv1);
                    V(verts, ref offset, wx, y1, wz + 1, -1, 0, 0, sr, sg, sb, su0, sv0);
                    V(verts, ref offset, wx, y1, wz, -1, 0, 0, sr, sg, sb, su1, sv0);
                    V(verts, ref offset, wx, y0, wz + 1, -1, 0, 0, sr, sg, sb, su0, sv1);
                    V(verts, ref offset, wx, y1, wz, -1, 0, 0, sr, sg, sb, su1, sv0);
                    V(verts, ref offset, wx, y0, wz, -1, 0, 0, sr, sg, sb, su1, sv1);
                }
            }

            // +X side
            nh = GetH(heights, x + 1, z);
            if (wy > nh)
            {
                float y0 = nh + 1;
                if (offset + 66 <= verts.Length)
                {
                    V(verts, ref offset, wx + 1, y0, wz, 1, 0, 0, sr, sg, sb, su0, sv1);
                    V(verts, ref offset, wx + 1, y1, wz, 1, 0, 0, sr, sg, sb, su0, sv0);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 1, 0, 0, sr, sg, sb, su1, sv0);
                    V(verts, ref offset, wx + 1, y0, wz, 1, 0, 0, sr, sg, sb, su0, sv1);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 1, 0, 0, sr, sg, sb, su1, sv0);
                    V(verts, ref offset, wx + 1, y0, wz + 1, 1, 0, 0, sr, sg, sb, su1, sv1);
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
                    V(verts, ref offset, wx, y0, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, su0, sv1);
                    V(verts, ref offset, wx, y1, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, su0, sv0);
                    V(verts, ref offset, wx + 1, y1, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, su1, sv0);
                    V(verts, ref offset, wx, y0, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, su0, sv1);
                    V(verts, ref offset, wx + 1, y1, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, su1, sv0);
                    V(verts, ref offset, wx + 1, y0, wz, 0, 0, -1, sr*ds, sg*ds, sb*ds, su1, sv1);
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
                    V(verts, ref offset, wx + 1, y0, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, su0, sv1);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, su0, sv0);
                    V(verts, ref offset, wx, y1, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, su1, sv0);
                    V(verts, ref offset, wx + 1, y0, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, su0, sv1);
                    V(verts, ref offset, wx, y1, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, su1, sv0);
                    V(verts, ref offset, wx, y0, wz + 1, 0, 0, 1, sr*ds, sg*ds, sb*ds, su1, sv1);
                }
            }
        }

        int vertexCount = offset / FloatsPerVertex;
        if (vertexCount == 0) return ([], 0);

        var result = new float[offset];
        Array.Copy(verts, result, offset);
        return (result, vertexCount);
    }

    private static int GetH(int[] heights, int x, int z)
    {
        if (x < 0 || x >= 16 || z < 0 || z >= 16) return -64;
        return heights[x + z * 16];
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
}
