using System.Globalization;
using System.Text;
using AubsCraft.Admin.Services;
using AubsCraft.Live.Model;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;

namespace AubsCraft.Admin.Rendering;

public sealed partial class ViewerHud
{
    // ---- the viewer's own menu: everything about the viewer is set from inside it, drawn on the GPU with the rest of the HUD ----

    private const float MenuPanelWidth = 500f, MenuTitleWidth = 84f, MenuPad = 12f, MenuRowHeight = 30f, MenuButtonWidth = 122f, MenuGap = 4f;
    private const int MaxServerButtons = 8;

    private UIButton _menuButton = null!;
    private UIPanel _menuPanel = null!;
    private UILabel _menuHeading = null!;
    private UILabel _voiceStatus = null!;
    private int _menuLogFrames;
    private ViewerSettings _settings = new();
    private ViewerPageState _pageState = new();
    private readonly List<string> _hudCommands = new();
    private readonly Dictionary<string, UIButton> _menuButtons = new();
    private readonly List<MenuSection> _menuSections = new();
    private readonly UIButton[] _serverButtons = new UIButton[MaxServerButtons];
    private UISlider _distanceSlider = null!, _fovSlider = null!, _speedSlider = null!;

    private sealed class MenuSection
    {
        public required UILabel Title;
        public required Func<bool> Show;
        public readonly List<(UIElement Element, Func<bool>? Show, bool Wide)> Items = new();
    }

    /// <summary>The viewer's settings were changed in the menu (the worker applies them, the page remembers them).</summary>
    public Action<ViewerSettings>? OnSettingsChanged { get; set; }

    /// <summary>The settings as the menu has them.</summary>
    public ViewerSettings Settings => _settings;

    /// <summary>The menu is open: the mouse is the HUD's.</summary>
    public bool IsMenuOpen => _menuPanel != null && _menuPanel.Visible;

    /// <summary>What the page should do (it has the DOM): fullscreen, voice, mic, vr, ar, lighting, shading, edit, world, bluemap, clearcache, copylink, panel, server:ID, settings.</summary>
    public string[] TakeCommands()
    {
        if (_hudCommands.Count == 0) return [];
        var commands = _hudCommands.ToArray();
        _hudCommands.Clear();
        return commands;
    }

