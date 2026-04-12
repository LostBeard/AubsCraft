namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Generates a heightmap mesh with top faces and side faces.
/// Side faces fill gaps where neighboring columns have different heights.
/// All face winding is CCW for correct back-face culling.
/// </summary>
public static class HeightmapMesher
{
    private const int MaxFloats = 256 * 25 * 6 * 9;

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

            // Top face (normal: +Y, CCW from above)
            float ty = topY + 1;
            V(verts, ref offset, wx, ty, wz, 0, 1, 0, r, g, b);
            V(verts, ref offset, wx, ty, wz + 1, 0, 1, 0, r, g, b);
            V(verts, ref offset, wx + 1, ty, wz + 1, 0, 1, 0, r, g, b);
            V(verts, ref offset, wx, ty, wz, 0, 1, 0, r, g, b);
            V(verts, ref offset, wx + 1, ty, wz + 1, 0, 1, 0, r, g, b);
            V(verts, ref offset, wx + 1, ty, wz, 0, 1, 0, r, g, b);

            // Side colors
            float sr, sg, sb;
            if (blockName is "minecraft:grass_block" or "minecraft:podzol" or "minecraft:mycelium")
            { sr = 0.55f; sg = 0.35f; sb = 0.18f; }
            else
            { sr = r * 0.82f; sg = g * 0.82f; sb = b * 0.82f; }

            float y1 = wy + 1; // top of this column

            // -X side (neighbor at x-1)
            int nh = GetH(heights, x - 1, z);
            if (wy > nh)
            {
                float y0 = nh + 1;
                if (offset + 54 <= verts.Length)
                {
                    // Face at x=wx, normal (-1,0,0), CCW viewed from -X
                    V(verts, ref offset, wx, y0, wz + 1, -1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx, y1, wz + 1, -1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx, y1, wz, -1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx, y0, wz + 1, -1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx, y1, wz, -1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx, y0, wz, -1, 0, 0, sr, sg, sb);
                }
            }

            // +X side (neighbor at x+1)
            nh = GetH(heights, x + 1, z);
            if (wy > nh)
            {
                float y0 = nh + 1;
                if (offset + 54 <= verts.Length)
                {
                    // Face at x=wx+1, normal (+1,0,0), CCW viewed from +X
                    V(verts, ref offset, wx + 1, y0, wz, 1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx + 1, y1, wz, 1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx + 1, y0, wz, 1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 1, 0, 0, sr, sg, sb);
                    V(verts, ref offset, wx + 1, y0, wz + 1, 1, 0, 0, sr, sg, sb);
                }
            }

            // -Z side (neighbor at z-1)
            nh = GetH(heights, x, z - 1);
            if (wy > nh)
            {
                float y0 = nh + 1;
                float ds = sr * 0.92f, dg = sg * 0.92f, db = sb * 0.92f;
                if (offset + 54 <= verts.Length)
                {
                    // Face at z=wz, normal (0,0,-1), CCW viewed from -Z
                    V(verts, ref offset, wx, y0, wz, 0, 0, -1, ds, dg, db);
                    V(verts, ref offset, wx, y1, wz, 0, 0, -1, ds, dg, db);
                    V(verts, ref offset, wx + 1, y1, wz, 0, 0, -1, ds, dg, db);
                    V(verts, ref offset, wx, y0, wz, 0, 0, -1, ds, dg, db);
                    V(verts, ref offset, wx + 1, y1, wz, 0, 0, -1, ds, dg, db);
                    V(verts, ref offset, wx + 1, y0, wz, 0, 0, -1, ds, dg, db);
                }
            }

            // +Z side (neighbor at z+1)
            nh = GetH(heights, x, z + 1);
            if (wy > nh)
            {
                float y0 = nh + 1;
                float ds = sr * 0.92f, dg = sg * 0.92f, db = sb * 0.92f;
                if (offset + 54 <= verts.Length)
                {
                    // Face at z=wz+1, normal (0,0,+1), CCW viewed from +Z
                    V(verts, ref offset, wx + 1, y0, wz + 1, 0, 0, 1, ds, dg, db);
                    V(verts, ref offset, wx + 1, y1, wz + 1, 0, 0, 1, ds, dg, db);
                    V(verts, ref offset, wx, y1, wz + 1, 0, 0, 1, ds, dg, db);
                    V(verts, ref offset, wx + 1, y0, wz + 1, 0, 0, 1, ds, dg, db);
                    V(verts, ref offset, wx, y1, wz + 1, 0, 0, 1, ds, dg, db);
                    V(verts, ref offset, wx, y0, wz + 1, 0, 0, 1, ds, dg, db);
                }
            }
        }

        int vertexCount = offset / 9;
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
        float px, float py, float pz, float nx, float ny, float nz, float r, float g, float b)
    {
        v[o++] = px; v[o++] = py; v[o++] = pz;
        v[o++] = nx; v[o++] = ny; v[o++] = nz;
        v[o++] = r;  v[o++] = g;  v[o++] = b;
    }
}
