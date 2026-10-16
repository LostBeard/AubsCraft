using System.Numerics;
using AubsCraft.Admin.Xr;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The world as a model on a table: where its parts stand in the room, and that what is done to the model (turn, pan, zoom) keeps the model coherent.</summary>
public class XrTabletopTests
{
    private static void Near(Vector3 a, Vector3 b, float tol = 1e-3f) => Assert.That((a - b).Length(), Is.LessThan(tol), $"{a} vs {b}");

    private static XrTabletop Table() => new() { Focus = new Vector3(1000, 70, -2000), Anchor = new Vector3(0.1f, 0.8f, -0.6f) };

    [Test]
    public void TheFocusStandsAtTheAnchor_AndTheWorldIsShrunkAroundIt()
    {
        var t = Table();
        t.SetScale(1f / 32f);
        Near(t.RoomPoint(t.Focus), t.Anchor);
        // 32 blocks east of the focus is a metre east of the anchor; 16 up is half a metre up
        Near(t.RoomPoint(t.Focus + new Vector3(32, 0, 0)), t.Anchor + new Vector3(1, 0, 0));
        Near(t.RoomPoint(t.Focus + new Vector3(0, 16, 0)), t.Anchor + new Vector3(0, 0.5f, 0));
    }

    [Test]
    public void RoomAndWorldPointsAreEachOthersInverse_WhateverTheTurnAndScale()
    {
        var t = Table();
        foreach (float scale in new[] { 1f / 256f, 1f / 32f, 0.1f, 0.25f })
            foreach (float yaw in new[] { 0f, 0.7f, -2.4f, 3.1f })
            {
                t.SetScale(scale); t.Yaw = yaw;
                var world = new Vector3(1013.5f, 66.25f, -1980f);
                Near(t.WorldPoint(t.RoomPoint(world)), world, 1e-2f);
                var room = new Vector3(-0.3f, 1.1f, -0.9f);
                Near(t.RoomPoint(t.WorldPoint(room)), room, 1e-3f);
            }
    }

    [Test]
    public void TheWorldFromRoomMatrixAgreesWithThePointMaps()
    {
        var t = Table();
        t.SetScale(1f / 20f); t.Yaw = 1.2f;
        var room = new Vector3(0.25f, 0.9f, -0.4f);
        Near(Vector3.Transform(room, t.WorldFromRoom), t.WorldPoint(room), 1e-2f);
        // And the eye's world view it makes is the usual one: a point at the anchor is straight ahead of an eye there
        var head = t.Anchor + new Vector3(0, 0.5f, 0.6f);
        var view = XrMath.ViewFromPose(head, Quaternion.Identity);
        var eyeView = XrMath.EyeView(view, t.WorldFromRoom);
        var inEye = Vector3.Transform(t.Focus, eyeView);
        Assert.That(inEye.Z, Is.LessThan(0), "in front of the eye (it looks down -Z)");
    }

    [Test]
    public void ATurnSpinsTheModelAboutItsMiddle()
    {
        var t = Table();
        t.SetScale(1f / 32f);
        var east = t.Focus + new Vector3(32, 0, 0);
        t.Yaw = MathF.PI / 2;   // a quarter turn left: what was east is now north (-Z in the room)
        Near(t.RoomPoint(east), t.Anchor + new Vector3(0, 0, -1), 1e-3f);
        Near(t.RoomPoint(t.Focus), t.Anchor);
    }

    [Test]
    public void ZoomIsClamped_AndLeavesTheMiddleWhereItWas()
    {
        var t = Table();
        t.ZoomBy(1000);
        Assert.That(t.Scale, Is.EqualTo(XrTabletop.MaxScale));
        t.ZoomBy(1e-9f);
        Assert.That(t.Scale, Is.EqualTo(XrTabletop.MinScale));
        t.SetScale(1f / 32f);
        t.ZoomBy(2);
        Assert.That(t.Scale, Is.EqualTo(1f / 16f).Within(1e-6));
        Near(t.RoomPoint(t.Focus), t.Anchor);
    }

    [Test]
    public void PanningSlidesTheWorldUnderTheModel_InTheTablesOwnDirections()
    {
        var t = Table();
        t.SetScale(1f / 32f);
        var before = t.Focus;
        t.Pan(0.5f, 0);   // half a metre along the table's right: 16 blocks of world
        Near(t.Focus - before, new Vector3(16, 0, 0), 1e-3f);
        // The point that was half a metre right of the middle is at the middle now
        Near(t.RoomPoint(before + new Vector3(16, 0, 0)), t.Anchor, 1e-3f);

        // Turned a quarter, "right" on the table is another way in the world, but still half a metre on the table
        t.Yaw = MathF.PI / 2;
        var f0 = t.Focus;
        t.Pan(0.5f, 0);
        Assert.That((t.Focus - f0).Length(), Is.EqualTo(16f).Within(1e-3));
        Near(t.RoomPoint(f0 + (t.Focus - f0)), t.Anchor, 1e-3f);
    }

    [Test]
    public void TheModelStandsInFrontOfTheHead_BelowTheEyes()
    {
        var t = new XrTabletop();
        t.PlaceBeforeHead(new Vector3(0, 1.7f, 0), Quaternion.Identity);
        Near(t.Anchor, new Vector3(0, 0.95f, -0.7f));
        // Looking left (a quarter turn), it is to the left
        t.PlaceBeforeHead(new Vector3(0, 1.7f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        Near(t.Anchor, new Vector3(-0.7f, 0.95f, 0), 1e-3f);
        // Looking straight up: still somewhere sensible
        t.PlaceBeforeHead(new Vector3(0, 1.7f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2));
        Assert.That(float.IsNaN(t.Anchor.X), Is.False);
    }

    [Test]
    public void TheChunksToDrawFillTheTable()
    {
        var t = new XrTabletop();
        t.SetScale(1f / 32f);   // the table reaches 0.6 m = 19 blocks: 2 chunks
        Assert.That(t.ChunkRadius, Is.EqualTo(2));
        t.SetScale(1f / 128f);  // 77 blocks: 5 chunks
        Assert.That(t.ChunkRadius, Is.EqualTo(5));
        t.SetScale(0.25f);
        Assert.That(t.ChunkRadius, Is.EqualTo(1));
    }
}
