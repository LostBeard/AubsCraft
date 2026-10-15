using AubsCraft.Live.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Following a player: where the camera stands and which way it looks, and finding the player by name in the live state.</summary>
public class FollowCameraTests
{
    private static LiveEntityView Player(float x, float y, float z, float yaw, float pitch = 0, int flags = LiveFlags.OnGround | LiveFlags.Player) =>
        new(1, "player", x, y, z, yaw, pitch, yaw, flags, 0f, 0f, "Steve", null);

    // Minecraft yaw: 0 faces south (+Z), 90 west (-X), 180 north (-Z), -90 east (+X). The viewer's Front for yaw c is (cos c, ., sin c).
    private static (float x, float z) Front(float cameraYaw) => (MathF.Cos(cameraYaw * MathF.PI / 180f), MathF.Sin(cameraYaw * MathF.PI / 180f));

    [TestCase(0f, 0f, 1f)]
    [TestCase(90f, -1f, 0f)]
    [TestCase(180f, 0f, -1f)]
    [TestCase(-90f, 1f, 0f)]
    public void TheCameraYaw_FacesWhereThePlayerFaces(float minecraftYaw, float dirX, float dirZ)
    {
        var (fx, fz) = Front(FollowCamera.CameraYawOf(minecraftYaw));
        Assert.That(fx, Is.EqualTo(dirX).Within(1e-5));
        Assert.That(fz, Is.EqualTo(dirZ).Within(1e-5));
    }

    [Test]
    public void EyesMode_IsAtTheEyesLookingWhereThePlayerLooks()
    {
        var p = Player(10, 64, 20, yaw: 180, pitch: 30);   // facing north, looking 30 degrees down
        var c = FollowCamera.Place(p, FollowMode.Eyes, yaw: 1234, pitch: 5, distance: 99);
        Assert.That((c.X, c.Y, c.Z), Is.EqualTo((10f, 64f + FollowCamera.StandingEyeHeight, 20f)));
        Assert.That(c.Yaw, Is.EqualTo(270f).Within(1e-4));   // -Z
        Assert.That(c.Pitch, Is.EqualTo(-30f).Within(1e-4)); // down is negative for the viewer's camera
    }

    [Test]
    public void ASneakingPlayer_HasLowerEyes()
    {
        var c = FollowCamera.Place(Player(0, 64, 0, 0, flags: LiveFlags.Player | LiveFlags.Sneaking), FollowMode.Eyes, 0, 0, 5);
        Assert.That(c.Y, Is.EqualTo(64f + FollowCamera.SneakingEyeHeight).Within(1e-5));
    }

    [Test]
    public void ChaseMode_StandsTheDistanceBehindTheEye_LookingAtIt()
    {
        var p = Player(0, 64, 0, yaw: 0);
        var (ex, ey, ez) = FollowCamera.Eye(p);
        foreach (var (yaw, pitch, d) in new[] { (90f, -15f, 5f), (0f, 30f, 8f), (200f, -60f, 3f) })
        {
            var c = FollowCamera.Place(p, FollowMode.Chase, yaw, pitch, d);
            float dx = ex - c.X, dy = ey - c.Y, dz = ez - c.Z;
            Assert.That(MathF.Sqrt(dx * dx + dy * dy + dz * dz), Is.EqualTo(d).Within(1e-4), "the distance");
            // The camera's own Front, from where it stands, points at the eye
            float yr = c.Yaw * MathF.PI / 180f, pr = c.Pitch * MathF.PI / 180f;
            var front = (x: MathF.Cos(yr) * MathF.Cos(pr), y: MathF.Sin(pr), z: MathF.Sin(yr) * MathF.Cos(pr));
            Assert.That(front.x * d, Is.EqualTo(dx).Within(1e-3));
            Assert.That(front.y * d, Is.EqualTo(dy).Within(1e-3));
            Assert.That(front.z * d, Is.EqualTo(dz).Within(1e-3));
        }
    }

    [Test]
    public void ChaseMode_LooksDownOnThePlayer_WhenTheCameraIsTippedDown()
    {
        var c = FollowCamera.Place(Player(0, 64, 0, 0), FollowMode.Chase, 90f, -15f, 5f);
        Assert.That(c.Y, Is.GreaterThan(64f + FollowCamera.StandingEyeHeight), "above the eye");
    }

