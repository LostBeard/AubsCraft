using System.Numerics;
using AubsCraft.Admin.Rendering;
using SpawnDev.VoxelEngine.Meshing;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The client's per-block-type tables for the VoxelEngine renderer, built from the server's block names.</summary>
public class VoxelBlockTablesTests
{
    private static ushort[] Faces(BlockMaterial m) =>
    [
        (ushort)(m.Layers01 & 0xFFFF), (ushort)(m.Layers01 >> 16),
        (ushort)(m.Layers23 & 0xFFFF), (ushort)(m.Layers23 >> 16),
        (ushort)(m.Layers45 & 0xFFFF), (ushort)(m.Layers45 >> 16),
    ];

    [TestCase("minecraft:air", BlockClass.None)]
    [TestCase("minecraft:cave_air", BlockClass.None)]
    [TestCase("minecraft:void_air", BlockClass.None)]
    [TestCase("minecraft:stone", BlockClass.Opaque)]
    [TestCase("minecraft:grass_block", BlockClass.Opaque)]   // contains "grass" but is a cube, not a plant
    [TestCase("minecraft:water", BlockClass.Water)]
    [TestCase("minecraft:flowing_water", BlockClass.Water)]
    [TestCase("minecraft:oak_leaves", BlockClass.Cutout)]
    [TestCase("minecraft:glass", BlockClass.Cutout)]
    [TestCase("minecraft:tinted_glass", BlockClass.Cutout)]
    [TestCase("minecraft:short_grass", BlockClass.Cross)]
    [TestCase("minecraft:poppy", BlockClass.Cross)]
    public void ClassOf_MatchesHowTheBlockMeshes(string name, int expected) =>
        Assert.That(VoxelBlockTables.ClassOf(name), Is.EqualTo(expected));

    [Test]
    public void GrassBlock_TopSideBottomLayers_AndTinted()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:grass_block");
        // sides 84 (grass_side), top 0 (grass_top), bottom 1 (dirt): the atlas's per-face table.
        Assert.That(Faces(m), Is.EqualTo(new ushort[] { 84, 84, 84, 84, 0, 1 }));
        Assert.That(m.Flags & BlockMaterial.FlagTinted, Is.EqualTo(BlockMaterial.FlagTinted));
        Assert.That(new Vector3(m.TintR, m.TintG, m.TintB), Is.EqualTo(new Vector3(0.30f, 0.65f, 0.20f)), "the biome colour");
    }

    [Test]
    public void TexturedPlainBlock_ShowsItsTextureAsIs()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:stone");
        Assert.That(Faces(m), Is.EqualTo(new ushort[] { 2, 2, 2, 2, 2, 2 }));
        Assert.That(m.Flags, Is.EqualTo(0u));
        Assert.That(new Vector3(m.TintR, m.TintG, m.TintB), Is.EqualTo(Vector3.One), "white tint = the texture unchanged");
    }

    [Test]
    public void UntexturedBlock_IsItsColour()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:beacon"); // a colour in BlockColorMap, no atlas tile
        Assert.That(Faces(m), Is.All.EqualTo(BlockMaterial.NoTexture));
        Assert.That(new Vector3(m.TintR, m.TintG, m.TintB), Is.EqualTo(new Vector3(0.50f, 0.85f, 0.85f)));
    }

    [Test]
    public void Water_IsTranslucent_WithALoweredTopAndFlatShading()
    {
        var m = VoxelBlockTables.MaterialOf("minecraft:water");
        Assert.That(m.Alpha, Is.EqualTo(0.6f));
        Assert.That(m.Flags, Is.EqualTo(BlockMaterial.FlagTinted | BlockMaterial.FlagLowerTop | BlockMaterial.FlagFlatShading));
        Assert.That(Faces(m), Is.All.EqualTo((ushort)50));
        Assert.That(VoxelBlockTables.MaterialOf("minecraft:stone").Alpha, Is.EqualTo(1f));
    }

    [Test]
    public void Build_IndexIsTheGlobalId()
    {
        string[] names = ["minecraft:air", "minecraft:stone", "minecraft:water", "minecraft:short_grass"];
        var (classes, materials) = VoxelBlockTables.Build(names);
        Assert.That(classes, Is.EqualTo(new[] { BlockClass.None, BlockClass.Opaque, BlockClass.Water, BlockClass.Cross }));
        Assert.That(materials, Has.Length.EqualTo(4));
        Assert.That(Faces(materials[1])[0], Is.EqualTo((ushort)2));
        Assert.That(materials[2].Alpha, Is.EqualTo(0.6f));
    }
}
