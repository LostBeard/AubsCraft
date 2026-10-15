using System.Numerics;
using AubsCraft.Assets.Model;
using SpawnDev.VoxelEngine.Meshing;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// What SpawnDev.VoxelEngine needs to know about each block type, built from the server's block names (<c>/api/world/blocks</c>: index =
/// global block id) and the generated <see cref="BlockAssets"/> (what each block looks like in the game's own assets): how the type meshes
/// (<see cref="BlockClass"/>) and how it is drawn (<see cref="BlockMaterial"/>). A name the assets do not know (a modded block, a newer version)
/// is drawn as an opaque cube in its flat colour (<see cref="BlockColorMap"/>), so it is visible and recognisable, never missing.
/// </summary>
public static class VoxelBlockTables
{
    /// <summary>The water pass opacity: the water texture's own, 180 of 255 (the game blends water at 70%).</summary>
    public const float WaterAlpha = 0.7f;

    private static bool IsAir(string name) => name is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air";

    /// <summary>Plants that stand in a cell of water: the cell is water, with the plant in it (the registry carries no waterlogged state, and these are never anywhere else).</summary>
    public static bool IsAquaticPlant(string name) => name is "minecraft:seagrass" or "minecraft:tall_seagrass" or "minecraft:kelp" or "minecraft:kelp_plant";

    /// <summary>The mesh class of a block name.</summary>
    public static int ClassOf(string name, BlockAssets assets)
    {
        if (IsAquaticPlant(name)) return BlockClass.Plant | BlockClass.Water;
        if (assets.Blocks.TryGetValue(name, out var block))
        {
            // A thin block (carpet, rail, plate, snow layer) is a layer on the block below: it hides nothing beside or under it
            if (block.Kind == BlockKinds.Shape && block.Thickness > 0) return BlockClass.Cutout;
            return block.Kind switch
            {
                BlockKinds.None => BlockClass.None,
                BlockKinds.Water => BlockClass.Water,
                BlockKinds.Cross => BlockClass.Cross,
                // Leaves, glass, ice and the like show what is behind them (cutout), and hide nothing of it
                _ => block.Render == BlockRenders.Opaque ? BlockClass.Opaque : BlockClass.Cutout,
            };
        }
        return IsAir(name) ? BlockClass.None : BlockClass.Opaque;
    }

    /// <summary>How a block name is drawn.</summary>
    public static BlockMaterial MaterialOf(string name, BlockAssets assets)
    {
        if (!assets.Blocks.TryGetValue(name, out var block))
        {
            // Unknown to the assets: its flat colour on every face
            var (r, g, b) = BlockColorMap.GetColor(name);
            return BlockMaterial.Uniform(BlockMaterial.NoTexture, new Vector3(r, g, b));
        }

        ushort Tile(int i) => block.Faces[i] < 0 ? BlockMaterial.NoTexture : (ushort)block.Faces[i];

        // Seagrass and kelp: water on the cube faces (the surface, the sides where it meets air or other blocks) and the plant on face 5, which the plant planes read
        if (IsAquaticPlant(name) && assets.Blocks.TryGetValue("minecraft:water", out var water))
        {
            ushort w = water.Faces[0] < 0 ? BlockMaterial.NoTexture : (ushort)water.Faces[0];
            var waterTint = new Vector3(water.TintColor[0], water.TintColor[1], water.TintColor[2]) / 255f;
            return BlockMaterial.Create(w, w, w, w, w, Tile(0), waterTint,
                BlockMaterial.TintFaces(0b011111) | BlockMaterial.FlagLowerTop | BlockMaterial.FlagFlatShading | BlockMaterial.FlagPlantFromBottom, WaterAlpha);
        }
        // The colour a tinted face is multiplied by (the biome colour: the plains one until a biome says otherwise), or, with no tint, the
        // flat colour of a face that has no texture
        Vector3 colour;
        if (block.TintFaces != 0) colour = new Vector3(block.TintColor[0], block.TintColor[1], block.TintColor[2]) / 255f;
        else
        {
            var (r, g, b) = BlockColorMap.GetColor(name);
            colour = new Vector3(r, g, b);
        }

        uint flags = BlockMaterial.TintFaces(block.TintFaces);
        if (block.Kind == BlockKinds.Shape) flags |= BlockMaterial.Thickness(block.Thickness);
        float alpha = 1f;
        if (block.Kind == BlockKinds.Water)
        {
            flags |= BlockMaterial.FlagTinted | BlockMaterial.FlagLowerTop | BlockMaterial.FlagFlatShading;
            alpha = WaterAlpha;
        }
        return BlockMaterial.Create(Tile(0), Tile(1), Tile(2), Tile(3), Tile(4), Tile(5), colour, flags, alpha);
    }

    /// <summary>
    /// What lies under water is drawn with the type + this offset: the same block, darker (<see cref="BlockMaterial.FlagDarken"/>).
    /// Real block ids stay below it (the registry holds a few hundred names; <see cref="BuildWithSeabeds"/> checks).
    /// </summary>
    public const int SeabedTypeOffset = 2048;

    /// <summary>The material of a block seen through water: <see cref="MaterialOf"/> darkened.</summary>
    public static BlockMaterial SeabedMaterialOf(string name, BlockAssets assets)
    {
        var m = MaterialOf(name, assets);
        m.Flags |= BlockMaterial.FlagDarken;
        return m;
    }

    /// <summary>
    /// <see cref="Build"/> plus the darker seabed variants: materials[i] draws block i, materials[<see cref="SeabedTypeOffset"/> + i] the
    /// same block under water. The class table is as for <see cref="Build"/>.
    /// </summary>
    public static (int[] Classes, BlockMaterial[] Materials) BuildWithSeabeds(IReadOnlyList<string> names, BlockAssets assets)
    {
        if (names.Count > SeabedTypeOffset)
            throw new InvalidOperationException($"{names.Count} block names do not fit below the seabed offset {SeabedTypeOffset}");
        var (classes, materials) = Build(names, assets);
        var all = new BlockMaterial[SeabedTypeOffset + names.Count];
        System.Array.Fill(all, BlockMaterial.Missing);
        materials.CopyTo(all, 0);
        for (int i = 0; i < names.Count; i++) all[SeabedTypeOffset + i] = SeabedMaterialOf(names[i], assets);
        return (classes, all);
    }

    /// <summary>The class and material tables for ids 0 .. names.Count-1.</summary>
    public static (int[] Classes, BlockMaterial[] Materials) Build(IReadOnlyList<string> names, BlockAssets assets)
    {
        var classes = new int[names.Count];
        var materials = new BlockMaterial[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            classes[i] = ClassOf(names[i], assets);
            materials[i] = MaterialOf(names[i], assets);
        }
        return (classes, materials);
    }
}
