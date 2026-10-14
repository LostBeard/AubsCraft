using System.Globalization;
using AubsCraft.Admin.Services;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Rendering;

/// <summary>What the HUD shows, read from the renderer each frame.</summary>
public readonly record struct HudData(
    float Fps, float CamX, float CamY, float CamZ, float YawDegrees,
    int VisibleSections, int TotalSections, int VisibleVertices, int TotalVertices, int DrawDistance,
    int LoadedColumns, float ColumnsPerSecond, float TimeOfDay, string Backend);

/// <summary>
/// The viewer's HUD, drawn by SpawnDev.GameUI on the GPU, in the render worker, in the same command encoder as the world:
/// stats panel, compass, crosshair while the pointer is locked, and a hint while it is not. F3 toggles GameUI's debug
/// overlay. The page has no DOM canvas overlay for any of it.
///
/// The worker has no DOM, so the page forwards the input it sees (<see cref="UiInputEvent"/>) and this class pushes it into
/// GameUI's PushedInputProvider.
/// </summary>
public sealed class ViewerHud
{
    private const string ScreenName = "hud";
    // Text is rebuilt this often, not every frame: formatting six strings 90 times a second is allocation for nothing.
    private const float TextRefreshSeconds = 0.25f;

    private readonly GameUIService _ui;
    private readonly UILabel[] _lines = new UILabel[4];
    private UIAnchorPanel _root = null!;
    private UICompass _compass = null!;
    private UICrosshair _crosshair = null!;
    private UILabel _hint = null!;
    private float _textTimer = TextRefreshSeconds; // fills the first frame
    private bool _pointerLocked;

    public ViewerHud(GameUIService ui) => _ui = ui;

    public bool IsInitialized => _ui.IsInitialized;

    /// <summary>False: the HUD is neither updated nor drawn (a clean frame). Input already queued is dropped with it.</summary>
    public bool Visible { get; set; } = true;

    public void Init(GPUDevice device, GPUQueue queue, string canvasFormat, int width, int height)
    {
        UITheme.Current = UITheme.AubsCraft;
        _ui.Init(device, queue, canvasFormat, width, height);
        BuildScreen(width, height);
        _ui.Screens.Push(ScreenName);
    }

    public void SetViewport(int width, int height)
    {
        if (!_ui.IsInitialized || width <= 0 || height <= 0) return;
        _ui.SetViewport(width, height);
    }

    private void BuildScreen(int width, int height)
    {
        var theme = UITheme.Current;
        _root = new UIAnchorPanel { Width = width, Height = height };

        const float pad = 8f;
        float lineH = _ui.Renderer.GetLineHeight(FontSize.Caption);
        var stats = new UIPanel { Width = 270, Height = pad * 2 + lineH * _lines.Length + 2 * (_lines.Length - 1) };
        for (int i = 0; i < _lines.Length; i++)
        {
            _lines[i] = new UILabel { FontSize = FontSize.Caption, Color = theme.TextPrimary, X = pad, Y = pad + i * (lineH + 2) };
            stats.AddChild(_lines[i]);
        }
        _root.AddAnchored(stats, Anchor.TopLeft, 8, 8);
        // GameUI's F3 overlay defaults to the top-left corner, where the stats panel is: put it right under it
        UIDebugOverlay.Instance.X = 8;
        UIDebugOverlay.Instance.Y = 8 + stats.Height + 8;

        _compass = new UICompass { Width = 420, Height = 28 };
        _root.AddAnchored(_compass, Anchor.TopCenter, 0, 8);

        _crosshair = new UICrosshair { Visible = false };
        _root.AddAnchored(_crosshair, Anchor.Center);

        _hint = new UILabel
        {
            Text = "Click the view to fly   WASD move   Space / Shift up / down   Esc release   F3 debug",
            FontSize = FontSize.Caption,
            Color = theme.TextPrimary,
            OutlineWidth = 0.12f,
        };
        _root.AddAnchored(_hint, Anchor.BottomCenter, 0, -10);

        _ui.Screens.Register(ScreenName, _root);
    }

