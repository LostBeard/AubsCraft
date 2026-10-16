using AubsCraft.Admin.Rendering;
using AubsCraft.Assets;
using AubsCraft.Assets.Model;
using SpawnDev.VoxelEngine.Meshing;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The blocks that are not cubes (stairs, slabs, fences, doors...) as boxes: the generator keeps their models and blockstate rules, the resolver works out the boxes of a state (turned
/// as the blockstate says), the tables give the engine a slot for each state a world has. The synthetic jar pins the logic; the real jar pins the game's own numbers.
/// </summary>
public class ShapeAssetTests
{
    private static BuildResult Build(FakeJar jar)
    {
        using var client = jar.Build();
        return new BlockAssetBuilder(client, "test").Build();
    }

    private static string Describe(IEnumerable<ResolvedBox> boxes) => string.Join(" ", boxes.Select(b => $"({b.X0},{b.Y0},{b.Z0})-({b.X1},{b.Y1},{b.Z1})"));

    private static Dictionary<string, string> P(string props) =>
        props.Length == 0 ? new() : props.Split(',').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => kv[1]);

    // The vanilla stairs model (explicit windows), with textures for bottom, top and side
    private static FakeJar StairsJar()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/planks_bottom", 10, 10, 10).Solid("block/planks_top", 20, 20, 20).Solid("block/planks_side", 30, 30, 30);
        jar.Model("block/stairs", """
            { "parent": "block/block", "textures": { "bottom": "block/planks_bottom", "top": "block/planks_top", "side": "block/planks_side" }, "elements": [
              { "from": [0,0,0], "to": [16,8,16], "faces": {
                  "down":  { "uv": [0,0,16,16], "texture": "#bottom", "cullface": "down" }, "up": { "uv": [0,0,16,16], "texture": "#top" },
                  "north": { "uv": [0,8,16,16], "texture": "#side", "cullface": "north" },  "south": { "uv": [0,8,16,16], "texture": "#side", "cullface": "south" },
                  "west":  { "uv": [0,8,16,16], "texture": "#side", "cullface": "west" },   "east": { "uv": [0,8,16,16], "texture": "#side" } } },
              { "from": [8,8,0], "to": [16,16,16], "faces": {
                  "up":    { "uv": [8,0,16,16], "texture": "#top", "cullface": "up" },
                  "north": { "uv": [0,0,8,8], "texture": "#side", "cullface": "north" },    "south": { "uv": [8,0,16,8], "texture": "#side", "cullface": "south" },
                  "west":  { "uv": [0,0,16,8], "texture": "#side" },                         "east":  { "uv": [0,0,16,8], "texture": "#side", "cullface": "east" } } } ] }
            """);
        jar.State("stairs", """
            { "variants": {
              "facing=east,half=bottom":  { "model": "block/stairs", "uvlock": true },
              "facing=south,half=bottom": { "model": "block/stairs", "uvlock": true, "y": 90 },
              "facing=west,half=bottom":  { "model": "block/stairs", "uvlock": true, "y": 180 },
              "facing=north,half=bottom": { "model": "block/stairs", "uvlock": true, "y": 270 },
              "facing=east,half=top":     { "model": "block/stairs", "uvlock": true, "x": 180 },
              "facing=south,half=top":    { "model": "block/stairs", "uvlock": true, "x": 180, "y": 90 },
              "facing=west,half=top":     { "model": "block/stairs", "uvlock": true, "x": 180, "y": 180 },
              "facing=north,half=top":    { "model": "block/stairs", "uvlock": true, "x": 180, "y": 270 } } }
            """);
        return jar;
    }

    [Test]
    public void Stairs_AreTwoBoxes_TurnedByTheirFacing_AndFlippedByTheirHalf()
    {
        var r = Build(StairsJar());
        Assert.That(r.Assets.Blocks["minecraft:stairs"].Shaped, Is.True);
        Assert.That(r.Assets.StateProps["minecraft:stairs"], Is.EqualTo(new[] { "facing", "half" }), "both decide the picture");
        string Boxes(string props) => Describe(ShapeResolver.Resolve(r.Assets, "minecraft:stairs", P(props))!);

        // The model as written: the high step on the east side (x 8..16)
        Assert.That(Boxes("facing=east,half=bottom"), Is.EqualTo("(0,0,0)-(16,8,16) (8,8,0)-(16,16,16)"));
        Assert.That(Boxes("facing=west,half=bottom"), Is.EqualTo("(0,0,0)-(16,8,16) (0,8,0)-(8,16,16)"));
        Assert.That(Boxes("facing=south,half=bottom"), Is.EqualTo("(0,0,0)-(16,8,16) (0,8,8)-(16,16,16)"));
        Assert.That(Boxes("facing=north,half=bottom"), Is.EqualTo("(0,0,0)-(16,8,16) (0,8,0)-(16,16,8)"));
        // Upside down: the slab is on top, the step hangs below it
        Assert.That(Boxes("facing=east,half=top"), Is.EqualTo("(0,8,0)-(16,16,16) (8,0,0)-(16,8,16)"));
        Assert.That(Boxes("facing=north,half=top"), Is.EqualTo("(0,8,0)-(16,16,16) (0,0,0)-(16,8,8)"));
        // A property the name does not carry is not asked: the first variant that fits is used
        Assert.That(Boxes("facing=west"), Is.EqualTo("(0,0,0)-(16,8,16) (0,8,0)-(8,16,16)"));
    }

    [Test]
    public void StairFaces_KeepTheirTilesAndTheirWindows_WithAndWithoutUvLock()
    {
        var r = Build(StairsJar());
        var east = ShapeResolver.Resolve(r.Assets, "minecraft:stairs", P("facing=east,half=bottom"))!;
        // The lower box (faces +X -X +Z -Z +Y -Y): sides show the lower half of the side tile; top and bottom are other tiles
        var low = east[0].Faces;
        Assert.That(low[0].Tile, Is.EqualTo(low[1].Tile));
        Assert.That(low[4].Tile, Is.Not.EqualTo(low[0].Tile), "the top is its own tile");
        Assert.That(low[5].Tile, Is.Not.EqualTo(low[4].Tile), "and so is the bottom");
        Assert.That((low[2].U0, low[2].V0, low[2].U1, low[2].V1), Is.EqualTo((0, 8, 16, 16)), "a side of the lower step is the lower half of the tile");
        Assert.That(low[0].Turns, Is.EqualTo(0));
        // The upper step's top is the window of the top tile it covers: x 8..16, all of z
        var up = east[1].Faces;
        Assert.That((up[4].U0, up[4].V0, up[4].U1, up[4].V1), Is.EqualTo((8, 0, 16, 16)));
        // Turned a quarter: with uvlock the window follows where the step IS (south side: z 8..16 -> v 8..16), the texture is not turned
        var south = ShapeResolver.Resolve(r.Assets, "minecraft:stairs", P("facing=south,half=bottom"))!;
        Assert.That((south[1].Faces[4].U0, south[1].Faces[4].V0, south[1].Faces[4].U1, south[1].Faces[4].V1), Is.EqualTo((0, 8, 16, 16)));
        Assert.That(south[1].Faces[4].Turns, Is.EqualTo(0));
    }

    [Test]
    public void WithoutUvLock_ATurnedModelTurnsItsTopTexture()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/t", 1, 2, 3);
        jar.Model("block/post", """{ "textures": { "t": "block/t" }, "elements": [ { "from": [4,0,4], "to": [12,16,12], "faces": { "up": { "uv": [4,4,12,12], "texture": "#t" }, "north": { "uv": [4,0,12,16], "texture": "#t" } } } ] }""");
        jar.State("post", """{ "variants": { "dir=a": { "model": "block/post" }, "dir=b": { "model": "block/post", "y": 90 }, "dir=c": { "model": "block/post", "y": 180 } } }""");
        var r = Build(jar);
        int Turns(string dir) => ShapeResolver.Resolve(r.Assets, "minecraft:post", P($"dir={dir}"))![0].Faces[4].Turns;
        Assert.That(Turns("a"), Is.EqualTo(0));
        Assert.That(Turns("b"), Is.EqualTo(1), "a quarter turn of the model turns its top texture a quarter clockwise");
        Assert.That(Turns("c"), Is.EqualTo(2));
        // The window is the model's own (it does not move with the block)
        var b = ShapeResolver.Resolve(r.Assets, "minecraft:post", P("dir=b"))![0].Faces[4];
        Assert.That((b.U0, b.V0, b.U1, b.V1), Is.EqualTo((4, 4, 12, 12)));
        // The side face that was the north is now the east one (turned clockwise a quarter: north -> east)
        var faces = ShapeResolver.Resolve(r.Assets, "minecraft:post", P("dir=b"))![0].Faces;
        Assert.That(faces[0].Tile, Is.GreaterThanOrEqualTo(0), "east has the north face's texture");
        Assert.That(faces[3].Tile, Is.EqualTo(-1), "and north has none");
    }

    [Test]
    public void AFence_IsAPostAndAnArmPerSideThatIsConnected()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/planks", 160, 120, 70);
        jar.Model("block/fence_post", """{ "textures": { "texture": "block/planks" }, "elements": [ { "from": [6,0,6], "to": [10,16,10], "faces": { "up": { "uv": [6,6,10,10], "texture": "#texture" }, "north": { "uv": [6,0,10,16], "texture": "#texture" } } } ] }""");
        jar.Model("block/fence_side", """
            { "textures": { "texture": "block/planks" }, "elements": [
              { "from": [7,12,0], "to": [9,15,9], "faces": { "north": { "uv": [7,1,9,4], "texture": "#texture" }, "up": { "uv": [7,0,9,9], "texture": "#texture" } } },
              { "from": [7,6,0],  "to": [9,9,9],  "faces": { "north": { "uv": [7,7,9,10], "texture": "#texture" }, "up": { "uv": [7,0,9,9], "texture": "#texture" } } } ] }
            """);
        jar.State("fence", """
            { "multipart": [
              { "apply": { "model": "block/fence_post" } },
              { "when": { "north": "true" }, "apply": { "model": "block/fence_side", "uvlock": true } },
              { "when": { "east": "true" },  "apply": { "model": "block/fence_side", "uvlock": true, "y": 90 } },
              { "when": { "south": "true" }, "apply": { "model": "block/fence_side", "uvlock": true, "y": 180 } },
              { "when": { "west": "true" },  "apply": { "model": "block/fence_side", "uvlock": true, "y": 270 } } ] }
            """);
        var r = Build(jar);
        Assert.That(r.Assets.StateProps["minecraft:fence"], Is.EqualTo(new[] { "north", "east", "south", "west" }));
        List<ResolvedBox> Boxes(string props) => ShapeResolver.Resolve(r.Assets, "minecraft:fence", P(props))!;

        Assert.That(Describe(Boxes("north=false,east=false,south=false,west=false")), Is.EqualTo("(6,0,6)-(10,16,10)"), "alone it is the post");
        Assert.That(Boxes("north=true,east=false,south=false,west=false"), Has.Count.EqualTo(3));
        Assert.That(Boxes("north=true,east=true,south=true,west=true"), Has.Count.EqualTo(9));
        // Each arm reaches its own edge of the cell
        Assert.That(Boxes("north=true,east=false,south=false,west=false").Skip(1).All(b => b.Z0 == 0 && b.X0 == 7 && b.X1 == 9), Is.True, "north: z 0..9 about the middle");
        Assert.That(Boxes("north=false,east=true,south=false,west=false").Skip(1).All(b => b.X1 == 16 && b.Z0 == 7 && b.Z1 == 9), Is.True, "east: x 7..16");
        Assert.That(Boxes("north=false,east=false,south=true,west=false").Skip(1).All(b => b.Z1 == 16), Is.True, "south: to z 16");
        Assert.That(Boxes("north=false,east=false,south=false,west=true").Skip(1).All(b => b.X0 == 0), Is.True, "west: from x 0");
        // The rails keep their heights
        Assert.That(Boxes("north=true").Skip(1).Select(b => (b.Y0, b.Y1)), Is.EquivalentTo(new[] { (12, 15), (6, 9) }));
    }

    [Test]
    public void TheDefaultWindows_AreTheGamesOwn_FromTheBoxesCoordinates()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/t", 1, 2, 3);
        // No windows in the model: every face takes its window from where the box is, as the game does (the box is x 4..12, y 3..9, z 2..10)
        jar.Model("block/b", """
            { "textures": { "t": "block/t" }, "elements": [ { "from": [4,3,2], "to": [12,9,10], "faces": {
              "down": { "texture": "#t" }, "up": { "texture": "#t" }, "north": { "texture": "#t" }, "south": { "texture": "#t" }, "west": { "texture": "#t" }, "east": { "texture": "#t" } } } ] }
            """);
        jar.State("b", """{ "variants": { "": { "model": "block/b" } } }""");
        var r = Build(jar);
        var f = ShapeResolver.Resolve(r.Assets, "minecraft:b", P(""))![0].Faces;
        (int, int, int, int) W(int i) => (f[i].U0, f[i].V0, f[i].U1, f[i].V1);
        Assert.That(W(0), Is.EqualTo((6, 7, 14, 13)), "east: u runs back along z (16 - to.z .. 16 - from.z), v down from the top (16 - to.y .. 16 - from.y)");
        Assert.That(W(1), Is.EqualTo((2, 7, 10, 13)), "west: z, y");
        Assert.That(W(2), Is.EqualTo((4, 7, 12, 13)), "south: x, y");
        Assert.That(W(3), Is.EqualTo((4, 7, 12, 13)), "north: 16 - to.x = 4 .. 16 - from.x = 12, y");
        Assert.That(W(4), Is.EqualTo((4, 2, 12, 10)), "up: x, z");
        Assert.That(W(5), Is.EqualTo((4, 6, 12, 14)), "down: x, 16 - to.z .. 16 - from.z");
    }

    [Test]
    public void ACondition_CanBeAnyOfSeveral_AllOfSeveral_OrAValueAmongOthers()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/t", 1, 2, 3);
        jar.Model("block/post", """{ "textures": { "t": "block/t" }, "elements": [ { "from": [6,0,6], "to": [10,16,10], "faces": { "up": { "texture": "#t" } } } ] }""");
        jar.Model("block/arm", """{ "textures": { "t": "block/t" }, "elements": [ { "from": [7,0,0], "to": [9,8,8], "faces": { "up": { "texture": "#t" } } } ] }""");
        jar.State("thing", """
            { "multipart": [
              { "apply": { "model": "block/post" } },
              { "when": { "OR": [ { "a": "true" }, { "b": "true" } ] }, "apply": { "model": "block/arm" } },
              { "when": { "AND": [ { "c": "true" }, { "d": "true" } ] }, "apply": { "model": "block/arm", "y": 90 } },
              { "when": { "side": "low|tall" }, "apply": { "model": "block/arm", "y": 180 } } ] }
            """);
        var r = Build(jar);
        Assert.That(r.Assets.StateProps["minecraft:thing"], Is.EquivalentTo(new[] { "a", "b", "c", "d", "side" }));
        int Count(string props) => ShapeResolver.Resolve(r.Assets, "minecraft:thing", P(props))!.Count;
        Assert.That(Count("a=false,b=false,c=false,d=false,side=none"), Is.EqualTo(1), "only the post");
        Assert.That(Count("a=true,b=false,c=false,d=false,side=none"), Is.EqualTo(2), "either of two");
        Assert.That(Count("a=false,b=true,c=false,d=false,side=none"), Is.EqualTo(2));
        Assert.That(Count("a=true,b=true,c=false,d=false,side=none"), Is.EqualTo(2), "both is still one arm");
        Assert.That(Count("a=false,b=false,c=true,d=false,side=none"), Is.EqualTo(1), "one of two that must both hold");
        Assert.That(Count("a=false,b=false,c=true,d=true,side=none"), Is.EqualTo(2));
        Assert.That(Count("a=false,b=false,c=false,d=false,side=low"), Is.EqualTo(2), "a value among several");
        Assert.That(Count("a=false,b=false,c=false,d=false,side=tall"), Is.EqualTo(2));
        Assert.That(Count("a=true,b=false,c=true,d=true,side=low"), Is.EqualTo(4));
    }

    [Test]
    public void ASlab_IsABottomHalf_ATopHalf_OrTheWholeCube()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/p", 160, 120, 70);
        jar.Model("block/slab", """{ "textures": { "bottom": "block/p", "top": "block/p", "side": "block/p" }, "elements": [ { "from": [0,0,0], "to": [16,8,16], "faces": { "down": { "texture": "#bottom" }, "up": { "texture": "#top" }, "north": { "texture": "#side" }, "south": { "texture": "#side" }, "west": { "texture": "#side" }, "east": { "texture": "#side" } } } ] }""");
        jar.Model("block/slab_top", """{ "textures": { "bottom": "block/p", "top": "block/p", "side": "block/p" }, "elements": [ { "from": [0,8,0], "to": [16,16,16], "faces": { "down": { "texture": "#bottom" }, "up": { "texture": "#top" }, "north": { "texture": "#side" }, "south": { "texture": "#side" }, "west": { "texture": "#side" }, "east": { "texture": "#side" } } } ] }""");
        jar.Model("block/p_cube", """{ "parent": "block/cube_all", "textures": { "all": "block/p" } }""");
        jar.State("slab", """{ "variants": { "type=bottom": { "model": "block/slab" }, "type=top": { "model": "block/slab_top" }, "type=double": { "model": "block/p_cube" } } }""");
        var r = Build(jar);
        Assert.That(r.Assets.StateProps["minecraft:slab"], Is.EqualTo(new[] { "type" }));
        string Boxes(string t) => Describe(ShapeResolver.Resolve(r.Assets, "minecraft:slab", P($"type={t}"))!);
        Assert.That(Boxes("bottom"), Is.EqualTo("(0,0,0)-(16,8,16)"));
        Assert.That(Boxes("top"), Is.EqualTo("(0,8,0)-(16,16,16)"));
        Assert.That(Boxes("double"), Is.EqualTo("(0,0,0)-(16,16,16)"));
        // With no windows in the model the game's defaults apply: a side of the bottom half shows the lower half of the tile (y 0..8 -> v 8..16)
        var side = ShapeResolver.Resolve(r.Assets, "minecraft:slab", P("type=bottom"))![0].Faces[2];
        Assert.That((side.U0, side.V0, side.U1, side.V1), Is.EqualTo((0, 8, 16, 16)));
        var topSide = ShapeResolver.Resolve(r.Assets, "minecraft:slab", P("type=top"))![0].Faces[2];
        Assert.That((topSide.U0, topSide.V0, topSide.U1, topSide.V1), Is.EqualTo((0, 0, 16, 8)), "and the top half the upper half");
    }

    [Test]
    public void ALayerThatFillsTheCell_StaysThin_ASmallOneIsAShape()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/c", 10, 20, 30);
        jar.Model("block/carpet", """{ "textures": { "wool": "block/c" }, "elements": [ { "from": [0,0,0], "to": [16,1,16], "faces": { "up": { "texture": "#wool" }, "north": { "texture": "#wool" } } } ] }""");
        jar.State("carpet", """{ "variants": { "": { "model": "block/carpet" } } }""");
        jar.Model("block/button", """{ "textures": { "texture": "block/c" }, "elements": [ { "from": [5,0,6], "to": [11,2,10], "faces": { "up": { "texture": "#texture" }, "north": { "texture": "#texture" } } } ] }""");
        jar.State("button", """{ "variants": { "face=floor": { "model": "block/button" }, "face=ceiling": { "model": "block/button", "x": 180 } } }""");
        var r = Build(jar);
        Assert.That(r.Assets.Blocks["minecraft:carpet"].Shaped, Is.False, "a carpet is a squashed cube");
        Assert.That(r.Assets.Blocks["minecraft:carpet"].Thickness, Is.EqualTo(1));
        Assert.That(r.Assets.Blocks["minecraft:button"].Shaped, Is.True, "a button is not the cell's whole footprint");
        Assert.That(Describe(ShapeResolver.Resolve(r.Assets, "minecraft:button", P("face=ceiling"))!), Is.EqualTo("(5,14,6)-(11,16,10)"));
    }

    [Test]
    public void ATrapdoor_ThatIsThinClosedAndTallOpen_IsOneShape()
    {
        var jar = new FakeJar().WithStandardParents();
        jar.Solid("block/tr", 10, 20, 30);
        jar.Model("block/trapdoor_bottom", """{ "textures": { "texture": "block/tr" }, "elements": [ { "from": [0,0,0], "to": [16,3,16], "faces": { "up": { "texture": "#texture" }, "north": { "texture": "#texture" } } } ] }""");
        jar.Model("block/trapdoor_open", """{ "textures": { "texture": "block/tr" }, "elements": [ { "from": [0,0,13], "to": [16,16,16], "faces": { "up": { "texture": "#texture" }, "north": { "texture": "#texture" } } } ] }""");
        jar.State("trapdoor", """{ "variants": { "half=bottom,open=false": { "model": "block/trapdoor_bottom" }, "half=bottom,open=true": { "model": "block/trapdoor_open" } } }""");
        var r = Build(jar);
        Assert.That(r.Assets.Blocks["minecraft:trapdoor"].Shaped, Is.True);
        Assert.That(r.Assets.StateProps["minecraft:trapdoor"], Is.EqualTo(new[] { "open" }), "half does not change the picture here");
        Assert.That(Describe(ShapeResolver.Resolve(r.Assets, "minecraft:trapdoor", P("open=false"))!), Is.EqualTo("(0,0,0)-(16,3,16)"));
        Assert.That(Describe(ShapeResolver.Resolve(r.Assets, "minecraft:trapdoor", P("open=true"))!), Is.EqualTo("(0,0,13)-(16,16,16)"));
    }

    [Test]
    public void TheTables_GiveEachStateOfAShapedBlockItsSlot_AndShareSlotsBetweenStatesThatLookAlike()
    {
        var r = Build(StairsJar());
        var names = new[] { "minecraft:air", "minecraft:stairs[facing=east,half=bottom]", "minecraft:stairs[facing=west,half=bottom]", "minecraft:stairs[facing=east,half=bottom]", "minecraft:stairs" };
        var (classes, materials, shapes) = VoxelBlockTables.BuildWithSeabedsAndShapes(names, r.Assets);
        Assert.That(classes[0], Is.EqualTo(BlockClass.None));
        foreach (int i in new[] { 1, 2, 3 })
        {
            Assert.That(classes[i] & BlockClass.Shaped, Is.Not.EqualTo(0), names[i]);
            Assert.That(classes[i] & (BlockClass.EmitOpaque | BlockClass.OccludeOpaque | BlockClass.OccludeWater), Is.EqualTo(0), "it draws no cube faces and hides nothing: " + names[i]);
        }
        int Slot(int i) => (classes[i] >> BlockClass.ShapeSlotShift) & BlockClass.ShapeSlotMask;
        Assert.That(Slot(1), Is.Not.EqualTo(Slot(2)), "facing east and west are different shapes");
        Assert.That(Slot(1), Is.EqualTo(Slot(3)), "the same state twice is one shape");
        Assert.That(shapes.Count, Is.EqualTo(2), "two shapes in the table");
        Assert.That(materials[1].Flags >> 22, Is.EqualTo((uint)Slot(1)), "the material points at the slot");
        Assert.That(shapes.Slot(Slot(1)), Has.Count.EqualTo(2));
        // The bare name (no properties: the default state's picture) is a shape too; a state with the properties of another stays what its properties say
        Assert.That(classes[4] & BlockClass.Shaped, Is.Not.EqualTo(0));
        // The shape's boxes are the resolver's
        var upper = shapes.Slot(Slot(1))[1];
        Assert.That((upper.X0, upper.Y0, upper.Z0, upper.X1, upper.Y1, upper.Z1), Is.EqualTo((8, 8, 0, 16, 16, 16)));
        Assert.That(upper.Faces[4].Tile, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void ANameThatIsNotShaped_IsUntouchedByTheShapeTable()
    {
        var r = Build(StairsJar().SimpleCube("stone", 100, 100, 100));
        var (classes, materials, shapes) = VoxelBlockTables.BuildWithSeabedsAndShapes(new[] { "minecraft:stone", "minecraft:nonexistent" }, r.Assets);
        Assert.That(classes[0], Is.EqualTo(BlockClass.Opaque));
        Assert.That(classes[1] & BlockClass.Shaped, Is.EqualTo(0));
        Assert.That(materials[0].Flags >> 22, Is.EqualTo(0u));
        Assert.That(shapes.Count, Is.EqualTo(0));
    }

    // ---- the game's own numbers ----

    private static BuildResult? _real;

    private static async Task<BuildResult> RealAsync()
    {
        if (_real == null)
        {
            using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync("1.21.5"));
            _real = new BlockAssetBuilder(jar, "1.21.5").Build();
        }
        return _real;
    }

    private static async Task<string> RealBoxes(string block, string props)
    {
        var r = await RealAsync();
        return Describe(ShapeResolver.Resolve(r.Assets, "minecraft:" + block, P(props)) ?? throw new AssertionException($"{block} has no shape rules"));
    }

    [Test]
    public async Task TheRealSlabsAndStairs_HaveTheGamesBoxes()
    {
        Assert.That(await RealBoxes("oak_slab", "type=bottom"), Is.EqualTo("(0,0,0)-(16,8,16)"));
        Assert.That(await RealBoxes("oak_slab", "type=top"), Is.EqualTo("(0,8,0)-(16,16,16)"));
        Assert.That(await RealBoxes("oak_slab", "type=double"), Is.EqualTo("(0,0,0)-(16,16,16)"));
        Assert.That(await RealBoxes("oak_stairs", "facing=east,half=bottom,shape=straight"), Is.EqualTo("(0,0,0)-(16,8,16) (8,8,0)-(16,16,16)"));
        Assert.That(await RealBoxes("oak_stairs", "facing=north,half=bottom,shape=straight"), Is.EqualTo("(0,0,0)-(16,8,16) (0,8,0)-(16,16,8)"));
        Assert.That(await RealBoxes("oak_stairs", "facing=east,half=top,shape=straight"), Is.EqualTo("(0,8,0)-(16,16,16) (8,0,0)-(16,8,16)"));
        // An inner corner has three boxes: the slab, the high step and the quarter beside it
        var r = await RealAsync();
        Assert.That(ShapeResolver.Resolve(r.Assets, "minecraft:oak_stairs", P("facing=east,half=bottom,shape=inner_left"))!, Has.Count.EqualTo(3));
        Assert.That(ShapeResolver.Resolve(r.Assets, "minecraft:oak_stairs", P("facing=east,half=bottom,shape=outer_left"))!, Has.Count.EqualTo(2));
        // A waterlogged or powered state is not a different shape: the server's names carry only the properties that matter
        Assert.That(r.Assets.StateProps["minecraft:oak_stairs"], Is.EquivalentTo(new[] { "facing", "half", "shape" }));
        Assert.That(r.Assets.StateProps["minecraft:oak_slab"], Is.EquivalentTo(new[] { "type" }));
    }

    [Test]
    public async Task TheRealFencesWallsAndPanes_AreAPostAndTheirArms()
    {
        var r = await RealAsync();
        Assert.That(await RealBoxes("oak_fence", "north=false,east=false,south=false,west=false"), Is.EqualTo("(6,0,6)-(10,16,10)"));
        Assert.That(ShapeResolver.Resolve(r.Assets, "minecraft:oak_fence", P("north=true,east=true,south=true,west=true"))!, Has.Count.EqualTo(9));
        var north = ShapeResolver.Resolve(r.Assets, "minecraft:oak_fence", P("north=true,east=false,south=false,west=false"))!;
        Assert.That(north.Skip(1).All(b => b.Z0 == 0 && b.X0 == 7 && b.X1 == 9), Is.True, "an arm toward the north");
        Assert.That(r.Assets.StateProps["minecraft:oak_fence"], Is.EquivalentTo(new[] { "north", "east", "south", "west" }));
        // A wall: a low side is lower than a tall one
        Assert.That(r.Assets.StateProps["minecraft:cobblestone_wall"], Is.EquivalentTo(new[] { "north", "east", "south", "west", "up" }));
        var low = ShapeResolver.Resolve(r.Assets, "minecraft:cobblestone_wall", P("north=low,east=none,south=none,west=none,up=true"))!;
        var tall = ShapeResolver.Resolve(r.Assets, "minecraft:cobblestone_wall", P("north=tall,east=none,south=none,west=none,up=true"))!;
        Assert.That(low.Max(b => b.Y1), Is.EqualTo(16), "the post is full height");
        Assert.That(tall.Count, Is.GreaterThan(1));
        Assert.That(tall.Where(b => b.Z0 == 0).Max(b => b.Y1), Is.GreaterThan(low.Where(b => b.Z0 == 0 && b.X1 - b.X0 < 16).Max(b => b.Y1)), "a tall wall side rises above a low one");
        // Panes and bars have up to 18 boxes and still fit the engine's 32
        Assert.That(ShapeResolver.Resolve(r.Assets, "minecraft:iron_bars", P("north=true,east=true,south=true,west=true"))!.Count, Is.InRange(5, 32));
        Assert.That(r.Assets.Shapes.ContainsKey("minecraft:glass_pane"), Is.True);
    }

    [Test]
    public async Task TheRealDoorsTrapdoorsLaddersAndFarmland_AreWhereTheyStand()
    {
        var r = await RealAsync();
        // Farmland is a sixteenth short of a full block
        Assert.That(await RealBoxes("farmland", "moisture=0"), Is.EqualTo("(0,0,0)-(16,15,16)"));
        // A closed bottom trapdoor is three sixteenths thick on the floor; open, it is a board against the wall it hangs from
        Assert.That(await RealBoxes("oak_trapdoor", "facing=north,half=bottom,open=false"), Is.EqualTo("(0,0,0)-(16,3,16)"));
        Assert.That(await RealBoxes("oak_trapdoor", "facing=north,half=top,open=false"), Is.EqualTo("(0,13,0)-(16,16,16)"));
        var open = ShapeResolver.Resolve(r.Assets, "minecraft:oak_trapdoor", P("facing=north,half=bottom,open=true"))!.Single();
        Assert.That(open.Y1 - open.Y0, Is.EqualTo(16));
        Assert.That(Math.Min(open.X1 - open.X0, open.Z1 - open.Z0), Is.EqualTo(3), "a board three sixteenths thick");
        // A closed door is a board three sixteenths thick the height of the block, on one edge of the cell
        var door = ShapeResolver.Resolve(r.Assets, "minecraft:oak_door", P("facing=east,half=lower,hinge=left,open=false"))!.Single();
        Assert.That(door.Y0, Is.EqualTo(0));
        Assert.That(door.Y1, Is.EqualTo(16));
        Assert.That(Math.Min(door.X1 - door.X0, door.Z1 - door.Z0), Is.EqualTo(3));
        // A ladder is a plane against its wall
        var ladder = ShapeResolver.Resolve(r.Assets, "minecraft:ladder", P("facing=north"))!.Single();
        Assert.That(ladder.Z0, Is.EqualTo(ladder.Z1), "a ladder has no thickness");
        Assert.That(ladder.Z0, Is.GreaterThanOrEqualTo(15));
        Assert.That(r.Assets.StateProps["minecraft:ladder"], Is.EqualTo(new[] { "facing" }));
    }

    [Test]
    public async Task EveryRealShapedBlock_ResolvesEveryOfItsStates_ToBoxesInsideTheCellWithTilesThatExist()
    {
        var r = await RealAsync();
        var a = r.Assets;
        Assert.That(a.Shapes.Count, Is.GreaterThan(350));
        int checkedStates = 0;
        foreach (var (name, logic) in a.Shapes)
        {
            Assert.That(a.Blocks[name].Shaped, Is.True, name);
            Assert.That(a.StateProps.ContainsKey(name), Is.True, name);
            var states = new List<Dictionary<string, string>>();
            if (logic.Variants.Count > 0) foreach (var v in logic.Variants) states.Add(P(v.Key));
            else states.Add(new Dictionary<string, string>());   // multipart: the state with no property true: just the always-on parts
            foreach (var props in states)
            {
                var boxes = ShapeResolver.Resolve(a, name, props)!;
                checkedStates++;
                Assert.That(boxes.Count, Is.LessThanOrEqualTo(ShapeTable.MaxBoxes), name);
                foreach (var b in boxes)
                {
                    Assert.That(new[] { b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1 }, Is.All.InRange(0, 16), name);
                    Assert.That(b.X1 >= b.X0 && b.Y1 >= b.Y0 && b.Z1 >= b.Z0, Is.True, name);
                    foreach (var f in b.Faces)
                    {
                        Assert.That(f.Tile, Is.InRange(-1, a.TileCount - 1), name);
                        Assert.That(f.Turns, Is.InRange(0, 3), name);
                        Assert.That(new[] { f.U0, f.V0, f.U1, f.V1 }, Is.All.InRange(0, 16), name);
                    }
                }
                // Where a block has a variant, that variant has something to draw (the rare empty one is a model without elements)
            }
        }
        Assert.That(checkedStates, Is.GreaterThan(1500));
    }

    [Test]
    public async Task TheShapeAssets_StayASmallPartOfTheDownload()
    {
        var r = await RealAsync();
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(r.Assets, BlockAssetsJson.Default.BlockAssets);
        TestContext.Progress.WriteLine($"[shapes] mc-blocks.json {json.Length / 1024} KB: {r.Assets.Shapes.Count} shaped blocks, {r.Assets.ShapeModels.Count} models, {r.Assets.ShapeModels.Sum(m => m.Elements.Count)} boxes");
        Assert.That(json.Length, Is.LessThan(4 * 1024 * 1024), "the block assets are fetched by every viewer");
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(json);
        Assert.That(ms.Length, Is.LessThan(512 * 1024), "gzipped, as the server sends it");
    }
}
