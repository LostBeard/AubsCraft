namespace AubsCraft.Admin.Rendering;

/// <summary>
/// CPU mesher for full 3D chunk rendering with texture atlas UVs.
/// Emits all exposed block faces (not just top like HeightmapMesher).
/// Vertex format: 11 floats (pos3 + normal3 + color3 + uv2).
/// Used for nearby chunks where full block detail is visible.
/// </summary>
public static class ChunkMesher
{
    private const int W = 16, H = 384;
    private const int FPV = 11; // floats per vertex

    public static (float[] vertices, int vertexCount) GenerateMesh(
        ushort[] blocks, List<string> palette, float[] paletteColors,
        int chunkX, int chunkZ)
    {
        // Estimate: typical chunk has ~30K exposed faces
        var verts = new List<float>(30000 * 6 * FPV);

        for (int y = 0; y < H; y++)
        for (int z = 0; z < W; z++)
        for (int x = 0; x < W; x++)
        {
            int blockId = blocks[x + z * W + y * W * W];
            if (blockId == 0) continue;

            var blockName = blockId < palette.Count ? palette[blockId] : "";
            if (blockName == "minecraft:cave_air") continue;

            float wx = chunkX * W + x;
            float wy = y - 64f;
            float wz = chunkZ * W + z;

            // Plants render as cross-shaped quads (two diagonal intersecting planes)
            if (TextureAtlas.IsPlant(blockName))
            {
                var (u0, v0, u1, v1) = TextureAtlas.GetTileUVs(blockName);
                float r = paletteColors[blockId * 3];
                float g = paletteColors[blockId * 3 + 1];
                float b = paletteColors[blockId * 3 + 2];
                // Tinted plants (grass, fern) keep vertex color; flowers use white tint
                bool isTinted = blockName.Contains("grass") || blockName.Contains("fern");
                if (u0 >= 0 && !isTinted) { r = 1f; g = 1f; b = 1f; }
                EmitCrossQuads(verts, wx, wy, wz, r, g, b, u0, v0, u1, v1);
                continue;
            }

            // Get per-face atlas UVs and colors for solid blocks
            var (topUV, sideUV, botUV) = TextureAtlas.GetPerFaceUVs(blockName);
            {
                float r = paletteColors[blockId * 3];
                float g = paletteColors[blockId * 3 + 1];
                float b = paletteColors[blockId * 3 + 2];

                // Tint: white for textured non-tinted blocks, vertex color for tinted/untextured
                bool isTinted = blockName.Contains("grass") || blockName.Contains("leaves")
                             || blockName.Contains("water") || blockName.Contains("vine")
                             || blockName.Contains("fern");
                float tr = r, tg = g, tb = b;
                if (topUV.u0 >= 0 && !isTinted) { tr = 1f; tg = 1f; tb = 1f; }

                // Side tint: white for textured non-tinted, vertex color for tinted/untextured
                float sr = r, sg = g, sb = b;
                if (sideUV.u0 >= 0 && !isTinted) { sr = 1f; sg = 1f; sb = 1f; }

                // Bottom tint
                float br2 = r, bg = g, bb = b;
                if (botUV.u0 >= 0 && !isTinted) { br2 = 1f; bg = 1f; bb = 1f; }

                // Check 6 faces
                if (IsAir(blocks, palette, x, y + 1, z)) // +Y top
                    EmitFace(verts, wx, wy + 1, wz, 0, 1, 0, tr, tg, tb, topUV.u0, topUV.v0, topUV.u1, topUV.v1, 0);
                if (IsAir(blocks, palette, x, y - 1, z)) // -Y bottom
                    EmitFace(verts, wx, wy, wz, 0, -1, 0, br2 * 0.7f, bg * 0.7f, bb * 0.7f, botUV.u0, botUV.v0, botUV.u1, botUV.v1, 1);

                // Side faces
                if (IsAir(blocks, palette, x - 1, y, z))
                    EmitFace(verts, wx, wy, wz, -1, 0, 0, sr * 0.85f, sg * 0.85f, sb * 0.85f, sideUV.u0, sideUV.v0, sideUV.u1, sideUV.v1, 2);
                if (IsAir(blocks, palette, x + 1, y, z))
                    EmitFace(verts, wx + 1, wy, wz, 1, 0, 0, sr * 0.85f, sg * 0.85f, sb * 0.85f, sideUV.u0, sideUV.v0, sideUV.u1, sideUV.v1, 3);
                if (IsAir(blocks, palette, x, y, z - 1))
                    EmitFace(verts, wx, wy, wz, 0, 0, -1, sr * 0.80f, sg * 0.80f, sb * 0.80f, sideUV.u0, sideUV.v0, sideUV.u1, sideUV.v1, 4);
                if (IsAir(blocks, palette, x, y, z + 1))
                    EmitFace(verts, wx, wy, wz + 1, 0, 0, 1, sr * 0.80f, sg * 0.80f, sb * 0.80f, sideUV.u0, sideUV.v0, sideUV.u1, sideUV.v1, 5);
            }
        }

        var arr = verts.ToArray();
        return (arr, arr.Length / FPV);
    }

