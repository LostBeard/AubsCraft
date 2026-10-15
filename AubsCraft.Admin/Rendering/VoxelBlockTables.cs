using System.Numerics;
using SpawnDev.VoxelEngine.Meshing;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// What SpawnDev.VoxelEngine needs to know about each block type, built from the server's block names
/// (<c>/api/world/blocks</c>: index = global block id): how the type meshes (<see cref="BlockClass"/>) and how it is drawn
/// (<see cref="BlockMaterial"/>). Same decisions the old viewer made per palette entry (flag 0 solid, 1 plant, 2 water, 3 tinted
/// solid, 4 leaves), expressed in the library's terms.
/// </summary>
public static class VoxelBlockTables
{
    /// <summary>The water pass opacity the old viewer used.</summary>
    public const float WaterAlpha = 0.6f;

    /// <summary>The mesh class of a block name.</summary>
    public static int ClassOf(string name)
    {
        if (name is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air") return BlockClass.None;
        if (name is "minecraft:water" or "minecraft:flowing_water") return BlockClass.Water;
        if (TextureAtlas.IsPlant(name)) return BlockClass.Cross;
        // Leaves and glass are drawn but hide nothing behind them.
        if (name.Contains("leaves") || name.Contains("glass")) return BlockClass.Cutout;
        return BlockClass.Opaque;
    }

    /// <summary>True when the block's colour multiplies its (grayscale) texture: grass, foliage, water, vines.</summary>
    public static bool IsTinted(string name) =>
        TextureAtlas.IsPlant(name)
        || name is "minecraft:water" or "minecraft:flowing_water"
        || name.Contains("leaves") || name.Contains("grass") || name.Contains("vine") || name.Contains("fern") || name.Contains("lily");

    /// <summary>How a block name is drawn.</summary>
    public static BlockMaterial MaterialOf(string name)
    {
        var (r, g, b) = BlockColorMap.GetColor(name);
        var colour = new Vector3(r, g, b);
        var (top, side, bottom) = TextureAtlas.GetFaceLayers(name);
        ushort Layer(int index) => index < 0 ? BlockMaterial.NoTexture : (ushort)index;
        bool textured = top >= 0 || side >= 0 || bottom >= 0;
        bool tinted = IsTinted(name);

        // Faces: 0 +X, 1 -X, 2 +Z, 3 -Z are sides, 4 +Y top, 5 -Y bottom.
        uint flags = tinted ? BlockMaterial.FlagTinted : 0;
        float alpha = 1f;
        if (name is "minecraft:water" or "minecraft:flowing_water")
        {
            flags |= BlockMaterial.FlagLowerTop | BlockMaterial.FlagFlatShading;
            alpha = WaterAlpha;
        }

        // A textured, untinted block shows its texture as is (white tint); an untextured one is its colour.
        var tint = textured && !tinted ? Vector3.One : colour;
        return BlockMaterial.Create(Layer(side), Layer(side), Layer(side), Layer(side), Layer(top), Layer(bottom), tint, flags, alpha);
    }

    /// <summary>
    /// What lies under water is drawn with the type + this offset: the same block, darker (the old far tier darkened a seabed to 0.7).
    /// Real block ids stay below it (the registry holds a few hundred names; <see cref="BuildWithSeabeds"/> checks).
    /// </summary>
    public const int SeabedTypeOffset = 2048;
    public const float SeabedDarkening = 0.7f;

    /// <summary>The material of a block seen through water: <see cref="MaterialOf"/> multiplied by <see cref="SeabedDarkening"/>.</summary>
    public static BlockMaterial SeabedMaterialOf(string name)
    {
        var m = MaterialOf(name);
        var tint = new Vector3(m.TintR, m.TintG, m.TintB) * SeabedDarkening;
        // A textured, untinted block has a white tint it does not use; the darkening needs the tint applied, so switch it on
        m.Flags |= BlockMaterial.FlagTinted;
        m.TintR = tint.X; m.TintG = tint.Y; m.TintB = tint.Z;
        return m;
    }

    /// <summary>
    /// <see cref="Build"/> plus the darker seabed variants: materials[i] draws block i, materials[<see cref="SeabedTypeOffset"/> + i] the
    /// same block under water. The class table is as for <see cref="Build"/>.
    /// </summary>
    public static (int[] Classes, BlockMaterial[] Materials) BuildWithSeabeds(IReadOnlyList<string> names)
    {
        if (names.Count > SeabedTypeOffset)
            throw new InvalidOperationException($"{names.Count} block names do not fit below the seabed offset {SeabedTypeOffset}");
        var (classes, materials) = Build(names);
        var all = new BlockMaterial[SeabedTypeOffset + names.Count];
        System.Array.Fill(all, BlockMaterial.Missing);
        materials.CopyTo(all, 0);
        for (int i = 0; i < names.Count; i++) all[SeabedTypeOffset + i] = SeabedMaterialOf(names[i]);
        return (classes, all);
    }

    /// <summary>The class and material tables for ids 0 .. names.Count-1.</summary>
    public static (int[] Classes, BlockMaterial[] Materials) Build(IReadOnlyList<string> names)
    {
        var classes = new int[names.Count];
        var materials = new BlockMaterial[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            classes[i] = ClassOf(names[i]);
            materials[i] = MaterialOf(names[i]);
        }
        return (classes, materials);
    }
}
