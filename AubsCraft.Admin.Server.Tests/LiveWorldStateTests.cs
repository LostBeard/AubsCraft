using AubsCraft.Live.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The live timeline: frames a few times a second in, positions at the display rate out - smooth between snapshots, no sliding across a
/// teleport, the short way round an angle, a vanished entity gone after a while, a late frame not moving the clock.
/// </summary>
public class LiveWorldStateTests
{
    private static LiveEntity Cow(int id, float x, float z, float yaw = 0, int flags = LiveFlags.OnGround, float y = 64f) =>
        new(id, "cow", x, y, z, yaw, 0f, yaw, flags, 0f, 0f, 0f, null, null);

    private static LiveFrame Frame(long fullTime, params LiveEntity[] entities) => new()
    {
        FullTime = fullTime, DayTime = 6000, Raining = false, Thundering = false,
        Worlds = [new LiveWorld { Name = "world", Entities = entities, Players = [] }],
    };

    // Frames every 4 ticks (0.2 s), arriving exactly on time: server time 0 is local time 100
    private static LiveWorldState Walking(float blocksPerFrame, int frames, int id = 1)
    {
        var s = new LiveWorldState();
        for (int i = 0; i < frames; i++)
            s.Apply(Frame(i * 4, Cow(id, i * blocksPerFrame, 0)), 100 + i * 0.2);
        return s;
    }

    [Test]
    public void BetweenTwoSnapshots_ThePositionIsInterpolated()
    {
        var s = Walking(1f, 6); // snapshots at server 0.0 .. 1.0 s, x = 0..5
        // Local 100.9 = server 0.9 (+ delay 0.3 means we show server 0.6 at local 100.9)
        var view = s.Sample("world", 100.9).Single();
        Assert.That(view.X, Is.EqualTo(3.0f).Within(0.01f)); // server 0.6 s = 3 frames' worth = x 3.0
        var half = s.Sample("world", 100.8 + 0.1).Single();
        Assert.That(half.X, Is.EqualTo(3.0f).Within(0.01f));
        var quarter = s.Sample("world", 100.95).Single();
        Assert.That(quarter.X, Is.EqualTo(3.25f).Within(0.01f));
    }

    [Test]
    public void Smooth_ManyDisplayFrames_NeverStepBackwardsOrJump()
    {
        var s = Walking(1f, 8);
        float previous = float.MinValue;
        for (double t = 100.3; t < 101.3; t += 1.0 / 60)
        {
            var views = s.Sample("world", t);
            if (views.Count == 0) continue;
            float x = views[0].X;
            Assert.That(x >= previous - 1e-4f, Is.True, $"x went back at {t}: {previous} -> {x}");
            Assert.That(x - previous < 0.2f || previous == float.MinValue, Is.True, $"x jumped at {t}: {previous} -> {x}");
            previous = x;
        }
        Assert.That(previous > 3f, Is.True);
    }

    [Test]
    public void ATeleport_IsNotASlide()
    {
        var s = new LiveWorldState();
        s.Apply(Frame(0, Cow(1, 0, 0)), 100.0);
        s.Apply(Frame(4, Cow(1, 1000, 0)), 100.2);
        s.Apply(Frame(8, Cow(1, 1001, 0)), 100.4);
        // Between the first two snapshots: at the new place, never somewhere in the 1000 blocks between
        for (double t = 100.3; t < 100.5; t += 0.01)
        {
            foreach (var v in s.Sample("world", t))
                Assert.That(v.X < 1 || v.X > 999, Is.True, $"slid through {v.X} at {t}");
        }
    }

    [Test]
    public void ATeleport_IsNotAWalk()
    {
        var s = new LiveWorldState();
        s.Apply(Frame(0, Cow(1, 0, 0)), 100.0);
        s.Apply(Frame(4, Cow(1, 1000, 0)), 100.2);
        s.Apply(Frame(8, Cow(1, 1001, 0)), 100.4);
        var v = s.Sample("world", 100.75).Single();
        Assert.That(v.WalkedBlocks < 3, Is.True, $"the walk cycle counted the teleport: {v.WalkedBlocks}");
    }

