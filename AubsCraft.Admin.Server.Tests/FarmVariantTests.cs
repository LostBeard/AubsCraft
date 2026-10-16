using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The warm and cold cows, pigs and chickens of 1.21.5: the plugin says which, the viewer shows the right coat (and, for the cold ones, the shaggier shape).</summary>
public class FarmVariantTests
{
    [Test]
    public void AWarmOrColdAnimal_WearsItsOwnCoat_AndAnyOtherIsTemperate()
    {
        Assert.That(EntityModels.Get("cow", "warm")!.Texture, Is.EqualTo("cow/warm_cow"));
        Assert.That(EntityModels.Get("cow", "cold")!.Texture, Is.EqualTo("cow/cold_cow"));
        Assert.That(EntityModels.Get("pig", "warm")!.Texture, Is.EqualTo("pig/warm_pig"));
        Assert.That(EntityModels.Get("pig", "cold")!.Texture, Is.EqualTo("pig/cold_pig"));
        Assert.That(EntityModels.Get("chicken", "warm")!.Texture, Is.EqualTo("chicken/warm_chicken"));
        Assert.That(EntityModels.Get("chicken", "cold")!.Texture, Is.EqualTo("chicken/cold_chicken"));
        foreach (var type in new[] { "cow", "pig", "chicken" })
        {
            Assert.That(EntityModels.Get(type, "temperate"), Is.SameAs(EntityModels.Get(type)), type);
            Assert.That(EntityModels.Get(type, null), Is.SameAs(EntityModels.Get(type)), type);
            Assert.That(EntityModels.Get(type, "nonsense"), Is.SameAs(EntityModels.Get(type)), type);
        }
        // Another creature's "warm" is nothing to it
        Assert.That(EntityModels.Get("sheep", "warm"), Is.SameAs(EntityModels.Get("sheep")));
    }

    [Test]
    public void TheColdOnesHaveTheirOwnShape_TheWarmCowItsOwnHorns()
    {
        Assert.That(EntityModels.Get("cow", "cold")!.Parts, Is.Not.EqualTo(EntityModels.Get("cow")!.Parts));
        Assert.That(EntityModels.Get("cow", "cold")!.Parts.Any(p => p.Name.Contains("horn")), Is.True, "cold cows have horn parts");
        Assert.That(EntityModels.Get("cow", "warm")!.Parts.First(p => p.Name == "head").Boxes.Length, Is.GreaterThanOrEqualTo(3), "a warm cow's head carries its horns");
        Assert.That(EntityModels.Get("pig", "cold")!.Parts.First(p => p.Name == "body").Boxes.Length, Is.EqualTo(2), "a cold pig has fur over its body");
        // Their legs still walk
        Assert.That(EntityModels.Get("cow", "cold")!.Parts.Count(p => p.Role is PartRole.RightHindLeg or PartRole.LeftHindLeg or PartRole.RightFrontLeg or PartRole.LeftFrontLeg), Is.EqualTo(4));
        Assert.That(EntityModels.Get("chicken", "cold")!.Parts.Any(p => p.Role == PartRole.RightWing), Is.True);
    }

    [Test]
    public void EveryTextureTheyNameIsInTheAtlasList()
    {
        var names = EntityModels.TextureNames().ToHashSet();
        foreach (var t in new[] { "cow/warm_cow", "cow/cold_cow", "pig/warm_pig", "pig/cold_pig", "chicken/warm_chicken", "chicken/cold_chicken" }) Assert.That(names, Does.Contain(t));
    }
}
