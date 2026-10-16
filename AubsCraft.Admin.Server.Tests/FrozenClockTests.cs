using AubsCraft.Live.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The feed's clock must tick: the viewer interpolates and ages the creatures by it. The plugin sends the world's game time (which ticks always), not its day time (which stands still when the day cycle is off).</summary>
public class FrozenClockTests
{
    private static LiveFrame Frame(long fullTime, float x) => new()
    {
        FullTime = fullTime, DayTime = 6000,
        Worlds = [new LiveWorld { Name = "w", Entities = [new LiveEntity(1, "pig", x, 64, 0, 0, 0, 0, LiveFlags.OnGround, 0, 0, 0, null, null)], Players = System.Array.Empty<LivePlayer>() }],
    };

    [Test]
    public void ACreatureMovesSmoothly_WhenTheClockTicks()
    {
        var state = new LiveWorldState();
        double t = 0;
        for (int i = 0; i < 20; i++) { state.Apply(Frame(1000 + i * 4, i * 0.5f), t); t += 0.2; }
        // Shown a little in the past, between two frames: not at the position of one of them
        var xs = new List<float>();
        for (int k = 0; k < 10; k++) xs.Add(state.Sample("w", t + k * 0.02).Single().X);
        Assert.That(xs.Distinct().Count(), Is.GreaterThan(5), "its position changes frame after frame, between the feed's steps");
    }

    [Test]
    public void WithAClockThatStandsStill_TheCreaturesStillMoveBetweenFrames()
    {
        // What the viewer got from a Paper server whose day cycle was off, before the plugin sent the game time: the same clock in every frame. The frames are then taken a real frame apart
        var state = new LiveWorldState();
        double t = 0;
        for (int i = 0; i < 20; i++) { state.Apply(Frame(1000, i * 0.5f), t); t += 0.2; }
        var xs = new List<float>();
        for (int k = 0; k < 10; k++) xs.Add(state.Sample("w", t + k * 0.02).Single().X);
        Assert.That(xs.Distinct().Count(), Is.GreaterThan(5), "its position changes between the feed's steps all the same");
    }

    [Test]
    public void AndACreatureThatLeavesTheFrames_IsGoneAfterAWhile_ThoughTheClockDoesNotMove()
    {
        var state = new LiveWorldState();
        double t = 0;
        for (int i = 0; i < 10; i++) { state.Apply(Frame(1000, i * 0.5f), t); t += 0.2; }
        var empty = new LiveFrame { FullTime = 1000, DayTime = 6000, Worlds = [new LiveWorld { Name = "w", Entities = System.Array.Empty<LiveEntity>(), Players = System.Array.Empty<LivePlayer>() }] };
        for (int i = 0; i < 20; i++) { state.Apply(empty, t); t += 0.2; }
        Assert.That(state.Sample("w", t).Count(), Is.Zero, "gone after its second and a half, by the frames' real time");
    }

    [Test]
    public void AClockThatTicksAgain_TakesOverAtOnce_AndAnOldFrameIsStillIgnored()
    {
        var state = new LiveWorldState();
        double t = 0;
        for (int i = 0; i < 5; i++) { state.Apply(Frame(1000, i), t); t += 0.2; }            // standing
        state.Apply(Frame(5000, 9f), t); t += 0.2;                                          // the day cycle is on again: 4,000 ticks on
        double latest = state.LatestServerTime;
        Assert.That(latest, Is.EqualTo(250.0).Within(0.01));
        state.Apply(Frame(100, 99f), t);                                                    // an old frame that overtook the others
        Assert.That(state.LatestServerTime, Is.EqualTo(latest));
    }
}
