using System.Numerics;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.BlockEntities;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The blocks the game draws with models: which blocks, how they face, how they travel in a header, how they are read from a chunk, and where their boxes land.</summary>
public class BlockEntityTests
{
    private static Dictionary<string, object> Props(params (string, string)[] values)
    {
        var d = new Dictionary<string, object>();
        foreach (var (k, v) in values) d[k] = v;
        return d;
    }

    [Test]
    public void TheKindsAndTheirFacings_AreReadFromTheBlocksProperties()
    {
        var chest = BlockEntityKinds.Classify("minecraft:chest", Props(("facing", "north"), ("type", "left")))!.Value;
        Assert.That((chest.Kind, chest.Orient, chest.Variant), Is.EqualTo((BlockEntityKind.Chest, (byte)8, (byte)1)));
        var ender = BlockEntityKinds.Classify("minecraft:ender_chest", Props(("facing", "east")))!.Value;
        Assert.That((ender.Kind, ender.Orient), Is.EqualTo((BlockEntityKind.EnderChest, (byte)12)));
        var bed = BlockEntityKinds.Classify("minecraft:red_bed", Props(("facing", "west"), ("part", "head")))!.Value;
        Assert.That((bed.Kind, bed.Orient, bed.Variant, bed.Part), Is.EqualTo((BlockEntityKind.Bed, (byte)4, (byte)14, (byte)1)), "red is the 15th dye; the head part is 1");
        var shulker = BlockEntityKinds.Classify("minecraft:purple_shulker_box", Props(("facing", "up")))!.Value;
        Assert.That((shulker.Kind, shulker.Orient, shulker.Variant), Is.EqualTo((BlockEntityKind.ShulkerBox, BlockEntityKinds.FacingUp, (byte)10)));
        Assert.That(BlockEntityKinds.Classify("minecraft:shulker_box", Props(("facing", "down")))!.Value.Variant, Is.EqualTo(BlockEntityKinds.Undyed));
        var sign = BlockEntityKinds.Classify("minecraft:spruce_sign", Props(("rotation", "6")))!.Value;
        Assert.That((sign.Kind, sign.Orient, sign.Variant, sign.Part), Is.EqualTo((BlockEntityKind.Sign, (byte)6, (byte)1, (byte)0)));
        var wallSign = BlockEntityKinds.Classify("minecraft:oak_wall_sign", Props(("facing", "south")))!.Value;
        Assert.That((wallSign.Kind, wallSign.Orient, wallSign.Part), Is.EqualTo((BlockEntityKind.Sign, (byte)0, (byte)1)));
        var hanging = BlockEntityKinds.Classify("minecraft:cherry_hanging_sign", Props(("rotation", "3")))!.Value;
        Assert.That((hanging.Kind, hanging.Variant), Is.EqualTo((BlockEntityKind.HangingSign, (byte)7)));
        var banner = BlockEntityKinds.Classify("minecraft:white_banner", Props(("rotation", "12")))!.Value;
        Assert.That((banner.Kind, banner.Orient, banner.Variant, banner.Part), Is.EqualTo((BlockEntityKind.Banner, (byte)12, (byte)0, (byte)0)));
        var wallBanner = BlockEntityKinds.Classify("minecraft:blue_wall_banner", Props(("facing", "east")))!.Value;
        Assert.That((wallBanner.Orient, wallBanner.Variant, wallBanner.Part), Is.EqualTo(((byte)12, (byte)11, (byte)1)));
        var skull = BlockEntityKinds.Classify("minecraft:wither_skeleton_skull", Props(("rotation", "9")))!.Value;
        Assert.That((skull.Kind, skull.Orient, skull.Variant, skull.Part), Is.EqualTo((BlockEntityKind.Skull, (byte)9, (byte)1, (byte)0)));
        Assert.That(BlockEntityKinds.Classify("minecraft:zombie_wall_head", Props(("facing", "north")))!.Value.Part, Is.EqualTo((byte)1));
    }

