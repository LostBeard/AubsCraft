namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Generates a heightmap mesh with top faces AND side faces where terrain height changes.
/// This creates solid-looking terrain from any viewing angle.
/// Top faces: one per column at surface height.
/// Side faces: emitted between columns with different heights (terrain edges, cliffs).
/// </summary>
public static class HeightmapMesher
{
    // Max faces: 256 top + up to 4 sides per column with variable height spans
    // Generous allocation: 256 * (1 top + 4 sides * avg 3 blocks height) * 6 verts * 9 floats
    private const int MaxFloats = 256 * 20 * 6 * 9;

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
            float renderY = isWater ? wy + 0.1f : wy; // water surface slightly below block top

            // Top face
            EmitTopFace(verts, ref offset, wx, renderY, wz, r, g, b);

            // Side faces - emit where this column is taller than its neighbor
            // Grass/podzol/mycelium show dirt on sides (like Minecraft)
            float sideR, sideG, sideB;
            if (blockName is "minecraft:grass_block" or "minecraft:podzol" or "minecraft:mycelium")
            { sideR = 0.55f; sideG = 0.35f; sideB = 0.18f; } // dirt color
            else
            { sideR = r * 0.82f; sideG = g * 0.82f; sideB = b * 0.82f; }

            // -X neighbor
            int neighborH = GetHeight(heights, x - 1, z);
            if (wy > neighborH)
                EmitSideFaces(verts, ref offset, wx, wz, wz + 1, neighborH, wy, -1, 0, 0, sideR, sideG, sideB);

            // +X neighbor
            neighborH = GetHeight(heights, x + 1, z);
            if (wy > neighborH)
                EmitSideFaces(verts, ref offset, wx + 1, wz, wz + 1, neighborH, wy, 1, 0, 0, sideR, sideG, sideB);

            // -Z neighbor
            neighborH = GetHeight(heights, x, z - 1);
            if (wy > neighborH)
                EmitSideFaces(verts, ref offset, wz, wx, wx + 1, neighborH, wy, 0, 0, -1, sideR * 0.95f, sideG * 0.95f, sideB * 0.95f);

            // +Z neighbor
            neighborH = GetHeight(heights, x, z + 1);
            if (wy > neighborH)
                EmitSideFaces(verts, ref offset, wz + 1, wx, wx + 1, neighborH, wy, 0, 0, 1, sideR * 0.95f, sideG * 0.95f, sideB * 0.95f);
        }

        int vertexCount = offset / 9;
        if (vertexCount == 0) return ([], 0);

        var result = new float[offset];
        Array.Copy(verts, result, offset);
        return (result, vertexCount);
    }

    private static int GetHeight(int[] heights, int x, int z)
    {
        if (x < 0 || x >= 16 || z < 0 || z >= 16) return -64; // chunk edge = deep, show side face
        return heights[x + z * 16];
    }

    private static void EmitTopFace(float[] verts, ref int offset,
        float wx, float wy, float wz, float r, float g, float b)
    {
        float y = wy + 1;
        AddVertex(verts, ref offset, wx, y, wz, 0, 1, 0, r, g, b);
        AddVertex(verts, ref offset, wx, y, wz + 1, 0, 1, 0, r, g, b);
        AddVertex(verts, ref offset, wx + 1, y, wz + 1, 0, 1, 0, r, g, b);
        AddVertex(verts, ref offset, wx, y, wz, 0, 1, 0, r, g, b);
        AddVertex(verts, ref offset, wx + 1, y, wz + 1, 0, 1, 0, r, g, b);
        AddVertex(verts, ref offset, wx + 1, y, wz, 0, 1, 0, r, g, b);
    }

    private static void EmitSideFaces(float[] verts, ref int offset,
        float fixedAxis, float varStart, float varEnd, float bottomY, float topY,
        int nx, int ny, int nz, float r, float g, float b)
    {
        // Emit a vertical rectangle from bottomY+1 to topY+1 along the face
        float y0 = bottomY + 1;
        float y1 = topY + 1;

        if (offset + 54 > verts.Length) return; // safety check

        if (nx > 0) // +X face (normal pointing right)
        {
            float x = fixedAxis;
            AddVertex(verts, ref offset, x, y0, varStart, 1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y1, varEnd, 1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y1, varStart, 1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y0, varStart, 1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y0, varEnd, 1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y1, varEnd, 1, 0, 0, r, g, b);
        }
        else if (nx < 0) // -X face (normal pointing left)
        {
            float x = fixedAxis;
            AddVertex(verts, ref offset, x, y0, varStart, -1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y1, varStart, -1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y1, varEnd, -1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y0, varStart, -1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y1, varEnd, -1, 0, 0, r, g, b);
            AddVertex(verts, ref offset, x, y0, varEnd, -1, 0, 0, r, g, b);
        }
        else if (nz > 0) // +Z face
        {
            float z = fixedAxis;
            AddVertex(verts, ref offset, varStart, y0, z, 0, 0, 1, r, g, b);
            AddVertex(verts, ref offset, varStart, y1, z, 0, 0, 1, r, g, b);
            AddVertex(verts, ref offset, varEnd, y1, z, 0, 0, 1, r, g, b);
            AddVertex(verts, ref offset, varStart, y0, z, 0, 0, 1, r, g, b);
            AddVertex(verts, ref offset, varEnd, y1, z, 0, 0, 1, r, g, b);
            AddVertex(verts, ref offset, varEnd, y0, z, 0, 0, 1, r, g, b);
        }
        else // -Z face
        {
            float z = fixedAxis;
            AddVertex(verts, ref offset, varStart, y0, z, 0, 0, -1, r, g, b);
            AddVertex(verts, ref offset, varEnd, y1, z, 0, 0, -1, r, g, b);
            AddVertex(verts, ref offset, varStart, y1, z, 0, 0, -1, r, g, b);
            AddVertex(verts, ref offset, varStart, y0, z, 0, 0, -1, r, g, b);
            AddVertex(verts, ref offset, varEnd, y0, z, 0, 0, -1, r, g, b);
            AddVertex(verts, ref offset, varEnd, y1, z, 0, 0, -1, r, g, b);
        }
    }

    private static void AddVertex(float[] verts, ref int offset,
        float px, float py, float pz, float nx, float ny, float nz, float r, float g, float b)
    {
        verts[offset++] = px; verts[offset++] = py; verts[offset++] = pz;
        verts[offset++] = nx; verts[offset++] = ny; verts[offset++] = nz;
        verts[offset++] = r;  verts[offset++] = g;  verts[offset++] = b;
    }
}
