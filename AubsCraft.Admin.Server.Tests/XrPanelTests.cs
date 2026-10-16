using System.Numerics;
using AubsCraft.Admin.Xr;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The HUD panel a headset shows: where it floats, how it follows the head, and where a controller's ray meets it.</summary>
public class XrPanelTests
{
    private static readonly Vector3 Head = new(0.1f, 1.7f, 0.2f);

    private static void Near(Vector3 a, Vector3 b, float tol = 1e-3f)
        => Assert.That((a - b).Length(), Is.LessThan(tol), $"{a} vs {b}");

    private static Quaternion Facing(float degreesLeft) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, degreesLeft * MathF.PI / 180f);

    [Test]
    public void ItStartsInFrontOfTheHead_AJustBelowTheEyes()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        Near(panel.Center, Head + new Vector3(0, -XrPanel.DropBelowEyes, -XrPanel.Distance));
        Near(panel.Forward, -Vector3.UnitZ);
        Near(panel.Right, Vector3.UnitX);
    }

    [Test]
    public void ItStartsWhereTheHeadLooks_AndItsRightIsTheViewersRight()
    {
        var panel = new XrPanel();
        // Turned a quarter left from -Z: looking down -X, where the viewer's right is -Z
        panel.Update(0.016f, Head, Facing(90));
        Near(panel.Forward, -Vector3.UnitX);
        Near(panel.Right, -Vector3.UnitZ);
        // Turned a quarter right: looking +X, right is +Z
        var other = new XrPanel();
        other.Update(0.016f, Head, Facing(-90));
        Near(other.Forward, Vector3.UnitX);
        Near(other.Right, Vector3.UnitZ);
    }

    [Test]
    public void TheCornersRunAsThePixelsDo_AndTheSizeIsTheConstants()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        var c = panel.Corners();
        Assert.That(c.TopLeft.X, Is.LessThan(c.TopRight.X), "left is left as the viewer sees it");
        Assert.That(c.TopLeft.Y, Is.GreaterThan(c.BottomLeft.Y), "the top is up");
        Assert.That((c.TopRight - c.TopLeft).Length(), Is.EqualTo(XrPanel.Width).Within(1e-4));
        Assert.That((c.TopLeft - c.BottomLeft).Length(), Is.EqualTo(XrPanel.Height).Within(1e-4));
        Near((c.TopLeft + c.BottomRight) / 2, panel.Center);
        // The panel and its pixels have the same shape
        Assert.That((double)XrPanel.PixelWidth / XrPanel.PixelHeight, Is.EqualTo(XrPanel.Width / XrPanel.Height).Within(1e-3));
    }

    [Test]
    public void AGlanceAside_LeavesItWhereItWas()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        float start = panel.Heading;
        for (int i = 0; i < 120; i++) panel.Update(0.016f, Head, Facing(XrPanel.DeadZoneDegrees - 3));
        Assert.That(panel.Heading, Is.EqualTo(start).Within(1e-4), "inside the dead zone the panel stays put");
    }

    [Test]
    public void LookingAwayDragsItAfterTheHead_UntilTheHeadIsAtTheEdgeOfTheDeadZone()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        float last = panel.Heading;
        for (int i = 0; i < 20; i++)
        {
            panel.Update(0.016f, Head, Facing(90));
            Assert.That(panel.Heading, Is.GreaterThanOrEqualTo(last), "it follows toward the head, never away");
            last = panel.Heading;
        }
        Assert.That(panel.Heading, Is.GreaterThan(0.05f), "it started to follow");
        for (int i = 0; i < 600; i++) panel.Update(0.016f, Head, Facing(90));
        float gap = MathF.PI / 2 - panel.Heading;
        Assert.That(gap * 180 / MathF.PI, Is.EqualTo(XrPanel.DeadZoneDegrees).Within(0.5), "it settles with the head at the edge of the dead zone");
    }

    [Test]
    public void FollowsTheShortWayRound()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Facing(170));
        for (int i = 0; i < 600; i++) panel.Update(0.016f, Head, Facing(-170));   // 20 degrees apart across the back, not 340
        float gap = MathF.Abs(MathF.Atan2(MathF.Sin(-170 * MathF.PI / 180f - panel.Heading), MathF.Cos(-170 * MathF.PI / 180f - panel.Heading)));
        Assert.That(gap * 180 / MathF.PI, Is.LessThanOrEqualTo(XrPanel.DeadZoneDegrees + 0.5));
    }

    [Test]
    public void ItKeepsItsDistanceFromTheHead_WhateverTheHeadDoes()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        var moved = Head + new Vector3(0.5f, 0.1f, 0.4f);
        panel.Update(0.016f, moved, Quaternion.Identity);
        Near(panel.Center - moved, new Vector3(0, -XrPanel.DropBelowEyes, -XrPanel.Distance));
    }

    [Test]
    public void LookingStraightUp_DoesNotSwingIt()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        float start = panel.Heading;
        var up = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);   // -Z becomes +Y
        for (int i = 0; i < 100; i++) panel.Update(0.016f, Head, up);
        Assert.That(panel.Heading, Is.EqualTo(start).Within(1e-4), "a head pointing at the ceiling has no heading to follow");
    }

    [Test]
    public void ShownAgain_ItComesBackWhereTheHeadLooks()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        panel.SetVisible(false);
        Assert.That(panel.Visible, Is.False);
        panel.SetVisible(true);
        panel.Update(0.016f, Head, Facing(120));
        Near(panel.Forward, new Vector3(-MathF.Sin(120 * MathF.PI / 180f), 0, -MathF.Cos(120 * MathF.PI / 180f)));
    }

    [Test]
    public void ARayFromTheHand_MeetsThePanelAtThePixelItPointsAt()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        var hand = Head + new Vector3(0.25f, -0.4f, -0.2f);

        // Straight at the middle
        var toMiddle = Vector3.Normalize(panel.Center - hand);
        Assert.That(panel.Hit(hand, toMiddle, out float x, out float y, out float d), Is.True);
        Assert.That(x, Is.EqualTo(XrPanel.PixelWidth / 2f).Within(0.5));
        Assert.That(y, Is.EqualTo(XrPanel.PixelHeight / 2f).Within(0.5));
        Assert.That(d, Is.EqualTo((panel.Center - hand).Length()).Within(1e-3));

        // At the top left corner and the bottom right one
        var c = panel.Corners();
        Assert.That(panel.Hit(hand, Vector3.Normalize(c.TopLeft - hand), out x, out y, out _), Is.True);
        Assert.That((x, y), Is.EqualTo((0f, 0f)).Within(0.5));
        Assert.That(panel.Hit(hand, Vector3.Normalize(c.BottomRight - hand), out x, out y, out _), Is.True);
        Assert.That(x, Is.EqualTo(XrPanel.PixelWidth).Within(0.5));
        Assert.That(y, Is.EqualTo(XrPanel.PixelHeight).Within(0.5));

        // A quarter of the way across, a quarter down: pixels a quarter along
        var spot = c.TopLeft + (c.TopRight - c.TopLeft) * 0.25f + (c.BottomLeft - c.TopLeft) * 0.25f;
        Assert.That(panel.Hit(hand, Vector3.Normalize(spot - hand), out x, out y, out _), Is.True);
        Assert.That(x, Is.EqualTo(XrPanel.PixelWidth * 0.25f).Within(0.5));
        Assert.That(y, Is.EqualTo(XrPanel.PixelHeight * 0.25f).Within(0.5));
    }

    [Test]
    public void ARayThatMissesPointsAwayOrMeetsAHiddenPanel_HitsNothing()
    {
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        var hand = Head + new Vector3(0.25f, -0.4f, -0.2f);
        var c = panel.Corners();

        Assert.That(panel.Hit(hand, Vector3.Normalize(c.TopRight + Vector3.UnitX * 0.3f - hand), out _, out _, out _), Is.False, "past the right edge");
        Assert.That(panel.Hit(hand, Vector3.Normalize(c.BottomLeft - Vector3.UnitY * 0.3f - hand), out _, out _, out _), Is.False, "below the bottom edge");
        Assert.That(panel.Hit(hand, Vector3.UnitZ, out _, out _, out _), Is.False, "pointing behind");
        Assert.That(panel.Hit(hand, Vector3.UnitX, out _, out _, out _), Is.False, "parallel to the panel");

        panel.SetVisible(false);
        Assert.That(panel.Hit(hand, Vector3.Normalize(panel.Center - hand), out _, out _, out _), Is.False, "a hidden panel is not there");
    }

    [Test]
    public void TheCornersLandWhereTheViewerSeesThem_InClipSpace()
    {
        // The mock device's eye: 90 degrees, looking down -Z from the head
        const float n = 0.05f, f = 3000f;
        var projection = new Matrix4x4(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, -(f + n) / (f - n), -1,
            0, 0, -2 * f * n / (f - n), 0);
        var panel = new XrPanel();
        panel.Update(0.016f, Head, Quaternion.Identity);
        var view = XrMath.ViewFromPose(Head, Quaternion.Identity);
        var c = panel.Corners();
        var tl = XrPanelRenderer.ClipOf(c.TopLeft, view, projection);
        var tr = XrPanelRenderer.ClipOf(c.TopRight, view, projection);
        var bl = XrPanelRenderer.ClipOf(c.BottomLeft, view, projection);
        foreach (var p in new[] { tl, tr, bl })
        {
            Assert.That(p.W, Is.GreaterThan(0), "in front of the eye");
            Assert.That(p.Z / p.W, Is.InRange(0f, 1f), "inside WebGPU's depth range");
        }
        Assert.That(tl.X / tl.W, Is.LessThan(0), "left");
        Assert.That(tr.X / tr.W, Is.GreaterThan(0), "right");
        Assert.That(tl.Y / tl.W, Is.GreaterThan(bl.Y / bl.W), "top above bottom");
        // 1.5 m wide at 1.3 m: the half width on a 90 degree view is 0.75 / 1.3
        Assert.That(tr.X / tr.W, Is.EqualTo(XrPanel.Width / 2 / XrPanel.Distance).Within(1e-3));
    }
}
