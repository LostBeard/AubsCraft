using System.Drawing;
using System.Globalization;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The HUD's second screen, made for a headset (VR and AR): the same widgets and the same logic as the desktop one (the palette, the blueprints, the players, the phrases), laid out as small
/// SURFACES on one atlas - a MENU of tiles for what can be done now, a WINDOW with tabs for one thing at a time, a BAR with the blocks in hand while building, and a FEED of chat and notices.
/// The headset puts each surface where it belongs in the room (<c>XrSurfaces</c>); nothing hangs in view while exploring, so a controller's ray reaches the world.
///
/// Building works as in QuestCraft's creative mode: the blocks in hand are on the left arm, the right trigger places, the left trigger breaks, the inventory (every block) is a window, and the rest
/// (marking an area, blueprints, fill, copy, paste) is in the Tools page, away from the way a block is put down.
/// </summary>
public sealed partial class ViewerHud
{
    private const string XrScreenName = "xr";
    private const int XrAtlasWidth = 1100, XrAtlasHeight = 840;

    private enum XrPage { None, Inventory, Tools, Blueprints, Players, Say, Map, World, Settings }

    private bool _xrBuilt, _xrActive;
    private UIPanel _xrRoot = null!, _xrMenu = null!, _xrWindow = null!, _xrBar = null!, _xrFeed = null!;
    private UIPanel _xrMapPage = null!, _xrSettingsPage = null!, _xrToolsPage = null!;
    private UILabel _xrMenuMode = null!, _xrMenuWhere = null!, _xrToolsHint = null!;
    private UIButton _xrClose = null!;
    private readonly List<(XrPage Page, UIButton Tab)> _xrTabs = new();
    private readonly Dictionary<string, UIButton> _xrTile = new();
    private readonly Dictionary<string, UIButton> _xrTool = new();
    private readonly List<UIButton> _phraseButtons = new();
    private bool _xrMenuOpen, _xrMapOpen, _xrPlayersOpen, _xrSettingsOpen, _xrToolsOpen;
    private float _feedAge = 1000f;
    private string[] _lastPlayers = [];

    // What the menu offers depends on what is going on (the editor tells, through SetEditToolbar and SetTableToolbar)
    private bool _ctxEditing, _ctxHasSelection, _ctxPasting, _ctxHasClipboard, _ctxSelecting, _ctxAr, _ctxTabletop;
    private bool _voiceOn, _micOn;

    /// <summary>The menu's "Leave VR / AR" tile was pressed.</summary>
    public Action? OnXrLeave { get; set; }

    /// <summary>The menu is open (the X button toggles it).</summary>
    public bool XrMenuOpen { get => _xrMenuOpen; set { if (_xrMenuOpen == value) return; _xrMenuOpen = value; LayoutXrMenu(); } }

    /// <summary>Some page of the window is open.</summary>
    public bool XrWindowOpen => _xrBuilt && (IsPaletteOpen || IsBlueprintsOpen || IsPhrasesOpen || IsWorldPanelOpen || _xrMapOpen || _xrPlayersOpen || _xrSettingsOpen || _xrToolsOpen);

    /// <summary>A page with a list is open (blocks, blueprints, players): the right stick scrolls it.</summary>
    public bool XrListOpen => _xrBuilt && (IsPaletteOpen || IsBlueprintsOpen || _xrPlayersOpen);

    /// <summary>The bar of blocks in hand shows (while building).</summary>
    public bool XrBarVisible => _xrBuilt && _editMode;

    /// <summary>The feed shows (chat and notices are fresh, or someone speaks).</summary>
    public bool XrFeedVisible => _xrBuilt && _feedAge < 14f;

    /// <summary>The minimap is wanted (the map page is open): the world draws it only then.</summary>
    public bool XrMapOpen => _xrMapOpen;

    /// <summary>The world draws the minimap texture: always on the desktop, in a headset only while the map page is open.</summary>
    public bool WantsMinimap => !_xrActive || _xrMapOpen;