    [Test]
    public void Yaw_TakesTheShortWayRound()
    {
        var s = new LiveWorldState();
        s.Apply(Frame(0, Cow(1, 0, 0, yaw: 170)), 100.0);
        s.Apply(Frame(4, Cow(1, 0, 0, yaw: -170)), 100.2);
        s.Apply(Frame(8, Cow(1, 0, 0, yaw: -170)), 100.4);
        // Halfway between 170 and -170 is 180, not 0
        var v = s.Sample("world", 100.4).Single();
        float d = MathF.Abs(((v.Yaw % 360f) + 360f) % 360f - 180f);
        Assert.That(d < 1f, Is.True, $"yaw {v.Yaw}");
    }

    [Test]
    public void AnEntityMissingFromTheFrames_IsGoneAfterAWhile()
    {
        var s = new LiveWorldState();
        for (int i = 0; i < 4; i++) s.Apply(Frame(i * 4, Cow(1, i, 0), Cow(2, 5, 5)), 100 + i * 0.2);
        // Entity 2 stops appearing; entity 1 carries on
        for (int i = 4; i < 20; i++) s.Apply(Frame(i * 4, Cow(1, i, 0)), 100 + i * 0.2);
        Assert.That(s.TrackCount, Is.EqualTo(1));
        Assert.That(s.Sample("world", 103.5).Any(v => v.Id == 2), Is.False);
        Assert.That(s.Sample("world", 103.5).Any(v => v.Id == 1), Is.True);
    }

    [Test]
    public void AnEntityThatBlinksOutForOneFrame_Stays()
    {
        var s = new LiveWorldState();
        s.Apply(Frame(0, Cow(1, 0, 0)), 100.0);
        s.Apply(Frame(4), 100.2);
        s.Apply(Frame(8, Cow(1, 2, 0)), 100.4);
        Assert.That(s.TrackCount, Is.EqualTo(1));
        Assert.That(s.Sample("world", 100.55).Any(v => v.Id == 1), Is.True);
    }

    [Test]
    public void AnEntityMissingForSeveralFrames_KeepsItsHistoryWhenItReturns()
    {
        var s = new LiveWorldState();
        int tick = 0; double now = 100;
        void Step(params LiveEntity[] e) { s.Apply(Frame(tick, e), now); tick += 4; now += 0.2; }
        for (int i = 0; i < 5; i++) Step(Cow(1, i, 0));        // walks 4 blocks
        for (int i = 0; i < 3; i++) Step();                     // out of the loaded chunks for 0.6 s
        for (int i = 0; i < 4; i++) Step(Cow(1, 10 + i, 0));    // back, walking on
        Assert.That(s.TrackCount, Is.EqualTo(1));
        var v = s.Sample("world", now + 0.1).Single();
        Assert.That(v.WalkedBlocks, Is.GreaterThan(8f), "the walk cycle started over");
    }

    [Test]
    public void ALateFrame_DoesNotMoveTheClock()
    {
        var onTime = Walking(1f, 10);
        var jitter = new LiveWorldState();
        for (int i = 0; i < 10; i++)
        {
            double late = i == 9 ? 0.15 : 0; // the newest frame stuck in the network for 150 ms
            jitter.Apply(Frame(i * 4, Cow(1, i, 0)), 100 + i * 0.2 + late);
        }
        // The clock offset is the best case seen, so the late frame leaves the picture where it was
        foreach (double t in new[] { 101.0, 101.2, 101.5 })
            Assert.That(jitter.Sample("world", t).Single().X, Is.EqualTo(onTime.Sample("world", t).Single().X).Within(0.001f));
    }

    [Test]
    public void AFrameThatOvertookANewerOne_IsIgnored()
    {
        var s = new LiveWorldState();
        s.Apply(Frame(0, Cow(1, 0, 0)), 100.0);
        s.Apply(Frame(8, Cow(1, 2, 0)), 100.4);
        s.Apply(Frame(4, Cow(1, 10, 0)), 100.45); // older, arrived later, and nowhere near the line the others make
        s.Apply(Frame(12, Cow(1, 3, 0)), 100.6);
        // x at server 0.3 s (6 ticks) is 1.5 if the straggler was dropped (0 -> 2 over 0.4 s: 1.5); with it, 1.0 + ... would differ
        var v = s.Sample("world", 100.0 + 0.3 + 0.3).Single();
        Assert.That(v.X, Is.EqualTo(1.5f).Within(0.01f));
    }

