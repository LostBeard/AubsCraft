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
    private PointerDot _pointerDot = null!;
    private UILabel _followLabel = null!;
    private UIHotbar _editBar = null!;
    private UILabel _editLabel = null!;
    private UIPanel _palette = null!;
    private UITextInput _paletteSearch = null!;
    private UIList _paletteList = null!;
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

    /// <summary>A line is being typed (the chat's, or the block search's): the keys are the text's.</summary>
    public bool IsTyping => (_chat != null && _chat.IsInputFocused) || (_paletteSearch != null && _palette.Visible && _paletteSearch.IsFocused) || (_nameInput != null && _namePrompt.Visible && _nameInput.IsFocused);

    private const string DesktopHint = "Click the view to fly   WASD move   Space / Shift up / down   Esc release   Enter chat   M map zoom   F3 debug   Click a player in the list for actions";
    private const string HeadsetHint = "Left stick fly or slide the model   Right stick turn, rise or zoom   Grips go fast   Trigger on a player or the map   X hide this panel   Y leaves";

    /// <summary>True while the HUD is a panel floating in a headset: its hint tells what the controllers do (there is no mouse, keyboard or pointer lock).</summary>
    public bool HeadsetMode
    {
        get => _headset;
        set
        {
            _headset = value;
            if (_hint != null) _hint.Text = value ? HeadsetHint : _editMode ? EditHint : DesktopHint;
        }
    }
    private bool _headset;

    /// <summary>Whether this pixel of the HUD is on the minimap.</summary>
    public bool OverMinimap(float x, float y) => _minimap != null && _minimap.HitTest(new System.Numerics.Vector2(x, y)) != null;

    /// <summary>A controller points at this pixel of the HUD (a dot shows it), or at nothing.</summary>
    public void SetPointer(float x, float y, bool visible)
    {
        if (_pointerDot == null) return;
        _pointerDot.Visible = visible;
        _pointerDot.X = x - _pointerDot.Width / 2f;
        _pointerDot.Y = y - _pointerDot.Height / 2f;
    }

    // The dot is only to look at: it takes no clicks
    private sealed class PointerDot : UIPanel
    {
        public override UIElement? HitTest(System.Numerics.Vector2 screenPos) => null;
    }

    // ---- edit mode ----

    private const string HeadsetEditHint = "EDIT   right trigger place   A break   left trigger copy the block   left stick click undo   right stick click leaves   the buttons on the left do the rest";
    private const string HeadsetSelectHint = "SELECT   right trigger marks a corner   A clears   then Fill, Copy, Cut or Save on the left";
    private const string HeadsetPasteHint = "PLACING   right trigger puts it down   A stops   Turn and Mirror on the left";
    private const string EditHint = "EDIT   left break   right place   middle copy block   1-9 wheel choose   E blocks   V select   K blueprints   Ctrl+Z/Y undo/redo   B leave";
    private const string SelectHint = "SELECT   click two corners   F fill   Shift+F hollow   Delete clear   Ctrl+C copy   Ctrl+X cut   Ctrl+V paste   Ctrl+S save blueprint   right click clears   V back to building";
    private const string PasteHint = "PLACING   left click puts it down   R turn   T mirror   right click or Esc stops";
    private bool _editMode;

    /// <summary>The slot of the edit bar that was chosen (a number key, the wheel, a click on it).</summary>
    public Action<int>? OnEditSlot { get; set; }

    /// <summary>Show the edit bar (nine blocks in hand) and the edit hint, or take them away.</summary>
    public void SetEditMode(bool on, IReadOnlyList<string>? slotLabels = null, int selected = 0)
    {
        _editMode = on;
        if (_editBar == null) return;
        _editBar.Visible = on;
        _editLabel.Visible = on;
        _hint.Text = on ? (_headset ? HeadsetEditHint : EditHint) : _headset ? HeadsetHint : DesktopHint;
        if (on && slotLabels != null)
        {
            for (int i = 0; i < slotLabels.Count && i < _editBar.SlotCount; i++) SetEditSlot(i, slotLabels[i]);
            SelectEditSlot(selected);
        }
    }

    // ---- an admin's menus: more in the player menu, and a panel for the world ----

    /// <summary>The user may act on players and the world (Owner or Admin): the player menu offers more, and the world panel opens. The panel decides again on every request.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>An admin chose an action on a player (their name, the action, a value: a game mode).</summary>
    public Action<string, string, string?>? OnAdminPlayerAction { get; set; }

    /// <summary>An admin chose an action on the world (time, weather, save) with its value.</summary>
    public Action<string, string?>? OnAdminWorldAction { get; set; }

    private UIPanel _worldPanel = null!;

    public bool IsWorldPanelOpen => _worldPanel != null && _worldPanel.Visible;

    private void BuildWorldPanel(UITheme theme)
    {
        _worldPanel = new UIPanel { Width = 330, Height = 196, Visible = false };
        _worldPanel.AddChild(new UILabel { Text = "The world   (O closes)", FontSize = FontSize.Caption, Color = theme.TextPrimary, X = 12, Y = 10 });
        void Row(string title, float y, params (string Label, string Action, string Value)[] buttons)
        {
            _worldPanel.AddChild(new UILabel { Text = title, FontSize = FontSize.Caption, Color = theme.TextPrimary, X = 12, Y = y + 6 });
            float x = 84;
            foreach (var (label, action, value) in buttons)
            {
                var a = action; var v = value;
                _worldPanel.AddChild(new UIButton { Text = label, FontSize = FontSize.Caption, X = x, Y = y, Width = 56, Height = 28, PaddingX = 4, PaddingY = 3, OnClick = () => OnAdminWorldAction?.Invoke(a, v) });
                x += 60;
            }
        }
        Row("Time", 40, ("Day", "time", "day"), ("Noon", "time", "noon"), ("Night", "time", "night"), ("Midnight", "time", "midnight"));
        Row("Weather", 80, ("Clear", "weather", "clear"), ("Rain", "weather", "rain"), ("Thunder", "weather", "thunder"));
        Row("Server", 120, ("Save", "save", ""));
        _root.AddAnchored(_worldPanel, Anchor.Center, 0, -10);
    }

    public void OpenWorldPanel(bool open)
    {
        if (_worldPanel != null && _worldPanel.Visible != open && (!open || IsAdmin)) _worldPanel.Visible = open;
    }

    // What an admin may do to a player, added under the ordinary actions of the player menu
    private void AddAdminItems(string name)
    {
        if (!IsAdmin) return;
        _menu.AddSeparator();
        _menu.AddItem("Bring " + name + " here", () => OnAdminPlayerAction?.Invoke(name, "teleport", null));
        _menu.AddItem("Survival", () => OnAdminPlayerAction?.Invoke(name, "gamemode", "survival"));
        _menu.AddItem("Creative", () => OnAdminPlayerAction?.Invoke(name, "gamemode", "creative"));
        _menu.AddItem("Spectator", () => OnAdminPlayerAction?.Invoke(name, "gamemode", "spectator"));
        _menu.AddItem("Heal " + name, () => OnAdminPlayerAction?.Invoke(name, "heal", null));
        _menu.AddItem("Kick " + name, () => OnAdminPlayerAction?.Invoke(name, "kick", null));
        _menu.AddItem("Ban " + name + "...", () => AskBan(name));
    }

    // A ban is for good and on every server: ask once more
    private void AskBan(string name)
    {
        _menu.ClearItems();
        _menu.AddItem("Yes: ban " + name + " from every server", () => OnAdminPlayerAction?.Invoke(name, "ban", null));
        _menu.AddSeparator();
        _menu.AddItem("No, leave them", null);
        float x = Math.Clamp(_mouseX, 0, Math.Max(0, _root.Width - MenuWidth));
        _menu.Show(new System.Numerics.Vector2(x, _mouseY));
        float maxY = Math.Max(0, _root.Height - _menu.Height);
        _menu.Y = Math.Min(_menu.Y, maxY);
    }

    // ---- the edit toolbar: what the keys do, as buttons (for a headset, where there is no keyboard, and for a mouse) ----

    private readonly List<(string Id, UIButton Button)> _toolButtons = new();

    /// <summary>A toolbar button was pressed (its id: blocks, select, blueprints, undo, redo, fill, clear, copy, cut, paste, save, turn, mirror, cancel, leave).</summary>
    public Action<string>? OnEditAction { get; set; }

    private void BuildToolbar()
    {
        foreach (var (id, label) in new[]
        {
            ("chat", "Say something"), ("mic", "Mic: off"), ("enter", "Edit mode"), ("blocks", "Blocks"), ("select", "Select"), ("blueprints", "Blueprints"), ("undo", "Undo"), ("redo", "Redo"),
            ("fill", "Fill"), ("clear", "Clear"), ("copy", "Copy"), ("cut", "Cut"), ("paste", "Paste"), ("save", "Save as blueprint"),
            ("turn", "Turn"), ("mirror", "Mirror"), ("cancel", "Stop placing"), ("leave", "Leave edit mode"),
        })
        {
            var button = new UIButton { Text = label, FontSize = FontSize.Caption, Width = 112, Height = 26, PaddingX = 4, PaddingY = 3, Visible = false };
            var captured = id;
            button.OnClick = () => { if (captured == "chat") OpenPhrases(!IsPhrasesOpen); else if (captured == "mic") OnMicToggle?.Invoke(); else OnEditAction?.Invoke(captured); };
            _toolButtons.Add((id, button));
            button.X = 8; button.Y = 118;
            _root.AddChild(button);
        }
    }

    /// <summary>Which toolbar buttons show: always the building ones in edit mode, the ones for a selection when there is one, the ones for placing when a blueprint is on its way down.</summary>
    public void SetEditToolbar(bool editing, bool hasSelection, bool pasting, bool hasClipboard, bool selecting)
    {
        if (_toolButtons.Count == 0) return;
        int shown = 0;
        foreach (var (id, button) in _toolButtons)
        {
            // In a headset the way into edit mode is a button (a hand has no thumbstick to press); everything else shows in edit mode
            bool show = id is "chat" or "mic" ? _headset : id == "enter" ? !editing && _headset : editing && id switch
            {
                "blocks" or "select" or "blueprints" or "undo" or "redo" or "leave" => !pasting || id is "leave" or "undo" or "redo",
                "fill" or "clear" or "copy" or "cut" or "save" => hasSelection && !pasting,
                "paste" => hasClipboard && !pasting,
                "turn" or "mirror" or "cancel" => pasting,
                _ => false,
            };
            button.Visible = show;
            if (id == "select") button.Text = selecting ? "Build" : "Select";
            if (!show) continue;
            // Seven to a column, then the next column over
            button.X = 8 + (shown / 7) * 118; button.Y = 118 + (shown % 7) * 30;
            shown++;
        }
    }

    // ---- augmented reality: the world as a model on a table, and its buttons ----

    private readonly List<(string Id, UIButton Button)> _tableButtons = new();

    /// <summary>A table button was pressed (table, bigger, smaller, bring).</summary>
    public Action<string>? OnTableAction { get; set; }

    private void BuildTableButtons()
    {
        foreach (var (id, label) in new[] { ("table", "Life size"), ("bigger", "Bigger"), ("smaller", "Smaller"), ("bring", "Bring it here") })
        {
            var button = new UIButton { Text = label, FontSize = FontSize.Caption, Width = 112, Height = 26, PaddingX = 4, PaddingY = 3, Visible = false };
            var captured = id;
            button.OnClick = () => OnTableAction?.Invoke(captured);
            _tableButtons.Add((id, button));
            _root.AddChild(button);
        }
    }

    /// <summary>The table buttons: shown in an augmented-reality session (<paramref name="ar"/>), "Life size" or "Tabletop" by what pressing it does.</summary>
    public void SetTableToolbar(bool ar, bool tabletop)
    {
        if (_tableButtons.Count == 0) return;
        int row = 0;
        foreach (var (id, button) in _tableButtons)
        {
            bool show = ar && (id == "table" || tabletop);
            button.Visible = show;
            if (id == "table") button.Text = tabletop ? "Life size" : "On the table";
            if (!show) continue;
            button.X = 8 + 2 * 118; button.Y = 118 + row * 30;
            row++;
        }
    }
    // ---- voice: the microphone button of a headset, and who is heard ----

    /// <summary>The Mic button was pressed (a headset's): the page opens or closes the microphone.</summary>
    public Action? OnMicToggle { get; set; }

    private UILabel _speaking = null!;

    /// <summary>What the Mic button says: whether the voice is connected and the microphone open.</summary>
    public void SetMicState(bool voiceOn, bool micOn)
    {
        foreach (var (id, button) in _toolButtons)
            if (id == "mic") button.Text = !voiceOn ? "Voice: off" : micOn ? "Mic: ON" : "Mic: off";
    }

    /// <summary>Who is being heard right now (names), or nothing.</summary>
    public void SetSpeaking(IReadOnlyList<string> names)
    {
        if (_speaking == null) return;
        var text = names.Count == 0 ? "" : "Speaking: " + string.Join(", ", names);
        if (_speaking.Text != text) _speaking.Text = text;
        _speaking.Visible = text.Length > 0;
    }
    // ---- quick things to say, for where there is no keyboard (a headset) ----

    private UIPanel _phrases = null!;
    private static readonly string[] QuickPhrases =
    [
        "Hi everyone!", "Come and see what I built", "Nice build!", "Do you need any help?",
        "Thank you!", "Be right back", "Look up!", "Good night!",
    ];

    public bool IsPhrasesOpen => _phrases != null && _phrases.Visible;

    private void BuildPhrases(UITheme theme)
    {
        _phrases = new UIPanel { Width = 280, Height = 40 + QuickPhrases.Length * 30, Visible = false };
        _phrases.AddChild(new UILabel { Text = "Say something", FontSize = FontSize.Caption, Color = theme.TextPrimary, X = 12, Y = 10 });
        for (int i = 0; i < QuickPhrases.Length; i++)
        {
            var phrase = QuickPhrases[i];
            _phrases.AddChild(new UIButton { Text = phrase, FontSize = FontSize.Caption, X = 10, Y = 34 + i * 30, Width = 260, Height = 26, PaddingX = 6, PaddingY = 3, OnClick = () => { _outgoingChat.Add(phrase); OpenPhrases(false); } });
        }
        _root.AddAnchored(_phrases, Anchor.Center, 0, -10);
    }

    public void OpenPhrases(bool open)
    {
        if (_phrases != null && _phrases.Visible != open) _phrases.Visible = open;
    }
    // ---- the block palette: every block of the game, searched by name ----

    private const int PaletteMaxRows = 400;
    private IReadOnlyList<string> _paletteBlocks = System.Array.Empty<string>();

    /// <summary>A block was chosen in the palette (its name, e.g. <c>minecraft:gold_block</c>).</summary>
    public Action<string>? OnPaletteChoice { get; set; }

    public bool IsPaletteOpen => _palette != null && _palette.Visible;

    private void BuildPalette(UITheme theme)
    {
        _palette = new UIPanel { Width = 380, Height = 460, Visible = false };
        _palette.AddChild(new UILabel { Text = "Blocks   click one to put it in your hand", FontSize = FontSize.Caption, Color = theme.TextPrimary, X = 12, Y = 10 });
        _paletteSearch = new UITextInput { Width = 356, Height = 30, X = 12, Y = 36, Placeholder = "Search by name (stairs, gold, glass...)", FontSize = FontSize.Caption };
        _paletteSearch.OnChanged = _ => RefreshPalette();
        _paletteSearch.OnSubmit = _ => { if (_paletteList.ItemCount > 0) ChoosePalette(0); };
        _palette.AddChild(_paletteSearch);
        // Words that fill the search in, for where there is no keyboard (a headset) and for a quick pick
        string[] chips = ["stone", "planks", "log", "glass", "wool", "stairs", "slab", "wall", "fence", "door", "ore", "lamp", "terracotta", "concrete"];
        for (int i = 0; i < chips.Length; i++)
        {
            var word = chips[i];
            _palette.AddChild(new UIButton { Text = word, FontSize = FontSize.Caption, X = 12 + (i % 7) * 51, Y = 72 + (i / 7) * 26, Width = 49, Height = 24, PaddingX = 2, PaddingY = 2, OnClick = () => { _paletteSearch.Text = word; RefreshPalette(); } });
        }
        _paletteList = new UIList { ItemHeight = 22, ItemFontSize = FontSize.Caption, Padding = 2, X = 10, Y = 128, Width = 360, Height = 322 };
        _paletteList.OnSelectionChanged = ChoosePalette;
        _palette.AddChild(_paletteList);
        _root.AddAnchored(_palette, Anchor.Center, 0, -10);
    }

    /// <summary>Every block the palette offers.</summary>
    public void SetPaletteBlocks(IReadOnlyList<string> blocks)
    {
        _paletteBlocks = blocks;
        if (_palette != null && _palette.Visible) RefreshPalette();
    }

    /// <summary>Open or close the palette. Opened, its search takes the keys.</summary>
    public void OpenPalette(bool open)
    {
        if (_palette == null || _palette.Visible == open) return;
        _palette.Visible = open;
        if (open)
        {
            _paletteSearch.Text = "";
            RefreshPalette();
            _paletteSearch.Focus();
        }
        else _paletteSearch.Blur();
    }

    private readonly List<string> _paletteShown = new();

    private void RefreshPalette()
    {
        string q = _paletteSearch.Text.Trim().Replace(' ', '_').ToLowerInvariant();
        _paletteShown.Clear();
        foreach (var b in _paletteBlocks)
        {
            if (q.Length > 0 && !b.Contains(q, StringComparison.Ordinal)) continue;
            _paletteShown.Add(b);
            if (_paletteShown.Count >= PaletteMaxRows) break;
        }
        _paletteList.ClearItems();
        foreach (var b in _paletteShown) _paletteList.AddItem(b.StartsWith("minecraft:", StringComparison.Ordinal) ? b[10..].Replace('_', ' ') : b);
        _paletteList.SelectedIndex = -1;
    }

    private void ChoosePalette(int index)
    {
        if (index < 0 || index >= _paletteShown.Count) return;
        var block = _paletteShown[index];
        _paletteList.SelectedIndex = -1;
        OnPaletteChoice?.Invoke(block);
    }


    // ---- the blueprint menu and the name prompt ----

    private UIPanel _blueprints = null!;
    private UIList _blueprintList = null!;
    private UIButton _blueprintDelete = null!;
    private UIPanel _namePrompt = null!;
    private UILabel _namePromptTitle = null!;
    private UITextInput _nameInput = null!;
    private readonly List<string> _blueprintNames = new();
    private bool _deleteMode;

    /// <summary>A blueprint was chosen in the menu (its name): the viewer starts placing it.</summary>
    public Action<string>? OnBlueprintChoice { get; set; }

    /// <summary>A blueprint was chosen while the menu was in delete mode.</summary>
    public Action<string>? OnBlueprintDelete { get; set; }

    /// <summary>A name was typed and sent (Enter) in the name prompt.</summary>
    public Action<string>? OnNameEntered { get; set; }

    public bool IsBlueprintsOpen => _blueprints != null && _blueprints.Visible;
    public bool IsNamePromptOpen => _namePrompt != null && _namePrompt.Visible;

    private void BuildBlueprints(UITheme theme)
    {
        _blueprints = new UIPanel { Width = 400, Height = 420, Visible = false };
        _blueprints.AddChild(new UILabel { Text = "Blueprints   click one to place it", FontSize = FontSize.Caption, Color = theme.TextPrimary, X = 12, Y = 10 });
        _blueprintList = new UIList { ItemHeight = 24, ItemFontSize = FontSize.Caption, Padding = 2, X = 10, Y = 36, Width = 380, Height = 330 };
        _blueprintList.OnSelectionChanged = ChooseBlueprint;
        _blueprints.AddChild(_blueprintList);
        _blueprintDelete = new UIButton { Text = "Delete: off", FontSize = FontSize.Caption, X = 10, Y = 376, Width = 130, Height = 30, PaddingY = 4 };
        _blueprintDelete.OnClick = () => { _deleteMode = !_deleteMode; _blueprintDelete.Text = _deleteMode ? "Delete: ON" : "Delete: off"; };
        _blueprints.AddChild(_blueprintDelete);
        _root.AddAnchored(_blueprints, Anchor.Center, 0, -10);

        _namePrompt = new UIPanel { Width = 400, Height = 100, Visible = false };
        _namePromptTitle = new UILabel { Text = "Name", FontSize = FontSize.Caption, Color = theme.TextPrimary, X = 12, Y = 10 };
        _namePrompt.AddChild(_namePromptTitle);
        _nameInput = new UITextInput { Width = 376, Height = 32, X = 12, Y = 40, Placeholder = "a name, then Enter", FontSize = FontSize.Caption, MaxLength = 60 };
        _nameInput.OnSubmit = text => { if (!string.IsNullOrWhiteSpace(text)) OnNameEntered?.Invoke(text.Trim()); };
        _namePrompt.AddChild(_nameInput);
        _root.AddAnchored(_namePrompt, Anchor.Center, 0, -10);
    }

    /// <summary>The blueprints the menu lists: a name and a size each.</summary>
    public void SetBlueprints(IReadOnlyList<AubsCraft.Live.Model.Editing.BlueprintItem> items)
    {
        _blueprintNames.Clear();
        _blueprintList.ClearItems();
        foreach (var b in items)
        {
            _blueprintNames.Add(b.Name);
            _blueprintList.AddItem($"{b.Name}    {b.Width} x {b.Height} x {b.Depth}   {b.Blocks:N0} blocks");
        }
        _blueprintList.SelectedIndex = -1;
        if (items.Count == 0) _blueprintList.AddItem("(none yet: select a box, Ctrl+S saves it)");
    }

    public void OpenBlueprints(bool open)
    {
        if (_blueprints == null || _blueprints.Visible == open) return;
        _blueprints.Visible = open;
        if (!open) { _deleteMode = false; _blueprintDelete.Text = "Delete: off"; }
    }

    private void ChooseBlueprint(int index)
    {
        if (index < 0 || index >= _blueprintNames.Count) return;
        var name = _blueprintNames[index];
        _blueprintList.SelectedIndex = -1;
        if (_deleteMode) OnBlueprintDelete?.Invoke(name); else OnBlueprintChoice?.Invoke(name);
    }

    /// <summary>Ask for a name (the blueprint to save). Enter sends it to <see cref="OnNameEntered"/>.</summary>
    public void OpenNamePrompt(bool open, string title = "Name")
    {
        if (_namePrompt == null || _namePrompt.Visible == open) return;
        _namePrompt.Visible = open;
        if (open)
        {
            _namePromptTitle.Text = title;
            _nameInput.Text = "";
            _nameInput.Focus();
        }
        else _nameInput.Blur();
    }

    /// <summary>Which hint the edit bar shows: 0 building, 1 selecting, 2 placing a blueprint.</summary>
    public void SetEditHint(int kind)
    {
        if (_editMode && _hint != null) _hint.Text = _headset ? kind switch { 1 => HeadsetSelectHint, 2 => HeadsetPasteHint, _ => HeadsetEditHint } : kind switch { 1 => SelectHint, 2 => PasteHint, _ => EditHint };
    }

    /// <summary>What a slot of the edit bar shows (a block's short name).</summary>
    public void SetEditSlot(int slot, string label)
    {
        if (_editBar == null) return;
        _editBar.SetSlot(slot, label.Length > 10 ? label[..10] : label);
    }

    public void SelectEditSlot(int slot)
    {
        if (_editBar != null) _editBar.SelectedSlot = slot;
    }

    /// <summary>The line over the edit bar: the block pointed at, what is in hand, how much can be undone.</summary>
    public void SetEditStatus(string text)
    {
        if (_editLabel != null && _editLabel.Text != text) _editLabel.Text = text;
    }

    /// <summary>The minimap zoom, 1..3 (64, 128, 256 blocks to an edge): the next one, round to the first.</summary>
    public void CycleMinimapZoom() => _minimapZoom = _minimapZoom % MaxMinimapZoom + 1;

    /// <summary>False: the HUD is neither updated nor drawn (a clean frame). Input already queued is dropped with it.</summary>
    public bool Visible { get; set; } = true;

    public void Init(GPUDevice device, GPUQueue queue, string canvasFormat, int width, int height)
    {
        // A headset session starts a world (and so inits the page's HUD) each time it is entered: the screen and what it holds (the chat so far) stay
        if (_root != null && _ui.IsInitialized) { _ui.SetViewport(width, height); return; }
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
            Text = _headset ? HeadsetHint : DesktopHint,
            FontSize = FontSize.Caption,
            Color = theme.TextPrimary,
            OutlineWidth = 0.12f,
        };
        _root.AddAnchored(_hint, Anchor.BottomCenter, 0, -10);

        _speaking = new UILabel { FontSize = FontSize.Caption, Color = theme.TextPrimary, OutlineWidth = 0.14f, Visible = false };
        _root.AddAnchored(_speaking, Anchor.TopCenter, 0, 36);
        _followLabel = new UILabel { FontSize = FontSize.Caption, Color = theme.TextPrimary, OutlineWidth = 0.14f, Visible = false };
        _root.AddAnchored(_followLabel, Anchor.BottomCenter, 0, -34);

        // Edit mode: the nine blocks in hand, and what the buttons do
        _editBar = new UIHotbar { SlotCount = 9, SlotSize = 66, Visible = false, OnSlotChanged = i => OnEditSlot?.Invoke(i) };
        _root.AddAnchored(_editBar, Anchor.BottomCenter, 0, -46);
        _editLabel = new UILabel { FontSize = FontSize.Caption, Color = theme.TextPrimary, OutlineWidth = 0.14f, Visible = false };
        _root.AddAnchored(_editLabel, Anchor.BottomCenter, 0, -132);

        BuildPalette(theme);
        BuildPhrases(theme);
        BuildToolbar();
        BuildTableButtons();
        BuildWorldPanel(theme);
        BuildBlueprints(theme);

        // Last, so it is on top of everything and gets a click first
        _menu = new UIContextMenu { Visible = false, MenuWidth = MenuWidth };
        _root.AddChild(_menu);

        // Where a headset's controller points; over the menu, and never in the way of a click
        _pointerDot = new PointerDot { Width = 14, Height = 14, CornerRadius = 7, BorderWidth = 2, BackgroundColor = Color.FromArgb(230, 255, 255, 255), BorderColor = Color.FromArgb(255, 40, 140, 255), Visible = false };
        _root.AddChild(_pointerDot);

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
        // In a headset the camera is the head: there is nothing to follow or look through
        if (_headset) { }
        else if (string.Equals(_followName, name, StringComparison.OrdinalIgnoreCase))
            _menu.AddItem("Stop following " + name, () => OnPlayerAction?.Invoke(name, PlayerAction.StopFollowing));
        else
        {
            _menu.AddItem("Follow " + name, () => OnPlayerAction?.Invoke(name, PlayerAction.Follow));
            _menu.AddItem("See through " + name + "'s eyes", () => OnPlayerAction?.Invoke(name, PlayerAction.Eyes));
        }
        AddAdminItems(name);
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
        // A menu is open (the player menu, the blocks, the blueprints, a name being asked, the world panel): the whole view is the HUD's until it closes
        if (_menu.Visible || IsPaletteOpen || IsBlueprintsOpen || IsNamePromptOpen || IsWorldPanelOpen || IsPhrasesOpen) return [0, 0, _root.Width, _root.Height];
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

    /// <summary>The dots over the minimap (creatures and players around the camera).</summary>
    public void SetMinimapMarkers(IReadOnlyList<MinimapMarker> markers) => _minimap.Markers = markers;

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
                if (e.Code is "Enter" or "NumpadEnter" && !_pointerLocked && !IsTyping) _chat.FocusInput();
                // M cycles the minimap zoom (not while typing: the letter belongs to the chat then)
                if (e.Code == "KeyM" && !IsTyping) _minimapZoom = _minimapZoom % MaxMinimapZoom + 1;
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
        _hint.Visible = _headset || _editMode || !_pointerLocked;

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