    private void BuildMenu(UITheme theme)
    {
        _menuButton = new UIButton { Text = "Menu", FontSize = FontSize.Caption, Width = 58, Height = 28, PaddingX = 4, PaddingY = 3, OnClick = () => OpenMenu(!IsMenuOpen) };
        _root.AddAnchored(_menuButton, Anchor.TopLeft, 8 + 270 + 6, 8);

        _menuPanel = new UIPanel { Width = MenuPanelWidth, Height = 200, Visible = false };
        _menuHeading = new UILabel { Text = "Viewer   (Tab closes)", FontSize = FontSize.Caption, Color = theme.TextPrimary, X = MenuPad, Y = 10 };
        _menuPanel.AddChild(_menuHeading);

        var server = Section("Server", () => (_pageState.ServerIds?.Length ?? 0) > 1);
        for (int i = 0; i < MaxServerButtons; i++)
        {
            int index = i;
            var button = new UIButton { FontSize = FontSize.Caption, Width = MenuButtonWidth, Height = MenuRowHeight - MenuGap, PaddingX = 3, PaddingY = 3, Visible = false,
                OnClick = () => { var ids = _pageState.ServerIds; if (ids != null && index < ids.Length && ids[index] != _pageState.SelectedServer) Command("server:" + ids[index]); } };
            _serverButtons[i] = button;
            _menuPanel.AddChild(button);
            server.Items.Add((button, () => index < (_pageState.ServerIds?.Length ?? 0), false));
        }

        var window = Section("Window", () => true);
        Button(window, "fullscreen", () => Command("fullscreen"));
        Button(window, "copylink", () => Command("copylink"));
        Button(window, "panel", () => Command("panel"));

        var show = Section("Show", () => true);
        Button(show, "stats", () => Change(_settings with { Stats = !_settings.Stats }));
        Button(show, "compass", () => Change(_settings with { Compass = !_settings.Compass }));
        Button(show, "minimap", () => Change(_settings with { Minimap = !_settings.Minimap }));
        Button(show, "players", () => Change(_settings with { PlayerList = !_settings.PlayerList }));
        Button(show, "chat", () => Change(_settings with { Chat = !_settings.Chat }));
        Button(show, "tags", () => Change(_settings with { NameTags = !_settings.NameTags }));
        Button(show, "bars", () => Change(_settings with { PlayerBars = !_settings.PlayerBars }));

        var picture = Section("Picture", () => true);
        Button(picture, "lighting", () => Command("lighting"));
        Button(picture, "shading", () => Command("shading"));
        Button(picture, "distance", () => Change(_settings with { DrawDistance = _settings.DrawDistance == 0 ? (int)_distanceSlider.Value : 0 }));
        _distanceSlider = Slider(picture, "Draw distance (chunks)", FarWindow.MinDrawDistance, FarWindow.MaxDrawDistance, "F0", v => Change(_settings with { DrawDistance = (int)MathF.Round(v) }));
        _distanceSlider.WheelStep = 1f;

        var camera = Section("Camera", () => true);
        _fovSlider = Slider(camera, "Field of view", 40f, 110f, "F0", v => Change(_settings with { Fov = MathF.Round(v) }));
        _fovSlider.WheelStep = 1f;
        _speedSlider = Slider(camera, "Flying speed (blocks a second; the wheel changes it while flying)", MinFlySpeed, MaxFlySpeed, "F0", v => Change(_settings with { FlySpeed = MathF.Round(v) }));
        _speedSlider.WheelStep = 5f;

        var voice = Section("Voice", () => true);
        Button(voice, "voice", () => Command("voice"));
        Button(voice, "mic", () => Command("mic"), () => _pageState.VoiceOn);
        _voiceStatus = new UILabel { FontSize = FontSize.Caption, Color = theme.TextPrimary, Width = MenuPanelWidth - MenuTitleWidth - 2 * MenuPad, Height = 18 };
        _menuPanel.AddChild(_voiceStatus);
        voice.Items.Add((_voiceStatus, () => _pageState.VoiceText.Length > 0, true));

        var headset = Section("Headset", () => _pageState.VrSupported || _pageState.ArSupported);
        Button(headset, "vr", () => Command("vr"), () => _pageState.VrSupported);
        Button(headset, "ar", () => Command("ar"), () => _pageState.ArSupported);

        var admin = Section("Admin", () => _pageState.Admin);
        Button(admin, "edit", () => Command("edit"));
        Button(admin, "world", () => { OpenMenu(false); OpenWorldPanel(true); });

        var other = Section("Other", () => true);
        Button(other, "bluemap", () => { OpenMenu(false); Command("bluemap"); }, () => _pageState.BlueMap);
        Button(other, "clearcache", () => Command("clearcache"));

        RefreshMenu();
        _root.AddAnchored(_menuPanel, Anchor.Center, 0, 0);
    }

    private MenuSection Section(string title, Func<bool> show)
    {
        var label = new UILabel { Text = title, FontSize = FontSize.Caption, Color = UITheme.Current.TextPrimary, Width = MenuTitleWidth - 4, Height = MenuRowHeight };
        _menuPanel.AddChild(label);
        var section = new MenuSection { Title = label, Show = show };
        _menuSections.Add(section);
        return section;
    }

    private void Button(MenuSection section, string id, Action onClick, Func<bool>? show = null)
    {
        var button = new UIButton { FontSize = FontSize.Caption, Width = MenuButtonWidth, Height = MenuRowHeight - MenuGap, PaddingX = 3, PaddingY = 3, OnClick = onClick };
        _menuButtons[id] = button;
        _menuPanel.AddChild(button);
        section.Items.Add((button, show, false));
    }