    [Test]
    public void WorldsAreSeparate_AndSampledSeparately()
    {
        var s = new LiveWorldState();
        s.Apply(new LiveFrame
        {
            FullTime = 0, DayTime = 0, Raining = false, Thundering = false,
            Worlds =
            [
                new LiveWorld { Name = "world", Entities = [Cow(1, 0, 0)], Players = [] },
                new LiveWorld { Name = "world_nether", Entities = [Cow(1, 50, 50)], Players = [] },
            ],
        }, 100.0);
        Assert.That(s.TrackCount, Is.EqualTo(2)); // the same entity id in two worlds is two entities
        Assert.That(s.Sample("world", 100.5).Single().X, Is.EqualTo(0f));
        Assert.That(s.Sample("world_nether", 100.5).Single().X, Is.EqualTo(50f));
    }

    [Test]
    public void Weather_AndTimeOfDay_FollowTheLatestFrame()
    {
        var s = new LiveWorldState();
        s.Apply(new LiveFrame { FullTime = 0, DayTime = 1000, Raining = false, Thundering = false, Worlds = [] }, 100);
        s.Apply(new LiveFrame { FullTime = 4, DayTime = 13000, Raining = true, Thundering = true, Worlds = [] }, 100.2);
        Assert.That(s.DayTime, Is.EqualTo(13000));
        Assert.That(s.Raining, Is.True);
        Assert.That(s.Thundering, Is.True);
    }

    [Test]
    public void ThePrimaryWorld_IsTheFirstOfTheLatestFrame()
    {
        var s = new LiveWorldState();
        Assert.That(s.PrimaryWorld, Is.Null);
        s.Apply(new LiveFrame
        {
            FullTime = 0, DayTime = 0, Raining = false, Thundering = false,
            Worlds = [new LiveWorld { Name = "Survival", Entities = [], Players = [] }, new LiveWorld { Name = "Survival_nether", Entities = [], Players = [] }],
        }, 100);
        Assert.That(s.PrimaryWorld, Is.EqualTo("Survival"));
    }

    [Test]
    public void Players_AreLookedUpByEntityId()
    {
        var s = new LiveWorldState();
        var player = new LivePlayer(7, "u", "Steve", "http://x/skin", false, 0, "air", 1f);
        s.Apply(new LiveFrame
        {
            FullTime = 0, DayTime = 0, Raining = false, Thundering = false,
            Worlds = [new LiveWorld { Name = "world", Entities = [Cow(7, 0, 0)], Players = [player] }],
        }, 100);
        Assert.That(s.PlayerOf("world", 7)?.Name, Is.EqualTo("Steve"));
        Assert.That(s.PlayerOf("world", 8), Is.Null);
        Assert.That(s.PlayerOf("world_nether", 7), Is.Null);
    }

    [Test]
    public void BeforeTheFirstFrame_NothingIsDrawn()
    {
        Assert.That(new LiveWorldState().Sample("world", 100), Is.Empty);
    }

    [Test]
    public void SpeedAndWalkedDistance_DriveTheWalkCycle()
    {
        var s = Walking(1f, 10);
        var v = s.Sample("world", 101.2).Single();
        Assert.That(v.SpeedBlocksPerTick, Is.EqualTo(1f / 4f).Within(0.001f)); // one block per 4 ticks
        Assert.That(v.WalkedBlocks > 3f, Is.True);

        var still = new LiveWorldState();
        for (int i = 0; i < 10; i++) still.Apply(Frame(i * 4, Cow(1, 5, 5)), 100 + i * 0.2);
        var sv = still.Sample("world", 101.2).Single();
        Assert.That(sv.SpeedBlocksPerTick, Is.EqualTo(0f));
        Assert.That(sv.WalkedBlocks, Is.EqualTo(0f));
    }
}
