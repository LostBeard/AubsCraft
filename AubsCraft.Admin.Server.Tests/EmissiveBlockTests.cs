using AubsCraft.Admin.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Which blocks glow: the light-emitting ones, and the ones that only do when lit.</summary>
public class EmissiveBlockTests
{
    [Test]
    public void TheGlowingBlocks_AreEmissive()
    {
        foreach (var name in new[] { "minecraft:lava", "minecraft:glowstone", "minecraft:sea_lantern", "minecraft:shroomlight", "minecraft:torch", "minecraft:wall_torch[facing=north]", "minecraft:soul_torch",
                     "minecraft:lantern[hanging=true]", "minecraft:jack_o_lantern[facing=south]", "minecraft:fire", "minecraft:magma_block", "minecraft:pearlescent_froglight[axis=y]", "minecraft:end_rod[facing=up]", "minecraft:nether_portal[axis=x]" })
            Assert.That(EmissiveBlocks.Is(name), Is.True, name);
    }

    [Test]
    public void ALitLampOrCampfire_IsEmissive_ADarkOneIsNot()
    {
        Assert.That(EmissiveBlocks.Is("minecraft:redstone_lamp[lit=true]"), Is.True);
        Assert.That(EmissiveBlocks.Is("minecraft:redstone_lamp[lit=false]"), Is.False);
        Assert.That(EmissiveBlocks.Is("minecraft:campfire[facing=north,lit=true]"), Is.True);
        Assert.That(EmissiveBlocks.Is("minecraft:campfire[facing=north,lit=false]"), Is.False);
        Assert.That(EmissiveBlocks.Is("minecraft:redstone_torch"), Is.True, "no lit in the name: lit");
        Assert.That(EmissiveBlocks.Is("minecraft:redstone_wall_torch[facing=east,lit=false]"), Is.False);
        Assert.That(EmissiveBlocks.Is("minecraft:copper_bulb[lit=true]"), Is.True);
        Assert.That(EmissiveBlocks.Is("minecraft:copper_bulb[lit=false]"), Is.False);
    }

    [Test]
    public void OrdinaryBlocks_AreNot()
    {
        foreach (var name in new[] { "minecraft:stone", "minecraft:grass_block", "minecraft:oak_planks", "minecraft:glass", "minecraft:furnace[facing=north,lit=true]", "minecraft:obsidian", "minecraft:water", "minecraft:air" })
            Assert.That(EmissiveBlocks.Is(name), Is.False, name);
        Assert.That(EmissiveBlocks.Is("minecraft:respawn_anchor[charges=0]"), Is.False);
        Assert.That(EmissiveBlocks.Is("minecraft:respawn_anchor[charges=3]"), Is.True);
    }

    [Test]
    public async Task TheGamesRealBlocks_GetTheFlag_InTheirMaterials()
    {
        using var jar = AubsCraft.Assets.ClientJar.Open(await AubsCraft.Assets.JarLocator.FindOrDownloadAsync("1.21.5"));
        var assets = new AubsCraft.Assets.BlockAssetBuilder(jar, "1.21.5").Build().Assets;
        var lava = VoxelBlockTables.MaterialOf("minecraft:lava", assets);
        var stone = VoxelBlockTables.MaterialOf("minecraft:stone", assets);
        Assert.That(lava.Flags & SpawnDev.VoxelEngine.Rendering.BlockMaterial.FlagEmissive, Is.Not.Zero);
        Assert.That(stone.Flags & SpawnDev.VoxelEngine.Rendering.BlockMaterial.FlagEmissive, Is.Zero);
    }
}
