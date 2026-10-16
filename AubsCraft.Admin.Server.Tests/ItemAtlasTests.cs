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
    public void APainting_IsABoxOfItsOwnSize_ThePictureOnTheFront_TheBackTextureOnTheBackAndEdges()
    {
        var jar = Jar();
        jar.Texture("painting/back", 16, 16, (x, y) => (90, 60, 30, 255));
        jar.Texture("painting/wide", 32, 16, (x, y) => ((byte)(x * 7), (byte)(y * 9), 5, 255));
        jar.Texture("block/birch_planks", 16, 16, (x, y) => (200, 180, 120, 255));
        jar.Texture("block/item_frame", 16, 16, (x, y) => (150, 100, 60, 255));
        jar.Texture("block/glow_item_frame", 16, 16, (x, y) => (60, 150, 100, 255));
        var r = Build(jar);
        var wide = r.Assets.Items["painting/wide"];
        Assert.That((wide[2], wide[3], wide[4]), Is.EqualTo((ItemAssets.KindHanging, 32, 16)));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++)
            {
                Assert.That(At(r, wide[0] + 1 + x, wide[1] + 1 + y), Is.EqualTo(((byte)(x * 7), (byte)(y * 9), (byte)5, (byte)255)), $"front ({x},{y})");
                Assert.That(At(r, wide[0] + 34 + x, wide[1] + 1 + y), Is.EqualTo(((byte)90, (byte)60, (byte)30, (byte)255)), $"back ({x},{y})");
            }
        Assert.That(At(r, wide[0] + 5, wide[1]), Is.EqualTo(((byte)90, (byte)60, (byte)30, (byte)255)), "an edge");
        var frame = r.Assets.Items["frame/item_frame"];
        Assert.That(At(r, frame[0] + 1, frame[1] + 1), Is.EqualTo(((byte)200, (byte)180, (byte)120, (byte)255)), "the ring is wood");
        Assert.That(At(r, frame[0] + 1 + 5, frame[1] + 1 + 5), Is.EqualTo(((byte)150, (byte)100, (byte)60, (byte)255)), "the middle is the back");
        Assert.That(At(r, r.Assets.Items["frame/glow_item_frame"][0] + 1 + 5, r.Assets.Items["frame/glow_item_frame"][1] + 1 + 5), Is.EqualTo(((byte)60, (byte)150, (byte)100, (byte)255)));
        // No two overlap, and all are inside the atlas
        var cells = r.Assets.Items.Values.Where(c => c[2] == ItemAssets.KindHanging).Select(c => (x: c[0], y: c[1], w: 2 * c[3] + 2, h: c[4] + 1)).ToList();
        foreach (var c in cells) { Assert.That(c.x + c.w, Is.LessThanOrEqualTo(r.Assets.AtlasWidth)); Assert.That(c.y + c.h, Is.LessThanOrEqualTo(r.Assets.AtlasHeight)); }
        for (int i = 0; i < cells.Count; i++)
            for (int j = i + 1; j < cells.Count; j++)
                Assert.That(cells[i].x + cells[i].w <= cells[j].x || cells[j].x + cells[j].w <= cells[i].x || cells[i].y + cells[i].h <= cells[j].y || cells[j].y + cells[j].h <= cells[i].y, Is.True, $"{i} overlaps {j}");
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
        // Paintings, with their own sizes, and the item frames
        var kebab = r.Assets.Items["painting/kebab"];
        Assert.That((kebab[2], kebab[3], kebab[4]), Is.EqualTo((ItemAssets.KindHanging, 16, 16)), "a one block painting is 16 pixels square");
        var pointer = r.Assets.Items["painting/pointer"];
        Assert.That((pointer[3], pointer[4]), Is.EqualTo((64, 64)), "a four by four block painting");
        Assert.That(r.Assets.Items.Keys.Count(k => k.StartsWith("painting/")), Is.GreaterThanOrEqualTo(40));
        foreach (var frame in new[] { "frame/item_frame", "frame/glow_item_frame" }) Assert.That(r.Assets.Items[frame].Skip(2).Take(3), Is.EqualTo(new[] { ItemAssets.KindHanging, 12, 12 }), frame);
        Assert.That(r.Assets.Items.ContainsKey("painting/back"), Is.False, "the back is not a painting");
        Assert.That(r.Assets.Items["oak_stairs"], Is.EqualTo(r.Assets.Items["oak_planks"]), "stairs show the planks' tiles: one picture");
        Assert.That(r.Rgba.Length, Is.EqualTo(r.Assets.AtlasWidth * r.Assets.AtlasHeight * 4));
        // Every item's cell is inside the atlas, and not blank
        foreach (var (name, cell) in r.Assets.Items)
        {
            int w = cell[2] switch { ItemAssets.KindSprite => ItemAssets.SpriteWidth, ItemAssets.KindHanging => 2 * cell[3] + 2, _ => ItemAssets.CubeWidth };
            int h = cell[2] switch { ItemAssets.KindSprite => ItemAssets.SpriteHeight, ItemAssets.KindHanging => cell[4] + 1, _ => ItemAssets.CubeHeight };
            Assert.That(cell[0] + w, Is.LessThanOrEqualTo(r.Assets.AtlasWidth), name);
            Assert.That(cell[1] + h, Is.LessThanOrEqualTo(r.Assets.AtlasHeight), name);
            bool any = false;
            for (int y = 0; y < h && !any; y++) for (int x = 0; x < w && !any; x++) any = At(r, cell[0] + x, cell[1] + y).a > 0;
            Assert.That(any, Is.True, $"{name} has pixels");
        }
        TestContext.Out.WriteLine($"atlas {r.Assets.AtlasWidth}x{r.Assets.AtlasHeight}, {r.Assets.Items.Count} items");
    }
}