    /// <summary>The inventory (every block) opens, or closes if it is the page showing: the Y button.</summary>
    public void ToggleXrInventory()
    {
        if (!_xrBuilt || !_ctxEditing) return;
        if (IsPaletteOpen) CloseXrWindow(); else OpenXrPage(XrPage.Inventory);
    }

    // ---- the look: a dark glass with a cool accent, big targets ----

    private static readonly Color Glass = Color.FromArgb(236, 12, 16, 24);
    private static readonly Color GlassBorder = Color.FromArgb(220, 64, 170, 205);
    private static readonly Color TileColor = Color.FromArgb(255, 30, 42, 58);
    private static readonly Color TileHover = Color.FromArgb(255, 48, 92, 120);
    private static readonly Color TilePressed = Color.FromArgb(255, 20, 30, 42);
    private static readonly Color TileAccent = Color.FromArgb(255, 22, 88, 104);
    private static readonly Color TileAccentHover = Color.FromArgb(255, 34, 130, 150);
    private static readonly Color TileOn = Color.FromArgb(255, 26, 118, 74);
    private static readonly Color TileOnHover = Color.FromArgb(255, 38, 150, 96);
    private static readonly Color TileDanger = Color.FromArgb(255, 112, 34, 42);
    private static readonly Color TileDangerHover = Color.FromArgb(255, 156, 48, 58);
    private static readonly Color HandleColor = Color.FromArgb(255, 44, 62, 80);

    private static UITheme CreateXrTheme() => new()
    {
        PanelBackground = Glass,
        PanelBorder = GlassBorder,
        PanelBorderWidth = 2,
        PanelCornerRadius = 16,
        ButtonCornerRadius = 10,
        ControlCornerRadius = 8,
        ButtonNormal = TileColor,
        ButtonHover = TileHover,
        ButtonPressed = TilePressed,
        ButtonBorder = Color.FromArgb(150, 70, 190, 210),
        ButtonBorderWidth = 1.5f,
        ButtonFontSize = FontSize.Body,
        FontScale = 1.2f,
        TextPrimary = Color.FromArgb(255, 238, 246, 250),
        WorldTextOutlineWidth = 0f,
    };

    private static UITheme? _xrTheme;

    private void Style(UIButton b, Color normal, Color hover)
    {
        b.NormalColor = normal;
        b.HoverColor = hover;
        b.PressedColor = TilePressed;
    }

    // ---- building the screen (once: the page's HUD is the headset's only) ----

    private void ActivateXr(bool on)
    {
        if (on)
        {
            _xrTheme ??= CreateXrTheme();
            UITheme.Current = _xrTheme;
            if (!_xrBuilt) BuildXrScreen();
            _ui.Screens.SetScreen(XrScreenName);
            _xrActive = true;
            _xrMenuOpen = false;
            CloseXrWindow();
            LayoutXrMenu();
            SetPlayers(_lastPlayers);
        }
        else
        {
            _xrActive = false;
            _xrMenuOpen = false;
            if (_xrBuilt) CloseXrWindow();
        }
    }

    private static UIPanel Surface(float x, float y, float w, float h, bool background = true) => new()
    {
        X = x, Y = y, Width = w, Height = h,
        BackgroundColor = background ? Glass : Color.Transparent,
        BorderColor = background ? GlassBorder : Color.Transparent,
        BorderWidth = background ? 2f : 0f,
        CornerRadius = 18,
        Padding = 0,
    };

    private void MoveTo(UIElement e, UIElement parent, float x, float y)
    {
        _root.RemoveAnchored(e);
        e.Parent?.RemoveChild(e);
        parent.AddChild(e);
        e.X = x; e.Y = y;
    }

    // The strip at the bottom of a surface that can be moved: pointed at with the trigger held, it takes the surface along (the headset does that; this only draws it)
    private static UIPanel Handle(float width, float y)
    {
        var handle = new UIPanel { X = 12, Y = y, Width = width - 24, Height = 22, BackgroundColor = HandleColor, BorderColor = Color.Transparent, BorderWidth = 0, CornerRadius = 11, Padding = 0 };
        handle.AddChild(new UILabel { Text = "hold the trigger here to move", FontSize = FontSize.Caption, Color = Color.FromArgb(255, 170, 190, 205), Align = TextAlign.Center, X = 0, Y = 3, Width = width - 24 });
        return handle;
    }

