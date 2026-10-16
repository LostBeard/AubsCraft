using AubsCraft.Live.Model.Biomes;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// What falls where, for the rain and snow: for each loaded column, how high the sky is blocked over each of its 256 blocks (the server's <c>X-RainHeights</c>) and its biomes (what the biome lets
/// fall: nothing in a desert, snow in the cold, rain elsewhere). <see cref="Build"/> turns the columns round the camera into the window of cells <see cref="WeatherPipeline"/> draws.
/// </summary>
public sealed class WeatherMap
{
    /// <summary>The window's side in columns: the streaks reach 12 blocks from the camera.</summary>
    public const int Side = 24;

    private sealed record Column(ushort[] Heights, byte[]? Biomes);

    private readonly Dictionary<(int cx, int cz), Column> _columns = new();
    private readonly uint[] _cells = new uint[Side * Side];
    private bool _dirty = true;
    private int _x0 = int.MinValue, _z0 = int.MinValue;

    public int ColumnCount => _columns.Count;

    /// <summary>How many of the window's columns have something to fall on them (as of the last <see cref="Build"/>).</summary>
    public int Active { get; private set; }

    /// <summary>A column's heights (256: x + z * 16, as the server sends them) and biomes (16 cells, or none).</summary>
    public void SetColumn(int cx, int cz, ushort[] heights, byte[]? biomes)
    {
        if (heights.Length != 256) return;
        _columns[(cx, cz)] = new Column(heights, biomes is { Length: 16 } ? biomes : null);
        _dirty = true;
    }

    public void RemoveColumn(int cx, int cz)
    {
        if (_columns.Remove((cx, cz))) _dirty = true;
    }

    /// <summary>What falls on a block of the biome: <see cref="WeatherPipeline.KindNone"/> (a desert, a savanna, a badlands: too hot), snow (too cold for rain) or rain.</summary>
    public static uint KindOf(byte biome)
    {
        if (biome >= BiomeData.All.Length) return WeatherPipeline.KindRain;   // an unknown biome is drawn as plains
        float t = BiomeData.All[biome].Temperature;
        return t >= 1.5f ? WeatherPipeline.KindNone : t <= 0.15f ? WeatherPipeline.KindSnow : WeatherPipeline.KindRain;
    }

    /// <summary>The window round (x, z): its first column's world x and z, and its cells (<see cref="Side"/> squared); false when nothing changed since the last call.</summary>
    public bool Build(float cameraX, float cameraZ, out int x0, out int z0, out uint[] cells)
    {
        x0 = (int)MathF.Floor(cameraX) - Side / 2;
        z0 = (int)MathF.Floor(cameraZ) - Side / 2;
        cells = _cells;
        if (!_dirty && x0 == _x0 && z0 == _z0) return false;
        _dirty = false;
        _x0 = x0;
        _z0 = z0;
        int active = 0;
        for (int dz = 0; dz < Side; dz++)
            for (int dx = 0; dx < Side; dx++)
            {
                int x = x0 + dx, z = z0 + dz;
                uint cell = 0;
                if (_columns.TryGetValue((x >> 4, z >> 4), out var column))
                {
                    int lx = x & 15, lz = z & 15;
                    uint kind = column.Biomes is { } biomes ? KindOf(biomes[(lz >> 2) * 4 + (lx >> 2)]) : WeatherPipeline.KindRain;
                    cell = (kind << 16) | column.Heights[lz * 16 + lx];
                }
                _cells[dz * Side + dx] = cell;
                if (cell >> 16 != 0) active++;
            }
        Active = active;
        return true;
    }
}
