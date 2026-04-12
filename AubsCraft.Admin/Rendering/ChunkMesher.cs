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

            // Get atlas UVs and colors
            var (u0, v0, u1, v1) = TextureAtlas.GetTileUVs(blockName);
            float r = paletteColors[blockId * 3];
            float g = paletteColors[blockId * 3 + 1];
            float b = paletteColors[blockId * 3 + 2];

            // Tint: white for textured non-tinted blocks, vertex color for tinted/untextured
            bool isTinted = blockName.Contains("grass") || blockName.Contains("leaves")
                         || blockName.Contains("water") || blockName.Contains("vine")
                         || blockName.Contains("fern");
            float tr = r, tg = g, tb = b;
            if (u0 >= 0 && !isTinted) { tr = 1f; tg = 1f; tb = 1f; }

            // Check 6 faces
            if (IsAir(blocks, palette, x, y + 1, z)) // +Y top
                EmitFace(verts, wx, wy + 1, wz, 0, 1, 0, tr, tg, tb, u0, v0, u1, v1, 0);
            if (IsAir(blocks, palette, x, y - 1, z)) // -Y bottom
                EmitFace(verts, wx, wy, wz, 0, -1, 0, tr * 0.7f, tg * 0.7f, tb * 0.7f, u0, v0, u1, v1, 1);

            // Side faces - use side texture for grass
            float sr = tr * 0.85f, sg = tg * 0.85f, sb = tb * 0.85f;
            float su0 = u0, sv0 = v0, su1 = u1, sv1 = v1;
            if (blockName == "minecraft:grass_block")
            {
                sr = 1f; sg = 1f; sb = 1f;
                su0 = -1f; sv0 = -1f; su1 = -1f; sv1 = -1f; // flat color for grass sides
                sr = 0.53f; sg = 0.38f; sb = 0.26f;
            }

            if (IsAir(blocks, palette, x - 1, y, z))
                EmitFace(verts, wx, wy, wz, -1, 0, 0, sr, sg, sb, su0, sv0, su1, sv1, 2);
            if (IsAir(blocks, palette, x + 1, y, z))
                EmitFace(verts, wx + 1, wy, wz, 1, 0, 0, sr, sg, sb, su0, sv0, su1, sv1, 3);
            if (IsAir(blocks, palette, x, y, z - 1))
                EmitFace(verts, wx, wy, wz, 0, 0, -1, sr * 0.95f, sg * 0.95f, sb * 0.95f, su0, sv0, su1, sv1, 4);
            if (IsAir(blocks, palette, x, y, z + 1))
                EmitFace(verts, wx, wy, wz + 1, 0, 0, 1, sr * 0.95f, sg * 0.95f, sb * 0.95f, su0, sv0, su1, sv1, 5);
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
        // Transparent blocks that let faces show through
        return name is "minecraft:water" or "minecraft:flowing_water"
            or "minecraft:cave_air" or "minecraft:glass"
            or "minecraft:tinted_glass";
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
