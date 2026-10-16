using AubsCraft.Admin.Rendering;
using AubsCraft.Assets;
using AubsCraft.Live.Model.Particles;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>What each emitter shows, checked against the game's real particle sprites: nothing missing, every number inside what the pipeline accepts.</summary>
public class ParticleCatalogTests
{
    private static async Task<(ParticleCatalog catalog, AubsCraft.Assets.Model.ParticleAssets assets)> RealAsync()
    {
        using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync("1.21.5"));
        var atlas = new ParticleAtlasBuilder(jar, "1.21.5").Build();
        return (ParticleCatalog.Build(atlas.Assets), atlas.Assets);
    }

    [Test]
    public async Task EveryEmitterKind_ShowsSomething_AndNoSpriteIsMissing()
    {
        var (catalog, _) = await RealAsync();
        Assert.That(catalog.Missing, Is.Empty, "the game's jar has every sprite the catalog names");
        foreach (var kind in Enum.GetValues<EmitterKind>().Where(k => k != EmitterKind.None))
        {
            Assert.That(catalog.KindsOf(kind), Is.Not.Empty, $"{kind} shows nothing");
            Assert.That(ParticleCatalog.RangeOf(kind), Is.GreaterThan(0f), $"{kind} has no range");
        }
        Assert.That(catalog.KindsOf(EmitterKind.None), Is.Empty);
    }

    [Test]
    public async Task EveryKind_FitsWhatThePipelineAccepts_AndItsSpriteLiesInsideTheAtlas()
    {
        var (catalog, assets) = await RealAsync();
        Assert.That(catalog.Kinds.Length, Is.InRange(1, ParticleRenderPipeline.MaxKinds));
        foreach (var k in catalog.Kinds)
        {
            Assert.That((int)k.Counts.X, Is.InRange(1, ParticleRenderPipeline.MaxPerEmitter));
            Assert.That(k.Counts.Y, Is.GreaterThan(0f), "a life");
            int frames = (int)k.Counts.W;
            Assert.That(frames, Is.GreaterThanOrEqualTo(1));
            Assert.That(k.Sprite.X + k.Sprite.Z * frames, Is.LessThanOrEqualTo(assets.AtlasWidth), "the strip ends inside the atlas");
            Assert.That(k.Sprite.Y + k.Sprite.W, Is.LessThanOrEqualTo(assets.AtlasHeight));
            Assert.That(k.Fade.X, Is.GreaterThan(0f)); Assert.That(k.Fade.Y, Is.GreaterThan(0f), "a particle has a size");
            Assert.That(k.Fade.Z, Is.InRange(0f, 1f)); Assert.That(k.Fade.W, Is.InRange(0f, 1f));
        }
    }

    [Test]
    public async Task ASmokeWith_TwelveFrames_AndAFlameWithOne_AreUsedAsTheyAre()
    {
        var (catalog, assets) = await RealAsync();
        var smoke = catalog.Kinds[catalog.KindsOf(EmitterKind.Campfire)[0]];
        Assert.That((int)smoke.Counts.W, Is.EqualTo(12));
        Assert.That(smoke.Counts.Z, Is.EqualTo(0f), "smoke is lit by the day, not glowing");
        var embers = catalog.Kinds[catalog.KindsOf(EmitterKind.Campfire)[1]];
        Assert.That((int)embers.Counts.W, Is.EqualTo(1));
        Assert.That(embers.Counts.Z, Is.EqualTo(ParticleKind.FlagEmissive), "embers glow");
    }

    [Test]
    public void ASpriteTheAtlasLacks_DropsItsEntryAndIsReported()
    {
        var assets = new AubsCraft.Assets.Model.ParticleAssets { AtlasWidth = 64, AtlasHeight = 16, Sprites = { ["flame"] = new[] { 0, 0, 8, 8, 1 } } };
        var catalog = ParticleCatalog.Build(assets);
        Assert.That(catalog.Missing, Is.Not.Empty);
        Assert.That(catalog.KindsOf(EmitterKind.Torch), Has.Count.EqualTo(1), "the torch's flame is there, its smoke is not");
        Assert.That(catalog.KindsOf(EmitterKind.Furnace), Is.Empty, "a furnace has only smoke");
    }
}