    private UISlider Slider(MenuSection section, string label, float min, float max, string format, Action<float> onChanged)
    {
        var slider = new UISlider { Label = label, MinValue = min, MaxValue = max, Format = format, Value = min, Width = MenuPanelWidth - MenuTitleWidth - 2 * MenuPad, Height = 40, OnChanged = onChanged };
        _menuPanel.AddChild(slider);
        section.Items.Add((slider, null, true));
        return slider;
    }

    private void Command(string command) => _hudCommands.Add(command);

    /// <summary>Something for the page to do (see <see cref="TakeCommands"/>).</summary>
    public void QueueCommand(string command) => _hudCommands.Add(command);

    private void Change(ViewerSettings next)
    {
        if (next == _settings) return;
        SetSettings(next);
        OnSettingsChanged?.Invoke(next);
        _hudCommands.Add("settings");
    }

    /// <summary>Use these settings: what the menu shows and what the HUD draws.</summary>
    public void SetSettings(ViewerSettings settings)
    {
        _settings = settings;
        ApplyHudSettings();
        RefreshMenu();
    }

    /// <summary>What the page knows: what the menu's buttons say.</summary>
    public void SetPageState(ViewerPageState state)
    {
        _pageState = state;
        RefreshMenu();
    }

    private void ApplyHudSettings()
    {
        if (_root == null || _xrBuilt) return;
        _statsPanel.Visible = _settings.Stats;
        _compass.Visible = _settings.Compass;
        _minimap.Visible = _settings.Minimap;
        _playersPanel.Visible = _settings.PlayerList;
        _chat.Visible = _settings.Chat;
    }

    private void RefreshMenu()
    {
        if (_menuPanel == null) return;
        void Say(string id, string text) { if (_menuButtons.TryGetValue(id, out var b) && b.Text != text) b.Text = text; }
        static string OnOff(bool on) => on ? "On" : "Off";
        Say("fullscreen", "Fullscreen: " + OnOff(_pageState.Fullscreen));
        Say("copylink", "Copy link here");
        Say("panel", "Admin panel");
        Say("stats", "Stats: " + OnOff(_settings.Stats));
        Say("compass", "Compass: " + OnOff(_settings.Compass));
        Say("minimap", "Map: " + OnOff(_settings.Minimap));
        Say("players", "Player list: " + OnOff(_settings.PlayerList));
        Say("chat", "Chat: " + OnOff(_settings.Chat));
        Say("tags", "Names: " + OnOff(_settings.NameTags));
        Say("bars", "Player bars: " + OnOff(_settings.PlayerBars));
        Say("lighting", "Lighting: " + OnOff(_pageState.Lighting));
        Say("shading", "Shading: " + (_pageState.Shading ? "Soft" : "Flat"));
        Say("distance", "Distance: " + (_settings.DrawDistance == 0 ? "Auto" : "Fixed"));
        Say("voice", "Voice: " + OnOff(_pageState.VoiceOn));
        Say("mic", "Mic: " + OnOff(_pageState.MicOn));
        Say("vr", "Enter VR");
        Say("ar", "Enter AR");
        Say("edit", "Edit: " + OnOff(_pageState.Editing));
        Say("world", "The world...");
        Say("bluemap", "BlueMap");
        Say("clearcache", "Clear cache");
        if (_voiceStatus.Text != _pageState.VoiceText) _voiceStatus.Text = _pageState.VoiceText;
        _fovSlider.Value = _settings.Fov;
        _speedSlider.Value = _settings.FlySpeed;
        if (_settings.DrawDistance > 0) _distanceSlider.Value = _settings.DrawDistance;
        var ids = _pageState.ServerIds;
        var labels = _pageState.ServerLabels;
        for (int i = 0; i < MaxServerButtons; i++)
        {
            if (ids == null || labels == null || i >= ids.Length) continue;
            _serverButtons[i].Text = (ids[i] == _pageState.SelectedServer ? "> " : "") + labels[i];
        }
        LayoutMenu();
    }

