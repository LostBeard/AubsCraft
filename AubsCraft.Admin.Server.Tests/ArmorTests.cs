using System.Numerics;
using AubsCraft.Assets;
using AubsCraft.Live.Model;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>What a player wears: read from the player's variant, drawn as the game's armor boxes over the body, in the material's own texture.</summary>
public class ArmorTests
{
    private static List<PartDraw> Pose(string? variant, bool coarse = false, string type = "player")
    {
        var model = EntityModels.Get(type)!;
        var view = new LiveEntityView(1, type, 10, 64, 10, 0, 0, 0, LiveFlags.OnGround | LiveFlags.Player, 0f, 0f, null, variant);
        var list = new List<PartDraw>();
        EntityPoser.Pose(view, model, 0, list, null, coarse);
        return list;
    }

    [Test]
    public void TheVariantIsFourPiecesJoined_ByBar_ANonArmorOneIsNothing()
    {
        var worn = WornArmor.Parse("diamond|iron||leather#FF0000");
        Assert.That((worn.Helmet, worn.Chest, worn.Legs, worn.Boots), Is.EqualTo(("diamond", "iron", (string?)null, "leather#FF0000")));
        Assert.That(WornArmor.Parse(null).Any, Is.False);
        Assert.That(WornArmor.Parse("").Any, Is.False);
        Assert.That(WornArmor.Parse("||||").Any, Is.False, "five parts is not armor");
        Assert.That(WornArmor.Parse("|||").Any, Is.False, "four empty ones");
        Assert.That(WornArmor.Parse("cow|stone||").Any, Is.False, "materials the game does not make armor of");
        Assert.That(WornArmor.Parse("wolf").Any, Is.False, "another creature's variant");
        Assert.That(WornArmor.LeatherColour("leather#3366FF"), Is.EqualTo(new Vector3(0x33 / 255f, 0x66 / 255f, 1f)));
        Assert.That(WornArmor.LeatherColour("leather"), Is.EqualTo(new Vector3(0xA0 / 255f, 0x65 / 255f, 0x40 / 255f)), "the game's own brown");
    }

    [Test]
    public void EachPiece_AddsItsBoxes_ToTheRightPartsOfTheBody()
    {
        var bare = Pose(null);
        Assert.That(Pose("diamond|||").Count - bare.Count, Is.EqualTo(1), "a helmet: one box on the head");
        Assert.That(Pose("|diamond||").Count - bare.Count, Is.EqualTo(3), "a chestplate: the body and the two arms");
        Assert.That(Pose("||diamond|").Count - bare.Count, Is.EqualTo(3), "leggings: the hips and the two legs");
        Assert.That(Pose("|||diamond").Count - bare.Count, Is.EqualTo(2), "boots: the two legs");
        Assert.That(Pose("diamond|diamond|diamond|diamond").Count - bare.Count, Is.EqualTo(9));
        var all = Pose("diamond|iron|gold|chainmail");
        Assert.That(all.Select(d => d.Texture).Where(t => t.StartsWith("equipment/")).Distinct(),
            Is.EquivalentTo(new[] { "equipment/humanoid/diamond", "equipment/humanoid/iron", "equipment/humanoid_leggings/gold", "equipment/humanoid/chainmail" }));
    }

    [Test]
    public void ArmorSitsOutsideTheBody_LeggingsInsideTheBoots()
    {
        var all = Pose("diamond|diamond|diamond|diamond").Where(d => d.Texture.StartsWith("equipment/")).ToList();
        Assert.That(all.Where(d => d.Texture.Contains("leggings")).All(d => d.Inflate == 0.5f));
        Assert.That(all.Where(d => !d.Texture.Contains("leggings")).All(d => d.Inflate == 1.0f));
    }

    [Test]
    public void Leather_IsDyedWithItsOwnColour_AndAnOverlayLiesOverIt()
    {
        var draws = Pose("leather#3366FF|||").Where(d => d.Texture.StartsWith("equipment/")).ToList();
        Assert.That(draws.Count, Is.EqualTo(2));
        Assert.That(draws[0].Texture, Is.EqualTo("equipment/humanoid/leather"));
        Assert.That(draws[0].Tint, Is.EqualTo(new Vector3(0x33 / 255f, 0x66 / 255f, 1f)));
        Assert.That(draws[1].Texture, Is.EqualTo("equipment/humanoid/leather_overlay"));
        Assert.That(draws[1].Tint, Is.EqualTo(Vector3.One), "the overlay is not dyed");
        Assert.That(draws[1].Inflate, Is.GreaterThan(draws[0].Inflate));
        Assert.That(Pose("||leather#3366FF|").Count(d => d.Texture == "equipment/humanoid_leggings/leather_overlay"), Is.EqualTo(3));
    }

    [Test]
    public void ATurtleShellIsAHelmetOnly()
    {
        Assert.That(Pose("turtle_scute|||").Count(d => d.Texture == "equipment/humanoid/turtle_scute"), Is.EqualTo(1));
        Assert.That(Pose("||turtle_scute|").Count(d => d.Texture.StartsWith("equipment/")), Is.Zero);
    }

    [Test]
    public void FarAway_NoArmorIsDrawn_AndNeitherIsAnyOnAnotherCreature()
    {
        Assert.That(Pose("diamond|diamond|diamond|diamond", coarse: true).Count(d => d.Texture.StartsWith("equipment/")), Is.Zero);
        Assert.That(Pose("diamond|diamond|diamond|diamond", type: "zombie").Count(d => d.Texture.StartsWith("equipment/")), Is.Zero, "the zombie's variant is nothing of the sort");
    }

    [Test]
    public void ArmorTurnsWithTheLimbItIsOn()
    {
        // The chestplate's arm boxes use their arms' own matrices: with the right arm swung forward, the armor on it goes too
        var model = EntityModels.Get("player")!;
        LiveEntityView View(int flags) => new(1, "player", 10, 64, 10, 0, 0, 0, flags, 0f, 0f, null, "|diamond||");
        var rest = new List<PartDraw>(); EntityPoser.Pose(View(LiveFlags.OnGround | LiveFlags.Player), model, 0, rest);
        var swing = new List<PartDraw>(); EntityPoser.Pose(View(LiveFlags.OnGround | LiveFlags.Player | LiveFlags.Swinging), model, 2, swing);
        var armRest = rest.First(d => d.Texture == "equipment/humanoid/diamond" && d.U == 40 && !d.Mirror);
        var armSwing = swing.First(d => d.Texture == "equipment/humanoid/diamond" && d.U == 40 && !d.Mirror);
        Assert.That(armSwing.Model, Is.Not.EqualTo(armRest.Model));
    }

    [Test]
    public async Task EveryArmorTexture_IsInTheGamesOwnJar()
    {
        using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync("1.21.5"));
        var r = new EntityAtlasBuilder(jar, "1.21.5").Build(EntityModels.TextureNames());
        TestContext.Out.WriteLine(r.Report);
        Assert.That(r.Report, Does.Not.Contain("MISSING texture: equipment"));
        foreach (var name in ArmorLook.TextureNames()) Assert.That(r.Assets.Textures.ContainsKey(name), Is.True, name);
    }
}
