using AubsCraft.Admin.Rendering;
using AubsCraft.Live.Model;
using AubsCraft.Live.Model.Entities;
using SpawnDev.GameUI.Elements;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Where the creatures of the live feed land on the minimap, which is turned so its up is where the camera faces.</summary>
public class MinimapMarkersTests
{
    [Test]
    public void AheadIsUp_ToTheRightIsRight_WhicheverWayTheCameraFaces()
    {
        // Facing +X (yaw 0): a creature 30 ahead is straight up the map, one 30 toward +Z is to its right
        var ahead = MinimapMarkers.MapPosition(30, 0, 0, 60);
        Assert.That(ahead.X, Is.EqualTo(0f).Within(1e-5)); Assert.That(ahead.Y, Is.EqualTo(-0.5f).Within(1e-5));
        var right = MinimapMarkers.MapPosition(0, 30, 0, 60);
        Assert.That(right.X, Is.EqualTo(0.5f).Within(1e-5)); Assert.That(right.Y, Is.EqualTo(0f).Within(1e-5));
        // Facing +Z (yaw 90): ahead is +Z, and the right hand is -X
        var ahead90 = MinimapMarkers.MapPosition(0, 30, 90, 60);
        Assert.That(ahead90.X, Is.EqualTo(0f).Within(1e-4)); Assert.That(ahead90.Y, Is.EqualTo(-0.5f).Within(1e-4));
        var right90 = MinimapMarkers.MapPosition(-30, 0, 90, 60);
        Assert.That(right90.X, Is.EqualTo(0.5f).Within(1e-4)); Assert.That(right90.Y, Is.EqualTo(0f).Within(1e-4));
        // Behind is down
        Assert.That(MinimapMarkers.MapPosition(-60, 0, 0, 60).Y, Is.EqualTo(1f).Within(1e-5));
    }

    [Test]
    public void TheMapAgreesWithTheMapRenderer_ForTheFourDirections()
    {
        // The renderer looks down with up = (cos yaw, sin yaw): the screen's right is the cross product of the view direction and that
        foreach (float yaw in new[] { 0f, 37f, 90f, 180f, 250f })
        {
            float r = yaw * MathF.PI / 180f;
            var heading = new System.Numerics.Vector3(MathF.Cos(r), 0f, MathF.Sin(r));
            var forward = new System.Numerics.Vector3(0, -1, 0);
            var screenRight = System.Numerics.Vector3.Cross(forward, heading);
            // A point 10 blocks along the screen's right lands on +x of the map and 10 along the heading on -y
            var p = MinimapMarkers.MapPosition(screenRight.X * 10, screenRight.Z * 10, yaw, 10);
            Assert.That(p.X, Is.EqualTo(1f).Within(1e-4), $"yaw {yaw}"); Assert.That(p.Y, Is.EqualTo(0f).Within(1e-4), $"yaw {yaw}");
            var q = MinimapMarkers.MapPosition(heading.X * 10, heading.Z * 10, yaw, 10);
            Assert.That(q.X, Is.EqualTo(0f).Within(1e-4), $"yaw {yaw}"); Assert.That(q.Y, Is.EqualTo(-1f).Within(1e-4), $"yaw {yaw}");
        }
    }

    private static LiveEntityView Creature(string type, float x, float z, bool player = false, int flags = 0)
        => new(0, type, x, 64f, z, 0f, 0f, 0f, (player ? LiveFlags.Player : 0) | flags, 0f, 0f, null, null);

    [Test]
    public void Players_AreBigAndPinnedToTheBorder_TheRestAreSmallAndLeftOutOfRange_AndColoursFollowTheKind()
    {
        var list = new List<MinimapMarker>();
        MinimapMarkers.Build(new[]
        {
            Creature("zombie", 10, 0), Creature("cow", 0, 10), Creature("villager", -10, 0), Creature("wolf", 0, -10),
            Creature("zombie", 500, 0),                      // far: not drawn
            Creature("player", 500, 0, player: true),         // a player far away: pinned to the border
            Creature("player", 5, 5, player: true),
        }, 0, 0, 0, 60, list);
        Assert.That(list.Count, Is.EqualTo(6), "the far zombie is left out, everyone else is there");
        Assert.That(list.Count(m => m.Color == MinimapMarkers.HostileColour), Is.EqualTo(1));
        Assert.That(list.Count(m => m.Color == MinimapMarkers.PassiveColour), Is.EqualTo(1));
        Assert.That(list.Count(m => m.Color == MinimapMarkers.VillagerColour), Is.EqualTo(1));
        Assert.That(list.Count(m => m.Color == MinimapMarkers.NeutralColour), Is.EqualTo(1));
        var players = list.Where(m => m.Color == MinimapMarkers.PlayerColour).ToList();
        Assert.That(players.Count, Is.EqualTo(2)); Assert.That(players.All(p => p.Size > 4f && p.PinToEdge), Is.True);
        Assert.That(list.Take(4).All(m => m.Size < 4f), Is.True, "the animals come first, the players are drawn over them");
    }

    [Test]
    public void TooManyCreatures_KeepTheNearest()
    {
        var farm = Enumerable.Range(0, 400).Select(i => Creature("chicken", 1 + i * 0.1f, 0)).ToList();
        var list = new List<MinimapMarker>();
        MinimapMarkers.Build(farm, 0, 0, 0, 100, list, max: 50);
        Assert.That(list.Count, Is.EqualTo(50));
        Assert.That(list.Max(m => Math.Abs(m.X) + Math.Abs(m.Y)), Is.LessThan(0.06f), "the 50 nearest of a row that runs 40 blocks");
    }

    [TestCase("zombie", MobKind.Hostile)]
    [TestCase("minecraft:creeper", MobKind.Hostile)]
    [TestCase("wolf", MobKind.Neutral)]
    [TestCase("iron_golem", MobKind.Neutral)]
    [TestCase("villager", MobKind.Villager)]
    [TestCase("wandering_trader", MobKind.Villager)]
    [TestCase("cow", MobKind.Passive)]
    [TestCase("mymod:thing", MobKind.Passive)]
    [TestCase("axolotl", MobKind.Passive)]
    public void TheKindOfACreature(string type, MobKind expected) => Assert.That(MobKinds.Of(type), Is.EqualTo(expected));
}
