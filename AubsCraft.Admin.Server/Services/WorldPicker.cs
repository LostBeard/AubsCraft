namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Which block a ray hits, from the chunks the panel holds (the live ones included): the first block along it that is not air or a liquid, and the face it was entered through. The viewer asks with its
/// camera's ray (or a controller's), so every mode picks the same way and no block data goes to the browser. A voxel walk (Amanatides and Woo), a block at a time.
/// </summary>
public static class WorldPicker
{
    public const int MinY = -64, MaxY = 319;
    /// <summary>Longest ray; the walk is at most this many blocks.</summary>
    public const double MaxReach = 256;

    public sealed record Hit(int X, int Y, int Z, int NormalX, int NormalY, int NormalZ, string Block, double Distance, double HitX, double HitY, double HitZ);

    /// <summary>Blocks a ray passes through: air, liquids, and the invisible helpers.</summary>
    public static bool PassesThrough(string name)
    {
        var plain = BlockStateNamer.NameOf(name);
        return plain is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air" or "minecraft:water" or "minecraft:lava" or "minecraft:light" or "minecraft:structure_void" or "minecraft:bubble_column";
    }

    /// <summary>The first block hit within <paramref name="maxDistance"/>, or null. <paramref name="chunkAt"/> gives the chunk of a column (null: unloaded, nothing there).</summary>
    public static Hit? Cast(Func<int, int, ChunkResult?> chunkAt, double ox, double oy, double oz, double dx, double dy, double dz, double maxDistance)
    {
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-9 || double.IsNaN(len) || double.IsInfinity(len)) return null;
        dx /= len; dy /= len; dz /= len;
        maxDistance = Math.Clamp(maxDistance, 0, MaxReach);

        int x = (int)Math.Floor(ox), y = (int)Math.Floor(oy), z = (int)Math.Floor(oz);
        int sx = dx >= 0 ? 1 : -1, sy = dy >= 0 ? 1 : -1, sz = dz >= 0 ? 1 : -1;
        double tdx = dx != 0 ? Math.Abs(1 / dx) : double.MaxValue, tdy = dy != 0 ? Math.Abs(1 / dy) : double.MaxValue, tdz = dz != 0 ? Math.Abs(1 / dz) : double.MaxValue;
        double tmx = dx != 0 ? ((dx > 0 ? x + 1 - ox : ox - x) * tdx) : double.MaxValue;
        double tmy = dy != 0 ? ((dy > 0 ? y + 1 - oy : oy - y) * tdy) : double.MaxValue;
        double tmz = dz != 0 ? ((dz > 0 ? z + 1 - oz : oz - z) * tdz) : double.MaxValue;

        // Chunks are looked up as the walk enters them
        int cachedCx = int.MinValue, cachedCz = int.MinValue;
        ChunkResult? chunk = null;
        int nx = 0, ny = 0, nz = 0;
        double t = 0;
        while (true)
        {
            if (y >= MinY && y <= MaxY)
            {
                int cx = x >> 4, cz = z >> 4;
                if (cx != cachedCx || cz != cachedCz) { cachedCx = cx; cachedCz = cz; chunk = chunkAt(cx, cz); }
                if (chunk != null)
                {
                    var name = chunk.Palette[chunk.Blocks[(x & 15) + (z & 15) * 16 + (y - MinY) * 256]];
                    if (!PassesThrough(name))
                    {
                        // Starting inside a block: the face is the one the ray would leave by, backwards
                        if (nx == 0 && ny == 0 && nz == 0)
                        {
                            double ax = Math.Abs(dx), ay = Math.Abs(dy), az = Math.Abs(dz);
                            if (ax >= ay && ax >= az) nx = -sx; else if (ay >= az) ny = -sy; else nz = -sz;
                        }
                        return new Hit(x, y, z, nx, ny, nz, name, t, ox + dx * t, oy + dy * t, oz + dz * t);
                    }
                }
            }
            // To the next block along the ray
            if (tmx < tmy && tmx < tmz) { t = tmx; if (t > maxDistance) return null; x += sx; tmx += tdx; nx = -sx; ny = 0; nz = 0; }
            else if (tmy < tmz) { t = tmy; if (t > maxDistance) return null; y += sy; tmy += tdy; nx = 0; ny = -sy; nz = 0; }
            else { t = tmz; if (t > maxDistance) return null; z += sz; tmz += tdz; nx = 0; ny = 0; nz = -sz; }
            // Out of the world vertically and moving away
            if ((y < MinY && sy < 0) || (y > MaxY && sy > 0)) return null;
        }
    }
}
