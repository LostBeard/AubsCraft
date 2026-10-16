using AubsCraft.Assets;
using AubsCraft.Assets.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The item atlas: a flat item is a sprite on both big faces, a block is a cube with its sides and top, identical pictures share a place, and the game's own items are all there.</summary>
public class ItemAtlasTests
{
    private static ItemAtlasResult Build(FakeJar jar) { using var j = jar.Build(); return new ItemAtlasBuilder(j, "test").Build(); }

    private static (byte r, byte g, byte b, byte a) At(ItemAtlasResult r, int x, int y)
    {
        int i = (y * r.Assets.AtlasWidth + x) * 4;
        return (r.Rgba[i], r.Rgba[i + 1], r.Rgba[i + 2], r.Rgba[i + 3]);
    }

    private static FakeJar Jar() => new FakeJar().WithStandardParents();

    [Test]
    public void AFlatItem_IsASpriteOnTheFront_AndItsMirrorImageOnTheBack_WithItsEdgesOnTheSides()
    {
        var jar = Jar();
        jar.Texture("item/apple", 16, 16, (x, y) => ((byte)(x * 10), (byte)(y * 10), 7, 255));
        jar.Model("item/apple", """{"parent":"minecraft:item/generated","textures":{"layer0":"minecraft:item/apple"}}""");
        jar.Json("assets/minecraft/items/apple.json", """{"model":{"type":"minecraft:model","model":"minecraft:item/apple"}}""");
        var r = Build(jar);
        var cell = r.Assets.Items["apple"];
        Assert.That(cell[2], Is.EqualTo(ItemAssets.KindSprite));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                var want = ((byte)(x * 10), (byte)(y * 10), (byte)7, (byte)255);
                var mirrored = ((byte)((15 - x) * 10), (byte)(y * 10), (byte)7, (byte)255);
                Assert.That(At(r, cell[0] + 1 + x, cell[1] + 1 + y), Is.EqualTo(want), $"front ({x},{y})");
                Assert.That(At(r, cell[0] + 18 + x, cell[1] + 1 + y), Is.EqualTo(mirrored), $"back ({x},{y}) is the mirror image");
            }
        Assert.That(At(r, cell[0] + 1 + 5, cell[1]), Is.EqualTo(((byte)50, (byte)0, (byte)7, (byte)255)), "the top strip is the sprite's top row");
        Assert.That(At(r, cell[0] + 17 + 5, cell[1]), Is.EqualTo(((byte)50, (byte)150, (byte)7, (byte)255)), "the bottom strip is its last row");
    }

    [Test]
    public void ALayeredItem_StacksItsLayers_AndAWornLeatherPieceIsTinted()
    {
        var jar = Jar();
        jar.Texture("item/base", 16, 16, (x, y) => (200, 200, 200, 255));
        jar.Texture("item/spot", 16, 16, (x, y) => x == 3 && y == 3 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        jar.Model("item/leather_cap", """{"parent":"minecraft:item/generated","textures":{"layer0":"minecraft:item/base","layer1":"minecraft:item/spot"}}""");
        jar.Json("assets/minecraft/items/leather_cap.json", """{"model":{"type":"minecraft:model","model":"minecraft:item/leather_cap"}}""");
        var r = Build(jar);
        var cell = r.Assets.Items["leather_cap"];
        Assert.That(At(r, cell[0] + 1 + 3, cell[1] + 1 + 3), Is.EqualTo(((byte)255, (byte)0, (byte)0, (byte)255)), "the overlay is on top, untinted");
        var (rr, g, b, _) = At(r, cell[0] + 1 + 8, cell[1] + 1 + 8);
        Assert.That((rr, g, b), Is.EqualTo(((byte)(200 * 160 / 255), (byte)(200 * 101 / 255), (byte)(200 * 64 / 255))), "the base is tinted brown");
    }

    [Test]
    public void ABlock_IsACube_WithItsSidesAndItsTop_AndTwoBlocksOfOnePictureShareAPlace()
    {
        var jar = Jar();
        jar.Texture("block/log_side", 16, 16, (x, y) => (120, 80, 40, 255));
        jar.Texture("block/log_top", 16, 16, (x, y) => (200, 170, 100, 255));
        jar.Model("block/oak_log", """{"parent":"minecraft:block/cube_column","textures":{"end":"minecraft:block/log_top","side":"minecraft:block/log_side"}}""");
        jar.Model("block/oak_wood", """{"parent":"minecraft:block/cube_column","textures":{"end":"minecraft:block/log_top","side":"minecraft:block/log_side"}}""");
        jar.Json("assets/minecraft/items/oak_log.json", """{"model":{"type":"minecraft:model","model":"minecraft:block/oak_log"}}""");
        jar.Json("assets/minecraft/items/oak_wood.json", """{"model":{"type":"minecraft:model","model":"minecraft:block/oak_wood"}}""");
        var r = Build(jar);
        var cell = r.Assets.Items["oak_log"];
        Assert.That(cell[2], Is.EqualTo(ItemAssets.KindCube));
        Assert.That(r.Assets.Items["oak_wood"], Is.EqualTo(cell), "the same picture, the same place");
        Assert.That(At(r, cell[0] + 16 + 8, cell[1] + 8), Is.EqualTo(((byte)200, (byte)170, (byte)100, (byte)255)), "top");
        Assert.That(At(r, cell[0] + 32 + 8, cell[1] + 8), Is.EqualTo(((byte)200, (byte)170, (byte)100, (byte)255)), "bottom");
        foreach (int x0 in new[] { 0, 16, 32, 48 })
            Assert.That(At(r, cell[0] + x0 + 8, cell[1] + 16 + 8), Is.EqualTo(((byte)120, (byte)80, (byte)40, (byte)255)), $"side at {x0}");
    }

    [Test]
    public void AModelDrawnByTheGamesCode_ShowsItsParticleTexture_AndAnItemWithNoModelIsLeftOut()
    {
        var jar = Jar();
        jar.Texture("block/planks", 16, 16, (x, y) => (150, 110, 60, 255));
        jar.Model("item/chest", """{"parent":"minecraft:builtin/entity","textures":{"particle":"minecraft:block/planks"}}""");
        jar.Json("assets/minecraft/items/chest.json", """{"model":{"type":"minecraft:special","base":"minecraft:item/chest","model":{"type":"minecraft:chest","texture":"minecraft:normal"}}}""");
        jar.Json("assets/minecraft/items/ghost.json", """{"model":{"type":"minecraft:model","model":"minecraft:item/ghost"}}""");
        var r = Build(jar);
        Assert.That(r.Assets.Items.Keys, Is.EquivalentTo(new[] { "chest" }));
        var cell = r.Assets.Items["chest"];
        Assert.That(cell[2], Is.EqualTo(ItemAssets.KindCube));
        Assert.That(At(r, cell[0] + 16 + 8, cell[1] + 16 + 8), Is.EqualTo(((byte)150, (byte)110, (byte)60, (byte)255)));
        Assert.That(r.Report, Does.Contain("NO picture: ghost"));
    }

    [Test]
    public void ATintedFace_TakesTheDroppedItemsColour()
    {
        var jar = Jar();
        jar.Texture("block/grass_top", 16, 16, (x, y) => (200, 200, 200, 255));
        jar.Texture("block/grass_side", 16, 16, (x, y) => (100, 70, 40, 255));
        jar.Model("block/grass_block", """{"elements":[{"from":[0,0,0],"to":[16,16,16],"faces":{"north":{"texture":"#side"},"up":{"texture":"#top","tintindex":0}}}],"textures":{"side":"block/grass_side","top":"block/grass_top"}}""");
        jar.Json("assets/minecraft/items/grass_block.json", """{"model":{"type":"minecraft:model","model":"minecraft:block/grass_block"}}""");
        var r = Build(jar);
        var cell = r.Assets.Items["grass_block"];
        var (rr, g, b, _) = At(r, cell[0] + 16 + 8, cell[1] + 8);
        Assert.That(g, Is.GreaterThan(rr), "a green top");
        Assert.That(At(r, cell[0] + 16 + 8, cell[1] + 16 + 8), Is.EqualTo(((byte)100, (byte)70, (byte)40, (byte)255)), "the dirt side");
    }

    [Test]
    public async Task TheGamesOwnItems_AreAllPictured()
    {
        using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync("1.21.5"));
        var r = new ItemAtlasBuilder(jar, "1.21.5").Build();
        TestContext.Out.WriteLine(r.Report.Length > 4000 ? r.Report[..4000] : r.Report);
        foreach (var name in new[] { "apple", "diamond_sword", "oak_log", "stone", "grass_block", "oak_leaves", "cobblestone", "bread", "arrow", "iron_pickaxe", "oak_planks", "dirt", "torch", "chest", "white_bed", "leather_boots", "enchanted_golden_apple" })
            Assert.That(r.Assets.Items.ContainsKey(name), Is.True, name);
        Assert.That(r.Assets.Items.Count, Is.GreaterThan(1000));
        Assert.That(r.Assets.Items["oak_stairs"], Is.EqualTo(r.Assets.Items["oak_planks"]), "stairs show the planks' tiles: one picture");
        Assert.That(r.Rgba.Length, Is.EqualTo(r.Assets.AtlasWidth * r.Assets.AtlasHeight * 4));
        // Every item's cell is inside the atlas, and not blank
        foreach (var (name, cell) in r.Assets.Items)
        {
            int w = cell[2] == ItemAssets.KindSprite ? ItemAssets.SpriteWidth : ItemAssets.CubeWidth, h = cell[2] == ItemAssets.KindSprite ? ItemAssets.SpriteHeight : ItemAssets.CubeHeight;
            Assert.That(cell[0] + w, Is.LessThanOrEqualTo(r.Assets.AtlasWidth), name);
            Assert.That(cell[1] + h, Is.LessThanOrEqualTo(r.Assets.AtlasHeight), name);
            bool any = false;
            for (int y = 0; y < h && !any; y++) for (int x = 0; x < w && !any; x++) any = At(r, cell[0] + x, cell[1] + y).a > 0;
            Assert.That(any, Is.True, $"{name} has pixels");
        }
        TestContext.Out.WriteLine($"atlas {r.Assets.AtlasWidth}x{r.Assets.AtlasHeight}, {r.Assets.Items.Count} items");
    }
}
