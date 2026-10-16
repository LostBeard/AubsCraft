namespace AubsCraft.Admin.Services;

/// <summary>
/// One input event the main thread forwards to the render worker's GameUI. The worker owns the canvas but has no DOM, so
/// the page listens and sends what it saw (batched, once per input tick); the worker pushes them into GameUI's
/// PushedInputProvider. <see cref="Kind"/> is an int, not an enum, so the record crosses the worker boundary as plain data.
/// </summary>
/// <param name="Kind">One of the constants below.</param>
/// <param name="X">MouseMove: canvas-relative X. MouseDown/MouseUp: the button (0 primary, 1 middle, 2 secondary).</param>
/// <param name="Y">MouseMove: canvas-relative Y. Wheel: DOM wheel deltaY.</param>
/// <param name="Code">KeyDown/KeyUp: DOM KeyboardEvent.code. KeyDown also carries the modifiers held in <paramref name="X"/>: 1 Ctrl, 2 Shift, 4 Alt.</param>
/// <param name="Text">KeyDown: the character the key types, or null when it types nothing.</param>
public record UiInputEvent(int Kind, float X = 0, float Y = 0, string? Code = null, string? Text = null)
{
    public const int MouseMove = 0;
    public const int MouseDown = 1;
    public const int MouseUp = 2;
    public const int Wheel = 3;
    public const int KeyDown = 4;
    public const int KeyUp = 5;
    /// <summary>The canvas lost focus: the matching up events will never arrive, so let go of everything.</summary>
    public const int ReleaseAll = 6;
    /// <summary>A mouse button pressed / released / the wheel turned while the camera holds the pointer (the HUD gets none of these): the world's, for building. X is the button (0 primary, 1 middle, 2 secondary); a wheel's Y is the DOM deltaY.</summary>
    public const int WorldMouseDown = 7;
    public const int WorldMouseUp = 8;
    public const int WorldWheel = 9;

    public const int Ctrl = 1, Shift = 2, Alt = 4;
}
