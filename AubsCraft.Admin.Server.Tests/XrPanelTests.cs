using System.Numerics;
using AubsCraft.Admin.Xr;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>A surface of the headset's UI: where it floats, how it follows the head (or stays), and where a controller's ray meets it.</summary>
public class XrPanelTests
{
    private static readonly Vector3 Head = new(0.1f, 1.7f, 0.2f);

    // A following surface 1.7 x 1.0625 m, 1.4 m away and 12 cm low, that shows an 800 x 500 part of the atlas
    private static readonly XrPanelSpec Follower = new("test", 1.7f, 1.0625f, 1.4f, 0.12f, 0f, true, 100, 50, 800, 500);
    private static readonly XrPanelSpec Fixed = Follower with { Follow = false };

    private static void Near(Vector3 a, Vector3 b, float tol = 1e-3f)
        => Assert.That((a - b).Length(), Is.LessThan(tol), $"{a} vs {b}");

    private static Quaternion Facing(float degreesLeft) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, degreesLeft * MathF.PI / 180f);

    private static XrPanel Shown(XrPanelSpec spec)
    {
        var panel = new XrPanel(spec);
        panel.SetVisible(true);
        return panel;
    }

    [Test]
    public void ItStartsInFrontOfTheHead_AJustBelowTheEyes()
    {
        var panel = Shown(Follower);
        panel.Update(0.016f, Head, Quaternion.Identity);
        Near(panel.Center, Head + new Vector3(0, -Follower.DropBelowEyes, -Follower.Distance));
        Near(panel.Forward, -Vector3.UnitZ);
        Near(panel.Right, Vector3.UnitX);
    }

    [Test]
    public void ItStartsWhereTheHeadLooks_AndItsRightIsTheViewersRight()
    {
        var panel = Shown(Follower);
        // Turned a quarter left from -Z: looking down -X, where the viewer's right is -Z
        panel.Update(0.016f, Head, Facing(90));
        Near(panel.Forward, -Vector3.UnitX);
        Near(panel.Right, -Vector3.UnitZ);
        // Turned a quarter right: looking +X, right is +Z
        var other = Shown(Follower);
        other.Update(0.016f, Head, Facing(-90));
        Near(other.Forward, Vector3.UnitX);
        Near(other.Right, Vector3.UnitZ);
    }

    [Test]
    public void ASideOffset_IsToTheViewersRightOfWhereTheyLook()
    {
        var left = Shown(Follower with { Lateral = -0.3f });
        left.Update(0.016f, Head, Quaternion.Identity);
        Near(left.Center, Head + new Vector3(-0.3f, -Follower.DropBelowEyes, -Follower.Distance));
        // Looking down -X, the viewer's right is -Z: a surface 0.3 m to the right is 0.3 m along -Z
        var right = Shown(Follower with { Lateral = 0.3f });
        right.Update(0.016f, Head, Facing(90));
        Near(right.Center, Head + new Vector3(-Follower.Distance, -Follower.DropBelowEyes, -0.3f));
    }

    [Test]
    public void TheCornersRunAsThePixelsDo_AndTheSizeIsTheSpecs()
    {
        var panel = Shown(Follower);
        panel.Update(0.016f, Head, Quaternion.Identity);
        var c = panel.Corners();
        Assert.That(c.TopLeft.X, Is.LessThan(c.TopRight.X), "left is left as the viewer sees it");
        Assert.That(c.TopLeft.Y, Is.GreaterThan(c.BottomLeft.Y), "the top is up");
        Assert.That((c.TopRight - c.TopLeft).Length(), Is.EqualTo(Follower.WidthMeters).Within(1e-4));
        Assert.That((c.TopLeft - c.BottomLeft).Length(), Is.EqualTo(Follower.HeightMeters).Within(1e-4));
        Near((c.TopLeft + c.BottomRight) / 2, panel.Center);
        Assert.That(panel.PixelWidth, Is.EqualTo(800));
        Assert.That(panel.PixelHeight, Is.EqualTo(500));
    }

    [Test]
    public void TheSurfacesOfTheHud_HaveTheShapeOfTheirPartOfTheAtlas_AndFitInIt()
    {
        foreach (var spec in XrSurfaces.All)
        {
            Assert.That((double)spec.RegionWidth / spec.RegionHeight, Is.EqualTo(spec.WidthMeters / spec.HeightMeters).Within(0.02), spec.Name + " is shaped like its pixels");
            Assert.That(spec.RegionX + spec.RegionWidth, Is.LessThanOrEqualTo(XrSurfaces.AtlasWidth), spec.Name);
            Assert.That(spec.RegionY + spec.RegionHeight, Is.LessThanOrEqualTo(XrSurfaces.AtlasHeight), spec.Name);
        }
        var all = XrSurfaces.All;
        for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
            {
                var a = all[i]; var b = all[j];
                bool apart = a.RegionX + a.RegionWidth <= b.RegionX || b.RegionX + b.RegionWidth <= a.RegionX || a.RegionY + a.RegionHeight <= b.RegionY || b.RegionY + b.RegionHeight <= a.RegionY;
                Assert.That(apart, Is.True, $"{a.Name} and {b.Name} are different parts of the atlas");
            }
    }

    [Test]
    public void AGlanceAside_LeavesItWhereItWas()
    {
        var panel = Shown(Follower);
        panel.Update(0.016f, Head, Quaternion.Identity);
        float start = panel.Heading;
        for (int i = 0; i < 120; i++) panel.Update(0.016f, Head, Facing(Follower.DeadZoneDegrees - 3));
        Assert.That(panel.Heading, Is.EqualTo(start).Within(1e-4), "inside the dead zone the surface stays put");
    }

    [Test]
    public void LookingAwayDragsAFollowerAfterTheHead_UntilTheHeadIsAtTheEdgeOfTheDeadZone()
    {
        var panel = Shown(Follower);
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
        Assert.That(gap * 180 / MathF.PI, Is.EqualTo(Follower.DeadZoneDegrees).Within(0.5), "it settles with the head at the edge of the dead zone");
    }

    [Test]
    public void AFollowerTakesTheShortWayRound()
    {
        var panel = Shown(Follower);
        panel.Update(0.016f, Head, Facing(170));
        for (int i = 0; i < 600; i++) panel.Update(0.016f, Head, Facing(-170));   // 20 degrees apart across the back, not 340
        float gap = MathF.Abs(MathF.Atan2(MathF.Sin(-170 * MathF.PI / 180f - panel.Heading), MathF.Cos(-170 * MathF.PI / 180f - panel.Heading)));
        Assert.That(gap * 180 / MathF.PI, Is.LessThanOrEqualTo(Follower.DeadZoneDegrees + 0.5));
    }

    [Test]
    public void AFollowerKeepsItsDistanceFromTheHead_WhateverTheHeadDoes()
    {
        var panel = Shown(Follower);
        panel.Update(0.016f, Head, Quaternion.Identity);
        var moved = Head + new Vector3(0.5f, 0.1f, 0.4f);
        panel.Update(0.016f, moved, Quaternion.Identity);
        Near(panel.Center - moved, new Vector3(0, -Follower.DropBelowEyes, -Follower.Distance));
    }

    [Test]
    public void AFixedSurface_StaysWhereItAppeared_HoweverTheHeadTurnsOrMoves()
    {
        var panel = Shown(Fixed);
        panel.Update(0.016f, Head, Quaternion.Identity);
        var where = panel.Center;
        float heading = panel.Heading;
        for (int i = 0; i < 300; i++) panel.Update(0.016f, Head + new Vector3(0.3f, 0, 0.3f), Facing(90));
        Near(panel.Center, where);
        Assert.That(panel.Heading, Is.EqualTo(heading).Within(1e-6), "it does not turn after the head");
    }

    [Test]
    public void LookingStraightUp_DoesNotSwingIt()
    {
        var panel = Shown(Follower);
        panel.Update(0.016f, Head, Quaternion.Identity);
        float start = panel.Heading;
        var up = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);   // -Z becomes +Y
        for (int i = 0; i < 100; i++) panel.Update(0.016f, Head, up);
        Assert.That(panel.Heading, Is.EqualTo(start).Within(1e-4), "a head pointing at the ceiling has no heading to follow");
    }

    [Test]
    public void ShownAgain_ItComesBackWhereTheHeadLooks()
    {
        foreach (var spec in new[] { Follower, Fixed })
        {
            var panel = Shown(spec);
            panel.Update(0.016f, Head, Quaternion.Identity);
            panel.SetVisible(false);
            Assert.That(panel.Visible, Is.False);
            panel.SetVisible(true);
            panel.Update(0.016f, Head, Facing(120));
            Near(panel.Forward, new Vector3(-MathF.Sin(120 * MathF.PI / 180f), 0, -MathF.Cos(120 * MathF.PI / 180f)));
        }
    }

    [Test]
    public void ARayFromTheHand_MeetsTheSurfaceAtThePixelItPointsAt()
    {
        var panel = Shown(Follower);
        panel.Update(0.016f, Head, Quaternion.Identity);
        var hand = Head + new Vector3(0.25f, -0.4f, -0.2f);

        // Straight at the middle
        var toMiddle = Vector3.Normalize(panel.Center - hand);
        Assert.That(panel.Hit(hand, toMiddle, out float x, out float y, out float d), Is.True);
        Assert.That(x, Is.EqualTo(panel.PixelWidth / 2f).Within(0.5));
        Assert.That(y, Is.EqualTo(panel.PixelHeight / 2f).Within(0.5));
        Assert.That(d, Is.EqualTo((panel.Center - hand).Length()).Within(1e-3));

        // At the top left corner and the bottom right one
        var c = panel.Corners();
        Assert.That(panel.Hit(hand, Vector3.Normalize(c.TopLeft - hand), out x, out y, out _), Is.True);
        Assert.That((x, y), Is.EqualTo((0f, 0f)).Within(0.5));
        Assert.That(panel.Hit(hand, Vector3.Normalize(c.BottomRight - hand), out x, out y, out _), Is.True);
        Assert.That(x, Is.EqualTo(panel.PixelWidth).Within(0.5));
        Assert.That(y, Is.EqualTo(panel.PixelHeight).Within(0.5));

        // A quarter of the way across, a quarter down: pixels a quarter along
        var spot = c.TopLeft + (c.TopRight - c.TopLeft) * 0.25f + (c.BottomLeft - c.TopLeft) * 0.25f;
        Assert.That(panel.Hit(hand, Vector3.Normalize(spot - hand), out x, out y, out _), Is.True);
        Assert.That(x, Is.EqualTo(panel.PixelWidth * 0.25f).Within(0.5));
        Assert.That(y, Is.EqualTo(panel.PixelHeight * 0.25f).Within(0.5));
    }

    [Test]
    public void ARayThatMissesPointsAwayOrMeetsAHiddenSurface_HitsNothing()
    {
        var panel = Shown(Follower);
        panel.Update(0.016f, Head, Quaternion.Identity);
        var hand = Head + new Vector3(0.25f, -0.4f, -0.2f);
        var c = panel.Corners();

        Assert.That(panel.Hit(hand, Vector3.Normalize(c.TopRight + Vector3.UnitX * 0.3f - hand), out _, out _, out _), Is.False, "past the right edge");
        Assert.That(panel.Hit(hand, Vector3.Normalize(c.BottomLeft - Vector3.UnitY * 0.3f - hand), out _, out _, out _), Is.False, "below the bottom edge");
        Assert.That(panel.Hit(hand, Vector3.UnitZ, out _, out _, out _), Is.False, "pointing behind");
        Assert.That(panel.Hit(hand, Vector3.UnitX, out _, out _, out _), Is.False, "parallel to the surface");

        panel.SetVisible(false);
        Assert.That(panel.Hit(hand, Vector3.Normalize(panel.Center - hand), out _, out _, out _), Is.False, "a hidden surface is not there");
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
        var panel = Shown(Follower);
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
        // 1.7 m wide at 1.4 m: the half width on a 90 degree view is 0.85 / 1.4
        Assert.That(tr.X / tr.W, Is.EqualTo(Follower.WidthMeters / 2 / Follower.Distance).Within(1e-3));
    }

    [Test]
    public void ASurfaceOverAHand_FacesTheViewer_WithItsTextLevel_AndRidesWithTheHand()
    {
        var spec = XrSurfaces.Bar;
        Assert.That(spec.OverLeftHand, Is.True);
        var panel = Shown(spec);
        var hand = new Vector3(-0.25f, 1.3f, -0.2f);
        panel.PlaceOverHand(hand, -Vector3.UnitY, Head);
        // A hand''s breadth above the hand, a little toward the head
        Assert.That(panel.Center.Y, Is.GreaterThan(hand.Y + 0.08f));
        Assert.That((panel.Center - hand).Length(), Is.LessThan(0.25f));
        // Facing the viewer: the normal points at the head; the text runs level
        var toHead = Vector3.Normalize(Head - panel.Center);
        Assert.That(Vector3.Dot(-panel.Forward, toHead), Is.GreaterThan(0.95f), "faces the viewer (a little turned, it is shifted toward the middle of the body)");
        Assert.That(panel.Right.Y, Is.EqualTo(0f).Within(1e-4), "the text is level");
        Assert.That(Vector3.Dot(panel.Right, panel.Up), Is.EqualTo(0f).Within(1e-4));
        // A ray from the head at its middle meets it at the middle pixel
        Assert.That(panel.Hit(Head, Vector3.Normalize(panel.Center - Head), out float x, out float y, out _), Is.True);
        Assert.That(x, Is.EqualTo(spec.RegionWidth / 2f).Within(1f));
        Assert.That(y, Is.EqualTo(spec.RegionHeight / 2f).Within(1f));
        // The hand moves: so does it
        var before = panel.Center;
        panel.PlaceOverHand(hand + new Vector3(0.2f, 0.1f, 0f), -Vector3.UnitY, Head);
        Assert.That((panel.Center - before).Length(), Is.GreaterThan(0.15f));
        // A heading puts a surface level again
        panel.Update(0.016f, Head, Quaternion.Identity);
        Near(panel.Up, Vector3.UnitY);
    }

    [Test]
    public void ASurfaceTakenByItsHandle_FollowsTheRay_FacesTheHead_AndStaysWhereItIsLeft()
    {
        Assert.That(XrSurfaces.Menu.GrabBand, Is.GreaterThan(0));
        Assert.That(XrSurfaces.Window.GrabBand, Is.GreaterThan(0));
        Assert.That(XrSurfaces.Bar.GrabBand, Is.Zero, "the armband is not moved by hand");
        var panel = Shown(Fixed);
        panel.Update(0.016f, Head, Quaternion.Identity);
        var hand = Head + new Vector3(0.3f, -0.4f, -0.2f);
        var ray = Vector3.Normalize(panel.Center - hand);
        Assert.That(panel.Hit(hand, ray, out _, out _, out float d), Is.True);
        panel.BeginGrab(hand, ray, d);
        Assert.That(panel.Grabbed, Is.True);
        var start = panel.Center;
        // The hand moves 0.4 m to the right and the ray follows: the surface goes a good way to the right, at about the same distance
        var moved = hand + new Vector3(0.4f, 0f, 0f);
        panel.UpdateGrab(moved, ray, Head);
        Assert.That(panel.Center.X - start.X, Is.EqualTo(0.4f).Within(1e-3));
        Assert.That((panel.Center - start).Length(), Is.EqualTo(0.4f).Within(1e-3), "it was carried, not turned about");
        // It faces the head
        var toHead = Vector3.Normalize(Head - panel.Center);
        Assert.That(Vector3.Dot(-panel.Forward, toHead), Is.GreaterThan(0.99f));
        // Pushed away: farther along the ray
        var before = panel.Center;
        panel.UpdateGrab(moved, ray, Head, 0.5f);
        Assert.That((panel.Center - moved).Length(), Is.GreaterThan((before - moved).Length() + 0.4f));
        // Let go: it stays, and a head that turns does not take it along (it is fixed)
        panel.EndGrab();
        var left = panel.Center;
        for (int i = 0; i < 100; i++) panel.Update(0.016f, Head, Facing(60));
        Near(panel.Center, left);
    }}
