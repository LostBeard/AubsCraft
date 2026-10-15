using System.Drawing;
using System.Globalization;
using AubsCraft.Admin.Services;
using AubsCraft.Live.Model;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Rendering;

/// <summary>What the viewer user can ask of a player in the list.</summary>
public enum PlayerAction { GoTo, Follow, Eyes, StopFollowing }

/// <summary>What the HUD shows, read from the renderer each frame.</summary>
public readonly record struct HudData(
    float Fps, float CamX, float CamY, float CamZ, float YawDegrees,
    int VisibleSections, int TotalSections, int VisibleVertices, int TotalVertices, int DrawDistance,
    int LoadedColumns, float ColumnsPerSecond, float TimeOfDay, string Backend, string RenderNote = "");

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
    private UIChatBox _chat = null!;
    private UIPanel _playersPanel = null!;
    private UILabel _playersTitle = null!;
    private UIList _playerList = null!;
    private UINotificationStack _toasts = null!;
    private UIContextMenu _menu = null!;
    private UILabel _followLabel = null!;
    private UIMinimapFrame _minimap = null!;
    // Where the mouse is (canvas pixels): a menu opens under the click
    private float _mouseX, _mouseY;
    private string? _followName;
    private FollowMode _followMode;
    private int _minimapZoom = 1;
    // Lines the player typed into the chat, waiting for the page to take them to the server
    private readonly List<string> _outgoingChat = new();
    // Seconds since the last chat line arrived: the chat has a backdrop while it is fresh or open, and none when idle
    private float _chatAge = 1000f;
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

        _minimap = new UIMinimapFrame { Size = MinimapSize, MaxZoom = MaxMinimapZoom, ZoomLevel = 1 };
        _root.AddAnchored(_minimap, Anchor.TopRight, -8, 8);
        BuildChat(theme);
        BuildPlayers(theme);

        _toasts = new UINotificationStack { Width = 300, MaxVisible = 4, DefaultDuration = 5f };
        _root.AddAnchored(_toasts, Anchor.TopCenter, 0, 44);

        _hint = new UILabel
        {
            Text = "Click the view to fly   WASD move   Space / Shift up / down   Esc release   Enter chat   M map zoom   F3 debug   Click a player in the list for actions",
            FontSize = FontSize.Caption,
            Color = theme.TextPrimary,
            OutlineWidth = 0.12f,
        };
        _root.AddAnchored(_hint, Anchor.BottomCenter, 0, -10);

        _followLabel = new UILabel { FontSize = FontSize.Caption, Color = theme.TextPrimary, OutlineWidth = 0.14f, Visible = false };
        _root.AddAnchored(_followLabel, Anchor.BottomCenter, 0, -34);

        // Last, so it is on top of everything and gets a click first
        _menu = new UIContextMenu { Visible = false, MenuWidth = MenuWidth };
        _root.AddChild(_menu);

        _ui.Screens.Register(ScreenName, _root);
    }

    // ---- what a click on a player in the list does ----

    private const float MenuWidth = 210f;

    /// <summary>The viewer user chose an action for a player (the worker moves the camera). The name is the one in the list.</summary>
    public Action<string, PlayerAction>? OnPlayerAction { get; set; }

    private void OpenPlayerMenu(int index)
    {
        if (index < 0 || index >= _playerList.ItemCount) return;
        string name = _playerList.GetItem(index).Text;
        // The same row can be clicked again: the selection is only a trigger, so clear it
        _playerList.SelectedIndex = -1;

        _menu.ClearItems();
        _menu.AddItem("Go to " + name, () => OnPlayerAction?.Invoke(name, PlayerAction.GoTo));
        if (string.Equals(_followName, name, StringComparison.OrdinalIgnoreCase))
            _menu.AddItem("Stop following " + name, () => OnPlayerAction?.Invoke(name, PlayerAction.StopFollowing));
        else
        {
            _menu.AddItem("Follow " + name, () => OnPlayerAction?.Invoke(name, PlayerAction.Follow));
            _menu.AddItem("See through " + name + "'s eyes", () => OnPlayerAction?.Invoke(name, PlayerAction.Eyes));
        }
        _menu.AddSeparator();
        _menu.AddItem("Close", null);
        // Under the click, kept inside the view
        float x = Math.Clamp(_mouseX, 0, Math.Max(0, _root.Width - MenuWidth));
        _menu.Show(new System.Numerics.Vector2(x, _mouseY));
        float maxY = Math.Max(0, _root.Height - _menu.Height);
        _menu.Y = Math.Min(_menu.Y, maxY);
    }

    /// <summary>Show (or clear, with null) which player the camera follows, for the label and the menu.</summary>
    public void SetFollowing(string? name, FollowMode mode = FollowMode.Chase)
    {
        _followName = name;
        _followMode = mode;
        if (_followLabel == null) return;
        _followLabel.Visible = name != null;
        if (name != null)
            _followLabel.Text = mode == FollowMode.Eyes
                ? "Seeing through " + name + "'s eyes   move to stop"
                : "Following " + name + "   mouse orbits, move to stop";
    }

    /// <summary>A short message over the world (the toast stack).</summary>
    public void Toast(string text, NotificationType type = NotificationType.Info) => _toasts?.Push(text, type);

    /// <summary>
    /// Rectangles (x, y, width, height in canvas pixels, four floats each) where a click belongs to the HUD, not to the camera: the player list, and the
    /// whole view while a menu is open (a click then only closes the menu). The page asks before it takes the pointer for the camera.
    /// </summary>
    public float[] ClickRects()
    {
        if (_menu == null || !Visible) return [];
        if (_menu.Visible) return [0, 0, _root.Width, _root.Height];
        var b = _playersPanel.ScreenBounds;
        return [b.X, b.Y, b.Width, b.Height];
    }

    // ---- name tags over creatures ----

    private readonly List<UILabel> _tagLabels = new();
    private const float TagWidth = 240f;

    /// <summary>Show these labels over the world this frame (screen pixels); an empty list hides them all.</summary>
    public void SetNameTags(IReadOnlyList<NameTag>? tags)
    {
        if (_root == null) return;
        int n = Visible ? tags?.Count ?? 0 : 0;
        float lineH = _ui.Renderer.GetLineHeight(FontSize.Caption);
        for (int i = 0; i < n; i++)
        {
            if (i >= _tagLabels.Count)
            {
                var label = new UILabel { FontSize = FontSize.Caption, Align = TextAlign.Center, Width = TagWidth, Height = lineH, OutlineWidth = 0.14f };
                _root.AddChild(label);
                _tagLabels.Add(label);
            }
            var tag = tags![i];
            var l = _tagLabels[i];
            l.Visible = true;
            l.Text = tag.Text;
            l.Color = tag.IsPlayer ? Color.White : Color.FromArgb(255, 220, 220, 190);
            l.X = tag.X - TagWidth / 2f;
            l.Y = tag.Y - lineH;
        }
        for (int i = n; i < _tagLabels.Count; i++) _tagLabels[i].Visible = false;
    }

    private const float ChatWidth = 380f, ChatHeight = 170f;
    private const float ChatFadeSeconds = 14f; // = the chat box's FadeStart

    private void BuildChat(UITheme theme)
    {
        // A feed that fades (messages vanish after a while), until the player opens it with Enter: then the whole history and an input
        _chat = new UIChatBox
        {
            Width = ChatWidth,
            Height = ChatHeight,
            ShowChannelTabs = false,
            InputOnlyWhenFocused = true,
            BottomAlign = true,
            MessageLineHeight = 16f,
            InputHeight = 24f,
            FadeStart = ChatFadeSeconds,
            FadeDuration = 4f,
            MaxMessages = 200,
        };
        _chat.OnSend = text => _outgoingChat.Add(text);
        _root.AddAnchored(_chat, Anchor.BottomLeft, 8, -34);
    }

    private const float PlayersWidth = 190f, PlayerRow = 18f, PlayersPad = 8f, MaxPlayerRows = 10;

    private void BuildPlayers(UITheme theme)
    {
        _playersPanel = new UIPanel { Width = PlayersWidth, Height = 40 };
        _playersTitle = new UILabel { FontSize = FontSize.Caption, Color = theme.TextPrimary, X = PlayersPad, Y = 6, Text = "Players" };
        _playersPanel.AddChild(_playersTitle);
        _playerList = new UIList
        {
            ItemHeight = PlayerRow,
            ItemFontSize = FontSize.Caption,
            Padding = 2,
            X = PlayersPad - 2,
            Y = 6 + _ui.Renderer.GetLineHeight(FontSize.Caption) + 4,
            Width = PlayersWidth - 2 * (PlayersPad - 2),
            Height = PlayerRow,
        };
        _playerList.OnSelectionChanged = OpenPlayerMenu;
        _playersPanel.AddChild(_playerList);
        _root.AddAnchored(_playersPanel, Anchor.TopRight, -8, MinimapReserve);
        SetPlayers([]);
    }

    private const float MinimapSize = 176f;
    private const int MaxMinimapZoom = 3;
    /// <summary>The top-right corner below the minimap (8 above it, its compass bar and coordinate bar, 8 gap).</summary>
    private const float MinimapReserve = 8f + MinimapSize + 18f + 16f + 8f;

    /// <summary>How many blocks from the camera the minimap shows (to each edge): M cycles 64, 128, 256.</summary>
    public float MinimapExtent => 64f * (1 << (_minimapZoom - 1));

    /// <summary>The world texture the minimap shows (the renderer draws it); null until there is one.</summary>
    public void SetMinimapTexture(GPUTextureView? view) => _minimap.MapTexture = view;

    private void SetPlayers(string[] names)
    {
        _playersTitle.Text = string.Create(CultureInfo.InvariantCulture, $"Players online ({names.Length})");
        _playerList.ClearItems();
        foreach (var n in names) _playerList.AddItem(n);
        int rows = (int)Math.Clamp(names.Length, 1, MaxPlayerRows);
        _playerList.Height = rows * PlayerRow + 2 * _playerList.Padding; // the viewport is the height minus the list's own padding
        _playersPanel.Height = 6 + _ui.Renderer.GetLineHeight(FontSize.Caption) + 4 + _playerList.Height + 6;
    }

    /// <summary>Chat lines, players and notices the page saw (SignalR lives on the page; the worker has no connection).</summary>
    public void Feed(HudFeed feed)
    {
        if (!_ui.IsInitialized) return;
        for (int i = 0; i < feed.ChatTexts.Length; i++)
            AddChatLine(feed.ChatSenders[i], feed.ChatTexts[i]);
        if (feed.ChatTexts.Length > 0) _chatAge = 0f;
        if (feed.PlayersChanged) SetPlayers(feed.Players);
        for (int i = 0; i < feed.ToastTexts.Length; i++)
            _toasts.Push(feed.ToastTexts[i], ToNotificationType(feed.ToastKinds[i]));
    }

    private static NotificationType ToNotificationType(int kind) => kind switch
    {
        HudToastKind.Success => NotificationType.Success,
        HudToastKind.Warning => NotificationType.Warning,
        HudToastKind.Danger => NotificationType.Damage,
        HudToastKind.Achievement => NotificationType.Achievement,
        _ => NotificationType.Info,
    };

    // GameUI draws a message on one line: a long one is wrapped here (the sender only in front of the first line)
    private void AddChatLine(string sender, string text)
    {
        float width = ChatWidth - 2 * _chat.Padding - 6;
        string prefix = string.IsNullOrEmpty(sender) ? "" : sender + ": ";
        float first = width - _ui.Renderer.MeasureText(prefix, FontSize.Caption);
        var lines = TextWrap.Wrap(text, first, width, s => _ui.Renderer.MeasureText(s, FontSize.Caption));
        for (int i = 0; i < lines.Count; i++)
            _chat.AddMessage(i == 0 && prefix.Length > 0 ? sender : null, lines[i]);
    }

    /// <summary>The lines the player typed into the chat since the last call.</summary>
    public string[] TakeOutgoingChat()
    {
        if (_outgoingChat.Count == 0) return [];
        var lines = _outgoingChat.ToArray();
        _outgoingChat.Clear();
        return lines;
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
            case UiInputEvent.MouseMove: _mouseX = e.X; _mouseY = e.Y; input.MouseMove(e.X, e.Y); break;
            case UiInputEvent.MouseDown: input.MouseDown((int)e.X); break;
            case UiInputEvent.MouseUp: input.MouseUp((int)e.X); break;
            case UiInputEvent.Wheel: input.Wheel(e.Y); break;
            case UiInputEvent.KeyDown:
                // Enter opens the chat (when the mouse is free and nothing is being typed); the same press then submits an empty line, which is ignored
                if (e.Code is "Enter" or "NumpadEnter" && !_pointerLocked && !_chat.IsInputFocused) _chat.FocusInput();
                // M cycles the minimap zoom (not while typing: the letter belongs to the chat then)
                if (e.Code == "KeyM" && !_chat.IsInputFocused) _minimapZoom = _minimapZoom % MaxMinimapZoom + 1;
                if (e.Code != null) input.KeyDown(e.Code, e.Text);
                break;
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
        // The chat: a faint backdrop while its lines are fresh, a solid one while it is open, nothing when it is idle
        _chatAge += dt;
        _chat.BackgroundColor = _chat.IsInputFocused ? Color.FromArgb(170, 12, 14, 24)
            : _chatAge < ChatFadeSeconds ? Color.FromArgb(90, 12, 14, 24) : Color.Transparent;
        _minimap.Bearing = d.YawDegrees + 90f;
        _minimap.Coordinates = string.Create(CultureInfo.InvariantCulture, $"{d.CamX:F0}, {d.CamZ:F0}");
        _minimap.ZoomLevel = _minimapZoom;
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
            if (d.RenderNote.Length > 0) UIDebugOverlay.Text(d.RenderNote);
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
