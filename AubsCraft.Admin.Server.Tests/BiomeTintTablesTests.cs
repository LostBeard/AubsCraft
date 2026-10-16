using AubsCraft.Admin.Rendering;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model.Biomes;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Which blocks take their colour from the biome (and which map), and what a column's biome cells make of the colour table.</summary>
public class BiomeTintTablesTests
{
    private static BlockAsset Tinted(string tint, int faces, int[] colour, string kind = BlockKinds.Cube) =>
        new() { Kind = kind, Faces = Enumerable.Repeat(5, 6).ToArray(), TintFaces = faces, Tint = tint, TintColor = colour };

    private static readonly BlockAssets Assets = new()
    {
        TileSize = 16, TilesPerRow = 8, TileCount = 64,
        Blocks =
        {
            ["minecraft:grass_block"] = Tinted(BlockTints.Grass, 1 << 4, new[] { 145, 189, 89 }),
            ["minecraft:oak_leaves"] = Tinted(BlockTints.Foliage, 63, new[] { 119, 171, 47 }),
            ["minecraft:birch_leaves"] = Tinted(BlockTints.Fixed, 63, new[] { 128, 167, 85 }),
            ["minecraft:water"] = Tinted(BlockTints.Water, 63, new[] { 63, 118, 228 }, BlockKinds.Water),
            ["minecraft:stone"] = new BlockAsset { Kind = BlockKinds.Cube, Faces = Enumerable.Repeat(2, 6).ToArray() },
            ["minecraft:seagrass"] = new BlockAsset { Kind = BlockKinds.Cross, Faces = Enumerable.Repeat(9, 6).ToArray(), TintFaces = 63, Tint = BlockTints.Grass, TintColor = new[] { 145, 189, 89 } },
        },
    };

    private static uint KindOf(string name) => VoxelBlockTables.MaterialOf(name, Assets).Flags & BlockMaterial.TintKindMask;

    [Test]
    public void GrassLeavesAndWater_FollowTheBiome_EachByItsOwnMap()
    {
        Assert.That(KindOf("minecraft:grass_block"), Is.EqualTo(BlockMaterial.TintGrass));
        Assert.That(KindOf("minecraft:oak_leaves"), Is.EqualTo(BlockMaterial.TintFoliage));
        Assert.That(KindOf("minecraft:water"), Is.EqualTo(BlockMaterial.TintWater));
    }

    [Test]
    public void AFixedTint_AndAPlainBlock_DoNotFollowTheBiome()
    {
        Assert.That(KindOf("minecraft:birch_leaves"), Is.EqualTo(0u), "birch leaves are always that green");
        Assert.That(KindOf("minecraft:stone"), Is.EqualTo(0u));
        // The fixed tint is still a tint (the material's own colour)
        var birch = VoxelBlockTables.MaterialOf("minecraft:birch_leaves", Assets);
        Assert.That(birch.TintR, Is.EqualTo(128f / 255f).Within(1e-4));
    }

    [Test]
    public void TheGrassBlocksTopOnly_AndWaterEverywhere_AreTinted_AsBefore()
    {
        var grass = VoxelBlockTables.MaterialOf("minecraft:grass_block", Assets);
        Assert.That((grass.Flags >> 16) & 63, Is.EqualTo(1u << 4), "only the top face takes the biome colour");
        var water = VoxelBlockTables.MaterialOf("minecraft:water", Assets);
        Assert.That(water.Flags & BlockMaterial.FlagTinted, Is.EqualTo(BlockMaterial.FlagTinted));
    }

    [Test]
    public void SeagrassStandingInWater_TakesTheWatersBiomeColour()
    {
        var seagrass = VoxelBlockTables.MaterialOf("minecraft:seagrass", Assets);
        Assert.That(seagrass.Flags & BlockMaterial.TintKindMask, Is.EqualTo(BlockMaterial.TintWater), "the water around it takes the water colour of the biome");
    }

    private static BiomeAssets TwoBiomes() => new()
    {
        Names = new[] { "minecraft:plains", "minecraft:swamp" },
        Grass = new byte[] { 0x91, 0xBD, 0x59, 0x6A, 0x70, 0x39 },
        Foliage = new byte[] { 0x77, 0xAB, 0x2F, 0x6A, 0x70, 0x39 },
        Water = new byte[] { 0x3F, 0x76, 0xE4, 0x61, 0x7B, 0x64 },
        PlainsId = 0,
    };

    [Test]
    public void AColumnsCells_TakeTheColoursOfTheirBiomes_AndAnUnknownIdIsPlains()
    {
        var table = BiomeColourTable.From(TwoBiomes());
        var ids = new byte[16];
        ids[5] = 1;                       // one swamp cell
        ids[9] = BiomeData.Unknown;       // one the table does not know
        var grass = new uint[16]; var foliage = new uint[16]; var water = new uint[16];
        table.Cells(ids, grass, foliage, water);
        Assert.That(grass[0], Is.EqualTo(0x91BD59u)); Assert.That(grass[5], Is.EqualTo(0x6A7039u)); Assert.That(grass[9], Is.EqualTo(0x91BD59u));
        Assert.That(foliage[5], Is.EqualTo(0x6A7039u)); Assert.That(water[5], Is.EqualTo(0x617B64u)); Assert.That(water[9], Is.EqualTo(0x3F76E4u));
    }

    [Test]
    public void ATableWhoseArraysDisagree_IsRefused_NotHalfRead()
    {
        var assets = TwoBiomes();
        assets.Water = new byte[] { 1, 2, 3 };
        Assert.Throws<ArgumentException>(() => BiomeColourTable.From(assets));
        Assert.Throws<ArgumentException>(() => BiomeColourTable.From(TwoBiomes()).Cells(new byte[15], new uint[16], new uint[16], new uint[16]));
    }
}
