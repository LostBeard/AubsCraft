namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Generates a simple top-face-only mesh from heightmap data.
/// Each column gets one quad (6 vertices) at its surface height.
/// 256 columns x 6 vertices x 9 floats = 13,824 floats max per chunk.
/// Fast enough for CPU - no GPU kernel needed for this small dataset.
/// </summary>
public static class HeightmapMesher
{
    public static (float[] vertices, int vertexCount) GenerateMesh(
        int[] heights, ushort[] blockIds, float[] paletteColors,
        int chunkX, int chunkZ)
    {
        var verts = new float[256 * 6 * 9]; // max possible
        int offset = 0;

        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int col = x + z * 16;
            int blockId = blockIds[col];
            if (blockId == 0) continue; // all air column

            float wx = chunkX * 16f + x;
            float wy = heights[col];
            float wz = chunkZ * 16f + z;

            float r = paletteColors[blockId * 3];
            float g = paletteColors[blockId * 3 + 1];
            float b = paletteColors[blockId * 3 + 2];

            // Emit top face (6 vertices, 2 triangles)
            // Normal: (0, 1, 0)
            AddVertex(verts, ref offset, wx, wy + 1, wz, 0, 1, 0, r, g, b);
            AddVertex(verts, ref offset, wx, wy + 1, wz + 1, 0, 1, 0, r, g, b);
            AddVertex(verts, ref offset, wx + 1, wy + 1, wz + 1, 0, 1, 0, r, g, b);
            AddVertex(verts, ref offset, wx, wy + 1, wz, 0, 1, 0, r, g, b);
            AddVertex(verts, ref offset, wx + 1, wy + 1, wz + 1, 0, 1, 0, r, g, b);
            AddVertex(verts, ref offset, wx + 1, wy + 1, wz, 0, 1, 0, r, g, b);
        }

        int vertexCount = offset / 9;
        if (vertexCount == 0) return ([], 0);

        // Trim to actual size
        var result = new float[offset];
        Array.Copy(verts, result, offset);
        return (result, vertexCount);
    }

    private static void AddVertex(float[] verts, ref int offset,
        float px, float py, float pz, float nx, float ny, float nz, float r, float g, float b)
    {
        verts[offset++] = px; verts[offset++] = py; verts[offset++] = pz;
        verts[offset++] = nx; verts[offset++] = ny; verts[offset++] = nz;
        verts[offset++] = r;  verts[offset++] = g;  verts[offset++] = b;
    }
}