    /// <summary>The camera took or gave back the pointer.</summary>
    public void SetPointerLocked(bool locked)
    {
        _pointerLocked = locked;
        if (_ui.Pushed is not { } input) return;
        // Locked: the mouse belongs to the camera, so nothing in the HUD hovers or clicks under it. Whatever was held when
        // the lock began (the click that asked for it) never gets its release, so cancel it.
        input.PointerActive = !locked;
        if (locked) input.ReleaseAll();
    }

    /// <summary>Push one input event the page forwarded.</summary>
    public void Apply(UiInputEvent e)
    {
        // Hidden: nothing polls the input, so do not let typed text and wheel deltas pile up behind it
        if (!Visible || _ui.Pushed is not { } input) return;
        switch (e.Kind)
        {
            case UiInputEvent.MouseMove: input.MouseMove(e.X, e.Y); break;
            case UiInputEvent.MouseDown: input.MouseDown((int)e.X); break;
            case UiInputEvent.MouseUp: input.MouseUp((int)e.X); break;
            case UiInputEvent.Wheel: input.Wheel(e.Y); break;
            case UiInputEvent.KeyDown: if (e.Code != null) input.KeyDown(e.Code, e.Text); break;
            case UiInputEvent.KeyUp: if (e.Code != null) input.KeyUp(e.Code); break;
            case UiInputEvent.ReleaseAll: input.ReleaseAll(); break;
        }
    }

    /// <summary>Once per frame, before the world is drawn.</summary>
    public void Update(float dt, in HudData d)
    {
        if (!_ui.IsInitialized || !Visible) return;

        // GameUI's compass: 0 = north. The camera's yaw -90 looks down -Z, which is north.
        _compass.Bearing = d.YawDegrees + 90f;
        _crosshair.Visible = _pointerLocked;
        _hint.Visible = !_pointerLocked;

        _textTimer += dt;
        if (_textTimer >= TextRefreshSeconds)
        {
            _textTimer = 0;
            RefreshText(d);
        }

        _ui.Update(dt);

        if (UIDebugOverlay.IsVisible)
        {
            UIDebugOverlay.Text($"Backend: {d.Backend}");
            UIDebugOverlay.Text(string.Create(CultureInfo.InvariantCulture,
                $"Sections: {d.VisibleSections} drawn / {d.TotalSections} resident"));
            UIDebugOverlay.Text(string.Create(CultureInfo.InvariantCulture,
                $"Vertices: {d.VisibleVertices:N0} drawn / {d.TotalVertices:N0} resident"));
            UIDebugOverlay.Text(string.Create(CultureInfo.InvariantCulture,
                $"Columns streamed: {d.LoadedColumns} ({d.ColumnsPerSecond:F1}/s)"));
            UIDebugOverlay.Text($"Pointer: {(_pointerLocked ? "locked to the camera" : "on the HUD")}");
        }
    }

    private void RefreshText(in HudData d)
    {
        int minutes = (int)(d.TimeOfDay * 1440f) % 1440; // TimeOfDay: 0 = midnight, 0.5 = noon
        _lines[0].Text = string.Create(CultureInfo.InvariantCulture, $"{d.Fps:F0} FPS     {minutes / 60:D2}:{minutes % 60:D2}");
        _lines[1].Text = string.Create(CultureInfo.InvariantCulture, $"XYZ  {d.CamX:F0}  {d.CamY:F0}  {d.CamZ:F0}");
        _lines[2].Text = string.Create(CultureInfo.InvariantCulture,
            $"Sections {d.VisibleSections}/{d.TotalSections}   Verts {d.VisibleVertices / 1000}K/{d.TotalVertices / 1000}K");
        _lines[3].Text = string.Create(CultureInfo.InvariantCulture, $"Draw distance {d.DrawDistance}   Streaming {d.ColumnsPerSecond:F1}/s");
    }

    /// <summary>Once per frame, after the world pass ended: the HUD goes on top of it in the same encoder.</summary>
    public void Draw(GPUCommandEncoder encoder, GPUTextureView target, int width, int height)
    {
        if (!_ui.IsInitialized || !Visible) return;
        _ui.BeginRender(width, height);
        _ui.Screens.Draw(_ui.Renderer);
        _ui.EndRender(encoder, target);
    }
}
