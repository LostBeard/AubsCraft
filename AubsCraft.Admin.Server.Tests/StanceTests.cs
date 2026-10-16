using System.Numerics;
using AubsCraft.Live.Model;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>How a creature stands, sits, swims, glides, sleeps or lies dead: the game's humanoid poses from the flags a live frame carries.</summary>
public class StanceTests
{
    private static LiveEntityView View(string type, int flags, float yaw = 0, float pitch = 0, float y = 64) =>
        new(1, type, 10, y, 10, yaw, pitch, yaw, flags, 0f, 0f, null, null);

    private static (List<PartDraw> draws, Matrix4x4? arm) Pose(string type, LiveEntityView view, float age = 0, float? sleepYaw = null, bool holding = false)
    {
        var model = EntityModels.Get(type) ?? throw new InvalidOperationException(type);
        var list = new List<PartDraw>();
        var arm = EntityPoser.Pose(view, model, age, list, null, false, sleepYaw, holding);
        return (list, arm);
    }

    private static Vector3 World(in PartDraw d, Vector3 p) => Vector3.Transform(p, d.Model);
    private static Vector3 Centre(in PartDraw d) => World(d, d.Origin + d.Size / 2f);

    private static (Vector3 min, Vector3 max) Extent(IEnumerable<PartDraw> draws)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var d in draws)
            for (int i = 0; i < 8; i++)
            {
                var p = World(d, d.Origin + new Vector3((i & 1) * d.Size.X, ((i >> 1) & 1) * d.Size.Y, ((i >> 2) & 1) * d.Size.Z));
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
        return (min, max);
    }

    private const int Standing = LiveFlags.OnGround | LiveFlags.Player;

    [Test]
    public void AUprightPlayer_IsAsTallAsTheGameSays()
    {
        var (min, max) = Extent(Pose("player", View("player", Standing)).draws);
        Assert.That(min.Y, Is.EqualTo(64f).Within(0.05f));
        Assert.That(max.Y - 64f, Is.InRange(1.7f, 1.9f));
    }

    [Test]
    public void ASneakingPlayer_IsLowerAndLeansForward_AndIsDrawnAnEighthLower()
    {
        var up = Extent(Pose("player", View("player", Standing)).draws);
        var sneak = Extent(Pose("player", View("player", Standing | LiveFlags.Sneaking)).draws);
        Assert.That(sneak.max.Y, Is.LessThan(up.max.Y - 0.1f), "the head is lower");
        Assert.That(sneak.max.Y, Is.GreaterThan(up.max.Y - 0.5f), "but not by much");
        Assert.That(sneak.min.Y, Is.EqualTo(64f - 0.125f).Within(0.08f), "the whole player is an eighth down");
        // The legs step back under the leaning body: the head and the feet are further apart along the way it faces than standing up
        float Lean(int flags) { var d = Pose("player", View("player", flags)).draws; return Math.Abs(Centre(d[0]).Z - Centre(d[8]).Z); }
        Assert.That(Lean(Standing | LiveFlags.Sneaking), Is.GreaterThan(Lean(Standing) + 0.15f), "the legs step back");
    }

    [Test]
    public void ASneakingMobWhoseModelIsNotAHumanoid_IsNotChanged()
    {
        var a = Pose("pig", View("pig", LiveFlags.OnGround)).draws;
        var b = Pose("pig", View("pig", LiveFlags.OnGround | LiveFlags.Sneaking)).draws;
        for (int i = 0; i < a.Count; i++) Assert.That(b[i].Model, Is.EqualTo(a[i].Model));
    }

    [Test]
    public void ASwimmingPlayer_LiesFlat_ItsLengthAlongTheWayItLooks()
    {
        var (min, max) = Extent(Pose("player", View("player", LiveFlags.Player | LiveFlags.Swimming, yaw: 0)).draws);
        Assert.That(max.Y - min.Y, Is.LessThan(1.3f), "flat, not standing");
        Assert.That(max.Z - min.Z, Is.GreaterThan(1.5f), "its length is along z: the way it faces (south)");
        var (minE, maxE) = Extent(Pose("player", View("player", LiveFlags.Player | LiveFlags.Swimming, yaw: -90)).draws);
        Assert.That(maxE.X - minE.X, Is.GreaterThan(1.5f), "facing east it lies along x");
    }

    [Test]
    public void AGlidingPlayer_IsFlatToo()
    {
        var (min, max) = Extent(Pose("player", View("player", LiveFlags.Player | LiveFlags.Gliding, pitch: 0)).draws);
        Assert.That(max.Y - min.Y, Is.LessThan(0.9f));
        Assert.That(max.Z - min.Z, Is.GreaterThan(1.5f));
        // Diving straight down (pitch 90) it is upright again, head down
        var (dmin, dmax) = Extent(Pose("player", View("player", LiveFlags.Player | LiveFlags.Gliding, pitch: 90)).draws);
        Assert.That(dmax.Y - dmin.Y, Is.GreaterThan(1.5f));
    }

    [Test]
    public void ARidingPlayer_SitsWithTheLegsForward()
    {
        var (_, maxStanding) = Extent(Pose("player", View("player", Standing)).draws);
        var (min, max) = Extent(Pose("player", View("player", LiveFlags.Player | LiveFlags.Riding)).draws);
        Assert.That(max.Z - min.Z, Is.GreaterThan(0.9f), "the legs reach forward");
        Assert.That(max.Y - min.Y, Is.LessThan(maxStanding.Y - 64f - 0.2f), "and the player is not as tall");
    }

    [Test]
    public void ADeadCreature_LiesOnItsSide_WhateverItIs()
    {
        foreach (var type in new[] { "pig", "zombie", "player" })
        {
            var flags = LiveFlags.Dead | (type == "player" ? LiveFlags.Player : 0);
            var (min, max) = Extent(Pose(type, View(type, flags)).draws);
            var (smin, smax) = Extent(Pose(type, View(type, flags & ~LiveFlags.Dead)).draws);
            Assert.That(max.Y - min.Y, Is.LessThan((smax.Y - smin.Y) * 0.8f), $"{type} is lower lying down");
        }
    }

    [Test]
    public void ASleepingPlayer_LiesAlongItsBed_HeadAtThePillow()
    {
        // A bed facing south (the head end is south): the sleeper lies along z
        var south = Pose("player", View("player", LiveFlags.Player | LiveFlags.Sleeping), sleepYaw: 0f).draws;
        var (smin, smax) = Extent(south);
        Assert.That(smax.Y - smin.Y, Is.LessThan(0.9f), "flat");
        Assert.That(smax.Z - smin.Z, Is.GreaterThan(1.5f), "along the bed (z)");
        var west = Pose("player", View("player", LiveFlags.Player | LiveFlags.Sleeping), sleepYaw: 90f).draws;
        var (wmin, wmax) = Extent(west);
        Assert.That(wmax.X - wmin.X, Is.GreaterThan(1.5f), "a bed facing west: along x");
        // The head is at the pillow end: for a bed facing south the head end is toward +z of the sleeper's centre
        var head = Centre(south[0]);
        Assert.That(head.Z, Is.GreaterThan((smin.Z + smax.Z) / 2f), "head toward the way the bed faces");
        // Someone asleep with no bed found lies down all the same
        var none = Extent(Pose("player", View("player", LiveFlags.Player | LiveFlags.Sleeping)).draws);
        Assert.That(none.max.Y - none.min.Y, Is.LessThan(0.9f));
    }

    [Test]
    public void ThePlayerWhoHoldsSomething_RaisesTheRightArm_AndTheMatrixOfItsHandIsGiven()
    {
        var empty = Pose("player", View("player", Standing));
        var holding = Pose("player", View("player", Standing), holding: true);
        Assert.That(empty.arm, Is.Not.Null);
        Assert.That(holding.arm, Is.Not.Null);
        Assert.That(holding.arm!.Value, Is.Not.EqualTo(empty.arm!.Value), "the arm moved forward");
        // The shoulder (the arm's own origin) is at the player's right side, a little below the top of the body
        var shoulder = Vector3.Transform(Vector3.Zero, holding.arm.Value);
        Assert.That(shoulder.Y - 64f, Is.InRange(1.2f, 1.4f));
        // A hand's length down the arm, with the arm lowered: the hand is around the hip
        var hand = Vector3.Transform(new Vector3(0f, 10f, 0f), empty.arm.Value);
        Assert.That(hand.Y - 64f, Is.InRange(0.5f, 0.9f));
        var model = EntityModels.Get("pig")!;
        var list = new List<PartDraw>();
        Assert.That(EntityPoser.Pose(View("pig", LiveFlags.OnGround), model, 0, list), Is.Null, "a pig has no right arm to hold anything");
    }
}
