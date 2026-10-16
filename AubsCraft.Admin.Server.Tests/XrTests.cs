using System.Numerics;
using AubsCraft.Admin.Rendering;
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

    [Test]
    public void TheTerrainInFrontOfAHeadsetIsInsideItsFrustum()
    {
        // What the mock headset of the viewer gate sends: 90 degrees, square eyes, near 0.05, far 3000; the head 120 up looking +X
        float f = 1f;
        float near = 0.05f, far = 3000f;
        var proj = new Matrix4x4(f, 0, 0, 0, 0, f, 0, 0, 0, 0, -(far + near) / (far - near), -1, 0, 0, -2f * far * near / (far - near), 0);
        var head = new Vector3(0, 1.7f, 0);
        var place = XrMath.WorldFromRoom(head, Quaternion.Identity, new Vector3(0, 120, 0), new Vector3(1, 0, 0));
        var roomView = XrMath.ViewFromPose(new Vector3(-0.032f, 1.7f, 0), Quaternion.Identity);
        var view = ViewSetup.FromXr(XrMath.EyeInWorld(roomView, place), XrMath.EyeView(roomView, place), proj);
        var planes = FrustumCuller.ExtractPlanes(view.ViewProjectionStandard);
        Assert.That(FrustumCuller.IsBoxVisible(in planes, new Vector3(64, 50, -8), new Vector3(80, 70, 8)), Is.True, "terrain ahead and below is seen");
        Assert.That(FrustumCuller.IsBoxVisible(in planes, new Vector3(-80, 50, -8), new Vector3(-64, 70, 8)), Is.False, "terrain behind is not");
    }
}