    private void BuildXrScreen()
    {
        var theme = UITheme.Current;
        _xrRoot = new UIPanel { Width = XrAtlasWidth, Height = XrAtlasHeight, BackgroundColor = Color.Transparent, BorderColor = Color.Transparent, BorderWidth = 0, CornerRadius = 0, Padding = 0 };

        // ---- the menu ----
        _xrMenu = Surface(0, 0, 330, 592);
        _xrRoot.AddChild(_xrMenu);
        _xrMenu.AddChild(Handle(330, 562));
        _xrMenuMode = new UILabel { Text = "Exploring", FontSize = FontSize.Heading, Color = theme.TextPrimary, X = 16, Y = 8 };
        _xrMenu.AddChild(_xrMenuMode);
        _xrMenuWhere = new UILabel { Text = "", FontSize = FontSize.Caption, Color = theme.TextSecondary, X = 16, Y = 38 };
        _xrMenu.AddChild(_xrMenuWhere);

        // The tools of the editor (marking an area, blueprints, fill, copy, paste) are the Tools page's: they leave the menu
        string[] toolIds = ["select", "blueprints", "undo", "redo", "fill", "clear", "copy", "cut", "paste", "save", "turn", "mirror", "cancel"];
        UIButton? snap = null;
        foreach (var (id, button) in _toolButtons)
        {
            _root.RemoveChild(button);
            var captured = id;
            button.FontSize = FontSize.Body;
            button.OnClick = () => OnXrTile(captured);
            if (id == "snap") snap = button;
            else if (toolIds.Contains(id)) _xrTool[id] = button;
            else { _xrMenu.AddChild(button); _xrTile[id] = button; }
        }
        foreach (var (id, button) in _tableButtons)
        {
            _root.RemoveChild(button);
            _xrMenu.AddChild(button);
            _xrTile[id] = button;
            var captured = id;
            button.FontSize = FontSize.Body;
            button.OnClick = () => OnTableAction?.Invoke(captured);
        }
        foreach (var (id, label) in new[] { ("players", "Players"), ("map", "Map"), ("world", "World"), ("settings", "Settings"), ("tools", "Tools"), ("exit", "Leave") })
        {
            var captured = id;
            var button = new UIButton { Text = label, FontSize = FontSize.Body, Visible = false, PaddingX = 4, PaddingY = 4 };
            button.OnClick = () => OnXrTile(captured);
            _xrMenu.AddChild(button);
            _xrTile[id] = button;
        }
        _xrTile["chat"].Text = "Say";
        _xrTile["enter"].Text = "Creative: off";
        _xrTile["leave"].Text = "Creative: ON";
        _xrTile["blocks"].Text = "Inventory";
        _xrTile["exit"].Text = "Leave";
        Style(_xrTile["enter"], TileColor, TileHover);
        Style(_xrTile["leave"], TileOn, TileOnHover);
        foreach (var id in new[] { "blocks", "tools" }) Style(_xrTile[id], TileAccent, TileAccentHover);
        Style(_xrTile["exit"], TileDanger, TileDangerHover);

        // ---- the window ----
        _xrWindow = Surface(340, 0, 760, 582);
        _xrRoot.AddChild(_xrWindow);
        _xrWindow.AddChild(Handle(760, 552));
        foreach (var (page, label, width) in new[]
        {
            (XrPage.Inventory, "Inventory", 120f), (XrPage.Tools, "Tools", 80f), (XrPage.Players, "Players", 96f), (XrPage.Say, "Say", 64f),
            (XrPage.Map, "Map", 64f), (XrPage.World, "World", 84f), (XrPage.Settings, "Settings", 104f),
        })
        {
            var captured = page;
            var tab = new UIButton { Text = label, FontSize = FontSize.Body, Width = width, Height = 44, PaddingX = 4, PaddingY = 4, OnClick = () => OpenXrPage(captured) };
            _xrWindow.AddChild(tab);
            _xrTabs.Add((page, tab));
        }
        _xrClose = new UIButton { Text = "Close", FontSize = FontSize.Body, X = 760 - 88, Y = 10, Width = 78, Height = 44, PaddingX = 4, PaddingY = 4, OnClick = CloseXrWindow };
        Style(_xrClose, TileDanger, TileDangerHover);
        _xrWindow.AddChild(_xrClose);

        BuildXrPages(theme, snap!);

        // ---- the bar of blocks in hand ----
        _xrBar = Surface(0, 600, 760, 96);
        _xrRoot.AddChild(_xrBar);
        MoveTo(_editLabel, _xrBar, 16, 6);
        _editLabel.FontSize = FontSize.Body;
        _editLabel.Visible = true;
        MoveTo(_editBar, _xrBar, 74, 32);
        _editBar.SlotSize = 64;
        _editBar.Visible = true;

        // ---- the feed ----
        _xrFeed = Surface(770, 600, 330, 240, background: false);
        _xrRoot.AddChild(_xrFeed);
        MoveTo(_chat, _xrFeed, 0, 30);
        _chat.Width = 330; _chat.Height = 210;
        MoveTo(_toasts, _xrFeed, 0, 0);
        _toasts.Width = 330;
        MoveTo(_speaking, _xrFeed, 6, 4);

        // ---- on top: the player menu and the dot ----
        MoveTo(_menu, _xrRoot, 0, 0);
        MoveTo(_pointerDot, _xrRoot, 0, 0);
        _pointerDot.Width = 18; _pointerDot.Height = 18; _pointerDot.CornerRadius = 9;

        _ui.Screens.Register(XrScreenName, _xrRoot);
        _xrBuilt = true;
        _xrActive = true;   // the rest of the build asks
        RelayoutPlayersForXr();
        _xrActive = false;
    }

