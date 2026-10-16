using AubsCraft.Live.Model.Editing;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Pasting a blueprint turned or mirrored: the blocks' own states turn with it, and positions land where the turned blueprint puts them.</summary>
public class BlockTransformTests
{
    [TestCase("minecraft:oak_stairs[facing=north,half=bottom]", 1, "minecraft:oak_stairs[facing=east,half=bottom]")]
    [TestCase("minecraft:oak_stairs[facing=north,half=bottom]", 2, "minecraft:oak_stairs[facing=south,half=bottom]")]
    [TestCase("minecraft:oak_stairs[facing=north,half=bottom]", 3, "minecraft:oak_stairs[facing=west,half=bottom]")]
    [TestCase("minecraft:oak_stairs[facing=north,half=bottom]", 4, "minecraft:oak_stairs[facing=north,half=bottom]")]
    [TestCase("minecraft:oak_stairs[facing=west,half=top]", -1, "minecraft:oak_stairs[facing=south,half=top]")]
    [TestCase("minecraft:furnace[facing=east,lit=true]", 1, "minecraft:furnace[facing=south,lit=true]")]
    [TestCase("minecraft:oak_log[axis=x]", 1, "minecraft:oak_log[axis=z]")]
    [TestCase("minecraft:oak_log[axis=z]", 3, "minecraft:oak_log[axis=x]")]
    [TestCase("minecraft:oak_log[axis=y]", 1, "minecraft:oak_log[axis=y]")]
    [TestCase("minecraft:oak_sign[rotation=0]", 1, "minecraft:oak_sign[rotation=4]")]
    [TestCase("minecraft:oak_sign[rotation=14]", 1, "minecraft:oak_sign[rotation=2]")]
    [TestCase("minecraft:stone", 1, "minecraft:stone")]
    [TestCase("minecraft:hopper[facing=down]", 1, "minecraft:hopper[facing=down]")]
    public void ATurnTurnsTheStatesOfABlock(string block, int turns, string expected) => Assert.That(BlockTransform.Transform(block, turns), Is.EqualTo(expected));

    [Test]
    public void TheArmsOfAFenceOrAWall_MoveToTheirNewSides()
    {
        // arms to the north and the west, a quarter turn: they point east and north
        Assert.That(BlockTransform.Transform("minecraft:oak_fence[east=false,north=true,south=false,west=true]", 1), Is.EqualTo("minecraft:oak_fence[east=true,north=true,south=false,west=false]"));
        Assert.That(BlockTransform.Transform("minecraft:cobblestone_wall[east=none,north=tall,south=none,up=true,west=low]", 1), Is.EqualTo("minecraft:cobblestone_wall[east=tall,north=low,south=none,up=true,west=none]"));
        // four quarter turns are no turn
        var fence = "minecraft:glass_pane[east=true,north=false,south=false,west=true]";
        Assert.That(BlockTransform.Transform(fence, 4), Is.EqualTo(fence));
        Assert.That(BlockTransform.Transform(BlockTransform.Transform(fence, 1), 3), Is.EqualTo(fence));
    }

    [Test]
    public void AMirrorSwapsTheSidesItNames()
    {
        Assert.That(BlockTransform.Transform("minecraft:furnace[facing=east]", 0, mirrorX: true), Is.EqualTo("minecraft:furnace[facing=west]"));
        Assert.That(BlockTransform.Transform("minecraft:furnace[facing=north]", 0, mirrorX: true), Is.EqualTo("minecraft:furnace[facing=north]"), "east-west leaves north alone");
        Assert.That(BlockTransform.Transform("minecraft:furnace[facing=north]", 0, mirrorZ: true), Is.EqualTo("minecraft:furnace[facing=south]"));
        Assert.That(BlockTransform.Transform("minecraft:oak_fence[east=true,north=false,south=false,west=false]", 0, mirrorX: true), Is.EqualTo("minecraft:oak_fence[east=false,north=false,south=false,west=true]"));
        Assert.That(BlockTransform.Transform("minecraft:oak_sign[rotation=4]", 0, mirrorX: true), Is.EqualTo("minecraft:oak_sign[rotation=12]"));
        Assert.That(BlockTransform.Transform("minecraft:oak_sign[rotation=0]", 0, mirrorZ: true), Is.EqualTo("minecraft:oak_sign[rotation=8]"));
    }

