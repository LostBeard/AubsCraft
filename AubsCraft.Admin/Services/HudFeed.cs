namespace AubsCraft.Admin.Services;

/// <summary>
/// What the page's SignalR connection saw since the last push, for the worker's HUD (the worker has no SignalR connection and no
/// DOM). Flat arrays of primitives, on purpose: a record of records does not cross the worker boundary reliably.
/// </summary>
/// <param name="ChatSenders">Sender of each new chat line, in order (parallel to <paramref name="ChatTexts"/>).</param>
/// <param name="ChatTexts">Text of each new chat line.</param>
/// <param name="PlayersChanged">True when <paramref name="Players"/> replaces the list the HUD shows (false: keep it).</param>
/// <param name="Players">Names of the players online on the selected server.</param>
/// <param name="ToastTexts">Short notices to flash (joins, leaves, deaths, advancements).</param>
/// <param name="ToastKinds">The style of each toast: see <see cref="HudToastKind"/>.</param>
public record HudFeed(
    string[] ChatSenders, string[] ChatTexts,
    bool PlayersChanged, string[] Players,
    string[] ToastTexts, int[] ToastKinds)
{
    public static HudFeed Empty { get; } = new([], [], false, [], [], []);

    public bool IsEmpty => ChatTexts.Length == 0 && !PlayersChanged && ToastTexts.Length == 0;
}

/// <summary>The style of a HUD toast (they map to GameUI's notification types).</summary>
public static class HudToastKind
{
    public const int Info = 0, Success = 1, Warning = 2, Danger = 3, Achievement = 4;
}