    // The pages the window holds: the HUD's own panels, moved in and made bigger
    private void BuildXrPages(UITheme theme, UIButton snap)
    {
        const float x = 10, y = 64, w = 740, h = 478;
        foreach (var page in new UIElement[] { _palette, _blueprints, _phrases, _worldPanel, _playersPanel, _namePrompt })
            MoveTo(page, _xrWindow, x, y);

        // The inventory: a search that chips fill in, and a list with big rows
        _palette.Width = w; _palette.Height = h; _palette.Visible = false;
        foreach (var child in _palette.Children) if (child is UILabel label) label.Visible = false;
        _paletteSearch.X = 12; _paletteSearch.Y = 8; _paletteSearch.Width = w - 24; _paletteSearch.Height = 40;
        _paletteSearch.FontSize = FontSize.Body;
        for (int i = 0; i < _paletteChips.Count; i++)
        {
            var chip = _paletteChips[i];
            chip.Visible = true; chip.FontSize = FontSize.Body;
            chip.Width = 100; chip.Height = 38;
            chip.X = 12 + (i % 7) * 104; chip.Y = 58 + (i / 7) * 44;
        }
        _paletteList.ItemFontSize = FontSize.Body;
        _paletteList.ItemHeight = 46;
        _paletteList.X = 10; _paletteList.Y = 152; _paletteList.Width = w - 20; _paletteList.Height = h - 152 - 8;

        // The blueprints
        _blueprints.Width = w; _blueprints.Height = h; _blueprints.Visible = false;
        foreach (var child in _blueprints.Children) if (child is UILabel label) label.Visible = false;
        _blueprintList.ItemFontSize = FontSize.Body;
        _blueprintList.ItemHeight = 46;
        _blueprintList.X = 10; _blueprintList.Y = 8; _blueprintList.Width = w - 20; _blueprintList.Height = h - 8 - 64;
        _blueprintDelete.FontSize = FontSize.Body;
        _blueprintDelete.X = 12; _blueprintDelete.Y = h - 54; _blueprintDelete.Width = 190; _blueprintDelete.Height = 44;

        // What to say: two columns of big buttons
        _phrases.Width = w; _phrases.Height = h; _phrases.Visible = false;
        foreach (var child in _phrases.Children)
        {
            if (child is UILabel label) label.Visible = false;
            else if (child is UIButton b) _phraseButtons.Add(b);
        }
        for (int i = 0; i < _phraseButtons.Count; i++)
        {
            var b = _phraseButtons[i];
            b.FontSize = FontSize.Body;
            b.Width = 358; b.Height = 64;
            b.X = 12 + (i % 2) * 366; b.Y = 10 + (i / 2) * 72;
        }

        // The players: the list with big rows (the panel decides its own height when the names change)
        _playersPanel.Visible = false;

        // The world (an admin's): time, weather, saving
        RebuildWorldPanelForXr(theme, w, h);

        // The name prompt is never asked for in a headset (blueprints are named by themselves), but it must not sit in the window
        _namePrompt.Visible = false;

        // The map: the minimap at a good size and a button for its zoom
        _xrMapPage = new UIPanel { X = x, Y = y, Width = w, Height = h, BackgroundColor = Color.Transparent, BorderColor = Color.Transparent, BorderWidth = 0, Visible = false };
        _xrWindow.AddChild(_xrMapPage);
        MoveTo(_minimap, _xrMapPage, 150, 10);
        _minimap.Size = 400;
        var zoom = new UIButton { Text = "Zoom", FontSize = FontSize.Body, X = 580, Y = 14, Width = 140, Height = 52, PaddingX = 4, PaddingY = 4, OnClick = () => CycleMinimapZoom() };
        _xrMapPage.AddChild(zoom);
        _xrMapPage.AddChild(new UILabel { Text = "Pull the trigger on the map to zoom", FontSize = FontSize.Caption, Color = theme.TextSecondary, X = 150, Y = 440 });

        // Settings
        _xrSettingsPage = new UIPanel { X = x, Y = y, Width = w, Height = h, BackgroundColor = Color.Transparent, BorderColor = Color.Transparent, BorderWidth = 0, Visible = false };
        _xrWindow.AddChild(_xrSettingsPage);
        _xrSettingsPage.AddChild(new UILabel { Text = "Turning", FontSize = FontSize.Body, Color = theme.TextPrimary, X = 20, Y = 24 });
        _xrSettingsPage.AddChild(new UILabel { Text = "The right stick turns smoothly, or in steps", FontSize = FontSize.Caption, Color = theme.TextSecondary, X = 20, Y = 54 });
        _xrMenu.RemoveChild(snap);
        _xrSettingsPage.AddChild(snap);
        snap.FontSize = FontSize.Body;
        snap.X = 440; snap.Y = 14; snap.Width = 280; snap.Height = 56;
        snap.Visible = true;
        Style(snap, TileAccent, TileAccentHover);
        snap.OnClick = () => OnTurnToggle?.Invoke();

        BuildXrToolsPage(theme, x, y, w, h);
    }