    private static bool IsAir(ushort[] blocks, List<string> palette, int x, int y, int z)
    {
        if (x < 0 || x >= W || z < 0 || z >= W || y < 0 || y >= H) return true;
        var id = blocks[x + z * W + y * W * W];
        if (id == 0) return true;
        var name = id < palette.Count ? palette[id] : "";
        // Transparent blocks that let adjacent faces show through
        if (name is "minecraft:water" or "minecraft:flowing_water"
            or "minecraft:cave_air" or "minecraft:glass"
            or "minecraft:tinted_glass")
            return true;
        // Plant blocks are transparent - adjacent solid blocks should show faces
        return TextureAtlas.IsPlant(name);
    }

    /// <summary>
    /// Emits two diagonal intersecting quads for plant/flower blocks.
    /// Creates an X-shape when viewed from above, like real Minecraft plant rendering.
    /// Both front and back faces are emitted (4 triangles total per quad = 24 vertices).
    /// </summary>
    private static void EmitCrossQuads(List<float> v,
        float x, float y, float z,
        float r, float g, float b,
        float u0, float v0, float u1, float v1)
    {
        // Diagonal 1: from (x,z) to (x+1,z+1) - NE/SW diagonal
        // Front face (normal roughly toward +X-Z)
        float nx1 = 0.707f, nz1 = -0.707f;
        V(v, x, y, z, nx1, 0, nz1, r, g, b, u0, v1);
        V(v, x, y + 1, z, nx1, 0, nz1, r, g, b, u0, v0);
        V(v, x + 1, y + 1, z + 1, nx1, 0, nz1, r, g, b, u1, v0);
        V(v, x, y, z, nx1, 0, nz1, r, g, b, u0, v1);
        V(v, x + 1, y + 1, z + 1, nx1, 0, nz1, r, g, b, u1, v0);
        V(v, x + 1, y, z + 1, nx1, 0, nz1, r, g, b, u1, v1);
        // Back face (flipped winding for opposite side)
        V(v, x + 1, y, z + 1, -nx1, 0, -nz1, r, g, b, u0, v1);
        V(v, x + 1, y + 1, z + 1, -nx1, 0, -nz1, r, g, b, u0, v0);
        V(v, x, y + 1, z, -nx1, 0, -nz1, r, g, b, u1, v0);
        V(v, x + 1, y, z + 1, -nx1, 0, -nz1, r, g, b, u0, v1);
        V(v, x, y + 1, z, -nx1, 0, -nz1, r, g, b, u1, v0);
        V(v, x, y, z, -nx1, 0, -nz1, r, g, b, u1, v1);

        // Diagonal 2: from (x+1,z) to (x,z+1) - NW/SE diagonal
        // Front face (normal roughly toward -X-Z)
        float nx2 = -0.707f, nz2 = -0.707f;
        V(v, x + 1, y, z, nx2, 0, nz2, r, g, b, u0, v1);
        V(v, x + 1, y + 1, z, nx2, 0, nz2, r, g, b, u0, v0);
        V(v, x, y + 1, z + 1, nx2, 0, nz2, r, g, b, u1, v0);
        V(v, x + 1, y, z, nx2, 0, nz2, r, g, b, u0, v1);
        V(v, x, y + 1, z + 1, nx2, 0, nz2, r, g, b, u1, v0);
        V(v, x, y, z + 1, nx2, 0, nz2, r, g, b, u1, v1);
        // Back face
        V(v, x, y, z + 1, -nx2, 0, -nz2, r, g, b, u0, v1);
        V(v, x, y + 1, z + 1, -nx2, 0, -nz2, r, g, b, u0, v0);
        V(v, x + 1, y + 1, z, -nx2, 0, -nz2, r, g, b, u1, v0);
        V(v, x, y, z + 1, -nx2, 0, -nz2, r, g, b, u0, v1);
        V(v, x + 1, y + 1, z, -nx2, 0, -nz2, r, g, b, u1, v0);
        V(v, x + 1, y, z, -nx2, 0, -nz2, r, g, b, u1, v1);
    }

