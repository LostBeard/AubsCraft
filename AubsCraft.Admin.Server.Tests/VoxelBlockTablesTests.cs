using System.Numerics;
using AubsCraft.Admin.Rendering;
using AubsCraft.Assets.Model;
using SpawnDev.VoxelEngine.Meshing;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The client's per-block-type tables for the VoxelEngine renderer, built from the server's block names and the generated block assets.</summary>
public class VoxelBlockTablesTests
{
    private static BlockAsset Cube(int tile, string render = BlockRenders.Opaque) =>
        new() { Kind = BlockKinds.Cube, Faces = Enumerable.Repeat(tile, 6).ToArray(), Render = render };

    // A few blocks as the generator describes them
    private static readonly BlockAssets Assets = new()
    {
        TileSize = 16, TilesPerRow = 8, TileCount = 64,
        Blocks =
        {
            ["minecraft:air"] = new BlockAsset { Kind = BlockKinds.None },
            ["minecraft:stone"] = Cube(2),
            // top 11 tinted green at draw time, sides 10 (the fringe baked in), bottom 12 (dirt)
            ["minecraft:grass_block"] = new BlockAsset
            {
                Kind = BlockKinds.Cube, Faces = new[] { 10, 10, 10, 10, 11, 12 }, TintFaces = 1 << 4, Tint = BlockTints.Grass, TintColor = new[] { 145, 189, 89 },
            },
            ["minecraft:water"] = new BlockAsset
            {
                Kind = BlockKinds.Water, Faces = Enumerable.Repeat(20, 6).ToArray(), TintFaces = 63, Tint = BlockTints.Water, TintColor = new[] { 63, 118, 228 },
                Render = BlockRenders.Translucent,
            },
            ["minecraft:oak_leaves"] = new BlockAsset
            {
                Kind = BlockKinds.Cube, Faces = Enumerable.Repeat(30, 6).ToArray(), TintFaces = 63, Tint = BlockTints.Foliage, TintColor = new[] { 119, 171, 47 }, Render = BlockRenders.Cutout,
            },
            ["minecraft:glass"] = Cube(31, BlockRenders.Cutout),
            ["minecraft:ice"] = Cube(32, BlockRenders.Translucent),
            ["minecraft:oak_stairs"] = new BlockAsset { Kind = BlockKinds.Shape, Faces = Enumerable.Repeat(40, 6).ToArray() },
            ["minecraft:short_grass"] = new BlockAsset
            {
                Kind = BlockKinds.Cross, Faces = Enumerable.Repeat(50, 6).ToArray(), TintFaces = 63, Tint = BlockTints.Grass, TintColor = new[] { 145, 189, 89 }, Render = BlockRenders.Cutout,
            },
            ["minecraft:white_carpet"] = new BlockAsset { Kind = BlockKinds.Shape, Faces = Enumerable.Repeat(60, 6).ToArray(), Thickness = 1 },
            ["minecraft:rail"] = new BlockAsset { Kind = BlockKinds.Shape, Faces = Enumerable.Repeat(61, 6).ToArray(), Render = BlockRenders.Cutout, Thickness = 1 },
            ["minecraft:snow"] = new BlockAsset { Kind = BlockKinds.Shape, Faces = Enumerable.Repeat(62, 6).ToArray(), Thickness = 2 },
            ["minecraft:seagrass"] = new BlockAsset { Kind = BlockKinds.Cross, Faces = Enumerable.Repeat(63, 6).ToArray(), Render = BlockRenders.Cutout },
            ["minecraft:poppy"] = new BlockAsset { Kind = BlockKinds.Cross, Faces = Enumerable.Repeat(51, 6).ToArray(), Render = BlockRenders.Cutout },
        },
    };

    private static ushort[] Faces(BlockMaterial m) =>
    [
        (ushort)(m.Layers01 & 0xFFFF), (ushort)(m.Layers01 >> 16),
        (ushort)(m.Layers23 & 0xFFFF), (ushort)(m.Layers23 >> 16),
        (ushort)(m.Layers45 & 0xFFFF), (ushort)(m.Layers45 >> 16),
    ];

    private static Vector3 Tint(BlockMaterial m) => new(m.TintR, m.TintG, m.TintB);