    // The Tools page: the editor's bigger jobs, with what the triggers do in each
    private void BuildXrToolsPage(UITheme theme, float x, float y, float w, float h)
    {
        _xrToolsPage = new UIPanel { X = x, Y = y, Width = w, Height = h, BackgroundColor = Color.Transparent, BorderColor = Color.Transparent, BorderWidth = 0, Visible = false };
        _xrWindow.AddChild(_xrToolsPage);
        _xrToolsPage.AddChild(new UILabel { Text = "Tools for bigger jobs", FontSize = FontSize.Body, Color = theme.TextPrimary, X = 16, Y = 10 });
        _xrToolsHint = new UILabel { Text = "", FontSize = FontSize.Caption, Color = theme.TextSecondary, X = 16, Y = 40 };
        _xrToolsPage.AddChild(_xrToolsHint);
        foreach (var button in _xrTool.Values) _xrToolsPage.AddChild(button);
        Style(_xrTool["select"], TileAccent, TileAccentHover);
        Style(_xrTool["blueprints"], TileAccent, TileAccentHover);
        _xrTool["blueprints"].Text = "Blueprints";
        _xrTool["cancel"].Text = "Stop placing";
        _xrTool["save"].Text = "Save as blueprint";
    }

    private void RebuildWorldPanelForXr(UITheme theme, float w, float h)
    {
        _worldPanel.ClearChildren();
        _worldPanel.Width = w; _worldPanel.Height = h; _worldPanel.Visible = false;
        float y = 16;
        void Row(string title, params (string Label, string Action, string Value)[] buttons)
        {
            _worldPanel.AddChild(new UILabel { Text = title, FontSize = FontSize.Body, Color = theme.TextPrimary, X = 16, Y = y + 14 });
            float x = 170;
            foreach (var (label, action, value) in buttons)
            {
                var a = action; var v = value;
                _worldPanel.AddChild(new UIButton { Text = label, FontSize = FontSize.Body, X = x, Y = y, Width = 130, Height = 56, PaddingX = 4, PaddingY = 4, OnClick = () => OnAdminWorldAction?.Invoke(a, v) });
                x += 138;
            }
            y += 78;
        }
        Row("Time", ("Day", "time", "day"), ("Noon", "time", "noon"), ("Night", "time", "night"), ("Midnight", "time", "midnight"));
        Row("Weather", ("Clear", "weather", "clear"), ("Rain", "weather", "rain"), ("Thunder", "weather", "thunder"));
        Row("Server", ("Save", "save", ""));
    }

