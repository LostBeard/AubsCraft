namespace AubsCraft.Live.Model.Biomes;

/// <summary>
/// The colours a biome gives grass, leaves and water, as the game works them out: the biome's climate picks a pixel of the grass or foliage colour map (a 256 x 256 picture: temperature across,
/// temperature times rainfall down), unless the biome fixes its own colour; the dark forest darkens its grass and the swamp has a murky one; water is always the biome's own.
/// Colours are 0xRRGGBB.
/// </summary>
public static class BiomeColors
{
    /// <summary>The colour map's pixel for a climate (the map as 256 x 256 RGBA bytes).</summary>
    public static int Lookup(byte[] colormapRgba, float temperature, float downfall)
    {
        float t = Math.Clamp(temperature, 0f, 1f);
        float d = Math.Clamp(downfall, 0f, 1f) * t;
        int x = (int)((1f - t) * 255f);
        int y = (int)((1f - d) * 255f);
        int i = (y * 256 + x) * 4;
        return (colormapRgba[i] << 16) | (colormapRgba[i + 1] << 8) | colormapRgba[i + 2];
    }

    public static int Grass(BiomeInfo biome, byte[] grassMapRgba)
    {
        int c = biome.Grass >= 0 ? biome.Grass : Lookup(grassMapRgba, biome.Temperature, biome.Downfall);
        return biome.Modifier switch
        {
            GrassModifier.DarkForest => ((c & 0xFEFEFE) + 0x28340A) >> 1,
            GrassModifier.Swamp => 0x6A7039,
            _ => c,
        };
    }

    public static int Foliage(BiomeInfo biome, byte[] foliageMapRgba)
        => biome.Foliage >= 0 ? biome.Foliage : Lookup(foliageMapRgba, biome.Temperature, biome.Downfall);

    public static int Water(BiomeInfo biome) => biome.Water;
}
