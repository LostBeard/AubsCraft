using System.Numerics;
using AubsCraft.Live.Model;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>An arrow flies the way it points: the tip along its yaw (the game's own for projectiles: south 0, east 90) and up by its pitch.</summary>
public class ProjectileTests
{
    private static (Vector3 tip, Vector3 tail) Axis(float yaw, float pitch)
    {
        var model = EntityModels.Get("arrow")!;
        var view = new LiveEntityView(1, "arrow", 100, 70, 200, yaw, pitch, yaw, 0, 0f, 0f, null, null);
        var draws = new List<PartDraw>();
        EntityPoser.Pose(view, model, 0, draws);
        // The shaft is the boxes' long axis: model x from -12 to +4 (the tip at +x): the extremes along the flight
        var points = draws.SelectMany(d => new[] { Vector3.Transform(d.Origin, d.Model), Vector3.Transform(d.Origin + d.Size, d.Model), Vector3.Transform(d.Origin + new Vector3(d.Size.X, 0, 0), d.Model), Vector3.Transform(d.Origin + new Vector3(0, d.Size.Y, 0), d.Model) }).ToList();
        var centre = new Vector3(100, 70, 200);
        var tip = points.OrderByDescending(p => (p - centre).Length()).First();
        var forward = Vector3.Normalize(Vector3.Transform(new Vector3(1, 0, 0), draws[0].Model) - Vector3.Transform(Vector3.Zero, draws[0].Model));
        return (forward, tip - centre);
    }

    [Test]
    public void TheArrowFliesAlongItsYaw()
    {
        // A projectile's yaw is not a creature's: the game gives it atan2(x, z) of its flight, so +90 is east (+x), -90 west, 0 south, 180 north
        var cases = new[] { (0f, Vector3.UnitZ), (90f, Vector3.UnitX), (180f, -Vector3.UnitZ), (-90f, -Vector3.UnitX) };
        foreach (var (yaw, want) in cases)
        {
            var (forward, _) = Axis(yaw, 0f);
            Assert.That(Vector3.Dot(forward, want), Is.GreaterThan(0.99f), $"yaw {yaw}: {forward}");
        }
    }

    [Test]
    public void ThePitchLiftsTheTip_UpPositive()
    {
        var (up, _) = Axis(0f, 45f);
        Assert.That(up.Y, Is.EqualTo(MathF.Sin(MathF.PI / 4)).Within(0.02f), "pitched 45 up the tip rises");
        var (down, _) = Axis(0f, -45f);
        Assert.That(down.Y, Is.EqualTo(-MathF.Sin(MathF.PI / 4)).Within(0.02f));
    }

    [Test]
    public void AnArrowIsNeitherACreatureNorOnTheMinimap_ButItHasAModel()
    {
        Assert.That(EntityModels.Get("arrow"), Is.Not.Null);
        Assert.That(EntityModels.Get("spectral_arrow"), Is.Not.Null);
        Assert.That(EntityModels.IsVehicleOrStand("arrow"), Is.True);
        Assert.That(EntityModels.IsNotACreature("arrow"), Is.False, "it is drawn like a model, not left out");
    }
}
