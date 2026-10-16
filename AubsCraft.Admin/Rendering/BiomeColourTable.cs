using AubsCraft.Assets.Model;
using AubsCraft.Live.Model.Biomes;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The colour of grass, leaves and water of every biome id (<see cref="BiomeData"/>), from the generated <see cref="BiomeAssets"/>, and what a column's 16 biome cells make of them: the colours the
/// world's biome maps are written with. An id the table does not know (a biome of a data pack, <see cref="BiomeData.Unknown"/>) is plains.
/// </summary>
public sealed class BiomeColourTable
{
    private readonly uint[] _grass, _foliage, _water;
    private readonly int _plains;

    private BiomeColourTable(uint[] grass, uint[] foliage, uint[] water, int plains)
    {
        _grass = grass; _foliage = foliage; _water = water; _plains = plains;
    }

    public int Count => _grass.Length;

    public static BiomeColourTable From(BiomeAssets assets)
    {
        int n = assets.Names.Length;
        if (assets.Grass.Length != n * 3 || assets.Foliage.Length != n * 3 || assets.Water.Length != n * 3)
            throw new ArgumentException($"The biome assets list {n} biomes but hold {assets.Grass.Length / 3} / {assets.Foliage.Length / 3} / {assets.Water.Length / 3} colours");
        uint[] Unpack(byte[] rgb)
        {
            var colours = new uint[n];
            for (int i = 0; i < n; i++) colours[i] = (uint)((rgb[i * 3] << 16) | (rgb[i * 3 + 1] << 8) | rgb[i * 3 + 2]);
            return colours;
        }
        return new BiomeColourTable(Unpack(assets.Grass), Unpack(assets.Foliage), Unpack(assets.Water), Math.Clamp(assets.PlainsId, 0, Math.Max(n - 1, 0)));
    }

    /// <summary>The colours of a column's 16 cells (ids at j * 4 + i): the three outputs each take 16.</summary>
    public void Cells(ReadOnlySpan<byte> biomeIds, Span<uint> grass, Span<uint> foliage, Span<uint> water)
    {
        if (biomeIds.Length != 16 || grass.Length != 16 || foliage.Length != 16 || water.Length != 16) throw new ArgumentException("A column has 16 cells");
        for (int i = 0; i < 16; i++)
        {
            int id = biomeIds[i] < _grass.Length ? biomeIds[i] : _plains;
            grass[i] = _grass[id]; foliage[i] = _foliage[id]; water[i] = _water[id];
        }
    }
}