    // ---- what the tiles do ----

    private void OnXrTile(string id)
    {
        switch (id)
        {
            case "chat": OpenXrPage(XrPage.Say); break;
            case "blocks": OpenXrPage(XrPage.Inventory); break;
            case "tools": OpenXrPage(XrPage.Tools); break;
            case "players": OpenXrPage(XrPage.Players); break;
            case "map": OpenXrPage(XrPage.Map); break;
            case "world": OpenXrPage(XrPage.World); break;
            case "settings": OpenXrPage(XrPage.Settings); break;
            case "blueprints": OpenXrPage(XrPage.Blueprints); break;
            case "mic": OnMicToggle?.Invoke(); break;
            case "exit": OnXrLeave?.Invoke(); break;
            default: OnEditAction?.Invoke(id); break;
        }
    }

    private void CloseAllPagesButKeep(XrPage keep)
    {
        bool leavingTools = _xrToolsOpen && keep is not (XrPage.Tools or XrPage.Blueprints);
        if (keep != XrPage.Inventory) OpenPalette(false);
        if (keep != XrPage.Blueprints) OpenBlueprints(false);
        if (keep != XrPage.Say) OpenPhrases(false);
        if (keep != XrPage.World) OpenWorldPanel(false);
        if (keep != XrPage.Players) { _xrPlayersOpen = false; _playersPanel.Visible = false; }
        if (keep != XrPage.Map) { _xrMapOpen = false; _xrMapPage.Visible = false; }
        if (keep != XrPage.Settings) { _xrSettingsOpen = false; _xrSettingsPage.Visible = false; }
        if (keep is not (XrPage.Tools or XrPage.Blueprints)) { _xrToolsOpen = false; _xrToolsPage.Visible = false; }
        OpenNamePrompt(false);
        _menu.Visible = false;
        // Done with the tools: the triggers place and break blocks again (a person left in "mark an area" could do nothing else: found on a real headset)
        if (leavingTools && (_ctxSelecting || _ctxPasting)) OnEditAction?.Invoke("tool-build");
    }

    private void OpenXrPage(XrPage page)
    {
        CloseAllPagesButKeep(page);
        switch (page)
        {
            case XrPage.Inventory: if (!IsPaletteOpen) OnEditAction?.Invoke("blocks"); break;
            case XrPage.Blueprints: _xrToolsOpen = true; if (!IsBlueprintsOpen) OnEditAction?.Invoke("blueprints"); break;
            case XrPage.Say: OpenPhrases(true); break;
            case XrPage.World: OpenWorldPanel(true); break;
            case XrPage.Players: _xrPlayersOpen = true; _playersPanel.Visible = true; break;
            case XrPage.Map: _xrMapOpen = true; _xrMapPage.Visible = true; break;
            case XrPage.Settings: _xrSettingsOpen = true; _xrSettingsPage.Visible = true; break;
            case XrPage.Tools: _xrToolsOpen = true; _xrToolsPage.Visible = true; LayoutXrTools(); break;
        }
        LayoutXrTabs();
    }

