using System.Numerics;
using AubsCraft.Admin.Xr;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Where a headset's room sits in the world, how the thumbsticks move it, and the views that follow.</summary>
public class XrTests
{
    private static void Near(Vector3 a, Vector3 b, float tol = 1e-3f)
        => Assert.That((a - b).Length(), Is.LessThan(tol), $"{a} vs {b}");

    [Test]
    public void TheHeadStartsWhereTheFlyCameraWas_LookingTheWayItLooked()
    {
        var head = new Vector3(0.3f, 1.7f, -0.2f);
        var m = XrMath.WorldFromRoom(head, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), new Vector3(100, 70, -20), new Vector3(1, 0, 0));
        Near(Vector3.Transform(head, m), new Vector3(100, 70, -20));
        var ahead = Vector3.TransformNormal(Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f)), m);
        Near(Vector3.Normalize(ahead), new Vector3(1, 0, 0));
    }

    [Test]
    public void TheRoomStaysLevel_WhateverTheCameraPitch()
    {
        var m = XrMath.WorldFromRoom(Vector3.Zero, Quaternion.Identity, Vector3.Zero, Vector3.Normalize(new Vector3(0, -0.8f, 0.6f)));
        Near(Vector3.TransformNormal(Vector3.UnitY, m), Vector3.UnitY);
    }

    [Test]
    public void AnEyesView_PutsItsOwnPositionAtTheOrigin()
    {
        var pose = XrMath.ViewFromPose(new Vector3(0.03f, 1.6f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f));
        var place = Matrix4x4.CreateRotationY(1.1f) * Matrix4x4.CreateTranslation(50, 64, -10);
        var eye = XrMath.EyeInWorld(pose, place);
        var view = XrMath.EyeView(pose, place);
        Near(Vector3.Transform(eye, view), Vector3.Zero);
    }

    [Test]
    public void AnglesOf_MatchTheFlyCamera()
    {
        var (yaw, pitch) = XrMath.AnglesOf(new Vector3(0, 0, 1));
        Assert.That(yaw, Is.EqualTo(90f).Within(1e-3));
        Assert.That(pitch, Is.EqualTo(0f).Within(1e-3));
        (yaw, pitch) = XrMath.AnglesOf(Vector3.Normalize(new Vector3(1, 1, 0)));
        Assert.That(yaw, Is.EqualTo(0f).Within(1e-3));
        Assert.That(pitch, Is.EqualTo(45f).Within(1e-3));
    }

    [Test]
    public void LeftStickForward_MovesTheWayTheHeadFaces_AtTheGivenSpeed()
    {
        var loco = new XrLocomotion();
        var m = loco.Step(Matrix4x4.Identity, new Vector3(0, 1.7f, 0), Quaternion.Identity, new Vector2(0, -1), Vector2.Zero, 0.5f, 8f);
        Near(m.Translation, new Vector3(0, 0, -4f));
    }

    [Test]
    public void LeftStick_IgnoresADrift()
    {
        var loco = new XrLocomotion();
        var m = loco.Step(Matrix4x4.Identity, Vector3.Zero, Quaternion.Identity, new Vector2(0.05f, -0.05f), Vector2.Zero, 1f, 8f);
        Near(m.Translation, Vector3.Zero);
    }

    [Test]
    public void RightStickUp_Rises_AndSideways_SnapTurnsOncePerPush()
    {
        var loco = new XrLocomotion();
        var m = loco.Step(Matrix4x4.Identity, Vector3.Zero, Quaternion.Identity, Vector2.Zero, new Vector2(0, -1), 0.5f, 8f);
        Near(m.Translation, new Vector3(0, 4f, 0));

        m = Matrix4x4.Identity;
        var pushed = new Vector2(1, 0);
        var once = loco.Step(m, Vector3.Zero, Quaternion.Identity, Vector2.Zero, pushed, 0.016f, 8f);
        var held = loco.Step(once, Vector3.Zero, Quaternion.Identity, Vector2.Zero, pushed, 0.016f, 8f);
        Assert.That(held, Is.EqualTo(once), "a held push turns once");
        var forward = Vector3.TransformNormal(-Vector3.UnitZ, once);
        Assert.That(forward.X, Is.GreaterThan(0.4f), "a right push turns to the right");
        loco.Step(held, Vector3.Zero, Quaternion.Identity, Vector2.Zero, Vector2.Zero, 0.016f, 8f);
        var again = loco.Step(held, Vector3.Zero, Quaternion.Identity, Vector2.Zero, pushed, 0.016f, 8f);
        Assert.That(again, Is.Not.EqualTo(held), "released and pushed again turns again");
    }
}