    // Each section: its title on the left, its items flowing to the right of it, a row at a time. Sections with nothing to show take no room.
    private void LayoutMenu()
    {
        float y = 36f, left = MenuPad + MenuTitleWidth, right = MenuPanelWidth - MenuPad;
        foreach (var section in _menuSections)
        {
            bool any = section.Show() && section.Items.Exists(i => i.Show?.Invoke() ?? true);
            section.Title.Visible = any;
            foreach (var (element, _, _) in section.Items) element.Visible = false;
            if (!any) continue;
            section.Title.X = MenuPad;
            section.Title.Y = y + 6f;
            float x = left;
            bool rowUsed = false;
            foreach (var (element, show, wide) in section.Items)
            {
                if (!(show?.Invoke() ?? true)) continue;
                if (wide || x + element.Width > right)
                {
                    if (rowUsed) { y += MenuRowHeight; rowUsed = false; }
                    x = left;
                }
                element.Visible = true;
                element.X = x;
                element.Y = y;
                if (wide) { y += element.Height + MenuGap; x = left; rowUsed = false; }
                else { x += element.Width + MenuGap; rowUsed = true; }
            }
            if (rowUsed) y += MenuRowHeight;
            y += MenuGap * 2f;
        }
        _menuPanel.Height = y + MenuPad;
    }

    /// <summary>Open or close the viewer's menu.</summary>
    public void OpenMenu(bool open)
    {
        if (_menuPanel == null || _menuPanel.Visible == open) return;
        _menuPanel.Visible = open;
        if (open)
        {
            RefreshMenu();
            _menuLogFrames = 2;   // a click is acted on at the end of a frame's update, and the panel is placed at the start of the next
        }
    }

    // The gate presses the menu's real buttons with the real mouse, so it asks where they are; a line only when the menu opens
    private void LogMenuLayout()
    {
        if (_menuLogFrames <= 0 || --_menuLogFrames > 0 || !_menuPanel.Visible) return;
        var line = new StringBuilder("[HudMenu]");
        void Add(string id, UIElement e)
        {
            if (!e.Visible) return;
            var b = e.ScreenBounds;
            line.Append(string.Create(CultureInfo.InvariantCulture, $" {id}={b.X:F0},{b.Y:F0},{b.Width:F0},{b.Height:F0}"));
        }
        foreach (var (id, button) in _menuButtons) Add(id, button);
        for (int i = 0; i < MaxServerButtons; i++) Add("server" + i, _serverButtons[i]);
        Add("distanceSlider", _distanceSlider);
        Add("fovSlider", _fovSlider);
        Add("speedSlider", _speedSlider);
        Console.WriteLine(line.ToString());
    }

    private bool _menuPressOutside;

    // A click away from the menu closes it, as any menu. Only a click that began out there: a slider dragged past the panel's edge must not close it
    private void MenuSawMouse(int kind)
    {
        if (!IsMenuOpen) { _menuPressOutside = false; return; }
        var at = new System.Numerics.Vector2(_mouseX, _mouseY);
        bool outside = _menuPanel.HitTest(at) == null && _menuButton.HitTest(at) == null;
        if (kind == UiInputEvent.MouseDown) _menuPressOutside = outside;
        else if (kind == UiInputEvent.MouseUp)
        {
            if (_menuPressOutside && outside) OpenMenu(false);
            _menuPressOutside = false;
        }
    }

    /// <summary>The flying speed the wheel moves between (blocks a second).</summary>
    public const float MinFlySpeed = 5f, MaxFlySpeed = 300f;

    /// <summary>Whether the mouse is over something of the HUD that uses the wheel itself: then the wheel is not the camera's.</summary>
    public bool WheelBelongsToHud()
    {
        if (_root == null || !Visible || _pointerLocked) return false;
        var rects = ClickRects();
        for (int i = 0; i + 3 < rects.Length; i += 4)
            if (_mouseX >= rects[i] && _mouseX < rects[i] + rects[i + 2] && _mouseY >= rects[i + 1] && _mouseY < rects[i + 1] + rects[i + 3]) return true;
        return _chat.IsInputFocused && _chat.HitTest(new System.Numerics.Vector2(_mouseX, _mouseY)) != null;
    }
}