    [TestCase("minecraft:air", BlockClass.None)]
    [TestCase("minecraft:stone", BlockClass.Opaque)]
    [TestCase("minecraft:grass_block", BlockClass.Opaque)]   // a cube with a tinted top, not a plant
    [TestCase("minecraft:water", BlockClass.Water)]
    [TestCase("minecraft:oak_leaves", BlockClass.Cutout)]
    [TestCase("minecraft:glass", BlockClass.Cutout)]
    [TestCase("minecraft:ice", BlockClass.Cutout)]           // partly transparent texels: shows what is behind it
    [TestCase("minecraft:oak_stairs", BlockClass.Opaque)]    // a shape, drawn as a cube for now
    [TestCase("minecraft:short_grass", BlockClass.Cross)]
    [TestCase("minecraft:poppy", BlockClass.Cross)]
    [TestCase("minecraft:cave_air", BlockClass.None)]        // not in the assets, but air is air
    [TestCase("minecraft:void_air", BlockClass.None)]
    [TestCase("somemod:strange_block", BlockClass.Opaque)]   // unknown: an opaque cube in its flat colour
    public void ClassOf_MatchesHowTheBlockMeshes(string name, int expected) =>
        Assert.That(VoxelBlockTables.ClassOf(name, Assets), Is.EqualTo(expected));