    [TestCase("minecraft:stone")]
    [TestCase("minecraft:oak_planks")]
    [TestCase("minecraft:bed")]
    [TestCase("minecraft:rainbow_bed")]
    [TestCase("minecraft:oak_wall_signs")]
    [TestCase("mymod:chest")]
    [TestCase("minecraft:sign")]
    [TestCase("minecraft:sculk_shrieker")]
    [TestCase("minecraft:barrel")]
    public void OtherBlocks_AreNotBlockEntitiesTheViewerDraws(string name) => Assert.That(BlockEntityKinds.Classify(name, null), Is.Null);

    [Test]
    public void BlocksWithModels_ArePickedOutByNameAlone()
    {
        foreach (var name in new[] { "minecraft:chest", "minecraft:trapped_chest", "minecraft:ender_chest", "minecraft:lime_bed", "minecraft:shulker_box", "minecraft:pink_shulker_box", "minecraft:acacia_sign",
                     "minecraft:acacia_wall_sign", "minecraft:crimson_hanging_sign", "minecraft:black_banner", "minecraft:red_wall_banner", "minecraft:creeper_head", "minecraft:piglin_wall_head" })
            Assert.That(BlockEntityKinds.IsBlockEntityBlock(name), Is.True, name);
        Assert.That(BlockEntityKinds.IsBlockEntityBlock("minecraft:cobblestone"), Is.False);
    }

