using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Tracks keyboard and mouse input state using BlazorJS event patterns.
/// Adapted from Lost Spawns InputService.cs.
/// </summary>
public sealed class InputService : IAsyncDisposable
{
    private readonly SpawnJSRuntime _js;
    private Window? _window;

    public HashSet<string> KeysDown { get; } = new(StringComparer.Ordinal);
    public double MouseDeltaX { get; private set; }
    public double MouseDeltaY { get; private set; }
    public bool IsAttached { get; private set; }

    public InputService(SpawnJSRuntime js)
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
        DetachCanvas();
        if (!IsAttached || _window == null) return;
        _window.OnKeyDown -= OnKeyDown;
        _window.OnKeyUp -= OnKeyUp;
        _window.OnMouseMove -= OnMouseMove;
        IsAttached = false;
    }

    // -- Input for the worker's GPU HUD --------------------------------------------------------------------------------
    // The render worker owns the canvas but has no DOM, so this side listens on the canvas and queues what it sees; the page
    // sends the queue to the worker once per input tick (TakeUiEvents).

    private HTMLCanvasElement? _canvas;
    private readonly List<UiInputEvent> _uiEvents = new();

    /// <summary>
    /// The camera holds the pointer lock: the mouse is the camera's, so mouse events are not queued for the HUD (keys still
    /// are). Set by the page when the lock changes.
    /// </summary>
    public bool PointerLocked { get; set; }

    /// <summary>Start queueing the canvas's mouse, wheel, key and blur events for the HUD. The caller keeps ownership of the canvas.</summary>
    public void AttachCanvas(HTMLCanvasElement canvas)
    {
        DetachCanvas();
        _canvas = canvas;
        _canvas.OnMouseMove += OnCanvasMouseMove;
        _canvas.OnMouseDown += OnCanvasMouseDown;
        _canvas.OnMouseUp += OnCanvasMouseUp;
        _canvas.OnWheel += OnCanvasWheel;
        _canvas.OnKeyDown += OnCanvasKeyDown;
        _canvas.OnKeyUp += OnCanvasKeyUp;
        _canvas.OnBlur += OnCanvasBlur;
    }

    public void DetachCanvas()
    {
        if (_canvas == null) return;
        _canvas.OnMouseMove -= OnCanvasMouseMove;
        _canvas.OnMouseDown -= OnCanvasMouseDown;
        _canvas.OnMouseUp -= OnCanvasMouseUp;
        _canvas.OnWheel -= OnCanvasWheel;
        _canvas.OnKeyDown -= OnCanvasKeyDown;
        _canvas.OnKeyUp -= OnCanvasKeyUp;
        _canvas.OnBlur -= OnCanvasBlur;
        _canvas = null;
        _uiEvents.Clear();
    }

    /// <summary>The events queued since the last call (oldest first), or null when there are none.</summary>
    public UiInputEvent[]? TakeUiEvents()
    {
        if (_uiEvents.Count == 0) return null;
        var events = _uiEvents.ToArray();
        _uiEvents.Clear();
        return events;
    }

    /// <summary>Where the mouse last was over the canvas (canvas pixels): a click is checked against the HUD's rectangles with it.</summary>
    public float MouseX { get; private set; }
    public float MouseY { get; private set; }

    private void OnCanvasMouseMove(MouseEvent e)
    {
        if (PointerLocked) return;
        MouseX = (float)e.OffsetX;
        MouseY = (float)e.OffsetY;
        // Only the latest position matters: a run of moves collapses into one
        if (_uiEvents.Count > 0 && _uiEvents[^1].Kind == UiInputEvent.MouseMove)
            _uiEvents[^1] = new UiInputEvent(UiInputEvent.MouseMove, (float)e.OffsetX, (float)e.OffsetY);
        else
            _uiEvents.Add(new UiInputEvent(UiInputEvent.MouseMove, (float)e.OffsetX, (float)e.OffsetY));
    }

    private void OnCanvasMouseDown(MouseEvent e)
    {
        if (!PointerLocked) _uiEvents.Add(new UiInputEvent(UiInputEvent.MouseDown, (int)e.Button));
    }

    private void OnCanvasMouseUp(MouseEvent e)
    {
        if (!PointerLocked) _uiEvents.Add(new UiInputEvent(UiInputEvent.MouseUp, (int)e.Button));
    }

    private void OnCanvasWheel(WheelEvent e)
    {
        if (!PointerLocked) _uiEvents.Add(new UiInputEvent(UiInputEvent.Wheel, 0, (float)e.DeltaY));
    }

    private void OnCanvasKeyDown(KeyboardEvent e)
    {
        // Keys the viewer uses must not ALSO do the browser's job: F3 is "find next" (it opens the find bar and takes focus, so the
        // next key and click never reach the canvas), Space scrolls the page.
        if (e.Code is "F3" or "Space") e.PreventDefault();

        // The character the key types: a single character with no Ctrl/Alt/Meta held
        bool types = e.Key.Length == 1 && !e.CtrlKey && !e.AltKey && !e.MetaKey;
        _uiEvents.Add(new UiInputEvent(UiInputEvent.KeyDown, Code: e.Code, Text: types ? e.Key : null));
    }

    private void OnCanvasKeyUp(KeyboardEvent e) => _uiEvents.Add(new UiInputEvent(UiInputEvent.KeyUp, Code: e.Code));

    private void OnCanvasBlur(FocusEvent e) => _uiEvents.Add(new UiInputEvent(UiInputEvent.ReleaseAll));

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
