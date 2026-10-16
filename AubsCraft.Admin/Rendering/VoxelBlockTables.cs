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

    /// <summary>The plain block name of a registry name: <c>minecraft:oak_log[axis=x]</c> is <c>minecraft:oak_log</c>.</summary>
    public static string BaseName(string name)
    {
        int i = name.IndexOf('[');
        return i < 0 ? name : name[..i];
    }

    // The asset of a registry name: its exact state when the assets have one (a log lying down), else the block's own (default state)
    private static bool TryAsset(string name, BlockAssets assets, out BlockAsset block) =>
        assets.Blocks.TryGetValue(name, out block!) || (name.Contains('[') && assets.Blocks.TryGetValue(BaseName(name), out block!));

    /// <summary>Plants that stand in a cell of water: the cell is water, with the plant in it (the registry carries no waterlogged state, and these are never anywhere else).</summary>
    public static bool IsAquaticPlant(string name) => BaseName(name) is "minecraft:seagrass" or "minecraft:tall_seagrass" or "minecraft:kelp" or "minecraft:kelp_plant";

    /// <summary>The mesh class of a block name.</summary>
    public static int ClassOf(string name, BlockAssets assets)
    {
        // Chests, beds, signs, banners, heads, shulker boxes: the game draws them with a model (BlockEntityLayer draws them here too), not with cube faces
        if (AubsCraft.Live.Model.BlockEntities.BlockEntityKinds.IsBlockEntityBlock(BaseName(name))) return BlockClass.None;
        if (IsAquaticPlant(name)) return BlockClass.Plant | BlockClass.Water;
        if (TryAsset(name, assets, out var block))
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
        return IsAir(BaseName(name)) ? BlockClass.None : BlockClass.Opaque;
    }

    /// <summary>The material flag for the biome colour a tint takes: grass, foliage and water follow the biome (the map the world fills), a fixed tint is the material's own colour.</summary>
    public static uint TintKindOf(string tint) => tint switch
    {
        BlockTints.Grass => BlockMaterial.TintGrass,
        BlockTints.Foliage => BlockMaterial.TintFoliage,
        BlockTints.Water => BlockMaterial.TintWater,
        _ => 0u,
    };

    /// <summary>How a block name is drawn.</summary>
    public static BlockMaterial MaterialOf(string name, BlockAssets assets)
    {
        if (!TryAsset(name, assets, out var block))
        {
            // Unknown to the assets: its flat colour on every face
            var (r, g, b) = BlockColorMap.GetColor(BaseName(name));
            return BlockMaterial.Uniform(BlockMaterial.NoTexture, new Vector3(r, g, b));
        }

        ushort Tile(int i) => block.Faces[i] < 0 ? BlockMaterial.NoTexture : (ushort)block.Faces[i];

        // Seagrass and kelp: water on the cube faces (the surface, the sides where it meets air or other blocks) and the plant on face 5, which the plant planes read
        if (IsAquaticPlant(name) && assets.Blocks.TryGetValue("minecraft:water", out var water))
        {
            ushort w = water.Faces[0] < 0 ? BlockMaterial.NoTexture : (ushort)water.Faces[0];
            var waterTint = new Vector3(water.TintColor[0], water.TintColor[1], water.TintColor[2]) / 255f;
            return BlockMaterial.Create(w, w, w, w, w, Tile(0), waterTint,
                BlockMaterial.TintFaces(0b011111) | BlockMaterial.TintWater | BlockMaterial.FlagLowerTop | BlockMaterial.FlagFlatShading | BlockMaterial.FlagPlantFromBottom, WaterAlpha);
        }
        // The colour a tinted face is multiplied by (the biome colour: the plains one until a biome says otherwise), or, with no tint, the
        // flat colour of a face that has no texture
        Vector3 colour;
        if (block.TintFaces != 0) colour = new Vector3(block.TintColor[0], block.TintColor[1], block.TintColor[2]) / 255f;
        else
        {
            var (r, g, b) = BlockColorMap.GetColor(BaseName(name));
            colour = new Vector3(r, g, b);
        }

        uint flags = BlockMaterial.TintFaces(block.TintFaces);
        if (EmissiveBlocks.Is(name)) flags |= BlockMaterial.FlagEmissive;
        if (block.TintFaces != 0 || block.Kind == BlockKinds.Water) flags |= TintKindOf(block.Tint);
        // A thin block is a squashed cube; a shaped block is drawn as its boxes (and, far away, as a plain cube): no squash
        if (block.Kind == BlockKinds.Shape && !block.Shaped) flags |= BlockMaterial.Thickness(block.Thickness) | BlockMaterial.Slope(block.Slope);
        float alpha = 1f;
        if (block.Kind == BlockKinds.Water)
        {
            flags |= BlockMaterial.FlagTinted | BlockMaterial.TintWater | BlockMaterial.FlagLowerTop | BlockMaterial.FlagFlatShading;
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

    /// <summary>The class and material tables for ids 0 .. names.Count-1 (blocks that are not cubes are drawn as cubes: no shape table).</summary>
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

    /// <summary>
    /// <see cref="BuildWithSeabeds"/> and the shapes: a block name whose assets have shape rules (a stair, a slab, a fence, a door...) gets the boxes of ITS state (the properties in the
    /// name, <c>minecraft:oak_stairs[facing=east,half=bottom,shape=straight]</c>) in the returned <see cref="ShapeTable"/>, a class that says so and a material that points at its slot. A
    /// state with no boxes, or past the table's 1,024 shapes, stays what <see cref="Build"/> makes of it. Names with the same boxes share a slot, so the table holds the shapes a world
    /// really uses. Slots are given in name order: a longer list of names (a registry only appends) keeps every slot it had.
    /// </summary>
    public static (int[] Classes, BlockMaterial[] Materials, ShapeTable Shapes) BuildWithSeabedsAndShapes(IReadOnlyList<string> names, BlockAssets assets)
    {
        var (classes, materials) = BuildWithSeabeds(names, assets);
        var table = new ShapeTable();
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++)
        {
            var boxes = ShapeBoxesOf(names[i], assets);
            if (boxes == null) continue;
            string key = ShapeKey(boxes);
            if (!slotOf.TryGetValue(key, out int slot))
            {
                if (table.Count >= ShapeTable.MaxSlots) continue;   // out of slots: this one stays a cube
                slot = slotOf[key] = table.Add(boxes);
            }
            classes[i] = BlockClass.ShapeSlot(slot);
            materials[i].Flags |= BlockMaterial.Shape(slot);
        }
        return (classes, materials, table);
    }

    /// <summary>The boxes of a block state's shape, or null when the block is not shaped or the state has none.</summary>
    public static List<ShapeBox>? ShapeBoxesOf(string name, BlockAssets assets)
    {
        string baseName = BaseName(name);
        if (AubsCraft.Live.Model.BlockEntities.BlockEntityKinds.IsBlockEntityBlock(baseName)) return null;
        if (!assets.Shapes.ContainsKey(baseName)) return null;
        var resolved = ShapeResolver.Resolve(assets, baseName, StateProps(name));
        if (resolved == null || resolved.Count == 0 || resolved.Count > ShapeTable.MaxBoxes) return null;
        var boxes = new List<ShapeBox>(resolved.Count);
        foreach (var box in resolved)
        {
            var faces = new ShapeFace[6];
            for (int f = 0; f < 6; f++)
            {
                var face = box.Faces[f];
                faces[f] = face.Tile < 0 ? ShapeFace.None : new ShapeFace(face.Tile, face.U0, face.V0, face.U1, face.V1, face.Turns, face.Tint);
            }
            // A flat plate lying on the ground (leaf litter, redstone wire, a coral fan: the game puts it 0.1/16 up, which whole sixteenths round to nothing) would sit exactly on the top of the block under it, and the two faces fight for the pixel: it flickers as the head moves. A sixteenth up it does not.
            int y1 = box.Y0 == 0 && box.Y1 == 0 ? 1 : box.Y1;
            boxes.Add(new ShapeBox(box.X0, box.Y0, box.Z0, box.X1, y1, box.Z1, faces));
        }
        return boxes;
    }

    /// <summary>The properties of a registry name: <c>minecraft:oak_stairs[facing=east,half=top]</c> is facing = east, half = top.</summary>
    public static Dictionary<string, string> StateProps(string name)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        int open = name.IndexOf('[');
        if (open < 0 || !name.EndsWith(']')) return props;
        foreach (var part in name[(open + 1)..^1].Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) props[part[..eq]] = part[(eq + 1)..];
        }
        return props;
    }

    private static string ShapeKey(List<ShapeBox> boxes)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in boxes)
        {
            sb.Append(b.X0).Append(',').Append(b.Y0).Append(',').Append(b.Z0).Append(',').Append(b.X1).Append(',').Append(b.Y1).Append(',').Append(b.Z1);
            foreach (var f in b.Faces) sb.Append('|').Append(f.Tile).Append(',').Append(f.U0).Append(',').Append(f.V0).Append(',').Append(f.U1).Append(',').Append(f.V1).Append(',').Append(f.Rotation).Append(f.Tinted ? 't' : 'f');
            sb.Append(';');
        }
        return sb.ToString();
    }
}
