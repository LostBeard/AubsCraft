using System.Numerics;
using AubsCraft.Admin.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The matrices a view is drawn with: the fly camera's, and an eye of a headset (WebXR's OpenGL projection turned into the engine's reversed-Z and the frustum's standard-Z).</summary>
public class ViewSetupTests
{
    private static (float x, float y, float z) Ndc(Matrix4x4 m, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1f), m);
        return (c.X / c.W, c.Y / c.W, c.Z / c.W);
    }

    // WebXR's projection as a row-vector matrix (the column-major GL one transposed by reading its bytes the other way)
    private static Matrix4x4 GlProjection(float fovY, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovY / 2f);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, -(far + near) / (far - near), -1,
            0, 0, -2f * far * near / (far - near), 0);
    }

    [Test]
    public void TheFlyCamera_GivesTheMatricesItDidBefore()
    {
        var cam = new FpsCamera { Position = new Vector3(10, 70, -4), Yaw = 35, Pitch = -12 };
        var v = ViewSetup.Of(cam, 1.6f);
        Assert.That(v.Position, Is.EqualTo(cam.Position));
        Assert.That(v.ViewProjectionStandard, Is.EqualTo(cam.GetVpMatrix(1.6f)));
        var ahead = cam.Position + cam.Front * 5f;
        var (x, y, z) = Ndc(v.ViewProjectionReversed, ahead);
        Assert.That(x, Is.EqualTo(0).Within(1e-4));
        Assert.That(y, Is.EqualTo(0).Within(1e-4));
        Assert.That(z, Is.InRange(0.0001f, 1f), "reversed-Z: in front is between 1 (near) and 0 (far)");
    }

    [Test]
    public void AnEyeWithTheSameFieldOfView_SeesWhatTheFlyCameraSees()
    {
        var cam = new FpsCamera { Position = new Vector3(0, 64, 0), Yaw = -90, Pitch = 0, FovDegrees = 80f, NearPlane = 0.05f };
        float aspect = 1.1f;
        var fly = ViewSetup.Of(cam, aspect);
        var eye = ViewSetup.FromXr(cam.Position, cam.GetViewMatrix(), GlProjection(80f * MathF.PI / 180f, aspect, 0.05f, 4000f));
        foreach (var p in new[] { new Vector3(0, 64, -10), new Vector3(3, 66, -20), new Vector3(-6, 60, -15), new Vector3(1, 64, -2) })
        {
            var (fx, fy, _) = Ndc(fly.ViewProjectionReversed, p);
            var (ex, ey, ez) = Ndc(eye.ViewProjectionReversed, p);
            Assert.That(ex, Is.EqualTo(fx).Within(1e-3), $"x at {p}");
            Assert.That(ey, Is.EqualTo(fy).Within(1e-3), $"y at {p}");
            Assert.That(ez, Is.InRange(0f, 1f), "inside the reversed depth range");
            var (_, _, sz) = Ndc(eye.ViewProjectionStandard, p);
            Assert.That(sz, Is.InRange(0f, 1f), "inside the standard depth range, for the frustum");
        }
    }

    [Test]
    public void NearIsOne_AndFarIsZero_InReversedZ_AndTheOtherWayInStandardZ()
    {
        var eye = ViewSetup.FromXr(Vector3.Zero, Matrix4x4.Identity, GlProjection(1.5f, 1f, 0.1f, 1000f));
        var (_, _, nearR) = Ndc(eye.ViewProjectionReversed, new Vector3(0, 0, -0.1f));
        var (_, _, farR) = Ndc(eye.ViewProjectionReversed, new Vector3(0, 0, -1000f));
        var (_, _, nearS) = Ndc(eye.ViewProjectionStandard, new Vector3(0, 0, -0.1f));
        var (_, _, farS) = Ndc(eye.ViewProjectionStandard, new Vector3(0, 0, -1000f));
        Assert.That(nearR, Is.EqualTo(1f).Within(1e-4));
        Assert.That(farR, Is.EqualTo(0f).Within(1e-4));
        Assert.That(nearS, Is.EqualTo(0f).Within(1e-4));
        Assert.That(farS, Is.EqualTo(1f).Within(1e-4));
    }

    [Test]
    public void AnOffAxisEye_KeepsItsOwnCentre()
    {
        // A headset's eyes look through off-centre frusta: a point straight ahead is not at the centre of the screen
        var p = GlProjection(1.4f, 1f, 0.1f, 500f);
        p.M31 = 0.12f;   // the x shift of the frustum (a row-vector matrix keeps it in the third row)
        var eye = ViewSetup.FromXr(Vector3.Zero, Matrix4x4.Identity, p);
        var (x, _, _) = Ndc(eye.ViewProjectionReversed, new Vector3(0, 0, -5));
        Assert.That(Math.Abs(x), Is.GreaterThan(0.05f), "off axis");
    }
}
