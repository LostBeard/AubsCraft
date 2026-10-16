using AubsCraft.Assets;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The sky atlas: the game's clouds, sun and moon phases, each where the description says, intact, none over another.</summary>
public class SkyAtlasTests
{
    private static (byte r, byte g, byte b, byte a) At(SkyAtlasResult r, int x, int y)
    {
        int i = (y * r.Assets.AtlasWidth + x) * 4;
        return (r.Rgba[i], r.Rgba[i + 1], r.Rgba[i + 2], r.Rgba[i + 3]);
    }

    [Test]
    public void ThePartsAreWhereTheDescriptionSays_AndIntact()
    {
        var fake = new FakeJar();
        fake.Texture("environment/clouds", 16, 16, (x, y) => (10, (byte)x, (byte)y, 255));
        fake.Texture("environment/sun", 8, 8, (x, y) => (20, (byte)x, (byte)y, 255));
        fake.Texture("environment/moon_phases", 16, 8, (x, y) => (30, (byte)x, (byte)y, 255));   // four frames of 4x4 across, two down
        using var jar = fake.Build();
        var r = new SkyAtlasBuilder(jar, "test").Build();
        Assert.That(r.Assets.Clouds, Is.EqualTo(new[] { 0, 0, 16, 16 }));
        Assert.That(r.Assets.Sun, Is.EqualTo(new[] { 16, 0, 8, 8 }));
        Assert.That(r.Assets.Moon, Is.EqualTo(new[] { 16, 8, 4, 4 }));
        Assert.That(r.Rgba.Length, Is.EqualTo(r.Assets.AtlasWidth * r.Assets.AtlasHeight * 4));
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) Assert.That(At(r, x, y), Is.EqualTo(((byte)10, (byte)x, (byte)y, (byte)255)));
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) Assert.That(At(r, 16 + x, y), Is.EqualTo(((byte)20, (byte)x, (byte)y, (byte)255)));
        for (int y = 0; y < 8; y++) for (int x = 0; x < 16; x++) Assert.That(At(r, 16 + x, 8 + y), Is.EqualTo(((byte)30, (byte)x, (byte)y, (byte)255)));
    }

    [Test]
    public void AMoonSheetThatIsNotFourByTwo_IsRefused()
    {
        var fake = new FakeJar();
        fake.Texture("environment/clouds", 4, 4, (x, y) => (1, 1, 1, 255));
        fake.Texture("environment/sun", 4, 4, (x, y) => (1, 1, 1, 255));
        fake.Texture("environment/moon_phases", 6, 3, (x, y) => (1, 1, 1, 255));
        using var jar = fake.Build();
        Assert.Throws<InvalidOperationException>(() => new SkyAtlasBuilder(jar, "test").Build());
    }

    [Test]
    public async Task TheGamesOwnSkyImages_ArePacked()
    {
        using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync("1.21.5"));
        var r = new SkyAtlasBuilder(jar, "1.21.5").Build();
        TestContext.Out.WriteLine(r.Report);
        Assert.That(r.Assets.Sun[2], Is.EqualTo(32)); Assert.That(r.Assets.Sun[3], Is.EqualTo(32));
        Assert.That(r.Assets.Moon[2], Is.EqualTo(32)); Assert.That(r.Assets.Moon[3], Is.EqualTo(32));
        Assert.That(r.Assets.Clouds[2], Is.EqualTo(256)); Assert.That(r.Assets.Clouds[3], Is.EqualTo(256));
        // The sun is not blank; every moon phase has something in it
        bool Any(int x0, int y0, int w, int h) { for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (At(r, x0 + x, y0 + y).a > 0) return true; return false; }
        Assert.That(Any(r.Assets.Sun[0], r.Assets.Sun[1], 32, 32), Is.True);
        for (int p = 0; p < 8; p++) Assert.That(Any(r.Assets.Moon[0] + (p % 4) * 32, r.Assets.Moon[1] + (p / 4) * 32, 32, 32), Is.True, $"phase {p}");
        Assert.That(Any(0, 0, 256, 256), Is.True, "clouds");
    }
}
