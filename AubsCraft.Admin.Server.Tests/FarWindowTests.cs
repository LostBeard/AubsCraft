using AubsCraft.Admin.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Terrain past the fog is the fog colour and costs draws: how far the world is drawn and meshed follows the fog distance.</summary>
public class FarWindowTests
{
    [Test]
    public void FogEndChunks_IsTheSmallestWholeNumberOfChunksThatReachesPastTheFog()
    {
        // A chunk at index distance d is at least 16 d - 16 sqrt(2) blocks from the camera: 24 covers its half diagonal and the camera's
        // offset inside its own chunk. At FogEndChunks the nearest corner is past the fog end; one chunk fewer would drop visible terrain.
        float need = DayLighting.FogEnd + 24f;
        Assert.That(DayLighting.FogEndChunks * 16f, Is.GreaterThanOrEqualTo(need));
        Assert.That((DayLighting.FogEndChunks - 1) * 16f, Is.LessThan(need), "no larger than it has to be");
    }

    [Test]
    public void TheDrawRadius_FollowsTheRequest_ButNeverPastTheFog()
    {
        Assert.That(FarWindow.CullRadius(12), Is.EqualTo(12));
        Assert.That(FarWindow.CullRadius(DayLighting.FogEndChunks), Is.EqualTo(DayLighting.FogEndChunks));
        Assert.That(FarWindow.CullRadius(80), Is.EqualTo(DayLighting.FogEndChunks), "80 chunks is 1280 blocks, 830 of them invisible fog");
        Assert.That(FarWindow.CullRadius(0), Is.EqualTo(1));
    }

    [Test]
    public void MeshesAreMadeSlightlyBeyondTheDrawRadius_AndGivenUpWellBeyondThat()
    {
        // Hysteresis: a chunk that just got a mesh is not freed by the next small move
        Assert.That(FarWindow.MeshRadiusSq(30), Is.EqualTo(32L * 32));
        Assert.That(FarWindow.FreeRadiusSq(30), Is.EqualTo(36L * 36));
        Assert.That(FarWindow.FreeRadiusSq(12), Is.GreaterThan(FarWindow.MeshRadiusSq(12)));
        Assert.That(FarWindow.FreeMargin, Is.GreaterThan(FarWindow.MeshMargin));
    }

    [Test]
    public void ShouldMeshAndShouldFree_AreExactAtTheirBorders_AndNeverBothTrue()
    {
        int dd = 20; // draw radius 20: mesh within 22, free beyond 26
        Assert.That(FarWindow.ShouldMesh(22, 0, 0, 0, dd), Is.True);
        Assert.That(FarWindow.ShouldMesh(23, 0, 0, 0, dd), Is.False);
        Assert.That(FarWindow.ShouldFree(26, 0, 0, 0, dd), Is.False);
        Assert.That(FarWindow.ShouldFree(27, 0, 0, 0, dd), Is.True);
        // The camera is not at the origin: distance is to its chunk, in any direction
        Assert.That(FarWindow.ShouldMesh(-100 + 22, 50, -100, 50, dd), Is.True);
        Assert.That(FarWindow.ShouldMesh(15, 15, 0, 0, dd), Is.True, "(15,15) is 21.2 chunks away");
        Assert.That(FarWindow.ShouldMesh(16, 16, 0, 0, dd), Is.False, "(16,16) is 22.6");
        for (int d = 0; d < 60; d++)
            Assert.That(FarWindow.ShouldMesh(d, 0, 0, 0, dd) && FarWindow.ShouldFree(d, 0, 0, 0, dd), Is.False, $"distance {d}");
    }

    [Test]
    public void TheAdaptivePolicy_StepsOneChunk_AndHoldsBetweenItsThresholds()
    {
        Assert.That(FarWindow.NextDrawDistance(20, 60f), Is.EqualTo(21), "at the display's rate: grow");
        Assert.That(FarWindow.NextDrawDistance(20, FarWindow.GrowAtFps), Is.EqualTo(21));
        Assert.That(FarWindow.NextDrawDistance(20, 42f), Is.EqualTo(19), "below 48: shrink one chunk");
        Assert.That(FarWindow.NextDrawDistance(20, 50f), Is.EqualTo(20), "between: hold, so it does not hunt");
        Assert.That(FarWindow.NextDrawDistance(20, FarWindow.ShrinkBelowFps), Is.EqualTo(20));
        Assert.That(FarWindow.NextDrawDistance(FarWindow.MaxDrawDistance, 240f), Is.EqualTo(FarWindow.MaxDrawDistance), "never past the fog");
        Assert.That(FarWindow.NextDrawDistance(FarWindow.MinDrawDistance, 5f), Is.EqualTo(FarWindow.MinDrawDistance), "never below the floor");
        Assert.That(FarWindow.GrowAtFps, Is.GreaterThan(FarWindow.ShrinkBelowFps), "a gap between the thresholds");
    }

    [Test]
    public void ASlowMachine_SettlesWhereItHoldsTheFrameRate()
    {
        // A GPU that does 42 fps at 30 chunks and gains 2 fps per chunk removed: the policy walks down until it is out of the shrink zone
        int dd = 30;
        for (int step = 0; step < 100; step++)
            dd = FarWindow.NextDrawDistance(dd, 42f + 2f * (30 - dd));
        Assert.That(dd, Is.EqualTo(27), "42 + 2 * 3 = 48: no longer below the threshold");
    }

    [Test]
    public void TheFog_AtTheFullDistance_IsTheOriginalFog()
    {
        Assert.That(FarWindow.FogEndBlocks(FarWindow.MaxDrawDistance), Is.EqualTo(DayLighting.FogEnd));
        Assert.That(FarWindow.FogStartBlocks(FarWindow.MaxDrawDistance), Is.EqualTo(DayLighting.FogStart).Within(0.01f));
    }

    [Test]
    public void TheFog_FollowsTheDrawDistance_AndIsTotalWhereTheTerrainStopsBeingDrawn()
    {
        float last = 0;
        for (int dd = FarWindow.MinDrawDistance; dd <= FarWindow.MaxDrawDistance; dd++)
        {
            float end = FarWindow.FogEndBlocks(dd), start = FarWindow.FogStartBlocks(dd);
            Assert.That(start, Is.LessThan(end), $"dd {dd}");
            Assert.That(end, Is.GreaterThan(last), "a longer draw distance has a longer fog");
            last = end;
            // The nearest block of a chunk at the draw radius is at least radius * 16 - 16 sqrt(2) away: fully fogged there, so a chunk that is
            // not drawn (and shows the fog colour behind it) and one that is look the same
            float nearest = FarWindow.CullRadius(dd) * 16f - 16f * MathF.Sqrt(2f);
            Assert.That(nearest, Is.GreaterThanOrEqualTo(end), $"dd {dd}: terrain at the radius is still visible through the fog");
        }
    }

    [Test]
    public void ALargeDistanceDoesNotOverflow()
    {
        Assert.That(FarWindow.DistanceSq(1_000_000, 1_000_000, -1_000_000, -1_000_000), Is.EqualTo(8_000_000_000_000L));
    }
}
