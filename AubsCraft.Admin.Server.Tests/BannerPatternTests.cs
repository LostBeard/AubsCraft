using System.Numerics;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.BlockEntities;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The patterns on banners: read from the chunk's block entities, carried in a header, laid over the base colour in order.</summary>
public class BannerPatternTests
{
    private static NbtCompound Banner(int x, int y, int z, params (object pattern, string color)[] layers)
    {
        var list = new NbtList(NbtTagType.Compound, layers.Length);
        foreach (var (pattern, color) in layers) list.Add(new NbtCompound { ["pattern"] = pattern, ["color"] = color });
        return new NbtCompound { ["id"] = "minecraft:banner", ["x"] = x, ["y"] = y, ["z"] = z, ["patterns"] = list };
    }

    private static NbtCompound Chunk(params NbtCompound[] entities)
    {
        var list = new NbtList(NbtTagType.Compound, entities.Length);
        foreach (var e in entities) list.Add(e);
        return new NbtCompound { ["block_entities"] = list };
    }

    [Test]
    public void TheLayersOfABanner_AreReadInOrder_WithTheirDyes_ByNameOrByDefinition()
    {
        var chunk = Chunk(
            Banner(35, 70, -13, ("minecraft:creeper", "white"), ("minecraft:border", "blue"), (new NbtCompound { ["asset_id"] = "minecraft:circle" }, "red")),
            Banner(1, 2, 3, ("minecraft:not_a_pattern", "white")),
            new NbtCompound { ["id"] = "minecraft:chest", ["x"] = 0, ["y"] = 0, ["z"] = 0 },
            new NbtCompound { ["id"] = "minecraft:banner", ["x"] = 4, ["y"] = 5, ["z"] = 6 });
        var banners = RegionReader.BannerLayersOf(chunk)!;
        Assert.That(banners.Count, Is.EqualTo(1), "one with patterns the viewer knows");
        Assert.That((banners[0].X, banners[0].Y, banners[0].Z), Is.EqualTo((3, 70, 3)));
        Assert.That(banners[0].Layers.Select(l => (BannerPatterns.Names[l.Pattern], BlockEntityKinds.Dyes[l.Color])),
            Is.EqualTo(new[] { ("creeper", "white"), ("border", "blue"), ("circle", "red") }));
        Assert.That(RegionReader.BannerLayersOf(new NbtCompound()), Is.Null);
    }

    [Test]
    public void TheHeaderCarriesThem()
    {
        var banners = new List<BannerLayers> { new(5, -60, 9, [(4, 0), (1, 11)]), new(15, 300, 0, [(30, 14)]) };
        var back = BannerLayersCodec.Decode(BannerLayersCodec.Encode(banners));
        Assert.That(back.Count, Is.EqualTo(2));
        Assert.That((back[0].X, back[0].Y, back[0].Z), Is.EqualTo((5, -60, 9)));
        Assert.That(back[0].Layers, Is.EqualTo(new[] { ((byte)4, (byte)0), ((byte)1, (byte)11) }));
        Assert.That(back[1].Y, Is.EqualTo(300));
        Assert.That(BannerLayersCodec.Decode(null), Is.Empty);
        Assert.That(BannerLayersCodec.Decode("!!"), Is.Empty);
        var bytes = Convert.FromBase64String(BannerLayersCodec.Encode(banners));
        Assert.That(BannerLayersCodec.Decode(Convert.ToBase64String(bytes[..^3])).Count, Is.EqualTo(1), "cut short: what came before it");
    }

    [Test]
    public void ThePatternNames_AreTheGamesFortyThree_ByNumber()
    {
        Assert.That(BannerPatterns.Names.Length, Is.EqualTo(43));
        Assert.That(BannerPatterns.Names.Distinct().Count(), Is.EqualTo(43));
        Assert.That(BannerPatterns.IndexOf("minecraft:stripe_top"), Is.EqualTo(Array.IndexOf(BannerPatterns.Names, "stripe_top")));
        Assert.That(BannerPatterns.IndexOf("stripe_top"), Is.GreaterThanOrEqualTo(0));
        Assert.That(BannerPatterns.IndexOf("mymod:thing"), Is.EqualTo(-1));
        Assert.That(EntityModels.TextureNames(), Does.Contain("banner/creeper"));
    }

    [Test]
    public void EachLayer_IsTheFlagAgain_InItsPatternAndDye_ALittleThickerThanTheOneBefore()
    {
        var spot = new BlockEntitySpot(0, 0, 0, BlockEntityKind.Banner, 0, 0, 0);
        var plain = new List<PartDraw>();
        BlockEntityModels.Pose(spot, 10, 64, 10, plain);
        var layered = new List<PartDraw>();
        BlockEntityModels.PoseBannerLayers(spot, 10, 64, 10, [((byte)4, (byte)0), ((byte)1, (byte)11), ((byte)3, (byte)14)], layered);
        Assert.That(layered.Count % 3, Is.EqualTo(0), "the flag's boxes, three times");
        int per = layered.Count / 3;
        Assert.That(layered.Take(per).All(d => d.Texture == "banner/creeper"));
        Assert.That(layered.Skip(per).Take(per).All(d => d.Texture == "banner/border"));
        Assert.That(layered.Skip(2 * per).All(d => d.Texture == "banner/circle"));
        Assert.That(layered[per].Inflate, Is.GreaterThan(layered[0].Inflate));
        Assert.That(layered[2 * per].Inflate, Is.GreaterThan(layered[per].Inflate));
        Assert.That(layered[per].Tint, Is.EqualTo(EntityPoser.DyeColour("blue")));
        // On the flag of the banner, not somewhere else: the same place as the plain banner's flag boxes
        var flag = plain.Where(d => d.Texture == "banner_base" && d.Tint != Vector3.One).ToList();
        Assert.That(flag.Count, Is.EqualTo(per));
        for (int i = 0; i < per; i++) Assert.That(layered[i].Model, Is.EqualTo(flag[i].Model));
    }
}
