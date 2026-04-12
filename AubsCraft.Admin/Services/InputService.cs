using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Tracks keyboard and mouse input state using BlazorJS event patterns.
/// Adapted from Lost Spawns InputService.cs.
/// </summary>
public sealed class InputService : IAsyncDisposable
{
    private readonly BlazorJSRuntime _js;
    private Window? _window;

    public HashSet<string> KeysDown { get; } = new(StringComparer.Ordinal);
    public double MouseDeltaX { get; private set; }
    public double MouseDeltaY { get; private set; }
    public bool IsAttached { get; private set; }

    public InputService(BlazorJSRuntime js)
    {
        _js = js;
    }

    public void Attach()
    {
        if (IsAttached) return;
        _window = _js.Get<Window>("window");
        _window.OnKeyDown += OnKeyDown;
        _window.OnKeyUp += OnKeyUp;
        _window.OnMouseMove += OnMouseMove;
        IsAttached = true;
    }

    public void Detach()
    {
        if (!IsAttached || _window == null) return;
        _window.OnKeyDown -= OnKeyDown;
        _window.OnKeyUp -= OnKeyUp;
        _window.OnMouseMove -= OnMouseMove;
        IsAttached = false;
    }

    public (float dx, float dy) ConsumeMouseDelta()
    {
        var result = ((float)MouseDeltaX, (float)MouseDeltaY);
        MouseDeltaX = 0;
        MouseDeltaY = 0;
        return result;
    }

    private void OnKeyDown(KeyboardEvent e)
    {
        KeysDown.Add(e.Code);
    }

    private void OnKeyUp(KeyboardEvent e)
    {
        KeysDown.Remove(e.Code);
    }

    private void OnMouseMove(MouseEvent e)
    {
        MouseDeltaX += e.MovementX;
        MouseDeltaY += e.MovementY;
    }

    public ValueTask DisposeAsync()
    {
        Detach();
        _window?.Dispose();
        return ValueTask.CompletedTask;
    }
}
