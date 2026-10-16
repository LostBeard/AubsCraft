namespace AubsCraft.Admin.Services;

/// <summary>
/// Collects what SignalR delivers while the 3D viewer is open and hands it to the render worker's HUD in batches (<see cref="Take"/>),
/// so a burst of chat is one worker call, not one per line. Not thread-safe: the page is one thread.
/// </summary>
public sealed class HudFeedBuffer
{
    /// <summary>Lines kept between two pushes at most (a flood is cut to the newest).</summary>
    public const int MaxPendingChat = 100;
    public const int MaxPendingToasts = 20;

    private readonly List<(string sender, string text)> _chat = new();
    private readonly List<(string text, int kind)> _toasts = new();
    private string[]? _players;          // the list last given to the HUD
    private string[]? _pendingPlayers;   // a new list not yet taken

    /// <summary>Lines kept for a HUD that starts late (a headset's).</summary>
    public const int MaxRecentChat = 30;
    private readonly List<(string sender, string text)> _recent = new();

    public void AddChat(string playerName, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _chat.Add((playerName ?? "", message));
        if (_chat.Count > MaxPendingChat) _chat.RemoveAt(0);
        _recent.Add((playerName ?? "", message));
        if (_recent.Count > MaxRecentChat) _recent.RemoveAt(0);
    }

    /// <summary>The newest chat lines, oldest first, taken or not.</summary>
    public (string sender, string text)[] RecentChat => _recent.ToArray();

    /// <summary>Turns a server activity event into a notice (or ignores it: chat has its own path, the rest is admin noise).</summary>
    public void AddActivity(ActivityEventDto e)
    {
        switch (e.Type)
        {
            case ActivityEventType.PlayerJoin when !string.IsNullOrEmpty(e.PlayerName):
                AddToast($"{e.PlayerName} joined", HudToastKind.Success); break;
            case ActivityEventType.PlayerLeave when !string.IsNullOrEmpty(e.PlayerName):
                AddToast($"{e.PlayerName} left", HudToastKind.Info); break;
            case ActivityEventType.Death when !string.IsNullOrWhiteSpace(e.Details):
                AddToast(e.Details, HudToastKind.Danger); break;
            case ActivityEventType.Advancement when !string.IsNullOrWhiteSpace(e.Details):
                AddToast(e.Details, HudToastKind.Achievement); break;
        }
    }

    private void AddToast(string text, int kind)
    {
        _toasts.Add((text, kind));
        if (_toasts.Count > MaxPendingToasts) _toasts.RemoveAt(0);
    }

    /// <summary>The players online now. Only a list that differs from the one the HUD has makes it into the next feed.</summary>
    public void SetPlayers(IReadOnlyList<string> players)
    {
        var sorted = players.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var current = _pendingPlayers ?? _players;
        if (current != null && current.SequenceEqual(sorted, StringComparer.Ordinal)) return;
        _pendingPlayers = sorted;
    }

    /// <summary>The players online as last set (taken or not): what a HUD that starts late (a headset's) begins with.</summary>
    public string[]? CurrentPlayers => _pendingPlayers ?? _players;

    /// <summary>Forget everything and make the next player list count as new (a different server, a new viewer).</summary>
    public void Reset()
    {
        _chat.Clear();
        _recent.Clear();
        _toasts.Clear();
        _players = null;
        _pendingPlayers = null;
    }

    public bool HasPending => _chat.Count > 0 || _toasts.Count > 0 || _pendingPlayers != null;

    /// <summary>Everything collected since the last call, and an empty buffer.</summary>
    public HudFeed Take()
    {
        if (!HasPending) return HudFeed.Empty;
        bool playersChanged = _pendingPlayers != null;
        if (playersChanged) _players = _pendingPlayers;
        var feed = new HudFeed(
            _chat.Select(c => c.sender).ToArray(), _chat.Select(c => c.text).ToArray(),
            playersChanged, playersChanged ? _players! : [],
            _toasts.Select(t => t.text).ToArray(), _toasts.Select(t => t.kind).ToArray());
        _chat.Clear();
        _toasts.Clear();
        _pendingPlayers = null;
        return feed;
    }
}