    /// <summary>Close whatever the window shows.</summary>
    public void CloseXrWindow()
    {
        if (!_xrBuilt) return;
        CloseAllPagesButKeep(XrPage.None);
        LayoutXrTabs();
    }

    private XrPage CurrentXrPage => IsPaletteOpen ? XrPage.Inventory : IsBlueprintsOpen ? XrPage.Blueprints : IsPhrasesOpen ? XrPage.Say : IsWorldPanelOpen ? XrPage.World
        : _xrPlayersOpen ? XrPage.Players : _xrMapOpen ? XrPage.Map : _xrSettingsOpen ? XrPage.Settings : _xrToolsOpen ? XrPage.Tools : XrPage.None;

    // The tabs that make sense now, in a row, the open page lit
    private void LayoutXrTabs()
    {
        if (!_xrBuilt) return;
        var current = CurrentXrPage;
        if (current == XrPage.Blueprints) current = XrPage.Tools;   // the blueprints are reached from the tools
        float x = 12;
        foreach (var (page, tab) in _xrTabs)
        {
            bool show = page switch { XrPage.Inventory or XrPage.Tools => _ctxEditing, XrPage.World => IsAdmin, _ => true };
            tab.Visible = show;
            if (!show) continue;
            tab.X = x; tab.Y = 10;
            x += tab.Width + 6;
            if (page == current) Style(tab, TileOn, TileOnHover); else Style(tab, TileColor, TileHover);
        }
    }

    // ---- the tools page ----

    private void LayoutXrTools()
    {
        if (!_xrBuilt) return;
        foreach (var button in _xrTool.Values) button.Visible = false;
        float y = 76;
        void Row(string[] ids, float width)
        {
            float x = 16;
            foreach (var id in ids)
            {
                var b = _xrTool[id];
                b.Visible = true; b.Width = width; b.Height = 52; b.X = x; b.Y = y;
                x += width + 8;
            }
            y += 64;
        }
        _xrTool["select"].Text = _ctxSelecting ? "Marking: stop" : "Mark an area";
        if (_ctxSelecting) Style(_xrTool["select"], TileOn, TileOnHover); else Style(_xrTool["select"], TileAccent, TileAccentHover);
        Row(["select", "blueprints", "undo", "redo"], 172);
        if (_ctxPasting) Row(["turn", "mirror", "cancel"], 172);
        else
        {
            if (_ctxHasSelection) Row(["fill", "clear", "copy", "cut"], 172);
            if (_ctxHasSelection) Row(["save"], 220);
            if (_ctxHasClipboard) Row(["paste"], 172);
        }
        _xrToolsHint.Text = _ctxPasting ? "Right trigger puts it down   left trigger stops placing"
            : _ctxSelecting ? "Right trigger marks the two corners   left trigger clears   then Fill, Copy, Cut or Save"
            : "Mark an area to fill, copy or save it as a blueprint; a blueprint is put down by choosing it";
    }

    // ---- the menu: the tiles of now ----