    [Test]
    public void ASpotSurvivesTheHeader_AndADamagedHeaderIsNothing()
    {
        var spots = new List<BlockEntitySpot>
        {
            new(0, -64, 0, BlockEntityKind.Chest, 8, 1, 0), new(15, 319, 15, BlockEntityKind.Banner, 15, 14, 1), new(7, 62, 3, BlockEntityKind.ShulkerBox, BlockEntityKinds.FacingDown, 16, 0),
            new(1, 0, 14, BlockEntityKind.Bed, 4, 3, 1),
        };
        Assert.That(BlockEntityCodec.Decode(BlockEntityCodec.Encode(spots)), Is.EqualTo(spots));
        Assert.That(BlockEntityCodec.Decode(null), Is.Empty);
        Assert.That(BlockEntityCodec.Decode("not base64!!"), Is.Empty);
        Assert.That(BlockEntityCodec.Decode(Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 })), Is.Empty, "not a whole number of spots");
        Assert.That(BlockEntityCodec.Decode(Convert.ToBase64String(new byte[] { 0x12, 10, 0, 99, 0, 0, 0 })), Is.Empty, "an unknown kind is dropped");
        var storeroom = Enumerable.Range(0, 900).Select(i => new BlockEntitySpot(i & 15, i % 300, (i >> 4) & 15, BlockEntityKind.Chest, 0, 0, 0)).ToList();
        Assert.That(BlockEntityCodec.Decode(BlockEntityCodec.Encode(storeroom)).Count, Is.EqualTo(BlockEntityCodec.MaxPerColumn));
    }

    // ---- read from a chunk's own NBT ----

    private static NbtCompound Entry(string name, params (string, string)[] props)
    {
        var e = new NbtCompound { ["Name"] = name };
        if (props.Length > 0) { var p = new NbtCompound(); foreach (var (k, v) in props) p[k] = v; e["Properties"] = p; }
        return e;
    }

    private static NbtCompound SectionOf(sbyte y, Dictionary<int, int> cells, params NbtCompound[] palette)
    {
        var list = new NbtList(NbtTagType.Compound, palette.Length);
        foreach (var e in palette) list.Add(e);
        var states = new NbtCompound { ["palette"] = list };
        var data = new long[256];
        foreach (var (index, p) in cells) data[index / 16] |= (long)p << ((index % 16) * 4);
        states["data"] = data;
        return new NbtCompound { ["Y"] = (byte)y, ["block_states"] = states };
    }

    [Test]
    public void AChunk_GivesItsBlockEntities_WherePlaced_AndHowTheyFace()
    {
        // Section 0 (blocks y 0..15): a chest facing east at (3, 5, 7) and a red bed's head facing north at (10, 0, 2); air elsewhere
        int At(int x, int y, int z) => x + z * 16 + y * 256;
        var section = SectionOf(0, new Dictionary<int, int> { [At(3, 5, 7)] = 1, [At(10, 0, 2)] = 2 },
            Entry("minecraft:air"), Entry("minecraft:chest", ("facing", "east"), ("type", "single")), Entry("minecraft:red_bed", ("facing", "north"), ("part", "head")));
        var list = new NbtList(NbtTagType.Compound, 1); list.Add(section);
        var chunk = RegionReader.ParseChunkNbt(new NbtCompound { ["sections"] = list });
        Assert.That(chunk.BlockEntities, Is.Not.Null);
        Assert.That(chunk.BlockEntities, Is.EquivalentTo(new[]
        {
            new BlockEntitySpot(3, 5, 7, BlockEntityKind.Chest, 12, 0, 0),
            new BlockEntitySpot(10, 0, 2, BlockEntityKind.Bed, 8, 14, 1),
        }), "section 0 is the world's y 0..15");
    }

    [Test]
    public void AChunkWithoutThem_HasNone()
    {
        var section = SectionOf(0, new Dictionary<int, int> { [5] = 1 }, Entry("minecraft:air"), Entry("minecraft:stone"));
        var list = new NbtList(NbtTagType.Compound, 1); list.Add(section);
        Assert.That(RegionReader.ParseChunkNbt(new NbtCompound { ["sections"] = list }).BlockEntities, Is.Null);
    }

    // ---- where the boxes land ----

    private static List<PartDraw> Posed(BlockEntitySpot spot, float x = 100, float y = 64, float z = 200)
    {
        var list = new List<PartDraw>();
        BlockEntityModels.Pose(spot, x, y, z, list);
        return list;
    }

    private static Vector3 Centre(PartDraw d) => Vector3.Transform(d.Origin + d.Size / 2f, d.Model);

    [TestCase(0, 0, 1)]     // south: +z
    [TestCase(4, -1, 0)]    // west: -x
    [TestCase(8, 0, -1)]    // north: -z
    [TestCase(12, 1, 0)]    // east: +x
    public void AChestsLatch_IsOnTheSideItFaces(int orient, int dx, int dz)
    {
        var draws = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Chest, (byte)orient, 0, 0));
        Assert.That(draws.Count, Is.EqualTo(3), "bottom, lid, lock");
        var middle = new Vector3(100.5f, 64f, 200.5f);
        var latch = draws[2];
        Assert.That(latch.Size, Is.EqualTo(new Vector3(2, 4, 1)));
        var d = Centre(latch) - middle;
        Assert.That(d.X * dx + d.Z * dz, Is.GreaterThan(0.3f), "the latch sits toward where the chest faces");
        // The whole chest stays inside its block
        foreach (var p in draws)
        {
            var c = Centre(p);
            Assert.That(c.X, Is.InRange(100f, 101f)); Assert.That(c.Z, Is.InRange(200f, 201f)); Assert.That(c.Y, Is.InRange(64f, 65f));
        }
    }

    [Test]
    public void ADoubleChest_HalvesMeetInTheMiddle()
    {
        // Facing north, the left half is on the east side and the right half on the west
        var left = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Chest, 8, 1, 0), 101, 64, 200);
        var right = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Chest, 8, 2, 0), 100, 64, 200);
        Assert.That(left.All(d => d.Texture == "chest/normal_left"), Is.True);
        Assert.That(right.All(d => d.Texture == "chest/normal_right"), Is.True);
        float leftX = Centre(left[0]).X, rightX = Centre(right[0]).X;
        Assert.That(leftX, Is.GreaterThan(rightX), "facing north the left half is to the east");
    }

    [Test]
    public void ABedLiesFlat_AtTheHeightOfAMattress()
    {
        foreach (int orient in new[] { 0, 4, 8, 12 })
            foreach (byte part in new byte[] { 0, 1 })
            {
                var draws = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Bed, (byte)orient, 14, part));
                var main = draws.First(d => d.Size == new Vector3(16, 16, 6));
                var c = Centre(main);
                Assert.That(c.Y - 64f, Is.InRange(0.2f, 0.55f), $"the mattress is a few pixels up (orient {orient}, part {part})");
                Assert.That(draws.All(d => d.Texture == "bed/red"), Is.True);
            }
    }

    [Test]
    public void ASignStandsOnItsStick_AWallSignHangsOnTheWall_AndABannerHasItsColour()
    {
        var standing = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Sign, 0, 0, 0));
        Assert.That(standing.Count, Is.EqualTo(2), "the board and the stick");
        Assert.That(Centre(standing[0]).Y, Is.GreaterThan(Centre(standing[1]).Y), "the board is above the stick");
        var wall = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Sign, 0, 0, 1));   // facing south: the wall is to the north
        Assert.That(wall.Count, Is.EqualTo(1));
        Assert.That(Centre(wall[0]).Z, Is.LessThan(200.5f), "against the north side of the block");
        var banner = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Banner, 0, 14, 0));
        Assert.That(banner.Any(d => d.Tint != Vector3.One), Is.True, "the flag takes the dye");
        Assert.That(banner.Select(d => d.Texture).Distinct(), Is.EqualTo(new[] { "banner_base" }));
    }

    [Test]
    public void AHangingSign_HangsBelowItsAttachment_InTheThreeWaysTheGameHasThem()
    {
        var chains = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.HangingSign, 0, 0, 0));
        var wall = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.HangingSign, 0, 0, 1));
        var middle = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.HangingSign, 0, 0, 2));
        Assert.That(chains.Count, Is.GreaterThan(1)); Assert.That(wall.Count, Is.GreaterThan(1)); Assert.That(middle.Count, Is.GreaterThan(1));
        Assert.That(chains.Select(d => d.Texture).Distinct(), Is.EqualTo(new[] { "signs/hanging/oak" }));
        // Whatever the way, everything stays within a block and a bit above it (the chains reach up to the block over it)
        foreach (var d in chains.Concat(wall).Concat(middle))
            Assert.That(Centre(d).Y, Is.InRange(63f, 65.2f));
        var spruce = BlockEntityKinds.Classify("minecraft:spruce_hanging_sign", Props(("rotation", "0"), ("attached", "true")))!.Value;
        Assert.That(spruce.Part, Is.EqualTo((byte)2));
    }

    [Test]
    public void AHeadOnTheGroundStandsOnItsFoot_AWallHeadHangsOnTheWallBehindIt()
    {
        var floor = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Skull, 0, 0, 0));
        Assert.That(floor.Count, Is.EqualTo(1));
        var c = Centre(floor[0]);
        Assert.That(c.X, Is.EqualTo(100.5f).Within(0.02f)); Assert.That(c.Z, Is.EqualTo(200.5f).Within(0.02f));
        Assert.That(c.Y - 64f, Is.InRange(0.1f, 0.35f), "an eight pixel head from the floor of its block");
        // A head facing north (the wall is to the south of it) is moved a quarter of a block toward the south
        var wall = Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Skull, 8, 0, 1));
        Assert.That(Centre(wall[0]).Z - 200.5f, Is.EqualTo(0.25f).Within(0.02f));
        // The creeper, the zombie and the player have heads; the dragon's and the piglin's are not drawn
        Assert.That(Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Skull, 0, 3, 0)).Count, Is.EqualTo(1));
        Assert.That(Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Skull, 0, 2, 0)).Select(d => d.Texture).Distinct(), Is.EqualTo(new[] { "zombie/zombie" }));
        Assert.That(Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Skull, 0, 5, 0)), Is.Empty);
        Assert.That(Posed(new BlockEntitySpot(0, 0, 0, BlockEntityKind.Skull, 0, 4, 0)), Is.Empty);
    }

    [Test]
    public void EveryTexture_TheModelsWear_IsNamedForTheAssetTool()
    {
        var names = EntityModels.TextureNames().ToList();
        foreach (var t in new[] { "chest/normal", "chest/ender", "bed/white", "bed/black", "shulker/shulker", "shulker/shulker_red", "signs/oak", "signs/warped", "banner_base" })
            Assert.That(names, Does.Contain(t), t);
    }
}
