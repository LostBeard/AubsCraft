using System.Numerics;
using AubsCraft.Admin.Rendering;
using AubsCraft.Assets.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>What the sky is told each frame: how much day and night, the colour overhead, the moon's phase, the clouds' drift, and the view turned into directions.</summary>
public class SkyLayerTests
{
    private static ViewSetup View() => ViewSetup.Of(new FpsCamera { Position = new Vector3(100, 70, -20), Yaw = 30, Pitch = 10 }, 1.6f);

    private static SkyAssets Assets() => new() { AtlasWidth = 384, AtlasHeight = 256, Sun = [256, 0, 32, 32], Moon = [256, 32, 32, 32], Clouds = [0, 0, 256, 256] };

    private static SpawnDev.VoxelEngine.Rendering.SkyUniforms Of(float time, float rain = 0f, float thunder = 0f, double server = -1, double local = 0, SkyAssets? assets = null)
    {
        var view = View();
        return SkyLayer.Uniforms(in view, time, rain, thunder, DayLighting.At(time, rain, thunder), server, local, assets ?? Assets());
    }

    [Test]
    public void NoonIsDay_MidnightIsNight_AndDuskAndDawnAreBetween()
    {
        Assert.That(Of(0.5f).SunDirection.W, Is.EqualTo(1f).Within(0.01f));
        Assert.That(Of(0.5f).Horizon.W, Is.EqualTo(0f).Within(0.01f));
        Assert.That(Of(0f).SunDirection.W, Is.EqualTo(0f).Within(0.01f));
        Assert.That(Of(0f).Horizon.W, Is.EqualTo(1f).Within(0.01f));
        Assert.That(Of(0.25f).SunDirection.W, Is.InRange(0.1f, 0.9f), "sunrise");
        Assert.That(Of(0.23f).Horizon.W, Is.InRange(0.05f, 0.95f), "stars fade out as the sun comes up");
        Assert.That(Of(0.25f).Horizon.W, Is.LessThan(0.05f), "gone by the time the sun is at the horizon");
        // Night grows monotonically as the sun goes down toward midnight
        float last = -1f;
        foreach (float t in new[] { 0.5f, 0.6f, 0.7f, 0.75f, 0.8f, 0.9f, 1f })
        {
            float n = Of(t).Horizon.W;
            Assert.That(n, Is.GreaterThanOrEqualTo(last - 1e-4f), $"time {t}");
            last = n;
        }
    }

    [Test]
    public void TheZenithIsBlueByDay_DarkAtNight_AndGreyInTheRain()
    {
        var noon = Of(0.5f).Zenith;
        Assert.That(noon.Z, Is.GreaterThan(noon.X + 0.3f), "blue overhead at noon");
        Assert.That(Of(0f).Zenith.Z, Is.LessThan(0.08f), "almost black at midnight");
        var rain = Of(0.5f, 1f).Zenith;
        float Spread(Vector4 c) => Math.Max(c.X, Math.Max(c.Y, c.Z)) - Math.Min(c.X, Math.Min(c.Y, c.Z));
        Assert.That(Spread(rain), Is.LessThan(Spread(noon) * 0.4f), "grey");
        Assert.That(Of(0.5f, 1f, 1f).Zenith.Z, Is.LessThan(rain.Z), "a storm is darker");
    }

    [Test]
    public void TheMoonsPhase_IsTheServersDayNumber_EightToACycle()
    {
        foreach (int day in new[] { 0, 1, 3, 7, 8, 11, 16 })
            Assert.That(Of(0f, server: day * 24000.0 / 20.0 + 100.0).Zenith.W, Is.EqualTo(day % 8), $"day {day}");
        Assert.That(Of(0f, server: -1).Zenith.W, Is.EqualTo(0f), "no server clock: the first phase");
    }

    [Test]
    public void TheClouds_DriftWithTheServersClock_AndWrapAtTheMapsWidth_AndAreAtTheGamesHeight()
    {
        var a = Of(0.5f, server: 0).Cloud;
        var b = Of(0.5f, server: 1).Cloud;   // a second: 20 ticks of 0.36 blocks
        Assert.That(b.Y - a.Y, Is.EqualTo(7.2f).Within(0.01f));
        Assert.That(Of(0.5f, server: 20.0 * 3600).Cloud.Y, Is.InRange(0f, 12f * 256f));
        Assert.That(a.X, Is.EqualTo(SkyLayer.CloudHeight));
        // Without the images there are no clouds (the layer is out of reach)
        var view = View();
        var none = SkyLayer.Uniforms(in view, 0.5f, 0f, 0f, DayLighting.At(0.5f), -1, 0, null);
        Assert.That(none.Cloud.X, Is.GreaterThan(1e8f));
        Assert.That(none.SunRect, Is.EqualTo(Vector4.Zero));
        // Without a server clock the viewer's own drifts them
        Assert.That(Of(0.5f, local: 10).Cloud.Y, Is.EqualTo(72f).Within(0.01f));
    }

    [Test]
    public void TheInverseTurnsAPixelIntoADirection_ForAnyView()
    {
        var view = View();
        var sky = SkyLayer.Uniforms(in view, 0.5f, 0f, 0f, DayLighting.At(0.5f), -1, 0, Assets());
        var product = view.ViewProjectionReversed * sky.InverseViewProjection;
        Assert.That(product.M11, Is.EqualTo(1f).Within(1e-3)); Assert.That(product.M22, Is.EqualTo(1f).Within(1e-3));
        Assert.That(product.M33, Is.EqualTo(1f).Within(1e-3)); Assert.That(product.M44, Is.EqualTo(1f).Within(1e-3));
        Assert.That(sky.Camera.X, Is.EqualTo(view.Position.X)); Assert.That(sky.Camera.Y, Is.EqualTo(view.Position.Y));
        // The point of the screen's middle, one unit out, is straight ahead of the camera
        var cam = new FpsCamera { Position = new Vector3(100, 70, -20), Yaw = 30, Pitch = 10 };
        var ahead = Vector4.Transform(new Vector4(0, 0, 0.02f, 1), sky.InverseViewProjection);
        var dir = Vector3.Normalize(new Vector3(ahead.X, ahead.Y, ahead.Z) / ahead.W - view.Position);
        Assert.That(Vector3.Dot(dir, Vector3.Normalize(cam.Front)), Is.GreaterThan(0.999f));
    }

    [Test]
    public void TheStarsFollowTheSun_TheirTurnIsMinusTheSunsAngle()
        => Assert.That(Of(0.3f).Cloud.W, Is.EqualTo(-0.3f * MathF.Tau).Within(1e-5));
}
