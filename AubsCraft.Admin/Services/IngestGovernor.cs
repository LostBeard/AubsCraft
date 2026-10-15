using System.Diagnostics;

namespace AubsCraft.Admin.Services;

/// <summary>Who is waiting for a turn: nearer work goes first.</summary>
public enum IngestPriority
{
    /// <summary>A full-detail column near the camera.</summary>
    Near = 0,
    /// <summary>A far-tier chunk.</summary>
    Far = 1,
    /// <summary>Meshing a column again because a neighbour arrived.</summary>
    Remesh = 2,
}

/// <summary>
/// Paces the work that turns streamed world data into meshes, so loading never takes the frame rate with it.
///
/// Every column or far chunk that arrives costs the browser thread a mesh dispatch (a dozen kernel launches, buffer uploads and a
/// readback), and that is the same thread that draws. On a fast machine the frame hardly notices; on an integrated GPU with a laptop CPU
/// each one is a visible hitch, and a few hundred in a row freeze the view. The governor hands out turns: each rendered frame grants a
/// number of them that falls as the frame time (a moving average) rises towards <see cref="TargetFrameMs"/>, but never below
/// <see cref="MinPerFrame"/>, so loading always makes progress. A strong machine renders in a few ms, gets
/// nearly <see cref="MaxPerFrame"/> turns per frame, and loads as fast as it can fetch.
///
/// If frames stop (a hidden tab) nobody waits longer than <see cref="StallMs"/>: the data is not held back by a render loop that is not running.
/// </summary>
public sealed class IngestGovernor
{
    private readonly Func<double> _nowMs;
    private readonly Queue<TaskCompletionSource>[] _waiting =
    {
        new(), new(), new(),
    };
    private double _tokens;
    private double _lastFrameAt;

    /// <param name="nowMs">A clock in milliseconds (tests pass their own); default: the monotonic clock.</param>
    public IngestGovernor(Func<double>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        _lastFrameAt = _nowMs();
        _tokens = StartingTurns; // the first columns do not wait for the first frame
    }

    /// <summary>The frame time (ms) at which loading slows to <see cref="MinPerFrame"/>: 30 fps.</summary>
    public double TargetFrameMs { get; set; } = 1000.0 / 30;

    /// <summary>Turns granted per frame when frames are instant.</summary>
    public double MaxPerFrame { get; set; } = 6;

    /// <summary>Turns granted per frame when frames are at or past the target: loading slows, it never stops.</summary>
    public double MinPerFrame { get; set; } = 0.5;

    /// <summary>Most turns that can pile up (an idle spell must not buy a burst that freezes the next frame).</summary>
    public double MaxBanked { get; set; } = 8;

    /// <summary>Turns available before the first frame.</summary>
    public double StartingTurns { get; set; } = 6;

    /// <summary>How long a waiter is held when no frame arrives to release it.</summary>
    public int StallMs { get; set; } = 500;

    /// <summary>The moving average of the frame time in ms (0 until the first frame).</summary>
    public double AverageFrameMs { get; private set; }

    /// <summary>Turns the next frame will add at the current average.</summary>
    public double TurnsPerFrame
    {
        get
        {
            if (AverageFrameMs <= 0) return MaxPerFrame;
            double headroom = Math.Clamp((TargetFrameMs - AverageFrameMs) / TargetFrameMs, 0, 1);
            return Math.Max(MinPerFrame, MaxPerFrame * headroom);
        }
    }

    /// <summary>Turns banked right now.</summary>
    public double Banked => _tokens;

    /// <summary>Waiters not yet released.</summary>
    public int Waiting => _waiting.Sum(q => q.Count);

    /// <summary>Call once per rendered frame with the time since the previous frame.</summary>
    public void OnFrame(double frameMs)
    {
        if (frameMs <= 0) return;
        _lastFrameAt = _nowMs();
        AverageFrameMs = AverageFrameMs <= 0 ? frameMs : AverageFrameMs + 0.2 * (frameMs - AverageFrameMs);
        _tokens = Math.Min(_tokens + TurnsPerFrame, MaxBanked);
        Release();
    }

    /// <summary>Completes when it is this caller's turn to start a mesh: nearer work first, then in order of arrival.</summary>
    public async Task WaitTurnAsync(IngestPriority priority = IngestPriority.Near)
    {
        if (Waiting == 0 && _tokens >= 1)
        {
            _tokens -= 1;
            return;
        }
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[(int)priority].Enqueue(turn);
        while (true)
        {
            using var stall = new CancellationTokenSource();
            var winner = await Task.WhenAny(turn.Task, Task.Delay(StallMs, stall.Token));
            stall.Cancel();
            if (winner == turn.Task) break;
            // Nobody released us in StallMs. Frames still flowing: the line is just long, wait on. Frames stopped (a hidden tab
            // renders nothing): go, the world should still load
            if (_nowMs() - _lastFrameAt > StallMs)
            {
                turn.TrySetResult();
                break;
            }
        }
        await turn.Task;
    }

    private void Release()
    {
        while (_tokens >= 1)
        {
            var turn = Dequeue();
            if (turn == null) return;
            if (turn.TrySetResult()) _tokens -= 1; // a waiter that already went (stall) cost no turn
        }
    }

    private TaskCompletionSource? Dequeue()
    {
        foreach (var q in _waiting)
            if (q.Count > 0) return q.Dequeue();
        return null;
    }
}