    private void LayoutXrMenu()
    {
        if (!_xrBuilt) return;
        foreach (var tile in _xrTile.Values) tile.Visible = false;
        const float padX = 12, gap = 6, tileH = 46, top = 66;
        float y = top;

        void Grid(string[] ids, int columns)
        {
            var shown = ids.Where(i => _xrTile.ContainsKey(i)).ToArray();
            if (shown.Length == 0) return;
            float w = (330 - 2 * padX - (columns - 1) * gap) / columns;
            for (int i = 0; i < shown.Length; i++)
            {
                var tile = _xrTile[shown[i]];
                tile.Visible = true;
                tile.Width = w; tile.Height = tileH;
                tile.X = padX + (i % columns) * (w + gap);
                tile.Y = y + (i / columns) * (tileH + gap);
            }
            y += ((shown.Length + columns - 1) / columns) * (tileH + gap) + 6;
        }

        // Creative: on or off (a toggle), then what it opens
        Grid([_ctxEditing ? "leave" : "enter"], 1);
        if (_ctxEditing) Grid(["blocks", "tools"], 2);
        if (_ctxAr) Grid(_ctxTabletop ? ["table", "bigger", "smaller", "bring"] : ["table"], 2);
        Grid(IsAdmin ? ["players", "chat", "map", "mic", "settings", "world"] : ["players", "chat", "map", "mic", "settings"], 3);

        var exit = _xrTile["exit"];
        exit.Visible = true;
        exit.Width = 330 - 2 * padX; exit.Height = tileH;
        exit.X = padX; exit.Y = 562 - 6 - tileH - 8;
        LayoutXrTabs();
    }

    private string XrModeText => !_ctxEditing ? "Exploring" : _ctxPasting ? "Placing" : _ctxSelecting ? "Marking" : "Creative";

    // ---- the menu's state, from the editor, the table and the voice ----

    private void XrContext(bool editing, bool hasSelection, bool pasting, bool hasClipboard, bool selecting)
    {
        _ctxEditing = editing; _ctxHasSelection = hasSelection; _ctxPasting = pasting; _ctxHasClipboard = hasClipboard; _ctxSelecting = selecting;
        if (!_xrBuilt) return;
        _xrMenuMode.Text = XrModeText;
        LayoutXrMenu();
        LayoutXrTools();
    }

    private void XrTableContext(bool ar, bool tabletop)
    {
        _ctxAr = ar; _ctxTabletop = tabletop;
        if (!_xrBuilt) return;
        _xrTile["table"].Text = tabletop ? "Life size" : "On the table";
        LayoutXrMenu();
    }

    private void XrMicState(bool voiceOn, bool micOn)
    {
        _voiceOn = voiceOn; _micOn = micOn;
        if (!_xrBuilt) return;
        var mic = _xrTile["mic"];
        mic.Text = !voiceOn ? "Voice" : micOn ? "Mic ON" : "Mic off";
        if (micOn) Style(mic, TileOn, TileOnHover); else Style(mic, TileColor, TileHover);
    }

    // Every frame in a headset
    private void XrUpdate(float dt, in HudData d)
    {
        if (!_xrBuilt || !_xrActive) return;
        _feedAge += dt;
        if (_speaking.Visible) _feedAge = 0;
        _minimap.Bearing = d.YawDegrees + 90f;
        _minimap.Coordinates = string.Create(CultureInfo.InvariantCulture, $"{d.CamX:F0}, {d.CamZ:F0}");
        _minimap.ZoomLevel = _minimapZoom;
        _chat.BackgroundColor = _chatAge < ChatFadeSeconds ? Color.FromArgb(150, 10, 14, 22) : Color.Transparent;
        _xrMenu.Visible = _xrMenuOpen;
        _xrBar.Visible = XrBarVisible;
        _xrFeed.Visible = XrFeedVisible;
        _xrMenuWhere.Text = string.Create(CultureInfo.InvariantCulture, $"{d.CamX:F0}  {d.CamY:F0}  {d.CamZ:F0}    {d.Fps:F0} FPS");
        _xrMenuMode.Text = XrModeText;
        LayoutXrTabs();
        // The window is as tall as the page it shows
        _xrWindow.Visible = XrWindowOpen;
    }

    // The players page: big rows, as wide as the page
    private void RelayoutPlayersForXr()
    {
        if (!_xrBuilt && !_xrActive) return;
        _playersPanel.Width = 740;
        _playersTitle.FontSize = FontSize.Body;
        _playersTitle.X = 16; _playersTitle.Y = 10;
        _playerList.ItemFontSize = FontSize.Body;
        _playerList.ItemHeight = 46;
        _playerList.X = 10;
        _playerList.Y = 50;
        _playerList.Width = 720;
        _playerList.Height = 478 - 50 - 10;
        _playersPanel.Height = 478;
    }
}