    [Test]
    public void ChaseMode_ClampsTheDistanceAndThePitch()
    {
        var p = Player(0, 64, 0, 0);
        var (ex, ey, ez) = FollowCamera.Eye(p);
        float Dist(float d) { var c = FollowCamera.Place(p, FollowMode.Chase, 0, -10, d); return MathF.Sqrt((c.X - ex) * (c.X - ex) + (c.Y - ey) * (c.Y - ey) + (c.Z - ez) * (c.Z - ez)); }
        Assert.That(Dist(0.1f), Is.EqualTo(FollowCamera.MinDistance).Within(1e-4));
        Assert.That(Dist(500f), Is.EqualTo(FollowCamera.MaxDistance).Within(1e-4));
        Assert.That(FollowCamera.Place(p, FollowMode.Chase, 0, -200, 5).Pitch, Is.EqualTo(-89f));
    }

    [Test]
    public void Behind_PutsTheCameraAtTheBackOfThePlayer()
    {
        // Facing east (+X): behind is to the west
        var p = Player(100, 70, 100, yaw: -90);
        var c = FollowCamera.Behind(p);
        Assert.That(c.X, Is.LessThan(100f - 3f));
        Assert.That(c.Z, Is.EqualTo(100f).Within(1e-3));
        // ...and it looks east, toward the player
        Assert.That(Front(c.Yaw).x, Is.GreaterThan(0.99f));
    }

    // ---- finding the player to follow ----

    private static LiveFrame FrameWith(long fullTime, string world, float x, params LivePlayer[] players) => new()
    {
        FullTime = fullTime, DayTime = 6000, Raining = false, Thundering = false,
        Worlds = [new LiveWorld
        {
            Name = world,
            Entities = players.Select(p => new LiveEntity(p.EntityId, "player", x, 64f, 0f, 0f, 0f, 0f, LiveFlags.Player | LiveFlags.OnGround, 0f, 0f, 0f, null, null)).ToArray(),
            Players = players,
        }],
    };

    private static LivePlayer Steve(int id = 7, string name = "HereticSpawn") => new(id, "uuid" + id, name, "http://x/skin", false, 0, "air", 20f);

    [Test]
    public void APlayer_IsFoundByNameInAnyCase()
    {
        var s = new LiveWorldState();
        s.Apply(FrameWith(0, "world", 0, Steve()), 100.0);
        Assert.That(s.TryFindPlayer("heretickspawn", out _, out _), Is.False);
        Assert.That(s.TryFindPlayer("hereticspawn", out var world, out var id), Is.True);
        Assert.That((world, id), Is.EqualTo(("world", 7)));
        Assert.That(s.TryFindPlayer("Nobody", out _, out _), Is.False);
    }

    [Test]
    public void APlayerInTheNether_IsFoundWithItsWorld()
    {
        var s = new LiveWorldState();
        s.Apply(FrameWith(0, "world_nether", 0, Steve(9, "Aubs")), 100.0);
        Assert.That(s.TryFindPlayer("Aubs", out var world, out var id), Is.True);
        Assert.That((world, id), Is.EqualTo(("world_nether", 9)));
    }

    [Test]
    public void SampleOne_GivesTheSamePlaceAsSample()
    {
        var s = new LiveWorldState();
        for (int i = 0; i < 6; i++) s.Apply(FrameWith(i * 4, "world", i * 1.0f, Steve()), 100 + i * 0.2);
        foreach (double t in new[] { 100.5, 100.7, 101.0, 101.4 })
        {
            var all = s.Sample("world", t).Single();
            var one = s.SampleOne("world", 7, t);
            Assert.That(one, Is.Not.Null, $"t={t}");
            Assert.That(one!.Value.X, Is.EqualTo(all.X).Within(1e-6), $"t={t}");
        }
        Assert.That(s.SampleOne("world", 999, 100.5), Is.Null, "an unknown id");
        Assert.That(s.SampleOne("elsewhere", 7, 100.5), Is.Null, "another world");
    }

    [Test]
    public void APlayerWhoLeft_IsNoLongerFound()
    {
        var s = new LiveWorldState();
        s.Apply(FrameWith(0, "world", 0, Steve()), 100.0);
        for (int i = 1; i < 20; i++)
            s.Apply(new LiveFrame { FullTime = i * 4, DayTime = 6000, Raining = false, Thundering = false, Worlds = [new LiveWorld { Name = "world", Entities = [], Players = [] }] }, 100 + i * 0.2);
        Assert.That(s.TryFindPlayer("HereticSpawn", out _, out _), Is.False);
    }
}
