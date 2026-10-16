using AubsCraft.Assets.Model;
using AubsCraft.Live.Model.Biomes;
using StbImageSharp;

namespace AubsCraft.Assets;

/// <summary>Works out the grass, foliage and water colour of every biome of <see cref="BiomeData"/> from the client jar's colour maps (<see cref="BiomeAssets"/>).</summary>
public sealed class BiomeColorBuilder
{
    private readonly ClientJar _jar;
    private readonly string _version;

    public BiomeColorBuilder(ClientJar jar, string version)
    {
        _jar = jar;
        _version = version;
    }

    private byte[] Map(string name)
    {
        var image = ImageResult.FromMemory(_jar.ReadBytes($"assets/minecraft/textures/colormap/{name}.png"), ColorComponents.RedGreenBlueAlpha);
        if (image.Width != 256 || image.Height != 256) throw new InvalidOperationException($"The {name} colour map is {image.Width}x{image.Height}, expected 256x256");
        return image.Data;
    }

    public BiomeAssets Build()
    {
        var grassMap = Map("grass");
        var foliageMap = Map("foliage");
        var all = BiomeData.All;
        var assets = new BiomeAssets
        {
            McVersion = _version,
            Names = all.Select(b => b.Name).ToArray(),
            Grass = new byte[all.Length * 3],
            Foliage = new byte[all.Length * 3],
            Water = new byte[all.Length * 3],
            PlainsId = BiomeData.Plains,
        };
        for (int i = 0; i < all.Length; i++)
        {
            Put(assets.Grass, i, BiomeColors.Grass(all[i], grassMap));
            Put(assets.Foliage, i, BiomeColors.Foliage(all[i], foliageMap));
            Put(assets.Water, i, BiomeColors.Water(all[i]));
        }
        return assets;
    }

    private static void Put(byte[] target, int index, int rgb)
    {
        target[index * 3] = (byte)(rgb >> 16);
        target[index * 3 + 1] = (byte)(rgb >> 8);
        target[index * 3 + 2] = (byte)rgb;
    }
}