    private static void EmitFace(List<float> v,
        float x, float y, float z, int nx, int ny, int nz,
        float r, float g, float b, float u0, float v0, float u1, float v1, int face)
    {
        // 6 vertices per face, CCW winding per face direction
        switch (face)
        {
            case 0: // +Y top
                V(v, x, y, z, nx, ny, nz, r, g, b, u0, v0);
                V(v, x, y, z + 1, nx, ny, nz, r, g, b, u0, v1);
                V(v, x + 1, y, z + 1, nx, ny, nz, r, g, b, u1, v1);
                V(v, x, y, z, nx, ny, nz, r, g, b, u0, v0);
                V(v, x + 1, y, z + 1, nx, ny, nz, r, g, b, u1, v1);
                V(v, x + 1, y, z, nx, ny, nz, r, g, b, u1, v0);
                break;
            case 1: // -Y bottom
                V(v, x, y, z + 1, nx, ny, nz, r, g, b, u0, v1);
                V(v, x, y, z, nx, ny, nz, r, g, b, u0, v0);
                V(v, x + 1, y, z, nx, ny, nz, r, g, b, u1, v0);
                V(v, x, y, z + 1, nx, ny, nz, r, g, b, u0, v1);
                V(v, x + 1, y, z, nx, ny, nz, r, g, b, u1, v0);
                V(v, x + 1, y, z + 1, nx, ny, nz, r, g, b, u1, v1);
                break;
            case 2: // -X
                V(v, x, y, z + 1, nx, ny, nz, r, g, b, u0, v1);
                V(v, x, y + 1, z + 1, nx, ny, nz, r, g, b, u0, v0);
                V(v, x, y + 1, z, nx, ny, nz, r, g, b, u1, v0);
                V(v, x, y, z + 1, nx, ny, nz, r, g, b, u0, v1);
                V(v, x, y + 1, z, nx, ny, nz, r, g, b, u1, v0);
                V(v, x, y, z, nx, ny, nz, r, g, b, u1, v1);
                break;
            case 3: // +X
                V(v, x, y, z, nx, ny, nz, r, g, b, u0, v1);
                V(v, x, y + 1, z, nx, ny, nz, r, g, b, u0, v0);
                V(v, x, y + 1, z + 1, nx, ny, nz, r, g, b, u1, v0);
                V(v, x, y, z, nx, ny, nz, r, g, b, u0, v1);
                V(v, x, y + 1, z + 1, nx, ny, nz, r, g, b, u1, v0);
                V(v, x, y, z + 1, nx, ny, nz, r, g, b, u1, v1);
                break;
            case 4: // -Z
                V(v, x, y, z, nx, ny, nz, r, g, b, u0, v1);
                V(v, x, y + 1, z, nx, ny, nz, r, g, b, u0, v0);
                V(v, x + 1, y + 1, z, nx, ny, nz, r, g, b, u1, v0);
                V(v, x, y, z, nx, ny, nz, r, g, b, u0, v1);
                V(v, x + 1, y + 1, z, nx, ny, nz, r, g, b, u1, v0);
                V(v, x + 1, y, z, nx, ny, nz, r, g, b, u1, v1);
                break;
            case 5: // +Z
                V(v, x + 1, y, z, nx, ny, nz, r, g, b, u0, v1);
                V(v, x + 1, y + 1, z, nx, ny, nz, r, g, b, u0, v0);
                V(v, x, y + 1, z, nx, ny, nz, r, g, b, u1, v0);
                V(v, x + 1, y, z, nx, ny, nz, r, g, b, u0, v1);
                V(v, x, y + 1, z, nx, ny, nz, r, g, b, u1, v0);
                V(v, x, y, z, nx, ny, nz, r, g, b, u1, v1);
                break;
        }
    }

    private static void V(List<float> v, float px, float py, float pz,
        float nx, float ny, float nz, float r, float g, float b, float u, float uv)
    {
        v.Add(px); v.Add(py); v.Add(pz);
        v.Add(nx); v.Add(ny); v.Add(nz);
        v.Add(r); v.Add(g); v.Add(b);
        v.Add(u); v.Add(uv);
    }
}