    [Test]
    public void GrassBlock_HasATileForEachFace_AndOnlyItsTopIsTinted()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:grass_block", Assets);
        Assert.That(Faces(m), Is.EqualTo(new ushort[] { 10, 10, 10, 10, 11, 12 }));
        Assert.That(m.Flags & BlockMaterial.FlagTinted, Is.Zero, "not tinted outright");
        Assert.That(m.Flags, Is.EqualTo(BlockMaterial.TintFaces(1 << 4)), "the top only");
        Assert.That(Tint(m), Is.EqualTo(new Vector3(145, 189, 89) / 255f), "the plains grass colour");
    }

    [Test]
    public void TexturedPlainBlock_ShowsItsTextureAsIs()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:stone", Assets);
        Assert.That(Faces(m), Is.All.EqualTo((ushort)2));
        Assert.That(m.Flags, Is.EqualTo(0u), "no tint: the texture is drawn unchanged");
    }

    [Test]
    public void LeavesAreTintedOnEveryFace()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:oak_leaves", Assets);
        Assert.That(m.Flags, Is.EqualTo(BlockMaterial.TintFaces(63)));
        Assert.That(Tint(m), Is.EqualTo(new Vector3(119, 171, 47) / 255f));
    }

    [Test]
    public void APlant_UsesItsFirstTile_AndItsTint()
    {
        var grass = VoxelBlockTables.MaterialOf("minecraft:short_grass", Assets);
        Assert.That(Faces(grass)[0], Is.EqualTo((ushort)50));
        Assert.That(grass.Flags & BlockMaterial.TintFaces(1), Is.Not.Zero, "face 0 is the one a plant uses: it must be in the mask");
        Assert.That(VoxelBlockTables.MaterialOf("minecraft:poppy", Assets).Flags, Is.Zero);
    }

    [Test]
    public void ABlockTheAssetsDoNotKnow_IsItsFlatColour()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:beacon", Assets); // a colour in BlockColorMap, no tile
        Assert.That(Faces(m), Is.All.EqualTo(BlockMaterial.NoTexture));
        Assert.That(Tint(m), Is.EqualTo(new Vector3(0.50f, 0.85f, 0.85f)));
    }

    [Test]
    public void Water_IsTranslucent_WithALoweredTopAndFlatShading_AndTinted()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:water", Assets);
        Assert.That(m.Alpha, Is.EqualTo(0.7f));
        Assert.That(m.Flags, Is.EqualTo(BlockMaterial.FlagTinted | BlockMaterial.FlagLowerTop | BlockMaterial.FlagFlatShading | BlockMaterial.TintFaces(63)));
        Assert.That(Faces(m), Is.All.EqualTo((ushort)20));
        Assert.That(Tint(m), Is.EqualTo(new Vector3(63, 118, 228) / 255f));
        Assert.That(VoxelBlockTables.MaterialOf("minecraft:stone", Assets).Alpha, Is.EqualTo(1f));
    }

    [Test]
    public void Seabed_IsTheSameBlockDarkened_AtTheOffsetSlot()
    {
        string[] names = ["minecraft:air", "minecraft:stone", "minecraft:grass_block", "minecraft:beacon"];
        var (classes, all) = VoxelBlockTables.BuildWithSeabeds(names, Assets);
        Assert.That(classes, Has.Length.EqualTo(4));
        Assert.That(all, Has.Length.EqualTo(VoxelBlockTables.SeabedTypeOffset + 4));

        foreach (int i in new[] { 1, 2, 3 })
        {
            var plain = all[i];
            var seabed = all[VoxelBlockTables.SeabedTypeOffset + i];
            Assert.That(Faces(seabed), Is.EqualTo(Faces(plain)), "the same textures");
            Assert.That(seabed.Flags, Is.EqualTo(plain.Flags | BlockMaterial.FlagDarken), "the same flags, darkened");
            Assert.That(Tint(seabed), Is.EqualTo(Tint(plain)), "the tint colour is untouched: the darkening is its own flag");
            Assert.That(plain.Flags & BlockMaterial.FlagDarken, Is.Zero, "the plain entries are not darkened");
        }
        Assert.That(all[100].TintG, Is.EqualTo(0f), "an unregistered type is magenta");
    }

    [Test]
    public void TooManyNames_DoNotFitBelowTheSeabedOffset()
    {
        var names = Enumerable.Range(0, VoxelBlockTables.SeabedTypeOffset + 1).Select(i => "x:b" + i).ToArray();
        Assert.Throws<InvalidOperationException>(() => VoxelBlockTables.BuildWithSeabeds(names, Assets));
    }

    [Test]
    public void Build_IndexIsTheGlobalId()
    {
        string[] names = ["minecraft:air", "minecraft:stone", "minecraft:water", "minecraft:short_grass"];
        var (classes, materials) = VoxelBlockTables.Build(names, Assets);
        Assert.That(classes, Is.EqualTo(new[] { BlockClass.None, BlockClass.Opaque, BlockClass.Water, BlockClass.Cross }));
        Assert.That(materials, Has.Length.EqualTo(4));
        Assert.That(Faces(materials[1])[0], Is.EqualTo((ushort)2));
        Assert.That(materials[2].Alpha, Is.EqualTo(0.7f));
    }

    [Test]
    public void WithoutAssets_EveryBlockIsItsFlatColour_AndTheViewerStillWorks()
    {
        var empty = new BlockAssets();
        var (classes, materials) = VoxelBlockTables.Build(["minecraft:air", "minecraft:stone", "minecraft:water"], empty);
        Assert.That(classes[0], Is.EqualTo(BlockClass.None));
        Assert.That(classes[1], Is.EqualTo(BlockClass.Opaque));
        Assert.That(Faces(materials[1]), Is.All.EqualTo(BlockMaterial.NoTexture));
    }

    [Test]
    public void AThinShape_HidesNothing_AndIsSquashedToItsThickness()
    {
        foreach (var (name, thickness) in new[] { ("minecraft:white_carpet", 1), ("minecraft:rail", 1), ("minecraft:snow", 2) })
        {
            Assert.That(VoxelBlockTables.ClassOf(name, Assets), Is.EqualTo(BlockClass.Cutout), name + ": a layer hides neither the block beside it nor the one below");
            Assert.That(VoxelBlockTables.MaterialOf(name, Assets).Flags & ~(63u << 16), Is.EqualTo(BlockMaterial.Thickness(thickness)), name);
        }
        // A shape with no thickness stays a plain cube material, a full block
        Assert.That(VoxelBlockTables.MaterialOf("minecraft:oak_stairs", Assets).Flags & ~(63u << 16), Is.EqualTo(0u));
        Assert.That(VoxelBlockTables.ClassOf("minecraft:oak_stairs", Assets), Is.EqualTo(BlockClass.Opaque));
    }

    [Test]
    public void SeagrassAndKelp_AreWaterWithAPlantInIt()
    {
        foreach (var name in new[] { "minecraft:seagrass", "minecraft:tall_seagrass", "minecraft:kelp", "minecraft:kelp_plant" })
        {
            Assert.That(VoxelBlockTables.IsAquaticPlant(name), Is.True, name);
            var cls = VoxelBlockTables.ClassOf(name, Assets);
            Assert.That(cls & BlockClass.Plant, Is.Not.EqualTo(0), name + " is a plant");
            Assert.That(cls & BlockClass.EmitWater, Is.Not.EqualTo(0), name + " draws water");
            Assert.That(cls & BlockClass.EmitOpaque, Is.EqualTo(0), name + " has no cube faces of its own");
            Assert.That(cls & BlockClass.OccludeWater, Is.Not.EqualTo(0), name + " hides the water faces between it and other water");
        }
        var m = VoxelBlockTables.MaterialOf("minecraft:seagrass", Assets);
        Assert.That(Faces(m), Is.EqualTo(new ushort[] { 20, 20, 20, 20, 20, 63 }), "water on the cube faces, the plant on face 5");
        Assert.That(m.Flags & BlockMaterial.FlagPlantFromBottom, Is.Not.EqualTo(0u));
        Assert.That(m.Flags & BlockMaterial.FlagLowerTop, Is.Not.EqualTo(0u), "the surface sits a tenth low like the rest of the water");
        Assert.That((m.Flags >> 16) & 63, Is.EqualTo(0b011111u), "the water faces are tinted, the plant (face 5) is not");
        Assert.That(Tint(m), Is.EqualTo(new Vector3(63, 118, 228) / 255f));
        Assert.That(m.Alpha, Is.EqualTo(VoxelBlockTables.WaterAlpha));
        // A plant that is not aquatic is unchanged
        Assert.That(VoxelBlockTables.IsAquaticPlant("minecraft:poppy"), Is.False);
        Assert.That(VoxelBlockTables.ClassOf("minecraft:poppy", Assets), Is.EqualTo(BlockClass.Cross));
    }
}
