using AubsCraft.Admin.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Loading is paced by how the frames are doing: fast machines load flat out, slow ones keep their frame rate, nothing stalls.</summary>
public class IngestGovernorTests
{
    private static void Frames(IngestGovernor g, double ms, int count)
    {
        for (int i = 0; i < count; i++) g.OnFrame(ms);
    }

    [Test]
    public void FastFrames_GrantNearlyTheMaximumPerFrame()
    {
        var g = new IngestGovernor();
        Frames(g, 5, 50);
        Assert.That(g.TurnsPerFrame, Is.GreaterThan(4.9).And.LessThanOrEqualTo(g.MaxPerFrame));
    }

    [Test]
    public void FramesAtOrPastTheTarget_GrantOnlyTheMinimum_NeverZero()
    {
        var g = new IngestGovernor();
        Frames(g, 40, 60);
        Assert.That(g.TurnsPerFrame, Is.EqualTo(g.MinPerFrame));
        Frames(g, 400, 60); // a terrible machine
        Assert.That(g.TurnsPerFrame, Is.EqualTo(g.MinPerFrame));
        Assert.That(g.MinPerFrame, Is.GreaterThan(0), "loading must always make progress");
    }

    [Test]
    public void TheSlowerTheFrames_TheFewerTheTurns()
    {
        double last = double.MaxValue;
        foreach (double ms in new[] { 4.0, 10, 17, 25, 30, 33, 50 })
        {
            var g = new IngestGovernor();
            Frames(g, ms, 80);
            Assert.That(g.TurnsPerFrame, Is.LessThanOrEqualTo(last), $"{ms} ms frames");
            last = g.TurnsPerFrame;
        }
        // An Intel-class laptop at 37 fps (27 ms) still loads at a useful pace: about one column a frame
        var laptop = new IngestGovernor();
        Frames(laptop, 27, 80);
        Assert.That(laptop.TurnsPerFrame, Is.InRange(0.9, 1.3));
    }

    [Test]
    public async Task ASlowMachine_StillLoadsEverything_AtTheMinimumRate()
    {
        // Frames are counted by Waiting (released synchronously by OnFrame); a waiter's own task finishes a thread hop later
        var g = new IngestGovernor();
        Frames(g, 40, 30);
        var waiters = Enumerable.Range(0, 100).Select(_ => g.WaitTurnAsync(IngestPriority.Near)).ToList();
        int frames = 0;
        while (g.Waiting > 0 && frames < 1000)
        {
            g.OnFrame(40);
            frames++;
        }
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));
        // 100 turns at 0.5 a frame, less what was banked: about 190 frames
        Assert.That(frames, Is.InRange(150, 230));
    }

    [Test]
    public async Task OnAFastMachine_ABurstOfWaitersIsServedInAFewFrames()
    {
        var g = new IngestGovernor();
        Frames(g, 5, 50);
        while (g.Banked >= 1) await g.WaitTurnAsync();   // spend what is banked
        var waiters = Enumerable.Range(0, 30).Select(_ => g.WaitTurnAsync()).ToList();
        int frames = 0;
        while (g.Waiting > 0 && frames < 100) { g.OnFrame(5); frames++; }
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(frames, Is.LessThanOrEqualTo(8), "30 columns at ~5 a frame");
    }

    [Test]
    public async Task NearerWorkGoesFirst()
    {
        var g = new IngestGovernor { MaxPerFrame = 1.2 };
        while (g.Banked >= 1) await g.WaitTurnAsync();
        var remesh = g.WaitTurnAsync(IngestPriority.Remesh);
        var far = g.WaitTurnAsync(IngestPriority.Far);
        var near = g.WaitTurnAsync(IngestPriority.Near);
        g.OnFrame(1); // one turn: 1.2 * (1 - 1/33) = 1.16
        await near.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(far.IsCompleted, Is.False, "one turn only, and the near column has it");
        Assert.That(remesh.IsCompleted, Is.False);
        g.OnFrame(1); // 0.16 left + 1.16 = a second turn: the far chunk, ahead of the earlier-queued re-mesh
        await far.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(remesh.IsCompleted, Is.False, "0.33 banked after the second turn: not a whole turn yet");
        g.OnFrame(1); // 0.33 + 1.16 = 1.49
        await remesh.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task AnIdleSpell_DoesNotBuyABurstBeyondTheBank()
    {
        var g = new IngestGovernor();
        Frames(g, 4, 500);
        Assert.That(g.Banked, Is.LessThanOrEqualTo(g.MaxBanked));
        int immediate = 0;
        for (int i = 0; i < 30; i++)
        {
            var t = g.WaitTurnAsync();
            if (t.IsCompleted) immediate++;
        }
        Assert.That(immediate, Is.LessThanOrEqualTo((int)g.MaxBanked));
        for (int i = 0; i < 50; i++) g.OnFrame(4);
        await Task.CompletedTask;
    }

    [Test]
    public void ATurnIsImmediate_WhenOneIsBankedAndNobodyIsWaiting()
    {
        var g = new IngestGovernor();
        Assert.That(g.WaitTurnAsync().IsCompleted, Is.True, "the first columns do not wait for the first frame");
    }

    [Test]
    public async Task WhenFramesStop_NobodyWaitsForeverForThem()
    {
        var g = new IngestGovernor { StallMs = 40 };
        while (g.Banked >= 1) await g.WaitTurnAsync();
        // No frame will ever come: the waiter must still go, after about StallMs
        await g.WaitTurnAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task WhenFramesAreFlowing_AWaiterIsNotReleasedByTheStallTimeout()
    {
        double now = 0;
        var g = new IngestGovernor(() => now) { StallMs = 30 };
        while (g.Banked >= 1) await g.WaitTurnAsync();
        var waiter = g.WaitTurnAsync();
        now = 10; // a frame was seen 10 ms ago on the fake clock; the real 30 ms delay passes below
        g.OnFrame(100);
        // OnFrame(100) banks 0.5: not a whole turn. The real-time delay fires, but frames are fresh on the fake clock
        await Task.Delay(200);
        Assert.That(waiter.IsCompleted, Is.False, "frames are flowing: the line is long, not stalled");
        now = 10_000; // and now they have stopped
        await waiter.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
