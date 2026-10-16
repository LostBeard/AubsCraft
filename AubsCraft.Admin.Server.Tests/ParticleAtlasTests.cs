using AubsCraft.Assets;
using AubsCraft.Assets.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The particle atlas: animations are strips, frame after frame; every sprite is where the table says, intact, none over another; and the game's real smoke and flames are there.</summary>
public class ParticleAtlasTests
{
    private static ParticleAtlasResult Build(FakeJar jar) { using var j = jar.Build(); return new ParticleAtlasBuilder(j, "test").Build(); }

    private static (byte r, byte g, byte b, byte a) At(ParticleAtlasResult r, int x, int y)
    {
        int i = (y * r.Assets.AtlasWidth + x) * 4;
        return (r.Rgba[i], r.Rgba[i + 1], r.Rgba[i + 2], r.Rgba[i + 3]);
    }

    [Test]
    public void FramesOfOneAnimation_BecomeOneStrip_InOrder_AndALoneSpriteStaysAlone()
    {
        var jar = new FakeJar();
        for (int f = 0; f < 3; f++)
        {
            int frame = f;
            jar.Texture($"particle/puff_{f}", 8, 8, (x, y) => ((byte)(frame * 50 + 10), (byte)x, (byte)y, 255));
        }
        jar.Texture("particle/flame", 8, 8, (x, y) => (200, (byte)(x * 10), (byte)(y * 10), 255));
        var r = Build(jar);
        Assert.That(r.Assets.Sprites.Keys, Is.EquivalentTo(new[] { "puff", "flame" }));
        var puff = r.Assets.Sprites["puff"];
        Assert.That(puff[2], Is.EqualTo(8)); Assert.That(puff[3], Is.EqualTo(8)); Assert.That(puff[4], Is.EqualTo(3), "three frames");
        for (int f = 0; f < 3; f++)
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    Assert.That(At(r, puff[0] + f * 8 + x, puff[1] + y), Is.EqualTo(((byte)(f * 50 + 10), (byte)x, (byte)y, (byte)255)), $"puff frame {f} ({x},{y})");
        var flame = r.Assets.Sprites["flame"];
        Assert.That(flame[4], Is.EqualTo(1));
        Assert.That(At(r, flame[0] + 3, flame[1] + 5), Is.EqualTo(((byte)200, (byte)30, (byte)50, (byte)255)));
    }

    [Test]
    public void ASequenceWithAGapOrMixedSizes_IsNotAStrip_ButIndividualSprites()
    {
        var jar = new FakeJar();
        jar.Solid("particle/gap_0", 1, 2, 3, 255, 8).Solid("particle/gap_2", 1, 2, 3, 255, 8);          // no frame 1
        jar.Solid("particle/mixed_0", 1, 2, 3, 255, 8).Solid("particle/mixed_1", 1, 2, 3, 255, 16);   // two sizes
        var r = Build(jar);
        Assert.That(r.Assets.Sprites.Keys, Is.EquivalentTo(new[] { "gap_0", "gap_2", "mixed_0", "mixed_1" }));
        Assert.That(r.Assets.Sprites.Values.All(s => s[4] == 1), Is.True);
    }

    [Test]
    public void NoSpriteOverlapsAnother_AndEverythingFitsInsideTheAtlas()
    {
        var jar = new FakeJar();
        for (int i = 0; i < 40; i++) jar.Solid($"particle/s{i}", (byte)i, (byte)(i * 5), (byte)(i * 11), 255, 4 + (i % 4) * 4);
        for (int f = 0; f < 12; f++) jar.Solid($"particle/anim_{f}", 9, 9, (byte)f, 255, 16);
        var r = Build(jar);
        var rects = r.Assets.Sprites.Values.Select(s => (x: s[0], y: s[1], w: s[2] * s[4], h: s[3])).ToList();
        foreach (var s in rects)
        {
            Assert.That(s.x + s.w, Is.LessThanOrEqualTo(r.Assets.AtlasWidth));
            Assert.That(s.y + s.h, Is.LessThanOrEqualTo(r.Assets.AtlasHeight));
        }
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i]; var b = rects[j];
                bool apart = a.x + a.w <= b.x || b.x + b.w <= a.x || a.y + a.h <= b.y || b.y + b.h <= a.y;
                Assert.That(apart, Is.True, $"sprite {i} overlaps {j}");
            }
    }

    [Test]
    public void TheAtlas_IsTheSameEveryTime()
    {
        FakeJar Make() { var jar = new FakeJar(); for (int i = 0; i < 20; i++) jar.Solid($"particle/s{i}", (byte)i, 9, 9, 255, 4 + (i % 3) * 4); return jar; }
        var a = Build(Make()); var b = Build(Make());
        Assert.That(b.Rgba, Is.EqualTo(a.Rgba));
        Assert.That(b.Assets.Sprites.Select(s => (s.Key, string.Join(",", s.Value))), Is.EqualTo(a.Assets.Sprites.Select(s => (s.Key, string.Join(",", s.Value)))));
    }

    [Test]
    public async Task TheGamesOwnSmokeAndFlames_ArePacked()
    {
        using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync("1.21.5"));
        var r = new ParticleAtlasBuilder(jar, "1.21.5").Build();
        TestContext.Out.WriteLine(r.Report);
        var smoke = r.Assets.Sprites["big_smoke"];
        Assert.That(smoke[4], Is.EqualTo(12), "big smoke has twelve frames");
        Assert.That(smoke[2], Is.EqualTo(16));
        Assert.That(r.Assets.Sprites["flame"][4], Is.EqualTo(1));
        Assert.That(r.Assets.Sprites["soul_fire_flame"][4], Is.EqualTo(1));
        Assert.That(r.Assets.Sprites["generic"][4], Is.EqualTo(8));
        // The smoke is not blank: some pixel of every frame is opaque
        for (int f = 0; f < 12; f++)
        {
            bool any = false;
            for (int y = 0; y < smoke[3] && !any; y++)
                for (int x = 0; x < smoke[2] && !any; x++)
                    any = At(r, smoke[0] + f * smoke[2] + x, smoke[1] + y).a > 0;
            Assert.That(any, Is.True, $"big_smoke frame {f} has pixels");
        }
    }
}