    [Test]
    public void AMirrorSwapsLeftAndRightHanded_StairsDoorsChests_ButTwoMirrorsDoNot()
    {
        Assert.That(BlockTransform.Transform("minecraft:oak_stairs[facing=east,half=bottom,shape=inner_left]", 0, mirrorX: true), Is.EqualTo("minecraft:oak_stairs[facing=west,half=bottom,shape=inner_right]"));
        Assert.That(BlockTransform.Transform("minecraft:oak_stairs[facing=east,shape=outer_right]", 0, mirrorZ: true), Is.EqualTo("minecraft:oak_stairs[facing=east,shape=outer_left]"));
        Assert.That(BlockTransform.Transform("minecraft:oak_door[facing=north,half=lower,hinge=left,open=false]", 0, mirrorX: true), Is.EqualTo("minecraft:oak_door[facing=north,half=lower,hinge=right,open=false]"));
        Assert.That(BlockTransform.Transform("minecraft:chest[facing=north,type=left]", 0, mirrorX: true), Is.EqualTo("minecraft:chest[facing=north,type=right]"));
        Assert.That(BlockTransform.Transform("minecraft:oak_stairs[facing=east,shape=inner_left]", 0, mirrorX: true, mirrorZ: true), Is.EqualTo("minecraft:oak_stairs[facing=west,shape=inner_left]"), "a mirror in each is a half turn: not handed");
        Assert.That(BlockTransform.Transform("minecraft:oak_slab[type=top]", 0, mirrorX: true), Is.EqualTo("minecraft:oak_slab[type=top]"), "a slab's type is not a side");
    }

    [Test]
    public void MirroringBothWays_IsAHalfTurn()
    {
        foreach (var block in new[] { "minecraft:furnace[facing=east]", "minecraft:oak_log[axis=x]", "minecraft:oak_sign[rotation=3]", "minecraft:oak_fence[east=true,north=false,south=true,west=false]", "minecraft:oak_stairs[facing=north,shape=outer_right]" })
            Assert.That(BlockTransform.Transform(block, 0, true, true), Is.EqualTo(BlockTransform.Transform(block, 2)), block);
    }

    [Test]
    public void ABlockWithNoStates_IsReturnedAsItIs()
    {
        Assert.That(BlockTransform.Transform("minecraft:stone", 3, true, true), Is.EqualTo("minecraft:stone"));
        Assert.That(BlockTransform.Transform("minecraft:oak_stairs[facing=north]", 0), Is.EqualTo("minecraft:oak_stairs[facing=north]"));
    }

    // ---- positions ----

    [Test]
    public void ATurnMovesEveryPlaceIntoTheTurnedBox_NoTwoToTheSame()
    {
        const int w = 3, d = 5;
        for (int turns = 0; turns < 4; turns++)
            foreach (bool mx in new[] { false, true })
                foreach (bool mz in new[] { false, true })
                {
                    var seen = new HashSet<(int, int)>();
                    int nw = 0, nd = 0;
                    for (int x = 0; x < w; x++)
                        for (int z = 0; z < d; z++)
                        {
                            var p = BlockTransform.Place(x, z, w, d, turns, mx, mz);
                            (nw, nd) = (p.Width, p.Depth);
                            Assert.That(p.X, Is.InRange(0, p.Width - 1));
                            Assert.That(p.Z, Is.InRange(0, p.Depth - 1));
                            Assert.That(seen.Add((p.X, p.Z)), Is.True, $"turns {turns} mirror {mx}/{mz}: two places to {p.X},{p.Z}");
                        }
                    Assert.That((nw, nd), Is.EqualTo(turns % 2 == 0 ? (w, d) : (d, w)));
                    Assert.That(seen.Count, Is.EqualTo(w * d));
                }
    }

    [Test]
    public void AQuarterTurnIsClockwiseFromAbove()
    {
        // The north-west corner (0, 0) of a 4 x 2 box goes to the north-east corner of the turned (2 x 4) box
        var p = BlockTransform.Place(0, 0, 4, 2, 1);
        Assert.That((p.X, p.Z, p.Width, p.Depth), Is.EqualTo((1, 0, 2, 4)));
        // and the north-east corner (3, 0) to the south-east corner
        var q = BlockTransform.Place(3, 0, 4, 2, 1);
        Assert.That((q.X, q.Z), Is.EqualTo((1, 3)));
        // a stair on the north side facing north is on the east side facing east
        Assert.That(BlockTransform.Transform("minecraft:oak_stairs[facing=north]", 1), Is.EqualTo("minecraft:oak_stairs[facing=east]"));
    }

    [Test]
    public void FourTurnsAndTwoMirrors_AreNothing()
    {
        var p = BlockTransform.Place(2, 3, 5, 7, 4);
        Assert.That((p.X, p.Z, p.Width, p.Depth), Is.EqualTo((2, 3, 5, 7)));
        var m = BlockTransform.Place(BlockTransform.Place(2, 3, 5, 7, 0, true, false).X, 3, 5, 7, 0, true, false);
        Assert.That(m.X, Is.EqualTo(2));
        Assert.That(BlockTransform.Place(1, 1, 4, 4, -1).X, Is.EqualTo(BlockTransform.Place(1, 1, 4, 4, 3).X));
    }
}
